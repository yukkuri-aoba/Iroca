// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>NDMF が、このいろかの非破壊の色替えに使えるか。</summary>
    internal enum NdmfSupportState {
        /// <summary>対応する版(asmdef の範囲 [1.8.0,2.0.0-0))が入っている。</summary>
        Supported,
        /// <summary>入っていない。</summary>
        Missing,
        /// <summary>1.8.0 より前の版が入っている。</summary>
        TooOld,
        /// <summary>2.0 以降(2.0 のベータ版を含む)が入っている。いろかがまだ対応していない。</summary>
        TooNew,
    }

    /// <summary>
    /// NDMF が使えないときの扱い。版の判定は本体 asmdef の versionDefines(IROCA_NDMF_PRESENT・
    /// IROCA_NDMF_TOO_NEW・IROCA_NDMF_INSTALLED)で決まる。範囲を変えるときは asmdef をそろえる。
    /// </summary>
    internal static class NdmfSupport {
        public static NdmfSupportState State =>
#if IROCA_NDMF_PRESENT
            NdmfSupportState.Supported;
#elif IROCA_NDMF_TOO_NEW
            NdmfSupportState.TooNew;
#elif IROCA_NDMF_INSTALLED
            NdmfSupportState.TooOld;
#else
            NdmfSupportState.Missing;
#endif

        /// <summary>
        /// アバターに、NDMF が無いと色替えされないもの(元テクスチャのあるレシピを持つ <see cref="IrocaRecolor"/>)が
        /// あるか。レシピが空のコンポーネントは、NDMF があっても何も変えないので数えない。EditorOnly タグの付いた
        /// オブジェクトとその下にあるものも、アップロードされない(VRChat SDK が外す)ので数えない。
        /// </summary>
        public static bool HasRecolorToApply(GameObject avatar) {
            if (avatar == null) {
                return false;
            }
            foreach (var component in avatar.GetComponentsInChildren<IrocaRecolor>(true)) {
                if (component.recipes == null || IsEditorOnly(component.transform, avatar.transform)) {
                    continue;
                }
                foreach (var recipe in component.recipes) {
                    if (recipe != null && recipe.sourceTexture != null) {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// アバターで使う(EditorOnly でない)レシピのうち、今の版より前の仕上がりで確かめたもの(並び順・重複なし)。
        /// 今の版では仕上がりが変わることがあるので、アップロードの前に確かめてもらう(<see cref="RecipeOutputVersion"/>)。
        /// </summary>
        public static System.Collections.Generic.List<IrocaRecipe> OutdatedRecipes(GameObject avatar) {
            var found = new System.Collections.Generic.List<IrocaRecipe>();
            if (avatar == null) {
                return found;
            }
            foreach (var component in avatar.GetComponentsInChildren<IrocaRecolor>(true)) {
                if (component.recipes == null || IsEditorOnly(component.transform, avatar.transform)) {
                    continue;
                }
                foreach (var recipe in component.recipes) {
                    if (recipe != null && recipe.sourceTexture != null && RecipeOutputVersion.IsOutdated(recipe)
                        && !found.Contains(recipe)) {
                        found.Add(recipe);
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// アップロードの前に、前の版の仕上がりで確かめたレシピについて確かめるか。再生(Av3Emulator などが、再生でも
        /// アップロードと同じ前処理を呼ぶ)では聞かない(再生のたびに出ると邪魔になるため。インスペクタに出ている)。
        /// 聞くなら <paramref name="outdated"/> に対象と、<paramref name="message"/> に見せる文を返す。
        /// </summary>
        public static bool ShouldConfirmOutdated(GameObject avatar, bool playMode,
            out System.Collections.Generic.List<IrocaRecipe> outdated, out string message) {
            message = null;
            outdated = playMode ? new System.Collections.Generic.List<IrocaRecipe>() : OutdatedRecipes(avatar);
            if (outdated.Count == 0) {
                return false;
            }
            var names = new System.Collections.Generic.List<string>();
            int oldest = RecipeOutputVersion.Current;
            foreach (var r in outdated) {
                names.Add(string.Format(Localization.NameQuoteFormat, r.name));
                oldest = Mathf.Min(oldest, r.outputVersion);
            }
            message = string.Format(Localization.OutdatedUploadFormat, outdated.Count, RecipeOutputVersion.ReleasesOf(oldest),
                string.Join(Localization.NameListSeparator, names));
            return true;
        }

        // t か、avatar までのその親に EditorOnly タグが付いているか。
        private static bool IsEditorOnly(Transform t, Transform avatar) {
            for (; t != null; t = t.parent) {
                if (t.CompareTag("EditorOnly")) {
                    return true;
                }
                if (t == avatar) {
                    break;
                }
            }
            return false;
        }

        /// <summary>
        /// VRChat へのアップロードを止めるか。NDMF が使えないのに色替えするものがあれば、色替えされないまま
        /// 上がらないよう止め、利用者に見せる理由を <paramref name="message"/> に返す。
        /// 再生(Av3Emulator などが、再生でもアップロードと同じ前処理を呼ぶ)では止めない。元の色で動くだけで、
        /// 理由はインスペクタの警告に出ている。
        /// </summary>
        public static bool ShouldBlockUpload(GameObject avatar, NdmfSupportState state, bool playMode, out string message) {
            message = null;
            if (state == NdmfSupportState.Supported || playMode || !HasRecolorToApply(avatar)) {
                return false;
            }
            message = Localization.UploadBlocked(state);
            return true;
        }
    }

#if IROCA_VRCSDK_AVATARS
    /// <summary>
    /// VRChat SDK のアップロード前の処理。NDMF が使えないときに「Iroca Recolor」の付いたアバターを
    /// 理由つきで止める(<see cref="NdmfSupport.ShouldBlockUpload"/>)。NDMF が使えるときは何もしない。
    /// <para>
    /// 「Iroca Recolor」は VRChat SDK があれば NDMF の有無に関係なく IEditorOnly なので、SDK はアップロードの
    /// 確認でエラーにせず、ビルドで外す。ここで止めないと、色替えされないまま黙って上がる。
    /// </para>
    /// </summary>
    internal sealed class NdmfUploadGuard : VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback {
        // ほかのツール(NDMF は -11000、VRCFury は -10000)が重い処理を始める前に止める。
        // SDK が IEditorOnly の部品を外す(-1024)より前であること。
        public int callbackOrder => -20000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (!NdmfSupport.ShouldBlockUpload(avatarGameObject, NdmfSupport.State,
                    EditorApplication.isPlayingOrWillChangePlaymode, out var message)) {
                return true;
            }
            Debug.LogError("[Iroca] " + message);
            if (!Application.isBatchMode) {
                EditorUtility.DisplayDialog(Localization.UploadBlockedTitle, message, Localization.OK);
            }
            return false;
        }
    }

    /// <summary>
    /// VRChat SDK のアップロード前の処理。前の版のいろかの仕上がりで確かめたレシピがあれば、1 回だけ確かめる
    /// (<see cref="NdmfSupport.ShouldConfirmOutdated"/>)。「このままアップロード」なら確かめたことにして続け、「やめる」なら止める。
    /// NDMF のエラー画面は使わない(報告が 1 件でもあると重大度に関係なく窓が開くので、再生のたびに出る)。
    /// batch mode では聞けないので、警告を残して続ける。
    /// </summary>
    internal sealed class OutdatedRecipeUploadGuard : VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback {
        // NDMF が使えないときに止める NdmfUploadGuard(-20000)の後、NDMF(-11000)が重い処理を始める前。
        public int callbackOrder => -19000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (!NdmfSupport.ShouldConfirmOutdated(avatarGameObject, EditorApplication.isPlayingOrWillChangePlaymode,
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
