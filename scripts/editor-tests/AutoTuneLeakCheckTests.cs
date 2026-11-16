// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 自動調整の後の漏れの確かめを通す合成の絵(128 × 128、下原点の行)。部品 A(行 0..47。陰影のある赤)をクリックし、
    /// その下に続く部品 B(行 48..127)は、A と同じ赤から青へなだらかに変わる面。証拠(最初の AI 提案)を A にすると、
    /// 自動調整の選択は B の赤い端(行 48..67 ほど)まで塗り、その境目は面の途中(見分けられない色の差)を通る。
    /// B の上で取り直した提案が B 全体なら、B は「一部だけが塗られた別の部品」なので外れる。
    /// 同じ絵をハーネスの --autotune・--leak-probe・--leak-judge に通して、塊が 1 つ・判定が外す・外す範囲 = B に
    /// なることを確かめてある(2026-11-16)。
    /// </summary>
    internal static class LeakScene {
        public const int W = 128, H = 128;
        // A は行 0..AEnd-Gap-1、B は行 AEnd..H-1、間の Gap 行は灰色。B の塊が A(証拠)を広げた所(L1 距離 8)に接すると
        // 確かめない(Core の FindLeakBlobs)ので、間を空けて別の部品にする。
        public const int AEnd = 48;
        public const int Gap = 16;
        public const int X0 = 8, X1 = W - 8; // どちらも列 X0..X1-1(左右は灰色)
        public const int ClickX = 64, ClickY = 24;

        public static bool InA(int x, int y) => y < AEnd - Gap && x >= X0 && x < X1;
        public static bool InB(int x, int y) => y >= AEnd && x >= X0 && x < X1;

        public static Color32[] Image() {
            var px = new Color32[W * H];
            int n = H - 1 - AEnd;
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    Color32 c = new Color32(128, 128, 128, 255);
                    if (InA(x, y)) {
                        c = new Color32((byte)(150 + (x * 7 + y * 3) % 40), 30, 30, 255);
                    } else if (InB(x, y)) {
                        int d = y - AEnd;
                        c = new Color32((byte)(170 - 140 * d / n), 30, (byte)(30 + 170 * d / n), 255);
                    }
                    px[y * W + x] = c;
                }
            }
            return px;
        }

        public static bool[] Mask(System.Func<int, int, bool> inside) {
            var m = new bool[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    m[y * W + x] = inside(x, y);
                }
            }
            return m;
        }

        public static int Count(bool[] m) => m.Count(b => b);

        // 提案の範囲はヒープの外で、受け取り先を呼んだ後に AiSuggestBroker が返す。
        public static MaskSuggestProposal Proposal(System.Func<int, int, bool> inside, bool flood = false)
            => new MaskSuggestProposal { mask = NativeBools.CopyOf(Mask(inside), W, H), width = W, height = H, floodWarning = flood };

        public static Vector2 ClickUv => new Vector2((ClickX + 0.5f) / W, (ClickY + 0.5f) / H);

        // クリックした部品 A の色をスポイトしたゾーン(スポイト位置つき)。
        public static void SetUpZone(ColorZone z) {
            z.sampleColor = Image()[ClickY * W + ClickX];
            z.sampleUV = ClickUv;
            z.targetColor = new Color(0.1f, 0.8f, 0.2f);
            z.EnsureId();
        }

        // 偽物の AI に要求が来るまで(または確かめが終わるまで)進める。
        public static void WaitForRequest(AutoTuneService.Run run, FakeMaskSuggestService svc) {
            var sw = Stopwatch.StartNew();
            while (svc.queue.Count == 0) {
                Assert.AreNotEqual(AutoTuneService.Status.Failed, run.Status, run.Failure + " " + run.FailureDetail);
                Assert.AreNotEqual(AutoTuneService.Status.Done, run.Status, "要求が来る前に終わった: " + run.LeakDiag);
                Assert.Less(sw.Elapsed.TotalSeconds, 60, "要求が来ない");
                Thread.Sleep(5);
                run.Advance();
            }
        }

        public static void WaitUntilFinished(AutoTuneService.Run run) {
            var sw = Stopwatch.StartNew();
            while (run.Status != AutoTuneService.Status.Done && run.Status != AutoTuneService.Status.Failed) {
                Assert.Less(sw.Elapsed.TotalSeconds, 60, "終わらない: " + run.Status);
                Thread.Sleep(5);
                run.Advance();
            }
        }

        // 証拠 A → 自動の提案 B → 細かい提案 fine(B の上の同じ点)と返して、終わるまで進める。
        public static void Drive(AutoTuneService.Run run, FakeMaskSuggestService svc, System.Func<int, int, bool> fine) {
            Assert.AreEqual(AutoTuneService.Status.WaitingForEvidence, run.Status, run.Failure + " " + run.FailureDetail);
            svc.Finish(Proposal(InA));
            WaitForRequest(run, svc);
            svc.Finish(Proposal(InB));
            WaitForRequest(run, svc);
            svc.Finish(Proposal(fine));
            WaitUntilFinished(run);
        }
    }

    /// <summary>
    /// 自動調整の流れに入れた漏れの確かめ(<see cref="AutoTuneService.Status.CheckingLeaks"/>)。証拠を使って導けたら、導いた値の
    /// 選択の中で証拠の外に塗られた塊ごとに AI の提案を取り直し(自動 → 要る塊だけ細かい。どちらも自動調整の証拠と同じ使い方)、
    /// 別の部品の一部だけが塗られた所を外す範囲にする。確かめの途中の失敗は確かめだけを飛ばし、取り消しは全体を止める。
    /// AI は偽物(<see cref="FakeMaskSuggestService"/>)。判定そのものの細かい規則は golden の test_leak_check.py(ハーネス)。
    /// </summary>
    public class AutoTuneLeakCheckTests {
        private const int W = LeakScene.W, H = LeakScene.H;
        private IMaskSuggestService _saved;
        private FakeMaskSuggestService _svc;
        private NativePixels _pixels;

        [SetUp]
        public void SetUp() {
            _saved = MaskSuggestBridge.Service;
            _svc = new FakeMaskSuggestService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
            _pixels = NativePixels.CopyOf(LeakScene.Image(), W, H);
        }

        [TearDown]
        public void TearDown() {
            AutoTuneService.EvidenceWaitSecondsForTests = 0;
            AutoTuneService.ThrowOnLeakProposalForTests = false;
            _pixels.Release();
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
        }

        private static ColorZone Zone() {
            var z = EditingSession.NewZone(new System.Collections.Generic.List<ColorZone>());
            LeakScene.SetUpZone(z);
            return z;
        }

        private AutoTuneService.Run Start(ColorZone zone)
            => AutoTuneService.Start(zone, _pixels, "k", null, 0, 0, RecolorSettings.Default, null);

        // 確かめを飛ばしても、自動調整の結果は Core の証拠つきの解析そのまま。
        private unsafe void AssertDoneLikeCore(AutoTuneService.Run run, ColorZone zone) {
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status, run.Failure + " " + run.FailureDetail);
            var expected = ZoneAutoTuner.AnalyzeWithEvidence(_pixels.Ptr, W, H, zone.Clone(), LeakScene.Mask(LeakScene.InA), W, H);
            var applied = zone.Clone();
            run.ApplyTo(applied);
            Assert.IsTrue(AutoTuneService.ValuesIntact(applied, expected), "導出値が Core の解析と違う");
            Assert.IsTrue(run.UsedEvidence, "テストの前提: 証拠を使って導けた");
        }

        [Test]
        public void PartlyPaintedOtherPart_IsExcluded_AskingAutoThenFineAtTheSamePoint() {
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);
            Assert.AreEqual(AutoTuneService.Status.CheckingLeaks, run.Status);
            Assert.IsTrue(run.LeakChecked);
            Assert.AreEqual(1, run.LeakBlobCount, "塊は B の塗られた端の 1 つ");
            var coarse = _svc.queue.Peek();
            Assert.AreEqual(MaskSuggestGranularity.Auto, coarse.granularity);
            Assert.AreEqual(MaskSuggestUse.Evidence, coarse.use, "自動調整の証拠と同じ使い方で頼む");
            int bx = (int)(coarse.u * W), by = (int)(coarse.v * H);
            Assert.IsTrue(LeakScene.InB(bx, by) && by >= LeakScene.AEnd + 8, $"点が B の塊の中に無い: {bx},{by}");
            Assert.AreEqual(bx + 0.5f, coarse.u * W, 1e-3f, "UV は画素の中心");

            _svc.Finish(LeakScene.Proposal(LeakScene.InB));
            LeakScene.WaitForRequest(run, _svc);
            var fine = _svc.queue.Peek();
            Assert.AreEqual(MaskSuggestGranularity.Fine, fine.granularity, "外す側に入った塊は、細かい提案で確かめる");
            Assert.AreEqual(MaskSuggestUse.Evidence, fine.use);
            Assert.AreEqual(coarse.u, fine.u, 1e-6f, "同じ点");
            Assert.AreEqual(coarse.v, fine.v, 1e-6f);

            _svc.Finish(LeakScene.Proposal(LeakScene.InB));
            LeakScene.WaitUntilFinished(run);
            AssertDoneLikeCore(run, zone);
            Assert.IsNull(run.LeakSkipReason);
            Assert.AreEqual(1, run.LeakExcludedParts);
            var b = LeakScene.Mask(LeakScene.InB);
            Assert.AreEqual(LeakScene.Count(b), run.LeakExcludedPixels, "外すのは提案から証拠を除いた所(= B)");
            Assert.IsTrue(run.HasLeakExclusion);
            StringAssert.Contains("leak=blobs 1 excluded 1", run.Provenance);
            Assert.AreEqual(1, run.ToInput().leakParts);

            // マスクの寸法にした範囲は B そのもの。そのゾーンの「含める」が立っている所は外さない。
            var ws = new MaskWorkspace();
            try {
                ws.Reset(W, H);
                var region = run.LeakRegionFor(ws, null);
                CollectionAssert.AreEqual(b, region.ToArray());
                region.Release();
                int kept = (LeakScene.AEnd + 30) * W + 40;
                ws.WritableLayer(true, zone.id).Set(kept, true);
                region = run.LeakRegionFor(ws, null);
                Assert.IsFalse(region.ToArray()[kept], "利用者が「ここも塗る」にした所を塗らない側へ戻した");
                Assert.AreEqual(LeakScene.Count(b) - 1, region.CountTrue());
                region.Release();
            } finally {
                ws.Clear();
            }
            run.ReleaseLeakExclusion();
            Assert.IsFalse(run.HasLeakExclusion);
            run.ReleaseLeakExclusion();   // 2 回目は何もしない
            Assert.AreEqual(1, _pixels.RefCount, "確かめが足した原本の参照は返す");
            Assert.IsFalse(AiSuggestBroker.HasLive);
        }

        // 細かい提案がほぼ塗られている(細い部品の上で自動の提案が服全体を返した形)なら外さない。
        [Test]
        public void FineProposalMostlyPainted_IsKept() {
            var zone = Zone();
            var run = Start(zone);
            LeakScene.Drive(run, _svc, (x, y) => LeakScene.InB(x, y) && y < LeakScene.AEnd + 22);
            AssertDoneLikeCore(run, zone);
            Assert.IsTrue(run.LeakChecked);
            Assert.AreEqual(1, run.LeakBlobCount);
            Assert.AreEqual(0, run.LeakExcludedParts);
            Assert.IsFalse(run.HasLeakExclusion);
            Assert.IsNull(run.LeakSkipReason);
            Assert.AreEqual(1, _pixels.RefCount);
        }

        // 提案がクリックした部品の続き(半分以上が証拠と重なる)なら、細かい提案を頼まずに終える。
        [Test]
        public void SamePart_EndsWithoutAskingForTheFineProposal() {
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);
            int before = _svc.requests;
            _svc.Finish(LeakScene.Proposal((x, y) => LeakScene.InA(x, y) || (LeakScene.InB(x, y) && y < LeakScene.AEnd + 20)));
            LeakScene.WaitUntilFinished(run);
            AssertDoneLikeCore(run, zone);
            Assert.AreEqual(before, _svc.requests, "細かい提案を頼んだ");
            Assert.IsFalse(run.HasLeakExclusion);
        }

        [Test]
        public void AiErrorDuringTheCheck_SkipsOnlyTheCheck() {
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);
            _svc.queue.Clear();
            _svc.Set(MaskSuggestPhase.Error);   // 処理待ちは捨てられ、受け取り先には「来ない」が返る
            AssertDoneLikeCore(run, zone);
            StringAssert.StartsWith(AutoTuneService.LeakSkipAi, run.LeakSkipReason);
            StringAssert.Contains("boom", run.LeakSkipReason, "AI のエラーの文言を残す");
            StringAssert.Contains("leak=skipped(ai", run.Provenance);
            Assert.IsFalse(run.HasLeakExclusion);
            Assert.AreEqual(1, _pixels.RefCount);
        }

        [Test]
        public void LostProposal_SkipsOnlyTheCheck() {
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);
            AiSuggestBroker.Cancel(run);   // 届かなかった(取り消し・破棄)
            AssertDoneLikeCore(run, zone);
            Assert.AreEqual(AutoTuneService.LeakSkipNoProposal, run.LeakSkipReason);
            Assert.IsFalse(run.HasLeakExclusion);
        }

        [Test]
        public void Timeout_SkipsOnlyTheCheck() {
            // 期限は提案を頼んだときに決まる。証拠はすぐ返すので、証拠待ちの期限は効かない。
            AutoTuneService.EvidenceWaitSecondsForTests = 0.05;
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);   // 自動の提案を頼んだ(期限は 0.05 秒)
            Thread.Sleep(100);
            run.Advance();
            AssertDoneLikeCore(run, zone);
            Assert.AreEqual(AutoTuneService.LeakSkipTimeout, run.LeakSkipReason);
            Assert.IsFalse(AiSuggestBroker.HasLive, "待っていた提案を取り消す");
            Assert.IsFalse(run.HasLeakExclusion);
            Assert.AreEqual(1, _pixels.RefCount);
        }

        [Test]
        public void ThrowingWhileReceiving_SkipsOnlyTheCheck() {
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);
            AutoTuneService.ThrowOnLeakProposalForTests = true;
            _svc.Finish(LeakScene.Proposal(LeakScene.InB));
            AssertDoneLikeCore(run, zone);
            StringAssert.StartsWith(AutoTuneService.LeakSkipError, run.LeakSkipReason);
            Assert.IsFalse(run.HasLeakExclusion);
        }

        [Test]
        public void Cancel_DuringTheCheck_StopsEverything() {
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);
            run.Cancel();
            Assert.AreEqual(AutoTuneService.Status.Failed, run.Status);
            Assert.AreEqual(AutoTuneService.Failure.Cancelled, run.Failure);
            Assert.IsFalse(AiSuggestBroker.HasLive, "待っていた提案も取り消す");
            Assert.IsFalse(run.HasLeakExclusion);
            Assert.AreEqual(1, _pixels.RefCount);
            Assert.Throws<System.InvalidOperationException>(() => run.ApplyTo(zone), "止めた結果は当てない");
        }

        // 背景の段(判定して外す範囲を作る)を走らせている間に止めても、段が読む物と段の結果(外す範囲)は段の後に返す(漏れない)。
        [Test]
        public void Cancel_WhileTheBackgroundStepRuns_ReturnsEverything() {
            long masksBefore = IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask);
            var zone = Zone();
            var run = Start(zone);
            _svc.Finish(LeakScene.Proposal(LeakScene.InA));
            LeakScene.WaitForRequest(run, _svc);
            _svc.Finish(LeakScene.Proposal(LeakScene.InB));
            LeakScene.WaitForRequest(run, _svc);
            _svc.Finish(LeakScene.Proposal(LeakScene.InB));
            run.Advance();  // 判定の段を始める
            run.Cancel();   // 走っている間(か、終わった直後で受け取る前)
            Assert.AreEqual(AutoTuneService.Failure.Cancelled, run.Failure);
            Assert.IsFalse(run.HasLeakExclusion);
            var sw = Stopwatch.StartNew();
            while (_pixels.RefCount != 1 || IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask) != masksBefore) {
                Assert.Less(sw.Elapsed.TotalSeconds, 30,
                    $"背景の段が返さない: 原本の参照 {_pixels.RefCount}・マスク {IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask) - masksBefore} バイト");
                Thread.Sleep(5);
            }
        }

        // 位置の無いゾーン(色で指定)と、提案が背景まで広がったときは、証拠が無いので確かめない。
        [Test]
        public void WithoutEvidence_NoCheck() {
            var zone = Zone();
            zone.sampleUV = new Vector2(-1f, -1f);
            var run = Start(zone);
            LeakScene.WaitUntilFinished(run);
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status);
            Assert.IsFalse(run.LeakChecked);
            Assert.AreEqual("", run.LeakDiag);

            var flood = Start(Zone());
            _svc.Finish(LeakScene.Proposal(LeakScene.InA, flood: true));
            LeakScene.WaitUntilFinished(flood);
            Assert.AreEqual(AutoTuneService.Status.Done, flood.Status);
            Assert.IsFalse(flood.LeakChecked);
            Assert.AreEqual(1, _pixels.RefCount);
        }
    }

    /// <summary>
    /// ウィンドウが確かめの結果を当てる所: ゾーンの値と同じ「元に戻す」1 回分で、外す範囲をゾーンの「塗らない」へ書く
    /// (右クリックの「ここは塗らない」と同じ入口。塗った範囲の記録が残り、「ここのマスクを消す」で戻せる)。
    /// </summary>
    public class AutoTuneLeakWindowTests {
        private const int W = LeakScene.W, H = LeakScene.H;
        private TestAssets _assets;
        private string _guid;
        private string _prefKey;
        private string _prefBefore;
        private IrocaWindow _win;
        private IMaskSuggestService _saved;
        private FakeMaskSuggestService _svc;

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            string tex = _assets.WritePng("leak", LeakScene.Image(), W, H);
            _guid = AssetDatabase.AssetPathToGUID(tex);
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreEqual(tex, AssetDatabase.GetAssetPath(_win.SourceTexture), "窓がテスト用のテクスチャを開いていない");
            _win.Editing.Masks.Reset(W, H);
            _saved = MaskSuggestBridge.Service;
            _svc = new FakeMaskSuggestService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown() {
            Undo.ClearAll();
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
            if (_win != null) {
                Object.DestroyImmediate(_win);
            }
            if (_prefBefore == null) {
                EditorPrefs.DeleteKey(_prefKey);
            } else {
                EditorPrefs.SetString(_prefKey, _prefBefore);
            }
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                if (!Directory.Exists(dir)) {
                    continue;
                }
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) {
                    File.Delete(f);
                }
            }
            _assets.Dispose();
        }

        private ColorZone ZoneById(string id) => _win.Session.zones.Find(z => z != null && z.id == id);

        private int Excluded(string zoneId) {
            var layer = _win.Editing.Masks.Layer(false, zoneId);
            if (layer == null) {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < W * H; i++) {
                if (layer.Get(i)) {
                    n++;
                }
            }
            return n;
        }

        [Test]
        public void Exclusion_IsWrittenWithTheValues_AndUndoneTogether() {
            Undo.IncrementCurrentGroup();
            int index = _win.Editing.AddZone();
            Undo.IncrementCurrentGroup();
            var zone = _win.Session.zones[index];
            LeakScene.SetUpZone(zone);
            string id = zone.id;
            float tolBefore = zone.tolerance;

            var px = NativePixels.CopyOf(LeakScene.Image(), W, H);
            AutoTuneService.Run run;
            try {
                run = AutoTuneService.Start(zone, px, "k", null, 0, 0, RecolorSettings.Default, null);
                LeakScene.Drive(run, _svc, LeakScene.InB);
            } finally {
                px.Release();
            }
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status, run.Failure + " " + run.FailureDetail);
            Assume.That(run.Result.tolerance, Is.Not.EqualTo(tolBefore).Within(1e-5f), "テストの前提: 自動調整で値が変わる");
            int b = LeakScene.Count(LeakScene.Mask(LeakScene.InB));

            _win.ApplyAutoTune(run);
            Assert.IsFalse(run.HasLeakExclusion, "当てた後に外す範囲を返していない");
            Assert.IsTrue(AutoTuneService.ValuesIntact(ZoneById(id), run.Result), "値が入っていない");
            Assert.AreEqual(b, Excluded(id), "外す範囲がゾーンの「塗らない」に入っていない");
            Assert.AreEqual(1, _win.Editing.Masks.Patches.Count, "塗った範囲の記録が無い(「ここのマスクを消す」で戻せない)");

            Undo.PerformUndo();
            Assert.AreEqual(tolBefore, ZoneById(id).tolerance, 1e-6f, "1 回の Undo で値が戻らない");
            Assert.AreEqual(0, Excluded(id), "1 回の Undo でマスクが戻らない");

            Undo.PerformRedo();
            Assert.IsTrue(AutoTuneService.ValuesIntact(ZoneById(id), run.Result), "やり直しで値が戻らない");
            Assert.AreEqual(b, Excluded(id), "やり直しでマスクが戻らない");

            // 「ここのマスクを消す」: B のどこを押しても、自動調整が入れた範囲をそっくり消す(値はそのまま)。
            Assert.IsTrue(_win.Editing.Masks.ClearMaskAt(id, 20, LeakScene.AEnd + 60, MaskRegionEdit.ClearProbeRadius, null, out var r));
            Assert.AreEqual(b, r.changed);
            Assert.AreEqual(0, Excluded(id));
            Assert.IsTrue(AutoTuneService.ValuesIntact(ZoneById(id), run.Result));
        }
    }

    /// <summary>
    /// 自動化(MCP)の自動調整も、確かめの結果をゾーンの「塗らない」へ書き、結果の zones[] に外した数と画素数を返す。
    /// </summary>
    public class AutoTuneLeakAutomationTests {
        private const int W = LeakScene.W, H = LeakScene.H;
        private TestAssets _assets;
        private IMaskSuggestService _saved;
        private FakeMaskSuggestService _svc;

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            IrocaAutomation.AssumeNdmfForTests = true;
            _saved = MaskSuggestBridge.Service;
            _svc = new FakeMaskSuggestService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
        }

        [TearDown]
        public void TearDown() {
            IrocaAutomation.CancelRecolorJob();
            IrocaAutomation.AssumeNdmfForTests = false;
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
            _assets?.Dispose();
        }

        // 提案を求められるたびに順に返し、終わるまで問い合わせ直す。
        private string Answer(string json, params MaskSuggestProposal[] proposals) {
            foreach (var p in proposals) {
                var wait = Stopwatch.StartNew();
                while (_svc.queue.Count == 0) {
                    Assert.IsTrue(json.Contains("\"pending\":true"), json);
                    Assert.Less(wait.Elapsed.TotalSeconds, 60, json);
                    Thread.Sleep(5);
                    json = IrocaAutomation.RecolorStatus();
                }
                _svc.Finish(p);
                json = IrocaAutomation.RecolorStatus();
            }
            var done = Stopwatch.StartNew();
            while (json.Contains("\"pending\":true")) {
                Assert.Less(done.Elapsed.TotalSeconds, 60, json);
                Thread.Sleep(5);
                json = IrocaAutomation.RecolorStatus();
            }
            return json;
        }

        [Test]
        public void AutoTune_WritesTheExclusion_AndReportsIt() {
            string tex = _assets.WritePng("leak", LeakScene.Image(), W, H);
            var uv = LeakScene.ClickUv;
            string at = $"[{uv.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
                + $"{uv.y.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
            string request = "{\"texture\":\"" + tex + "\",\"apply\":\"preview\",\"changes\":[{\"name\":\"red\",\"at\":" + at
                + ",\"to\":\"#1A2B5C\"}],\"masks\":[],\"autoTune\":true}";
            string json = Answer(IrocaAutomation.Recolor(request),
                LeakScene.Proposal(LeakScene.InA), LeakScene.Proposal(LeakScene.InB), LeakScene.Proposal(LeakScene.InB));
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            var zone = r.zones.Single();
            Assert.AreEqual("evidence", zone.autoTune);
            int b = LeakScene.Count(LeakScene.Mask(LeakScene.InB));
            Assert.AreEqual(1, zone.autoTuneExcludedParts, json);
            Assert.AreEqual(b, zone.autoTuneExcludedPixels, json);
            Assert.AreEqual(b / (float)(W * H), zone.excludedFraction, 1e-4f, "外した範囲がゾーンの「塗らない」に入っていない");
            Assert.IsFalse(AiSuggestBroker.HasLive);
        }
    }
}
