using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
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

        [System.NonSerialized] private Vector2 _presetScrollPos;
        [System.NonSerialized] private VACCWindow _host;

        private string ActivePresetFolder
            => presetStorageProject ? PresetStore.ProjectPresetFolder : PresetStore.UserPresetFolder;

        public void Initialize(VACCWindow host)
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

            EditorGUILayout.HelpBox(Localization.PresetTips, MessageType.Info);

            // 保存先切り替え
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(presetStorageProject, new GUIContent(Localization.PresetStorageProject, Localization.PresetStorageProjectTooltip), EditorStyles.miniButtonLeft) && !presetStorageProject)
                presetStorageProject = true;
            if (GUILayout.Toggle(!presetStorageProject, new GUIContent(Localization.PresetStorageUser, Localization.PresetStorageUserTooltip), EditorStyles.miniButtonRight) && presetStorageProject)
                presetStorageProject = false;
            EditorGUILayout.EndHorizontal();

            // 保存
            EditorGUILayout.BeginHorizontal();
            presetSaveName = EditorGUILayout.TextField(
                new GUIContent(Localization.PresetName, Localization.PresetNameTooltip),
                presetSaveName);
            if (GUILayout.Button(new GUIContent(Localization.SavePreset, Localization.SavePresetTooltip), GUILayout.Width(VACCConsts.Layout.SmallButtonWidth)))
                SavePreset(presetSaveName);
            EditorGUILayout.EndHorizontal();

            presetIncludeMasks = EditorGUILayout.ToggleLeft(
                new GUIContent(Localization.PresetIncludeMasks, Localization.PresetIncludeMasksTooltip),
                presetIncludeMasks);
            presetApplyMasks = EditorGUILayout.ToggleLeft(
                new GUIContent(Localization.PresetApplyMasks, Localization.PresetApplyMasksTooltip),
                presetApplyMasks);

            // インポート / エクスポート
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(Localization.ExportJson, Localization.ExportJsonTooltip)))
                ExportPresetJson();
            if (GUILayout.Button(new GUIContent(Localization.ImportJson, Localization.ImportJsonTooltip)))
                ImportPresetJson();
            EditorGUILayout.EndHorizontal();

            // 一覧
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
                    if (GUILayout.Button(new GUIContent(Localization.LoadPreset, Localization.LoadPresetTooltip), GUILayout.Width(40)))
                        LoadPreset(file);
                    if (GUILayout.Button(new GUIContent("×", Localization.DeletePresetTooltip), GUILayout.Width(VACCConsts.Layout.RemoveButtonWidth)))
                    {
                        if (EditorUtility.DisplayDialog(Localization.Confirm,
                            Localization.DeletePresetConfirm(pname), Localization.OK, Localization.Cancel))
                        {
                            PresetStore.Delete(file);
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
            var data = BuildPresetData(name);
            if (presetStorageProject)
                PresetStore.SaveToProject(name, data);
            else
                PresetStore.SaveToUser(name, data);
        }

        // 現在の設定を VACCPresetData に詰めて返す。
        private VACCPresetData BuildPresetData(string presetName)
        {
            _host.EnsureAllZoneIds();
            var session = _host.Session;

            // ColorZone は public フィールドのみなのでシャローコピーでよいが、
            // id は必ず引き継ぐため変わらない
            var zonesCopy = new List<ColorZone>(session.zones);

            var data = new VACCPresetData
            {
                name = presetName,
                zones = zonesCopy,
                edgeFeather = session.edgeFeather,
                advancedMode = session.advancedMode,
                antiAliasCleanup = session.antiAliasCleanup,
                holeFillPasses = session.holeFillPasses,
                holeFillMinNeighbors = session.holeFillMinNeighbors,
                relaxedSatMin = session.relaxedSatMin,
                relaxedSatRamp = session.relaxedSatRamp,
                useDecontamination = session.useDecontamination,
                decontaminationRadius = session.decontaminationRadius,
            };

            if (presetIncludeMasks)
            {
                _host.WriteMaskToPreset(data);
            }

            return data;
        }

        private void LoadPreset(string filePath)
        {
            var data = PresetStore.Load(filePath);
            if (data == null) return;

            // Unity の JsonUtility は JSON に含まれないフィールドを「既定値で上書き」ではなく
            // 「クラスのフィールド初期化子の値を保持」するため、ここで `> 0 ? : default` のような
            // defaulting を行うとユーザーが明示的に 0 を保存したケース（UI レンジに 0 を含む
            // antiAliasCleanup や holeFillPasses）を不当に書き換えてしまう。
            // 旧バージョンとの後方互換は VACCPresetData の初期化子側に寄せる。
            var session = _host.Session;
            session.zones = data.zones ?? new List<ColorZone>();
            _host.EnsureAllZoneIds();
            session.edgeFeather          = data.edgeFeather;
            session.advancedMode         = data.advancedMode;
            session.antiAliasCleanup     = data.antiAliasCleanup;
            session.holeFillPasses       = data.holeFillPasses;
            session.holeFillMinNeighbors = data.holeFillMinNeighbors;
            session.relaxedSatMin        = data.relaxedSatMin;
            session.relaxedSatRamp       = data.relaxedSatRamp;
            session.useDecontamination   = data.useDecontamination;
            session.decontaminationRadius = Mathf.Clamp(data.decontaminationRadius, 1, 12);

            if (presetApplyMasks && data.maskWidth > 0 && data.maskHeight > 0)
            {
                _host.ApplyMaskFromPreset(data);
            }

            _host.ResetActiveMaskTarget();
            _host.MarkMaskDirty();
            _host.MarkPreviewDirty();
        }

        private void ExportPresetJson()
        {
            string path = EditorUtility.SaveFilePanel(
                Localization.ExportJson, "", "VACC_preset", "json");
            if (string.IsNullOrEmpty(path)) return;
            var data = BuildPresetData(Path.GetFileNameWithoutExtension(path));
            PresetStore.SaveToPath(path, data);
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
