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
    /// 編集セッション（<see cref="IrocaSessionState"/> のうちマスク以外＝ゾーン定義・色・
    /// 処理パラメータ）の永続化を担う。保存先は
    /// <c>&lt;Project&gt;/UserSettings/Iroca/SessionCache/&lt;テクスチャGUID&gt;.iroca-session.json</c> で、
    /// git 非追跡フォルダ（個人作業データ）に置く。GUID ベースのため
    /// テクスチャの rename / move には自動追従する。<see cref="MaskFileStore"/> と対になり、
    /// 「ウィンドウを閉じても、開き直したテクスチャの編集内容がまるごと復元される」を実現する。
    /// <para>
    /// マスク（<see cref="MaskState"/>）は <see cref="MaskFileStore"/> が別ファイルで管理する唯一の正。
    /// ここでは巨大な base64 を二重に持たないよう、書き出し時に maskState を空へ差し替える。
    /// </para>
    /// </summary>
    internal static class SessionFileStore
    {
        private const string CacheDirRelative = "UserSettings/Iroca/SessionCache";
        private const string SessionFileExtension = ".iroca-session.json";

        /// <summary>
        /// プロジェクトルート直下の <c>UserSettings/Iroca/SessionCache</c> 絶対パスを返す。
        /// </summary>
        public static string CacheDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", CacheDirRelative));

        private static string SessionFilePath(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return null;
            string guid = AssetDatabase.AssetPathToGUID(texturePath);
            if (string.IsNullOrEmpty(guid)) return null;
            return Path.Combine(CacheDir, guid + SessionFileExtension);
        }

        /// <summary>
        /// 指定テクスチャのセッションを保存する。
        /// ゾーンが無い（＝再着色を生まない実質空）セッションの場合は既存ファイルを削除して終わる。
        /// 保存先パスが解決できない（テクスチャ未設定/Assets 外）場合や空セッションは
        /// 「保存すべきものが無い＝成功」として true。実際の書き込みに失敗したときだけ false。
        /// <para>
        /// <paramref name="lastLoadFailed"/> が true（このセッションで既存ファイルを読み込めていない）
        /// のときは破壊的動作を抑止する: 空保存でも既存ファイルを削除せず、上書き時は先に
        /// <c>.bak</c> へ退避する。ウイルススキャナ等による一時的な読込失敗が
        /// 「空保存 → 無傷ファイルの恒久削除」に化けるのを防ぐ（<see cref="MaskFileStore"/> と同じ防御）。
        /// </para>
        /// </summary>
        public static bool SaveSession(string texturePath, IrocaSessionState state, bool lastLoadFailed = false)
        {
            string path = SessionFilePath(texturePath);
            if (string.IsNullOrEmpty(path)) return true;

            if (IsEmpty(state))
            {
                if (File.Exists(path))
                {
                    if (lastLoadFailed)
                    {
                        Debug.LogWarning(
                            "[Iroca] セッションファイルの読み込みに失敗したセッションのため、空保存による削除をスキップしました: " + path);
                        return true;
                    }
                    try { File.Delete(path); }
                    catch (Exception ex) { Debug.LogWarning($"[Iroca] Session delete failed: {ex.Message}"); return false; }
                }
                return true;
            }

            // マスクは MaskFileStore が唯一の正。二重保存を避けるため、書き出しの間だけ
            // maskState を空へ差し替える（Editor は単一スレッドのため一時的な差し替えで安全）。
            var savedMask = state.maskState;
            state.maskState = new MaskState();
            try
            {
                Directory.CreateDirectory(CacheDir);
                if (lastLoadFailed && File.Exists(path))
                {
                    // 読めなかった既存データを潰す前に退避する（失敗しても保存は続行）。
                    try
                    {
                        File.Copy(path, path + ".bak", overwrite: true);
                        Debug.LogWarning($"[Iroca] 読み込めなかった既存セッションを退避しました: {path}.bak");
                    }
                    catch (Exception ex) { Debug.LogWarning($"[Iroca] Session backup failed: {ex.Message}"); }
                }
                AtomicFile.WriteAllText(path, JsonUtility.ToJson(state));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Session save failed: {ex.Message}");
                return false;
            }
            finally
            {
                state.maskState = savedMask;
            }
        }

        /// <summary>
        /// 指定テクスチャのセッションを読み込む。
        /// ファイルが無い・破損していれば <c>null</c> を返す。
        /// <paramref name="unreadable"/> は「ファイルは存在するのに読めなかった」ときだけ true
        /// （呼び出し側はこのセッションでの破壊的保存を抑止すること）。
        /// 返すオブジェクトの maskState は常に空（マスクは別ストアが復元する）。
        /// </summary>
        public static IrocaSessionState LoadSession(string texturePath, out bool unreadable)
        {
            unreadable = false;
            string path = SessionFilePath(texturePath);
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path)) return null;
            try
            {
                var state = JsonUtility.FromJson<IrocaSessionState>(File.ReadAllText(path));
                if (state == null) { unreadable = true; return null; }
                // 保存時に空へしているが、防御的に初期化して null 参照を避ける。
                if (state.zones == null) state.zones = new List<ColorZone>();
                if (state.maskState == null) state.maskState = new MaskState();
                return state;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Session load failed: {ex.Message}");
                unreadable = true;
                return null;
            }
        }

        /// <summary>
        /// GUID 直接指定でセッションファイルを削除する。
        /// テクスチャ削除フックなど、AssetPath が既に解決できないタイミングから呼ぶ用途。
        /// </summary>
        public static void DeleteSessionByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            string path = Path.Combine(CacheDir, guid + SessionFileExtension);
            if (!File.Exists(path)) return;
            try { File.Delete(path); }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Session delete failed: {ex.Message}"); }
        }

        /// <summary>
        /// SessionCache を走査し、対応するテクスチャ（GUID）が見つからないファイルを削除する。
        /// AssetWatcher の delete フックを取りこぼした場合の二段構え。
        /// </summary>
        public static void CleanupOrphans()
        {
            if (!Directory.Exists(CacheDir)) return;
            string[] files;
            try { files = Directory.GetFiles(CacheDir, "*" + SessionFileExtension); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Session cache scan failed: {ex.Message}");
                return;
            }

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(SessionFileExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                string guid = fileName.Substring(0, fileName.Length - SessionFileExtension.Length);
                if (string.IsNullOrEmpty(guid)) continue;
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath)) continue;

                try { File.Delete(file); }
                catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan session delete failed: {ex.Message}"); }
            }
        }

        // ゾーンが無ければ再着色は生まれない＝実質空。処理パラメータだけでは出力に影響しないため
        // 保存対象にしない（ファイルを無駄に増やさない）。
        private static bool IsEmpty(IrocaSessionState state)
            => state == null || state.zones == null || state.zones.Count == 0;
    }
}
