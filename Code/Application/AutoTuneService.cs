// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Iroca {
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
    ///
    /// 証拠を使って導けたときは、解析の後に「漏れの確かめ」(<see cref="Status.CheckingLeaks"/>。Core の
    /// ZoneAutoTuner.Leak.cs)を続ける: 導いた値で選択だけを回し、証拠の外に塗られた塊ごとに AI の提案を取り直して、
    /// 別の部品の陰・ツヤだけを巻き込んでいる所を、外す範囲(<see cref="Run.LeakExclusion"/>)にする。呼び出し側は
    /// <see cref="Run.ApplyTo"/> と同じ Undo の 1 回分で、その範囲をゾーンの「塗らない」へ書く(右クリックの「ここは塗らない」と
    /// 同じ入口)。確かめの途中の失敗(AI のエラー・取り消し・期限切れ・空の提案)は自動調整を失敗にせず、確かめだけを飛ばす
    /// (<see cref="Run.LeakSkipReason"/>)。
    /// </summary>
    internal static class AutoTuneService {
        /// <summary>
        /// 証拠待ちの上限。埋め込み計算(テクスチャ毎 1 回)は CPU バックエンドの大きなテクスチャで数十秒かかり得るので、
        /// 短い期限で諦めて従来導出へ落とさない(以前の 8 秒は「待たない方針」の名残。期限切れは中止して案内する)。
        /// </summary>
        internal const double EvidenceWaitSeconds = 180.0;

        // EditMode テストだけが立てる(0 = 使わない)。証拠待ちの期限切れを短い時間で確かめるため。
        internal static double EvidenceWaitSecondsForTests;

        /// <summary>証拠待ちの上限(秒)。ふつうは <see cref="EvidenceWaitSeconds"/>。</summary>
        internal static double EvidenceWait => EvidenceWaitSecondsForTests > 0 ? EvidenceWaitSecondsForTests : EvidenceWaitSeconds;

        /// <summary>証拠なしで解析した理由: スポイト位置が無い(色で指定したゾーン)。</summary>
        internal const string ReasonNoSamplePosition = AutoTuneFlow.ReasonNoSamplePosition;
        /// <summary>証拠なしで解析した理由: 提案が背景まで広がった。</summary>
        internal const string ReasonEvidenceFlood = AutoTuneFlow.ReasonEvidenceFlood;

        /// <summary>漏れの確かめを飛ばした理由: 提案の寸法が作業解像度と違う。</summary>
        internal const string LeakSkipSizeMismatch = AutoTuneFlow.LeakSkipSizeMismatch;
        /// <summary>漏れの確かめを飛ばした理由: 提案を待つ期限が切れた。</summary>
        internal const string LeakSkipTimeout = AutoTuneFlow.LeakSkipTimeout;
        /// <summary>漏れの確かめを飛ばした理由: 提案が来なかった(取り消し・破棄・空の提案)。</summary>
        internal const string LeakSkipNoProposal = AutoTuneFlow.LeakSkipNoProposal;
        /// <summary>漏れの確かめを飛ばした理由: AI が使えなくなった(後ろに AI のエラーの文言が付くことがある)。</summary>
        internal const string LeakSkipAi = AutoTuneFlow.LeakSkipAi;
        /// <summary>漏れの確かめを飛ばした理由: 確かめの計算が例外で終わった(後ろに例外の文言が付く)。</summary>
        internal const string LeakSkipError = AutoTuneFlow.LeakSkipError;

        // 証拠つきの解析の後に漏れの確かめが続くときの、進み具合の割り振り(解析 0..AnalysisShare、塊の取り出し、
        // 自動の提案、その測り、細かい提案、判定の順)。表示だけに使う。
        private const float AnalysisShare = 0.70f;
        private const float LeakProbedAt = 0.75f;
        private const float LeakCoarseSpan = 0.10f;
        private const float LeakMeasuredAt = 0.87f;
        private const float LeakFineSpan = 0.08f;

        internal enum Status {
            WaitingForEvidence,
            Analyzing,
            /// <summary>解析の後の漏れの確かめ(AI の提案を待つ・背景で測る・判定する)。</summary>
            CheckingLeaks,
            Done,
            Failed,
        }

        internal enum Failure {
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

        // EditMode テストだけが立てる。証拠の受け取り先で例外が出たときの後始末を確かめるため、受け取りで投げる。
        // 製品のコードからは立てない。
        internal static bool ThrowOnEvidenceForTests;

        // EditMode テストだけが立てる。漏れの確かめの提案の受け取りで投げる(確かめだけを飛ばし、自動調整は失敗にしないことを確かめる)。
        // 製品のコードからは立てない。
        internal static bool ThrowOnLeakProposalForTests;

        // 解析は 1 本ずつ(取り消した前の解析が抜けてから次を始める。PreviewJob と同じく、作業配列を 2 組借りない)。
        // メインスレッドからしか触らない。
        private static Task s_lastAnalysis = Task.CompletedTask;

        /// <summary>
        /// 1 つのゾーンの自動調整を始める(メインスレッド)。待ちに入らずに終わったとき(AI が無いなど)は、返した
        /// <see cref="Run.Status"/> が <see cref="Status.Failed"/>。
        /// </summary>
        /// <param name="zone">調整するゾーン(値はここで写す。待ち・解析の間に変わってもよい。当て先は id で引き直す)。</param>
        /// <param name="pixels">作業解像度の原本(下原点)。参照を足して持ち、終わったら返す。終わるまで書き換えないこと。</param>
        /// <param name="sourceKey">
        /// AI の元のキー(ウィンドウのプレビューと同じ形。窓が同じテクスチャを表示していれば埋め込みを使い回す)。
        /// null = AI と共有できる元が無い(この画素で同じキーを使うと、別の画素の埋め込みと取り違える)。
        /// </param>
        /// <param name="excluded">除外マスク(共通 ∪ そのゾーン、true=除外)。null = マスク無し。持ち主は自動調整になり、終わったら返す。</param>
        /// <param name="settings">
        /// 全体設定(ウィンドウはセッションの設定、自動化はその依頼の設定)。漏れの確かめで、導いた値の選択を製品が塗るときと
        /// 同じ道で回すのに使う。
        /// </param>
        /// <param name="masks">
        /// 選択を回すときのマスク(共通・ゾーン別の除外と含める。<see cref="MaskWorkspace.ToSnapshot"/>)。null = マスク無し。
        /// 持ち主は自動調整になり、終わったら返す。
        /// </param>
        /// <param name="retryAiError">
        /// 前の推論のエラーをなかったことにして試し直すか。何ゾーンもまとめて頼むときは最初の 1 回だけ(直らないエラーでゾーンごとに待たせない)。
        /// </param>
        internal static Run Start(ColorZone zone, NativePixels pixels, string sourceKey,
            NativeBools excluded, int maskW, int maskH, in RecolorSettings settings, MaskSnapshot masks,
            bool retryAiError = true) {
            zone.EnsureId();
            // 背景の解析には live の zone を直接渡さず、値等価コピーを渡す。解析中も Undo などでゾーンが変わり得るため、
            // live を渡すと sampleColor の変化で混成サンプルから無意味な tolerance を導出し得る。
            // 原本の参照はフロー(AutoTuneFlow)が自分で足して持つので、ここでは足さない(足すと 1 つ返し忘れる)。
            var run = new Run(zone.Clone(), pixels, sourceKey,
                excluded, maskW, maskH, settings, masks) { _retryAiError = retryAiError };
            run.Begin();
            return run;
        }

        /// <summary>
        /// 自動調整 1 回の入力(ゾーン id ごと)。再現データ書き出し(ReproDump)が meta.json / zone{i}_evidence.png に載せ、
        /// dev_safe の test_repro_cases が同じ入力から導出をやり直す(ワンショットの再現)。書き出しの zones.json は
        /// 自動調整「後」の値なので、これが無いと「自動調整がどう導出したか」は後から再現できない(2026-09-02)。
        /// テクスチャ固有の一時情報なので Undo/プリセットには載せない。
        /// </summary>
        internal sealed class Input {
            public Color sample;          // 導出に渡したサンプル色(正規化前のスポイト色)
            public Color target;
            // 証拠マスク(下原点、1bit/画素に MaskSnapshot.Pack したもの。null=証拠なし導出、または控えを取っていない)。
            // bool[] のままだと 4K で 1 ゾーン 16MB を持ち続ける(詰めると 2MB)。控えるのは <see cref="KeepEvidenceForRepro"/> のときだけ。
            public ulong[] evidence;
            public int evW, evH;
            public int texW, texH;
            public bool maskUsed;         // 除外マスク(共通∪ゾーン別)を導出に渡したか
            public ZoneAutoTuner.TuneResult applied;
            // 漏れの確かめで外した塊の数と画素数(作業解像度)。ゾーンの除外マスクには、導出の後にこの範囲が足されている
            // (再現で導出をやり直すとき、書き出しの除外マスクは導出の時点のものより広い)。確かめなかったら 0。
            public int leakParts;
            public int leakPixels;
        }

        /// <summary>
        /// 解析に渡した証拠を、再現データ書き出し(Code/Debug の ReproDump)のために詰めて控えるか。読むのは ReproDump だけで、
        /// Code/Debug は配布しない。配布物では誰も読まないのに、全画素を詰め直す時間が自動調整の待ちに乗り、ゾーンごとに
        /// 4K で 2MB・8K で 8MB を管理ヒープに持ち続けていた。Debug のアセンブリが起動時に立てる(DebugBootstrap)。
        /// </summary>
        internal static volatile bool KeepEvidenceForRepro;

        // 自動調整の適用値と同じとみなす許容差（ParamsIntact / ValuesIntact で共用）。
        private static bool ParamEq(float a, float b) => Mathf.Abs(a - b) <= 1e-5f;

        /// <summary>
        /// ゾーンのパラメータが、その自動調整の適用値のまま(手で触っていない)か。
        /// 再現側はこれが真のときだけ「再導出した値 = 書き出しの値」を契約にできる。
        /// </summary>
        internal static bool ParamsIntact(ColorZone zone, Input input) {
            if (zone == null || input == null) {
                return false;
            }
            var r = input.applied;
            if (!ValuesIntact(zone, r)) {
                return false;
            }
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
        internal static bool ValuesIntact(ColorZone zone, ZoneAutoTuner.TuneResult r) {
            if (zone == null) {
                return false;
            }
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
                && ParamEq(zone.holeFillMargin, r.holeFillMargin)
                && ParamEq(zone.matchCutoff, r.matchCutoff);
        }

        // 適用値のうち、前のサンプルの島から導いた 6 項目を既定へ戻したもの(サンプル色を取り直したときの
        // EditingSession.ClearIslandDerived と同じ。手で触っていないので、手調整とは見ない)。6 項目の一覧は Core に 1 つだけ。
        private static ZoneAutoTuner.TuneResult IslandCleared(ZoneAutoTuner.TuneResult r) =>
            ZoneAutoTuner.ResetIslandDerived(r);

        // 既定値の見本(読むだけ。メインスレッドからだけ作る)。
        private static ColorZone s_zoneDefaults;

        /// <summary>
        /// 導出パラメータが「ユーザーの手調整を含まない」状態か(上書きの確認を出さなくてよいか)。
        /// ・自動調整済みのゾーン(<paramref name="lastInput"/> がある): その適用値のまま(サンプル色を取り直して、前の島から
        ///   導いた値だけが既定へ戻ったもの・塗り先を変えて模様保持を導き直したものを含む)なら手調整なし
        /// ・それ以外(未調整のゾーン、自動調整の後に Undo で適用前の値へ戻ったゾーン): 新しいゾーンへ入れる初期値
        ///   （<see cref="EditingSession.NewZoneInitialTolerance"/>）だけが ZoneAutoTuner の既定と違うなら、それは置いた値で
        ///   あって手調整ではない(<see cref="UserOverwrittenSettings"/> が空)。入力は Undo では消えないので、適用値と違う
        ///   からといって手調整とは決めない(以前は決めていて、自動調整 → Undo → 自動調整で確認が出た。点検 自-1)。
        /// 自動調整の入力はドメインリロードで失われるため、リロード直後の 1 回だけは
        /// 調整済みゾーンでも「手調整あり」と見えるが、安全側（確認を出す側）に倒れる。
        /// </summary>
        internal static bool UntouchedByUser(ColorZone zone, Input lastInput) {
            if (zone == null) {
                return false;
            }
            if (lastInput != null) {
                var applied = lastInput.applied;
                // 塗り先を変えて模様保持を導き直した(ZoneEdits.SetTarget)ゾーンも、手で触っていない。
                if (zone.ValueBlendIsDerived) {
                    applied.valueBlend = zone.valueBlend;
                }
                if (ValuesIntact(zone, applied) || ValuesIntact(zone, IslandCleared(applied))) {
                    return true;
                }
            }
            return UserOverwrittenSettings(zone).Count == 0;
        }

        /// <summary>
        /// 自動調整が上書きする、既定と違う導出パラメータ(上書きの確認に並べる項目)。新しいゾーンへ入れる初期の許容範囲
        /// (<see cref="EditingSession.NewZoneInitialTolerance"/>)は置いた値なので、既定へ均してから見る。
        /// </summary>
        internal static System.Collections.Generic.List<ZoneAutoTuner.TunedSetting> UserOverwrittenSettings(ColorZone zone) {
            if (zone == null) {
                return new System.Collections.Generic.List<ZoneAutoTuner.TunedSetting>();
            }
            var probe = zone.Clone();
            if (Mathf.Approximately(probe.tolerance, EditingSession.NewZoneInitialTolerance)) {
                probe.tolerance = (s_zoneDefaults ??= new ColorZone()).tolerance;
            }
            return ZoneAutoTuner.PreviewOverwrittenSettings(probe);
        }

        /// <summary>
        /// 1 つのゾーンの自動調整(<see cref="Start"/> で始める)。メインスレッドで <see cref="Advance"/> を呼んで進め、
        /// <see cref="Status.Done"/> になったら <see cref="ApplyTo"/> で当てる(外す範囲があれば同じ Undo の 1 回分で
        /// <see cref="LeakRegionFor"/> をゾーンの「塗らない」へ書き、最後に <see cref="ReleaseLeakExclusion"/>)。
        /// AI の提案の受け取り先の持ち主はこれ(<see cref="AiSuggestBroker"/>)。
        /// <para>
        /// 状態: 証拠待ち(<see cref="Status.WaitingForEvidence"/>) → 解析(<see cref="Status.Analyzing"/>。背景。証拠を使って
        /// 導けたら、続けて選択を回して漏れの塊を探す) → 塊があれば漏れの確かめ(<see cref="Status.CheckingLeaks"/>。塊ごとに
        /// 自動の提案を 1 件ずつ頼む → 背景で測る → 要る塊だけ細かい提案を頼む → 背景で判定する) → <see cref="Status.Done"/>。
        /// 取り消し(<see cref="Cancel"/>)はどの段でも全体を止める(<see cref="Status.Failed"/>)。
        /// </para>
        /// <para>
        /// 持ち物(ヒープの外): 原本・証拠・マスクのスナップショット・選択・提案は、終わる(Done・Failed)まで Run が参照を 1 つ持ち、
        /// 背景の段はそれぞれ自分の参照を足して読み、段の後始末で返す(<see cref="NativeBuffer"/> の決まり)。段の作業配列は
        /// Core がプールから借りて返す。段の結果(選択・外す範囲)は段が作り、受け取らずにやめたときは段の後に返す
        /// (<see cref="Fail"/>)。終わった後に残るのは外す範囲(<see cref="LeakExclusion"/>)だけで、呼び出し側が返す。
        /// </para>
        /// </summary>
        internal sealed unsafe class Run {
            private readonly string _sourceKey;
            internal bool _retryAiError = true;
            // 段取り(分岐と順序・持ち物。Core の AutoTuneFlow)。ここは待つ・背景へ投げる・取り消す・期限・進み具合の宿主。
            private readonly AutoTuneFlow _flow;
            private readonly PreviewJobProgress _progress = new PreviewJobProgress();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private double _deadline;
            private bool _requested;
            // 解析(と、続けて回す漏れの塊の取り出し)。例外も結果として返す(取り消した解析の例外を誰も見ないまま残さない)。
            private Task<AutoTuneWorkResult> _analysis;
            // 漏れの確かめの背景の段(自動の提案を測る・判定する)。走っていなければ null。
            private Task<AutoTuneWorkResult> _step;
            // 漏れの確かめで提案を待っているか、その要求の番号(古い知らせを見分ける)、今の要求の期限(0 = まだ頼んでいない)。
            private bool _awaiting;
            private int _ticket;
            private double _leakDeadline;

            internal Run(ColorZone zone, NativePixels pixels, string sourceKey,
                NativeBools excluded, int maskW, int maskH, in RecolorSettings settings, MaskSnapshot masks) {
                // 原本の参照はフローが足して持つ(終わったら返す)。
                _flow = new AutoTuneFlow(zone, pixels, excluded, maskW, maskH, in settings, masks);
                _sourceKey = sourceKey;
                TextureWidth = pixels.Width;
                TextureHeight = pixels.Height;
            }

            /// <summary>調整するゾーンの id(当てるときはこれで引き直す。待っている間に消えたり並びが変わったりする)。</summary>
            public string ZoneId => _flow.Zone.id;
            public Status Status { get; private set; }
            public Failure Failure { get; private set; }
            /// <summary>AI のエラーの文言(<see cref="Failure.AiError"/>)。</summary>
            public string FailureDetail { get; private set; }
            /// <summary>解析の例外(<see cref="Failure.AnalysisFailed"/>)。</summary>
            public Exception Error { get; private set; }
            /// <summary>証拠を待っていて、AI がまだ受け付けていない(モデルの読み込み・埋め込みの計算・ほかの提案の処理)。</summary>
            public bool EvidencePreparing => Status == Status.WaitingForEvidence && !_requested;
            /// <summary>進み具合 0..1(待ちの間は AI の準備の進み、解析中は解析の各ステップ、確かめの間は頼んだ提案の数)。</summary>
            public float Progress => _progress.Value;
            /// <summary>AI に提案を求めたか(求めたなら、窓の無い自動化は使い終わった後に GPU を手放す)。</summary>
            public bool AskedAi { get; private set; }
            /// <summary>導出の結果(<see cref="Status.Done"/> のとき)。</summary>
            public ZoneAutoTuner.TuneResult Result => _flow.Result;
            /// <summary>
            /// 証拠なしで解析した理由(<see cref="ReasonNoSamplePosition"/> / <see cref="ReasonEvidenceFlood"/>)。
            /// 証拠ありで解析した・まだ解析していないなら null。
            /// </summary>
            public string ConventionalReason => _flow.ConventionalReason;
            /// <summary>解析に証拠を渡したか。</summary>
            public bool HadEvidence => _flow.HadEvidence;
            public int TextureWidth { get; }
            public int TextureHeight { get; }

            /// <summary>漏れの確かめで塊を探したか(証拠を使って導けて、寸法が合ったとき)。</summary>
            public bool LeakChecked => _flow.LeakChecked;
            /// <summary>確かめた塊の数(探さなかったら 0)。</summary>
            public int LeakBlobCount => _flow.LeakBlobCount;
            /// <summary>外すと決めた塊の数(<see cref="Status.Done"/> のとき)。</summary>
            public int LeakExcludedParts => _flow.LeakExcludedParts;
            /// <summary>外す範囲の画素数(作業解像度。塊どうしの重なりは 1 回)。</summary>
            public int LeakExcludedPixels => _flow.LeakExcludedPixels;
            /// <summary>漏れの確かめを途中で飛ばした理由(<see cref="LeakSkipTimeout"/> など)。飛ばしていなければ null。</summary>
            public string LeakSkipReason => _flow.LeakSkipReason;
            /// <summary>
            /// 外す範囲(作業解像度 <see cref="TextureWidth"/> × <see cref="TextureHeight"/>。下原点。true = 塗らない)。無ければ null。
            /// 持ち主はこの Run で、<see cref="ReleaseLeakExclusion"/> で返す。当てるときは <see cref="LeakRegionFor"/> を使う。
            /// </summary>
            internal NativeBools LeakExclusion { get; private set; }
            /// <summary>外す範囲があるか。</summary>
            public bool HasLeakExclusion => LeakExclusion != null;

            /// <summary>
            /// 証拠が実際に導出に使われたか(汚染セグメント等で従来へ戻った場合は導出診断が "fallback" で始まる)。
            /// </summary>
            public bool UsedEvidence => _flow.UsedEvidence;

            /// <summary>
            /// どう調整したか(自動化の zones[].autoTune。英語の決まった文字列。作るのは Core の <see cref="AutoTuneFlow.Method"/>
            /// 1 か所で、ハーネスも同じものを出す)。
            /// </summary>
            public string Method => _flow.Method;

            /// <summary>
            /// 漏れの確かめの診断(由来に載せる)。確かめていなければ空。
            /// 例: "leak=blobs 3 excluded 1 (12345px)"、"leak=skipped(timeout)"。
            /// </summary>
            public string LeakDiag => _flow.LeakDiag;

            /// <summary>
            /// 由来(再現データの meta.json 用): 証拠の有無・正規化の有無・導出診断・漏れの確かめ。書き出された失敗ケースが
            /// 「証拠経路を通ったのか、AI 未ウォームで従来導出だったのか」を後から判別できないと、どちらの経路を直せばよいか
            /// 分からない(2026-08-29: スニーカー明部クリックの再現データで証拠なし導出と byte 一致したが、証拠ありでも
            /// 同じ結果になる位置だったため区別できなかった)。
            /// </summary>
            public string Provenance => _flow.Provenance;

            internal void Begin() {
                if (_flow.Start() == AutoTuneStep.NeedWork) {
                    StartAnalysis();   // スポイト位置が無い: 証拠なしで解析する
                    return;
                }
                // モデルのファイルが無い・読めないときは、最初の要求(TryEnsureModels)が Unavailable を返す。
                if (!MaskSuggestBridge.Available) {
                    Fail(Failure.AiUnavailable);
                    return;
                }
                if (_sourceKey == null) {
                    Fail(Failure.SourceNotShared);
                    return;
                }
                Status = Status.WaitingForEvidence;
                AskedAi = true;
                _deadline = EditorApplication.timeSinceStartup + EvidenceWait;
                _progress.Report(0.02f);
                // 前の推論のエラーが残っていても、押し直したら再試行する(残したままだと、右クリックで AI を選び直すか
                // テクスチャを切り替えるまで、自動調整が毎回すぐ中止される)。
                if (_retryAiError) {
                    AiSuggestBroker.RetryAfterError();
                }
                RequestEvidence();
            }

            /// <summary>進められるところまで進める(メインスレッド)。</summary>
            public void Advance() {
                switch (Status) {
                    case Status.WaitingForEvidence:
                        if (!_requested) {
                            RequestEvidence();
                        }
                        // 期限切れ: 中止して案内する(後から届いた提案は捨てる)。
                        if (Status == Status.WaitingForEvidence && EditorApplication.timeSinceStartup >= _deadline) {
                            Fail(Failure.Timeout);
                        }
                        break;
                    case Status.Analyzing:
                        if (_analysis.IsCompleted) {
                            FinishAnalysis();
                        }
                        break;
                    case Status.CheckingLeaks:
                        AdvanceLeaks();
                        break;
                }
            }

            /// <summary>止める(待ちも解析も確かめも)。背景の計算は次のステップの間で抜ける。</summary>
            public void Cancel() {
                if (Status == Status.Done || Status == Status.Failed) {
                    return;
                }
                _cts.Cancel();
                Fail(Failure.Cancelled);
            }

            /// <summary>
            /// 結果をゾーンへ当てる(<see cref="Status.Done"/> のとき)。導出結果 → ゾーンの写像は
            /// <see cref="ZoneAutoTuner.TuneResult.ApplyTo"/> が単一の正(headless ハーネスの --autotune と共有。
            /// ここに項目を並べ直さない)。外す範囲はマスクなので、ここでは当てない(<see cref="LeakRegionFor"/>)。
            /// </summary>
            public void ApplyTo(ColorZone zone) {
                if (Status != Status.Done) {
                    throw new InvalidOperationException("auto-tune has not finished");
                }
                Result.ApplyTo(zone);
            }

            /// <summary>
            /// 外す範囲を、マスクの寸法(<paramref name="masks"/> の Width × Height)にして返す(呼び出し側が Release する)。
            /// 寸法が違えば、右クリックの提案と同じ写し方(<see cref="ProposalMaskRegion.ToMaskSize"/>)で写す。そのゾーンの
            /// 「含める」が立っている画素は外す(利用者が塗ると決めた所を、自動調整が塗らない側へ戻さない。
            /// <see cref="AutoTuneFlow.DropIncluded"/>)。外す範囲が無い・残らない・マスクの寸法が決まっていなければ null。
            /// メインスレッドで呼ぶ。
            /// </summary>
            internal NativeBools LeakRegionFor(MaskWorkspace masks, Texture2D texture) {
                var src = LeakExclusion;
                if (src == null || masks == null || masks.Width <= 0 || masks.Height <= 0) {
                    return null;
                }
                var region = ProposalMaskRegion.ToMaskSize(src, masks.Width, masks.Height, texture, out bool owned, out _);
                if (!owned) {
                    region = region.Clone();   // 自分の範囲は書き換えない
                }
                if (!AutoTuneFlow.DropIncluded(region, masks.Layer(true, ZoneId))) {
                    region.Release();
                    return null;
                }
                return region;
            }

            /// <summary>外す範囲を返す(当て終えた・当てずにやめたら呼ぶ。2 回目からは何もしない)。</summary>
            public void ReleaseLeakExclusion() {
                var e = LeakExclusion;
                LeakExclusion = null;
                e?.Release();
            }

            /// <summary>再現データ用の入力(<see cref="Status.Done"/> のとき)。</summary>
            public Input ToInput() => new Input {
                sample = _flow.Zone.sampleColor,
                target = _flow.Zone.targetColor,
                evidence = _flow.EvidencePacked,
                evW = _flow.EvidenceWidth,
                evH = _flow.EvidenceHeight,
                texW = TextureWidth,
                texH = TextureHeight,
                maskUsed = _flow.MaskUsed,
                applied = Result,
                leakParts = LeakExcludedParts,
                leakPixels = LeakExcludedPixels,
            };

            // 証拠の要求を(再)試行する。準備中(Busy)なら次の Advance で求め直す。
            private void RequestEvidence() {
                var ask = _flow.Ask;
                var r = AiSuggestBroker.RequestWhenIdle(this, _sourceKey, _flow.Pixels,
                    ask.U, ask.V, OnEvidence, GranularityOf(ask), MaskSuggestUse.Evidence);
                switch (r) {
                    case AiSuggestBroker.Request.Started:
                        _requested = true;
                        break;
                    case AiSuggestBroker.Request.Busy: {
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

            // フローの頼む提案の粒度(用途はどれも自動調整の証拠と同じ使い方)。
            private static MaskSuggestGranularity GranularityOf(in AutoTuneAsk ask)
                => ask.Fine ? MaskSuggestGranularity.Fine : MaskSuggestGranularity.Auto;

            // 届いた提案をフローへ渡す形に(範囲の参照は足さない。フローが要るなら足して持つ)。
            private static AutoTuneProposal ToFlow(MaskSuggestProposal p)
                => p == null ? AutoTuneProposal.None : new AutoTuneProposal(p.mask, p.width, p.height, p.floodWarning);

            private void FailAi() {
                var svc = MaskSuggestBridge.Service;
                if (svc != null && svc.Phase == MaskSuggestPhase.Error && !string.IsNullOrEmpty(svc.ErrorMessage)) {
                    Fail(Failure.AiError, svc.ErrorMessage);
                } else {
                    Fail(Failure.AiUnavailable);
                }
            }

            // 提案の到着(null = 取消・破棄)。null なら中止(従来導出へは落とさない)。
            // 受け取りで投げたら(写しの確保の失敗など)、解析の失敗として終える。broker はログに出すだけなので、
            // 以前は証拠を待ったまま期限(180 秒)まで止まっていた。
            private void OnEvidence(MaskSuggestProposal proposal) {
                if (Status != Status.WaitingForEvidence) {
                    return;
                }
                try {
                    ReceiveEvidence(proposal);
                } catch (Exception e) {
                    Fail(Failure.AnalysisFailed, e.Message, e);
                }
            }

            private void ReceiveEvidence(MaskSuggestProposal proposal) {
                if (ThrowOnEvidenceForTests) {
                    throw new InvalidOperationException("thrown for tests");
                }
                if (proposal == null || proposal.mask == null || proposal.width <= 0 || proposal.height <= 0) {
                    // AI のエラーで捨てられたのなら、その理由を出す。
                    var svc = MaskSuggestBridge.Service;
                    if (svc != null && svc.Phase == MaskSuggestPhase.Error) {
                        FailAi();
                        return;
                    }
                }
                // 来なければ失敗(NoEvidence)。背景まで広がった提案なら証拠なし、それ以外は証拠つきで解析する(AutoTuneFlow)。
                // 提案の範囲は受け取りの間だけ使える(AiSuggestBroker が後で返す)。フローが参照を足して持つ。
                if (_flow.Offer(ToFlow(proposal)) == AutoTuneStep.Failed) {
                    Fail(Failure.NoEvidence);
                    return;
                }
                StartAnalysis();
            }

            // 解析を背景で始める(証拠の有無はフローが決めた)。
            private void StartAnalysis() {
                Status = Status.Analyzing;
                _progress.Reset();
                // 証拠つきなら、解析の後に漏れの確かめが続きうる。解析の進み具合を 0..AnalysisShare に収める。
                float span = _flow.HadEvidence ? AnalysisShare : 1f;
                _progress.Report(0.05f * span);

                // 背景の計算が読むものは、計算が自分の参照を足して持つ(計算の後始末で返す)。除外マスクは解析だけが使うので渡しきる。
                var work = _flow.TakeWork(KeepEvidenceForRepro);
                var ct = _cts.Token;
                var progress = _progress;
                // 予約から抜けるまでを計算中として数える(閉じたときの解放が、走り終わるのを待って作業配列を返すため)。
                NativeMemoryLifecycle.WorkStarted();
                _analysis = s_lastAnalysis.ContinueWith(_ => {
                    try {
                        // 解析の各ステップから進捗を受け取る。渡さないと 0.10 → 1.00 の 2 点しか報告されない。
                        return work.Run(ct,
                            started: () => progress.Report(0.10f * span),
                            report: v => progress.Report(v * span),
                            probed: () => progress.Report(LeakProbedAt));
                    } finally {
                        work.Dispose();
                        NativeMemoryLifecycle.WorkEnded();
                    }
                }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                s_lastAnalysis = _analysis;
            }

            private void FinishAnalysis() {
                var outcome = _analysis.Result;
                _analysis = null;
                var step = _flow.Complete(outcome, _cts.IsCancellationRequested);
                if (step == AutoTuneStep.Failed) {
                    if (_flow.Failure == AutoTuneFlowFailure.Cancelled) {
                        Fail(Failure.Cancelled);
                    } else {
                        Fail(Failure.AnalysisFailed, _flow.Error.Message, _flow.Error);
                    }
                    return;
                }
                if (step == AutoTuneStep.Done) {
                    Complete();
                    return;
                }
                Status = Status.CheckingLeaks;
                _progress.Report(LeakProbedAt);
                AdvanceLeaks();
            }

            // ─────────────── 漏れの確かめ(メインスレッド) ───────────────

            // 確かめを進める: 背景の段が終わっていれば受け取り、提案を待っていれば期限を見て、次に頼む塊があれば頼み、
            // 頼み終えたら背景の段(自動の提案を測る・判定する)を始める。次に何をするかはフローが決める。
            private void AdvanceLeaks() {
                if (_step != null) {
                    if (!_step.IsCompleted) {
                        return;
                    }
                    var result = _step.Result;
                    _step = null;
                    bool wasFine = _flow.LeakFinePhase;
                    var s = _flow.Complete(result, _cts.IsCancellationRequested);
                    if (s == AutoTuneStep.Failed) {
                        Fail(Failure.Cancelled);
                        return;
                    }
                    if (s == AutoTuneStep.Done) {
                        // 判定した(外す範囲があれば持ち主は Run。呼び出し側が ReleaseLeakExclusion で返す)・細かい提案が
                        // 要る塊が無い・確かめの計算の失敗(確かめだけを飛ばした)。
                        LeakExclusion = _flow.TakeLeakExclusion();
                        Complete();
                        return;
                    }
                    if (!wasFine && _flow.LeakFinePhase) {
                        _progress.Report(LeakMeasuredAt);
                    }
                }
                if (_awaiting) {
                    if (EditorApplication.timeSinceStartup >= _leakDeadline) {
                        SkipLeaks(LeakSkipTimeout);
                    }
                    return;
                }
                if (_flow.Current == AutoTuneStep.NeedWork) {
                    StartLeakStep();
                    return;
                }
                RequestLeakProposal(_flow.Ask);
            }

            // 塊の問い合わせ点で AI の提案を 1 件頼む(自動調整の証拠と同じ使い方。粒度は段で決まる。フローの Ask)。
            private void RequestLeakProposal(AutoTuneAsk ask) {
                if (_leakDeadline <= 0d) {
                    _leakDeadline = EditorApplication.timeSinceStartup + EvidenceWait;
                }
                int ticket = ++_ticket;
                _awaiting = true;
                var r = AiSuggestBroker.RequestWhenIdle(this, _sourceKey, _flow.Pixels, ask.U, ask.V,
                    p => OnLeakProposal(ticket, p), GranularityOf(ask), MaskSuggestUse.Evidence);
                if (r == AiSuggestBroker.Request.Started) {
                    return;   // (その場で届いたら、受け取り先が進めてある)
                }
                if (Status != Status.CheckingLeaks || _ticket != ticket) {
                    return;
                }
                _awaiting = false;
                if (r == AiSuggestBroker.Request.Busy) {
                    // ほかの提案の処理中。次の Advance で頼み直す。
                    if (EditorApplication.timeSinceStartup >= _leakDeadline) {
                        SkipLeaks(LeakSkipTimeout);
                    }
                    return;
                }
                SkipLeaks(AiSkipReason());
            }

            // 頼んだ提案の到着(null = 取消・破棄・AI のエラー)。古い要求の知らせ(期限切れの後など)は捨てる。
            private void OnLeakProposal(int ticket, MaskSuggestProposal proposal) {
                if (Status != Status.CheckingLeaks || !_flow.InLeakCheck || !_awaiting || _ticket != ticket) {
                    return;
                }
                _awaiting = false;
                try {
                    if (ThrowOnLeakProposalForTests) {
                        throw new InvalidOperationException("thrown for tests");
                    }
                    if (proposal == null || proposal.mask == null || proposal.width <= 0 || proposal.height <= 0) {
                        var svc = MaskSuggestBridge.Service;
                        if (svc != null && svc.Phase == MaskSuggestPhase.Error) {
                            SkipLeaks(AiSkipReason());
                            return;
                        }
                    }
                    // 来ない(no-proposal)・寸法違い(size-mismatch)なら確かめを飛ばして終える。それ以外は範囲を持つ(フロー)。
                    if (_flow.Offer(ToFlow(proposal)) == AutoTuneStep.Done) {
                        Complete();
                        return;
                    }
                    _leakDeadline = 0d;
                    _progress.Report(_flow.LeakFinePhase
                        ? LeakMeasuredAt + LeakFineSpan * _flow.LeakReceived / Math.Max(1, _flow.LeakFineCount)
                        : LeakProbedAt + LeakCoarseSpan * _flow.LeakReceived / _flow.LeakBlobs);
                } catch (Exception e) {
                    SkipLeaks(LeakSkipError + ": " + e.Message);
                }
            }

            private static string AiSkipReason() {
                var svc = MaskSuggestBridge.Service;
                return svc != null && svc.Phase == MaskSuggestPhase.Error && !string.IsNullOrEmpty(svc.ErrorMessage)
                    ? LeakSkipAi + ": " + svc.ErrorMessage
                    : LeakSkipAi;
            }

            // 背景の段: 自動の提案を測る(細かい提案が要る塊を決める)か、判定して外す範囲を作る(どちらかはフローが決める)。
            private void StartLeakStep() {
                var work = _flow.TakeWork();
                var ct = _cts.Token;
                NativeMemoryLifecycle.WorkStarted();
                _step = s_lastAnalysis.ContinueWith(_ => {
                    try {
                        return work.Run(ct);
                    } finally {
                        work.Dispose();
                        NativeMemoryLifecycle.WorkEnded();
                    }
                }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                s_lastAnalysis = _step;
            }

            // 確かめを飛ばして、解析の結果だけで終える(自動調整は失敗にしない)。待っている提案は取り消す。
            private void SkipLeaks(string reason) {
                if (Status != Status.CheckingLeaks) {
                    return;
                }
                _flow.SkipLeaks(reason);
                Complete();
            }

            // 終える(Done)。終わった後に要らないもの(原本・証拠・マスク・確かめの途中の物)を返す。外す範囲だけは残す。
            private void Complete() {
                Status = Status.Done;
                _progress.Report(1.0f);
                if (_flow.InLeakCheck) {
                    // 待っている提案(あれば)を取り消す。届かない知らせは、確かめていないので素通りする。
                    AiSuggestBroker.Cancel(this);
                }
                ReleaseWork();
            }

            private void Fail(Failure failure, string detail = null, Exception error = null) {
                Status = Status.Failed;
                Failure = failure;
                FailureDetail = detail;
                Error = error;
                // 待っている提案(あれば)を取り消す。届かない知らせ(OnEvidence(null))は、待っていないので素通りする。
                AiSuggestBroker.Cancel(this);
                // 走っている解析は、取り消しで抜けた後に結果(選択)を返す。
                var a = _analysis;
                _analysis = null;
                a?.ContinueWith(t => t.Result.Discard(), CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                ReleaseWork();
                _flow.Dispose();
                ReleaseLeakExclusion();
            }

            // 終わった後に要らないものを返す(Done・Failed)。
            private void ReleaseWork() {
                // 走っている段は、抜けた後に結果(外す範囲)を返す。段が読む物は段が自分の参照を持っている。
                var step = _step;
                _step = null;
                step?.ContinueWith(t => t.Result.Discard(), CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                _flow.ReleaseWork();
            }
        }
    }
}
