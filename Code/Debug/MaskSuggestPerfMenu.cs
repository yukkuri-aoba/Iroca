// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca.DebugTools
{
    /// <summary>
    /// AI マスク提案の段別時間計測(<see cref="MaskSuggestPerf"/>)のオン/オフ。
    /// 開発者向けなので配布物に入らない Debug アセンブリに置く。
    /// </summary>
    internal static class MaskSuggestPerfMenu
    {
        const string MenuPath = "Window/Iroca/AI 提案の計測ログ";

        [MenuItem(MenuPath, priority = 201)]
        static void Toggle() => MaskSuggestPerf.SetEnabled(!MaskSuggestPerf.Enabled);

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, MaskSuggestPerf.Enabled);
            return true;
        }
    }
}
