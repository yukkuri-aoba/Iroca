// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca.DebugTools
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
            PerfView.Register();
            DebugCaptureHooks.OnDrawFoldout += PerfView.Draw;
        }
    }
}
