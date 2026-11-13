// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// アップロードの前の、「前の版のいろかの仕上がりで確かめたレシピ」の確認(<see cref="RecipeOutputVersion"/>)。
    /// NDMF が使えるかどうかとは関係が無い(NDMF が使えないときに止めるのは <see cref="NdmfSupport"/>)。
    /// </summary>
    internal static class OutdatedRecipeUpload {
        /// <summary>
        /// アバターで使う(EditorOnly でない)レシピのうち、今の版より前の仕上がりで確かめたもの(並び順・重複なし)。
        /// 今の版では仕上がりが変わることがあるので、アップロードの前に確かめてもらう。
        /// </summary>
        public static List<IrocaRecipe> Find(GameObject avatar) {
            var found = new List<IrocaRecipe>();
            foreach (var recipe in RecipeRegistration.UploadedRecipes(avatar)) {
                if (RecipeOutputVersion.IsOutdated(recipe) && !found.Contains(recipe)) {
                    found.Add(recipe);
                }
            }
            return found;
        }

        /// <summary>
        /// アップロードの前に、前の版の仕上がりで確かめたレシピについて確かめるか。再生(Av3Emulator などが、再生でも
        /// アップロードと同じ前処理を呼ぶ)では聞かない(再生のたびに出ると邪魔になるため。インスペクタに出ている)。
        /// 聞くなら <paramref name="outdated"/> に対象と、<paramref name="message"/> に見せる文を返す。
        /// </summary>
        public static bool ShouldConfirm(GameObject avatar, bool playMode,
            out List<IrocaRecipe> outdated, out string message) {
            message = null;
            outdated = playMode ? new List<IrocaRecipe>() : Find(avatar);
            if (outdated.Count == 0) {
                return false;
            }
            var names = new List<string>();
            int oldest = RecipeOutputVersion.Current;
            foreach (var r in outdated) {
                names.Add(string.Format(Localization.NameQuoteFormat, r.name));
                oldest = Mathf.Min(oldest, r.outputVersion);
            }
            message = string.Format(Localization.OutdatedUploadFormat, outdated.Count, RecipeOutputVersion.ReleasesOf(oldest),
                string.Join(Localization.NameListSeparator, names));
            return true;
        }
    }

#if IROCA_VRCSDK_AVATARS
    /// <summary>
    /// VRChat SDK のアップロード前の処理。前の版のいろかの仕上がりで確かめたレシピがあれば、1 回だけ確かめる
    /// (<see cref="OutdatedRecipeUpload.ShouldConfirm"/>)。「このままアップロード」なら確かめたことにして続け、「やめる」なら止める。
    /// NDMF のエラー画面は使わない(報告が 1 件でもあると重大度に関係なく窓が開くので、再生のたびに出る)。
    /// batch mode では聞けないので、警告を残して続ける。
    /// </summary>
    internal sealed class OutdatedRecipeUploadGuard : VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback {
        // NDMF が使えないときに止める NdmfUploadGuard(-20000)の後、NDMF(-11000)が重い処理を始める前。
        public int callbackOrder => -19000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (!OutdatedRecipeUpload.ShouldConfirm(avatarGameObject, EditorApplication.isPlayingOrWillChangePlaymode,
                    out var outdated, out var message)) {
                return true;
            }
            if (Application.isBatchMode) {
                Debug.LogWarning("[Iroca] " + message);
                return true;
            }
            if (!EditorUtility.DisplayDialog(Localization.OutdatedUploadTitle, message,
                    Localization.OutdatedUploadContinue, Localization.OutdatedUploadStop)) {
                Debug.Log("[Iroca] " + Localization.OutdatedUploadStopped);
                return false;
            }
            foreach (var recipe in outdated) {
                RecipeStore.MarkOutputChecked(recipe);
            }
            return true;
        }
    }
#endif
}
