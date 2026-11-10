// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Iroca.EditorTests {
    /// <summary>
    /// NDMF が使えないときの扱い(<see cref="NdmfSupport"/>)。asmdef の版の範囲を Unity がそのとおりに読んでいるか、
    /// VRChat へのアップロードを止める条件と案内、VRChat SDK への組み込み。
    /// </summary>
    public class NdmfSupportTests {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown() {
            foreach (var o in _created) {
                if (o != null) {
                    Object.DestroyImmediate(o);
                }
            }
            _created.Clear();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        private static string InstalledVersion(string package) =>
            PackageInfo.FindForAssetPath($"Packages/{package}/package.json")?.version;

        // 版の比べ方は SemVer(2.0.0-beta.1 は 2.0.0 より前)。いろかが対応するのは 1.8.0 以上の 1.x だけ。
        private static NdmfSupportState Expected(string ndmfVersion) {
            if (ndmfVersion == null) {
                return NdmfSupportState.Missing;
            }
            int dash = ndmfVersion.IndexOf('-');
            var core = (dash < 0 ? ndmfVersion : ndmfVersion.Substring(0, dash)).Split('.').Select(int.Parse).ToArray();
            if (core[0] >= 2) {
                return NdmfSupportState.TooNew;
            }
            var v = (core[0], core[1], core[2]);
            bool atLeast180 = v.CompareTo((1, 8, 0)) > 0 || (v == (1, 8, 0) && dash < 0);
            return atLeast180 ? NdmfSupportState.Supported : NdmfSupportState.TooOld;
        }

        [Test]
        public void VersionDefines_FollowTheInstalledPackages() {
            // 範囲の式("[1.8.0,2.0.0-0)" など)は Unity が読む。書き間違えると define が付かず、NDMF 連携が
            // 黙って消える。入っているパッケージの版から、各 asmdef に付くはずの define を出して照らす。
            string ndmf = InstalledVersion("nadena.dev.ndmf");
            var state = Expected(ndmf);
            bool vrcBase = InstalledVersion("com.vrchat.base") != null;
            bool vrcAvatars = InstalledVersion("com.vrchat.avatars") != null;
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor).ToDictionary(a => a.name);

            void Check(string assembly, string define, bool expected) {
                Assert.IsTrue(assemblies.TryGetValue(assembly, out var a), $"{assembly} が見つからない");
                Assert.AreEqual(expected, a.defines.Contains(define),
                    $"{assembly} の {define}（NDMF {ndmf ?? "なし"}・VRChat SDK base {vrcBase}・avatars {vrcAvatars}）");
            }
            bool supported = state == NdmfSupportState.Supported;
            Check("com.yukkuri-aoba.iroca.Runtime", "IROCA_NDMF_PRESENT", supported);
            Check("com.yukkuri-aoba.iroca.Runtime", "IROCA_VRCSDK_BASE", vrcBase);
            Check("com.yukkuri-aoba.iroca.Editor.Services", "IROCA_NDMF_PRESENT", supported);
            Check("com.yukkuri-aoba.iroca.Editor.Services", "IROCA_NDMF_INSTALLED", ndmf != null);
            Check("com.yukkuri-aoba.iroca.Editor.Services", "IROCA_NDMF_TOO_NEW", state == NdmfSupportState.TooNew);
            Check("com.yukkuri-aoba.iroca.Editor.Services", "IROCA_VRCSDK_AVATARS", vrcAvatars);
            Check("com.yukkuri-aoba.iroca.Editor", "IROCA_NDMF_PRESENT", supported);
            Check("Iroca.EditorTests", "IROCA_NDMF_PRESENT", supported);
            if (supported) {
                Check("Iroca.NdmfIntegration", "IROCA_NDMF_PRESENT", true);
            }
            Assert.AreEqual(state, NdmfSupport.State);
        }

        [Test]
        public void Upload_IsStoppedOnlyWhenNdmfCannotRecolorSomething() {
            var avatar = Track(new GameObject("avatar"));
            var outfit = new GameObject("outfit");
            outfit.transform.SetParent(avatar.transform, false);
            outfit.SetActive(false);   // 非表示の衣装でも、アニメーションで出せば色替えされているはず
            bool Blocks(NdmfSupportState s, bool playMode = false) =>
                NdmfSupport.ShouldBlockUpload(avatar, s, playMode, out _);

            Assert.IsFalse(Blocks(NdmfSupportState.Missing), "いろかの部品が無いのに止めた");
            var component = outfit.AddComponent<IrocaRecolor>();
            component.recipes.Add(null);
            component.recipes.Add(Track(ScriptableObject.CreateInstance<IrocaRecipe>()));
            Assert.IsFalse(Blocks(NdmfSupportState.Missing), "空のレシピしか無い（NDMF があっても何も変わらない）のに止めた");

            var recipe = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            recipe.sourceTexture = Track(new Texture2D(4, 4));
            component.recipes.Add(recipe);
            var messages = new HashSet<string>();
            foreach (var s in new[] { NdmfSupportState.Missing, NdmfSupportState.TooOld, NdmfSupportState.TooNew }) {
                Assert.IsTrue(NdmfSupport.ShouldBlockUpload(avatar, s, false, out var message), $"{s} で止めていない");
                StringAssert.Contains("Iroca Recolor", message);
                messages.Add(message);
            }
            Assert.AreEqual(3, messages.Count, "入っていない・古い・新しすぎるで、直し方の案内が分かれていない");

            Assert.IsFalse(Blocks(NdmfSupportState.Supported), "NDMF が使えるのに止めた");
            Assert.IsFalse(Blocks(NdmfSupportState.Missing, playMode: true), "再生（アップロードではない）で止めた");
        }

        [Test]
        public void Upload_IsNotStoppedByRecolorsLeftUnderEditorOnly() {
            // EditorOnly タグの付いたオブジェクト(とその下)は VRChat SDK が外すのでアップロードされない。
            // そこに残った「Iroca Recolor」では止めない。
            var avatar = Track(new GameObject("avatar"));
            var spare = new GameObject("spare");
            spare.transform.SetParent(avatar.transform, false);
            spare.tag = "EditorOnly";
            var child = new GameObject("outfit");
            child.transform.SetParent(spare.transform, false);
            var recipe = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            recipe.sourceTexture = Track(new Texture2D(4, 4));
            child.AddComponent<IrocaRecolor>().recipes.Add(recipe);

            Assert.IsFalse(NdmfSupport.ShouldBlockUpload(avatar, NdmfSupportState.Missing, false, out _),
                "EditorOnly の下(アップロードされない)の部品で止めた");
            spare.tag = "Untagged";
            Assert.IsTrue(NdmfSupport.ShouldBlockUpload(avatar, NdmfSupportState.Missing, false, out _),
                "アップロードされる部品なのに止めていない");
        }

        private IrocaRecipe OutputRecipe(string name, int outputVersion, bool withSource = true) {
            var recipe = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            recipe.name = name;
            recipe.outputVersion = outputVersion;
            if (withSource) {
                recipe.sourceTexture = Track(new Texture2D(4, 4));
            }
            return recipe;
        }

        // 前の版のいろかの仕上がりで確かめたレシピ(RecipeOutputVersion)は、アップロードの前に 1 回確かめる。アバターで使う
        // (EditorOnly でない)元テクスチャのあるものだけを、重複なしで数える。再生では聞かない。
        [Test]
        public void OutdatedRecipes_AreConfirmedOnceBeforeUpload_NotInPlayMode() {
            var avatar = Track(new GameObject("avatar"));
            var outfit = new GameObject("outfit");
            outfit.transform.SetParent(avatar.transform, false);
            var old = OutputRecipe("old-recipe", 0);
            var current = OutputRecipe("current-recipe", RecipeOutputVersion.Current);
            var noSource = OutputRecipe("no-source", 0, withSource: false);
            var c = outfit.AddComponent<IrocaRecolor>();
            c.recipes.AddRange(new[] { old, current, null, noSource });
            avatar.AddComponent<IrocaRecolor>().recipes.Add(old);   // 同じレシピを別の所でも使う
            var spare = new GameObject("spare");
            spare.transform.SetParent(avatar.transform, false);
            spare.tag = "EditorOnly";
            spare.AddComponent<IrocaRecolor>().recipes.Add(OutputRecipe("spare-recipe", 0));

            CollectionAssert.AreEqual(new[] { old }, NdmfSupport.OutdatedRecipes(avatar),
                "今の版のもの・元テクスチャの無いもの・EditorOnly の下のものを数えた／重複した");
            Assert.IsTrue(NdmfSupport.ShouldConfirmOutdated(avatar, false, out var outdated, out var message));
            CollectionAssert.AreEqual(new[] { old }, outdated);
            StringAssert.Contains("old-recipe", message);
            StringAssert.Contains(RecipeOutputVersion.ReleasesOf(0), message);
            Assert.IsFalse(NdmfSupport.ShouldConfirmOutdated(avatar, true, out _, out _), "再生(アップロードではない)で聞いた");

            old.outputVersion = RecipeOutputVersion.Current;
            Assert.IsFalse(NdmfSupport.ShouldConfirmOutdated(avatar, false, out _, out _), "確かめた後も聞いた");
        }

        // VRChat SDK のアップロード前の処理に組み込まれ、NDMF が使えないときに止める確認の後・NDMF などの前に動く。
        // batch mode では聞けないので、警告だけで続ける(確かめたことにはしない)。
        [Test]
        public void VrchatSdk_RunsTheOutdatedRecipeGuard_AfterTheNdmfGuard_AndDoesNotAskInBatchMode() {
            var callback = FindType("VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback");
            if (callback == null) {
                Assert.Ignore("VRChat SDK が無い");
            }
            var guard = typeof(NdmfSupport).Assembly.GetType("Iroca.OutdatedRecipeUploadGuard");
            var ndmfGuard = typeof(NdmfSupport).Assembly.GetType("Iroca.NdmfUploadGuard");
            Assert.IsNotNull(guard, "VRChat SDK があるのに、前の版のレシピの確認がコンパイルされていない");
            Assert.IsTrue(callback.IsAssignableFrom(guard));
            object instance = Activator.CreateInstance(guard, nonPublic: true);
            int order = (int)guard.GetProperty("callbackOrder").GetValue(instance);
            int ndmfOrder = (int)ndmfGuard.GetProperty("callbackOrder").GetValue(Activator.CreateInstance(ndmfGuard, nonPublic: true));
            Assert.Greater(order, ndmfOrder, "NDMF が使えないときに止める確認より先に聞く");
            Assert.Less(order, -11000, "NDMF などの前処理より先に確かめていない");

            if (!Application.isBatchMode) {
                return;   // 画面のある Unity では確認の画面が出るので呼ばない
            }
            var avatar = Track(new GameObject("avatar"));
            var old = OutputRecipe("old-recipe", 0);
            avatar.AddComponent<IrocaRecolor>().recipes.Add(old);
            Assert.IsTrue((bool)guard.GetMethod("OnPreprocessAvatar").Invoke(instance, new object[] { avatar }),
                "batch mode なのにアップロードを止めた");
            Assert.AreEqual(0, old.outputVersion, "聞いていないのに確かめたことにした");
        }

        private static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(t => t != null);

        [Test]
        public void VrchatSdk_TreatsRecolorAsEditorOnly_AndRunsTheUploadGuardFirst() {
            var editorOnly = FindType("VRC.SDKBase.IEditorOnly");
            var callback = FindType("VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback");
            if (editorOnly == null || callback == null) {
                Assert.Ignore("VRChat SDK が無い");
            }

            // NDMF の有無や版に関係なく、SDK に「使えないコンポーネント」として消させない。
            Assert.IsTrue(editorOnly.IsAssignableFrom(typeof(IrocaRecolor)), "Iroca Recolor が IEditorOnly でない");

            var guard = typeof(NdmfSupport).Assembly.GetType("Iroca.NdmfUploadGuard");
            Assert.IsNotNull(guard, "VRChat SDK があるのに、アップロードの確認がコンパイルされていない（IROCA_VRCSDK_AVATARS）");
            Assert.IsTrue(callback.IsAssignableFrom(guard));
            object instance = Activator.CreateInstance(guard, nonPublic: true);
            int order = (int)guard.GetProperty("callbackOrder").GetValue(instance);
            Assert.Less(order, -11000, "NDMF などの前処理より先に確かめていない");

            // NDMF が使えるホストでは素通しする(使えないホストでは確認の画面が出るので呼ばない)。
            if (NdmfSupport.State != NdmfSupportState.Supported) {
                return;
            }
            var avatar = Track(new GameObject("avatar"));
            var recipe = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            recipe.sourceTexture = Track(new Texture2D(4, 4));
            avatar.AddComponent<IrocaRecolor>().recipes.Add(recipe);
            Assert.IsTrue((bool)guard.GetMethod("OnPreprocessAvatar").Invoke(instance, new object[] { avatar }),
                "NDMF が使えるのにアップロードを止めた");
        }
    }
}
