// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// プリセット JSON の保存・読込・削除と、保存先フォルダの解決を担う。
    /// UI / プレビュー状態 / マスク描画には依存しない。
    /// 既存 <see cref="IrocaPresetData"/> のスキーマはそのまま使う。
    /// </summary>
    internal static class PresetStore
    {
        // Assets/Iroca/Editor 配置を前提とした固定パス。
        // 自己探索を廃止して挙動の予測可能性を上げる。
        private const string ProjectPresetFolderRelative = "Assets/Iroca/Presets";

        public static string ProjectPresetFolder
            => Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", ProjectPresetFolderRelative));

        public static string UserPresetFolder
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IrocaPresets");

        /// <summary>
        /// プロジェクト保存フォルダ内に <paramref name="name"/>.json として書き出し、
        /// Assets 配下なら AssetDatabase に取り込む。成功したら true。
        /// </summary>
        public static bool SaveToProject(string name, IrocaPresetData data)
        {
            if (data == null) return false;
            string sanitized = SanitizeFileName(name);
            EnsureDirectory(ProjectPresetFolder);
            string path = Path.Combine(ProjectPresetFolder, sanitized + ".json");
            if (!WriteJson(path, data)) return false;

            string rel = PathUtils.ToAssetsRelativeOrNull(path);
            if (rel != null) AssetDatabase.ImportAsset(rel);
            return true;
        }

        /// <summary>
        /// ユーザー保存フォルダ（%APPDATA%/IrocaPresets）に書き出す。
        /// Assets 外なので AssetDatabase は触らない。成功したら true。
        /// </summary>
        public static bool SaveToUser(string name, IrocaPresetData data)
        {
            if (data == null) return false;
            string sanitized = SanitizeFileName(name);
            EnsureDirectory(UserPresetFolder);
            string path = Path.Combine(UserPresetFolder, sanitized + ".json");
            return WriteJson(path, data);
        }

        /// <summary>
        /// 任意の絶対パスへ書き出す（エクスポート用）。成功したら true。
        /// </summary>
        public static bool SaveToPath(string path, IrocaPresetData data)
        {
            if (data == null || string.IsNullOrEmpty(path)) return false;
            return WriteJson(path, data);
        }

        /// <summary>
        /// 指定 JSON ファイルから <see cref="IrocaPresetData"/> を読み込む。
        /// 失敗時は <c>null</c>。
        /// </summary>
        public static IrocaPresetData Load(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                string json = File.ReadAllText(filePath);
                return JsonUtility.FromJson<IrocaPresetData>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Preset load failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// プリセットファイルを削除する。Assets 配下なら AssetDatabase 経由で消す。
        /// 成功したら true。
        /// </summary>
        public static bool Delete(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string rel = PathUtils.ToAssetsRelativeOrNull(filePath);
            if (rel != null)
            {
                return AssetDatabase.DeleteAsset(rel);
            }
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); return true; }
                catch (Exception ex) { Debug.LogWarning($"[Iroca] Preset delete failed: {ex.Message}"); return false; }
            }
            return false;
        }

        /// <summary>
        /// 指定フォルダ・プリセット名に対応する JSON ファイルの絶対パスを返す。
        /// 保存時と同じファイル名サニタイズを通すため、上書き判定に使える。
        /// </summary>
        public static string PresetFilePath(string folder, string name)
            => Path.Combine(folder, SanitizeFileName(name) + ".json");

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

        private static bool WriteJson(string path, IrocaPresetData data)
        {
            try
            {
                AtomicFile.WriteAllText(path, JsonUtility.ToJson(data, true));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Preset save failed: {ex.Message}");
                return false;
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Preset";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");
            return name;
        }
    }
}
