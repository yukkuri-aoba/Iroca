// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// <c>&lt;Project&gt;/&lt;相対フォルダ&gt;/&lt;テクスチャGUID&gt;&lt;拡張子&gt;</c> 形式のキャッシュファイルの
    /// パス解決・GUID 指定削除・orphan 整理（退避・復元・期限削除）を担う。
    /// <see cref="MaskFileStore"/> と <see cref="SessionFileStore"/> が共有し、中身の保存・読み込みは各ストアが持つ。
    /// </summary>
    internal sealed class GuidFileCache
    {
        // 未解決 GUID のファイルは即削除せずこの接尾辞を付けて退避する（CleanupOrphans 参照）。
        private const string OrphanSuffix = ".orphan";
        // 退避したまま GUID がこの日数を超えて解決できなければ初めて実削除する（猶予期間）。
        private const int OrphanRetentionDays = 30;

        private readonly string _relativeDir;
        private readonly string _extension;
        // ログ用の語（"Mask" / "mask" など）。ログ文言を変えないよう小文字形も明示で受け取る。
        private readonly string _label;
        private readonly string _labelLower;

        public GuidFileCache(string relativeDir, string extension, string label, string labelLower)
        {
            _relativeDir = relativeDir;
            _extension = extension;
            _label = label;
            _labelLower = labelLower;
        }

        /// <summary>
        /// プロジェクトルート直下の相対フォルダの絶対パスを返す（呼ぶたびに計算する）。
        /// </summary>
        public string CacheDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", _relativeDir));

        /// <summary>
        /// テクスチャのアセットパスから保存先ファイルのパスを返す。
        /// パスが空、または GUID が引けない（Assets 外など）ときは <c>null</c>。
        /// </summary>
        public string PathForAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return null;
            return Path.Combine(CacheDir, guid + _extension);
        }

        /// <summary>
        /// GUID 直接指定でファイルを削除する。
        /// </summary>
        public void DeleteByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            string path = Path.Combine(CacheDir, guid + _extension);
            if (!File.Exists(path)) return;
            try { File.Delete(path); }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] {_label} delete failed: {ex.Message}"); }
        }

        /// <summary>
        /// キャッシュフォルダを走査し、GUID が引けない現用ファイルを <c>.orphan</c> へ退避、
        /// GUID が再び引けた退避ファイルを元名へ復元、猶予期間を過ぎた退避ファイルを実削除する。
        /// 即削除しない理由は各ストアの CleanupOrphans を参照。
        /// </summary>
        public void CleanupOrphans()
        {
            if (!Directory.Exists(CacheDir)) return;
            string[] files;
            try { files = Directory.GetFiles(CacheDir); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] {_label} cache scan failed: {ex.Message}");
                return;
            }

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName)) continue;

                if (fileName.EndsWith(_extension + OrphanSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    string activeName = fileName.Substring(0, fileName.Length - OrphanSuffix.Length);
                    string guid = activeName.Substring(0, activeName.Length - _extension.Length);
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                        RestoreFromOrphan(file, Path.Combine(CacheDir, activeName));
                    else
                        DeleteOrphanIfExpired(file);
                }
                else if (fileName.EndsWith(_extension, StringComparison.OrdinalIgnoreCase))
                {
                    string guid = fileName.Substring(0, fileName.Length - _extension.Length);
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
        private void RetireToOrphan(string file)
        {
            string orphanPath = file + OrphanSuffix;
            try
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                if (File.Exists(orphanPath)) File.Delete(orphanPath);
                File.Move(file, orphanPath);   // 同一ボリュームの rename は mtime を保つ
            }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan {_labelLower} retire failed: {ex.Message}"); }
        }

        private void RestoreFromOrphan(string orphan, string activePath)
        {
            try
            {
                if (File.Exists(activePath)) File.Delete(orphan);
                else File.Move(orphan, activePath);
            }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan {_labelLower} restore failed: {ex.Message}"); }
        }

        private void DeleteOrphanIfExpired(string orphan)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(orphan) < DateTime.UtcNow.AddDays(-OrphanRetentionDays))
                    File.Delete(orphan);
            }
            catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan {_labelLower} delete failed: {ex.Message}"); }
        }
    }
}
