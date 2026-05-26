using UnityEditor;

namespace VRCAvatarColorChanger.DebugTools
{
    /// <summary>
    /// Editor 起動時に <see cref="DebugCaptureHooks"/> へファクトリと描画フックを登録する。
    /// この asmdef ごと削除すれば <c>InitializeOnLoadMethod</c> は呼ばれず、
    /// 本体 <see cref="DebugCaptureHooks.Factory"/> は null のままで何も起きない。
    /// </summary>
    internal static class DebugBootstrap
    {
        [InitializeOnLoadMethod]
        private static void Init()
        {
            DebugCaptureHooks.Factory = DebugView.CurrentCaptureOrNull;
            DebugCaptureHooks.OnDrawFoldout += DebugView.Draw;
        }
    }
}
