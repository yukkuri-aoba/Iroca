// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>NDMF が、このいろかの非破壊の色替えに使えるか。</summary>
    internal enum NdmfSupportState
    {
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
    internal static class NdmfSupport
    {
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
        public static bool HasRecolorToApply(GameObject avatar)
        {
            if (avatar == null) return false;
            foreach (var component in avatar.GetComponentsInChildren<IrocaRecolor>(true))
            {
                if (component.recipes == null || IsEditorOnly(component.transform, avatar.transform)) continue;
                foreach (var recipe in component.recipes)
                    if (recipe != null && recipe.sourceTexture != null) return true;
            }
            return false;
        }

        // t か、avatar までのその親に EditorOnly タグが付いているか。
        private static bool IsEditorOnly(Transform t, Transform avatar)
        {
            for (; t != null; t = t.parent)
            {
                if (t.CompareTag("EditorOnly")) return true;
                if (t == avatar) break;
            }
            return false;
        }

        /// <summary>
        /// VRChat へのアップロードを止めるか。NDMF が使えないのに色替えするものがあれば、色替えされないまま
        /// 上がらないよう止め、利用者に見せる理由を <paramref name="message"/> に返す。
        /// 再生(Av3Emulator などが、再生でもアップロードと同じ前処理を呼ぶ)では止めない。元の色で動くだけで、
        /// 理由はインスペクタの警告に出ている。
        /// </summary>
        public static bool ShouldBlockUpload(GameObject avatar, NdmfSupportState state, bool playMode, out string message)
        {
            message = null;
            if (state == NdmfSupportState.Supported || playMode || !HasRecolorToApply(avatar)) return false;
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
    internal sealed class NdmfUploadGuard : VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback
    {
        // ほかのツール(NDMF は -11000、VRCFury は -10000)が重い処理を始める前に止める。
        // SDK が IEditorOnly の部品を外す(-1024)より前であること。
        public int callbackOrder => -20000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            if (!NdmfSupport.ShouldBlockUpload(avatarGameObject, NdmfSupport.State,
                    EditorApplication.isPlayingOrWillChangePlaymode, out var message))
                return true;
            Debug.LogError("[Iroca] " + message);
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog(Localization.UploadBlockedTitle, message, Localization.OK);
            return false;
        }
    }
#endif
}
