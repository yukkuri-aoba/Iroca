// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 自動調整の流れ(<see cref="AutoTuneService"/>)。ウィンドウと自動化が同じ手順を使う: 位置の無いゾーンは証拠なし、
    /// 位置があれば AI の提案を待って証拠つき、背景まで広がった提案は証拠なし、AI が無ければ中止。結果は Core の解析を
    /// 直接呼んだものと同じ。AI は偽物(<see cref="FakeMaskSuggestService"/>)に差し替えるので Sentis の無いホストでも走る。
    /// </summary>
    public class AutoTuneServiceTests
    {
        private const int W = 64, H = 64;
        private IMaskSuggestService _saved;
        private FakeMaskSuggestService _svc;
        private NativePixels _pixels;

        [SetUp]
        public void SetUp()
        {
            _saved = MaskSuggestBridge.Service;
            _svc = new FakeMaskSuggestService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
            _pixels = NativePixels.CopyOf(Halves(), W, H);
        }

        [TearDown]
        public void TearDown()
        {
            _pixels.Release();
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
        }

        // 左半分は陰影のある赤、右半分は青(下原点)。
        private static Color32[] Halves()
        {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte shade = (byte)(150 + (x * 7 + y * 3) % 70);
                px[y * W + x] = x < W / 2 ? new Color32(shade, 30, 30, 255) : new Color32(30, 40, 200, 255);
            }
            return px;
        }

        private static ColorZone RedZone(bool clicked)
        {
            var z = EditingSession.NewZone(new System.Collections.Generic.List<ColorZone>());
            z.sampleColor = new Color32(185, 30, 30, 255);
            z.targetColor = new Color(0.1f, 0.2f, 0.6f);
            if (clicked) z.sampleUV = new Vector2(0.25f, 0.5f);
            z.EnsureId();
            return z;
        }

        private static IrocaSessionState Session(ColorZone zone)
        {
            var s = IrocaSessionState.CreateDefault();
            s.zones.Clear();
            s.zones.Add(zone);
            return s;
        }

        // 左半分(赤)を素材とする証拠。
        private static bool[] LeftHalfMask()
        {
            var m = new bool[W * H];
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W / 2; x++)
                m[y * W + x] = true;
            return m;
        }

        // 提案の範囲はヒープの外で、受け取り先を呼んだ後に AiSuggestBroker が返す(テストは写しの LeftHalfMask と比べる)。
        private static MaskSuggestProposal LeftHalf(bool flood = false)
            => new MaskSuggestProposal { mask = NativeBools.CopyOf(LeftHalfMask(), W, H), width = W, height = H, floodWarning = flood };

        private static void WaitForAnalysis(AutoTuneService.Run run)
        {
            var sw = Stopwatch.StartNew();
            while (run.Status == AutoTuneService.Status.Analyzing)
            {
                Assert.Less(sw.Elapsed.TotalSeconds, 60, "解析が終わらない");
                run.Advance();
                Thread.Sleep(5);
            }
        }

        // 結果を当てたゾーンの導出値が、Core の解析を直接呼んだ結果と同じか。
        private static void AssertSameAsCore(AutoTuneService.Run run, ColorZone zone, IrocaSessionState session,
            ZoneAutoTuner.TuneResult expected)
        {
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status, run.Failure + " " + run.FailureDetail);
            var applied = zone.Clone();
            run.ApplyTo(applied, session);
            Assert.IsTrue(AutoTuneService.ValuesIntact(applied, expected), "導出値が Core の解析と違う");
            Assert.AreEqual(expected.hasNormalizedSample, run.Result.hasNormalizedSample);
            Assert.AreEqual(expected.evidenceDiag, run.Result.evidenceDiag);
        }

        [Test]
        public unsafe void ZoneWithoutPosition_IsAnalysedWithoutEvidence_AndWithoutAskingTheAi()
        {
            var zone = RedZone(clicked: false);
            var session = Session(zone);
            var run = AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0);
            Assert.AreEqual(AutoTuneService.Status.Analyzing, run.Status);
            Assert.AreEqual(AutoTuneService.ReasonNoSamplePosition, run.ConventionalReason);
            Assert.IsFalse(run.AskedAi);
            Assert.AreEqual(0, _svc.queue.Count, "AI に求めない");
            WaitForAnalysis(run);
            var expected = ZoneAutoTuner.Analyze(_pixels.Ptr, W, H, zone.Clone(), ZoneAutoTuner.SessionInput.From(session));
            AssertSameAsCore(run, zone, session, expected);
            Assert.IsFalse(run.HadEvidence);
            StringAssert.StartsWith("conventional(no-sample-position)", run.Provenance);
            Assert.AreEqual(1, _pixels.RefCount, "解析が足した参照は返す");
        }

        [Test]
        public unsafe void ClickedZone_WaitsForTheAiAndUsesItsRegionAsEvidence()
        {
            var zone = RedZone(clicked: true);
            var session = Session(zone);
            var run = AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0);
            Assert.AreEqual(AutoTuneService.Status.WaitingForEvidence, run.Status);
            Assert.IsTrue(run.AskedAi);
            Assert.IsFalse(run.EvidencePreparing, "AI が待機中なら、その場で受け付けられる");
            Assert.AreEqual("k", _svc.sourceKey);
            var fake = _svc.queue.Peek();
            Assert.AreEqual(0.25f, fake.u, 1e-6f);
            Assert.AreEqual(0.5f, fake.v, 1e-6f);

            var evidence = LeftHalf();
            _svc.Finish(evidence);
            Assert.AreEqual(AutoTuneService.Status.Analyzing, run.Status);
            WaitForAnalysis(run);
            var expected = ZoneAutoTuner.AnalyzeWithEvidence(_pixels.Ptr, W, H, zone.Clone(),
                ZoneAutoTuner.SessionInput.From(session), LeftHalfMask(), W, H);
            AssertSameAsCore(run, zone, session, expected);
            Assert.IsTrue(run.HadEvidence);
            var input = run.ToInput();
            Assert.IsNotNull(input.evidence, "再現データ用に証拠を詰めて持つ");
            Assert.AreEqual(W, input.evW);
            Assert.AreEqual(W, input.texW);
            Assert.AreEqual(1, _pixels.RefCount);
        }

        [Test]
        public void RegionThatSpreadToTheBackground_IsNotUsedAsEvidence()
        {
            var zone = RedZone(clicked: true);
            var run = AutoTuneService.Start(zone, Session(zone), _pixels, "k", null, 0, 0);
            _svc.Finish(LeftHalf(flood: true));
            Assert.AreEqual(AutoTuneService.ReasonEvidenceFlood, run.ConventionalReason);
            WaitForAnalysis(run);
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status);
            Assert.IsFalse(run.HadEvidence);
        }

        [Test]
        public void WaitsWhileTheAiPrepares()
        {
            _svc.encodeOnNewSource = true;
            var zone = RedZone(clicked: true);
            var run = AutoTuneService.Start(zone, Session(zone), _pixels, "k", null, 0, 0);
            Assert.AreEqual(AutoTuneService.Status.WaitingForEvidence, run.Status);
            Assert.IsTrue(run.EvidencePreparing, "埋め込みの計算中は受け付けられていない");
            run.Advance();
            Assert.IsTrue(run.EvidencePreparing);
            _svc.Set(MaskSuggestPhase.Idle);
            run.Advance();
            Assert.IsFalse(run.EvidencePreparing, "待機中になったら求め直す");
            Assert.AreEqual(1, _svc.queue.Count);
            run.Cancel();
        }

        [Test]
        public void StopsWhenTheAiCannotBeUsed()
        {
            var zone = RedZone(clicked: true);
            var session = Session(zone);

            Assert.AreEqual(AutoTuneService.Failure.SourceNotShared,
                AutoTuneService.Start(zone, session, _pixels, null, null, 0, 0).Failure, "AI と共有できる元が無い");

            _svc.modelsOk = false;
            var noModel = AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0);
            Assert.AreEqual(AutoTuneService.Status.Failed, noModel.Status);
            Assert.AreEqual(AutoTuneService.Failure.AiUnavailable, noModel.Failure, "証拠なしの導出へ落とさない");

            MaskSuggestBridge.Service = null;
            Assert.AreEqual(AutoTuneService.Failure.AiUnavailable,
                AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0).Failure);
            Assert.AreEqual(1, _pixels.RefCount, "始められなかったら参照は返す");
        }

        [Test]
        public void CancelAndLostProposals_DoNotAnalyse()
        {
            var zone = RedZone(clicked: true);
            var session = Session(zone);
            var run = AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0);
            run.Cancel();
            Assert.AreEqual(AutoTuneService.Failure.Cancelled, run.Failure);
            Assert.IsFalse(AiSuggestBroker.HasLive, "待っていた提案も取り消す");
            Assert.AreEqual(1, _pixels.RefCount);

            var lost = AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0);
            AiSuggestBroker.Release(new object());   // ほかの持ち主の取り消しでは消えない
            Assert.AreEqual(AutoTuneService.Status.WaitingForEvidence, lost.Status);
            AiSuggestBroker.Cancel(lost);            // 届かなかった(取り消し・破棄)
            Assert.AreEqual(AutoTuneService.Failure.NoEvidence, lost.Failure);

            var error = AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0);
            _svc.queue.Clear();
            _svc.Set(MaskSuggestPhase.Error);
            Assert.AreEqual(AutoTuneService.Failure.AiError, error.Failure, "エラーで捨てられたらその理由");
            Assert.AreEqual("boom", error.FailureDetail);
            Assert.Throws<System.InvalidOperationException>(() => error.ApplyTo(zone, session), "終わっていない結果は当てない");
        }

        [Test]
        public void UntouchedByUser_NewZoneAndLastAutoTune()
        {
            var zone = RedZone(clicked: false);
            Assert.IsTrue(AutoTuneService.UntouchedByUser(zone, null), "新しいゾーンの初期値は手調整ではない");
            zone.saturationGuard += 0.2f;
            Assert.IsFalse(AutoTuneService.UntouchedByUser(zone, null));

            var session = Session(zone);
            var run = AutoTuneService.Start(zone, session, _pixels, "k", null, 0, 0);
            WaitForAnalysis(run);
            run.ApplyTo(zone, session);
            var input = run.ToInput();
            Assert.IsTrue(AutoTuneService.UntouchedByUser(zone, input), "自動調整の値のまま");
            Assert.IsTrue(AutoTuneService.ParamsIntact(zone, input));
            zone.tolerance += 0.05f;
            Assert.IsFalse(AutoTuneService.UntouchedByUser(zone, input));
        }
    }
}
