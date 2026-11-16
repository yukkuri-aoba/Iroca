// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 影の色を塗り先に合わせる設定(<see cref="IrocaRecolor.shadowMatchMaterials"/>)の非破壊ビルドでの効き方。
    /// 複製にだけ効き、元のマテリアルは変わらない。計算そのものは unit-run の ShadowMatch_*。
    /// lilToon が無いホストでは Ignore。
    /// </summary>
    public class ShadowColorMatchTests {
        private static readonly Color BlueShadow = new Color(0.606f, 0.693f, 0.784f, 1f);
        private static readonly Color Sample = new Color(0.676f, 0.851f, 0.998f);
        private static readonly Color Target = new Color(0.904f, 1f, 0.896f);

        private TestAssets _assets;
        private readonly List<Object> _created = new List<Object>();
        // 非破壊ビルドが残す、どのゾーンがどこを塗るかの控え(後で消す)。
        private readonly List<string> _shareFiles = new List<string>();

        [SetUp] public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown() {
            foreach (var o in _created) {
                if (o != null) {
                    Object.DestroyImmediate(o);
                }
            }
            _created.Clear();
            foreach (var f in _shareFiles) {
                try { System.IO.File.Delete(f); } catch (System.IO.IOException) { /* 次回 */ }
            }
            _shareFiles.Clear();
            RecipeZoneShares.ClearMemory();
            _assets.Dispose();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        private Texture2D Tex(string name) {
            string path = _assets.WritePng(name, TestAssets.Solid(8, 8, new Color32(172, 217, 254, 255)), 8, 8);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private Material LilToon(Texture2D main) {
            var shader = Shader.Find("lilToon");
            if (shader == null) {
                Assert.Ignore("lilToon が入っていないホスト");
            }
            var m = Track(new Material(shader));
            m.SetTexture(MaterialColorSettings.MainTextureProperty(m), main);
            m.SetFloat("_UseShadow", 1f);
            m.SetColor("_ShadowColor", BlueShadow);
            return m;
        }

        // 影色テクスチャ(lilToon の _Shadow*ColorTex)が元テクスチャと別に描かれていると、そのテクスチャは色替えされないので
        // 影はその色のまま残る。インスペクタと窓で知らせるため、そういう影色テクスチャを返す。同じテクスチャ(一緒に色替え
        // される)・空・使っていない影(色の α が 0)の欄・lilToon 以外は入れない。
        [Test]
        public void SeparateShadowTextures_AreTheSetOnesThatDifferFromTheSource() {
            var src = Tex("src");
            var shadowTex = Tex("shadow");
            var m = LilToon(src);
            CollectionAssert.IsEmpty(MaterialShadowColors.SeparateShadowTextures(m, src), "影色テクスチャが無いのに返した");

            m.SetTexture("_ShadowColorTex", src);
            CollectionAssert.IsEmpty(MaterialShadowColors.SeparateShadowTextures(m, src), "同じテクスチャ(一緒に色替えされる)を返した");

            m.SetTexture("_ShadowColorTex", shadowTex);
            CollectionAssert.AreEqual(new[] { shadowTex }, MaterialShadowColors.SeparateShadowTextures(m, src));

            // 2 影の欄: 使っていない(色の α が 0)なら入れず、使っていれば入れる(同じテクスチャは 1 回)。
            m.SetTexture("_Shadow2ndColorTex", Tex("shadow2"));
            m.SetColor("_Shadow2ndColor", new Color(0.5f, 0.5f, 0.5f, 0f));
            CollectionAssert.AreEqual(new[] { shadowTex }, MaterialShadowColors.SeparateShadowTextures(m, src), "使っていない影の欄を返した");
            m.SetTexture("_Shadow2ndColorTex", shadowTex);
            m.SetColor("_Shadow2ndColor", new Color(0.5f, 0.5f, 0.5f, 1f));
            CollectionAssert.AreEqual(new[] { shadowTex }, MaterialShadowColors.SeparateShadowTextures(m, src), "同じテクスチャを 2 回返した");

            // 影を使っていない・lilToon 以外は対象外(影の色を合わせられるのは lilToon だけ)。
            m.SetFloat("_UseShadow", 0f);
            CollectionAssert.IsEmpty(MaterialShadowColors.SeparateShadowTextures(m, src), "影を使っていないのに返した");
            var standard = Track(new Material(Shader.Find("Standard")) { mainTexture = src });
            CollectionAssert.IsEmpty(MaterialShadowColors.SeparateShadowTextures(standard, src));
        }

        private IrocaRecipe Recipe(Texture2D src) => RecipeWith(src, Zone(Sample, Target));

        private GameObject Node(string name, Transform parent, Material mat = null) {
            var go = Track(mat != null ? GameObject.CreatePrimitive(PrimitiveType.Cube) : new GameObject());
            go.name = name;
            if (parent != null) {
                go.transform.SetParent(parent, false);
            }
            if (mat != null) {
                go.GetComponent<Renderer>().sharedMaterial = mat;
            }
            return go;
        }

        private static Color Expected => ShadowColorMatch.Compute(BlueShadow, Sample, Target);

        private static readonly Color Pink = new Color(250 / 255f, 200 / 255f, 210 / 255f);

        // 左半分が Sample の色、右半分がピンクのテクスチャ。
        private Texture2D HalfAndHalf(string name) {
            const int w = 16, h = 8;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    px[y * w + x] = x < w / 2 ? new Color32(172, 217, 254, 255) : new Color32(250, 200, 210, 255);
                }
            }
            string path = _assets.WritePng(name, px, w, h);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static ColorZone Zone(Color sample, Color target) {
            var zone = new ColorZone { sampleColor = sample, sampleColorSet = true, targetColor = target, tolerance = 0.2f };
            zone.EnsureId();
            return zone;
        }

        private IrocaRecipe RecipeWith(Texture2D src, params ColorZone[] zones) {
            var state = new IrocaSessionState();
            state.zones.AddRange(zones);
            var recipe = RecipeStore.Create(src, state, _assets.Folder);
            _shareFiles.Add(RecipeZoneShares.CachePath(RecipeTextureBuilder.CacheKey(recipe, src)));
            return recipe;
        }

        // UV が横の [u0, u1] の帯だけを覆う板(パーツごとのマテリアルが、アトラスの一部だけを映す)。
        private GameObject Quad(string name, Transform parent, Material mat, float u0, float u1) {
            var go = Track(new GameObject(name));
            go.transform.SetParent(parent, false);
            var mesh = Track(new Mesh {
                vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) },
                uv = new[] { new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u0, 1), new Vector2(u1, 1) },
                triangles = new[] { 0, 2, 1, 2, 3, 1 },
            });
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        private static void AssertColor(Color expected, Color actual, string message) {
            Assert.AreEqual(expected.r, actual.r, 1e-3f, message);
            Assert.AreEqual(expected.g, actual.g, 1e-3f, message);
            Assert.AreEqual(expected.b, actual.b, 1e-3f, message);
        }

        [Test]
        public void Applier_ChangesOnlyTheClone() {
            var src = Tex("src");
            var mat = LilToon(src);
            var root = Node("avatar", null);
            var mesh = Node("mesh", root.transform, mat);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(Recipe(src));
            c.shadowMatchMaterials.Add(mat);

            NonDestructiveApplier.Apply(root, new Host());

            var clone = mesh.GetComponent<Renderer>().sharedMaterial;
            Assert.AreNotSame(mat, clone);
            AssertColor(Expected, clone.GetColor("_ShadowColor"), "複製の影の色が合っていない");
            AssertColor(BlueShadow, mat.GetColor("_ShadowColor"), "元のマテリアルを書き換えた");
        }

        [Test]
        public void Applier_NotChosen_KeepsTheShadowColor() {
            var src = Tex("src");
            var mat = LilToon(src);
            var root = Node("avatar", null);
            var mesh = Node("mesh", root.transform, mat);
            root.AddComponent<IrocaRecolor>().recipes.Add(Recipe(src));

            NonDestructiveApplier.Apply(root, new Host());

            AssertColor(BlueShadow, mesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "選んでいないのに変えた");
        }

        [Test]
        public void Applier_SameRecipe_OnlyTheChoosingComponentMatches() {
            // 同じレシピでも、設定はコンポーネントごと。複製を共有すると、選んでいない方の影まで変わる。
            var src = Tex("src");
            var mat = LilToon(src);
            var recipe = Recipe(src);
            var root = Node("avatar", null);
            var chosen = Node("chosen", root.transform, mat);
            var plain = Node("plain", root.transform, mat);
            var a = chosen.AddComponent<IrocaRecolor>();
            a.recipes.Add(recipe);
            a.shadowMatchMaterials.Add(mat);
            plain.AddComponent<IrocaRecolor>().recipes.Add(recipe);

            NonDestructiveApplier.Apply(root, new Host());

            AssertColor(Expected, chosen.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "選んだ方");
            AssertColor(BlueShadow, plain.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "選んでいない方");
        }

        [Test]
        public void Applier_TwoStages_DoesNotShiftTwice() {
            var src = Tex("src");
            var mat = LilToon(src);
            var root = Node("avatar", null);
            var mesh = Node("mesh", root.transform, mat);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(Recipe(src));
            c.shadowMatchMaterials.Add(mat);
            var state = new NonDestructiveApplier.BuildState();
            var host = new Host();

            NonDestructiveApplier.Apply(root, host, state, NonDestructiveApplier.Stage.First);
            var first = mesh.GetComponent<Renderer>().sharedMaterial;
            NonDestructiveApplier.Apply(root, host, state, NonDestructiveApplier.Stage.Late);

            Assert.AreSame(first, mesh.GetComponent<Renderer>().sharedMaterial);
            AssertColor(Expected, first.GetColor("_ShadowColor"), "2 段目で計算し直した");
        }

        [Test]
        public void Applier_RecipeOfAnotherSlotFirst_StillUsesTheOriginalColors() {
            // 別の欄のレシピが先に複製しても、影の色はメインのレシピで、元のマテリアルの色から決まる。
            var main = Tex("main");
            var layer = Tex("layer");
            var mat = LilToon(main);
            mat.SetTexture("_Main2ndTex", layer);
            var root = Node("avatar", null);
            var mesh = Node("mesh", root.transform, mat);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(Recipe(layer));
            c.recipes.Add(Recipe(main));
            c.shadowMatchMaterials.Add(mat);

            NonDestructiveApplier.Apply(root, new Host());

            AssertColor(Expected, mesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "別の欄の複製で合わせそこねた");
        }

        [Test]
        public void Applier_ChosenButTextureNotMain_KeepsTheShadowColor() {
            var main = Tex("main");
            var layer = Tex("layer");
            var mat = LilToon(main);
            mat.SetTexture("_Main2ndTex", layer);
            var root = Node("avatar", null);
            var mesh = Node("mesh", root.transform, mat);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(Recipe(layer));
            c.shadowMatchMaterials.Add(mat);

            NonDestructiveApplier.Apply(root, new Host());

            AssertColor(BlueShadow, mesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "重ね描きの層の色替えで変えた");
        }

        // レシピのゾーンの色を読む(非破壊ビルドとシーンのプレビューが、複製・枠ごとに呼ぶ)。中身が同じ間は読み直さず、
        // 中身か形式の版が変われば読み直す。
        [Test]
        public void ColorsOf_FromRecipe_FollowsTheRecipeContent() {
            var src = Tex("src");
            var recipe = Recipe(src);
            string id = RecipeStore.LoadZones(recipe)[0].id;
            var colors = MaterialShadowColors.ColorsOf(recipe);
            Assert.IsTrue(colors.TryGet(id, out var sample, out var target));
            AssertColor(Sample, sample, "サンプルの色");
            AssertColor(Target, target, "塗り先の色");
            Assert.AreSame(colors, MaterialShadowColors.ColorsOf(recipe), "2 回目(覚えから)で読み直した");

            // 中身が変わったら、新しい色を読む。
            var changed = RecipeStore.Load(recipe);
            changed.zones[0].targetColor = Color.yellow;
            RecipeStore.Save(recipe, changed);
            Assert.IsTrue(MaterialShadowColors.ColorsOf(recipe).TryGet(id, out _, out target));
            AssertColor(Color.yellow, target, "レシピを書き換えた後の塗り先の色");

            // 読めない形式の版になったら空(中身の文字列は同じ)。
            int version = recipe.formatVersion;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try {
                recipe.formatVersion = IrocaRecipe.CurrentFormatVersion + 1;
                Assert.IsTrue(MaterialShadowColors.ColorsOf(recipe).IsEmpty, "読めない版のレシピから色を返した");
            } finally {
                recipe.formatVersion = version;
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
            Assert.IsFalse(MaterialShadowColors.ColorsOf(recipe).IsEmpty, "版を戻した後");
            Assert.IsTrue(MaterialShadowColors.ColorsOf(null).IsEmpty);
        }

        // パーツごとにマテリアルを分けて 1 枚のテクスチャ(左半分が水色・右半分がピンク)を共有しているとき、影の色は
        // マテリアルごとに、そのマテリアルが映す所を塗ったゾーンの色に合わせる。色替えしていないパーツの影は変えない。
        [Test]
        public void Applier_PartsSharingATexture_EachFollowsItsOwnZone() {
            var src = HalfAndHalf("atlas");
            var left = LilToon(src);
            var right = LilToon(src);
            var root = Node("avatar", null);
            var leftMesh = Quad("left", root.transform, left, 0f, 0.5f);
            var rightMesh = Quad("right", root.transform, right, 0.5f, 1f);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(RecipeWith(src, Zone(Sample, Target)));
            c.shadowMatchMaterials.Add(left);
            c.shadowMatchMaterials.Add(right);

            NonDestructiveApplier.Apply(root, new Host());

            AssertColor(Expected, leftMesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "塗ったパーツが合っていない");
            AssertColor(BlueShadow, rightMesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"),
                "色替えしていないパーツの影まで変えた");
        }

        [Test]
        public void Applier_PartsSharingATexture_TwoZones_EachMaterialGetsItsZoneColor() {
            var src = HalfAndHalf("atlas2");
            var left = LilToon(src);
            var right = LilToon(src);
            var root = Node("avatar", null);
            var leftMesh = Quad("left", root.transform, left, 0f, 0.5f);
            var rightMesh = Quad("right", root.transform, right, 0.5f, 1f);
            var red = new Color(1f, 0.3f, 0.3f);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(RecipeWith(src, Zone(Sample, Target), Zone(Pink, red)));
            c.shadowMatchMaterials.Add(left);
            c.shadowMatchMaterials.Add(right);

            NonDestructiveApplier.Apply(root, new Host());

            AssertColor(Expected, leftMesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "左のパーツ");
            AssertColor(ShadowColorMatch.Compute(BlueShadow, Pink, red),
                rightMesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "右のパーツが最初のゾーンの色になった");
        }

        [Test]
        public void NeedsMatch_OnlyWhenTheShadowIsVisiblyOff() {
            var src = Tex("src");
            var mat = LilToon(src);
            mat.SetColor("_Shadow2ndColor", new Color(0.7f, 0.7f, 0.7f, 1f));
            Assert.IsTrue(MaterialShadowColors.NeedsMatch(mat, Sample, Target), "青い影 × 緑の塗り先");
            Assert.IsFalse(MaterialShadowColors.NeedsMatch(mat, Sample, new Color(0.3f, 0.6f, 0.95f)), "同じ色相");

            mat.SetColor("_ShadowColor", new Color(0.7f, 0.7f, 0.7f, 1f));
            Assert.IsFalse(MaterialShadowColors.NeedsMatch(mat, Sample, Target), "無彩色の影");

            mat.SetColor("_ShadowColor", BlueShadow);
            mat.SetFloat("_UseShadow", 0f);
            Assert.IsFalse(MaterialShadowColors.NeedsMatch(mat, Sample, Target), "影を使っていない");
        }

        [Test]
        public void AddShadowMatch_SkipsOutOfScopeAndDuplicates() {
            var src = Tex("src");
            var inScope = LilToon(src);
            var outOfScope = LilToon(src);
            var root = Node("avatar", null);
            Node("mesh", root.transform, inScope);
            Node("other", null, outOfScope);
            var c = root.AddComponent<IrocaRecolor>();
            var materials = new[] { inScope, outOfScope };

            Assert.AreEqual(1, RecipeRegistration.AddShadowMatch(c, materials));
            Assert.AreEqual(0, RecipeRegistration.AddShadowMatch(c, materials));
            CollectionAssert.AreEqual(new[] { inScope }, c.shadowMatchMaterials);
        }

        // いろかウィンドウの「影の色を変更先カラーに合わせる」を外すと、そのテクスチャのレシピを持つコンポーネントから外れ、
        // 合わせているかの判定(HasShadowMatch)も false になる。ほかのテクスチャのレシピだけを持つコンポーネントには触らない。
        [Test]
        public void RemoveShadowMatch_ClearsOnlyComponentsWithTheSourceRecipe() {
            var src = Tex("src");
            var other = Tex("other");
            var mat = LilToon(src);
            var keep = LilToon(src);
            var root = Node("avatar", null);
            Node("mesh", root.transform, mat);
            var c = root.AddComponent<IrocaRecolor>();
            c.recipes.Add(Recipe(src));
            c.shadowMatchMaterials.Add(mat);
            c.shadowMatchMaterials.Add(keep);
            var unrelatedRoot = Node("unrelated", null);
            var unrelated = unrelatedRoot.AddComponent<IrocaRecolor>();
            unrelated.recipes.Add(Recipe(other));
            unrelated.shadowMatchMaterials.Add(mat);
            var components = new[] { c, unrelated };
            Assert.IsTrue(RecipeRegistration.HasShadowMatch(components, mat, src));

            Assert.AreEqual(1, RecipeRegistration.RemoveShadowMatch(components, new[] { mat }, src));
            Assert.IsFalse(RecipeRegistration.HasShadowMatch(components, mat, src), "外した後も合わせている判定のまま");
            CollectionAssert.AreEqual(new[] { keep }, c.shadowMatchMaterials, "指定していないマテリアルまで外した");
            CollectionAssert.AreEqual(new[] { mat }, unrelated.shadowMatchMaterials, "ほかのテクスチャのレシピのコンポーネントから外した");
            Assert.AreEqual(0, RecipeRegistration.RemoveShadowMatch(components, new[] { mat }, src));
        }

        private sealed class Host : NonDestructiveApplier.IHost {
            public Texture2D BuildTexture(IrocaRecipe recipe, out RecipeTextureBuilder.Failure failure) {
                failure = RecipeTextureBuilder.Failure.None;
                return new Texture2D(2, 2) { name = "recolored:" + recipe.name };
            }
            public void SaveAsset(Object generated) { }
            public void RegisterReplaced(Object original, Object replacement) { }
            public void Report(NonDestructiveApplier.Problem problem, IrocaRecolor component, IrocaRecipe recipe,
                RecipeTextureBuilder.Failure failure) { }
            public void RewriteAnimatedMaterials(System.Func<Transform, bool> inScope, System.Func<Material, Material> mapping) { }
        }
    }
}
