// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案パイプラインの段別時間計測。どの段(前処理/エンコード pump/読出/
    /// デコード/後処理/ズーム再推論)が遅いかを特定するための開発者向け機能で、
    /// メニューでオンにすると 1 操作 1 行で Console に出す。
    ///
    /// 時刻取得(Stopwatch)は常時行い(数十 ns)、文字列整形とログ出力はオン時のみ。
    /// Now/MsSince は任意スレッド可。Log はメインスレッドから呼ぶこと。
    /// </summary>
    internal static class MaskSuggestPerf
    {
        const string PrefKey = "Iroca.AiSuggestPerfLog";
        const string MenuPath = "Window/Iroca/AI 提案の計測ログ";

        static bool? _enabled;

        /// <summary>計測ログを Console へ出すか(EditorPrefs 永続・既定オフ)。</summary>
        public static bool Enabled
        {
            get
            {
                _enabled ??= EditorPrefs.GetBool(PrefKey, false);
                return _enabled.Value;
            }
        }

        [MenuItem(MenuPath, priority = 201)]
        static void Toggle()
        {
            _enabled = !Enabled;
            EditorPrefs.SetBool(PrefKey, _enabled.Value);
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        /// <summary>現在時刻(Stopwatch タイムスタンプ)。</summary>
        public static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>startTimestamp(= Now の戻り値)からの経過ミリ秒。</summary>
        public static double MsSince(long startTimestamp) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>オン時のみ Console へ 1 行出す。</summary>
        public static void Log(string message)
        {
            if (Enabled) UnityEngine.Debug.Log($"[Iroca][AI計測] {message}");
        }
    }
}
