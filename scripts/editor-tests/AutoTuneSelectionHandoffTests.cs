// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 窓の「自動調整」で、漏れの確かめが回した選択をプレビューの選択キャッシュへ引き渡す配線(IrocaWindow.AutoTune の
    /// PlanSelectionHandoff・HandOverAutoTuneSelection → AutoTuneService.Run.HandOverSelection → Core の
    /// AutoTuneFlow.HandOverSelection・SelectionCache.TryAdopt)。
    /// (a) 引き渡した選択で作った確定段の出力が、キャッシュを使わない経路(保存形式から塗る SessionRecolor)・キャッシュを
    ///     空にして作り直した確定段と全画素で同じ。
    /// (b) 当てた直後(プレビューを作り直す前)に、プレビューの選択キャッシュが、そのゾーンの「今の値で塗るときの鍵」の
    ///     選択を持っている(プレビューを回していないので、引き渡し以外からは入らない)。
    /// (c) 自動調整を始めた後にプレビューの選択キャッシュが空にされた(原本が替わったかもしれない)ら引き渡さない。
    /// AI は偽物(<see cref="FakeMaskSuggestService"/>)なので、Sentis の無い既定の EditMode のホストでも証拠つきの解析と
    /// 漏れの確かめ(SimulateSelection)を通る。通ったことは各テストで確かめる(UsedEvidence・LeakChecked)。
    /// 絵は漏れの確かめの合成の絵(LeakScene)。細かい提案はほぼ塗られている形にして外す範囲を付けない(付けるとゾーンの
    /// 「塗らない」が変わり、引き渡した選択は除外の比べ直しで使われない)。
    /// </summary>
    public class AutoTuneSelectionHandoffTests {
        private const int W = LeakScene.W, H = LeakScene.H;
        private TestAssets _assets;
        private string _path;
        private string _guid;
        private Texture2D _texture;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaWindow _win;
        private IMaskSuggestService _saved;
        private FakeMaskSuggestService _svc;

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            _path = _assets.WritePng("handoff", LeakScene.Image(), W, H);
            _guid = AssetDatabase.AssetPathToGUID(_path);
            _texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_path);
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
            _saved = MaskSuggestBridge.Service;
            _svc = new FakeMaskSuggestService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_texture, _win.SourceTexture, "窓がテスト用のテクスチャを開いていない");
            _win.Editing.Masks.Reset(W, H);
            _win.Session.zones.Clear();
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown() {
            Undo.ClearAll();
            if (_win != null) {
                _win.CancelAutoTune();
                Object.DestroyImmediate(_win);
            }
            _win = null;
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
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

        // 細かい提案: B の塗られた端だけ(ほぼ塗られている)なので外さない(AutoTuneLeakCheckTests の FineProposalMostlyPainted_IsKept)。
        private static bool KeptFine(int x, int y) => LeakScene.InB(x, y) && y < LeakScene.AEnd + 22;

        // 部品 A をスポイトしたゾーンを足し、確定段を 1 回作る(原本を読み、確定段のキャッシュが原本のハッシュを覚える。
        // 引き取るときに突き合わせる)。
        private ColorZone AddZoneAndPreview() {
            int index = _win.Editing.AddZone();
            var zone = _win.Session.zones[index];
            LeakScene.SetUpZone(zone);
            RunFullStage();
            return zone;
        }

        // 窓の「自動調整」でゾーンを調整し、偽物の AI に答えて終わるまで進める(当てる前で止める)。
        private AutoTuneService.Run StartAndDrive(ColorZone zone) {
            _win.RunAutoTune(zone);
            var run = _win.RunningAutoTune;
            Assert.IsNotNull(run, "自動調整が始まっていない");
            Assert.AreEqual(AutoTuneService.Status.WaitingForEvidence, run.Status, run.Failure + " " + run.FailureDetail);
            // 証拠 = A、漏れの確かめの自動の提案 = B、細かい提案 = B の塗られた端。
            var answers = new System.Func<int, int, bool>[] { LeakScene.InA, LeakScene.InB, KeptFine };
            int answered = 0;
            var sw = Stopwatch.StartNew();
            while (run.Status != AutoTuneService.Status.Done && run.Status != AutoTuneService.Status.Failed) {
                Assert.Less(sw.Elapsed.TotalSeconds, 60, "終わらない: " + run.Status);
                if (_svc.queue.Count > 0 && _svc.phase == MaskSuggestPhase.Decoding) {
                    Assert.Less(answered, answers.Length, "思っていたより多く提案を頼んだ");
                    _svc.Finish(LeakScene.Proposal(answers[answered]));
                    answered++;
                    continue;
                }
                Thread.Sleep(5);
                run.Advance();
            }
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status, run.Failure + " " + run.FailureDetail);
            Assert.IsTrue(run.UsedEvidence, "テストの前提: 証拠を使って導けた(漏れの確かめが選択を回す道)");
            Assert.IsTrue(run.LeakChecked, "テストの前提: 漏れの確かめが選択を回した");
            Assert.AreEqual(0, run.LeakExcludedParts, "テストの前提: 外す範囲を付けない");
            return run;
        }

        // 窓に当てさせる(EditorApplication.update の代わりに 1 回呼ぶ)。
        private void Apply(AutoTuneService.Run run) {
            Assert.AreSame(run, _win.RunningAutoTune);
            _win.TickAutoTune();
            Assert.IsNull(_win.RunningAutoTune, "当てていない");
        }

        // プレビューの確定段がそのゾーンを今の値で塗るときに引く選択を、キャッシュが持っているか(確定段と同じ鍵・同じマスク)。
        private unsafe bool CacheHoldsSelectionFor(string zoneId) {
            var live = _win.Session.zones.Find(z => z != null && z.id == zoneId);
            Assert.IsNotNull(live);
            var z = live.Clone();
            RecolorValueRanges.ClampZone(z);   // 確定段も鍵を作る前に丸める(ProcessPixelsArrayCore の冒頭)
            var st = RecolorSettings.From(_win.Session);
            var snap = _win.Editing.Masks.ToSnapshot();
            try {
                string key = PixelProcessor.BuildSelectionKey(z, st.edgeFeather, st.antiAliasCleanup, st.holeFillPasses,
                    st.holeFillMinNeighbors, st.relaxedSatMin, snap.width, snap.height,
                    PixelProcessor.IsMixtureBandZone(z, st.useDecontamination, st.edgeFeather));
                snap.zones.TryGetValue(zoneId, out var zoneLayer);
                return _win.Preview.Pipeline.Selection.TryCopy(zoneId, key, W, H, null, snap.common, zoneLayer, null, out _);
            } finally {
                snap.Release();
            }
        }

        // 確定段を作り直し、終わるまでメインスレッドへの戻りを捌いて出力を読む(WindowOutputParityTests と同じ待ち方)。
        private Color32[] RunFullStage() {
            var preview = _win.Preview;
            preview.GeneratePreviewAsync();
            var watch = Stopwatch.StartNew();
            int idle = 0;
            while (idle < 3) {
                Assert.Less(watch.Elapsed.TotalSeconds, 60, "プレビューが終わらない");
                PreviewJobMainThread.Drain();
                idle = preview.IsGenerating ? 0 : idle + 1;
                Thread.Sleep(2);
            }
            var output = preview.FullOutput;
            Assert.IsNotNull(output, "確定段の出力が無い");
            Assert.AreEqual(W * H, output.Length);
            return output.ToArray();
        }

        // 選択キャッシュを使わない経路(非破壊ビルドと同じ入口。保存形式から塗る)。
        private Color32[] BySessionRecolor() {
            var state = JsonUtility.FromJson<IrocaSessionState>(JsonUtility.ToJson(_win.Editing.ToSaveState()));
            Assert.IsTrue(_win.Preview.TryGetTrueSourcePixels(_texture, out var src, out int w, out int h), "原本を読めない");
            Assert.AreEqual((W, H), (w, h));
            var px = src.ToArray();
            Assert.IsTrue(SessionRecolor.Apply(px, w, h, state), "有効なゾーンが無い");
            return px;
        }

        private static void AssertSamePixels(Color32[] expected, Color32[] actual, string what) {
            Assert.AreEqual(expected.Length, actual.Length, what + ": 画素数");
            int diff = 0, first = -1;
            for (int i = 0; i < expected.Length; i++) {
                var a = expected[i];
                var b = actual[i];
                if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) {
                    if (first < 0) {
                        first = i;
                    }
                    diff++;
                }
            }
            Assert.AreEqual(0, diff, $"{what}: {diff} 画素が違う(最初は {first})");
        }

        [Test]
        public void HandedOverSelection_IsInThePreviewCache_AndPaintsTheSamePixels() {
            var zone = AddZoneAndPreview();
            var run = StartAndDrive(zone);
            string id = zone.id;
            Apply(run);
            Assert.IsTrue(AutoTuneService.ValuesIntact(_win.Session.zones.Find(z => z.id == id), run.Result), "値が入っていない");

            // (b) プレビューをまだ作り直していないのに、今の値で塗るときの選択がある = 引き渡された。
            Assert.IsTrue(CacheHoldsSelectionFor(id), "漏れの確かめで回した選択がプレビューの選択キャッシュへ引き渡されていない");

            // (a) 引き渡した選択で作った確定段 = キャッシュを使わない経路 = キャッシュを空にして作り直した確定段。
            var withHandoff = RunFullStage();
            var expected = BySessionRecolor();
            AssertSamePixels(expected, withHandoff, "引き渡した選択で作った確定段");
            _win.Preview.Pipeline.Selection.Clear();
            AssertSamePixels(withHandoff, RunFullStage(), "選択キャッシュを空にして作り直した確定段");
        }

        [Test]
        public void CacheClearedAfterTheStart_IsNotHandedOver() {
            var zone = AddZoneAndPreview();
            var run = StartAndDrive(zone);
            string id = zone.id;
            // 自動調整を始めた後に原本が替わった(プレビューが選択キャッシュを空にした)のと同じ。
            _win.Preview.Pipeline.Selection.Clear();
            Apply(run);
            Assert.IsTrue(AutoTuneService.ValuesIntact(_win.Session.zones.Find(z => z.id == id), run.Result), "値が入っていない");
            Assert.IsFalse(CacheHoldsSelectionFor(id), "始めた後に空にされたキャッシュへ引き渡した");

            // 引き渡さなくても、出力はキャッシュを使わない経路と同じ。
            AssertSamePixels(BySessionRecolor(), RunFullStage(), "引き渡さなかったときの確定段");
        }
    }
}
