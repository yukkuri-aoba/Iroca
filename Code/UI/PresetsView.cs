// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// プリセット一覧 UI、保存 / 読込 / JSON 入出力を担当する。
    /// マスクペイント・プレビュー生成・エクスポートには関与しない。
    /// </summary>
    [System.Serializable]
    internal class PresetsView
    {
        public bool presetsFoldout;
        public string presetSaveName = "Preset";
        public bool presetStorageProject = true;
        public bool presetIncludeMasks = true;
        public bool presetApplyMasks = true;
        // マスクオプションと JSON 入出力をまとめる低頻度操作の折りたたみ（既定で閉じる）。
        public bool presetAdvancedFoldout;

        [System.NonSerialized] private IrocaWindow _host;

        private string ActivePresetFolder
            => presetStorageProject ? PresetStore.ProjectPresetFolder : PresetStore.UserPresetFolder;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
        }

        public void Draw()
        {
            presetsFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(presetsFoldout, Localization.Presets);
            if (!presetsFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            // 使い方の説明は各コントロールのツールチップに委ね、常時表示の HelpBox は置かない。

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(presetStorageProject, new GUIContent(Localization.PresetStorageProject, Localization.PresetStorageProjectTooltip), EditorStyles.miniButtonLeft) && !presetStorageProject)
                presetStorageProject = true;
            if (GUILayout.Toggle(!presetStorageProject, new GUIContent(Localization.PresetStorageUser, Localization.PresetStorageUserTooltip), EditorStyles.miniButtonRight) && presetStorageProject)
                presetStorageProject = false;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            presetSaveName = EditorGUILayout.TextField(
                new GUIContent(Localization.PresetName, Localization.PresetNameTooltip),
                presetSaveName);
            if (GUILayout.Button(new GUIContent(Localization.SavePreset, Localization.SavePresetTooltip), GUILayout.Width(IrocaUiConsts.Layout.SmallButtonWidth)))
                SavePreset(presetSaveName);
            EditorGUILayout.EndHorizontal();

            // 低頻度のオプション（マスク同梱/適用・JSON 入出力）は折りたたみへ。
            presetAdvancedFoldout = EditorGUILayout.Foldout(presetAdvancedFoldout,
                new GUIContent(Localization.PresetAdvanced, Localization.PresetAdvancedTooltip), true);
            if (presetAdvancedFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    presetIncludeMasks = EditorGUILayout.ToggleLeft(
                        new GUIContent(Localization.PresetIncludeMasks, Localization.PresetIncludeMasksTooltip),
                        presetIncludeMasks);
                    presetApplyMasks = EditorGUILayout.ToggleLeft(
                        new GUIContent(Localization.PresetApplyMasks, Localization.PresetApplyMasksTooltip),
                        presetApplyMasks);

                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(new GUIContent(Localization.ExportJson, Localization.ExportJsonTooltip)))
                        ExportPresetJson();
                    if (GUILayout.Button(new GUIContent(Localization.ImportJson, Localization.ImportJsonTooltip)))
                        ImportPresetJson();
                    EditorGUILayout.EndHorizontal();
                }
            }

            string[] files = PresetStore.ListJson(ActivePresetFolder);

            if (files.Length == 0)
            {
                EditorGUILayout.HelpBox(Localization.NoPresets, MessageType.None);
            }
            else
            {
                foreach (string file in files)
                {
                    string pname = Path.GetFileNameWithoutExtension(file);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(pname, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(new GUIContent(Localization.LoadPreset, Localization.LoadPresetTooltip),
                            GUILayout.Width(IrocaWindow.FitButtonWidth(40f, Localization.LoadPreset))))
                        LoadPreset(file);
                    if (GUILayout.Button(new GUIContent("×", Localization.DeletePresetTooltip), GUILayout.Width(IrocaUiConsts.Layout.RemoveButtonWidth)))
                    {
                        if (EditorUtility.DisplayDialog(Localization.Confirm,
                            Localization.DeletePresetConfirm(pname), Localization.OK, Localization.Cancel))
                        {
                            bool ok = PresetStore.Delete(file);
                            Notify(ok ? Localization.PresetDeleted : $"{Localization.Error}: {Localization.PresetDeleteFailed}");
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        private void SavePreset(string name)
        {
            string existingPath = PresetStore.PresetFilePath(ActivePresetFolder, name);
            if (File.Exists(existingPath))
            {
                if (!EditorUtility.DisplayDialog(
                        Localization.Confirm,
                        Localization.PresetOverwriteConfirm(Path.GetFileNameWithoutExtension(existingPath)),
                        Localization.Overwrite,
                        Localization.Cancel))
                {
                    return;
                }
            }

            var data = BuildPresetData(name);
            bool ok = presetStorageProject
                ? PresetStore.SaveToProject(name, data)
                : PresetStore.SaveToUser(name, data);
            Notify(ok ? Localization.PresetSaved : $"{Localization.Error}: {Localization.PresetSaveFailed}");
        }

        // ウィンドウ右下に非モーダル通知を出す（EditorUtility.DisplayDialog は Editor 全体を
        // ブロックするため、結果通知には ShowNotification を使う。ExportView.NotifyError と同型）。
        private void Notify(string message)
        {
            _host?.ShowNotification(new GUIContent(message));
        }

        private IrocaPresetData BuildPresetData(string presetName)
        {
            _host.EnsureAllZoneIds();
            var session = _host.Session;

            // 複製して書く（id は引き継ぐ）。スポイト位置はテクスチャ固有なので複製からだけ外す。
            var zonesCopy = IrocaPresetData.WithoutSamplePositions(session.zones);

            var data = new IrocaPresetData
            {
                name = presetName,
                zones = zonesCopy,
            };
            // 編集モード(かんたん/通常/上級)は UI 表示の好みであり色設定ではないため、
            // プリセットには保存しない（読込で表示モードが勝手に変わらないように）。
            RecolorSettings.From(session).CopyTo(data);

            if (presetIncludeMasks)
            {
                _host.WriteMaskToPreset(data);
            }

            return data;
        }

        // internal は OldFormatLoadTests が古い版のプリセットを読み込むため。
        internal void LoadPreset(string filePath)
        {
            var data = PresetStore.Load(filePath);
            if (data == null)
            {
                Notify($"{Localization.Error}: {Localization.PresetLoadFailed}");
                return;
            }

            // ゾーン・処理設定・(選んでいれば)マスクを 1 ステップの Undo で読み込む(EditingSession.ApplyPreset)。
            // 編集モードはプリセットで上書きしない（現在の表示モードを維持）。
            if (_host.Editing.ApplyPreset(data, presetApplyMasks))
                _host.SaveMasksToFile();

            _host.ResetActiveMaskTarget();
            Notify(Localization.PresetLoaded);
        }

        private void ExportPresetJson()
        {
            string path = EditorUtility.SaveFilePanel(
                Localization.ExportJson, "", "Iroca_preset", "json");
            if (string.IsNullOrEmpty(path)) return;
            var data = BuildPresetData(Path.GetFileNameWithoutExtension(path));
            bool ok = PresetStore.SaveToPath(path, data);
            Notify(ok ? Localization.PresetSaved : $"{Localization.Error}: {Localization.PresetSaveFailed}");
        }

        private void ImportPresetJson()
        {
            string path = EditorUtility.OpenFilePanel(
                Localization.ImportJson, "", "json");
            if (string.IsNullOrEmpty(path)) return;
            LoadPreset(path);
        }
    }
}
