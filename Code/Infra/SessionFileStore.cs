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
        // 未解決 GUID のファイルは即削除せずこの接尾辞を付けて退避する（CleanupOrphans 参照）。
        private const string OrphanSuffix = ".orphan";
        // 退避したまま GUID がこの日数を超えて解決できなければ初めて実削除する（猶予期間）。
        private const int OrphanRetentionDays = 30;

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
        /// SessionCache を走査し、対応するテクスチャ（GUID）が見つからないファイルを整理する。
        /// AssetWatcher の delete フックを取りこぼした場合の二段構え。
        /// <para>
        /// GUID が未解決というだけでは即削除しない: ブランチ切替中・Library 再構築中など
        /// 一時的に GUID を引けないだけのことがあり、その瞬間に消すと編集内容が恒久的に失われる
        /// （ブランチを戻しても復元不能）。そこで未解決ファイルは <c>.orphan</c> へリネーム退避し、
        /// GUID が再び解決できたら元名へ復元、猶予期間（<see cref="OrphanRetentionDays"/> 日）を
        /// 超えて未解決のままの退避ファイルだけを実削除する。CleanupOrphans は Load より先に走る
        /// （<c>IrocaWindow.OnEnable</c>）ので、退避→復元は読み込み前に完了する。
        /// </para>
        /// </summary>
        public static void CleanupOrphans()
        {
            if (!Directory.Exists(CacheDir)) return;
            string[] files;
            try { files = Directory.GetFiles(CacheDir); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] Session cache scan failed: {ex.Message}");
                return;
            }

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName)) continue;

                if (fileName.EndsWith(SessionFileExtension + OrphanSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    string activeName = fileName.Substring(0, fileName.Length - OrphanSuffix.Length);
                    string guid = activeName.Substring(0, activeName.Length - SessionFileExtension.Length);
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                        RestoreFromOrphan(file, Path.Combine(CacheDir, activeName));
                    else
                        DeleteOrphanIfExpired(file);
                }
                else if (fileName.EndsWith(SessionFileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    string guid = fileName.Substring(0, fileName.Length - SessionFileExtension.Length);
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                        RetireToOrphan(file);
                }
            }
        }

        // GUID 未解決の現用ファイルを削除せず .orphan へ退避する。退避時刻を LastWriteTime に刻んで
        // 猶予クロックの起点にする（元の最終編集時刻ではなく「退避した瞬間」から N 日数える）。
        //
        // 刻んでから移動する順序が重要。移動後に刻む順序だと SetLastWriteTimeUtc が失敗したとき
        // 「元の最終編集時刻のまま .orphan になったファイル」が残り、それが猶予日数より古ければ
        // 次回の掃除で猶予を待たず即削除される（＝データを守るための退避が消す側に回る）。
        // 先に刻めば、失敗した場合は退避自体が起きず現用のまま残る。
        private static void RetireToOrphan(string file)
        {
            string orphanPath = file + OrphanSuffix;
            try
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                if (File.Exists(orphanPath)) File.Delete(orphanPath);
                File.Move(file, orphanPath);   // 同一ボリュームの rename は mtime を保つ
            }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan session retire failed: {ex.Message}"); }
        }

        private static void RestoreFromOrphan(string orphan, string activePath)
        {
            try
            {
                if (File.Exists(activePath)) File.Delete(orphan);
                else File.Move(orphan, activePath);
            }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan session restore failed: {ex.Message}"); }
        }

        private static void DeleteOrphanIfExpired(string orphan)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(orphan) < DateTime.UtcNow.AddDays(-OrphanRetentionDays))
                    File.Delete(orphan);
            }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan session delete failed: {ex.Message}"); }
        }

        // ゾーンが無ければ再着色は生まれない＝実質空。処理パラメータだけでは出力に影響しないため
        // 保存対象にしない（ファイルを無駄に増やさない）。
        private static bool IsEmpty(IrocaSessionState state)
            => state == null || state.zones == null || state.zones.Count == 0;
    }
}
