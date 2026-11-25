// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iroca {
    /// <summary>自動調整の段の機械(<see cref="AutoTuneFlow"/>)が次に要るもの。</summary>
    internal enum AutoTuneStep {
        /// <summary>AI の提案が 1 件要る(<see cref="AutoTuneFlow.Ask"/>)。</summary>
        NeedProposal,
        /// <summary>背景で回してよい計算が 1 つある(<see cref="AutoTuneFlow.TakeWork"/>)。</summary>
        NeedWork,
        /// <summary>終わった(<see cref="AutoTuneFlow.Result"/>・外す範囲)。</summary>
        Done,
        /// <summary>失敗した(<see cref="AutoTuneFlow.Failure"/>)。</summary>
        Failed,
    }

    /// <summary>頼む提案の種類。</summary>
    internal enum AutoTuneAskKind {
        /// <summary>スポイト位置の提案(自動調整の証拠)。</summary>
        Evidence,
        /// <summary>漏れの塊の点の提案(自動)。</summary>
        LeakAuto,
        /// <summary>漏れの塊の点の提案(細かい。自動の提案を測って要ると決めた塊だけ)。</summary>
        LeakFine,
    }

    /// <summary>
    /// 頼む提案(UV は下原点。製品の RequestProposal と同じ)。用途はどれも自動調整の証拠と同じ使い方(Evidence)で、粒度は
    /// <see cref="Fine"/> なら細かい、それ以外は自動。(Core は AI 提案の型を知らないので、宿主が粒度・用途の型へ写す)
    /// </summary>
    internal readonly struct AutoTuneAsk {
        public readonly AutoTuneAskKind Kind;
        public readonly float U, V;
        /// <summary>漏れの塊の番号(証拠なら -1)。</summary>
        public readonly int Blob;

        public AutoTuneAsk(AutoTuneAskKind kind, float u, float v, int blob) {
            Kind = kind;
            U = u;
            V = v;
            Blob = blob;
        }

        /// <summary>細かい提案を頼むか(漏れの細かい段だけ)。</summary>
        public bool Fine => Kind == AutoTuneAskKind.LeakFine;
    }

    /// <summary>届いた提案(範囲の参照ごと渡す。受け取った側が Mask を Release)。Mask が null = 来なかった。</summary>
    internal readonly struct AutoTuneProposal {
        /// <summary>下原点 Width*Height の範囲。</summary>
        public readonly NativeBools Mask;
        public readonly int Width, Height;
        /// <summary>背景に流れた可能性(提案の floodWarning)。</summary>
        public readonly bool Flood;

        public AutoTuneProposal(NativeBools mask, int width, int height, bool flood) {
            Mask = mask;
            Width = width;
            Height = height;
            Flood = flood;
        }

        public static AutoTuneProposal None => default;
    }

    /// <summary>段の機械が決める失敗(AI が使えない・期限・AI のエラーは宿主が決める)。</summary>
    internal enum AutoTuneFlowFailure {
        None,
        /// <summary>証拠の提案が来なかった(空)。</summary>
        NoEvidence,
        /// <summary>解析が例外で終わった(<see cref="AutoTuneFlow.Error"/>)。</summary>
        AnalysisFailed,
        /// <summary>取り消された(解析の取り消し・確かめの段の取り消し)。</summary>
        Cancelled,
    }

    /// <summary>
    /// 同期の駆動(<see cref="AutoTuneFlow.RunSynchronously"/>)へ提案を渡す源。製品は使わない(AiSuggestBroker の非同期の宿主)。
    /// </summary>
    internal interface IProposalSource {
        /// <summary>同期で 1 件(範囲の参照ごと渡す。受け取った側が Release)。Mask が null = 来なかった(製品の「取り消し・空」と同じ扱い)。</summary>
        AutoTuneProposal Propose(AutoTuneAsk ask, CancellationToken ct);
    }

    /// <summary>
    /// 自動調整 1 ゾーン分の段取り(どの順で何を呼び、どの条件で分岐するか)。「提案が要る」「背景で回してよい計算が 1 つ」
    /// 「終わり・失敗」を返すだけで、時計・期限・スレッド・AI の窓口・進み具合の割り振り・失敗の文言を知らない。
    /// 製品の宿主は AutoTuneService.Run(待つ・背景へ投げる・取り消す)、ハーネスは <see cref="RunSynchronously"/>。
    ///   1. スポイト位置が無ければ証拠なしで解析(理由 <see cref="ReasonNoSamplePosition"/>)。あれば証拠の提案を頼む。
    ///   2. 証拠が来なければ失敗(<see cref="AutoTuneFlowFailure.NoEvidence"/>。従来の導出へ落とさない)。背景まで広がった提案なら
    ///      証拠なしで解析(<see cref="ReasonEvidenceFlood"/>)。それ以外は証拠つきで解析する。
    ///   3. 解析(背景)。証拠つきなら続けて、導いた値で選択を回して漏れの塊を探す(証拠を使って導けたときだけ)。
    ///   4. 塊があれば、塊ごとに自動の提案を頼む → 背景で測る → 外す側に入った塊だけ細かい提案を頼む → 背景で判定して外す範囲を作る。
    ///      確かめの途中の失敗(提案が来ない・寸法違い・計算の例外。宿主の期限・AI のエラー)は確かめだけを飛ばして終える。
    /// メモリ(どの段でやめても <see cref="Dispose"/> が返す): 原本は参照を足して持つ。除外・マスクのスナップショットは引き取る
    /// (除外は解析の計算へ渡しきる)。証拠・漏れの提案の範囲(多くて 12 件)は参照を足して持つ。選択・外す範囲は計算が作り、
    /// <see cref="Complete"/> で受け取る。外す範囲は <see cref="TakeLeakExclusion"/> で渡す。計算(<see cref="AutoTuneWork"/>)は
    /// 読む物の参照を自分で持つので、走っている間にフローを捨ててよい(結果は受け取らなかった側が <see cref="AutoTuneWorkResult.Discard"/>)。
    /// メインスレッド(1 本のスレッド)だけで使う。
    /// </summary>
    internal sealed unsafe class AutoTuneFlow : IDisposable {
        /// <summary>証拠なしで解析した理由: スポイト位置が無い(色で指定したゾーン)。</summary>
        internal const string ReasonNoSamplePosition = "no-sample-position";
        /// <summary>証拠なしで解析した理由: 提案が背景まで広がった。</summary>
        internal const string ReasonEvidenceFlood = "evidence-flood";
        /// <summary>漏れの確かめを飛ばした理由: 提案の寸法が作業解像度と違う。</summary>
        internal const string LeakSkipSizeMismatch = "size-mismatch";
        /// <summary>漏れの確かめを飛ばした理由: 提案を待つ期限が切れた(宿主が決める)。</summary>
        internal const string LeakSkipTimeout = "timeout";
        /// <summary>漏れの確かめを飛ばした理由: 提案が来なかった(取り消し・破棄・空の提案)。</summary>
        internal const string LeakSkipNoProposal = "no-proposal";
        /// <summary>漏れの確かめを飛ばした理由: AI が使えなくなった(後ろに AI のエラーの文言が付くことがある。宿主が決める)。</summary>
        internal const string LeakSkipAi = "ai";
        /// <summary>漏れの確かめを飛ばした理由: 確かめの計算が例外で終わった(後ろに例外の文言が付く)。</summary>
        internal const string LeakSkipError = "error";
        /// <summary>証拠なしで解析した理由: 証拠を渡されなかった(<see cref="StartGiven"/>。ハーネスの --autotune だけ)。</summary>
        internal const string ReasonEvidenceNotGiven = "evidence-not-given";

        enum Stage { Created, AwaitEvidence, Analysis, Leak, Done, Failed }

        readonly ColorZone _zone;
        readonly RecolorSettings _settings;
        readonly int _mw, _mh;
        readonly bool _checkLeaks;
        NativePixels _pixels;
        NativeBools _excluded;
        MaskSnapshot _masks;
        NativeBools _evidence;
        LeakState _leak;
        NativeBools _leakExclusion;
        Stage _stage = Stage.Created;
        AutoTuneStep _current;

        /// <summary>
        /// zoneCopy は調整するゾーンの写し(解析の間に変わらないもの)。pixels は作業解像度の原本(下原点。参照を足して持つ)。
        /// excluded(除外。true = 除外。null = 無し)・masks(選択を回すときのマスク。null = 無し)は引き取る。
        /// checkLeaks = false なら漏れの確かめをしない(ハーネスの --autotune。前から確かめない口なので。製品は常に true)。
        /// </summary>
        public AutoTuneFlow(ColorZone zoneCopy, NativePixels pixels, NativeBools excluded, int maskW, int maskH,
                            in RecolorSettings settings, MaskSnapshot masks, bool checkLeaks = true) {
            _checkLeaks = checkLeaks;
            _zone = zoneCopy;
            _pixels = pixels.AddRef();
            _excluded = excluded;
            _mw = maskW;
            _mh = maskH;
            _settings = settings;
            _masks = masks;
            MaskUsed = excluded != null;
            TextureWidth = pixels.Width;
            TextureHeight = pixels.Height;
        }

        /// <summary>調整するゾーン(写し。読むだけ)。</summary>
        public ColorZone Zone => _zone;
        /// <summary>原本(提案を頼むときに渡す。終わったら返すので、Done・Failed の後は null)。</summary>
        public NativePixels Pixels => _pixels;
        public int TextureWidth { get; }
        public int TextureHeight { get; }
        /// <summary>除外マスクを解析に渡したか。</summary>
        public bool MaskUsed { get; }
        /// <summary>今の段(最後に返したもの)。</summary>
        public AutoTuneStep Current => _current;
        /// <summary>頼む提案(<see cref="AutoTuneStep.NeedProposal"/> のとき)。</summary>
        public AutoTuneAsk Ask { get; private set; }

        public AutoTuneFlowFailure Failure { get; private set; }
        /// <summary>解析の例外(<see cref="AutoTuneFlowFailure.AnalysisFailed"/>)。</summary>
        public Exception Error { get; private set; }
        /// <summary>導出の結果(Done のとき)。</summary>
        public ZoneAutoTuner.TuneResult Result { get; private set; }
        /// <summary>証拠なしで解析した理由(<see cref="ReasonNoSamplePosition"/> / <see cref="ReasonEvidenceFlood"/>)。無ければ null。</summary>
        public string ConventionalReason { get; private set; }
        /// <summary>解析に証拠を渡したか。</summary>
        public bool HadEvidence { get; private set; }
        /// <summary>証拠の寸法(証拠つきで解析したとき)。</summary>
        public int EvidenceWidth { get; private set; }
        public int EvidenceHeight { get; private set; }
        /// <summary>解析に渡した証拠を 1bit/画素に詰めたもの(<see cref="TakeWork"/> に packEvidence を渡したときだけ)。</summary>
        public ulong[] EvidencePacked { get; private set; }
        /// <summary>漏れの確かめで塊を探したか。</summary>
        public bool LeakChecked { get; private set; }
        public int LeakBlobCount { get; private set; }
        public int LeakExcludedParts { get; private set; }
        public int LeakExcludedPixels { get; private set; }
        /// <summary>漏れの確かめを飛ばした理由(飛ばしていなければ null)。</summary>
        public string LeakSkipReason { get; private set; }
        /// <summary>漏れの確かめの段に入っているか(終わる・やめるまで)。</summary>
        public bool InLeakCheck => _leak != null;
        /// <summary>漏れの確かめで細かい提案を集めている段か。</summary>
        public bool LeakFinePhase => _leak != null && _leak.FinePhase;
        /// <summary>今の段で受け取った提案の数・塊の数・細かい提案を頼む塊の数(進み具合の表示用)。</summary>
        public int LeakReceived => _leak?.Received ?? 0;
        public int LeakBlobs => _leak?.Blobs.Length ?? 0;
        public int LeakFineCount => _leak?.FineCount ?? 0;

        /// <summary>証拠が実際に導出に使われたか(導出診断が "fallback" で始まれば従来の導出へ戻った)。</summary>
        public bool UsedEvidence => UsedEvidenceOf(Result);

        internal static bool UsedEvidenceOf(in ZoneAutoTuner.TuneResult r)
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

        /// <summary>漏れの確かめの診断(由来に載せる)。確かめていなければ空。例: "leak=blobs 3 excluded 1 (12345px)"。</summary>
        public string LeakDiag {
            get {
                if (LeakSkipReason != null) {
                    return $"leak=skipped({LeakSkipReason})";
                }
                return LeakChecked ? $"leak=blobs {LeakBlobCount} excluded {LeakExcludedParts} ({LeakExcludedPixels}px)" : "";
            }
        }

        /// <summary>由来(再現データの meta.json 用): 証拠の有無・正規化の有無・導出診断・漏れの確かめ。</summary>
        public string Provenance =>
            (HadEvidence ? (UsedEvidence ? "evidence" : "evidence-fallback")
                         : "conventional" + (ConventionalReason != null ? "(" + ConventionalReason + ")" : ""))
            + $"; normalized={Result.hasNormalizedSample}"
            + (string.IsNullOrEmpty(Result.evidenceDiag) ? "" : "; " + Result.evidenceDiag)
            + (LeakDiag.Length == 0 ? "" : "; " + LeakDiag);

        /// <summary>始める。スポイト位置が無ければ証拠なしの解析(NeedWork)、あれば証拠の提案(NeedProposal)。</summary>
        public AutoTuneStep Start() {
            Expect(Stage.Created);
            if (!_zone.HasSampleUV) {
                BeginAnalysis(null, 0, 0, ReasonNoSamplePosition);
                return _current;
            }
            _stage = Stage.AwaitEvidence;
            Ask = new AutoTuneAsk(AutoTuneAskKind.Evidence, _zone.sampleUV.x, _zone.sampleUV.y, -1);
            return _current = AutoTuneStep.NeedProposal;
        }

        /// <summary>
        /// 証拠を先に渡して始める(ハーネスの --autotune。製品は使わない)。スポイト位置の有無に依らず、渡した証拠を
        /// <see cref="Offer"/> と同じ規則で使う(null なら証拠なしの解析。理由 <see cref="ReasonEvidenceNotGiven"/>)。
        /// </summary>
        public AutoTuneStep StartGiven(AutoTuneProposal evidence) {
            Expect(Stage.Created);
            if (evidence.Mask == null) {
                BeginAnalysis(null, 0, 0, ReasonEvidenceNotGiven);
                return _current;
            }
            _stage = Stage.AwaitEvidence;
            return Offer(evidence);
        }

        /// <summary>
        /// 頼んだ提案が届いた(null・空 = 来なかった)。範囲は参照を足して持つ(呼んだ側は自分の分を返す)。
        ///   証拠: 来なければ Failed(NoEvidence)。背景まで広がった提案なら証拠なしの解析、それ以外は証拠つきの解析(NeedWork)。
        ///   漏れ: 来なければ確かめを飛ばして Done(no-proposal)。寸法が違えば Done(size-mismatch)。それ以外は次の提案か計算。
        /// </summary>
        public AutoTuneStep Offer(AutoTuneProposal proposal) {
            bool empty = proposal.Mask == null || proposal.Width <= 0 || proposal.Height <= 0;
            if (_stage == Stage.AwaitEvidence) {
                if (empty) {
                    return FailWith(AutoTuneFlowFailure.NoEvidence, null);
                }
                // 背景まで広がった提案は素材の証拠にならない。この場合だけは証拠なしで解析する。
                if (proposal.Flood) {
                    BeginAnalysis(null, 0, 0, ReasonEvidenceFlood);
                } else {
                    // 提案の範囲は写さずに参照を足して持つ(配り終えた範囲へは誰も書かず、解析も確かめも読むだけ)。
                    BeginAnalysis(proposal.Mask.AddRef(), proposal.Width, proposal.Height, null);
                }
                return _current;
            }
            if (_stage != Stage.Leak || _current != AutoTuneStep.NeedProposal) {
                throw new InvalidOperationException($"提案を受ける段ではない: {_stage}");
            }
            if (empty) {
                return SkipLeaks(LeakSkipNoProposal);
            }
            if (proposal.Width != TextureWidth || proposal.Height != TextureHeight
                || proposal.Mask.Width != TextureWidth || proposal.Mask.Height != TextureHeight) {
                return SkipLeaks(LeakSkipSizeMismatch);
            }
            _leak.Store(Ask.Blob, proposal.Mask.AddRef(), proposal.Flood);
            return NextLeak();
        }

        /// <summary>宿主の都合で漏れの確かめだけを飛ばして終える(期限・AI のエラー)。確かめの段でなければ何もしない。</summary>
        public AutoTuneStep SkipLeaks(string reason) {
            if (_stage != Stage.Leak) {
                return _current;
            }
            LeakSkipReason = reason;
            return Finish();
        }

        /// <summary>
        /// 背景で回してよい計算を 1 つ渡す(NeedWork のとき。1 回だけ)。読む物の参照はここで足し、計算の Dispose で返す。
        /// packEvidence: 解析に渡した証拠を詰めて控えるか(再現データの書き出しがある環境だけ)。
        /// </summary>
        public AutoTuneWork TakeWork(bool packEvidence = false) {
            if (_current != AutoTuneStep.NeedWork) {
                throw new InvalidOperationException($"計算が要る段ではない: {_stage}");
            }
            if (_stage == Stage.Analysis) {
                var excluded = _excluded;
                _excluded = null;   // 除外は解析だけが使うので渡しきる
                return AutoTuneWork.ForAnalysis(_pixels.AddRef(), _evidence?.AddRef(), EvidenceWidth, EvidenceHeight,
                    _evidence != null ? _masks?.AddRef() : null, excluded, _zone, _mw, _mh, in _settings, packEvidence,
                    _checkLeaks);
            }
            Expect(Stage.Leak);
            var lk = _leak;
            bool judge = lk.FinePhase;
            return AutoTuneWork.ForLeakStep(judge, _pixels.AddRef(), _evidence.AddRef(), lk.Selection.AddRef(),
                AddRefAll(lk.Coarse), judge ? AddRefAll(lk.Fine) : null,
                (bool[])lk.CoarseFlood.Clone(), (bool[])lk.FineFlood.Clone(), TextureWidth, TextureHeight,
                judge ? lk.CoarseVerdicts : null);
        }

        /// <summary>
        /// 計算の結果を受け取る(持ち主はフローへ移る)。cancellationRequested は宿主の取り消しが立っているか
        /// (確かめの段の取り消しで抜けた計算を、確かめの失敗と見分ける)。
        /// </summary>
        public AutoTuneStep Complete(AutoTuneWorkResult r, bool cancellationRequested) {
            if (_current != AutoTuneStep.NeedWork) {
                r?.Discard();
                throw new InvalidOperationException($"計算の結果を受ける段ではない: {_stage}");
            }
            if (_stage == Stage.Analysis) {
                if (r.Error != null) {
                    r.Discard();
                    return FailWith(r.Error is OperationCanceledException
                        ? AutoTuneFlowFailure.Cancelled : AutoTuneFlowFailure.AnalysisFailed, r.Error);
                }
                Result = r.Result;
                EvidencePacked = r.EvidencePacked;
                LeakSkipReason = r.LeakSkip;
                if (r.Blobs != null) {
                    LeakChecked = true;
                    LeakBlobCount = r.Blobs.Length;
                }
                if (r.Selection == null) {
                    return Finish();
                }
                _leak = new LeakState(r.Blobs, r.Selection);
                r.Selection = null;
                _stage = Stage.Leak;
                return NextLeak();
            }
            Expect(Stage.Leak);
            if (r.Error != null) {
                r.Discard();
                if (r.Error is OperationCanceledException && cancellationRequested) {
                    return FailWith(AutoTuneFlowFailure.Cancelled, null);
                }
                return SkipLeaks(LeakSkipError + ": " + r.Error.Message);
            }
            if (!r.Judged) {
                // 自動の提案を測り終えた: 細かい提案が要る塊(外す側に入った塊)だけ、同じ点で頼み直す。
                _leak.NeedsFine = r.NeedsFine;
                _leak.CoarseVerdicts = r.CoarseVerdicts;
                if (Array.IndexOf(_leak.NeedsFine, true) < 0) {
                    return Finish();   // 外す側に入った塊が無い(細かい提案が無いと外さない)
                }
                _leak.FinePhase = true;
                _leak.Next = 0;
                return NextLeak();
            }
            LeakExcludedParts = r.ExcludedParts;
            LeakExcludedPixels = r.ExcludedPixels;
            _leakExclusion = r.Exclusion;
            r.Exclusion = null;
            return Finish();
        }

        /// <summary>外す範囲を渡す(作業解像度・下原点。true = 塗らない。無ければ null。受け取った側が Release)。</summary>
        public NativeBools TakeLeakExclusion() {
            var e = _leakExclusion;
            _leakExclusion = null;
            return e;
        }

        /// <summary>
        /// 終わった後に要らないもの(原本・証拠・マスク・除外・確かめの途中の物)を返す。外す範囲と結果の値は残す。
        /// </summary>
        public void ReleaseWork() {
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

        /// <summary>持ち物を全部返す(外す範囲も)。何度呼んでもよい。</summary>
        public void Dispose() {
            ReleaseWork();
            var e = _leakExclusion;
            _leakExclusion = null;
            e?.Release();
        }

        void BeginAnalysis(NativeBools evidence, int evW, int evH, string conventionalReason) {
            _stage = Stage.Analysis;
            ConventionalReason = conventionalReason;
            HadEvidence = evidence != null;
            _evidence = evidence;
            EvidenceWidth = evW;
            EvidenceHeight = evH;
            _current = AutoTuneStep.NeedWork;
        }

        // 次に頼む塊(自動の段は全部、細かい段は要る塊だけ)。無ければ測る・判定する計算。
        AutoTuneStep NextLeak() {
            int k = _leak.NextToAsk();
            if (k < 0) {
                return _current = AutoTuneStep.NeedWork;
            }
            var b = _leak.Blobs[k];
            // 塊の点は下原点の画素の位置。UV は画素の中心(ZoneAutoTuner.Leak.cs の冒頭の説明)。
            float u = (b.X + 0.5f) / TextureWidth, v = (b.Y + 0.5f) / TextureHeight;
            bool fine = _leak.FinePhase;
            Ask = new AutoTuneAsk(fine ? AutoTuneAskKind.LeakFine : AutoTuneAskKind.LeakAuto, u, v, k);
            return _current = AutoTuneStep.NeedProposal;
        }

        AutoTuneStep Finish() {
            _stage = Stage.Done;
            return _current = AutoTuneStep.Done;
        }

        AutoTuneStep FailWith(AutoTuneFlowFailure failure, Exception error) {
            _stage = Stage.Failed;
            Failure = failure;
            Error = error;
            return _current = AutoTuneStep.Failed;
        }

        void Expect(Stage s) {
            if (_stage != s) {
                throw new InvalidOperationException($"段が違う: {_stage}(期待 {s})");
            }
        }

        static NativeBools[] AddRefAll(NativeBools[] masks) {
            var copy = new NativeBools[masks.Length];
            for (int k = 0; k < masks.Length; k++) {
                copy[k] = masks[k]?.AddRef();
            }
            return copy;
        }

        /// <summary>
        /// 外す範囲から、利用者が「含める」にした画素を外す(include が null か長さが違えば何も外さない)。外した後に 1 画素でも残れば true。
        /// 語(64 画素)の区間に分けて並列に回す(画素ごとに独立なので、どう分けても中身は同じ)。
        /// </summary>
        public static bool DropIncluded(NativeBools region, NativeBitMask include) {
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

        /// <summary>
        /// ハーネス・テスト用の同期の駆動(製品は使わない)。提案の源を渡すと最後まで回して、終わりの段(Done・Failed)を返す。
        /// 結果は <see cref="Result"/>・<see cref="TakeLeakExclusion"/> などで読む。提案の源の例外は、証拠のときはそのまま投げ、
        /// 漏れの確かめのときは確かめだけを飛ばす(製品の AI のエラーと同じ扱い。理由は ai: 例外の文言)。
        /// onWork は計算ごとの観察者(切り分け用。結果は読むだけで、持ち続けない)。
        /// </summary>
        public static AutoTuneStep RunSynchronously(AutoTuneFlow flow, IProposalSource source, CancellationToken ct,
            bool packEvidence = false, Action<AutoTuneWorkKind, AutoTuneWorkResult> onWork = null)
            => Drive(flow, flow.Start(), source, ct, packEvidence, onWork);

        /// <summary>始めた後の段から最後まで回す(<see cref="RunSynchronously"/>。<see cref="StartGiven"/> の後もこれで回す)。</summary>
        public static AutoTuneStep Drive(AutoTuneFlow flow, AutoTuneStep step, IProposalSource source, CancellationToken ct,
            bool packEvidence = false, Action<AutoTuneWorkKind, AutoTuneWorkResult> onWork = null) {
            while (step == AutoTuneStep.NeedProposal || step == AutoTuneStep.NeedWork) {
                ct.ThrowIfCancellationRequested();
                if (step == AutoTuneStep.NeedProposal) {
                    var ask = flow.Ask;
                    AutoTuneProposal p;
                    if (ask.Kind == AutoTuneAskKind.Evidence) {
                        p = source.Propose(ask, ct);
                    } else {
                        try {
                            p = source.Propose(ask, ct);
                        } catch (Exception e) when (!(e is OperationCanceledException)) {
                            step = flow.SkipLeaks(LeakSkipAi + ": " + e.Message);
                            continue;
                        }
                    }
                    try {
                        step = flow.Offer(p);
                    } finally {
                        p.Mask?.Release();
                    }
                    continue;
                }
                using (var work = flow.TakeWork(packEvidence)) {
                    var r = work.Run(ct);
                    onWork?.Invoke(work.Kind, r);
                    step = flow.Complete(r, ct.IsCancellationRequested);
                }
            }
            flow.ReleaseWork();
            return step;
        }

        // 漏れの確かめの途中の状態。提案の範囲は届いた順に参照を足して持ち、判定が済んだら返す。
        // 持つのは多くても 12 件(塊 6 × 自動・細かい)で、どれも AI が作った範囲の参照(写さない)。
        sealed class LeakState {
            public readonly ZoneAutoTuner.LeakBlob[] Blobs;
            public NativeBools Selection;
            public readonly NativeBools[] Coarse, Fine;
            public readonly bool[] CoarseFlood, FineFlood;
            public bool[] NeedsFine;
            // 自動の提案を測った判定(塊ごと)。判定の計算へ渡し、同じ提案を測り直さない(Coarse は細かい段で書き換わらない)。
            public ZoneAutoTuner.LeakVerdict[] CoarseVerdicts;
            // false = 自動の提案を集める、true = 細かい提案を集める。
            public bool FinePhase;
            // 次に頼む塊の番号(段の中で、前から順に)。
            public int Next;

            public LeakState(ZoneAutoTuner.LeakBlob[] blobs, NativeBools selection) {
                Blobs = blobs;
                Selection = selection;
                Coarse = new NativeBools[blobs.Length];
                Fine = new NativeBools[blobs.Length];
                CoarseFlood = new bool[blobs.Length];
                FineFlood = new bool[blobs.Length];
            }

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

    /// <summary>背景の計算の種類。</summary>
    internal enum AutoTuneWorkKind {
        /// <summary>解析(と、証拠つきなら続けて漏れの塊の取り出し)。</summary>
        Analysis,
        /// <summary>漏れの塊ごとの自動の提案を測る(細かい提案が要る塊を決める)。</summary>
        MeasureLeaks,
        /// <summary>判定して外す範囲を作る。</summary>
        JudgeLeaks,
    }

    /// <summary>背景の計算の結果(背景 → メインスレッド)。選択・外す範囲の持ち主は受け取った側。</summary>
    internal sealed class AutoTuneWorkResult {
        public AutoTuneWorkKind Kind;
        public Exception Error;
        // 解析
        public ZoneAutoTuner.TuneResult Result;
        public ulong[] EvidencePacked;
        /// <summary>漏れの塊(探さなかったら null)。</summary>
        public ZoneAutoTuner.LeakBlob[] Blobs;
        /// <summary>塊があったときの選択(w*h。確かめが済むまで持つ)。</summary>
        public NativeBools Selection;
        /// <summary>確かめなかった理由(探す前に飛ばした)。</summary>
        public string LeakSkip;
        // 漏れの段
        public bool Judged;
        public bool[] NeedsFine;
        /// <summary>自動の提案を測った判定(測る段だけ。判定の段へ渡して測り直しを省く)。</summary>
        public ZoneAutoTuner.LeakVerdict[] CoarseVerdicts;
        public NativeBools Exclusion;
        public int ExcludedParts, ExcludedPixels;

        /// <summary>持っている範囲(選択・外す範囲)を返す。</summary>
        public void Discard() {
            var s = Selection;
            Selection = null;
            s?.Release();
            var e = Exclusion;
            Exclusion = null;
            e?.Release();
        }
    }

    /// <summary>
    /// <see cref="AutoTuneFlow"/> が渡す背景の計算 1 つ。読む物(原本・証拠・マスク・除外・選択・提案)の参照を持ち、
    /// <see cref="Dispose"/> で返す(背景のスレッドで呼んでよい)。<see cref="Run"/> は例外を結果に入れて返す(投げない)。
    /// </summary>
    internal sealed unsafe class AutoTuneWork : IDisposable {
        readonly AutoTuneWorkKind _kind;
        NativePixels _px;
        NativeBools _ev;
        MaskSnapshot _masks;
        NativeBools _excluded;
        NativeBools _sel;
        NativeBools[] _coarse, _fine;
        readonly bool[] _coarseFlood, _fineFlood;
        readonly ZoneAutoTuner.LeakVerdict[] _coarseVerdicts;
        readonly ColorZone _zone;
        readonly int _evW, _evH, _mw, _mh, _w, _h;
        readonly RecolorSettings _settings;
        readonly bool _packEvidence;
        readonly bool _checkLeaks;

        AutoTuneWork(AutoTuneWorkKind kind, NativePixels px, NativeBools ev, int evW, int evH, MaskSnapshot masks,
                     NativeBools excluded, ColorZone zone, int mw, int mh, in RecolorSettings settings, bool packEvidence,
                     NativeBools sel, NativeBools[] coarse, NativeBools[] fine, bool[] coarseFlood, bool[] fineFlood, int w, int h,
                     bool checkLeaks, ZoneAutoTuner.LeakVerdict[] coarseVerdicts = null) {
            _kind = kind;
            _checkLeaks = checkLeaks;
            _px = px;
            _ev = ev;
            _evW = evW;
            _evH = evH;
            _masks = masks;
            _excluded = excluded;
            _zone = zone;
            _mw = mw;
            _mh = mh;
            _settings = settings;
            _packEvidence = packEvidence;
            _sel = sel;
            _coarse = coarse;
            _fine = fine;
            _coarseFlood = coarseFlood;
            _fineFlood = fineFlood;
            _coarseVerdicts = coarseVerdicts;
            _w = w;
            _h = h;
        }

        internal static AutoTuneWork ForAnalysis(NativePixels px, NativeBools ev, int evW, int evH, MaskSnapshot masks,
            NativeBools excluded, ColorZone zone, int mw, int mh, in RecolorSettings settings, bool packEvidence, bool checkLeaks)
            => new AutoTuneWork(AutoTuneWorkKind.Analysis, px, ev, evW, evH, masks, excluded, zone, mw, mh, in settings,
                packEvidence, null, null, null, null, null, 0, 0, checkLeaks);

        // coarseVerdicts: 判定の段で、測る段が同じ自動の提案を測った判定(無ければ null で、判定の段が測る)。
        internal static AutoTuneWork ForLeakStep(bool judge, NativePixels px, NativeBools ev, NativeBools sel,
            NativeBools[] coarse, NativeBools[] fine, bool[] coarseFlood, bool[] fineFlood, int w, int h,
            ZoneAutoTuner.LeakVerdict[] coarseVerdicts = null)
            => new AutoTuneWork(judge ? AutoTuneWorkKind.JudgeLeaks : AutoTuneWorkKind.MeasureLeaks, px, ev, 0, 0, null, null,
                null, 0, 0, default, false, sel, coarse, fine, coarseFlood, fineFlood, w, h, false, coarseVerdicts);

        public AutoTuneWorkKind Kind => _kind;

        /// <summary>
        /// 計算する(背景のスレッドで呼んでよい)。例外は結果の Error に入れて返す。
        /// 解析の観察者(進み具合の表示用。どれも省略可): started = 取り消しを見た後、解析の前。report = 解析の進み(0..1。
        /// 証拠なしなら最後に 1)。probed = 証拠つきで漏れの塊を探し終えた後。
        /// </summary>
        public AutoTuneWorkResult Run(CancellationToken ct, Action started = null, Action<float> report = null, Action probed = null) {
            return _kind == AutoTuneWorkKind.Analysis ? RunAnalysis(ct, started, report, probed) : RunLeakStep(ct);
        }

        AutoTuneWorkResult RunAnalysis(CancellationToken ct, Action started, Action<float> report, Action probed) {
            var outcome = new AutoTuneWorkResult { Kind = AutoTuneWorkKind.Analysis };
            try {
                ct.ThrowIfCancellationRequested();
                started?.Invoke();
                // ct を Analyze へ渡す。取り消されたら各解析ステップ間で停止し、ゾンビ実行(CPU 2 倍)を防ぐ。
                bool* excludedPtr = _excluded != null ? _excluded.Ptr : null;
                outcome.Result = _ev != null
                    ? ZoneAutoTuner.AnalyzeWithEvidenceCore(_px.Ptr, _px.Width, _px.Height, _zone,
                        _ev.Ptr, _evW, _evH, excludedPtr, _mw, _mh, ct, report)
                    : ZoneAutoTuner.AnalyzeCore(_px.Ptr, _px.Width, _px.Height, _zone, excludedPtr, _mw, _mh, ct, report);
                // 適用後も持つ証拠(再現データ用)は 1bit/画素に詰めたもの。再現データを書き出せる環境でだけ控える。
                outcome.EvidencePacked = _packEvidence ? MaskSnapshot.Pack(_ev) : null;   // null は null
                if (_ev != null && _checkLeaks) {
                    // 確かめの失敗は自動調整を失敗にしない(取り消しだけは全体を止める)。
                    try {
                        ProbeLeaks(_px, _ev, _evW, _evH, _zone, in _settings, _masks, ct, outcome);
                    } catch (Exception e) when (!(e is OperationCanceledException)) {
                        outcome.Discard();
                        outcome.Blobs = null;
                        outcome.LeakSkip = AutoTuneFlow.LeakSkipError + ": " + e.Message;
                    }
                    probed?.Invoke();
                } else {
                    report?.Invoke(1.0f);
                }
                return outcome;
            } catch (Exception e) {
                outcome.Discard();
                outcome.Error = e;
                return outcome;
            }
        }

        // 漏れの確かめの 1 段目(解析と同じ計算で続けて回す): 導いた値を当てたゾーンの写しで、製品が塗るときと同じ選択を
        // 回し(PixelProcessor.SimulateSelection)、証拠の外に塗られた塊を探す(ZoneAutoTuner.FindLeakBlobs)。
        // 確かめるのは証拠を使って導けたときだけ(従来の導出へ落ちたら、証拠は導出の根拠でない)。
        static void ProbeLeaks(NativePixels px, NativeBools evidence, int evW, int evH, ColorZone zone,
            in RecolorSettings settings, MaskSnapshot masks, CancellationToken ct, AutoTuneWorkResult outcome) {
            if (!AutoTuneFlow.UsedEvidenceOf(outcome.Result)) {
                return;
            }
            int w = px.Width, h = px.Height;
            // 提案は作業解像度の原本から作る(AI と同じ元のキー)ので、ふつうは同じ寸法。違えば確かめない
            // (塊の点と提案の画素の対応が取れない)。
            if (evW != w || evH != h || evidence.Width != w || evidence.Height != h) {
                outcome.LeakSkip = AutoTuneFlow.LeakSkipSizeMismatch;
                return;
            }
            var tuned = zone.Clone();
            outcome.Result.ApplyTo(tuned);
            var sel = NativeBools.AllocateUninitialized(w, h);
            try {
                PixelProcessor.SimulateSelection(px.Ptr, w, h, tuned, in settings, masks, sel.Ptr, null, ct);
                outcome.Blobs = ZoneAutoTuner.FindLeakBlobs(sel.Ptr, evidence.Ptr, w, h, ct);
                if (outcome.Blobs.Length > 0) {
                    outcome.Selection = sel;   // 確かめが済むまでフローが持つ
                    sel = null;
                }
            } finally {
                sel?.Release();
            }
        }

        AutoTuneWorkResult RunLeakStep(CancellationToken ct) {
            bool judge = _kind == AutoTuneWorkKind.JudgeLeaks;
            var o = new AutoTuneWorkResult { Kind = _kind, Judged = judge };
            try {
                ct.ThrowIfCancellationRequested();
                var props = Proposals(_coarse, _coarseFlood);
                if (!judge) {
                    o.NeedsFine = new bool[props.Length];
                    o.CoarseVerdicts = new ZoneAutoTuner.LeakVerdict[props.Length];
                    for (int k = 0; k < props.Length; k++) {
                        o.CoarseVerdicts[k] = ZoneAutoTuner.MeasureLeakProposal(_px.Ptr, _sel.Ptr, _ev.Ptr, _w, _h, props[k], ct);
                        o.NeedsFine[k] = o.CoarseVerdicts[k].NeedsFine;
                    }
                } else {
                    o.Exclusion = NativeBools.AllocateUninitialized(_w, _h);
                    var verdicts = ZoneAutoTuner.JudgeLeaks(_px.Ptr, _sel.Ptr, _ev.Ptr, _w, _h, props,
                        Proposals(_fine, _fineFlood), o.Exclusion.Ptr, out int excludedPixels, ct,
                        coarseVerdicts: _coarseVerdicts);
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
            }
        }

        static ZoneAutoTuner.LeakProposal[] Proposals(NativeBools[] masks, bool[] flood) {
            var props = new ZoneAutoTuner.LeakProposal[masks.Length];
            for (int k = 0; k < masks.Length; k++) {
                props[k] = new ZoneAutoTuner.LeakProposal { Mask = masks[k] != null ? masks[k].Ptr : null, Flood = flood[k] };
            }
            return props;
        }

        /// <summary>
        /// 読む間の参照を返す(作り替え前の後始末と同じ順: 解析は証拠 → マスク → 除外 → 原本、漏れの段は細かい → 自動 → 選択 →
        /// 証拠 → 原本)。何度呼んでもよい。
        /// </summary>
        public void Dispose() {
            if (_kind == AutoTuneWorkKind.Analysis) {
                Interlocked.Exchange(ref _ev, null)?.Release();
                Interlocked.Exchange(ref _masks, null)?.Release();
                Interlocked.Exchange(ref _excluded, null)?.Release();
                Interlocked.Exchange(ref _px, null)?.Release();
                return;
            }
            ReleaseAll(Interlocked.Exchange(ref _fine, null));
            ReleaseAll(Interlocked.Exchange(ref _coarse, null));
            Interlocked.Exchange(ref _sel, null)?.Release();
            Interlocked.Exchange(ref _ev, null)?.Release();
            Interlocked.Exchange(ref _px, null)?.Release();
        }

        static void ReleaseAll(NativeBools[] masks) {
            if (masks == null) {
                return;
            }
            for (int k = 0; k < masks.Length; k++) {
                masks[k]?.Release();
                masks[k] = null;
            }
        }
    }
}
