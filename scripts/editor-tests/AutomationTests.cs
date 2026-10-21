// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 自動化 API（<see cref="IrocaAutomation"/>。MCP・batchmode の入口）が、UI でプリセットを読み込んで
    /// 書き出したときと同じ処理を通るか。プリセットに入っているマスク（除外・含める）も当たること。
    /// </summary>
    public class AutomationTests
    {
        private const int W = 16, H = 8;
        private static readonly Color32 Red = new Color32(200, 30, 30, 255);

        private TestAssets _assets;

        [SetUp]
        public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown() => _assets?.Dispose();

        [Test]
        public void RecolorByPreset_WithoutMask_ChangesEveryPixel()
        {
            var px = Run(RedToBlue(out _), out var result);
            Assert.AreEqual(W * H, result.changedPixels);
            Assert.IsTrue(px.All(c => !Same(c, Red)), "全部変わること");
        }

        [Test]
        public void RecolorByPreset_AppliesCommonExclusionMask()
        {
            var preset = RedToBlue(out _);
            preset.maskWidth = W;
            preset.maskHeight = H;
            preset.commonMaskBase64 = MaskRle.Encode(LeftHalf(W, H), W, H);
            AssertOnlyRightHalfChanged(Run(preset, out _));
        }

        [Test]
        public void RecolorByPreset_AppliesZoneExclusionMask()
        {
            var preset = RedToBlue(out var zone);
            preset.maskWidth = W;
            preset.maskHeight = H;
            preset.zoneMasks.Add(new ZoneMaskEntry { zoneId = zone.id, maskBase64 = MaskRle.Encode(LeftHalf(W, H), W, H) });
            AssertOnlyRightHalfChanged(Run(preset, out _));
        }

        [Test]
        public void RecolorByPreset_ScalesMaskOfOtherSize()
        {
            // マスクは編集時の作業解像度で保存されるので、原本と寸法が違ってよい（半分の寸法でも同じ所を外す）。
            var preset = RedToBlue(out _);
            preset.maskWidth = W / 2;
            preset.maskHeight = H / 2;
            preset.commonMaskBase64 = MaskRle.Encode(LeftHalf(W / 2, H / 2), W / 2, H / 2);
            AssertOnlyRightHalfChanged(Run(preset, out _));
        }

        // ─── 調べる(Inspect) ───

        [Test]
        public void Inspect_Texture_ReturnsEachColorWithPositionInsideIt()
        {
            // 左半分が赤・右半分が青(64×32)。2 色が半分ずつで、代表点はそれぞれの側の内側。
            var blue = new Color32(30, 40, 200, 255);
            var px = new Color32[64 * 32];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 64; x++)
                    px[y * 64 + x] = x < 32 ? Red : blue;
            string src = _assets.WritePng("halves", px, 64, 32);

            var r = Inspect(src);
            Assert.AreEqual("texture", r.kind);
            var info = r.textures.Single();
            Assert.AreEqual(src, info.path);
            Assert.AreEqual(2, info.colors.Count, string.Join(",", info.colors.Select(c => c.hex)));
            var red = info.colors.Single(c => c.hex == "#C81E1E");
            var bl = info.colors.Single(c => c.hex == "#1E28C8");
            Assert.AreEqual(0.5f, red.coverage, 1e-4f);
            Assert.Less(red.uv[0], 0.5f);
            Assert.Greater(bl.uv[0], 0.5f);
            Assert.AreEqual(1f, red.regionShare, 1e-4f);
            Assert.IsTrue(File.Exists(info.thumbnail), "印付きの縮小画像を書くこと");
        }

        [Test]
        public void Inspect_SceneObject_ListsMainTexturesUnderIt()
        {
            string src = _assets.WritePng("body", Enumerable.Repeat(Red, W * H).ToArray(), W, H);
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            var root = new GameObject("IrocaInspectTestAvatar");
            var mat = new Material(Shader.Find("Standard")) { mainTexture = tex };
            try
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Quad);
                body.name = "Body";
                body.transform.SetParent(root.transform);
                body.GetComponent<Renderer>().sharedMaterial = mat;

                var r = Inspect("IrocaInspectTestAvatar");
                Assert.AreEqual("object", r.kind);
                var info = r.textures.Single();
                Assert.AreEqual(src, info.path);
                CollectionAssert.AreEqual(new[] { "IrocaInspectTestAvatar/Body" }, info.renderers);
                CollectionAssert.IsEmpty(info.colors, "オブジェクトでは色を出さない(テクスチャを渡したときだけ)");

                Assert.AreEqual(src, Inspect("IrocaInspectTestAvatar/Body").textures.Single().path);
                Assert.AreEqual(src, Inspect("#" + root.GetInstanceID()).textures.Single().path);

                var scene = Inspect("");
                Assert.AreEqual("scene", scene.kind);
                Assert.AreEqual(1, scene.objects.Single(o => o.path == "IrocaInspectTestAvatar").textureCount);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mat);
            }
        }

        [Test]
        public void Inspect_UnknownTarget_IsError()
        {
            string json = IrocaAutomation.Inspect("NoSuchObject_IrocaTest");
            var r = JsonUtility.FromJson<IrocaAutomation.InspectResult>(json);
            Assert.IsFalse(r.ok, json);
            StringAssert.StartsWith("not found:", r.error);
        }

        private static IrocaAutomation.InspectResult Inspect(string target)
        {
            string json = IrocaAutomation.Inspect(target);
            var r = JsonUtility.FromJson<IrocaAutomation.InspectResult>(json);
            Assert.IsTrue(r.ok, json);
            return r;
        }

        // ─── 色替え(Recolor) ───

        private static readonly Color32 Blue = new Color32(30, 40, 200, 255);

        // 左半分が赤・右半分が青(32×16)。
        private string Halves()
        {
            var px = new Color32[32 * 16];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 32; x++)
                    px[y * 32 + x] = x < 16 ? Red : Blue;
            return _assets.WritePng("halves", px, 32, 16);
        }

        // 自動調整は別のテストで見る(ここでは新しいゾーンの初期値のまま、結果を決まった形にする)。
        private static string Request(string texture, string apply, string changes, string extra = "",
                                      bool autoTune = false)
            => "{\"texture\":\"" + texture + "\",\"apply\":\"" + apply + "\",\"changes\":" + changes + extra
               + ",\"autoTune\":" + (autoTune ? "true" : "false") + "}";

        private static IrocaAutomation.RecolorTextureResult RecolorOk(string request)
        {
            string json = IrocaAutomation.Recolor(request);
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            return r;
        }

        [Test]
        public void Recolor_Preview_SavesNothingAndChangesOnlyTheClickedColor()
        {
            string src = Halves();
            var r = RecolorOk(Request(src, "preview", "[{\"name\":\"red\",\"at\":[0.25,0.5],\"to\":\"#1A2B5C\"}]"));
            Assert.AreEqual(0.5f, r.changedFraction, 1e-4f);
            Assert.AreEqual("#C81E1E", r.zones.Single().from);
            Assert.AreEqual("#1A2B5C", r.zones.Single().to);
            Assert.IsTrue(File.Exists(r.preview), "比較画像を書くこと");
            Assert.IsEmpty(r.recipe);
            Assert.IsEmpty(r.output);
            CollectionAssert.IsEmpty(UnityEditor.AssetDatabase.FindAssets("t:IrocaRecipe", new[] { _assets.Folder }));
        }

        [Test]
        public void Recolor_Png_WritesRecoloredNextToSource()
        {
            string src = Halves();
            var r = RecolorOk(Request(src, "png", "[{\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]"));
            Assert.AreEqual(TestAssets.Abs(_assets.Folder + "/halves_recolored.png"), r.output);
            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(r.output)));
                var px = tex.GetPixels32();
                Assert.IsFalse(Same(px[0], Red), "赤い側が変わること");
                Assert.IsTrue(Same(px[31], Blue), "青い側は変わらないこと");
            }
            finally { Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Recolor_Avatar_RegistersRecipeAndReplacesZoneOfSameName()
        {
            if (NdmfSupport.State != NdmfSupportState.Supported) Assert.Ignore("NDMF が使えないホストでは登録を断る(AvatarApplyBlocked で見る)");
            string src = Halves();
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            bool hadRecipeFolder = UnityEditor.AssetDatabase.IsValidFolder(RecipeStore.DefaultFolder);
            bool hadIrocaFolder = UnityEditor.AssetDatabase.IsValidFolder("Assets/Iroca");
            var root = new GameObject("IrocaRecolorTestAvatar");
            var mat = new Material(Shader.Find("Standard")) { mainTexture = tex };
            string recipePath = null;
            try
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Quad);
                body.name = "Body";
                body.transform.SetParent(root.transform);
                body.GetComponent<Renderer>().sharedMaterial = mat;

                // 登録先の既定は、そのテクスチャを使うレンダラーの共通の親(1 つならそのレンダラー。UI と同じ)。
                string first = "[{\"name\":\"skirt\",\"at\":[0.25,0.5],\"to\":\"#1A2B5C\"}]";
                var r = RecolorOk(Request(src, "avatar", first));
                recipePath = r.recipe;
                Assert.AreEqual("IrocaRecolorTestAvatar/Body", r.avatar);
                var component = body.GetComponent<IrocaRecolor>();
                Assert.IsNotNull(component, "コンポーネントを付けること");
                var recipe = component.recipes.Single();
                Assert.AreEqual(tex, recipe.sourceTexture);
                Assert.AreEqual(recipePath, UnityEditor.AssetDatabase.GetAssetPath(recipe));

                // 同じ名前で頼み直す(登録先は明示) → 同じレシピのまま、ゾーンは 1 つで変更先が替わる。
                string again = "[{\"name\":\"skirt\",\"at\":[0.25,0.5],\"to\":\"#FFAA00\"}]";
                var r2 = RecolorOk(Request(src, "avatar", again, ",\"avatar\":\"IrocaRecolorTestAvatar/Body\""));
                Assert.AreEqual(recipePath, r2.recipe);
                var state = RecipeStore.Load(recipe);
                Assert.AreEqual(1, state.zones.Count);
                Assert.AreEqual("#FFAA00", "#" + ColorUtility.ToHtmlStringRGB(state.zones[0].targetColor));
                Assert.AreEqual(1, body.GetComponents<IrocaRecolor>().Length);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mat);
                if (!string.IsNullOrEmpty(recipePath)) UnityEditor.AssetDatabase.DeleteAsset(recipePath);
                if (!hadRecipeFolder) UnityEditor.AssetDatabase.DeleteAsset(RecipeStore.DefaultFolder);
                if (!hadIrocaFolder) UnityEditor.AssetDatabase.DeleteAsset("Assets/Iroca");
            }
        }

        [TestCase("preview", "[{\"at\":[0.25,0.5],\"to\":\"darkish blue\"}]", "'to' must be a color")]
        [TestCase("preview", "[{\"at\":[1.5,0.5],\"to\":\"#000000\"}]", "'at' must be [u, v]")]
        [TestCase("preview", "[{\"to\":\"#000000\"}]", "give 'at'")]
        [TestCase("preview", "[]", "no enabled zones")]
        [TestCase("save", "[{\"from\":\"#C81E1E\",\"to\":\"#000000\"}]", "unknown apply")]
        public void Recolor_InvalidRequest_IsError(string apply, string changes, string error)
        {
            string json = IrocaAutomation.Recolor(Request(Halves(), apply, changes));
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsFalse(r.ok, json);
            StringAssert.Contains(error, r.error);
        }

        [Test]
        public void AvatarApplyBlocked_InPlayModeOrWithoutNdmf()
        {
            // Play 中の登録は Play を止めると消える。NDMF が使えなければ登録しても色替えされない。どちらも断って代わりを案内する。
            Assert.IsNull(IrocaAutomation.AvatarApplyBlocked(false, NdmfSupportState.Supported));
            StringAssert.Contains("Play mode", IrocaAutomation.AvatarApplyBlocked(true, NdmfSupportState.Supported));
            foreach (var state in new[] { NdmfSupportState.Missing, NdmfSupportState.TooOld, NdmfSupportState.TooNew })
                StringAssert.Contains("apply='png'", IrocaAutomation.AvatarApplyBlocked(false, state), state.ToString());
        }

        // ─── PNG の書き出し(作業解像度の画素を使い回すか) ───

        [Test]
        public void IsFullResolution_SameSizePng_ReadsTheSamePixelsAsTheExport()
        {
            // 取り込みで縮まない PNG: 作業解像度で読んだ画素が、書き出しで読む画素とビット一致する(読み直さなくてよい)。
            string src = Halves();
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            string abs = TestAssets.Abs(src);
            var kind = ExportPipeline.ReadSourcePixels(abs, tex, out var work, out int w, out int h, workingSize: true);
            ExportPipeline.ReadSourcePixels(abs, tex, out var full, out int fw, out int fh);
            Assert.IsTrue(IrocaAutomation.IsFullResolution(kind, tex, w, h));
            Assert.AreEqual((fw, fh), (w, h));
            CollectionAssert.AreEqual(full, work);
        }

        [Test]
        public void IsFullResolution_PngShrunkOnImport_IsFalse()
        {
            // 取り込みで縮む(最大サイズ 32 の 64×64)ときは、書き出しで元の解像度を読み直す。
            var px = Enumerable.Repeat(Red, 64 * 64).ToArray();
            string src = _assets.WritePng("big", px, 64, 64);
            var importer = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(src);
            importer.maxTextureSize = 32;
            importer.SaveAndReimport();
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            var kind = ExportPipeline.ReadSourcePixels(TestAssets.Abs(src), tex, out _, out int w, out int h, workingSize: true);
            Assert.AreEqual((32, 32), (w, h), "テストの前提(作業解像度が取り込み後の寸法)");
            Assert.IsFalse(IrocaAutomation.IsFullResolution(kind, tex, w, h));
        }

        [Test]
        public void IsFullResolution_ImportedTexture_ReadsTheSamePixelsAsTheExport()
        {
            // PNG/JPG として読めない原本(TGA)は、作業解像度でも書き出しでも取り込み済みテクスチャの同じ画素を読む。
            var b = new byte[18 + 8 * 4 * 4];
            b[2] = 2; b[12] = 8; b[14] = 4; b[16] = 32; b[17] = 8;
            for (int i = 0; i < 8 * 4; i++) { b[18 + i * 4] = 30; b[19 + i * 4] = 200; b[20 + i * 4] = 10; b[21 + i * 4] = 255; }
            string src = _assets.WriteBytes("src.tga", b, readable: true);
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            string abs = TestAssets.Abs(src);
            var kind = ExportPipeline.ReadSourcePixels(abs, tex, out var work, out int w, out int h, workingSize: true);
            ExportPipeline.ReadSourcePixels(abs, tex, out var full, out _, out _);
            Assert.AreEqual(ExportPipeline.SourceKind.ImportedTexture, kind);
            Assert.IsTrue(IrocaAutomation.IsFullResolution(kind, tex, w, h));
            CollectionAssert.AreEqual(full, work);
        }

        [Test]
        public void PngOutput_HasTheSamePixelsAsTexture2DEncode()
        {
            // 書き出しのエンコードを Texture2D.EncodeToPNG から ExportPipeline.EncodePng に替えた。読み戻した画素が同じこと。
            var rng = new System.Random(7);
            const int pw = 37, ph = 23;
            var px = new Color32[pw * ph];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            var viaTexture = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
            var a = new Texture2D(2, 2);
            var c = new Texture2D(2, 2);
            try
            {
                viaTexture.SetPixels32(px);
                Assert.IsTrue(a.LoadImage(viaTexture.EncodeToPNG()));
                Assert.IsTrue(c.LoadImage(ExportPipeline.EncodePng(px, pw, ph)));
                CollectionAssert.AreEqual(a.GetPixels32(), c.GetPixels32());
                CollectionAssert.AreEqual(px, c.GetPixels32());
            }
            finally
            {
                Object.DestroyImmediate(viaTexture);
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(c);
            }
        }

        // ─── 自動調整 ───

        // 自動調整を待つ依頼を、終わるまで問い合わせ直す(MCP のサーバーと同じ。間に editor の 1 コマを挟む)。
        private static IEnumerator RecolorUntilDone(string request, System.Action<string> done, double timeoutSeconds = 120)
        {
            string json = IrocaAutomation.Recolor(request);
            double deadline = UnityEditor.EditorApplication.timeSinceStartup + timeoutSeconds;
            while (json.Contains("\"pending\":true"))
            {
                Assert.Less(UnityEditor.EditorApplication.timeSinceStartup, deadline, "自動調整が終わらない: " + json);
                yield return null;
                json = IrocaAutomation.RecolorStatus();
            }
            done(json);
        }

        [UnityTest]
        public IEnumerator Recolor_AutoTune_ZoneGivenByColor_UsesConventionalAnalysis()
        {
            // 色で指定したゾーン(スポイト位置なし)は、UI と同じく AI を使わない導出で調整する。
            string src = Halves();
            string json = null;
            yield return RecolorUntilDone(
                Request(src, "preview", "[{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]", autoTune: true),
                j => json = j);
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            Assert.AreEqual("conventional", r.zones.Single().autoTune, json);
            Assert.AreEqual(0.5f, r.changedFraction, 1e-4f, "赤い側だけ変わること");
        }

        [UnityTest, Timeout(1200000)]
        public IEnumerator Recolor_AutoTune_ClickedZone_UsesAiEvidenceWhenAvailable()
        {
            // スポイト位置のあるゾーンは AI の提案を証拠にする。AI が無いホストでは、UI と同じく初期値のまま残して知らせる。
            var svc = MaskSuggestBridge.Service;
            bool ai = svc != null && MaskSuggestBridge.ModelFilesPresent && svc.TryEnsureModels();
            string src = Halves();
            string json = null;
            // -nographics では推論エンジンが GPU のカーネルが無いと LogError してから CPU へ切り替える
            // (MaskSuggestInferenceTests と同じ)。切り替え後に通ることを見るので、エラーログでは落とさない。
            LogAssert.ignoreFailingMessages = true;
            try
            {
                yield return RecolorUntilDone(
                    Request(src, "preview", "[{\"name\":\"red\",\"at\":[0.25,0.5],\"to\":\"#1A2B5C\"}]", autoTune: true),
                    j => json = j, timeoutSeconds: 600);
            }
            finally { LogAssert.ignoreFailingMessages = false; }
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            string how = r.zones.Single().autoTune;
            if (ai) StringAssert.DoesNotStartWith("skipped", how, json);
            else
            {
                StringAssert.StartsWith("skipped", how, json);
                Assert.IsTrue(r.warnings.Any(w => w.Contains("AI")), json);
            }
            Assert.AreEqual(0.5f, r.changedFraction, 1e-4f, "赤い側だけ変わること");
        }

        [Test]
        public void RecolorStatus_WithoutJob_IsError()
        {
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(IrocaAutomation.RecolorStatus());
            Assert.IsFalse(r.ok);
            StringAssert.Contains("no recolor is in progress", r.error);
        }

        // ─── 窓で開く(Open) ───
        // 窓を実際に開くのは GUI のホストで確かめる(batchmode -nographics では窓を出さない)。ここでは何を開くかの解決だけ。

        [Test]
        public void OpenTarget_TextureAndRecipeAndObject()
        {
            string src = Halves();
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            Assert.IsTrue(IrocaAutomation.ResolveOpenTarget(src, out var t, out var rec, out string err), err);
            Assert.AreEqual(tex, t);
            Assert.IsNull(rec);

            var state = IrocaSessionState.CreateDefault();
            var recipe = RecipeStore.Create(tex, state, _assets.Folder);
            Assert.IsTrue(IrocaAutomation.ResolveOpenTarget(UnityEditor.AssetDatabase.GetAssetPath(recipe), out t, out rec, out err), err);
            Assert.AreEqual(recipe, rec);
            Assert.AreEqual(tex, t);

            var go = new GameObject("IrocaOpenTestAvatar");
            try
            {
                Assert.IsFalse(IrocaAutomation.ResolveOpenTarget("IrocaOpenTestAvatar", out _, out _, out err));
                StringAssert.StartsWith("no Iroca recipe", err);
                go.AddComponent<IrocaRecolor>().recipes = new System.Collections.Generic.List<IrocaRecipe> { recipe };
                Assert.IsTrue(IrocaAutomation.ResolveOpenTarget("IrocaOpenTestAvatar", out t, out rec, out err), err);
                Assert.AreEqual(recipe, rec);
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ─── 道具 ───

        private static IrocaPresetData RedToBlue(out ColorZone zone)
        {
            zone = new ColorZone
            {
                name = "red",
                sampleColor = (Color)Red,
                sampleColorSet = true,
                targetColor = new Color(0.1f, 0.2f, 0.8f, 1f),
                targetColorSet = true,
                tolerance = ZonesJsonDefaults.Tolerance,   // ColorZone の既定は 0（何も拾わない）。UI の新規ゾーンと同じ値
            };
            zone.EnsureId();
            var preset = new IrocaPresetData { name = "test" };
            preset.zones.Add(zone);
            return preset;
        }

        // 左半分が true の (w×h) マスク。
        private static bool[] LeftHalf(int w, int h)
        {
            var m = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w / 2; x++)
                    m[y * w + x] = true;
            return m;
        }

        // 原本(一面の赤)をプリセット(インライン JSON)で色替えし、書き出した PNG の画素を返す。
        private Color32[] Run(IrocaPresetData preset, out IrocaAutomation.RecolorResult result)
        {
            string src = _assets.WritePng("src", Enumerable.Repeat(Red, W * H).ToArray(), W, H);
            string json = IrocaAutomation.RecolorByPreset(src, JsonUtility.ToJson(preset), _assets.Folder + "/out.png");
            result = JsonUtility.FromJson<IrocaAutomation.RecolorResult>(json);
            Assert.IsTrue(result.ok, json);
            CollectionAssert.IsEmpty(result.warnings, json);

            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(result.output)));
                Assert.AreEqual(W, tex.width);
                Assert.AreEqual(H, tex.height);
                return tex.GetPixels32();
            }
            finally { Object.DestroyImmediate(tex); }
        }

        private static void AssertOnlyRightHalfChanged(Color32[] px)
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var c = px[y * W + x];
                    if (x < W / 2) Assert.IsTrue(Same(c, Red), $"除外した ({x},{y}) が変わった: {c}");
                    else Assert.IsFalse(Same(c, Red), $"({x},{y}) が変わっていない");
                }
        }

        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    }
}
