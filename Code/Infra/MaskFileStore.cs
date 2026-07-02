// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスクデータ（<see cref="MaskState"/>）の永続化を担う。
    /// 保存先は <c>&lt;Project&gt;/UserSettings/Iroca/MaskCache/&lt;テクスチャGUID&gt;.iroca-mask.json</c> で、
    /// git 非追跡フォルダ（個人作業データ）に置く。GUID ベースのため
    /// テクスチャの rename / move には自動追従する。
    /// </summary>
    internal static class MaskFileStore
    {
        private const string CacheDirRelative = "UserSettings/Iroca/MaskCache";
        private const string MaskFileExtension = ".iroca-mask.json";

        /// <summary>
        /// プロジェクトルート直下の <c>UserSettings/Iroca/MaskCache</c> 絶対パスを返す。
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
        /// 保存先パスが解決できない（テクスチャ未設定/Assets 外）場合や中身が空の場合は
        /// 「保存すべきものが無い＝成功」として true。実際の書き込みに失敗したときだけ false。
        /// <para>
        /// <paramref name="lastLoadFailed"/> が true（このセッションで既存ファイルを
        /// 読み込めていない）のときは破壊的動作を抑止する: 空保存でも既存ファイルを
        /// 削除せず、上書き時は先に <c>.bak</c> へ退避する。ウイルススキャナ等による
        /// 一時的な読込失敗が「空保存 → 無傷ファイルの恒久削除」に化けるのを防ぐ。
        /// </para>
        /// </summary>
        public static bool SaveMask(string texturePath, MaskState state, bool lastLoadFailed = false)
        {
            string path = MaskFilePath(texturePath);
            if (string.IsNullOrEmpty(path)) return true;

            if (state == null || IsEmpty(state))
            {
                if (File.Exists(path))
                {
                    if (lastLoadFailed)
                    {
                        Debug.LogWarning(
                            "[Iroca] マスクファイルの読み込みに失敗したセッションのため、空マスクによる削除をスキップしました: " + path);
                        return true;
                    }
                    try { File.Delete(path); }
                    catch (Exception ex) { Debug.LogWarning($"[Iroca] Mask delete failed: {ex.Message}"); return false; }
                }
                return true;
            }

            try
            {
                Directory.CreateDirectory(CacheDir);
                if (lastLoadFailed && File.Exists(path))
                {
                    // 読めなかった既存データを潰す前に退避する（失敗しても保存は続行）。
                    try
                    {
                        File.Copy(path, path + ".bak", overwrite: true);
                        Debug.LogWarning($"[Iroca] 読み込めなかった既存マスクを退避しました: {path}.bak");
                    }
                    catch (Exception ex) { Debug.LogWarning($"[Iroca] Mask backup failed: {ex.Message}"); }
                }
                state.schemaVersion = MaskState.CurrentSchemaVersion;
                AtomicFile.WriteAllText(path, JsonUtility.ToJson(state));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Mask save failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 指定テクスチャの <see cref="MaskState"/> を読み込む。
        /// ファイルが無い・破損していれば <c>null</c> を返す。
        /// <paramref name="unreadable"/> は「ファイルは存在するのに読めなかった」ときだけ true
        /// （呼び出し側はこのセッションでの破壊的保存を抑止すること）。
        /// </summary>
        public static MaskState LoadMask(string texturePath, out bool unreadable)
        {
            unreadable = false;
            string path = MaskFilePath(texturePath);
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<MaskState>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Mask load failed: {ex.Message}");
                unreadable = true;
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
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Mask delete failed: {ex.Message}"); }
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
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Mask delete failed: {ex.Message}"); }
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
                Debug.LogWarning($"[Iroca] Mask cache scan failed: {ex.Message}");
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
                catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan mask delete failed: {ex.Message}"); }
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
