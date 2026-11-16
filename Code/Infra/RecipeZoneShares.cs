// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// レシピの色替えで、どのゾーンがどこを塗るか(<see cref="ZoneShareGrid"/>)の控え。非破壊ビルドとシーンのプレビューが、
    /// 影の色をマテリアルごとに合わせるのに使う(<see cref="MaterialShadowColors"/>)。
    /// 鍵は差し替えテクスチャのキャッシュと同じ(<see cref="RecipeTextureBuilder.CacheKey(IrocaRecipe, Texture2D)"/>。レシピの中身・
    /// 元テクスチャの取り込み・作業解像度・いろかのコードが同じなら同じ)。差し替えテクスチャを作る計算が同じ計算の中で控えて
    /// 入れる(<see cref="Store"/>)。テクスチャをキャッシュから読んで計算しなかったときに無ければ、1 回だけ計算して控える。
    /// メモリ(最近のもの)とファイル(Library/Iroca/ZoneShareCache。1 つ 64KB × ゾーンの数ほど)に置く。メインスレッドで使う。
    /// </summary>
    internal static class RecipeZoneShares {
        // メモリに置く数と、ファイルに残す数(古いものから消す)。
        private const int MemoryEntries = 16;
        private const int MaxFiles = 256;

        private static readonly Dictionary<string, ZoneShareGrid> s_memory = new Dictionary<string, ZoneShareGrid>();
        private static readonly LinkedList<string> s_order = new LinkedList<string>();

        internal static string CacheDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library/Iroca/ZoneShareCache"));

        /// <summary>鍵の控えのファイル(internal はテストが後で消すため)。</summary>
        internal static string CachePath(string key) => Path.Combine(CacheDir, key + ".shares");

        private static string PathOf(string key) => CachePath(key);

        /// <summary>
        /// レシピの控えを返す。無ければ <paramref name="computeIfMissing"/> のときだけ計算する(元テクスチャを作業解像度で読み、
        /// 色替えを 1 回走らせる。4K で 1 秒ほど)。作れなければ null。
        /// </summary>
        internal static ZoneShareGrid Get(IrocaRecipe recipe, bool computeIfMissing) {
            var like = recipe != null ? recipe.sourceTexture : null;
            if (like == null) {
                return null;
            }
            string key = RecipeTextureBuilder.CacheKey(recipe, like);
            var grid = Find(key);
            if (grid != null || !computeIfMissing) {
                return grid;
            }
            grid = RecipeTextureBuilder.ComputeZoneShares(recipe);
            if (grid != null) {
                Store(key, grid);
            }
            return grid;
        }

        /// <summary>メモリかファイルにある控え(無ければ null)。</summary>
        internal static ZoneShareGrid Find(string key) {
            if (string.IsNullOrEmpty(key)) {
                return null;
            }
            if (s_memory.TryGetValue(key, out var grid)) {
                Touch(key);
                return grid;
            }
            string path = PathOf(key);
            if (!File.Exists(path)) {
                return null;
            }
            try {
                using (var stream = File.OpenRead(path)) {
                    grid = ZoneShareGrid.Read(stream);
                }
            } catch (IOException) {
                return null;
            } catch (UnauthorizedAccessException) {
                return null;
            }
            if (grid != null) {
                Remember(key, grid);
            }
            return grid;
        }

        /// <summary>控えを入れる(メモリとファイル)。ファイルに書けなくても、メモリには残る。</summary>
        internal static void Store(string key, ZoneShareGrid grid) {
            if (string.IsNullOrEmpty(key) || grid == null) {
                return;
            }
            Remember(key, grid);
            try {
                string dir = CacheDir;
                Directory.CreateDirectory(dir);
                string path = PathOf(key), temp = path + ".tmp";
                using (var stream = File.Create(temp)) {
                    grid.Write(stream);
                }
                if (File.Exists(path)) {
                    File.Delete(path);
                }
                File.Move(temp, path);
                Trim(dir);
            } catch (IOException ex) {
                Debug.LogWarning($"[Iroca] 影の色の控えを書けませんでした: {ex.Message}");
            } catch (UnauthorizedAccessException ex) {
                Debug.LogWarning($"[Iroca] 影の色の控えを書けませんでした: {ex.Message}");
            }
        }

        private static void Remember(string key, ZoneShareGrid grid) {
            s_memory[key] = grid;
            Touch(key);
            while (s_order.Count > MemoryEntries) {
                var oldest = s_order.Last.Value;
                s_order.RemoveLast();
                s_memory.Remove(oldest);
            }
        }

        private static void Touch(string key) {
            s_order.Remove(key);
            s_order.AddFirst(key);
        }

        // 数が上限を超えたら、古いものから消す(書きかけの残りも消す)。
        private static void Trim(string dir) {
            var info = new DirectoryInfo(dir);
            foreach (var f in info.GetFiles("*.shares.tmp")) {
                try { f.Delete(); } catch (IOException) { /* 使用中などは次回 */ }
            }
            var files = new List<FileInfo>(info.GetFiles("*.shares"));
            if (files.Count <= MaxFiles) {
                return;
            }
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < files.Count - MaxFiles; i++) {
                try { files[i].Delete(); } catch (IOException) { /* 使用中などは次回 */ }
            }
        }

        /// <summary>メモリの控えを捨てる(テスト用)。</summary>
        internal static void ClearMemory() {
            s_memory.Clear();
            s_order.Clear();
        }
    }
}
