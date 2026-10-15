// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
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

        // パス解決・GUID 指定削除・orphan 整理は SessionFileStore と共通（GuidFileCache）。
        private static readonly GuidFileCache Files =
            new GuidFileCache(CacheDirRelative, MaskFileExtension, "Mask", "mask");

        /// <summary>
        /// プロジェクトルート直下の <c>UserSettings/Iroca/MaskCache</c> 絶対パスを返す。
        /// </summary>
        public static string CacheDir => Files.CacheDir;

        private static string MaskFilePath(string texturePath) => Files.PathForAsset(texturePath);

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
                var state = JsonUtility.FromJson<MaskState>(File.ReadAllText(path));
                // JsonUtility は空文字や "null" で例外を投げずに null を返す。ここを見落とすと
                // 「ファイルはあるのに読めなかった」を「マスク無し」と誤認し、破壊的保存の抑止
                // (lastLoadFailed) が効かないまま空マスクで上書き＝手描きマスクの恒久喪失になる。
                // SessionFileStore.LoadSession は同じケースを unreadable=true にしており、
                // ここだけ非対称だった。
                if (state == null) { unreadable = true; return null; }
                return state;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Mask load failed: {ex.Message}");
                unreadable = true;
                return null;
            }
        }

        /// <summary>
        /// GUID 直接指定でマスクファイルを削除する。
        /// テクスチャ削除フックなど、AssetPath が既に解決できないタイミングから呼ぶ用途。
        /// </summary>
        public static void DeleteMaskByGuid(string guid) => Files.DeleteByGuid(guid);

        /// <summary>
        /// MaskCache を走査し、対応するテクスチャ（GUID）が見つからないファイルを整理する。
        /// AssetWatcher の delete フックを取りこぼした場合の二段構え。
        /// <para>
        /// GUID が未解決というだけでは即削除しない: ブランチ切替中・Library 再構築中など
        /// 一時的に GUID を引けないだけのことがあり、その瞬間に消すと手描きマスクが恒久的に失われる
        /// （ブランチを戻しても復元不能）。そこで未解決ファイルは <c>.orphan</c> へリネーム退避し、
        /// GUID が再び解決できたら元名へ復元、猶予期間（30 日。<see cref="GuidFileCache"/> が持つ）を
        /// 超えて未解決のままの退避ファイルだけを実削除する。CleanupOrphans は Load より先に走る
        /// （<c>IrocaWindow.OnEnable</c>）ので、退避→復元は読み込み前に完了する。
        /// </para>
        /// </summary>
        public static void CleanupOrphans() => Files.CleanupOrphans();

        private static bool IsEmpty(MaskState state)
        {
            if (state == null) return true;
            bool hasCommon = !string.IsNullOrEmpty(state.commonMaskBase64);
            return !hasCommon && !HasAnyEntry(state.zones) && !HasAnyEntry(state.zoneIncludes);
        }

        private static bool HasAnyEntry(System.Collections.Generic.List<MaskZoneEntry> entries)
        {
            if (entries == null) return false;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.maskBase64)) continue;
                return true;
            }
            return false;
        }
    }
}
