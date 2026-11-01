// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 影の色を塗り先に合わせる設定(<see cref="IrocaRecolor.shadowMatchMaterials"/>)の非破壊ビルドでの効き方。
    /// 複製にだけ効き、元のマテリアルは変わらない。計算そのものは unit-run の ShadowMatch_*。
    /// lilToon が無いホストでは Ignore。
    /// </summary>
    public class ShadowColorMatchTests
    {
        private static readonly Color BlueShadow = new Color(0.606f, 0.693f, 0.784f, 1f);
        private static readonly Color Sample = new Color(0.676f, 0.851f, 0.998f);
        private static readonly Color Target = new Color(0.904f, 1f, 0.896f);

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

        private Texture2D Tex(string name)
        {
            string path = _assets.WritePng(name, TestAssets.Solid(8, 8, new Color32(172, 217, 254, 255)), 8, 8);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private Material LilToon(Texture2D main)
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("lilToon が入っていないホスト");
            var m = Track(new Material(shader));
            m.SetTexture(MaterialColorSettings.MainTextureProperty(m), main);
            m.SetFloat("_UseShadow", 1f);
            m.SetColor("_ShadowColor", BlueShadow);
            return m;
        }

        private IrocaRecipe Recipe(Texture2D src)
        {
            var zone = new ColorZone { sampleColor = Sample, sampleColorSet = true, targetColor = Target, tolerance = 0.2f };
            zone.EnsureId();
            var state = new IrocaSessionState();
            state.zones.Add(zone);
            return RecipeStore.Create(src, state, _assets.Folder);
        }

        private GameObject Node(string name, Transform parent, Material mat = null)
        {
            var go = Track(mat != null ? GameObject.CreatePrimitive(PrimitiveType.Cube) : new GameObject());
            go.name = name;
            if (parent != null) go.transform.SetParent(parent, false);
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private static Color Expected => ShadowColorMatch.Compute(BlueShadow, Sample, Target);

        private static void AssertColor(Color expected, Color actual, string message)
        {
            Assert.AreEqual(expected.r, actual.r, 1e-3f, message);
            Assert.AreEqual(expected.g, actual.g, 1e-3f, message);
            Assert.AreEqual(expected.b, actual.b, 1e-3f, message);
        }

        [Test]
        public void Applier_ChangesOnlyTheClone()
        {
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
        public void Applier_NotChosen_KeepsTheShadowColor()
        {
            var src = Tex("src");
            var mat = LilToon(src);
            var root = Node("avatar", null);
            var mesh = Node("mesh", root.transform, mat);
            root.AddComponent<IrocaRecolor>().recipes.Add(Recipe(src));

            NonDestructiveApplier.Apply(root, new Host());

            AssertColor(BlueShadow, mesh.GetComponent<Renderer>().sharedMaterial.GetColor("_ShadowColor"), "選んでいないのに変えた");
        }

        [Test]
        public void Applier_SameRecipe_OnlyTheChoosingComponentMatches()
        {
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
        public void Applier_TwoStages_DoesNotShiftTwice()
        {
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
        public void Applier_RecipeOfAnotherSlotFirst_StillUsesTheOriginalColors()
        {
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
        public void Applier_ChosenButTextureNotMain_KeepsTheShadowColor()
        {
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

        [Test]
        public void NeedsMatch_OnlyWhenTheShadowIsVisiblyOff()
        {
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
        public void AddShadowMatch_SkipsOutOfScopeAndDuplicates()
        {
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

        private sealed class Host : NonDestructiveApplier.IHost
        {
            public Texture2D BuildTexture(IrocaRecipe recipe, out RecipeTextureBuilder.Failure failure)
            {
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
