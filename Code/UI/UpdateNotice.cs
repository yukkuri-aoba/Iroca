// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 新しい版のお知らせ(<see cref="UpdateCheck"/>)の帯と、Preferences の「いろか」の頁(お知らせを止める項目と、
    /// スポイトのクリックで自動調整するかの項目)。
    /// 更新は VCC / ALCOM で行うので、ここでは変更履歴とマニュアルの手順へ案内するだけにする。
    /// </summary>
    internal static class UpdateNotice {
        public const string PreferencesPath = "Preferences/いろか";
        const string ManualAnchor = "#install-update";

        /// <summary>
        /// いろかのウィンドウに出す帯。頃合いなら確認も始める(ウィンドウを開いた人だけが通信する)。
        /// 新しい版が無ければ何も描かない。
        /// </summary>
        public static void DrawBanner() {
            UpdateCheck.CheckIfDue();
            string latest = UpdateCheck.AvailableVersion;
            if (latest == null) {
                return;
            }

            EditorGUILayout.Space(2);
            int pressed = NoticeBox.Draw(
                string.Format(Localization.UpdateNoticeFormat, latest, UpdateCheck.CurrentVersion),
                MessageType.Info,
                TempContent.Of(Localization.UpdateNoticeChanges, Localization.UpdateNoticeChangesTooltip),
                TempContent.Of(Localization.UpdateNoticeHowTo, Localization.UpdateNoticeHowToTooltip),
                TempContent.Of(Localization.UpdateNoticeDismiss, Localization.UpdateNoticeDismissTooltip));
            if (pressed == 0) {
                Application.OpenURL(UpdateCheck.ChangelogUrl);
            } else if (pressed == 1) {
                Application.OpenURL(IrocaManual.OnlineUrl + ManualAnchor);
            } else if (pressed == 2) {
                UpdateCheck.Dismiss(latest);
            }
        }

        [SettingsProvider]
        static SettingsProvider CreatePreferences() {
            return new SettingsProvider(PreferencesPath, SettingsScope.User) {
                label = Localization.PreferencesLabel,
                guiHandler = _ => {
                    EditorGUILayout.Space(4);
                    // スポイトでクリックしたら、その場で自動調整までかけてから塗るか(IrocaWindow.AutoTuneOnPick。2026-11-23)。
                    // 窓の縦幅を使わないよう、PC ごとの設定の置き場であるここに置く。
                    bool autoTune = EditorGUILayout.ToggleLeft(
                        TempContent.Of(Localization.AutoTuneOnPickToggle, Localization.AutoTuneOnPickToggleTooltip),
                        IrocaWindow.AutoTuneOnPick);
                    if (autoTune != IrocaWindow.AutoTuneOnPick) {
                        IrocaWindow.AutoTuneOnPick = autoTune;
                    }
                    EditorGUILayout.Space(4);
                    bool enabled = EditorGUILayout.ToggleLeft(
                        TempContent.Of(Localization.UpdateCheckToggle, Localization.UpdateCheckToggleTooltip),
                        UpdateCheck.Enabled);
                    if (enabled != UpdateCheck.Enabled) {
                        UpdateCheck.Enabled = enabled;
                    }
                    EditorGUILayout.LabelField(Localization.UpdateCheckPrivacy, EditorStyles.wordWrappedMiniLabel);
                },
                keywords = new[] { "update", "version", "更新", "版", "auto-tune", "eyedropper", "自動調整", "スポイト" },
            };
        }
    }
}
