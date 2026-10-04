// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_NDMF_PRESENT
using System.Collections.Generic;
using System.Linq;
using Iroca.NdmfIntegration;
using nadena.dev.ndmf.preview;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// シーン上のアバターへのプレビュー(<see cref="IrocaPreviewFilter"/>)を NDMF の API どおりに呼ぶ:
    /// 対象の選び方、プロキシへの差し替え、毎フレームの差し替え直し、後片付け、変化の監視。
    /// NDMF の描画の流れ(プロキシの入れ替え)そのものは NDMF の責任なので、ここでは呼び出し口までを見る。
    /// NDMF が入ったホスト(Iroca_Dev)でだけコンパイルされる(asmdef の IROCA_NDMF_PRESENT)。
    /// </summary>
    public class NdmfPreviewFilterTests
    {
        private const int W = 16, H = 16;
        private TestAssets _assets;
        private readonly List<Object> _created = new List<Object>();
        private readonly List<string> _cacheFiles = new List<string>();
        private LivePreview.Target _saved;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _saved = LivePreview.Current;
            LivePreview.SetTarget(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            LivePreview.SetTarget(_saved?.source, _saved?.boundRecipe);
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
            foreach (var f in _cacheFiles)
                if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
            _cacheFiles.Clear();
            _assets.Dispose();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        private (Texture2D src, Material mat) SourceAndMaterial()
        {
            string path = _assets.WritePng("src", TestAssets.Solid(W, H, new Color32(200, 40, 40, 255)), W, H);
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var mat = Track(new Material(Shader.Find("Standard")) { mainTexture = src });
            return (src, mat);
        }

        private MeshRenderer Mesh(string name, Material mat, GameObject parent = null)
        {
            var go = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            go.name = name;
            if (parent != null) go.transform.SetParent(parent.transform, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            return r;
        }

        private Renderer ProxyOf(Renderer original)
        {
            var copy = Track(Object.Instantiate(original.gameObject));
            copy.hideFlags = HideFlags.HideAndDontSave;
            return copy.GetComponent<Renderer>();
        }

        // RenderGroup.Renderers は ImmutableList(このアセンブリは参照しない)なので、等価比較で探す。
        private static RenderGroup GroupOf(IEnumerable<RenderGroup> groups, Renderer r)
        {
            var wanted = RenderGroup.For(r);
            return groups.FirstOrDefault(g => g.Equals(wanted));
        }

        private IrocaRecipe RedToGreen(Texture2D src)
        {
            var zone = new ColorZone
            {
                sampleColor = new Color(200 / 255f, 40 / 255f, 40 / 255f),
                sampleColorSet = true,
                targetColor = new Color(0.1f, 0.6f, 0.2f),
                tolerance = 0.2f,
            };
            zone.EnsureId();
            var state = new IrocaSessionState();
            state.zones.Add(zone);
            var recipe = RecipeStore.Create(src, state, _assets.Folder);
            _cacheFiles.Add(System.IO.Path.Combine(RecipeTextureBuilder.CacheDir,
                RecipeTextureBuilder.CacheKey(recipe, src) + ".tex"));
            return recipe;
        }

        [Test]
        public void Live_ReplacesOnTheProxy_ReappliesEachFrame_AndCleansUp()
        {
            var (src, mat) = SourceAndMaterial();
            var renderer = Mesh("mesh", mat);
            LivePreview.SetTarget(src, null);

            var filter = new IrocaPreviewFilter();
            var group = GroupOf(filter.FindTargets(new ComputeContext("test")), renderer);
            Assert.IsNotNull(group, "編集中のテクスチャを使う Renderer が対象にならない");

            var proxy = ProxyOf(renderer);
            var node = filter.Instantiate(group, new List<(Renderer, Renderer)> { (renderer, proxy) },
                new ComputeContext("node")).Result;
            Material swapped;
            try
            {
                swapped = proxy.sharedMaterial;
                Assert.AreNotSame(mat, swapped, "プロキシのマテリアルが差し替わっていない");
                Assert.IsInstanceOf<RenderTexture>(swapped.mainTexture);
                Assert.AreSame(src, mat.mainTexture, "元のマテリアルを書き換えた");
                Assert.AreSame(mat, renderer.sharedMaterial, "元の Renderer を書き換えた");
                Assert.AreEqual(1, LivePreview.RefCount(src));

                // NDMF は描画のたびに、描画用のプロキシを元のマテリアルへ戻してから OnFrame を呼ぶ。
                proxy.sharedMaterials = renderer.sharedMaterials;
                node.OnFrame(renderer, proxy);
                Assert.AreSame(swapped, proxy.sharedMaterial, "毎フレームの差し替え直しをしていない");
            }
            finally
            {
                node.Dispose();
            }
            Assert.AreEqual(0, LivePreview.RefCount(src), "借りたテクスチャを返していない");
            Assert.IsTrue(swapped == null, "マテリアルの複製を捨てていない");
        }

        [Test]
        public void Registered_ShowsTheRecipeOnlyInsideTheComponent()
        {
            var (src, mat) = SourceAndMaterial();
            var avatar = Track(new GameObject("avatar"));
            var inside = Mesh("inside", mat, avatar);
            var outside = Mesh("outside", mat);
            var recipe = RedToGreen(src);
            avatar.AddComponent<IrocaRecolor>().recipes.Add(recipe);

            var filter = new IrocaPreviewFilter();
            var groups = filter.FindTargets(new ComputeContext("test"));
            Assert.IsNull(GroupOf(groups, outside), "範囲外(ビルドでも変わらない)に映している");
            var group = GroupOf(groups, inside);
            Assert.IsNotNull(group);

            var proxy = ProxyOf(inside);
            var node = filter.Instantiate(group, new List<(Renderer, Renderer)> { (inside, proxy) },
                new ComputeContext("node")).Result;
            try
            {
                var tex = proxy.sharedMaterial.mainTexture as Texture2D;
                Assert.IsNotNull(tex, "レシピの出来上がり(Texture2D)が入っていない");
                Assert.IsTrue(tex.name.EndsWith("(Iroca)"), tex.name);
                Assert.AreEqual((src.width, src.height, src.format), (tex.width, tex.height, tex.format),
                    "非破壊ビルドと同じテクスチャになっていない");
            }
            finally
            {
                node.Dispose();
            }

            // ウィンドウがこのレシピを編集し始めたら、同じ範囲にライブを映す。
            LivePreview.SetTarget(src, recipe);
            var liveNode = filter.Instantiate(group, new List<(Renderer, Renderer)> { (inside, ProxyOf(inside)) },
                new ComputeContext("node")).Result;
            try
            {
                Assert.AreEqual(1, LivePreview.RefCount(src), "編集中のレシピの範囲にライブを映していない");
            }
            finally
            {
                liveNode.Dispose();
            }
        }

        [Test]
        public void OneComponentWithSeveralRecipes_ShowsEachTexture()
        {
            string pathA = _assets.WritePng("a", TestAssets.Solid(W, H, new Color32(200, 40, 40, 255)), W, H);
            string pathB = _assets.WritePng("b", TestAssets.Solid(W, H, new Color32(200, 40, 40, 255)), W, H);
            var texA = AssetDatabase.LoadAssetAtPath<Texture2D>(pathA);
            var texB = AssetDatabase.LoadAssetAtPath<Texture2D>(pathB);
            var avatar = Track(new GameObject("avatar"));
            var a = Mesh("a", Track(new Material(Shader.Find("Standard")) { mainTexture = texA }), avatar);
            var b = Mesh("b", Track(new Material(Shader.Find("Standard")) { mainTexture = texB }), avatar);
            var c = avatar.AddComponent<IrocaRecolor>();
            c.recipes.Add(RedToGreen(texA));
            c.recipes.Add(RedToGreen(texB));

            var filter = new IrocaPreviewFilter();
            var groups = filter.FindTargets(new ComputeContext("test"));
            foreach (var r in new[] { a, b })
            {
                var group = GroupOf(groups, r);
                Assert.IsNotNull(group, $"{r.name}: 2 つ目以降のレシピのテクスチャが対象にならない");
                var proxy = ProxyOf(r);
                var node = filter.Instantiate(group, new List<(Renderer, Renderer)> { (r, proxy) },
                    new ComputeContext("node")).Result;
                try
                {
                    var tex = proxy.sharedMaterial.mainTexture;
                    Assert.IsTrue(tex != null && tex.name.EndsWith("(Iroca)"), $"{r.name}: {tex}");
                }
                finally
                {
                    node.Dispose();
                }
            }
        }

        [Test]
        public void ChangingWhatTheWindowEdits_InvalidatesThePreview()
        {
            var (src, mat) = SourceAndMaterial();
            Mesh("mesh", mat);
            LivePreview.SetTarget(src, null);

            var ctx = new ComputeContext("test");
            new IrocaPreviewFilter().FindTargets(ctx);
            Assert.IsFalse(ctx.IsInvalidated);
            LivePreview.SetTarget(null, null);
            ComputeContext.FlushInvalidates();
            Assert.IsTrue(ctx.IsInvalidated, "ウィンドウの編集対象が変わっても NDMF が対象を選び直さない");
        }
    }
}
#endif
