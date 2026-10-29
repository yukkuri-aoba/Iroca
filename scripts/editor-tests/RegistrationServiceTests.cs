// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 「アバターに非破壊で登録」の判断(<see cref="RegistrationService"/>)。ウィンドウ(確認画面)と自動化(警告)が同じ判断を
    /// 使う: 使うレシピの順(結び付き → 登録先のもの → プロジェクトにちょうど 1 つ)と、ほかのアバターのレシピを流用しない
    /// 選び方、登録の計画(書き出した画像・共有のマテリアル)、共有のマテリアルを戻さない登録。
    /// 登録の部品そのものは RecipeRegistrationTests、自動化の通しは AutomationTests が見る。
    /// </summary>
    public class RegistrationServiceTests
    {
        private readonly List<Object> _created = new List<Object>();
        private TestAssets _assets;

        [SetUp]
        public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
            {
                if (o == null) continue;
                // 登録は Undo に記録する。記録を残したまま消すと、Undo の巻き戻しで空の GameObject として復活する。
                if (o is GameObject go)
                    foreach (var c in go.GetComponents<Component>())
                        Undo.ClearUndo(c);
                Undo.ClearUndo(o);
                Object.DestroyImmediate(o);
            }
            _created.Clear();
            _assets.Dispose();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        private Texture2D Png(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>(
            _assets.WritePng(name, TestAssets.Solid(4, 4, new Color32(200, 40, 40, 255)), 4, 4));

        private GameObject Make(string name, GameObject parent = null)
        {
            var go = Track(new GameObject(name));
            if (parent != null) go.transform.SetParent(parent.transform, false);
            return go;
        }

        private IrocaRecipe ProjectRecipe(Texture2D source)
            => RecipeStore.Create(source, IrocaSessionState.CreateDefault(), _assets.Folder);

        [Test]
        public void ChooseRecipe_BoundThenTargetsOwnThenTheOnlyOneInTheProject()
        {
            var src = Png("costume");
            var inProject = ProjectRecipe(src);
            var avatar = Make("avatar");

            Assert.AreSame(inProject, RegistrationService.ChooseRecipe(avatar, src, null, true, out _, out _),
                "プロジェクトにちょうど 1 つならそれ");

            var own = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            own.sourceTexture = src;
            RecipeRegistration.Attach(avatar, own);
            Assert.AreSame(own, RegistrationService.ChooseRecipe(avatar, src, null, true, out _, out _),
                "登録先に既にあるもの(登録し直し)");

            var bound = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            Assert.AreSame(bound, RegistrationService.ChooseRecipe(avatar, src, bound, true, out _, out _),
                "結び付いているものがいちばん先");
        }

        [Test]
        public void ChooseRecipe_DoesNotShareAnotherAvatarsRecipeWhenAsked()
        {
            var src = Png("costume");
            var recipe = ProjectRecipe(src);
            var a = Make("avatar A");
            var b = Make("avatar B");
            var aBody = Make("body", a);
            var usedOnA = RecipeRegistration.Attach(aBody, recipe);

            Assert.AreSame(recipe, RegistrationService.ChooseRecipe(b, src, null, shareWithOtherAvatars: true,
                out var notShared, out _), "ウィンドウは流用する(確認画面で知らせる)");
            Assert.IsNull(notShared);

            Assert.IsNull(RegistrationService.ChooseRecipe(b, src, null, shareWithOtherAvatars: false,
                out notShared, out var users), "自動化は流用しない(新しく作る)");
            Assert.AreSame(recipe, notShared);
            CollectionAssert.AreEqual(new[] { usedOnA }, users);

            var aHead = Make("head", a);
            Assert.AreSame(recipe, RegistrationService.ChooseRecipe(aHead, src, null, shareWithOtherAvatars: false,
                out _, out _), "同じアバターの中なら流用する");
            Assert.AreSame(recipe, RegistrationService.ChooseRecipe(null, src, null, shareWithOtherAvatars: false,
                out _, out _), "登録先が無い(書き出し・比較画像)なら、そのまま使う");
        }

        [Test]
        public void Describe_AndSwitchAndAttach_KeepSharedMaterialsWhenAsked()
        {
            var src = Png("costume");
            var exported = Png("costume_recolored");
            var sharedMat = new Material(Shader.Find("Standard")) { mainTexture = exported };
            AssetDatabase.CreateAsset(sharedMat, _assets.Folder + "/shared.mat");
            var ownMat = new Material(Shader.Find("Standard")) { mainTexture = exported };
            AssetDatabase.CreateAsset(ownMat, _assets.Folder + "/own.mat");
            var avatar = Make("avatar");
            Make("mesh", avatar).AddComponent<MeshRenderer>().sharedMaterials = new[] { sharedMat, ownMat };
            Make("other").AddComponent<MeshRenderer>().sharedMaterial = sharedMat;
            var recipe = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            recipe.sourceTexture = src;

            var plan = RegistrationService.Describe(avatar, src, recipe);
            CollectionAssert.AreEquivalent(new[] { sharedMat, ownMat }, plan.exportUsers);
            Assert.AreEqual(2, plan.switchable);
            CollectionAssert.AreEqual(new[] { sharedMat }, plan.shared);
            Assert.AreEqual(1, plan.sharedPlaces);
            Assert.AreEqual(0, plan.components);
            Assert.IsEmpty(plan.otherUsers);

            Undo.IncrementCurrentGroup();
            Assert.AreEqual(1, RegistrationService.SwitchAndAttach(plan, recipe, keepShared: true, out var component));
            Assert.AreSame(src, ownMat.mainTexture, "登録先だけのマテリアルは戻す");
            Assert.AreSame(exported, sharedMat.mainTexture, "共有のマテリアルは戻さない");
            CollectionAssert.Contains(component.recipes, recipe);
            Undo.ClearUndo(ownMat);
        }
    }
}
