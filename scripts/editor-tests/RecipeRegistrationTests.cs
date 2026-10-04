// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 「アバターに非破壊で登録」でコンポーネントが増えないこと: 同じオブジェクトへの登録は 1 つの
    /// <see cref="IrocaRecolor"/> にレシピを足し、同じテクスチャは置き換え、旧版で複数付いていたら
    /// 1 つにまとめる(結果は変えない・1 回の Undo で戻る)。旧版の保存形式(1 コンポーネント = 1 レシピ)の読み込み。
    /// </summary>
    public class RecipeRegistrationTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        private Texture2D Tex(string name) => Track(new Texture2D(2, 2) { name = name });

        private IrocaRecipe Recipe(string name, Texture2D source)
        {
            var r = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            r.name = name;
            r.sourceTexture = source;
            return r;
        }

        private GameObject Avatar() => Track(new GameObject("avatar"));

        [Test]
        public void Attach_AddsToTheExistingComponent()
        {
            var go = Avatar();
            var rA = Recipe("a", Tex("a"));
            var rB = Recipe("b", Tex("b"));

            var c1 = RecipeRegistration.Attach(go, rA);
            var c2 = RecipeRegistration.Attach(go, rB);
            var c3 = RecipeRegistration.Attach(go, rA);   // 登録し直し(同じレシピ)

            Assert.AreSame(c1, c2);
            Assert.AreSame(c1, c3);
            Assert.AreEqual(1, go.GetComponents<IrocaRecolor>().Length, "登録のたびにコンポーネントが増えた");
            CollectionAssert.AreEqual(new[] { rA, rB }, c1.recipes);
        }

        [Test]
        public void Attach_SameTextureReplacesInPlace_AndDescribeSaysSo()
        {
            var go = Avatar();
            var texA = Tex("a");
            var rA = Recipe("a", texA);
            var rB = Recipe("b", Tex("b"));
            var rA2 = Recipe("a2", texA);
            RecipeRegistration.Attach(go, rA);
            RecipeRegistration.Attach(go, rB);

            RecipeRegistration.Describe(go, texA, rA2, out var replaced, out int components);
            Assert.AreSame(rA, replaced, "置き換えるレシピを確認画面に出せない");
            Assert.AreEqual(1, components);
            Assert.AreSame(rA, RecipeRegistration.ExistingFor(go, texA));

            var c = RecipeRegistration.Attach(go, rA2);
            CollectionAssert.AreEqual(new[] { rA2, rB }, c.recipes, "同じテクスチャはその場所で置き換える");

            RecipeRegistration.Describe(go, texA, rA2, out var none, out _);
            Assert.IsNull(none, "登録済みのレシピなのに置き換えると言っている");
        }

        [Test]
        public void Attach_MergesOldDuplicateComponents_InOneUndoStep()
        {
            var go = Avatar();
            var texA = Tex("a");
            var rA = Recipe("a", texA);
            var rB = Recipe("b", Tex("b"));
            var rA2 = Recipe("a2", texA);   // 旧版で同じテクスチャを 2 回登録していた(ビルドでは先の rA だけが効く)
            var rC = Recipe("c", Tex("c"));
            go.AddComponent<IrocaRecolor>().recipes.Add(rA);
            go.AddComponent<IrocaRecolor>().recipes.Add(rB);
            go.AddComponent<IrocaRecolor>().recipes.Add(rA2);

            RecipeRegistration.Describe(go, rC.sourceTexture, rC, out _, out int components);
            Assert.AreEqual(3, components);

            Undo.IncrementCurrentGroup();
            var c = RecipeRegistration.Attach(go, rC);
            var all = go.GetComponents<IrocaRecolor>();
            Assert.AreEqual(1, all.Length, "1 つにまとまっていない");
            Assert.AreSame(c, all[0]);
            CollectionAssert.AreEqual(new[] { rA, rB, rC }, c.recipes,
                "付いていた順に並べ、同じテクスチャは先のもの(ビルドで効いていた方)だけ残す");

            Undo.PerformUndo();
            var restored = go.GetComponents<IrocaRecolor>();
            Assert.AreEqual(3, restored.Length, "1 回の Undo で元に戻らない");
            CollectionAssert.AreEqual(new[] { rA }, restored[0].recipes);
        }

        [Test]
        public void MergeComponents_LeavesASingleComponentAlone()
        {
            var go = Avatar();
            Assert.IsNull(RecipeRegistration.MergeComponents(go));
            var c = go.AddComponent<IrocaRecolor>();
            c.recipes.Add(null);   // 空の欄はまとめるときだけ落とす(1 つしか無ければ触らない)
            Assert.AreSame(c, RecipeRegistration.MergeComponents(go));
            Assert.AreEqual(1, c.recipes.Count);
        }

        [Test]
        public void OldSaveFormat_SingleRecipeMovesIntoTheList()
        {
            var go = Avatar();
            var rOld = Recipe("old", Tex("old"));
            var rNew = Recipe("new", Tex("new"));
            var c = go.AddComponent<IrocaRecolor>();
            c.recipes.Add(rNew);

            // 旧版で保存されたシーンを読み込んだのと同じ: 旧フィールド recipe に値が入った状態で復元する。
            var so = new SerializedObject(c);
            so.FindProperty("recipe").objectReferenceValue = rOld;
            so.ApplyModifiedPropertiesWithoutUndo();

            CollectionAssert.AreEqual(new[] { rOld, rNew }, c.recipes, "旧形式のレシピが一覧の先頭へ移っていない");
            so.Update();
            Assert.IsNull(so.FindProperty("recipe").objectReferenceValue, "旧フィールドが空になっていない(二重に移る)");
        }
    }
}
