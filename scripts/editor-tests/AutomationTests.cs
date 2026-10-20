// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

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
