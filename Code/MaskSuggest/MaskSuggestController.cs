// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の入力→反映を仲介するコントローラ。
    ///
    /// UX は「メニューで選ぶ 1 回 = 1 反映」:
    ///   プレビューを右クリック → メニューの「ここも塗る / ここは塗らない / この部分だけ塗る」→ SAM が領域を推定 →
    ///   その領域に、選んだ時点の宛先(ゾーン × 操作)を即当てる(= 1 つの Undo
    ///   ストローク)。積み上げ・確定ボタンは無く、間違えたら Ctrl+Z で 1 手ずつ戻す。
    ///   複数の島に分かれたパーツは、島ごとに右クリックすればそれぞれ別ストロークで足される。
    ///
    /// 2026-09-30 まではマスク編集パレットの「AI 提案」ツール(モード)を ON にして右クリックする
    /// 方式だった。よく使う操作がパレットの奥に隠れるので、モードを廃して右クリックメニューへ移した
    /// (メニューは PreviewView.ShowPreviewContextMenu)。
    ///
    /// サービス(Sentis 側)へ求めるのも、提案を受け取るのも <see cref="AiSuggestBroker"/> を通す(待ち行列の持ち主は
    /// そこだけ。自動調整の証拠も同じ待ち行列に並ぶ)。Sentis 不在時はインスタンスも作られない(MaskSuggestSection 参照)。
    /// </summary>
    internal sealed class MaskSuggestController
    {
        IrocaWindow _host;
        MaskPaintView _maskView;
        bool _subscribed;

        // 反映待ちのクリック 1 件(受理した時刻は E2E 計測用、宛先はメニューで選んだ時点の対象ゾーン × 操作)。
        sealed class PendingClick
        {
            public Vector2 uv;
            public long at;
            public MaskPaintView.MaskDestination dest;
        }
        readonly List<PendingClick> _pending = new List<PendingClick>();

        // 反映待ちクリックの UV(古い順。_pending と同じ並び)。プレビュー上の待機マーカー表示に使う(可視化しないと
        // 推論が追いつくまで「押したのに無反応」に見えて二度押しを誘う)。要素の増減は必ず _pending と同じ箇所で行う。
        readonly List<Vector2> _pendingClicks = new List<Vector2>();

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
            if (!_subscribed)
            {
                AiSuggestBroker.StateChanged += OnServiceStateChanged;
                AiSuggestBroker.EnsureSubscribed();
                _subscribed = true;
            }
        }

        /// <summary>
        /// ウィンドウ破棄時の後始末。描き直しの購読を外し、このウィンドウの要求を取り消す。
        /// 購読は静的(<see cref="AiSuggestBroker.StateChanged"/>)なので、外さないと閉じたウィンドウの
        /// コントローラがイベント経由で生き続ける。
        /// </summary>
        public void Shutdown()
        {
            if (_subscribed) AiSuggestBroker.StateChanged -= OnServiceStateChanged;
            _subscribed = false;
            OnSourceChangedOrClosing();
        }

        /// <summary>
        /// 処理待ちの提案を取り消す(Esc)。進行中の 1 件はサービス側が完走後に捨てる。
        /// 戻り値 true = 取り消すものがあった。
        /// </summary>
        public bool CancelPending()
        {
            if (_pending.Count == 0) return false;
            ClearQueues();
            _host?.RequestRepaint();
            return true;
        }

        // 待ちの要求を取り消し、手元の列を空にする。
        void ClearQueues()
        {
            AiSuggestBroker.Cancel(this);
            _pending.Clear();
            _pendingClicks.Clear();
        }

        /// <summary>
        /// クリックを待たずにソース画像の解析(埋め込み計算)を先行させる。
        ///
        /// Unity 起動後の初回はモデルのロードと推論カーネル(Burst / コンピュートシェーダ)の
        /// コンパイルで時間がかかる。クリック後にそれを始めると「押しても無反応」に見えるため、
        /// 右クリックメニューを開いた時点(PreviewView.ShowPreviewContextMenu)で走らせて進捗を出す。
        /// メニューを開くたびに呼ばれるが、同一ソースならサービス側で no-op になる。
        /// </summary>
        public void PrepareSource(NativePixels pixelsBottomUp, string sourceKey)
        {
            // 待機中だけ先行させる。モデルのロードは同期で重いのでここでは開始せず(それはメニュー
            // 項目を選んだときの RequestProposal の仕事)、エラー中も再試行しない(原因が直らないまま
            // メニューを開くたびに走る。復帰はメニューから選び直す = RequestProposal が止めてやり直す)。
            AiSuggestBroker.Prepare(sourceKey, pixelsBottomUp);
        }

        /// <summary>
        /// 右クリックメニューからの提案要求。pixels は実フル解像度ソース(下原点)。
        /// 推論は非同期で、結果は <see cref="OnServiceStateChanged"/> が dest のマスクへ直接反映する。
        /// 推論中の要求も FIFO で受理され順に反映される(受理された分だけマーカーを積む)。
        /// 戻り値 false = 受理できなかった(モデルのロード失敗など)。
        /// granularity = この 1 回だけの粒度(右クリックの「小さめの部分で」など)。null = <see cref="Granularity"/>。
        /// </summary>
        public bool RequestProposal(float u, float v, MaskPaintView.MaskDestination dest,
                                    NativePixels pixelsBottomUp, string sourceKey,
                                    MaskSuggestGranularity? granularity = null)
        {
            var click = new PendingClick { uv = new Vector2(u, v), at = MaskSuggestPerf.Now, dest = dest };
            // 手元の列には先に積む(受理されなかったら外す)。結果が null(取り消し・破棄・エラー)なら
            // マーカーだけ消す。残ると「処理中」に見え続ける。
            _pending.Add(click);
            _pendingClicks.Add(click.uv);
            if (!AiSuggestBroker.Enqueue(this, sourceKey, pixelsBottomUp, u, v, granularity ?? Granularity,
                    proposal => OnProposal(click, proposal)))
            {
                RemovePending(click);
                return false;
            }
            _host?.RequestRepaint();
            return true;
        }

        void OnProposal(PendingClick click, MaskSuggestProposal proposal)
        {
            // 取り消し後(手元の列から外した後)に届いたものは反映しない。
            if (!RemovePending(click)) return;
            // 破棄済みウィンドウ(fake-null)には触れない。Unity の破棄済みオブジェクトは参照自体は非 null のため
            // `?.` をすり抜けるので、判定には Unity がオーバーロードした `==` を使うこと。
            if (_host == null) return;
            if (proposal != null) CommitProposalToMask(proposal, click.dest, click.at);
            _host.RequestRepaint();
        }

        bool RemovePending(PendingClick click)
        {
            int i = _pending.IndexOf(click);
            if (i < 0) return false;
            _pending.RemoveAt(i);
            _pendingClicks.RemoveAt(i);
            return true;
        }

        // サービスの状態の変化(読み込み・埋め込みの計算の進み)を描き直す。
        void OnServiceStateChanged()
        {
            // 破棄済みウィンドウに紐づく購読者(= Shutdown を取りこぼした残骸)は自己解除する。
            if (_host == null)
            {
                AiSuggestBroker.StateChanged -= OnServiceStateChanged;
                _subscribed = false;
                return;
            }
            _host.RequestRepaint();
        }

        /// <summary>
        /// 提案領域をマスク解像度の範囲にし、宛先の操作(ここも塗る / ここは塗らない / この部分だけ塗る)を
        /// 1 つの Undo ストロークとして当てる(規則は MaskRegionEdit、当てるのは MaskPaintView.ApplyRegion)。
        /// 反映後は通常のマスクとしてブラシ修正・Ctrl+Z(1 ストローク扱い)が効く。
        /// </summary>
        void CommitProposalToMask(MaskSuggestProposal proposal, MaskPaintView.MaskDestination dest,
                                  long clickStartedAt = 0)
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
            int mw = _maskView.maskWidth, mh = _maskView.maskHeight;
            if (mw <= 0 || mh <= 0) return;

            long tCommit = MaskSuggestPerf.Now;
            double transferMs = 0, getPixelsMs = 0, aaMs = 0, applyMs;
            long t;
            bool[] region = src;
            if (mw != sw || mh != sh)
            {
                // マスク解像度がソースと異なる場合(実ファイル解像度で仕上げた提案 →
                // インポート解像度のマスクキャンバス等)は被覆保存で転写する。旧実装の
                // 最近傍サンプリングは縮小時に仕上げ済み境界の被覆を 1 セル単位で欠けさせ、
                // 取り残し画素が再着色されて境界の点ノイズになっていた(実測: 強ドット 177個
                // → 被覆保存で 0)。さらにインポート縮小はマスク解像度側に新たな混合画素を
                // 作るため、提案の寄与分だけを対象に AA 遷移包含をマスク解像度で再適用して
                // から当てる(ユーザーの既存ストロークには触れない)。
                t = MaskSuggestPerf.Now;
                region = SamMaskRefine.TransferCoverage(src, sw, sh, mw, mh);
                transferMs = MaskSuggestPerf.MsSince(t);
                if (region == null) return;
                var tex = _host?.SourceTexture;
                if (tex != null && tex.width == mw && tex.height == mh &&
                    SamMaskRefine.TryDeriveAaCropRect(region, mw, mh,
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
                        SamMaskRefine.IncludeAaTransitionCropped(region, mw, mh,
                            cropPx, rx0, ry0, rw, rh, aaD);
                        // 細い描き込みの提案は、やり直した AA 包含が落ち影を「混色」として足し直すので、
                        // 精密化と同じく最後に地を外す(細い成分が無ければ外接矩形の距離変換 1 回で戻る)。
                        SamMaskRefine.PeelThinSpill(region, texPx, mw, mh);
                        aaMs = MaskSuggestPerf.MsSince(t);
                    }
                    catch (System.Exception)
                    {
                        // 画素を取得できない場合も、被覆保存転写だけで提案を適用できる。
                    }
                }
            }

            int proposed = 0; // 提案そのものの画素数(0 = 推論が領域を返していない)
            for (int i = 0; i < region.Length; i++)
                if (region[i]) proposed++;
            LastClickFloodWarning = proposal.floodWarning;
            LastProposalEmpty = proposed == 0;
            // 空の提案で「この部分だけ」を当てると全体が除外になるので、当てずに止める。
            if (proposed == 0) return;

            t = MaskSuggestPerf.Now;
            bool ok = false;
            MaskRegionEditResult result = default;
            // 提案は推論を待ってから届くので、待っている間の別の操作とは別の「元に戻す」1 回分にする。
            SeparateUndo.Record("AI Mask Suggestion",
                () => ok = _maskView.ApplyRegion(region, dest, out result));
            applyMs = MaskSuggestPerf.MsSince(t);
            LastCommitEmpty = !ok || result.changed == 0;
            // 何をしたか(と、指示どおりに塗られない理由)を知らせる。結果は推論待ちの後に届くので、
            // 黙っていると右クリックの操作と結び付かない。
            _host?.ShowNotification(new GUIContent(_maskView.DescribeRegionEdit(dest, ok, result)));
            if (MaskSuggestPerf.Enabled)
                MaskSuggestPerf.Log(
                    $"コミット: 転写 {transferMs:F0}ms / GetPixels32 {getPixelsMs:F0}ms / AA包含 {aaMs:F0}ms" +
                    $" / 反映(Undo 込み) {applyMs:F0}ms / 合計 {MaskSuggestPerf.MsSince(tCommit):F0}ms");
            if (clickStartedAt != 0)
                MaskSuggestPerf.Log($"クリック→コミット完了 {MaskSuggestPerf.MsSince(clickStartedAt):F0}ms");
            // プロキシ段なしの再生成: 確定表示中のプレビューが低解像度へ一瞬戻る「ちらつき」を
            // 防ぐ(キューで連続コミットすると毎回プロキシが挟まり点滅に見える)。
            if (clickStartedAt != 0) MaskSuggestPerf.ArmE2EWatch(clickStartedAt);
            _host?.NotifyMasksEditedFullRefine();
            _host?.RequestRepaint();
        }

        /// <summary>
        /// Unity Undo/Redo 実行時: ユーザーは巻き戻し中なので、処理待ち・処理中のクリックを
        /// まとめて破棄する(進行中の 1 件は完走後にサービス側が提案を捨てる)。
        /// </summary>
        public void OnUndoRedoPerformed()
        {
            if (_pending.Count > 0) ClearQueues();
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }

        /// <summary>テクスチャ切替・ウィンドウ破棄時の後始末。</summary>
        public void OnSourceChangedOrClosing()
        {
            // ほかの要求(別のウィンドウ・自動化)が無ければ、サービスの処理も止めて元の参照を手放させる。
            AiSuggestBroker.Release(this);
            _pending.Clear();
            _pendingClicks.Clear();
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }
    }
}
