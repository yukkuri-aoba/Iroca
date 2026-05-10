using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// マスクデータ（<see cref="MaskState"/>）の永続化を担う。
    /// 保存先は <c>&lt;Project&gt;/UserSettings/VACC/MaskCache/&lt;テクスチャGUID&gt;.vacc-mask.json</c> で、
    /// git 非追跡フォルダ（個人作業データ）に置く。GUID ベースのため
    /// テクスチャの rename / move には自動追従する。
    /// </summary>
    internal static class MaskFileStore
    {
        private const string CacheDirRelative = "UserSettings/VACC/MaskCache";
        private const string MaskFileExtension = ".vacc-mask.json";

        /// <summary>
        /// プロジェクトルート直下の <c>UserSettings/VACC/MaskCache</c> 絶対パスを返す。
        /// </summary>
        public static string CacheDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", CacheDirRelative));

        private static string MaskFilePath(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return null;
            string guid = AssetDatabase.AssetPathToGUID(texturePath);
            if (string.IsNullOrEmpty(guid)) return null;
            return Path.Combine(CacheDir, guid + MaskFileExtension);
        }

        /// <summary>
        /// 指定テクスチャの <see cref="MaskState"/> を保存する。
        /// 中身が空（共通もゾーンも未設定）の場合は既存ファイルを削除して終わる。
        /// </summary>
        public static void SaveMask(string texturePath, MaskState state)
        {
            string path = MaskFilePath(texturePath);
            if (string.IsNullOrEmpty(path)) return;

            if (state == null || IsEmpty(state))
            {
                if (File.Exists(path))
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { Debug.LogWarning($"[VACC] Mask delete failed: {ex.Message}"); }
                }
                return;
            }

            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(path, JsonUtility.ToJson(state));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VACC] Mask save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 指定テクスチャの <see cref="MaskState"/> を読み込む。
        /// ファイルが無い・破損していれば <c>null</c> を返す。
        /// </summary>
        public static MaskState LoadMask(string texturePath)
        {
            string path = MaskFilePath(texturePath);
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<MaskState>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VACC] Mask load failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 指定テクスチャに対応するマスクファイルを削除する。
        /// </summary>
        public static void DeleteMask(string texturePath)
        {
            string path = MaskFilePath(texturePath);
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) return;
            try { File.Delete(path); }
            catch (Exception ex) { Debug.LogWarning($"[VACC] Mask delete failed: {ex.Message}"); }
        }

        /// <summary>
        /// GUID 直接指定でマスクファイルを削除する。
        /// テクスチャ削除フックなど、AssetPath が既に解決できないタイミングから呼ぶ用途。
        /// </summary>
        public static void DeleteMaskByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            string path = Path.Combine(CacheDir, guid + MaskFileExtension);
            if (!File.Exists(path)) return;
            try { File.Delete(path); }
            catch (Exception ex) { Debug.LogWarning($"[VACC] Mask delete failed: {ex.Message}"); }
        }

        /// <summary>
        /// MaskCache を走査し、対応するテクスチャ（GUID）が見つからないファイルを削除する。
        /// AssetWatcher の delete フックを取りこぼした場合の二段構え。
        /// </summary>
        public static void CleanupOrphans()
        {
            if (!Directory.Exists(CacheDir)) return;
            string[] files;
            try { files = Directory.GetFiles(CacheDir, "*" + MaskFileExtension); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VACC] Mask cache scan failed: {ex.Message}");
                return;
            }

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(MaskFileExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                string guid = fileName.Substring(0, fileName.Length - MaskFileExtension.Length);
                if (string.IsNullOrEmpty(guid)) continue;
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath)) continue;

                try { File.Delete(file); }
                catch (Exception ex) { Debug.LogWarning($"[VACC] Orphan mask delete failed: {ex.Message}"); }
            }
        }

        private static bool IsEmpty(MaskState state)
        {
            if (state == null) return true;
            bool hasCommon = !string.IsNullOrEmpty(state.commonMaskBase64);
            bool hasZone = false;
            if (state.zones != null)
            {
                foreach (var entry in state.zones)
                {
                    if (entry == null) continue;
                    if (string.IsNullOrEmpty(entry.maskBase64)) continue;
                    hasZone = true;
                    break;
                }
            }
            return !hasCommon && !hasZone;
        }
    }
}
