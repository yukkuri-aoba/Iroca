// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 自動調整の流れ(0.4.0 の計画の 4-3)。1 つのゾーンについて、スポイト位置の AI 提案を証拠に待ち、背景のスレッドで
    /// 解析して、結果(<see cref="ZoneAutoTuner.TuneResult"/>)をゾーンへ当てる。ウィンドウ(進み具合・通知・Undo・由来の
    /// 記録)と自動化(MCP。ゾーンを順に・警告)が同じ手順を使う(前は 2 本あった)。
    ///
    /// 従来導出は「クリック 1 texel + 近傍窓」の当て推量で母集団を作るため、ハイライト(明度↑彩度↓の非対称な逸脱)を
    /// 原理的に取りこぼす。そこでスポイトした位置に AI マスク提案(SAM)を 1 回かけ、そのセグメントを
    /// <see cref="ZoneAutoTuner.AnalyzeWithEvidence"/> の証拠に渡す(実測 42 ケース非退行、明部クリック IoU 0.15→0.82 等。
    /// ZoneAutoTuner.Evidence.cs 参照)。2026-08-29 まで「AI が温まっていなければ待たず従来導出」だったが、その従来導出が
    /// ハイライトを取りこぼす当の経路で、ユーザーには「自動調整が壊れる」としか見えなかった。今は:
    ///   - スポイト位置の無いゾーン(色で指定)は、証拠なしで解析する。
    ///   - 位置があれば AI の提案を待つ(AI が無ければ中止。準備中なら届くまで待つ)。
    ///   - 提案が背景まで広がったとき(floodWarning)だけ、証拠なしで解析する(AI は使えたが証拠が取れなかった)。
    /// 待ちと解析の終わりは <see cref="Run.Advance"/> で見る(メインスレッド。ウィンドウは EditorApplication.update、
    /// 自動化は update と MCP の問い合わせ)。
    /// </summary>
    internal static class AutoTuneService
    {
        /// <summary>
        /// 証拠待ちの上限。埋め込み計算(テクスチャ毎 1 回)は CPU バックエンドの大きなテクスチャで数十秒かかり得るので、
        /// 短い期限で諦めて従来導出へ落とさない(以前の 8 秒は「待たない方針」の名残。期限切れは中止して案内する)。
        /// </summary>
        internal const double EvidenceWaitSeconds = 180.0;

        /// <summary>証拠なしで解析した理由: スポイト位置が無い(色で指定したゾーン)。</summary>
        internal const string ReasonNoSamplePosition = "no-sample-position";
        /// <summary>証拠なしで解析した理由: 提案が背景まで広がった。</summary>
        internal const string ReasonEvidenceFlood = "evidence-flood";

        internal enum Status { WaitingForEvidence, Analyzing, Done, Failed }

        internal enum Failure
        {
            None,
            /// <summary>AI(Sentis とモデル)が無い。</summary>
            AiUnavailable,
            /// <summary>AI がエラー(<see cref="Run.FailureDetail"/> に理由)。</summary>
            AiError,
            /// <summary>証拠の元を AI と共有できない(ウィンドウで、プレビューと同じ原本が取れなかった)。</summary>
            SourceNotShared,
            /// <summary>提案が来なかった(取り消し・破棄・空の提案)。</summary>
            NoEvidence,
            /// <summary>証拠待ちの期限切れ。</summary>
            Timeout,
            /// <summary>解析が例外で終わった(<see cref="Run.Error"/>)。</summary>
            AnalysisFailed,
            /// <summary>呼ぶ側が止めた。</summary>
            Cancelled,
        }

        // 解析は 1 本ずつ(取り消した前の解析が抜けてから次を始める。PreviewJob と同じく、作業配列を 2 組借りない)。
        // メインスレッドからしか触らない。
        private static Task s_lastAnalysis = Task.CompletedTask;

        /// <summary>
        /// 1 つのゾーンの自動調整を始める(メインスレッド)。待ちに入らずに終わったとき(AI が無いなど)は、返した
        /// <see cref="Run.Status"/> が <see cref="Status.Failed"/>。
        /// </summary>
        /// <param name="zone">調整するゾーン(値はここで写す。待ち・解析の間に変わってもよい。当て先は id で引き直す)。</param>
        /// <param name="session">全体設定と、どのゾーンも既定のままか(ここで読む)。</param>
        /// <param name="pixels">作業解像度の原本(下原点)。参照を足して持ち、終わったら返す。終わるまで書き換えないこと。</param>
        /// <param name="sourceKey">
        /// AI の元のキー(ウィンドウのプレビューと同じ形。窓が同じテクスチャを表示していれば埋め込みを使い回す)。
        /// null = AI と共有できる元が無い(この画素で同じキーを使うと、別の画素の埋め込みと取り違える)。
        /// </param>
        /// <param name="excluded">除外マスク(共通 ∪ そのゾーン、true=除外)。null = マスク無し。</param>
        internal static Run Start(ColorZone zone, IrocaSessionState session, NativePixels pixels, string sourceKey,
            bool[] excluded, int maskW, int maskH)
        {
            zone.EnsureId();
            // 背景の解析には live の zone / session を直接渡さず、値等価コピーを渡す。解析中も Undo などでゾーンや
            // session が変異し得るため、live を渡すと sampleColor 変異で混成サンプルから無意味な tolerance を導出したり、
            // session.zones 列挙中の add/remove で InvalidOperationException になり得る。
            var run = new Run(zone.Clone(), ZoneAutoTuner.SessionInput.From(session), pixels.AddRef(), sourceKey,
                excluded, maskW, maskH);
            run.Begin();
            return run;
        }

        /// <summary>
        /// 自動調整 1 回の入力(ゾーン id ごと)。再現データ書き出し(ReproDump)が meta.json / zone{i}_evidence.png に載せ、
        /// dev_safe の test_repro_cases が同じ入力から導出をやり直す(ワンショットの再現)。書き出しの zones.json は
        /// 自動調整「後」の値なので、これが無いと「自動調整がどう導出したか」は後から再現できない(2026-09-02)。
        /// テクスチャ固有の一時情報なので Undo/プリセットには載せない。
        /// </summary>
        internal sealed class Input
        {
            public Color sample;          // 導出に渡したサンプル色(正規化前のスポイト色)
            public Color target;
            // 証拠マスク(下原点、1bit/画素に MaskSnapshot.Pack したもの。null=証拠なし導出)。
            // bool[] のままだと 4K で 1 ゾーン 16MB を持ち続ける(詰めると 2MB)。
            public ulong[] evidence;
            public int evW, evH;
            public int texW, texH;
            public bool maskUsed;         // 除外マスク(共通∪ゾーン別)を導出に渡したか
            public ZoneAutoTuner.TuneResult applied;
        }

        // 自動調整の適用値と同じとみなす許容差（ParamsIntact / ValuesIntact で共用）。
        private static bool ParamEq(float a, float b) => Mathf.Abs(a - b) <= 1e-5f;

        /// <summary>
        /// ゾーンのパラメータが、その自動調整の適用値のまま(手で触っていない)か。
        /// 再現側はこれが真のときだけ「再導出した値 = 書き出しの値」を契約にできる。
        /// </summary>
        internal static bool ParamsIntact(ColorZone zone, Input input)
        {
            if (zone == null || input == null) return false;
            var r = input.applied;
            if (!ValuesIntact(zone, r)) return false;
            var expectedSample = r.hasNormalizedSample ? r.normalizedSample : input.sample;
            int autoCount = r.autoSamples != null ? r.autoSamples.Count : 0;
            return ParamEq(zone.sampleColor.r, expectedSample.r) && ParamEq(zone.sampleColor.g, expectedSample.g)
                && ParamEq(zone.sampleColor.b, expectedSample.b)
                && (zone.extraSamples != null ? zone.extraSamples.Count : 0) == autoCount;
        }

        /// <summary>
        /// 導出パラメータだけが自動調整の適用値のままか（サンプル色・内部サンプルは見ない）。
        /// 上書き確認は「パラメータを手で変えたか」だけを問うので、色を取り直しただけの
        /// ゾーンまで手調整扱いにしないよう、サンプル側の一致とは分けて判定する。
        /// </summary>
        internal static bool ValuesIntact(ColorZone zone, ZoneAutoTuner.TuneResult r)
        {
            if (zone == null) return false;
            return ParamEq(zone.tolerance, r.tolerance)
                && ParamEq(zone.saturationStrictness, r.saturationStrictness)
                && ParamEq(zone.saturationGuard, r.saturationGuard)
                && ParamEq(zone.chromaThreshold, r.chromaThreshold)
                && zone.highlightRecovery == r.highlightRecovery
                && ParamEq(zone.valueBlend, r.valueBlend)
                && ParamEq(zone.edgeSoftness, r.edgeSoftness)
                && ParamEq(zone.shadowDesaturation, r.shadowDesaturation)
                && ParamEq(zone.shadowForgivenessSatMin, r.shadowForgivenessSatMin)
                && ParamEq(zone.shadowValueFloor, r.shadowValueFloor)
                && ParamEq(zone.partSatCeiling, r.partSatCeiling)
                && ParamEq(zone.partHueBand, r.partHueBand)
                && ParamEq(zone.chromaCeiling, r.chromaCeiling)
                && ParamEq(zone.holeFillMargin, r.holeFillMargin);
        }

        /// <summary>
        /// 導出パラメータが「ユーザーの手調整を含まない」状態か(上書きの確認を出さなくてよいか)。
        /// ・自動調整済みのゾーン(<paramref name="lastInput"/> がある): その適用値のままなら手調整なし
        /// ・未調整のゾーン: 新しいゾーンへ入れる初期値（<see cref="EditingSession.NewZoneInitialTolerance"/>）
        ///   だけが ZoneAutoTuner の既定と違うなら、それは置いた値であって手調整ではない
        /// 自動調整の入力はドメインリロードで失われるため、リロード直後の 1 回だけは
        /// 調整済みゾーンでも「手調整あり」と見えるが、安全側（確認を出す側）に倒れる。
        /// </summary>
        private static ColorZone s_zoneDefaults;

        internal static bool UntouchedByUser(ColorZone zone, Input lastInput)
        {
            if (zone == null) return false;
            if (lastInput != null) return ValuesIntact(zone, lastInput.applied);

            // 未調整ゾーン: 初期 tolerance を既定値に均してから、他に既定と違う項目が
            // 残らないかを既存の判定（PreviewOverwrittenSettings）で見る。
            var probe = zone.Clone();
            if (Mathf.Approximately(probe.tolerance, EditingSession.NewZoneInitialTolerance))
                probe.tolerance = (s_zoneDefaults ??= new ColorZone()).tolerance;
            return ZoneAutoTuner.PreviewOverwrittenSettings(probe).Count == 0;
        }

        /// <summary>
        /// 1 つのゾーンの自動調整(<see cref="Start"/> で始める)。メインスレッドで <see cref="Advance"/> を呼んで進め、
        /// <see cref="Status.Done"/> になったら <see cref="ApplyTo"/> で当てる。AI の提案の受け取り先の持ち主はこれ
        /// (<see cref="AiSuggestBroker"/>)。
        /// </summary>
        internal sealed unsafe class Run
        {
            private readonly ColorZone _zone;
            private readonly ZoneAutoTuner.SessionInput _session;
            private readonly string _sourceKey;
            private readonly bool[] _excluded;
            private readonly int _mw, _mh;
            private readonly PreviewJobProgress _progress = new PreviewJobProgress();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            // 持っている原本の参照(解析に渡したら解析が返す。失敗・取り消しで返す)。
            private NativePixels _pixels;
            private double _deadline;
            private bool _requested;
            // 解析は例外で終わらせず、例外も結果として返す(取り消した解析の例外を誰も見ないまま残さない)。
            private Task<(ZoneAutoTuner.TuneResult result, Exception error)> _analysis;
            // 解析に渡した証拠を 1bit/画素に詰めたもの(解析と同じ背景で詰める。終わった後にだけ読む)。
            private ulong[] _evidencePacked;
            private int _evW, _evH;

            internal Run(ColorZone zone, ZoneAutoTuner.SessionInput session, NativePixels pixels, string sourceKey,
                bool[] excluded, int maskW, int maskH)
            {
                _zone = zone;
                _session = session;
                _pixels = pixels;
                _sourceKey = sourceKey;
                _excluded = excluded;
                _mw = maskW;
                _mh = maskH;
                TextureWidth = pixels.Width;
                TextureHeight = pixels.Height;
            }

            /// <summary>調整するゾーンの id(当てるときはこれで引き直す。待っている間に消えたり並びが変わったりする)。</summary>
            public string ZoneId => _zone.id;
            public Status Status { get; private set; }
            public Failure Failure { get; private set; }
            /// <summary>AI のエラーの文言(<see cref="Failure.AiError"/>)。</summary>
            public string FailureDetail { get; private set; }
            /// <summary>解析の例外(<see cref="Failure.AnalysisFailed"/>)。</summary>
            public Exception Error { get; private set; }
            /// <summary>証拠を待っていて、AI がまだ受け付けていない(モデルの読み込み・埋め込みの計算・ほかの提案の処理)。</summary>
            public bool EvidencePreparing => Status == Status.WaitingForEvidence && !_requested;
            /// <summary>進み具合 0..1(待ちの間は AI の準備の進み、解析中は解析の各ステップ)。</summary>
            public float Progress => _progress.Value;
            /// <summary>AI に提案を求めたか(求めたなら、窓の無い自動化は使い終わった後に GPU を手放す)。</summary>
            public bool AskedAi { get; private set; }
            /// <summary>導出の結果(<see cref="Status.Done"/> のとき)。</summary>
            public ZoneAutoTuner.TuneResult Result { get; private set; }
            /// <summary>
            /// 証拠なしで解析した理由(<see cref="ReasonNoSamplePosition"/> / <see cref="ReasonEvidenceFlood"/>)。
            /// 証拠ありで解析した・まだ解析していないなら null。
            /// </summary>
            public string ConventionalReason { get; private set; }
            /// <summary>解析に証拠を渡したか。</summary>
            public bool HadEvidence { get; private set; }
            public int TextureWidth { get; }
            public int TextureHeight { get; }

            /// <summary>
            /// 証拠が実際に導出に使われたか(汚染セグメント等で従来へ戻った場合は導出診断が "fallback" で始まる)。
            /// </summary>
            public bool UsedEvidence => !string.IsNullOrEmpty(Result.evidenceDiag) && !Result.evidenceDiag.StartsWith("fallback");

            /// <summary>
            /// 由来(再現データの meta.json 用): 証拠の有無・正規化の有無・導出診断。書き出された失敗ケースが
            /// 「証拠経路を通ったのか、AI 未ウォームで従来導出だったのか」を後から判別できないと、どちらの経路を直せばよいか
            /// 分からない(2026-08-29: スニーカー明部クリックの再現データで証拠なし導出と byte 一致したが、証拠ありでも
            /// 同じ結果になる位置だったため区別できなかった)。
            /// </summary>
            public string Provenance =>
                (HadEvidence ? (UsedEvidence ? "evidence" : "evidence-fallback")
                             : "conventional" + (ConventionalReason != null ? "(" + ConventionalReason + ")" : ""))
                + $"; normalized={Result.hasNormalizedSample}"
                + (string.IsNullOrEmpty(Result.evidenceDiag) ? "" : "; " + Result.evidenceDiag);

            internal void Begin()
            {
                if (!_zone.HasSampleUV)
                {
                    StartAnalysis(null, 0, 0, ReasonNoSamplePosition);
                    return;
                }
                // モデルのファイルが無い・読めないときは、最初の要求(TryEnsureModels)が Unavailable を返す。
                if (!MaskSuggestBridge.Available)
                {
                    Fail(Failure.AiUnavailable);
                    return;
                }
                if (_sourceKey == null)
                {
                    Fail(Failure.SourceNotShared);
                    return;
                }
                Status = Status.WaitingForEvidence;
                AskedAi = true;
                _deadline = EditorApplication.timeSinceStartup + EvidenceWaitSeconds;
                _progress.Report(0.02f);
                RequestEvidence();
            }

            /// <summary>進められるところまで進める(メインスレッド)。</summary>
            public void Advance()
            {
                switch (Status)
                {
                    case Status.WaitingForEvidence:
                        if (!_requested) RequestEvidence();
                        // 期限切れ: 中止して案内する(後から届いた提案は捨てる)。
                        if (Status == Status.WaitingForEvidence && EditorApplication.timeSinceStartup >= _deadline)
                            Fail(Failure.Timeout);
                        break;
                    case Status.Analyzing:
                        if (_analysis.IsCompleted) FinishAnalysis();
                        break;
                }
            }

            /// <summary>止める(待ちも解析も)。解析中なら、背景の計算は次のステップの間で抜ける。</summary>
            public void Cancel()
            {
                if (Status == Status.Done || Status == Status.Failed) return;
                _cts.Cancel();
                Fail(Failure.Cancelled);
            }

            /// <summary>
            /// 結果をゾーン・全体設定へ当てる(<see cref="Status.Done"/> のとき)。導出結果 → ゾーン/グローバルの写像は
            /// <see cref="ZoneAutoTuner.TuneResult.ApplyTo"/> が単一の正(headless ハーネスの --autotune と共有。
            /// ここに項目を並べ直さない)。
            /// </summary>
            public void ApplyTo(ColorZone zone, IrocaSessionState session)
            {
                if (Status != Status.Done) throw new InvalidOperationException("auto-tune has not finished");
                Result.ApplyTo(zone, session);
            }

            /// <summary>再現データ用の入力(<see cref="Status.Done"/> のとき)。</summary>
            public Input ToInput() => new Input
            {
                sample = _zone.sampleColor, target = _zone.targetColor,
                evidence = _evidencePacked, evW = _evW, evH = _evH, texW = TextureWidth, texH = TextureHeight,
                maskUsed = _excluded != null, applied = Result,
            };

            // 証拠の要求を(再)試行する。準備中(Busy)なら次の Advance で求め直す。
            private void RequestEvidence()
            {
                var r = AiSuggestBroker.RequestWhenIdle(this, _sourceKey, _pixels,
                    _zone.sampleUV.x, _zone.sampleUV.y, OnEvidence);
                switch (r)
                {
                    case AiSuggestBroker.Request.Started:
                        _requested = true;
                        break;
                    case AiSuggestBroker.Request.Busy:
                    {
                        // 準備の進み(モデルロード・埋め込み計算)を進捗に映す。
                        var svc = MaskSuggestBridge.Service;
                        _progress.Report(0.02f + 0.28f * (svc != null ? Mathf.Clamp01(svc.Progress) : 0f));
                        break;
                    }
                    default:
                        FailAi();
                        break;
                }
            }

            private void FailAi()
            {
                var svc = MaskSuggestBridge.Service;
                if (svc != null && svc.Phase == MaskSuggestPhase.Error && !string.IsNullOrEmpty(svc.ErrorMessage))
                    Fail(Failure.AiError, svc.ErrorMessage);
                else
                    Fail(Failure.AiUnavailable);
            }

            // 提案の到着(null = 取消・破棄)。null なら中止(従来導出へは落とさない)。
            private void OnEvidence(MaskSuggestProposal proposal)
            {
                if (Status != Status.WaitingForEvidence) return;
                if (proposal == null || proposal.mask == null || proposal.width <= 0 || proposal.height <= 0)
                {
                    // AI のエラーで捨てられたのなら、その理由を出す。
                    var svc = MaskSuggestBridge.Service;
                    if (svc != null && svc.Phase == MaskSuggestPhase.Error) FailAi();
                    else Fail(Failure.NoEvidence);
                    return;
                }
                // 背景まで広がった提案は素材の証拠にならない。この場合だけは証拠なしで解析する。
                if (proposal.floodWarning) StartAnalysis(null, 0, 0, ReasonEvidenceFlood);
                // 提案の範囲は受け取りの間だけ使える(AiSuggestBroker が後で返す)。旧の解析(ZoneAutoTuner)は配列で
                // 受け取って持つので、ここで写す(T5 の新しい層で置き換える。0.4.0 の計画の 3.4.3)。
                else StartAnalysis(proposal.mask.ToArray(), proposal.width, proposal.height, null);
            }

            // evidence: 証拠マスク（true=このゾーンの素材そのもの。pixels と同じ下原点並び）。null なら従来導出。
            private void StartAnalysis(bool[] evidence, int evW, int evH, string conventionalReason)
            {
                Status = Status.Analyzing;
                ConventionalReason = conventionalReason;
                HadEvidence = evidence != null;
                _evW = evW;
                _evH = evH;
                _progress.Reset();
                _progress.Report(0.05f);

                var px = _pixels;   // ここから先は解析が返す
                _pixels = null;
                var zone = _zone;
                var session = _session;
                var excluded = _excluded;
                int mw = _mw, mh = _mh;
                var ct = _cts.Token;
                var progress = _progress;
                // 予約から抜けるまでを計算中として数える(閉じたときの解放が、走り終わるのを待って作業配列を返すため)。
                NativeMemoryLifecycle.WorkStarted();
                _analysis = s_lastAnalysis.ContinueWith(_ =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.Report(0.10f);
                        // ct を Analyze へ渡す。取り消されたら各解析ステップ間で停止し、ゾンビ実行(CPU 2 倍)を防ぐ。
                        // 解析の各ステップから進捗を受け取る。渡さないと 0.10 → 1.00 の 2 点しか報告されない。
                        var result = evidence != null
                            ? ZoneAutoTuner.AnalyzeWithEvidence(px.Ptr, px.Width, px.Height, zone, session,
                                evidence, evW, evH, excluded, mw, mh, ct, progress.Report)
                            : ZoneAutoTuner.Analyze(px.Ptr, px.Width, px.Height, zone, session, excluded, mw, mh, ct,
                                progress.Report);
                        // 適用後も持つ証拠(再現データ用)は 1bit/画素に詰めたもの。
                        _evidencePacked = MaskSnapshot.Pack(evidence);   // null は null
                        progress.Report(1.0f);
                        return (result, (Exception)null);
                    }
                    catch (Exception e) { return (default(ZoneAutoTuner.TuneResult), e); }
                    finally
                    {
                        px.Release();
                        NativeMemoryLifecycle.WorkEnded();
                    }
                }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                s_lastAnalysis = _analysis;
            }

            private void FinishAnalysis()
            {
                var (result, error) = _analysis.Result;
                if (error == null)
                {
                    Result = result;
                    Status = Status.Done;
                }
                else if (error is OperationCanceledException) Fail(Failure.Cancelled);
                else Fail(Failure.AnalysisFailed, error.Message, error);
            }

            private void Fail(Failure failure, string detail = null, Exception error = null)
            {
                Status = Status.Failed;
                Failure = failure;
                FailureDetail = detail;
                Error = error;
                // 待っている提案(あれば)を取り消す。届かない知らせ(OnEvidence(null))は、待っていないので素通りする。
                AiSuggestBroker.Cancel(this);
                var px = _pixels;
                _pixels = null;
                px?.Release();
            }
        }
    }
}
