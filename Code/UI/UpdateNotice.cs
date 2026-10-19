// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 新しい版のお知らせ(<see cref="UpdateCheck"/>)の帯と、それを止める Preferences の項目。
    /// 更新は VCC / ALCOM で行うので、ここでは変更履歴とマニュアルの手順へ案内するだけにする。
    /// </summary>
    internal static class UpdateNotice
    {
        public const string PreferencesPath = "Preferences/いろか";
        const string ManualAnchor = "#install-update";

        /// <summary>
        /// いろかのウィンドウに出す帯。頃合いなら確認も始める(ウィンドウを開いた人だけが通信する)。
        /// 新しい版が無ければ何も描かない。
        /// </summary>
        public static void DrawBanner()
        {
            UpdateCheck.CheckIfDue();
            string latest = UpdateCheck.AvailableVersion;
            if (latest == null) return;

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                string.Format(Localization.UpdateNoticeFormat, latest, UpdateCheck.CurrentVersion),
                MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(Localization.UpdateNoticeChanges, Localization.UpdateNoticeChangesTooltip)))
                Application.OpenURL(UpdateCheck.ChangelogUrl);
            if (GUILayout.Button(new GUIContent(Localization.UpdateNoticeHowTo, Localization.UpdateNoticeHowToTooltip)))
                Application.OpenURL(IrocaManual.OnlineUrl + ManualAnchor);
            if (GUILayout.Button(new GUIContent(Localization.UpdateNoticeDismiss, Localization.UpdateNoticeDismissTooltip)))
                UpdateCheck.Dismiss(latest);
            EditorGUILayout.EndHorizontal();
        }

        [SettingsProvider]
        static SettingsProvider CreatePreferences()
        {
            return new SettingsProvider(PreferencesPath, SettingsScope.User)
            {
                label = Localization.PreferencesLabel,
                guiHandler = _ =>
                {
                    EditorGUILayout.Space(4);
                    bool enabled = EditorGUILayout.ToggleLeft(
                        new GUIContent(Localization.UpdateCheckToggle, Localization.UpdateCheckToggleTooltip),
                        UpdateCheck.Enabled);
                    if (enabled != UpdateCheck.Enabled) UpdateCheck.Enabled = enabled;
                    EditorGUILayout.LabelField(Localization.UpdateCheckPrivacy, EditorStyles.wordWrappedMiniLabel);
                },
                keywords = new[] { "update", "version", "更新", "版" },
            };
        }
    }
}
