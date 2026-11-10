// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Linq;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// 自動化 API(MCP)のマスク操作(右クリックと同じ操作)と、iroca_inspect の情報(今のゾーン・マスク・指定した場所の様子)。
    /// AI は偽物(<see cref="FakeMaskSuggestService"/>)に差し替えて、提案の範囲を決めて試す。
    /// </summary>
    public class AutomationMaskTests {
        private const int W = 32, H = 16;
        private static readonly Color32 Red = new Color32(200, 30, 30, 255);
        private static readonly Color32 Blue = new Color32(30, 40, 200, 255);
        private const string RedZone = "[{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]";

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

        private string AllRed() => _assets.WritePng("red", TestAssets.Solid(W, H, Red), W, H);

        // 左半分が赤・右半分が青。
        private string Halves() {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    px[y * W + x] = x < W / 2 ? Red : Blue;
                }
            }
            return _assets.WritePng("halves", px, W, H);
        }

        private static MaskSuggestProposal Half(bool left, bool flood = false) {
            var m = new bool[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    m[y * W + x] = x < W / 2 == left;
                }
            }
            return new MaskSuggestProposal { mask = NativeBools.CopyOf(m, W, H), width = W, height = H, floodWarning = flood };
        }

        private static string Request(string texture, string changes, string masks, string apply = "preview",
            bool autoTune = false, string extra = "")
            => "{\"texture\":\"" + texture + "\",\"apply\":\"" + apply + "\",\"changes\":" + changes + ",\"masks\":" + masks
               + extra + ",\"autoTune\":" + (autoTune ? "true" : "false") + "}";

        // 依頼を出し、提案を順に返して、終わるまで問い合わせ直す。
        private static string Run(FakeMaskSuggestService svc, string request, params MaskSuggestProposal[] proposals) {
            string json = IrocaAutomation.Recolor(request);
            foreach (var p in proposals) {
                Assert.IsTrue(json.Contains("\"pending\":true"), json);
                svc.Finish(p);
                json = IrocaAutomation.RecolorStatus();
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (json.Contains("\"pending\":true")) {
                Assert.Less(watch.Elapsed.TotalSeconds, 60, json);
                System.Threading.Thread.Sleep(5);
                json = IrocaAutomation.RecolorStatus();
            }
            return json;
        }

        private IrocaAutomation.RecolorTextureResult RunOk(string request, params MaskSuggestProposal[] proposals) {
            string json = Run(_svc, request, proposals);
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            return r;
        }

        private void Fails(string request, string expected) {
            string json = IrocaAutomation.Recolor(request);
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsFalse(r.ok, json);
            StringAssert.Contains(expected, r.error);
        }

        // ─── AI の範囲に当てる ───

        [Test]
        public void DontPaint_KeepsTheAiPartUnpainted() {
            var r = RunOk(Request(AllRed(), RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5]}]"), Half(left: true));
            Assert.AreEqual(0.5f, r.changedFraction, 1e-4f);
            var edit = r.maskEdits.Single();
            Assert.AreEqual("red", edit.zone);
            Assert.AreEqual("dont_paint", edit.op);
            Assert.AreEqual("ai", edit.by);
            Assert.AreEqual(0.5f, edit.regionFraction, 1e-4f);
            Assert.AreEqual(W * H / 2, edit.changedPixels);
            Assert.AreEqual(0.5f, r.zones.Single().excludedFraction, 1e-4f);
        }

        [Test]
        public void Only_PaintsJustTheAiPart() {
            var r = RunOk(Request(AllRed(), RedZone, "[{\"op\":\"only\",\"at\":[0.25,0.5],\"zone\":\"red\"}]"), Half(left: true));
            Assert.AreEqual(0.5f, r.changedFraction, 1e-4f);
        }

        [Test]
        public void Paint_AddsAPartOfAnotherColor() {
            var r = RunOk(Request(Halves(), RedZone, "[{\"op\":\"paint\",\"at\":[0.75,0.5],\"zone\":\"red\"}]"), Half(left: false));
            Assert.AreEqual(1f, r.changedFraction, 1e-4f, "含めた青い側も塗る");
            Assert.AreEqual(0.5f, r.zones.Single().includedFraction, 1e-4f);
        }

        [Test]
        public void Operations_AreAppliedInOrder_ClearUndoesTheEarlierOne() {
            var r = RunOk(Request(AllRed(), RedZone,
                    "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5]},{\"op\":\"clear\",\"at\":[0.25,0.5]}]"),
                Half(left: true), Half(left: true));
            Assert.AreEqual(1f, r.changedFraction, 1e-4f);
            Assert.AreEqual(2, r.maskEdits.Count);
        }

        [Test]
        public void Common_DontPaint_AppliesToEveryZone() {
            string changes = "[{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"},{\"name\":\"blue\",\"from\":\"#1E28C8\",\"to\":\"#FFAA00\"}]";
            var r = RunOk(Request(Halves(), changes, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5],\"common\":true}]"), Half(left: true));
            Assert.AreEqual(0.5f, r.changedFraction, 1e-4f, "赤い側は共通の除外で塗らない");
            Assert.IsEmpty(r.maskEdits.Single().zone);
        }

        [Test]
        public void Masks_AreAppliedBeforeAutoTune() {
            var r = RunOk(Request(AllRed(), RedZone, "[{\"op\":\"only\",\"at\":[0.25,0.5]}]", autoTune: true), Half(left: true));
            Assert.AreEqual("conventional", r.zones.Single().autoTune);
            Assert.AreEqual(0.5f, r.changedFraction, 1e-4f);
        }

        [Test]
        public void FloodWarning_IsReported() {
            var r = RunOk(Request(AllRed(), RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5]}]"), Half(left: true, flood: true));
            Assert.IsTrue(r.warnings.Any(w => w.Contains("spread to the background")));
        }

        [Test]
        public void EmptyProposal_FailsInsteadOfExcludingEverything() {
            // 空の提案で「この部分だけ」を当てると全体が除外になる(右クリックも当てない)。
            var empty = new MaskSuggestProposal { mask = NativeBools.CopyOf(new bool[W * H], W, H), width = W, height = H };
            string json = Run(_svc, Request(AllRed(), RedZone, "[{\"op\":\"only\",\"at\":[0.25,0.5]}]"), empty);
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsFalse(r.ok, json);
            StringAssert.Contains("empty region", r.error);
        }

        // ─── 本物の AI ───

        [UnityTest, Timeout(1200000)]
        public IEnumerator RealAi_DontPaint_FinishesThroughPolling() {
            // 偽物に差し替える前のサービス(本物)で、問い合わせ直しだけで最後まで進むことを見る。
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
            if (_saved == null || !MaskSuggestBridge.ModelFilesPresent || !_saved.TryEnsureModels()) {
                Assert.Ignore("AI(Sentis とモデル)の無いホスト");
            }
            string request = Request(Halves(), "[{\"name\":\"red\",\"at\":[0.25,0.5],\"to\":\"#1A2B5C\"}]",
                "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5],\"zone\":\"red\"}]");
            // -nographics では推論エンジンが GPU のカーネルが無いと LogError してから CPU へ切り替える(AutomationTests と同じ)。
            LogAssert.ignoreFailingMessages = true;
            string json;
            try {
                json = IrocaAutomation.Recolor(request);
                double deadline = EditorApplication.timeSinceStartup + 600;
                while (json.Contains("\"pending\":true")) {
                    Assert.Less(EditorApplication.timeSinceStartup, deadline, json);
                    yield return null;
                    json = IrocaAutomation.RecolorStatus();
                }
            } finally { LogAssert.ignoreFailingMessages = false; }
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            var edit = r.maskEdits.Single();
            Assert.Greater(edit.regionFraction, 0f, json);
            Assert.Less(r.changedFraction, 0.5f, "クリックした部分を塗らない: " + json);
        }

        // ─── 依頼の確かめ ───

        [TestCase("[{\"op\":\"nope\",\"at\":[0.5,0.5]}]", "unknown mask op")]
        [TestCase("[{\"op\":\"only\"}]", "'at' must be")]
        [TestCase("[{\"op\":\"only\",\"at\":[1.5,0.5]}]", "'at' must be")]
        [TestCase("[{\"op\":\"only\",\"at\":[0.5,0.5],\"zone\":\"nope\"}]", "no zone named 'nope'")]
        [TestCase("[{\"op\":\"only\",\"at\":[0.5,0.5],\"common\":true}]", "common:true works only")]
        [TestCase("[{\"op\":\"clear\",\"at\":[0.5,0.5],\"common\":true,\"zone\":\"red\"}]", "not both")]
        [TestCase("[{\"op\":\"paint\",\"at\":[0.5,0.5],\"by\":\"other_meshes\"}]", "other_meshes")]
        [TestCase("[{\"op\":\"only\",\"at\":[0.5,0.5],\"by\":\"nope\"}]", "unknown mask 'by'")]
        [TestCase("[{\"op\":\"only\",\"at\":[0.5,0.5],\"size\":\"nope\"}]", "unknown mask size")]
        public void BadRequests_AreRefused(string masks, string expected) {
            Fails(Request(AllRed(), RedZone, masks), expected);
        }

        [Test]
        public void SeveralZones_WithoutZoneName_IsRefused() {
            string changes = "[{\"name\":\"a\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"},{\"name\":\"b\",\"from\":\"#1E28C8\",\"to\":\"#FFAA00\"}]";
            Fails(Request(Halves(), changes, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5],\"by\":\"island\"}]"), "pass 'zone'");
        }

        [Test]
        public void Ai_WithoutSentis_SuggestsTheMeshIsland() {
            MaskSuggestBridge.Service = null;
            Fails(Request(AllRed(), RedZone, "[{\"op\":\"only\",\"at\":[0.25,0.5]}]"), "by:'island'");
        }

        // ─── メッシュの島に当てる ───

        // 離れた 2 枚の四角(= 2 つの島)。左下と右上。
        private static Mesh TwoQuads(float offset) {
            var m = new Mesh { name = "quads" + offset };
            m.vertices = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 1, 0), new Vector3(2, 1, 0),
            };
            m.uv = new[] {
                new Vector2(0.1f, 0.1f), new Vector2(0.4f, 0.1f), new Vector2(0.4f, 0.4f), new Vector2(0.1f, 0.4f),
                new Vector2(0.6f, 0.6f), new Vector2(0.9f, 0.6f), new Vector2(0.9f, 0.9f), new Vector2(0.6f, 0.9f),
            };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return m;
        }

        private static GameObject MeshObject(string name, Mesh mesh, Material mat) {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        [Test]
        public void Island_DontPaint_LeavesThatIslandUnpainted() {
            string src = _assets.WritePng("red64", TestAssets.Solid(64, 64, Red), 64, 64);
            var mat = new Material(Shader.Find("Standard")) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(src) };
            var go = MeshObject("IrocaIslandTest", TwoQuads(0), mat);
            try {
                var r = RunOk(Request(src, RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.25],\"by\":\"island\"}]"));
                var edit = r.maskEdits.Single();
                Assert.AreEqual("island", edit.by);
                Assert.Greater(edit.regionFraction, 0.05f);
                Assert.Less(edit.regionFraction, 0.25f, "もう 1 つの島まで広げない");
                Assert.AreEqual(1f - edit.regionFraction, r.changedFraction, 1e-3f);
            } finally {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mat);
            }
        }

        [Test]
        public void Island_NoIslandThere_Fails() {
            string src = _assets.WritePng("red64", TestAssets.Solid(64, 64, Red), 64, 64);
            var mat = new Material(Shader.Find("Standard")) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(src) };
            var go = MeshObject("IrocaIslandTest", TwoQuads(0), mat);
            try {
                string json = Run(_svc, Request(src, RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.75,0.25],\"by\":\"island\"}]"));
                StringAssert.Contains("no UV island", JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json).error);
            } finally {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mat);
            }
        }

        [Test]
        public void Island_WithoutMesh_Fails() {
            string json = Run(_svc, Request(AllRed(), RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5],\"by\":\"island\"}]"));
            StringAssert.Contains("no readable mesh", JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json).error);
        }

        [Test]
        public void Island_WithoutMesh_Fails_AndReturnsThePixelsTheAutoTuneHeld() {
            // マスク操作で失敗した依頼でも、自動調整が足した原本の参照を返す(以前は 8K で 256MB がファイナライザまで残った)。
            string src = AllRed();
            long before = IrocaNativeMemory.LiveBytesOf(NativeUsage.Pixels);
            string json = Run(_svc, Request(src, RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5],\"by\":\"island\"}]", autoTune: true));
            StringAssert.Contains("no readable mesh", JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json).error);
            Assert.AreEqual(before, IrocaNativeMemory.LiveBytesOf(NativeUsage.Pixels), "原本の参照が残っている");
        }

        // ─── AI のエラーと、受け取り先の例外 ───

        [Test]
        public void AiErrorRightAtTheRequest_ReportsTheReason_AndReturnsThePixels() {
            // 受理した直後に AI がエラーになったとき(Sentis のデコーダが同期で投げたなど)、"returned nothing" ではなく
            // 本当の理由を返し、マスク操作が持った原本も返す。
            string src = AllRed();
            long before = IrocaNativeMemory.LiveBytesOf(NativeUsage.Pixels);
            _svc.errorOnRequest = true;
            string json = Run(_svc, Request(src, RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5]}]"));
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsFalse(r.ok, json);
            StringAssert.Contains("boom", r.error);
            Assert.AreEqual(before, IrocaNativeMemory.LiveBytesOf(NativeUsage.Pixels), "原本の参照が残っている");
        }

        [Test]
        public void ExceptionInTheMaskReceiver_FailsAtOnceWithTheReason() {
            // 受け取り先が投げても、AI 待ちの時間切れ(180 秒)まで待たせず、その場で理由を返す。
            AutomationMaskEdits.ThrowOnProposalForTests = true;
            try {
                string json = Run(_svc, Request(AllRed(), RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5]}]"), Half(left: true));
                var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
                Assert.IsFalse(r.ok, json);
                StringAssert.Contains("thrown for tests", r.error);
            } finally {
                AutomationMaskEdits.ThrowOnProposalForTests = false;
            }
        }

        [Test]
        public void ExceptionInTheAutoTuneReceiver_SkipsThatZoneAtOnce() {
            // 自動調整の証拠の受け取り先が投げても、180 秒待たせず、そのゾーンの調整を飛ばして理由を知らせる。
            AutoTuneService.ThrowOnEvidenceForTests = true;
            try {
                var r = RunOk(Request(AllRed(), "[{\"name\":\"red\",\"at\":[0.25,0.5],\"to\":\"#1A2B5C\"}]", "[]", autoTune: true),
                    Half(left: true));
                StringAssert.StartsWith("skipped: analysis failed", r.zones.Single().autoTune);
                Assert.IsTrue(r.warnings.Any(w => w.Contains("thrown for tests")), string.Join("\n", r.warnings));
            } finally {
                AutoTuneService.ThrowOnEvidenceForTests = false;
            }
        }

        // ─── 保存と、iroca_inspect の情報 ───

        private void WithAvatar(string src, System.Action body) {
            bool hadRecipeFolder = AssetDatabase.IsValidFolder(RecipeStore.DefaultFolder);
            bool hadIrocaFolder = AssetDatabase.IsValidFolder("Assets/Iroca");
            var root = new GameObject("IrocaMaskTestAvatar");
            foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies()) {
                var t = a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false);
                if (t != null) { root.AddComponent(t); break; }
            }
            var mat = new Material(Shader.Find("Standard")) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(src) };
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Body";
            quad.transform.SetParent(root.transform);
            quad.GetComponent<Renderer>().sharedMaterial = mat;
            try { body(); } finally {
                foreach (var c in root.GetComponentsInChildren<IrocaRecolor>(true)) {
                    foreach (var recipe in c.recipes) {
                        if (recipe != null) {
                            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(recipe));
                        }
                    }
                }
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mat);
                if (!hadRecipeFolder) {
                    AssetDatabase.DeleteAsset(RecipeStore.DefaultFolder);
                }
                if (!hadIrocaFolder) {
                    AssetDatabase.DeleteAsset("Assets/Iroca");
                }
            }
        }

        private static IrocaAutomation.TextureInfo InspectTexture(string src, float[] points, bool coverage,
            out IrocaAutomation.InspectResult result) {
            string json = IrocaAutomation.Inspect(src, 0, points, coverage);
            result = JsonUtility.FromJson<IrocaAutomation.InspectResult>(json);
            Assert.IsTrue(result.ok, json);
            return result.textures.Single();
        }

        [Test]
        public void Avatar_SavesTheMasks_AndInspectReportsThem() {
            string src = AllRed();
            WithAvatar(src, () => {
                var r = RunOk(Request(src, RedZone, "[{\"op\":\"dont_paint\",\"at\":[0.25,0.5]}]", "avatar"), Half(left: true));
                Assert.IsNotEmpty(r.recipe);

                // 保存したマスクは、次の依頼の土台にも残る(マスク操作なしで頼み直しても左は塗らない)。
                var again = RunOk(Request(src, "[{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#FFAA00\"}]", "[]", "preview"));
                Assert.AreEqual(0.5f, again.changedFraction, 1e-4f);

                var info = InspectTexture(src, new[] { 0.25f, 0.5f, 0.75f, 0.5f }, false, out _);
                Assert.AreEqual(r.recipe, info.zonesFrom);
                var zone = info.zones.Single();
                Assert.AreEqual("red", zone.name);
                Assert.AreEqual("#C81E1E", zone.from);
                Assert.AreEqual("#1A2B5C", zone.to);
                Assert.AreEqual(0.5f, zone.excludedFraction, 1e-4f);
                Assert.AreEqual(0.5f, zone.approxCoverage, 0.02f);

                var unpainted = info.points[0];
                Assert.AreEqual("#C81E1E", unpainted.hex);
                Assert.AreEqual(255, unpainted.alpha);
                Assert.IsFalse(unpainted.changed);
                Assert.IsEmpty(unpainted.paintedBy);
                Assert.IsTrue(unpainted.zones.Single().excluded);
                Assert.AreEqual(0f, unpainted.zones.Single().strength, 1e-4f);

                var painted = info.points[1];
                Assert.IsTrue(painted.changed);
                Assert.AreEqual("red", painted.paintedBy);
                Assert.AreNotEqual(painted.hex, painted.afterHex);
                Assert.IsFalse(painted.zones.Single().excluded);
                Assert.Greater(painted.zones.Single().strength, 0.5f);
            });
        }

        [Test]
        public void Inspect_RecipeTarget_ShowsThatRecipe() {
            string src = AllRed();
            WithAvatar(src, () => {
                var r = RunOk(Request(src, RedZone, "[]", "avatar"));
                string json = IrocaAutomation.Inspect(r.recipe);
                var result = JsonUtility.FromJson<IrocaAutomation.InspectResult>(json);
                Assert.IsTrue(result.ok, json);
                Assert.AreEqual("texture", result.kind);
                Assert.AreEqual(r.recipe, result.textures.Single().zonesFrom);
            });
        }

        [Test]
        public void Inspect_HiddenZone_IsReported() {
            // 先のゾーンが同じ色を全部取ると、後のゾーンは何も塗らない(AI には理由が見えない)。
            string src = AllRed();
            string changes = "[{\"name\":\"first\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"},{\"name\":\"second\",\"from\":\"#C81E1E\",\"to\":\"#FFAA00\"}]";
            WithAvatar(src, () => {
                RunOk(Request(src, changes, "[]", "avatar"));
                var info = InspectTexture(src, null, true, out var result);
                Assert.AreEqual(1f, info.zones[0].approxCoverage, 0.01f);
                Assert.AreEqual(0f, info.zones[1].approxCoverage, 0.01f);
                Assert.IsTrue(result.warnings.Any(w => w.Contains("'second'") && w.Contains("paints almost nothing")),
                    string.Join("\n", result.warnings));
            });
        }

        [Test]
        public void Inspect_PointsWithoutZones_ReturnsColorAndIsland() {
            string src = _assets.WritePng("red64", TestAssets.Solid(64, 64, Red), 64, 64);
            var mat = new Material(Shader.Find("Standard")) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(src) };
            var go = MeshObject("IrocaIslandTest", TwoQuads(0), mat);
            try {
                var info = InspectTexture(src, new[] { 0.25f, 0.25f, 0.75f, 0.75f, 0.75f, 0.25f }, false, out _);
                Assert.IsEmpty(info.zones);
                Assert.AreEqual("IrocaIslandTest", info.meshes.Single());
                Assert.AreEqual("#C81E1E", info.points[0].hex);
                Assert.IsFalse(info.points[0].changed);
                Assert.AreEqual("IrocaIslandTest", info.points[0].mesh);
                Assert.AreNotEqual(info.points[0].island, info.points[1].island, "別の島");
                Assert.AreEqual(-1, info.points[2].island, "島の無い場所");
            } finally {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mat);
            }
        }

        [Test]
        public void Inspect_ReportsTheEnvironment() {
            string json = IrocaAutomation.Inspect(null);
            var r = JsonUtility.FromJson<IrocaAutomation.InspectResult>(json);
            Assert.IsTrue(r.ok, json);
            Assert.AreEqual("supported", r.ndmf, "AssumeNdmfForTests");
            Assert.IsNotEmpty(r.ai);
            Assert.IsFalse(r.recolorRunning);
            Assert.IsNotNull(r.presets);

            MaskSuggestBridge.Service = null;
            r = JsonUtility.FromJson<IrocaAutomation.InspectResult>(IrocaAutomation.Inspect(null));
            Assert.AreEqual("no_sentis", r.ai);
        }

        [TestCase(new[] { 0.5f }, "pairs")]
        [TestCase(new[] { 1.5f, 0.5f }, "within 0..1")]
        public void Inspect_BadPoints_AreRefused(float[] points, string expected) {
            var r = JsonUtility.FromJson<IrocaAutomation.InspectResult>(IrocaAutomation.Inspect(AllRed(), 0, points, false));
            Assert.IsFalse(r.ok);
            StringAssert.Contains(expected, r.error);
        }
    }
}
