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
        internal const string ReasonNoSamplePosition = "no-sample-position";
        /// <summary>証拠なしで解析した理由: 提案が背景まで広がった。</summary>
        internal const string ReasonEvidenceFlood = "evidence-flood";

        /// <summary>漏れの確かめを飛ばした理由: 提案の寸法が作業解像度と違う。</summary>
        internal const string LeakSkipSizeMismatch = "size-mismatch";
        /// <summary>漏れの確かめを飛ばした理由: 提案を待つ期限が切れた。</summary>
        internal const string LeakSkipTimeout = "timeout";
        /// <summary>漏れの確かめを飛ばした理由: 提案が来なかった(取り消し・破棄・空の提案)。</summary>
        internal const string LeakSkipNoProposal = "no-proposal";
        /// <summary>漏れの確かめを飛ばした理由: AI が使えなくなった(後ろに AI のエラーの文言が付くことがある)。</summary>
        internal const string LeakSkipAi = "ai";
        /// <summary>漏れの確かめを飛ばした理由: 確かめの計算が例外で終わった(後ろに例外の文言が付く)。</summary>
        internal const string LeakSkipError = "error";

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
            var run = new Run(zone.Clone(), pixels.AddRef(), sourceKey,
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
        ///   導いた値だけが既定へ戻ったものを含む)なら手調整なし
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
            if (lastInput != null
                && (ValuesIntact(zone, lastInput.applied) || ValuesIntact(zone, IslandCleared(lastInput.applied)))) {
                return true;
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
            private readonly ColorZone _zone;
            private readonly string _sourceKey;
            internal bool _retryAiError = true;
            private NativeBools _excluded;
            private readonly bool _maskUsed;
            private readonly int _mw, _mh;
            private readonly RecolorSettings _settings;
            // 漏れの確かめで選択を回すときのマスク(持ち主。終わったら返す)。null = マスク無し。
            private MaskSnapshot _masks;
            private readonly PreviewJobProgress _progress = new PreviewJobProgress();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            // 持っている原本の参照(背景の段は自分の参照を足す。終わったら返す)。
            private NativePixels _pixels;
            // 証拠(提案の範囲の参照。漏れの確かめが済むまで持つ。終わったら返す)。
            private NativeBools _evidence;
            private double _deadline;
            private bool _requested;
            // 解析(と、続けて回す漏れの塊の取り出し)。例外も結果として返す(取り消した解析の例外を誰も見ないまま残さない)。
            private Task<AnalysisOutcome> _analysis;
            // 解析に渡した証拠を 1bit/画素に詰めたもの(解析と同じ背景で詰める。終わった後にだけ読む)。
            private ulong[] _evidencePacked;
            private int _evW, _evH;
            // 漏れの確かめの途中の状態(CheckingLeaks の間だけ)。
            private LeakCheck _leak;

            internal Run(ColorZone zone, NativePixels pixels, string sourceKey,
                NativeBools excluded, int maskW, int maskH, in RecolorSettings settings, MaskSnapshot masks) {
                _zone = zone;
                _pixels = pixels;
                _sourceKey = sourceKey;
                _excluded = excluded;
                _maskUsed = excluded != null;
                _mw = maskW;
                _mh = maskH;
                _settings = settings;
                _masks = masks;
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
            /// <summary>進み具合 0..1(待ちの間は AI の準備の進み、解析中は解析の各ステップ、確かめの間は頼んだ提案の数)。</summary>
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

            /// <summary>漏れの確かめで塊を探したか(証拠を使って導けて、寸法が合ったとき)。</summary>
            public bool LeakChecked { get; private set; }
            /// <summary>確かめた塊の数(探さなかったら 0)。</summary>
            public int LeakBlobCount { get; private set; }
            /// <summary>外すと決めた塊の数(<see cref="Status.Done"/> のとき)。</summary>
            public int LeakExcludedParts { get; private set; }
            /// <summary>外す範囲の画素数(作業解像度。塊どうしの重なりは 1 回)。</summary>
            public int LeakExcludedPixels { get; private set; }
            /// <summary>漏れの確かめを途中で飛ばした理由(<see cref="LeakSkipTimeout"/> など)。飛ばしていなければ null。</summary>
            public string LeakSkipReason { get; private set; }
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
            public bool UsedEvidence => UsedEvidenceOf(Result);

            private static bool UsedEvidenceOf(in ZoneAutoTuner.TuneResult r)
                => !string.IsNullOrEmpty(r.evidenceDiag) && !r.evidenceDiag.StartsWith("fallback");

            /// <summary>
            /// どう調整したか(自動化の zones[].autoTune。英語の決まった文字列で、ここ 1 か所で作る)。
            /// 証拠を渡しても使えずに従来の導出へ戻ったとき(汚れた提案・素材の画素が足りない)は "evidence" と言わない。
            /// </summary>
            public string Method {
                get {
                    if (HadEvidence) {
                        return UsedEvidence ? "evidence" : "conventional (evidence not usable: " + EvidenceUnusableReason + ")";
                    }
                    return ConventionalReason == ReasonEvidenceFlood
                        ? "conventional (AI region spread to the background)"
                        : "conventional";
                }
            }

            // 証拠を使えなかった理由(導出診断の "fallback <理由> …" の理由。診断が無い = 小さすぎて解析しなかった)。
            private string EvidenceUnusableReason {
                get {
                    const string prefix = "fallback ";
                    string diag = Result.evidenceDiag;
                    if (string.IsNullOrEmpty(diag) || !diag.StartsWith(prefix)) {
                        return "not analysed";
                    }
                    int end = diag.IndexOf(' ', prefix.Length);
                    return end < 0 ? diag.Substring(prefix.Length) : diag.Substring(prefix.Length, end - prefix.Length);
                }
            }

            /// <summary>
            /// 漏れの確かめの診断(由来に載せる)。確かめていなければ空。
            /// 例: "leak=blobs 3 excluded 1 (12345px)"、"leak=skipped(timeout)"。
            /// </summary>
            public string LeakDiag {
                get {
                    if (LeakSkipReason != null) {
                        return $"leak=skipped({LeakSkipReason})";
                    }
                    return LeakChecked ? $"leak=blobs {LeakBlobCount} excluded {LeakExcludedParts} ({LeakExcludedPixels}px)" : "";
                }
            }

            /// <summary>
            /// 由来(再現データの meta.json 用): 証拠の有無・正規化の有無・導出診断・漏れの確かめ。書き出された失敗ケースが
            /// 「証拠経路を通ったのか、AI 未ウォームで従来導出だったのか」を後から判別できないと、どちらの経路を直せばよいか
            /// 分からない(2026-08-29: スニーカー明部クリックの再現データで証拠なし導出と byte 一致したが、証拠ありでも
            /// 同じ結果になる位置だったため区別できなかった)。
            /// </summary>
            public string Provenance =>
                (HadEvidence ? (UsedEvidence ? "evidence" : "evidence-fallback")
                             : "conventional" + (ConventionalReason != null ? "(" + ConventionalReason + ")" : ""))
                + $"; normalized={Result.hasNormalizedSample}"
                + (string.IsNullOrEmpty(Result.evidenceDiag) ? "" : "; " + Result.evidenceDiag)
                + (LeakDiag.Length == 0 ? "" : "; " + LeakDiag);

            internal void Begin() {
                if (!_zone.HasSampleUV) {
                    StartAnalysis(null, 0, 0, ReasonNoSamplePosition);
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
            /// 「含める」が立っている画素は外す(利用者が塗ると決めた所を、自動調整が塗らない側へ戻さない)。
            /// 外す範囲が無い・残らない・マスクの寸法が決まっていなければ null。メインスレッドで呼ぶ。
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
                if (!DropIncluded(region, masks.Layer(true, ZoneId))) {
                    region.Release();
                    return null;
                }
                return region;
            }

            // 範囲から、含めるが立っている画素を外す。外した後に 1 画素でも残れば true。語(64 画素)の区間に分けて並列に回す
            // (画素ごとに独立なので、どう分けても中身は同じ)。
            private static bool DropIncluded(NativeBools region, NativeBitMask include) {
                bool* r = region.Ptr;
                int len = region.Length;
                ulong* inc = include != null && include.Length == len ? include.Bits : null;
                int words = (len + 63) >> 6;
                const int WordsPerChunk = 1 << 12;
                int chunks = (words + WordsPerChunk - 1) / WordsPerChunk;
                int any = 0;
                Parallel.For(0, chunks, CoreParallelism.Options(), k => {
                    int w0 = k * WordsPerChunk, w1 = Math.Min(words, w0 + WordsPerChunk);
                    bool found = false;
                    for (int w = w0; w < w1; w++) {
                        ulong v = inc != null ? inc[w] : 0UL;
                        int i0 = w << 6, n = Math.Min(64, len - i0);
                        for (int b = 0; b < n; b++) {
                            if (!r[i0 + b]) {
                                continue;
                            }
                            if (((v >> b) & 1UL) != 0UL) {
                                r[i0 + b] = false;
                            } else {
                                found = true;
                            }
                        }
                    }
                    if (found) {
                        Volatile.Write(ref any, 1);
                    }
                });
                return any != 0;
            }

            /// <summary>外す範囲を返す(当て終えた・当てずにやめたら呼ぶ。2 回目からは何もしない)。</summary>
            public void ReleaseLeakExclusion() {
                var e = LeakExclusion;
                LeakExclusion = null;
                e?.Release();
            }

            /// <summary>再現データ用の入力(<see cref="Status.Done"/> のとき)。</summary>
            public Input ToInput() => new Input {
                sample = _zone.sampleColor,
                target = _zone.targetColor,
                evidence = _evidencePacked,
                evW = _evW,
                evH = _evH,
                texW = TextureWidth,
                texH = TextureHeight,
                maskUsed = _maskUsed,
                applied = Result,
                leakParts = LeakExcludedParts,
                leakPixels = LeakExcludedPixels,
            };

            // 証拠の要求を(再)試行する。準備中(Busy)なら次の Advance で求め直す。
            private void RequestEvidence() {
                var r = AiSuggestBroker.RequestWhenIdle(this, _sourceKey, _pixels,
                    _zone.sampleUV.x, _zone.sampleUV.y, OnEvidence, use: MaskSuggestUse.Evidence);
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
                    } else {
                        Fail(Failure.NoEvidence);
                    }
                    return;
                }
                // 背景まで広がった提案は素材の証拠にならない。この場合だけは証拠なしで解析する。
                if (proposal.floodWarning) {
                    StartAnalysis(null, 0, 0, ReasonEvidenceFlood);
                }
                // 提案の範囲は受け取りの間だけ使える(AiSuggestBroker が後で返す)。解析と漏れの確かめが終わるまで持つので参照を
                // 足す(終わったら返す)。写さなくてよいのは、配り終えた範囲へは誰も書かず(サービスは渡したら手放し、
                // 受け取り先は提案ごとに 1 つ)、解析も確かめも証拠を読むだけ(収縮は自分の作業配列へ写してから)だから。以前は写していた
                // (メインスレッドで 4K 16MB・8K 67MB を写し、解析が終わるまで 1 枚余分に持っていた)。
                else {
                    StartAnalysis(proposal.mask.AddRef(), proposal.width, proposal.height, null);
                }
            }

            // evidence: 証拠マスク（true=このゾーンの素材そのもの。pixels と同じ下原点並び。持ち主は Run になる）。null なら従来導出。
            private void StartAnalysis(NativeBools evidence, int evW, int evH, string conventionalReason) {
                Status = Status.Analyzing;
                ConventionalReason = conventionalReason;
                HadEvidence = evidence != null;
                _evidence = evidence;
                _evW = evW;
                _evH = evH;
                _progress.Reset();
                // 証拠つきなら、解析の後に漏れの確かめが続きうる。解析の進み具合を 0..AnalysisShare に収める。
                float span = evidence != null ? AnalysisShare : 1f;
                _progress.Report(0.05f * span);

                // 背景の段が読むものは、自分の参照を足して渡す(段の後始末で返す)。除外マスクは解析だけが使うので渡しきる。
                var px = _pixels.AddRef();
                var ev = evidence?.AddRef();
                var masks = evidence != null ? _masks?.AddRef() : null;
                var zone = _zone;
                var excluded = _excluded;
                _excluded = null;
                int mw = _mw, mh = _mh;
                var settings = _settings;
                var ct = _cts.Token;
                var progress = _progress;
                // 予約から抜けるまでを計算中として数える(閉じたときの解放が、走り終わるのを待って作業配列を返すため)。
                NativeMemoryLifecycle.WorkStarted();
                _analysis = s_lastAnalysis.ContinueWith(_ => {
                    var outcome = new AnalysisOutcome();
                    try {
                        ct.ThrowIfCancellationRequested();
                        progress.Report(0.10f * span);
                        // ct を Analyze へ渡す。取り消されたら各解析ステップ間で停止し、ゾンビ実行(CPU 2 倍)を防ぐ。
                        // 解析の各ステップから進捗を受け取る。渡さないと 0.10 → 1.00 の 2 点しか報告されない。
                        bool* excludedPtr = excluded != null ? excluded.Ptr : null;
                        Action<float> report = v => progress.Report(v * span);
                        outcome.Result = ev != null
                            ? ZoneAutoTuner.AnalyzeWithEvidenceCore(px.Ptr, px.Width, px.Height, zone,
                                ev.Ptr, evW, evH, excludedPtr, mw, mh, ct, report)
                            : ZoneAutoTuner.AnalyzeCore(px.Ptr, px.Width, px.Height, zone, excludedPtr, mw, mh, ct, report);
                        // 適用後も持つ証拠(再現データ用)は 1bit/画素に詰めたもの。再現データを書き出せる環境でだけ控える。
                        _evidencePacked = KeepEvidenceForRepro ? MaskSnapshot.Pack(ev) : null;   // null は null
                        if (ev != null) {
                            // 確かめの失敗は自動調整を失敗にしない(取り消しだけは全体を止める)。
                            try {
                                ProbeLeaks(px, ev, evW, evH, zone, in settings, masks, ct, outcome);
                            } catch (Exception e) when (!(e is OperationCanceledException)) {
                                outcome.Discard();
                                outcome.Blobs = null;
                                outcome.LeakSkip = LeakSkipError + ": " + e.Message;
                            }
                            progress.Report(LeakProbedAt);
                        } else {
                            progress.Report(1.0f);
                        }
                        return outcome;
                    } catch (Exception e) {
                        outcome.Discard();
                        outcome.Error = e;
                        return outcome;
                    } finally {
                        ev?.Release();
                        masks?.Release();
                        excluded?.Release();
                        px.Release();
                        NativeMemoryLifecycle.WorkEnded();
                    }
                }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                s_lastAnalysis = _analysis;
            }

            // 漏れの確かめの 1 段目(背景。解析と同じ段で続けて回す): 導いた値を当てたゾーンの写しで、製品が塗るときと同じ選択を
            // 回し(PixelProcessor.SimulateSelection)、証拠の外に塗られた塊を探す(ZoneAutoTuner.FindLeakBlobs)。
            // 確かめるのは証拠を使って導けたときだけ(従来の導出へ落ちたら、証拠は導出の根拠でない)。
            private static void ProbeLeaks(NativePixels px, NativeBools evidence, int evW, int evH, ColorZone zone,
                in RecolorSettings settings, MaskSnapshot masks, CancellationToken ct, AnalysisOutcome outcome) {
                if (!UsedEvidenceOf(outcome.Result)) {
                    return;
                }
                int w = px.Width, h = px.Height;
                // 提案は作業解像度の原本から作る(AI と同じ元のキー)ので、ふつうは同じ寸法。違えば確かめない
                // (塊の点と提案の画素の対応が取れない)。
                if (evW != w || evH != h || evidence.Width != w || evidence.Height != h) {
                    outcome.LeakSkip = LeakSkipSizeMismatch;
                    return;
                }
                var tuned = zone.Clone();
                outcome.Result.ApplyTo(tuned);
                var sel = NativeBools.AllocateUninitialized(w, h);
                try {
                    PixelProcessor.SimulateSelection(px.Ptr, w, h, tuned, in settings, masks, sel.Ptr, null, ct);
                    outcome.Blobs = ZoneAutoTuner.FindLeakBlobs(sel.Ptr, evidence.Ptr, w, h, ct);
                    if (outcome.Blobs.Length > 0) {
                        outcome.Selection = sel;   // 確かめが済むまで Run が持つ
                        sel = null;
                    }
                } finally {
                    sel?.Release();
                }
            }

            private void FinishAnalysis() {
                var outcome = _analysis.Result;
                _analysis = null;
                if (outcome.Error != null) {
                    outcome.Discard();
                    if (outcome.Error is OperationCanceledException) {
                        Fail(Failure.Cancelled);
                    } else {
                        Fail(Failure.AnalysisFailed, outcome.Error.Message, outcome.Error);
                    }
                    return;
                }
                Result = outcome.Result;
                LeakSkipReason = outcome.LeakSkip;
                if (outcome.Blobs != null) {
                    LeakChecked = true;
                    LeakBlobCount = outcome.Blobs.Length;
                }
                if (outcome.Selection == null) {
                    Complete();
                    return;
                }
                _leak = new LeakCheck(outcome.Blobs, outcome.Selection);
                Status = Status.CheckingLeaks;
                _progress.Report(LeakProbedAt);
                AdvanceLeaks();
            }

            // ─────────────── 漏れの確かめ(メインスレッド) ───────────────

            // 確かめを進める: 背景の段が終わっていれば受け取り、提案を待っていれば期限を見て、次に頼む塊があれば頼み、
            // 頼み終えたら背景の段(自動の提案を測る・判定する)を始める。
            private void AdvanceLeaks() {
                var lk = _leak;
                if (lk.Step != null) {
                    if (!lk.Step.IsCompleted) {
                        return;
                    }
                    var step = lk.Step.Result;
                    lk.Step = null;
                    if (step.Error != null) {
                        step.Discard();
                        if (step.Error is OperationCanceledException && _cts.IsCancellationRequested) {
                            Fail(Failure.Cancelled);
                        } else {
                            SkipLeaks(LeakSkipError + ": " + step.Error.Message);
                        }
                        return;
                    }
                    if (!step.Judged) {
                        // 自動の提案を測り終えた: 細かい提案が要る塊(外す側に入った塊)だけ、同じ点で頼み直す。
                        lk.NeedsFine = step.NeedsFine;
                        if (Array.IndexOf(lk.NeedsFine, true) < 0) {
                            Complete();   // 外す側に入った塊が無い(細かい提案が無いと外さない)
                            return;
                        }
                        lk.FinePhase = true;
                        lk.Next = 0;
                        _progress.Report(LeakMeasuredAt);
                    } else {
                        LeakExcludedParts = step.ExcludedParts;
                        LeakExcludedPixels = step.ExcludedPixels;
                        LeakExclusion = step.Exclusion;   // 持ち主は Run(呼び出し側が ReleaseLeakExclusion で返す)
                        step.Exclusion = null;
                        Complete();
                        return;
                    }
                }
                if (lk.Awaiting >= 0) {
                    if (EditorApplication.timeSinceStartup >= lk.Deadline) {
                        SkipLeaks(LeakSkipTimeout);
                    }
                    return;
                }
                int k = lk.NextToAsk();
                if (k < 0) {
                    StartLeakStep(judge: lk.FinePhase);
                    return;
                }
                RequestLeakProposal(lk, k);
            }

            // 塊 k の問い合わせ点で AI の提案を 1 件頼む(自動調整の証拠と同じ使い方。粒度は段で決まる)。
            private void RequestLeakProposal(LeakCheck lk, int k) {
                if (lk.Deadline <= 0d) {
                    lk.Deadline = EditorApplication.timeSinceStartup + EvidenceWait;
                }
                var b = lk.Blobs[k];
                // 塊の点は下原点の画素の位置。UV は画素の中心(ファイルの冒頭の説明。Core の ZoneAutoTuner.Leak.cs)。
                float u = (b.X + 0.5f) / TextureWidth, v = (b.Y + 0.5f) / TextureHeight;
                int ticket = ++lk.Ticket;
                lk.Awaiting = k;
                var gran = lk.FinePhase ? MaskSuggestGranularity.Fine : MaskSuggestGranularity.Auto;
                var r = AiSuggestBroker.RequestWhenIdle(this, _sourceKey, _pixels, u, v,
                    p => OnLeakProposal(ticket, p), gran, MaskSuggestUse.Evidence);
                if (r == AiSuggestBroker.Request.Started) {
                    return;   // (その場で届いたら、受け取り先が進めてある)
                }
                if (Status != Status.CheckingLeaks || _leak != lk || lk.Ticket != ticket) {
                    return;
                }
                lk.Awaiting = -1;
                if (r == AiSuggestBroker.Request.Busy) {
                    // ほかの提案の処理中。次の Advance で頼み直す。
                    if (EditorApplication.timeSinceStartup >= lk.Deadline) {
                        SkipLeaks(LeakSkipTimeout);
                    }
                    return;
                }
                SkipLeaks(AiSkipReason());
            }

            // 頼んだ提案の到着(null = 取消・破棄・AI のエラー)。古い要求の知らせ(期限切れの後など)は捨てる。
            private void OnLeakProposal(int ticket, MaskSuggestProposal proposal) {
                var lk = _leak;
                if (Status != Status.CheckingLeaks || lk == null || lk.Awaiting < 0 || lk.Ticket != ticket) {
                    return;
                }
                int k = lk.Awaiting;
                lk.Awaiting = -1;
                try {
                    if (ThrowOnLeakProposalForTests) {
                        throw new InvalidOperationException("thrown for tests");
                    }
                    if (proposal == null || proposal.mask == null || proposal.width <= 0 || proposal.height <= 0) {
                        var svc = MaskSuggestBridge.Service;
                        SkipLeaks(svc != null && svc.Phase == MaskSuggestPhase.Error ? AiSkipReason() : LeakSkipNoProposal);
                        return;
                    }
                    if (proposal.width != TextureWidth || proposal.height != TextureHeight
                        || proposal.mask.Width != TextureWidth || proposal.mask.Height != TextureHeight) {
                        SkipLeaks(LeakSkipSizeMismatch);
                        return;
                    }
                    lk.Store(k, proposal.mask.AddRef(), proposal.floodWarning);
                    lk.Deadline = 0d;
                    _progress.Report(lk.FinePhase
                        ? LeakMeasuredAt + LeakFineSpan * lk.Received / Math.Max(1, lk.FineCount)
                        : LeakProbedAt + LeakCoarseSpan * lk.Received / lk.Blobs.Length);
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

            // 背景の段: 自動の提案を測る(judge = false。細かい提案が要る塊を決める)か、判定して外す範囲を作る(judge = true)。
            private void StartLeakStep(bool judge) {
                var lk = _leak;
                int w = TextureWidth, h = TextureHeight;
                var px = _pixels.AddRef();
                var ev = _evidence.AddRef();
                var sel = lk.Selection.AddRef();
                var coarse = AddRefAll(lk.Coarse);
                var fine = judge ? AddRefAll(lk.Fine) : null;
                var coarseFlood = (bool[])lk.CoarseFlood.Clone();
                var fineFlood = (bool[])lk.FineFlood.Clone();
                var ct = _cts.Token;
                NativeMemoryLifecycle.WorkStarted();
                lk.Step = s_lastAnalysis.ContinueWith(_ => {
                    var o = new StepOutcome { Judged = judge };
                    try {
                        ct.ThrowIfCancellationRequested();
                        var props = Proposals(coarse, coarseFlood);
                        if (!judge) {
                            o.NeedsFine = new bool[props.Length];
                            for (int k = 0; k < props.Length; k++) {
                                o.NeedsFine[k] = ZoneAutoTuner.MeasureLeakProposal(px.Ptr, sel.Ptr, ev.Ptr, w, h, props[k], ct)
                                    .NeedsFine;
                            }
                        } else {
                            o.Exclusion = NativeBools.AllocateUninitialized(w, h);
                            var verdicts = ZoneAutoTuner.JudgeLeaks(px.Ptr, sel.Ptr, ev.Ptr, w, h, props,
                                Proposals(fine, fineFlood), o.Exclusion.Ptr, out int excludedPixels, ct);
                            foreach (var v in verdicts) {
                                if (v.Decision == ZoneAutoTuner.LeakDecision.Exclude) {
                                    o.ExcludedParts++;
                                }
                            }
                            o.ExcludedPixels = excludedPixels;
                            if (excludedPixels == 0) {
                                o.Discard();
                            }
                        }
                        return o;
                    } catch (Exception e) {
                        o.Discard();
                        o.Error = e;
                        return o;
                    } finally {
                        ReleaseAll(fine);
                        ReleaseAll(coarse);
                        sel.Release();
                        ev.Release();
                        px.Release();
                        NativeMemoryLifecycle.WorkEnded();
                    }
                }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                s_lastAnalysis = lk.Step;
            }

            private static NativeBools[] AddRefAll(NativeBools[] masks) {
                var copy = new NativeBools[masks.Length];
                for (int k = 0; k < masks.Length; k++) {
                    copy[k] = masks[k]?.AddRef();
                }
                return copy;
            }

            private static void ReleaseAll(NativeBools[] masks) {
                if (masks == null) {
                    return;
                }
                for (int k = 0; k < masks.Length; k++) {
                    masks[k]?.Release();
                    masks[k] = null;
                }
            }

            private static ZoneAutoTuner.LeakProposal[] Proposals(NativeBools[] masks, bool[] flood) {
                var props = new ZoneAutoTuner.LeakProposal[masks.Length];
                for (int k = 0; k < masks.Length; k++) {
                    props[k] = new ZoneAutoTuner.LeakProposal { Mask = masks[k] != null ? masks[k].Ptr : null, Flood = flood[k] };
                }
                return props;
            }

            // 確かめを飛ばして、解析の結果だけで終える(自動調整は失敗にしない)。待っている提案は取り消す。
            private void SkipLeaks(string reason) {
                if (Status != Status.CheckingLeaks) {
                    return;
                }
                LeakSkipReason = reason;
                Complete();
            }

            // 終える(Done)。終わった後に要らないもの(原本・証拠・マスク・確かめの途中の物)を返す。外す範囲だけは残す。
            private void Complete() {
                Status = Status.Done;
                _progress.Report(1.0f);
                if (_leak != null) {
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
                ReleaseLeakExclusion();
            }

            // 終わった後に要らないものを返す(Done・Failed)。
            private void ReleaseWork() {
                var px = _pixels;
                _pixels = null;
                px?.Release();
                var ev = _evidence;
                _evidence = null;
                ev?.Release();
                var masks = _masks;
                _masks = null;
                masks?.Release();
                _excluded?.Release();
                _excluded = null;
                var lk = _leak;
                _leak = null;
                lk?.Release();
            }
        }

        // 解析の段の結果(背景 → メインスレッド)。選択の持ち主は受け取った側。
        private sealed class AnalysisOutcome {
            public ZoneAutoTuner.TuneResult Result;
            public Exception Error;
            // 漏れの塊(探さなかったら null)。
            public ZoneAutoTuner.LeakBlob[] Blobs;
            // 塊があったときの選択(w*h。確かめが済むまで持つ)。
            public NativeBools Selection;
            // 確かめなかった理由(探す前に飛ばした)。
            public string LeakSkip;

            public void Discard() {
                var s = Selection;
                Selection = null;
                s?.Release();
            }
        }

        // 漏れの確かめの背景の段の結果。外す範囲の持ち主は受け取った側。
        private sealed class StepOutcome {
            public bool Judged;
            public Exception Error;
            public bool[] NeedsFine;
            public NativeBools Exclusion;
            public int ExcludedParts, ExcludedPixels;

            public void Discard() {
                var e = Exclusion;
                Exclusion = null;
                e?.Release();
            }
        }

        // 漏れの確かめの途中の状態(メインスレッドだけが触る)。提案の範囲は届いた順に参照を足して持ち、判定が済んだら返す。
        // 持つのは多くても 12 件(塊 6 × 自動・細かい)で、どれも AI が作った範囲の参照(写さない)。
        private sealed class LeakCheck {
            public readonly ZoneAutoTuner.LeakBlob[] Blobs;
            public NativeBools Selection;
            public readonly NativeBools[] Coarse, Fine;
            public readonly bool[] CoarseFlood, FineFlood;
            public bool[] NeedsFine;
            // false = 自動の提案を集める、true = 細かい提案を集める。
            public bool FinePhase;
            // 次に頼む塊の番号(段の中で、前から順に)。
            public int Next;
            // 届くのを待っている塊(-1 = 待っていない)と、その要求の番号(古い知らせを見分ける)。
            public int Awaiting = -1;
            public int Ticket;
            // 今の要求の期限(0 = まだ頼んでいない)。
            public double Deadline;
            // 走っている背景の段(無ければ null)。
            public Task<StepOutcome> Step;

            public LeakCheck(ZoneAutoTuner.LeakBlob[] blobs, NativeBools selection) {
                Blobs = blobs;
                Selection = selection;
                Coarse = new NativeBools[blobs.Length];
                Fine = new NativeBools[blobs.Length];
                CoarseFlood = new bool[blobs.Length];
                FineFlood = new bool[blobs.Length];
            }

            /// <summary>今の段で受け取った提案の数。</summary>
            public int Received {
                get {
                    int n = 0;
                    foreach (var m in FinePhase ? Fine : Coarse) {
                        if (m != null) {
                            n++;
                        }
                    }
                    return n;
                }
            }

            /// <summary>細かい提案を頼む塊の数。</summary>
            public int FineCount {
                get {
                    int n = 0;
                    if (NeedsFine != null) {
                        foreach (bool b in NeedsFine) {
                            if (b) {
                                n++;
                            }
                        }
                    }
                    return n;
                }
            }

            /// <summary>次に頼む塊(自動の段は全部、細かい段は要る塊だけ)。無ければ -1。</summary>
            public int NextToAsk() {
                for (; Next < Blobs.Length; Next++) {
                    if (!FinePhase || NeedsFine[Next]) {
                        return Next;
                    }
                }
                return -1;
            }

            public void Store(int k, NativeBools mask, bool flood) {
                var dst = FinePhase ? Fine : Coarse;
                var floods = FinePhase ? FineFlood : CoarseFlood;
                dst[k]?.Release();
                dst[k] = mask;
                floods[k] = flood;
                Next = k + 1;
            }

            public void Release() {
                // 走っている段は、抜けた後に結果(外す範囲)を返す。段が読む物は段が自分の参照を持っている。
                var step = Step;
                Step = null;
                step?.ContinueWith(t => t.Result.Discard(), CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                for (int k = 0; k < Blobs.Length; k++) {
                    Coarse[k]?.Release();
                    Coarse[k] = null;
                    Fine[k]?.Release();
                    Fine[k] = null;
                }
                var sel = Selection;
                Selection = null;
                sel?.Release();
            }
        }
    }
}
