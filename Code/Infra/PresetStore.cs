using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// プリセット JSON の保存・読込・削除と、保存先フォルダの解決を担う。
    /// UI / プレビュー状態 / マスク描画には依存しない。
    /// 既存 <see cref="VACCPresetData"/> のスキーマはそのまま使う。
    /// </summary>
    internal static class PresetStore
    {
        // Assets/VACC/Editor 配置を前提とした固定パス。
        // 自己探索を廃止して挙動の予測可能性を上げる。
        private const string ProjectPresetFolderRelative = "Assets/VACC/Presets";

        public static string ProjectPresetFolder
            => Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", ProjectPresetFolderRelative));

        public static string UserPresetFolder
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VACCPresets");

        /// <summary>
        /// プロジェクト保存フォルダ内に <paramref name="name"/>.json として書き出し、
        /// Assets 配下なら AssetDatabase に取り込む。
        /// </summary>
        public static void SaveToProject(string name, VACCPresetData data)
        {
            if (data == null) return;
            string sanitized = SanitizeFileName(name);
            EnsureDirectory(ProjectPresetFolder);
            string path = Path.Combine(ProjectPresetFolder, sanitized + ".json");
            WriteJson(path, data);

            string rel = ToAssetsRelativeOrNull(path);
            if (rel != null) AssetDatabase.ImportAsset(rel);
        }

        /// <summary>
        /// ユーザー保存フォルダ（%APPDATA%/VACCPresets）に書き出す。
        /// Assets 外なので AssetDatabase は触らない。
        /// </summary>
        public static void SaveToUser(string name, VACCPresetData data)
        {
            if (data == null) return;
            string sanitized = SanitizeFileName(name);
            EnsureDirectory(UserPresetFolder);
            string path = Path.Combine(UserPresetFolder, sanitized + ".json");
            WriteJson(path, data);
        }

        /// <summary>
        /// 任意の絶対パスへ書き出す（エクスポート用）。
        /// </summary>
        public static void SaveToPath(string path, VACCPresetData data)
        {
            if (data == null || string.IsNullOrEmpty(path)) return;
            WriteJson(path, data);
        }

        /// <summary>
        /// 指定 JSON ファイルから <see cref="VACCPresetData"/> を読み込む。
        /// 失敗時は <c>null</c>。
        /// </summary>
        public static VACCPresetData Load(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                string json = File.ReadAllText(filePath);
                return JsonUtility.FromJson<VACCPresetData>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VACC] Preset load failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// プリセットファイルを削除する。Assets 配下なら AssetDatabase 経由で消す。
        /// </summary>
        public static void Delete(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            string rel = ToAssetsRelativeOrNull(filePath);
            if (rel != null)
            {
                AssetDatabase.DeleteAsset(rel);
            }
            else if (File.Exists(filePath))
            {
                try { File.Delete(filePath); }
                catch (Exception ex) { Debug.LogWarning($"[VACC] Preset delete failed: {ex.Message}"); }
            }
        }

        public static string[] ListJson(string folder)
        {
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.json")
                : Array.Empty<string>();
        }

        private static void EnsureDirectory(string folder)
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
        }

        private static void WriteJson(string path, VACCPresetData data)
        {
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VACC] Preset save failed: {ex.Message}");
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Preset";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");
            return name;
        }

        private static string ToAssetsRelativeOrNull(string path)
            => VACCWindow.ToAssetsRelative(path);
    }
}
