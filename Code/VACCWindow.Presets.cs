using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // プリセット
        [SerializeField] private bool presetsFoldout;
        [SerializeField] private string presetSaveName = "Preset";
        [SerializeField] private bool presetStorageProject = true;
        [SerializeField] private bool presetIncludeMasks = true;
        [SerializeField] private bool presetApplyMasks = true;
        private Vector2 presetScrollPos;

        // Assets/VACC/Editor 配置を前提とした固定パス。
        // 自己探索を廃止して挙動の予測可能性を上げる。
        private const string ProjectPresetFolderRelative = "Assets/VACC/Presets";

        private static string ProjectPresetFolder
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(
                Application.dataPath, "..", ProjectPresetFolderRelative));

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
            if (GUILayout.Button(new GUIContent(Localization.SavePreset, Localization.SavePresetTooltip), GUILayout.Width(48)))
                SavePreset(presetSaveName);
            EditorGUILayout.EndHorizontal();

            // マスク保存・読込トグル
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
                // 親ScrollViewがあるため、ネストしたScrollViewを削除し全件表示する（UI破壊を防ぐ）
                foreach (string file in files)
                {
                    string pname = System.IO.Path.GetFileNameWithoutExtension(file);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(pname, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(new GUIContent(Localization.LoadPreset, Localization.LoadPresetTooltip), GUILayout.Width(40)))
                        LoadPreset(file);
                    if (GUILayout.Button(new GUIContent("×", Localization.DeletePresetTooltip), GUILayout.Width(22)))
                    {
                        if (EditorUtility.DisplayDialog(Localization.Confirm,
                            Localization.DeletePresetConfirm(pname), Localization.OK, Localization.Cancel))
                        {
                            string rel = ToAssetsRelative(file);
                            if (rel != null)
                            {
                                AssetDatabase.DeleteAsset(rel);
                            }
                            else
                            {
                                System.IO.File.Delete(file);
                            }
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
            if (string.IsNullOrWhiteSpace(name)) name = "Preset";
            // ファイル名サニタイズ
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");

            string folder = ActivePresetFolder;
            if (!System.IO.Directory.Exists(folder))
                System.IO.Directory.CreateDirectory(folder);

            var data = BuildPresetData(name);
            string json = JsonUtility.ToJson(data, true);
            string path = System.IO.Path.Combine(folder, name + ".json");
            System.IO.File.WriteAllText(path, json);
            if (presetStorageProject)
            {
                string rel = ToAssetsRelative(path);
                if (rel != null) AssetDatabase.ImportAsset(rel);
            }
        }

        // 現在の設定を VACCPresetData に詰めて返す。
        // presetIncludeMasks=true ならマスクも同梱する。
        private VACCPresetData BuildPresetData(string presetName)
        {
            EnsureAllZoneIds();

            // ColorZone は public フィールドのみなのでシャローコピーでよいが、
            // id は必ず引き継ぐため変わらない
            var zonesCopy = new List<ColorZone>(zones);

            var data = new VACCPresetData
            {
                name = presetName,
                zones = zonesCopy,
                edgeFeather = edgeFeather,
                advancedMode = advancedMode,
                antiAliasCleanup = antiAliasCleanup,
                holeFillPasses = holeFillPasses,
                holeFillMinNeighbors = holeFillMinNeighbors,
                relaxedSatMin = relaxedSatMin,
                relaxedSatRamp = relaxedSatRamp,
                useDecontamination = useDecontamination,
                decontaminationRadius = decontaminationRadius,
            };

            if (presetIncludeMasks && maskWidth > 0 && maskHeight > 0)
            {
                bool includedAnything = false;

                if (exclusionMask != null && HasAnyTrue(exclusionMask))
                {
                    data.commonMaskBase64 = EncodeMask(exclusionMask, maskWidth, maskHeight);
                    includedAnything = true;
                }

                foreach (var kv in zoneMasks)
                {
                    if (kv.Value == null || !HasAnyTrue(kv.Value)) continue;
                    data.zoneMasks.Add(new ZoneMaskEntry
                    {
                        zoneId = kv.Key,
                        maskBase64 = EncodeMask(kv.Value, maskWidth, maskHeight),
                    });
                    includedAnything = true;
                }

                if (includedAnything)
                {
                    data.maskWidth = maskWidth;
                    data.maskHeight = maskHeight;
                }
            }

            return data;
        }

        private static bool HasAnyTrue(bool[] arr)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++) if (arr[i]) return true;
            return false;
        }

        // プリセットに含まれるマスクデータを現在の状態に適用する。
        private void ApplyPresetMasks(VACCPresetData data)
        {
            if (data == null || data.maskWidth <= 0 || data.maskHeight <= 0) return;

            maskWidth = data.maskWidth;
            maskHeight = data.maskHeight;
            exclusionMask = null;
            zoneMasks.Clear();

            if (!string.IsNullOrEmpty(data.commonMaskBase64))
            {
                var m = DecodeMask(data.commonMaskBase64, out int w, out int h);
                if (m != null && w == maskWidth && h == maskHeight)
                    exclusionMask = m;
            }

            if (data.zoneMasks != null)
            {
                foreach (var e in data.zoneMasks)
                {
                    if (e == null || string.IsNullOrEmpty(e.zoneId)) continue;
                    var m = DecodeMask(e.maskBase64, out int w, out int h);
                    if (m == null || w != maskWidth || h != maskHeight) continue;
                    zoneMasks[e.zoneId] = m;
                }
            }

            _undoMaskHistory.Clear();
            SaveMaskToSession();
        }

        private void LoadPreset(string filePath)
        {
            if (!System.IO.File.Exists(filePath)) return;
            string json = System.IO.File.ReadAllText(filePath);
            var data = JsonUtility.FromJson<VACCPresetData>(json);
            if (data == null) return;

            // Unity の JsonUtility は JSON に含まれないフィールドを「既定値で上書き」ではなく
            // 「クラスのフィールド初期化子の値を保持」するため、ここで `> 0 ? : default` のような
            // defaulting を行うとユーザーが明示的に 0 を保存したケース（UI レンジに 0 を含む
            // antiAliasCleanup や holeFillPasses）を不当に書き換えてしまう。
            // 旧バージョンとの後方互換は VACCPresetData の初期化子側に寄せる。
            zones = data.zones ?? new List<ColorZone>();
            EnsureAllZoneIds();
            edgeFeather          = data.edgeFeather;
            advancedMode         = data.advancedMode;
            antiAliasCleanup     = data.antiAliasCleanup;
            holeFillPasses       = data.holeFillPasses;
            holeFillMinNeighbors = data.holeFillMinNeighbors;
            relaxedSatMin        = data.relaxedSatMin;
            relaxedSatRamp       = data.relaxedSatRamp;
            useDecontamination   = data.useDecontamination;
            decontaminationRadius = Mathf.Clamp(data.decontaminationRadius, 1, 12);

            if (presetApplyMasks && data.maskWidth > 0 && data.maskHeight > 0)
            {
                ApplyPresetMasks(data);
            }

            activeMaskTarget = -1;
            maskDirty = true;
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
            var data = BuildPresetData(System.IO.Path.GetFileNameWithoutExtension(path));
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(data, true));
        }
    }
}
