// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の入力→反映を仲介するコントローラ。
    ///
    /// UX は「クリック 1 回 = 1 反映」に一本化している:
    ///   プレビューでパーツを右クリック → SAM が領域を推定 → その領域を即、編集対象の除外マスクへ
    ///   追加する(= 1 つの Undo ストローク)。積み上げ・確定ボタンは無く、間違えたら Ctrl+Z で
    ///   1 手ずつ戻す(手描きブラシと同じ操作感)。複数の島に分かれたパーツは、島を順に右クリックすれば
    ///   それぞれが別ストロークとしてマスクへ足される。
    ///
    /// 受け口を右ボタンにしているのは、左ドラッグのパンを AI モード中も残すため
    /// (入力の横取りは PreviewView.HandleAiSuggestInput 側)。
    ///
    /// サービス(Sentis 側)とは MaskSuggestBridge 経由で結合し、Sentis 不在時はインスタンスも
    /// 作られない(MaskSuggestSection 参照)。
    /// </summary>
    internal sealed class MaskSuggestController
    {
        IrocaWindow _host;
        MaskPaintView _maskView;
        bool _subscribed;

        // 反映待ちクリックの UV(古い順)。サービスは FIFO で処理するので、提案を 1 件
        // 受け取るたび先頭を除く。プレビュー上の待機マーカー表示に使う(可視化しないと
        // 推論が追いつくまで「押したのに無反応」に見えて二度押しを誘う)。
        readonly List<Vector2> _pendingClicks = new List<Vector2>();

        // _pendingClicks と同期して保つクリック受理時刻(E2E 計測用)。要素の増減は
        // 必ず _pendingClicks と同じ箇所で行うこと。
        readonly List<long> _pendingClickTimes = new List<long>();

        /// <summary>AI 提案モードが有効か(プレビュークリックを提案に使う)。</summary>
        public bool Active { get; private set; }

        /// <summary>反映待ちクリックの UV(古い順・先頭が処理中)。プレビューの待機マーカー用。</summary>
        public IReadOnlyList<Vector2> PendingClicks => _pendingClicks;

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
                // モードを抜けたら処理待ちクリックは破棄する(進行中の 1 件はサービス側が
                // 完走後に捨てる)。残すと再入時に古いクリックが突然反映されて見える。
                MaskSuggestBridge.Service?.FlushPendingClicks();
                _pendingClicks.Clear();
                _pendingClickTimes.Clear();
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
        /// 推論中のクリックも FIFO で受理され順に反映される(受理された分だけマーカーを積む)。
        /// </summary>
        public void OnPreviewClick(float u, float v, Color32[] pixelsBottomUp,
                                   int width, int height, string sourceKey)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null || !Active) return;
            if (!svc.TryEnsureModels()) return;

            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
            if (svc.RequestProposal(u, v, Granularity))
            {
                _pendingClicks.Add(new Vector2(u, v));
                _pendingClickTimes.Add(MaskSuggestPerf.Now);
            }
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
                // サービスは FIFO 処理なので、届いた提案 = マーカー先頭のクリック分。
                if (_pendingClicks.Count > 0) _pendingClicks.RemoveAt(0);
                long clickAt = 0;
                if (_pendingClickTimes.Count > 0)
                {
                    clickAt = _pendingClickTimes[0];
                    _pendingClickTimes.RemoveAt(0);
                }
                // モードを抜けていたら反映しない(Undo 割り込み分はサービス側で破棄済み)。
                if (Active && proposal != null)
                    CommitProposalToMask(proposal, clickAt);
            }
            else if (svc.Phase == MaskSuggestPhase.Error)
            {
                // Error では処理待ちがサービス側で破棄される(復帰は AI モード入り直し)。
                // マーカーだけ残ると「処理中」に見え続けるため同期して消す。
                _pendingClicks.Clear();
                _pendingClickTimes.Clear();
            }
            _host.RequestRepaint();
        }

        /// <summary>
        /// 提案領域を編集対象の除外マスク(共通 or ゾーン)へ OR 合成し、1 つの Undo ストロークとして反映する。
        /// 選んだ部分を「色替えしない範囲」へ加える = そのパーツを色替えから保護する。
        /// 反映後は通常のマスクとしてブラシ修正・Ctrl+Z(1 ストローク扱い)が効く。
        /// </summary>
        void CommitProposalToMask(MaskSuggestProposal proposal, long clickStartedAt = 0)
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

            long tCommit = MaskSuggestPerf.Now;
            double beginMs, transferMs = 0, getPixelsMs = 0, aaMs = 0, orMs, endMs;
            int added = 0;    // 実際にマスクへ足された画素数
            int proposed = 0; // 提案そのものの画素数(0 = 推論が領域を返していない)
            long t = MaskSuggestPerf.Now;
            _maskView.BeginStroke();
            beginMs = MaskSuggestPerf.MsSince(t);
            if (mw == sw && mh == sh)
            {
                t = MaskSuggestPerf.Now;
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!src[i]) continue;
                    proposed++;
                    if (!mask[i]) { mask[i] = true; added++; }
                }
                orMs = MaskSuggestPerf.MsSince(t);
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
                t = MaskSuggestPerf.Now;
                var transferred = SamMaskRefine.TransferCoverage(src, sw, sh, mw, mh);
                transferMs = MaskSuggestPerf.MsSince(t);
                orMs = 0;
                if (transferred != null)
                {
                    var tex = _host?.SourceTexture;
                    if (tex != null && tex.width == mw && tex.height == mh &&
                        SamMaskRefine.TryDeriveAaCropRect(transferred, mw, mh,
                            out int rx0, out int ry0, out int rw, out int rh, out int aaD))
                    {
                        // AA 包含は提案 bbox + マージンのクロップで実行する(出力は全画像実行と
                        // ビット同一 — 根拠は TryDeriveAaCropRect)。小パーツ提案でもマスク全
                        // 解像度の距離変換×最大 10 回が走っていた(実測 162-225ms のメイン停止)
                        // のを、画素取得ごとクロップ分に抑える。提案が空なら丸ごとスキップ。
                        try
                        {
                            // 画素は GetPixels32 で取り(GetPixels(rect) の float→byte 丸めは
                            // テクスチャ形式によって GetPixels32 と一致する保証がない)、
                            // クロップは行コピーで切り出す。
                            t = MaskSuggestPerf.Now;
                            var texPx = tex.GetPixels32();
                            Color32[] cropPx;
                            if (rw == mw && rh == mh)
                            {
                                cropPx = texPx;
                            }
                            else
                            {
                                cropPx = new Color32[rw * rh];
                                for (int cy = 0; cy < rh; cy++)
                                    System.Array.Copy(texPx, (ry0 + cy) * mw + rx0,
                                                      cropPx, cy * rw, rw);
                            }
                            getPixelsMs = MaskSuggestPerf.MsSince(t);
                            t = MaskSuggestPerf.Now;
                            SamMaskRefine.IncludeAaTransitionCropped(transferred, mw, mh,
                                cropPx, rx0, ry0, rw, rh, aaD);
                            aaMs = MaskSuggestPerf.MsSince(t);
                        }
                        catch (System.Exception)
                        {
                            // 非 Readable 等で画素が取れない場合は被覆保存転写のみ
                            // (最近傍起因の強い点ノイズはこれだけでも解消する)。
                        }
                    }
                    t = MaskSuggestPerf.Now;
                    for (int i = 0; i < mask.Length; i++)
                    {
                        if (!transferred[i]) continue;
                        proposed++;
                        if (!mask[i]) { mask[i] = true; added++; }
                    }
                    orMs = MaskSuggestPerf.MsSince(t);
                }
            }
            t = MaskSuggestPerf.Now;
            _maskView.EndStroke();
            endMs = MaskSuggestPerf.MsSince(t);
            _maskView.maskDirty = true;
            LastClickFloodWarning = proposal.floodWarning;
            LastCommitEmpty = added == 0;
            LastProposalEmpty = proposed == 0;
            if (MaskSuggestPerf.Enabled)
                MaskSuggestPerf.Log(
                    $"コミット: BeginStroke {beginMs:F0}ms / 転写 {transferMs:F0}ms" +
                    $" / GetPixels32 {getPixelsMs:F0}ms / AA包含 {aaMs:F0}ms / OR {orMs:F0}ms" +
                    $" / EndStroke {endMs:F0}ms / 合計 {MaskSuggestPerf.MsSince(tCommit):F0}ms");
            if (clickStartedAt != 0)
                MaskSuggestPerf.Log($"クリック→コミット完了 {MaskSuggestPerf.MsSince(clickStartedAt):F0}ms");
            // プロキシ段なしの再生成: 確定表示中のプレビューが低解像度へ一瞬戻る「ちらつき」を
            // 防ぐ(キューで連続コミットすると毎回プロキシが挟まり点滅に見える)。
            if (clickStartedAt != 0) MaskSuggestPerf.ArmE2EWatch(clickStartedAt);
            _host?.MarkPreviewDirtyFullRefine();
            _host?.RequestRepaint();
        }

        /// <summary>
        /// Unity Undo/Redo 実行時: ユーザーは巻き戻し中なので、処理待ち・処理中のクリックを
        /// まとめて破棄する(進行中の 1 件は完走後にサービス側が提案を捨てる)。
        /// </summary>
        public void OnUndoRedoPerformed()
        {
            if (_pendingClicks.Count > 0)
            {
                MaskSuggestBridge.Service?.FlushPendingClicks();
                _pendingClicks.Clear();
                _pendingClickTimes.Clear();
            }
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }

        /// <summary>テクスチャ切替・ウィンドウ破棄時の後始末。</summary>
        public void OnSourceChangedOrClosing()
        {
            MaskSuggestBridge.Service?.CancelAll();
            _pendingClicks.Clear();
            _pendingClickTimes.Clear();
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }
    }
}
