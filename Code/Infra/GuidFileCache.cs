// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// <c>&lt;Project&gt;/&lt;相対フォルダ&gt;/&lt;テクスチャGUID&gt;&lt;拡張子&gt;</c> 形式のキャッシュファイルの
    /// パス解決・GUID 指定削除・orphan 整理（退避・復元・期限削除）を担う。
    /// <see cref="MaskFileStore"/> と <see cref="SessionFileStore"/> が共有し、中身の保存・読み込みは各ストアが持つ。
    /// </summary>
    internal sealed class GuidFileCache {
        // 未解決 GUID のファイルは即削除せずこの接尾辞を付けて退避する（CleanupOrphans 参照）。
        private const string OrphanSuffix = ".orphan";
        // 退避したまま GUID がこの日数を超えて解決できなければ初めて実削除する（猶予期間）。
        private const int OrphanRetentionDays = 30;

        /// <summary>
        /// 保存の鍵(<see cref="PathForAsset"/> に渡す文字列)で、アセットのパスとサブアセットの番号を分ける文字。パスには入らない。
        /// 1 つのファイルに複数のテクスチャがあると(.asset・FBX の埋め込み)GUID は同じなので、番号で分ける。
        /// </summary>
        internal const char SubAssetSeparator = '\n';

        private readonly string _relativeDir;
        private readonly string _extension;
        // ログ用の語（"Mask" / "mask" など）。ログ文言を変えないよう小文字形も明示で受け取る。
        private readonly string _label;
        private readonly string _labelLower;

        public GuidFileCache(string relativeDir, string extension, string label, string labelLower) {
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
        /// テクスチャのアセットパスから保存先ファイルのパスを返す。サブアセットは <see cref="SubAssetSeparator"/> の後の番号でファイルを分ける。
        /// パスが空、または GUID が引けない（Assets 外など）ときは <c>null</c>。
        /// </summary>
        public string PathForAsset(string assetPath) {
            if (string.IsNullOrEmpty(assetPath)) {
                return null;
            }
            string subAsset = "";
            int separator = assetPath.IndexOf(SubAssetSeparator);
            if (separator >= 0) {
                subAsset = "_" + assetPath.Substring(separator + 1);
                assetPath = assetPath.Substring(0, separator);
            }
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) {
                return null;
            }
            return Path.Combine(CacheDir, guid + subAsset + _extension);
        }

        // ファイル名(拡張子を除く)の GUID の部分。
        private static string GuidOfName(string name) {
            int subAsset = name.IndexOf('_');
            return subAsset >= 0 ? name.Substring(0, subAsset) : name;
        }

        /// <summary>
        /// このアセットのファイルが退避されたままなら元へ戻す(読み込みの前に呼ぶ)。掃除(<see cref="CleanupOrphans"/>)は窓を開いたときだけ
        /// 走るので、その後で GUID が引けるようになった(同じ GUID でパッケージを入れ直した)テクスチャを開くと、ファイルが無いものとして
        /// 空で始まり、次の掃除で退避が消えていた。
        /// </summary>
        public void RestoreOrphanFor(string assetPath) {
            string path = PathForAsset(assetPath);
            if (path == null || File.Exists(path)) {
                return;
            }
            string orphan = path + OrphanSuffix;
            if (File.Exists(orphan)) {
                RestoreFromOrphan(orphan, path);
                return;
            }
            AdoptSharedFile(assetPath, path);
        }

        // 0.3.x までは同じファイルのテクスチャが GUID だけのファイルを共有していた。本体がテクスチャでなければ(FBX の埋め込みなど)
        // 誰の編集か決められないので、開いたサブアセットごとに写して引き継ぐ。本体がテクスチャなら、そのファイルは本体のもの。
        private void AdoptSharedFile(string key, string path) {
            int separator = key.IndexOf(SubAssetSeparator);
            if (separator < 0) {
                return;
            }
            string assetPath = key.Substring(0, separator);
            string shared = PathForAsset(assetPath);
            if (shared == null || !File.Exists(shared) || AssetDatabase.GetMainAssetTypeAtPath(assetPath) == typeof(Texture2D)) {
                return;
            }
            try { File.Copy(shared, path); } catch (Exception ex) { Debug.LogWarning($"[Iroca] {_label} could not be carried over: {ex.Message}"); }
        }

        /// <summary>
        /// GUID 直接指定でファイルを <c>.orphan</c> へ退避する(無ければ何もしない)。
        /// </summary>
        public void RetireByGuid(string guid) {
            if (string.IsNullOrEmpty(guid) || !Directory.Exists(CacheDir)) {
                return;
            }
            string path = Path.Combine(CacheDir, guid + _extension);
            if (File.Exists(path)) {
                RetireToOrphan(path);
            }
            foreach (var subAsset in Directory.GetFiles(CacheDir, guid + "_*" + _extension)) {
                RetireToOrphan(subAsset);
            }
        }

        /// <summary>
        /// <see cref="RetireByGuid"/> を複数の GUID にまとめて当てる。キャッシュフォルダの列挙は 1 回だけ
        /// (フォルダごとの削除で、中のアセットの数だけ列挙していた)。退避するファイルは GUID ごとに呼んだときと同じ
        /// (名前が「GUID + 拡張子」か「GUID_番号 + 拡張子」のもの)。
        /// </summary>
        public void RetireByGuids(System.Collections.Generic.ICollection<string> guids) {
            if (guids == null || guids.Count == 0 || !Directory.Exists(CacheDir)) {
                return;
            }
            var wanted = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var g in guids) {
                if (!string.IsNullOrEmpty(g)) {
                    wanted.Add(g);
                }
            }
            if (wanted.Count == 0) {
                return;
            }
            string[] files;
            try { files = Directory.GetFiles(CacheDir); } catch (Exception ex) {
                Debug.LogWarning($"[Iroca] {_label} cache scan failed: {ex.Message}");
                return;
            }
            foreach (string file in files) {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(_extension, StringComparison.Ordinal)) {
                    continue;
                }
                string stem = fileName.Substring(0, fileName.Length - _extension.Length);
                if (wanted.Contains(GuidOfName(stem))) {
                    RetireToOrphan(file);
                }
            }
        }

        /// <summary>
        /// キャッシュフォルダを走査し、GUID が引けない現用ファイルを <c>.orphan</c> へ退避、
        /// GUID が再び引けた退避ファイルを元名へ復元、猶予期間を過ぎた退避ファイルを実削除する。
        /// 即削除しない理由は各ストアの CleanupOrphans を参照。
        /// </summary>
        public void CleanupOrphans() {
            if (!Directory.Exists(CacheDir)) {
                return;
            }
            string[] files;
            try { files = Directory.GetFiles(CacheDir); } catch (Exception ex) {
                Debug.LogWarning($"[Iroca] {_label} cache scan failed: {ex.Message}");
                return;
            }

            foreach (string file in files) {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName)) {
                    continue;
                }

                if (fileName.EndsWith(_extension + OrphanSuffix, StringComparison.OrdinalIgnoreCase)) {
                    string activeName = fileName.Substring(0, fileName.Length - OrphanSuffix.Length);
                    string guid = GuidOfName(activeName.Substring(0, activeName.Length - _extension.Length));
                    if (string.IsNullOrEmpty(guid)) {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid))) {
                        RestoreFromOrphan(file, Path.Combine(CacheDir, activeName));
                    } else {
                        DeleteOrphanIfExpired(file);
                    }
                } else if (fileName.EndsWith(_extension, StringComparison.OrdinalIgnoreCase)) {
                    string guid = GuidOfName(fileName.Substring(0, fileName.Length - _extension.Length));
                    if (string.IsNullOrEmpty(guid)) {
                        continue;
                    }
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid))) {
                        RetireToOrphan(file);
                    }
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
        private void RetireToOrphan(string file) {
            string orphanPath = file + OrphanSuffix;
            try {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                if (File.Exists(orphanPath)) {
                    File.Delete(orphanPath);
                }
                File.Move(file, orphanPath);   // 同一ボリュームの rename は mtime を保つ
            } catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan {_labelLower} retire failed: {ex.Message}"); }
        }

        private void RestoreFromOrphan(string orphan, string activePath) {
            try {
                if (File.Exists(activePath)) {
                    File.Delete(orphan);
                } else {
                    File.Move(orphan, activePath);
                }
            } catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan {_labelLower} restore failed: {ex.Message}"); }
        }

        private void DeleteOrphanIfExpired(string orphan) {
            try {
                if (File.GetLastWriteTimeUtc(orphan) < DateTime.UtcNow.AddDays(-OrphanRetentionDays)) {
                    File.Delete(orphan);
                }
            } catch (Exception ex) { Debug.LogWarning($"[Iroca] Orphan {_labelLower} delete failed: {ex.Message}"); }
        }
    }
}
