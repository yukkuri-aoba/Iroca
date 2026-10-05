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

        // 提案の宛先。サービスは受理順(FIFO)に 1 件ずつ ProposalReady にするので、要求と同じ
        // 順で宛先を積んでおけば「今取り出せる提案が誰のものか」が決まる。マスク反映
        // (右クリック)のほかに、自動調整が証拠セグメントを 1 件要求する経路(RequestEvidence)
        // が同じサービスを共有するため必要になった。サービス側のキュー破棄(Flush/CancelAll/
        // Error)と同じ箇所で必ず丸ごと空にし、両者を一致させ続ける。
        enum ProposalOwner { Mask, Evidence }
        readonly List<ProposalOwner> _owners = new List<ProposalOwner>();
        // 証拠要求の受け取り先(同時に 1 件)。null = 要求なし、または取消済み
        // (取消後に届いた提案は宛先だけ消費して捨てる)。
        System.Action<MaskSuggestProposal> _evidenceCallback;

        // _pendingClicks と同期して保つ宛先(メニューで選んだ時点の対象ゾーン × 除外/含める)。
        readonly List<MaskPaintView.MaskDestination> _pendingDestinations = new List<MaskPaintView.MaskDestination>();

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
        }

        /// <summary>
        /// 処理待ちの提案を取り消す(Esc)。進行中の 1 件はサービス側が完走後に捨てる。
        /// 戻り値 true = 取り消すものがあった。
        /// </summary>
        public bool CancelPending()
        {
            if (_pendingClicks.Count == 0) return false;
            MaskSuggestBridge.Service?.FlushPendingClicks();
            ClearQueues();
            _host?.RequestRepaint();
            return true;
        }

        // サービスのキューと対になっている手元の列を丸ごと空にする(Flush/CancelAll/Error と同じ箇所で呼ぶ)。
        void ClearQueues()
        {
            _pendingClicks.Clear();
            _pendingClickTimes.Clear();
            _pendingDestinations.Clear();
            _owners.Clear();
            FailEvidence();
        }

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
            if (svc == null) return;
            // 待機中だけ先行させる。モデルのロードは同期で重いのでレイアウト中には開始せず
            // (それは AI 提案を開始したときの仕事)、エラー中も再試行しない(原因が直らない
            // まま毎レイアウト走ってエディタが重くなる。復帰は AI 提案の入り直し)。
            if (svc.Phase != MaskSuggestPhase.Idle) return;
            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
        }

        /// <summary>
        /// 右クリックメニューからの提案要求。pixels は実フル解像度ソース(下原点)。
        /// 推論は非同期で、結果は <see cref="OnServiceStateChanged"/> が dest のマスクへ直接反映する。
        /// 推論中の要求も FIFO で受理され順に反映される(受理された分だけマーカーを積む)。
        /// 戻り値 false = 受理できなかった(モデルのロード失敗など)。
        /// </summary>
        public bool RequestProposal(float u, float v, MaskPaintView.MaskDestination dest,
                                    Color32[] pixelsBottomUp, int width, int height, string sourceKey)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) return false;
            // エラー表示のまま選び直したときは、ここが再試行の導線になる
            // (自動再試行は原因が直らないまま走り続けるので入れない)。
            if (svc.Phase == MaskSuggestPhase.Error) svc.CancelAll();
            if (!svc.TryEnsureModels()) return false;

            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
            if (!svc.RequestProposal(u, v, Granularity)) return false;
            _pendingClicks.Add(new Vector2(u, v));
            _pendingClickTimes.Add(MaskSuggestPerf.Now);
            _pendingDestinations.Add(dest);
            _owners.Add(ProposalOwner.Mask);
            _host?.RequestRepaint();
            return true;
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
                // サービスは FIFO のため、提案は宛先列の先頭に対応する(宛先列が空なら
                // 従来どおりマスク反映として扱う)。
                var owner = ProposalOwner.Mask;
                if (_owners.Count > 0)
                {
                    owner = _owners[0];
                    _owners.RemoveAt(0);
                }
                if (owner == ProposalOwner.Evidence)
                {
                    // 自動調整の証拠。取消済み(callback null)なら宛先だけ消費して捨てる。
                    var cb = _evidenceCallback;
                    _evidenceCallback = null;
                    cb?.Invoke(proposal);
                }
                else
                {
                    if (_pendingClicks.Count > 0) _pendingClicks.RemoveAt(0);
                    long clickAt = 0;
                    if (_pendingClickTimes.Count > 0)
                    {
                        clickAt = _pendingClickTimes[0];
                        _pendingClickTimes.RemoveAt(0);
                    }
                    // 宛先が無い(取り消し後に届いた)提案はマスクへ反映しない。
                    if (_pendingDestinations.Count > 0)
                    {
                        var dest = _pendingDestinations[0];
                        _pendingDestinations.RemoveAt(0);
                        if (proposal != null) CommitProposalToMask(proposal, dest, clickAt);
                    }
                }
            }
            else if (svc.Phase == MaskSuggestPhase.Error)
            {
                // Error では処理待ちがサービス側で破棄される(復帰はメニューから選び直す)。
                // マーカーだけ残ると「処理中」に見え続けるため同期して消す。
                ClearQueues();
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
            bool ok = _maskView.ApplyRegion(region, dest, out var result);
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
                // Flush はサービスのキューを丸ごと捨てる(証拠要求が並んでいても同じ)。
                MaskSuggestBridge.Service?.FlushPendingClicks();
                ClearQueues();
            }
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }

        /// <summary>テクスチャ切替・ウィンドウ破棄時の後始末。</summary>
        public void OnSourceChangedOrClosing()
        {
            MaskSuggestBridge.Service?.CancelAll();
            ClearQueues();
            LastClickFloodWarning = false;
            LastCommitEmpty = false;
            LastProposalEmpty = false;
        }

        // ─── 自動調整の証拠(スポイト位置の提案セグメント) ───

        /// <summary>
        /// 自動調整の証拠用に、UV(下原点)の提案を 1 件要求する。AI が温まっている
        /// (モデルロード済み・このソースの埋め込み計算済み・待機中)ときだけ受理する(Started)。
        /// 準備中なら Busy(埋め込み計算は SetSource で始まるので、呼び出し側は待って再要求する)。
        /// 結果は onResult(提案。取消・破棄時は null)で 1 回だけ返す。同時に 1 件まで。
        /// 右クリックメニューからの提案とは独立に使える(同じサービスを FIFO で共有する)。
        /// pixels/sourceKey はプレビューの AI 提案と同じ true source を渡すこと
        /// (別キーだとソース切替扱いになり、進行中の提案が破棄される)。
        /// </summary>
        public EvidenceRequest RequestEvidence(float u, float v, Color32[] pixelsBottomUp, int width, int height,
                                               string sourceKey, System.Action<MaskSuggestProposal> onResult)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null || onResult == null || _evidenceCallback != null) return EvidenceRequest.Unavailable;
            if (!svc.TryEnsureModels()) return EvidenceRequest.Unavailable;
            if (svc.Phase == MaskSuggestPhase.Error) return EvidenceRequest.Unavailable;
            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
            // Idle 以外(ロード中・埋め込み計算中・マスク提案の処理中)は「準備中」。呼び出し側が
            // 待って再要求する(従来導出へは落とさない。落とすと、その従来導出がハイライトを
            // 取りこぼす当の経路なので「自動調整が壊れる」ようにしか見えない)。
            if (svc.Phase != MaskSuggestPhase.Idle) return EvidenceRequest.Busy;
            if (!svc.RequestProposal(u, v, MaskSuggestGranularity.Auto)) return EvidenceRequest.Unavailable;
            _owners.Add(ProposalOwner.Evidence);
            _evidenceCallback = onResult;
            return EvidenceRequest.Started;
        }

        /// <summary><see cref="RequestEvidence"/> の結果。</summary>
        public enum EvidenceRequest
        {
            /// <summary>受理した。結果は onResult で 1 回だけ返る。</summary>
            Started,
            /// <summary>AI が準備中(モデルロード・埋め込み計算・別の提案処理)。後で再要求する。</summary>
            Busy,
            /// <summary>AI が使えない(Sentis/モデル不在・エラー・ソース未設定)。</summary>
            Unavailable,
        }

        /// <summary>証拠要求を取り消す(期限切れ・中止)。届いた提案は宛先だけ消費して捨てる。</summary>
        public void CancelEvidence()
        {
            _evidenceCallback = null;
        }

        // サービスのキューが破棄されたとき、待っている証拠要求に「来ない」を伝える。
        void FailEvidence()
        {
            var cb = _evidenceCallback;
            _evidenceCallback = null;
            cb?.Invoke(null);
        }
    }
}
