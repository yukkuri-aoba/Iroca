// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の入力→反映を仲介するコントローラ。
    ///
    /// UX は「クリック 1 回 = 1 反映」に一本化している:
    ///   プレビューでパーツをクリック → SAM が領域を推定 → その領域を即、編集対象の除外マスクへ
    ///   追加する(= 1 つの Undo ストローク)。積み上げ・確定ボタンは無く、間違えたら Ctrl+Z で
    ///   1 手ずつ戻す(手描きブラシと同じ操作感)。複数の島に分かれたパーツは、島を順にクリックすれば
    ///   それぞれが別ストロークとしてマスクへ足される。
    ///
    /// サービス(Sentis 側)とは MaskSuggestBridge 経由で結合し、Sentis 不在時はインスタンスも
    /// 作られない(MaskSuggestSection 参照)。
    /// </summary>
    internal sealed class MaskSuggestController
    {
        IrocaWindow _host;
        MaskPaintView _maskView;
        bool _subscribed;

        // クリック後、推論結果を待っている間 true。結果が来たら即マスクへ反映する。
        bool _awaitingProposal;
        // 待っている間に Undo/Redo が割り込んだら、遅れて届く提案を古い状態へ誤って足さないよう捨てる。
        bool _dropNextProposal;

        /// <summary>AI 提案モードが有効か(プレビュークリックを提案に使う)。</summary>
        public bool Active { get; private set; }

        /// <summary>提案の粒度(次のクリックから適用)。</summary>
        public MaskSuggestGranularity Granularity = MaskSuggestGranularity.Auto;

        /// <summary>直近のクリックが背景まで広がった可能性(粒度を下げる/やり直しの誘導に使う)。</summary>
        public bool LastClickFloodWarning { get; private set; }

        /// <summary>
        /// 直近のクリックでマスクへ 1 画素も追加されなかった。推論が完走しても何も足されないと
        /// 画面上は「何も起きない」としか見えないので、UI で明示するために持つ。
        /// </summary>
        public bool LastCommitEmpty { get; private set; }

        /// <summary>
        /// 直近のクリックで AI が領域を 1 画素も返さなかった(= 追加済みだったのではなく推論が空)。
        /// 「すでに塗ってある所を押しただけ」と「推論エンジンが動いていない」を UI で区別するために持つ。
        /// </summary>
        public bool LastProposalEmpty { get; private set; }

        public void Initialize(IrocaWindow host, MaskPaintView maskView)
        {
            _host = host;
            _maskView = maskView;
            var svc = MaskSuggestBridge.Service;
            if (svc != null && !_subscribed)
            {
                svc.StateChanged += OnServiceStateChanged;
                _subscribed = true;
            }
        }

        /// <summary>
        /// 静的サービスへの購読を解除する。<see cref="Shutdown"/> と自己修復経路の共通処理。
        /// </summary>
        void Unsubscribe()
        {
            var svc = MaskSuggestBridge.Service;
            if (svc != null && _subscribed) svc.StateChanged -= OnServiceStateChanged;
            _subscribed = false;
        }

        /// <summary>
        /// ウィンドウ破棄時の後始末。**購読解除がここの主目的**。
        ///
        /// サービスはドメイン寿命の静的保持(<see cref="MaskSuggestBridge.Service"/>)なので、
        /// 解除しないと閉じたウィンドウのコントローラがイベント経由で生き続ける。再オープン後は
        /// 新旧 2 購読者が並び、**先に登録された旧側**が <see cref="OnServiceStateChanged"/> で
        /// <c>TryTakeProposal</c> を先に呼んで提案を奪い、そのまま捨てる(サービスは Idle へ戻るので
        /// 新側には何も届かない)。ユーザーには「クリックしても時々何も起きない」としか見えない。
        /// </summary>
        public void Shutdown()
        {
            Unsubscribe();
            OnSourceChangedOrClosing();
            Active = false;
        }

        public void SetActive(bool active)
        {
            if (Active == active) return;
            Active = active;
            if (active)
            {
                var svc = MaskSuggestBridge.Service;
                // エラー表示のまま入り直したときは、ここが唯一の再試行導線になる
                // (自動再試行は原因が直らないまま毎レイアウト走るので入れない)。
                if (svc != null && svc.Phase == MaskSuggestPhase.Error) svc.CancelAll();
                svc?.TryEnsureModels();
            }
            else
            {
                LastClickFloodWarning = false;
                LastCommitEmpty = false;
                LastProposalEmpty = false;
            }
            _host?.RequestRepaint();
        }

        // ─────────────────── クリック → 推論 → 即マスク反映 ───────────────────

        /// <summary>
        /// クリックを待たずにソース画像の解析(埋め込み計算)を先行させる。
        ///
        /// Unity 起動後の初回はモデルのロードと推論カーネル(Burst / コンピュートシェーダ)の
        /// コンパイルで時間がかかる。クリック後にそれを始めると「押しても無反応」に見えるため、
        /// AI モードに入った時点で走らせて進捗を出す。AI モード中は毎レイアウトで呼ばれるが、
        /// 同一ソースならサービス側で no-op になる。
        /// </summary>
        public void PrepareSource(Color32[] pixelsBottomUp, int width, int height, string sourceKey)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null || !Active) return;
            // 待機中だけ先行させる。モデルのロードは同期で重いのでレイアウト中には開始せず
            // (それは AI 提案を開始したときの仕事)、エラー中も再試行しない(原因が直らない
            // まま毎レイアウト走ってエディタが重くなる。復帰は AI 提案の入り直し)。
            if (svc.Phase != MaskSuggestPhase.Idle) return;
            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
        }

        /// <summary>
        /// プレビュークリック。pixels は実フル解像度ソース(下原点)。
        /// 推論は非同期で、結果は <see cref="OnServiceStateChanged"/> がマスクへ直接反映する。
        /// </summary>
        public void OnPreviewClick(float u, float v, Color32[] pixelsBottomUp,
                                   int width, int height, string sourceKey)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null || !Active) return;
            if (!svc.TryEnsureModels()) return;

            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
            svc.RequestProposal(u, v, Granularity);
            _awaitingProposal = true;
            _dropNextProposal = false;
        }

        void OnServiceStateChanged()
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) return;
            // 破棄済みウィンドウに紐づく購読者(= Shutdown を取りこぼした残骸)は、提案に一切触れずに
            // 自己解除する。触れると生きている購読者から提案を奪ってしまう。
            // Unity の破棄済みオブジェクトは「fake-null」で参照自体は非 null のため `?.` をすり抜ける。
            // 判定には Unity がオーバーロードした `==` を使うこと(以降の _host 参照も同様)。
            if (_host == null)
            {
                Unsubscribe();
                return;
            }
            if (svc.Phase == MaskSuggestPhase.ProposalReady &&
                svc.TryTakeProposal(out var proposal))
            {
                _awaitingProposal = false;
                // モードを抜けた・Undo が割り込んだ・空提案、のいずれかなら反映しない。
                if (!_dropNextProposal && Active && proposal != null)
                    CommitProposalToMask(proposal);
                _dropNextProposal = false;
            }
            _host.RequestRepaint();
        }

        /// <summary>
        /// 提案領域を編集対象の除外マスク(共通 or ゾーン)へ OR 合成し、1 つの Undo ストロークとして反映する。
        /// 選んだ部分を「色替えしない範囲」へ加える = そのパーツを色替えから保護する。
        /// 反映後は通常のマスクとしてブラシ修正・Ctrl+Z(1 ストローク扱い)が効く。
        /// </summary>
        void CommitProposalToMask(MaskSuggestProposal proposal)
        {
            // 反映できなかったときは黙って戻らず「空だった」と UI に出す。画面上は
            // どのルートも「クリックしたのに何も起きない」に見えてしまうため。
            LastProposalEmpty = true;
            LastCommitEmpty = true;
            if (_maskView == null) return;
            var src = proposal.maskBottomUp;
            int sw = proposal.width, sh = proposal.height;
            if (src == null || sw <= 0 || sh <= 0) return;

            _maskView.EnsureMasks();
            var mask = _maskView.GetActiveMaskArray();
            int mw = _maskView.maskWidth, mh = _maskView.maskHeight;
            if (mask == null || mw <= 0 || mh <= 0) return;

            int added = 0;    // 実際にマスクへ足された画素数
            int proposed = 0; // 提案そのものの画素数(0 = 推論が領域を返していない)
            _maskView.BeginStroke();
            if (mw == sw && mh == sh)
            {
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!src[i]) continue;
                    proposed++;
                    if (!mask[i]) { mask[i] = true; added++; }
                }
            }
            else
            {
                // マスク解像度がソースと異なる場合(実ファイル解像度で仕上げた提案 →
                // インポート解像度のマスクキャンバス等)は被覆保存で転写する。旧実装の
                // 最近傍サンプリングは縮小時に仕上げ済み境界の被覆を 1 セル単位で欠けさせ、
                // 取り残し画素が再着色されて境界の点ノイズになっていた(実測: 強ドット 177個
                // → 被覆保存で 0)。さらにインポート縮小はマスク解像度側に新たな混合画素を
                // 作るため、提案の寄与分だけを対象に AA 遷移包含をマスク解像度で再適用して
                // から OR する(ユーザーの既存ストロークには触れない)。
                var transferred = SamMaskRefine.TransferCoverage(src, sw, sh, mw, mh);
                if (transferred != null)
                {
                    var tex = _host?.SourceTexture;
                    if (tex != null && tex.width == mw && tex.height == mh)
                    {
                        try
                        {
                            SamMaskRefine.IncludeAaTransition(transferred, tex.GetPixels32(), mw, mh);
                        }
                        catch (System.Exception)
                        {
                            // 非 Readable 等で画素が取れない場合は被覆保存転写のみ
                            // (最近傍起因の強い点ノイズはこれだけでも解消する)。
                        }
                    }
                    for (int i = 0; i < mask.Length; i++)
                    {
                        if (!transferred[i]) continue;
                        proposed++;
                        if (!mask[i]) { mask[i] = true; added++; }
                    }
                }
            }
            _maskView.EndStroke();
            _maskView.maskDirty = true;
            LastClickFloodWarning = proposal.floodWarning;
            LastCommitEmpty = added == 0;
            LastProposalEmpty = proposed == 0;
            _host?.MarkPreviewDirty();
            _host?.RequestRepaint();
        }

        /// <summary>Unity Undo/Redo 実行時: 反映待ちの提案は古い状態に重なるため、届いても捨てる。</summary>
        public void OnUndoRedoPerformed()
        {
            if (_awaitingProposal) _dropNextProposal = true;
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }

        /// <summary>テクスチャ切替・ウィンドウ破棄時の後始末。</summary>
        public void OnSourceChangedOrClosing()
        {
            MaskSuggestBridge.Service?.CancelAll();
            _awaitingProposal = false;
            _dropNextProposal = false;
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }
    }
}
