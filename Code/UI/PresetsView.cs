// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        [System.NonSerialized] private Vector2 _presetScrollPos;
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
            if (GUILayout.Button(new GUIContent(Localization.SavePreset, Localization.SavePresetTooltip), GUILayout.Width(IrocaConsts.Layout.SmallButtonWidth)))
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

                    // インポート / エクスポート
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(new GUIContent(Localization.ExportJson, Localization.ExportJsonTooltip)))
                        ExportPresetJson();
                    if (GUILayout.Button(new GUIContent(Localization.ImportJson, Localization.ImportJsonTooltip)))
                        ImportPresetJson();
                    EditorGUILayout.EndHorizontal();
                }
            }

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
                    if (GUILayout.Button(new GUIContent("×", Localization.DeletePresetTooltip), GUILayout.Width(IrocaConsts.Layout.RemoveButtonWidth)))
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
            // 同名プリセットが既にある場合は上書き前に確認する。
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

        // 現在の設定を IrocaPresetData に詰めて返す。
        private IrocaPresetData BuildPresetData(string presetName)
        {
            _host.EnsureAllZoneIds();
            var session = _host.Session;

            // ColorZone は public フィールドのみなのでシャローコピーでよいが、
            // id は必ず引き継ぐため変わらない
            var zonesCopy = new List<ColorZone>(session.zones);

            var data = new IrocaPresetData
            {
                name = presetName,
                zones = zonesCopy,
                edgeFeather = session.edgeFeather,
                // 編集モード(かんたん/通常/上級)は UI 表示の好みであり色設定ではないため、
                // プリセットには保存しない（読込で表示モードが勝手に変わらないように）。
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
            if (data == null)
            {
                Notify($"{Localization.Error}: {Localization.PresetLoadFailed}");
                return;
            }

            // 読込前の状態を Unity Undo に登録する（zones + 処理パラメータ + マスク状態を
            // 1 ステップで復元可能にする）。マスクは bool[] バッファを先に _session.maskState へ
            // 同期してから登録することで、Undo 後に SyncBuffersFromState で正しく再展開できる。
            _host.SyncMaskBuffersToState();
            Undo.RegisterCompleteObjectUndo(_host, "Load Preset");

            // Unity の JsonUtility は JSON に含まれないフィールドを「既定値で上書き」ではなく
            // 「クラスのフィールド初期化子の値を保持」するため、ここで `> 0 ? : default` のような
            // defaulting を行うとユーザーが明示的に 0 を保存したケース（UI レンジに 0 を含む
            // antiAliasCleanup や holeFillPasses）を不当に書き換えてしまう。
            // 旧バージョンとの後方互換は IrocaPresetData の初期化子側に寄せる。
            var session = _host.Session;
            session.zones = data.zones ?? new List<ColorZone>();
            MigrateLegacyLayerPriority(session.zones);
            _host.EnsureAllZoneIds();
            session.edgeFeather          = data.edgeFeather;
            // 編集モードはプリセットで上書きしない（現在の表示モードを維持）。
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
            Notify(Localization.PresetLoaded);
        }

        /// <summary>
        /// 旧バージョン互換の一度きり移行。かつて優先度は layerIndex(数値・大きいほど
        /// 重なりで上に来る)で表していたが、現在は「リストの並び順＝優先度(先頭が最優先)」
        /// に変更した。layerIndex に差異を持つ旧プリセットだけを降順で安定ソートし、
        /// 旧来の勝敗(大きい layerIndex が勝つ → 先頭が勝つ)を再現する。
        /// 全ゾーンが同値(既定 0 を含む)なら並び順は既に意図どおりなので何もしない。
        /// </summary>
        private static void MigrateLegacyLayerPriority(List<ColorZone> zones)
        {
            if (zones == null || zones.Count < 2) return;
            int first = zones[0].layerIndex;
            bool allSame = true;
            for (int i = 1; i < zones.Count; i++)
            {
                if (zones[i].layerIndex != first) { allSame = false; break; }
            }
            if (allSame) return;
            var migrated = zones.OrderByDescending(z => z.layerIndex).ToList();
            zones.Clear();
            zones.AddRange(migrated);
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
