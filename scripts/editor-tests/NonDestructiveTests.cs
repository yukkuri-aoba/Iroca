// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 非破壊の色替え(レシピ → テクスチャ → マテリアル差し替え)のうち、NDMF に依存しない部分。
    /// NDMF を通した結合は <c>NdmfProcessAvatarTests</c>(NDMF があるホストでだけコンパイル)。
    /// </summary>
    public class NonDestructiveTests
    {
        private const int W = 64, H = 64;

        private TestAssets _assets;
        private readonly List<Object> _created = new List<Object>();

        [SetUp] public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
            _assets.Dispose();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        // 上半分 = 赤、下半分 = 青。どちらも縦方向に明暗がある(再着色の明度リマップが実際に動く)。
        private static Color32[] Bands()
        {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float shade = 0.6f + 0.4f * y / (H - 1);
                for (int x = 0; x < W; x++)
                {
                    bool top = y >= H / 2;   // 行 0 = 画像下端
                    px[y * W + x] = top
                        ? new Color32((byte)(200 * shade), (byte)(40 * shade), (byte)(40 * shade), 255)
                        : new Color32((byte)(40 * shade), (byte)(90 * shade), (byte)(200 * shade), 255);
                }
            }
            return px;
        }

        // 赤 → 緑のゾーン 1 つ + 青の帯の左下を「含める」マスク。
        private static IrocaSessionState RedToGreenWithInclude()
        {
            var zone = new ColorZone
            {
                name = "red",
                sampleColor = new Color(0.78f, 0.16f, 0.16f),
                sampleColorSet = true,
                targetColor = new Color(0.10f, 0.60f, 0.20f),
                tolerance = 0.2f,
                valueBlend = 0.85f,
            };
            zone.EnsureId();
            var state = new IrocaSessionState();
            state.zones.Add(zone);
            var include = new bool[W * H];
            for (int y = 0; y < H / 4; y++)
                for (int x = 0; x < W / 4; x++)
                    include[y * W + x] = true;
            MaskStateCodec.Encode(state.maskState, W, H, null, null,
                new Dictionary<string, bool[]> { [zone.id] = include });
            return state;
        }

        private Texture2D ImportSource(bool readable = false, int maxSize = 0)
        {
            string path = _assets.WritePng("src", Bands(), W, H, readable);
            if (maxSize > 0)
            {
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                imp.maxTextureSize = maxSize;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ───────────── レシピ → 画素 ─────────────

        [Test]
        public void Recipe_RoundTrip_RecolorsExactlyLikeTheLiveSession()
        {
            var src = ImportSource();
            var state = RedToGreenWithInclude();

            var expected = Bands();
            Assert.IsTrue(SessionRecolor.Apply(expected, W, H, state));
            Assert.AreNotEqual(Bands()[(H - 1) * W], expected[(H - 1) * W], "赤の帯が色替えされていない(テストの前提が崩れている)");
            Assert.AreNotEqual(Bands()[0], expected[0], "含めるマスクの画素が色替えされていない(マスクが効いていない)");

            // JSON(JsonUtility)へ保存 → 読み戻し → 原本ファイルから読み直して当てる、の全経路。
            var recipe = RecipeStore.Create(src, state, _assets.Folder);
            Assert.IsTrue(RecipeTextureBuilder.TryRecolor(recipe, out var actual, out int w, out int h,
                out var failure), failure.ToString());
            Assert.AreEqual((W, H), (w, h));
            for (int i = 0; i < expected.Length; i++)
                if (!expected[i].Equals(actual[i]))
                    Assert.Fail($"画素 {i} が編集状態の直接適用と違います: {expected[i]} vs {actual[i]}");
        }

        [Test]
        public void TryRecolor_ReportsWhyItCannotRun()
        {
            var src = ImportSource();

            var noSource = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            Assert.IsFalse(RecipeTextureBuilder.TryRecolor(noSource, out _, out _, out _, out var f1));
            Assert.AreEqual(RecipeTextureBuilder.Failure.NoSourceTexture, f1);

            var empty = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            empty.sourceTexture = src;
            Assert.IsFalse(RecipeTextureBuilder.TryRecolor(empty, out _, out _, out _, out var f2));
            Assert.AreEqual(RecipeTextureBuilder.Failure.UnreadableRecipe, f2);

            var state = RedToGreenWithInclude();
            state.zones[0].enabled = false;
            var disabled = RecipeStore.Create(src, state, _assets.Folder);
            Assert.IsFalse(RecipeTextureBuilder.TryRecolor(disabled, out _, out _, out _, out var f3));
            Assert.AreEqual(RecipeTextureBuilder.Failure.NoEnabledZones, f3);
        }

        [Test]
        public void Recipe_Save_WritesOnlyWhenChanged()
        {
            var src = ImportSource();
            var state = RedToGreenWithInclude();
            var recipe = RecipeStore.Create(src, state, _assets.Folder);
            Assert.IsFalse(RecipeStore.Save(recipe, state), "同じ内容で保存し直した");
            state.zones[0].tolerance = 0.3f;
            Assert.IsTrue(RecipeStore.Save(recipe, state));
            Assert.AreEqual(0.3f, RecipeStore.Load(recipe).zones[0].tolerance);
            CollectionAssert.AreEqual(new[] { recipe }, RecipeStore.FindForTexture(src));
        }

        // ───────────── 取り込み設定へそろえる ─────────────

        [Test]
        public void CreateMatching_FollowsImportedSizeFormatMipsAndColorSpace()
        {
            var like = ImportSource(maxSize: 32);
            Assert.AreEqual(32, like.width, "取り込み時の最大サイズが効いていない(テストの前提)");

            var tex = Track(RecipeTextureBuilder.CreateMatching(Bands(), W, H, like));
            Assert.AreEqual(like.width, tex.width);
            Assert.AreEqual(like.height, tex.height);
            Assert.AreEqual(like.format, tex.format, "圧縮形式が取り込みと違う(VRAM が変わる)");
            Assert.AreEqual(like.mipmapCount, tex.mipmapCount);
            Assert.AreEqual(GraphicsFormatUtility.IsSRGBFormat(like.graphicsFormat),
                GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat), "色空間(sRGB)が取り込みと違う");
        }

        [Test]
        public void ResizeArea_AveragesBlocksForIntegerRatios()
        {
            var src = new Color32[4 * 4];
            for (int i = 0; i < src.Length; i++) src[i] = new Color32((byte)(i * 10), 0, 0, 255);
            var dst = RecipeTextureBuilder.ResizeArea(src, 4, 4, 2, 2);
            // 左下 2x2 = 画素 0,1,4,5 → (0+10+40+50)/4 = 25
            Assert.AreEqual(25, dst[0].r);
            // 右上 2x2 = 画素 10,11,14,15 → (100+110+140+150)/4 = 125
            Assert.AreEqual(125, dst[3].r);
        }

        [Test]
        public void Build_SecondCallComesFromCacheWithIdenticalData()
        {
            var src = ImportSource(readable: true);
            var recipe = RecipeStore.Create(src, RedToGreenWithInclude(), _assets.Folder);
            string cacheFile = Path.Combine(RecipeTextureBuilder.CacheDir,
                RecipeTextureBuilder.CacheKey(recipe, src) + ".tex");
            try
            {
                var first = Track(RecipeTextureBuilder.Build(recipe, out var f1));
                Assert.IsNotNull(first, f1.ToString());
                Assert.IsTrue(File.Exists(cacheFile), "キャッシュが書かれていない");
                var second = Track(RecipeTextureBuilder.Build(recipe, out var f2));
                Assert.IsNotNull(second, f2.ToString());
                CollectionAssert.AreEqual(first.GetRawTextureData(), second.GetRawTextureData());
                Assert.AreEqual(first.format, second.format);
                Assert.AreEqual(first.mipmapCount, second.mipmapCount);

                // レシピが変われば別の鍵になる(古い色が出ない)。
                var changed = RedToGreenWithInclude();
                changed.zones[0].targetColor = Color.yellow;
                RecipeStore.Save(recipe, changed);
                Assert.AreNotEqual(cacheFile, Path.Combine(RecipeTextureBuilder.CacheDir,
                    RecipeTextureBuilder.CacheKey(recipe, src) + ".tex"));
            }
            finally
            {
                if (File.Exists(cacheFile)) File.Delete(cacheFile);
            }
        }

        private static (bool on, int priority) StreamingOf(Texture2D tex)
        {
            using (var so = new SerializedObject(tex))
                return (so.FindProperty("m_StreamingMipmaps").boolValue,
                        so.FindProperty("m_StreamingMipmapsPriority").intValue);
        }

        // mip streaming は元の取り込み設定をそのまま引き継ぐ(NDMF の CheckMipStreamingPass が見る値)。
        // 元がオフなら元のアバターでも VRChat SDK が止めるので、そこで元を直せば引き継いでオンになる。
        [TestCase(false, 0)]
        [TestCase(true, 3)]
        public void Build_InheritsMipStreamingFromSource(bool sourceStreaming, int sourcePriority)
        {
            string path = _assets.WritePng("src", Bands(), W, H);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.streamingMipmaps = sourceStreaming;
            imp.streamingMipmapsPriority = sourcePriority;
            imp.SaveAndReimport();
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.Greater(src.mipmapCount, 1, "元にミップが無い(テストの前提)");
            Assert.AreEqual(sourceStreaming, StreamingOf(src).on, "取り込み設定が効いていない(テストの前提)");

            var recipe = RecipeStore.Create(src, RedToGreenWithInclude(), _assets.Folder);
            string cacheFile = Path.Combine(RecipeTextureBuilder.CacheDir,
                RecipeTextureBuilder.CacheKey(recipe, src) + ".tex");
            try
            {
                // 1 回目 = 作りたて、2 回目 = キャッシュから。どちらも同じ設定になる。
                for (int i = 0; i < 2; i++)
                {
                    var tex = Track(RecipeTextureBuilder.Build(recipe, out var failure));
                    Assert.IsNotNull(tex, failure.ToString());
                    Assert.AreEqual((sourceStreaming, sourcePriority), StreamingOf(tex), i == 0 ? "作りたて" : "キャッシュから");
                }
            }
            finally
            {
                if (File.Exists(cacheFile)) File.Delete(cacheFile);
            }
        }

        // ───────────── マテリアルの差し替え ─────────────

        private sealed class FakeHost : NonDestructiveApplier.IHost
        {
            public readonly List<(NonDestructiveApplier.Problem, IrocaRecolor, IrocaRecipe, RecipeTextureBuilder.Failure)> Reports
                = new List<(NonDestructiveApplier.Problem, IrocaRecolor, IrocaRecipe, RecipeTextureBuilder.Failure)>();
            public readonly List<Object> Saved = new List<Object>();
            public readonly Dictionary<IrocaRecipe, Texture2D> Textures = new Dictionary<IrocaRecipe, Texture2D>();
            public int Builds;
            public bool Fail;

            public Texture2D BuildTexture(IrocaRecipe recipe, out RecipeTextureBuilder.Failure failure)
            {
                Builds++;
                failure = Fail ? RecipeTextureBuilder.Failure.SourceUnreadable : RecipeTextureBuilder.Failure.None;
                if (Fail) return null;
                var t = new Texture2D(2, 2) { name = "recolored:" + recipe.name };
                Textures[recipe] = t;
                return t;
            }
            public void SaveAsset(Object generated) => Saved.Add(generated);
            public void RegisterReplaced(Object original, Object replacement) { }
            public void Report(NonDestructiveApplier.Problem p, IrocaRecolor c, IrocaRecipe r, RecipeTextureBuilder.Failure f)
                => Reports.Add((p, c, r, f));

            // アニメーションのマテリアル切り替え(切り替える先の GameObject と、キーフレームのマテリアル)。
            public readonly List<(GameObject target, Material[] keys)> Animated = new List<(GameObject, Material[])>();

            public void RewriteAnimatedMaterials(Transform scope, System.Func<Material, Material> mapping)
            {
                foreach (var (target, keys) in Animated)
                {
                    if (!target.transform.IsChildOf(scope)) continue;
                    for (int i = 0; i < keys.Length; i++)
                        if (keys[i] != null) keys[i] = mapping(keys[i]);
                }
            }
        }

        private Texture2D Tex(string name) => Track(new Texture2D(2, 2) { name = name });

        private Material Mat(string name, Texture tex)
        {
            var m = Track(new Material(Shader.Find("Standard")) { name = name });
            m.mainTexture = tex;
            return m;
        }

        private GameObject Node(string name, Transform parent, Material mat = null)
        {
            var go = Track(new GameObject(name));
            go.transform.SetParent(parent, false);
            if (mat != null) go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        private IrocaRecipe Recipe(string name, Texture2D source)
        {
            var r = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            r.name = name;
            r.sourceTexture = source;
            return r;
        }

        private static IrocaRecolor AddRecolor(GameObject go, IrocaRecipe recipe)
        {
            var c = go.AddComponent<IrocaRecolor>();
            c.recipes.Add(recipe);
            return c;
        }

        [Test]
        public void Applier_ReplacesOnlyMaterialsInScopeThatUseTheTexture()
        {
            var src = Tex("src");
            var other = Tex("other");
            var shared = Mat("shared", src);
            var unrelated = Mat("unrelated", other);

            var root = Node("avatar", null);
            var outfit = Node("outfit", root.transform);
            var a = Node("a", outfit.transform, shared);
            var a2 = Node("a2", outfit.transform, shared);
            var b = Node("b", outfit.transform, unrelated);
            var body = Node("body", root.transform, shared);   // 範囲外で同じマテリアル
            var recipe = Recipe("r", src);
            AddRecolor(outfit, recipe);

            var host = new FakeHost();
            int replaced = NonDestructiveApplier.Apply(root, host);

            var aMat = a.GetComponent<Renderer>().sharedMaterial;
            Assert.AreEqual(1, replaced);
            Assert.AreNotSame(shared, aMat, "範囲内のマテリアルが差し替わっていない");
            Assert.AreSame(host.Textures[recipe], aMat.mainTexture);
            Assert.AreSame(aMat, a2.GetComponent<Renderer>().sharedMaterial, "同じマテリアルの複製が共有されていない");
            Assert.AreSame(unrelated, b.GetComponent<Renderer>().sharedMaterial, "元テクスチャを使わないマテリアルまで複製した");
            Assert.AreSame(shared, body.GetComponent<Renderer>().sharedMaterial, "範囲外の Renderer まで差し替えた");
            Assert.AreSame(src, shared.mainTexture, "元のマテリアルを書き換えた");
            Assert.AreEqual(1, host.Builds);
            CollectionAssert.Contains(host.Saved, aMat);
            CollectionAssert.Contains(host.Saved, host.Textures[recipe]);
            Assert.IsEmpty(root.GetComponentsInChildren<IrocaRecolor>(true), "コンポーネントが残っている");
            Assert.IsEmpty(host.Reports);
        }

        [Test]
        public void Applier_DeeperComponentWinsWhereScopesOverlap()
        {
            var src = Tex("src");
            var mOuter = Mat("outer", src);
            var mInner = Mat("inner", src);
            var root = Node("avatar", null);
            var outerOnly = Node("outerOnly", root.transform, mOuter);
            var innerGo = Node("innerGroup", root.transform);
            var innerR = Node("innerR", innerGo.transform, mInner);
            var rOuter = Recipe("outer", src);
            var rInner = Recipe("inner", src);
            AddRecolor(root, rOuter);
            AddRecolor(innerGo, rInner);

            var host = new FakeHost();
            NonDestructiveApplier.Apply(root, host);

            Assert.AreSame(host.Textures[rInner], innerR.GetComponent<Renderer>().sharedMaterial.mainTexture,
                "入れ子の内側(近い方)のレシピが使われていない");
            Assert.AreSame(host.Textures[rOuter], outerOnly.GetComponent<Renderer>().sharedMaterial.mainTexture);
            Assert.IsEmpty(host.Reports);
        }

        [Test]
        public void Applier_ReportsProblemsAndStillRemovesComponents()
        {
            var src = Tex("src");
            var m = Mat("m", src);
            var root = Node("avatar", null);
            var noRecipe = Node("noRecipe", root.transform);
            AddRecolor(noRecipe, null);
            var notUsed = Node("notUsed", root.transform);
            Node("child", notUsed.transform, Mat("other", Tex("other")));
            AddRecolor(notUsed, Recipe("r", src));
            var failing = Node("failing", root.transform);
            var r = Node("r", failing.transform, m);
            AddRecolor(failing, Recipe("f", src));

            var host = new FakeHost { Fail = true };
            NonDestructiveApplier.Apply(root, host);

            var kinds = host.Reports.ConvertAll(x => x.Item1);
            CollectionAssert.AreEquivalent(new[]
            {
                NonDestructiveApplier.Problem.MissingRecipe,
                NonDestructiveApplier.Problem.TextureNotUsedInScope,
                NonDestructiveApplier.Problem.BuildFailed,
            }, kinds);
            Assert.AreSame(m, r.GetComponent<Renderer>().sharedMaterial, "失敗したのにマテリアルを差し替えた");
            Assert.IsEmpty(root.GetComponentsInChildren<IrocaRecolor>(true), "失敗時にコンポーネントが残っている");
        }

        [Test]
        public void Applier_OneComponentRecolorsSeveralTextures()
        {
            var texA = Tex("a");
            var texB = Tex("b");
            var both = Mat("both", texA);
            both.SetTexture("_EmissionMap", texB);
            var onlyB = Mat("onlyB", texB);
            var root = Node("avatar", null);
            var x = Node("x", root.transform, both);
            var y = Node("y", root.transform, onlyB);
            var rA = Recipe("rA", texA);
            var rB = Recipe("rB", texB);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(rA);
            c.recipes.Add(rB);

            var host = new FakeHost();
            NonDestructiveApplier.Apply(root, host);

            var xMat = x.GetComponent<Renderer>().sharedMaterial;
            Assert.AreSame(host.Textures[rA], xMat.mainTexture, "1 つ目のレシピが効いていない");
            Assert.AreSame(host.Textures[rB], xMat.GetTexture("_EmissionMap"), "同じマテリアルの 2 つ目のテクスチャが効いていない");
            Assert.AreSame(host.Textures[rB], y.GetComponent<Renderer>().sharedMaterial.mainTexture);
            Assert.AreSame(texA, both.mainTexture, "元のマテリアルを書き換えた");
            Assert.IsEmpty(host.Reports);
            Assert.IsEmpty(root.GetComponentsInChildren<IrocaRecolor>(true));
        }

        [Test]
        public void Applier_MaterialsSwitchedByAnimationAreRecoloredInScope()
        {
            var src = Tex("src");
            var plain = Mat("plain", Tex("plain"));
            var alt = Mat("alt", src);           // トグルで切り替える先(既定では使っていない)
            var shared = Mat("shared", src);     // 既定でもアニメーションでも使う
            var root = Node("avatar", null);
            var outfit = Node("outfit", root.transform);
            var mesh = Node("mesh", outfit.transform, plain);
            var other = Node("other", outfit.transform, shared);
            var body = Node("body", root.transform, plain);   // 範囲外
            var recipe = Recipe("r", src);
            AddRecolor(outfit, recipe);

            var host = new FakeHost();
            var meshKeys = new[] { plain, alt, shared };
            var bodyKeys = new[] { alt };
            host.Animated.Add((mesh, meshKeys));
            host.Animated.Add((body, bodyKeys));
            NonDestructiveApplier.Apply(root, host);

            var recolored = host.Textures[recipe];
            Assert.AreSame(plain, meshKeys[0], "元テクスチャを使わないマテリアルまで差し替えた");
            Assert.AreNotSame(alt, meshKeys[1], "アニメーションで切り替わるマテリアルが差し替わっていない");
            Assert.AreSame(recolored, meshKeys[1].mainTexture);
            Assert.AreSame(other.GetComponent<Renderer>().sharedMaterial, meshKeys[2],
                "既定とアニメーションで同じマテリアルなら同じ複製を使う");
            Assert.AreSame(alt, bodyKeys[0], "範囲外の Renderer へのアニメーションまで差し替えた");
            Assert.AreSame(src, alt.mainTexture, "元のマテリアルを書き換えた");
            Assert.AreEqual(1, host.Builds);
            Assert.IsEmpty(host.Reports);
        }

        [Test]
        public void Applier_TextureUsedOnlyByAnimation_IsNotReportedAsUnused_AndDeeperWins()
        {
            var src = Tex("src");
            var alt = Mat("alt", src);
            var root = Node("avatar", null);
            var inner = Node("inner", root.transform);
            var mesh = Node("mesh", inner.transform, Mat("plain", Tex("plain")));
            var outerRecipe = Recipe("outer", src);
            var innerRecipe = Recipe("inner", src);
            AddRecolor(root, outerRecipe);
            AddRecolor(inner, innerRecipe);

            var host = new FakeHost();
            var keys = new[] { alt };
            host.Animated.Add((mesh, keys));
            NonDestructiveApplier.Apply(root, host);

            Assert.AreSame(host.Textures[innerRecipe], keys[0].mainTexture, "アニメーションでも近い(深い)コンポーネントが勝つ");
            CollectionAssert.AreEquivalent(new[]
            {
                (NonDestructiveApplier.Problem.TextureNotUsedInScope, outerRecipe),   // 内側が差し替え済み
            }, host.Reports.ConvertAll(x => (x.Item1, x.Item3)),
                "アニメーションでだけ使うテクスチャを「使っていない」と報告した");
        }

        [Test]
        public void Applier_TwoStages_RecolorMaterialsAddedInBetween_WithOneTexture()
        {
            // VRCFury のように 1 段目(NDMF の Transforming)と 2 段目(最適化段)の間に動くツールが、
            // トグルなどで元のマテリアルを入れ直す場合。
            var src = Tex("src");
            var shared = Mat("shared", src);
            var root = Node("avatar", null);
            var outfit = Node("outfit", root.transform);
            var mesh = Node("mesh", outfit.transform, shared);
            var later = Node("later", outfit.transform, Mat("plain", Tex("plain")));
            var recipe = Recipe("r", src);
            AddRecolor(outfit, recipe);
            var empty = Node("empty", root.transform);
            empty.AddComponent<IrocaRecolor>();

            var host = new FakeHost();
            var state = new NonDestructiveApplier.BuildState();
            NonDestructiveApplier.Apply(root, host, state, NonDestructiveApplier.Stage.First);
            var first = mesh.GetComponent<Renderer>().sharedMaterial;
            Assert.AreSame(host.Textures[recipe], first.mainTexture);
            Assert.IsNotEmpty(root.GetComponentsInChildren<IrocaRecolor>(true), "1 段目でコンポーネントを外した(2 段目が範囲を知れない)");

            // 間に動くツールが、元のマテリアルを既定とアニメーションで入れ直す。
            later.GetComponent<Renderer>().sharedMaterial = shared;
            var keys = new[] { shared };
            host.Animated.Add((later, keys));
            NonDestructiveApplier.Apply(root, host, state, NonDestructiveApplier.Stage.Late);

            Assert.AreSame(first, later.GetComponent<Renderer>().sharedMaterial, "2 段目で入った元のマテリアルが 1 段目と同じ複製にならない");
            Assert.AreSame(first, keys[0], "2 段目で入ったアニメーションのマテリアルが差し替わっていない");
            Assert.AreEqual(1, host.Builds, "色替え済みテクスチャを 2 段目で作り直した(同じテクスチャが 2 枚入る)");
            Assert.IsEmpty(root.GetComponentsInChildren<IrocaRecolor>(true), "2 段目でコンポーネントが外れていない");
            CollectionAssert.AreEquivalent(new[] { NonDestructiveApplier.Problem.MissingRecipe },
                host.Reports.ConvertAll(x => x.Item1), "設定の問題は 1 回だけ報告する");
        }

        [Test]
        public void Applier_TwoStages_UnusedIsDecidedAtTheEnd()
        {
            // 1 段目では使われず、間に動くツールが入れたマテリアルでだけ使われるテクスチャ。
            var src = Tex("src");
            var root = Node("avatar", null);
            var mesh = Node("mesh", root.transform, Mat("plain", Tex("plain")));
            var used = Recipe("used", src);
            var unused = Recipe("unused", Tex("never"));
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(used);
            c.recipes.Add(unused);

            var host = new FakeHost();
            var state = new NonDestructiveApplier.BuildState();
            NonDestructiveApplier.Apply(root, host, state, NonDestructiveApplier.Stage.First);
            Assert.IsEmpty(host.Reports, "1 段目で「使っていない」と決めつけた");

            mesh.GetComponent<Renderer>().sharedMaterial = Mat("toggled", src);
            NonDestructiveApplier.Apply(root, host, state, NonDestructiveApplier.Stage.Late);
            Assert.AreSame(host.Textures[used], mesh.GetComponent<Renderer>().sharedMaterial.mainTexture);
            CollectionAssert.AreEquivalent(new[] { (NonDestructiveApplier.Problem.TextureNotUsedInScope, unused) },
                host.Reports.ConvertAll(x => (x.Item1, x.Item3)));
        }

        [Test]
        public void Applier_SameTextureTwiceInOneComponent_FirstWins_AndEmptyListIsReported()
        {
            var src = Tex("src");
            var root = Node("avatar", null);
            var r = Node("r", root.transform, Mat("m", src));
            var first = Recipe("first", src);
            var second = Recipe("second", src);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(first);
            c.recipes.Add(second);
            var empty = Node("empty", root.transform);
            empty.AddComponent<IrocaRecolor>();

            var host = new FakeHost();
            NonDestructiveApplier.Apply(root, host);

            Assert.AreSame(host.Textures[first], r.GetComponent<Renderer>().sharedMaterial.mainTexture,
                "同じテクスチャは先のレシピが勝つ(並び順が優先順)");
            CollectionAssert.AreEquivalent(new[]
            {
                (NonDestructiveApplier.Problem.TextureNotUsedInScope, second),   // 先のレシピが差し替え済み
                (NonDestructiveApplier.Problem.MissingRecipe, (IrocaRecipe)null), // レシピの無いコンポーネント
            }, host.Reports.ConvertAll(x => (x.Item1, x.Item3)));
        }
    }
}
