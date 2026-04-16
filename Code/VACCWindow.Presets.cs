using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // プリセット
        private bool presetsFoldout;
        private string presetSaveName = "Preset";
        private bool presetStorageProject = true;
        private Vector2 presetScrollPos;

        private static string ProjectPresetFolder
            => System.IO.Path.Combine(Application.dataPath, "VACCPresets");

        private static string UserPresetFolder
            => System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "VACCPresets");

        private string ActivePresetFolder
            => presetStorageProject ? ProjectPresetFolder : UserPresetFolder;

        // ─────────────────────── プリセット ──────────────────────────────

        private void DrawPresetsSection()
        {
            presetsFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(presetsFoldout, Localization.Presets);
            if (!presetsFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(Localization.PresetTips, MessageType.Info);

            // 保存先切り替え
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(presetStorageProject, Localization.PresetStorageProject, EditorStyles.miniButtonLeft) && !presetStorageProject)
                presetStorageProject = true;
            if (GUILayout.Toggle(!presetStorageProject, Localization.PresetStorageUser, EditorStyles.miniButtonRight) && presetStorageProject)
                presetStorageProject = false;
            EditorGUILayout.EndHorizontal();

            // 保存
            EditorGUILayout.BeginHorizontal();
            presetSaveName = EditorGUILayout.TextField(Localization.PresetName, presetSaveName);
            if (GUILayout.Button(Localization.SavePreset, GUILayout.Width(48)))
                SavePreset(presetSaveName);
            EditorGUILayout.EndHorizontal();

            // インポート / エクスポート
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.ExportJson))
                ExportPresetJson();
            if (GUILayout.Button(Localization.ImportJson))
                ImportPresetJson();
            EditorGUILayout.EndHorizontal();

            // 一覧
            string folder = ActivePresetFolder;
            string[] files = System.IO.Directory.Exists(folder)
                ? System.IO.Directory.GetFiles(folder, "*.json")
                : System.Array.Empty<string>();

            if (files.Length == 0)
            {
                EditorGUILayout.HelpBox(Localization.NoPresets, MessageType.None);
            }
            else
            {
                presetScrollPos = EditorGUILayout.BeginScrollView(presetScrollPos, GUILayout.MaxHeight(120));
                foreach (string file in files)
                {
                    string pname = System.IO.Path.GetFileNameWithoutExtension(file);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(pname, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(Localization.LoadPreset, GUILayout.Width(40)))
                        LoadPreset(file);
                    if (GUILayout.Button("×", GUILayout.Width(22)))
                    {
                        if (EditorUtility.DisplayDialog(Localization.Confirm,
                            Localization.DeletePresetConfirm(pname), Localization.OK, Localization.Cancel))
                        {
                            System.IO.File.Delete(file);
                            AssetDatabase.Refresh();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        private void SavePreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Preset";
            // ファイル名サニタイズ
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");

            string folder = ActivePresetFolder;
            if (!System.IO.Directory.Exists(folder))
                System.IO.Directory.CreateDirectory(folder);

            var data = new VACCPresetData
            {
                name = name,
                zones = new List<ColorZone>(zones),
                edgeFeather = edgeFeather,
                advancedMode = advancedMode,
                antiAliasCleanup = antiAliasCleanup,
                holeFillPasses = holeFillPasses,
                holeFillMinNeighbors = holeFillMinNeighbors,
                relaxedSatMin = relaxedSatMin,
                relaxedSatRamp = relaxedSatRamp
            };
            string json = JsonUtility.ToJson(data, true);
            string path = System.IO.Path.Combine(folder, name + ".json");
            System.IO.File.WriteAllText(path, json);
            if (presetStorageProject) AssetDatabase.Refresh();
        }

        private void LoadPreset(string filePath)
        {
            if (!System.IO.File.Exists(filePath)) return;
            string json = System.IO.File.ReadAllText(filePath);
            var data = JsonUtility.FromJson<VACCPresetData>(json);
            if (data == null) return;

            zones = data.zones ?? new List<ColorZone>();
            edgeFeather = data.edgeFeather;
            advancedMode = data.advancedMode;
            antiAliasCleanup = data.antiAliasCleanup > 0 ? data.antiAliasCleanup : 3;
            holeFillPasses = data.holeFillPasses > 0 ? data.holeFillPasses : 3;
            holeFillMinNeighbors = data.holeFillMinNeighbors > 0 ? data.holeFillMinNeighbors : 4;
            relaxedSatMin = data.relaxedSatMin > 0f ? data.relaxedSatMin : 0.02f;
            relaxedSatRamp = data.relaxedSatRamp > 0f ? data.relaxedSatRamp : 0.08f;
            previewDirty = true;
        }

        private void ExportPresetJson()
        {
            string path = EditorUtility.SaveFilePanel(
                Localization.ExportJson, "", "VACC_preset", "json");
            if (string.IsNullOrEmpty(path)) return;
            SavePresetToPath(path);
        }

        private void ImportPresetJson()
        {
            string path = EditorUtility.OpenFilePanel(
                Localization.ImportJson, "", "json");
            if (string.IsNullOrEmpty(path)) return;
            LoadPreset(path);
        }

        private void SavePresetToPath(string path)
        {
            var data = new VACCPresetData
            {
                name = System.IO.Path.GetFileNameWithoutExtension(path),
                zones = new List<ColorZone>(zones),
                edgeFeather = edgeFeather,
                advancedMode = advancedMode,
                antiAliasCleanup = antiAliasCleanup,
                holeFillPasses = holeFillPasses,
                holeFillMinNeighbors = holeFillMinNeighbors,
                relaxedSatMin = relaxedSatMin,
                relaxedSatRamp = relaxedSatRamp
            };
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(data, true));
        }
    }
}
