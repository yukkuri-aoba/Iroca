// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// シーン上のプレビューで、登録済みのレシピ(いろかウィンドウで編集していないもの)を映すための
    /// 色替え済みテクスチャの貸し借り。作り方は非破壊ビルドと同じ <see cref="RecipeTextureBuilder.Build"/> で、
    /// ディスクのキャッシュ(Library/Iroca/RecipeTextureCache)も共有する(プレビューで作ったものは次の再生でも使われる)。
    /// 同じ中身のレシピは 1 枚を共有し、最後の借り手が返したら捨てる。
    /// </summary>
    internal static class RecipePreviewTextures
    {
        private sealed class Entry
        {
            public Texture2D texture;
            public int refs;
        }

        // 鍵 = RecipeTextureBuilder.CacheKey(レシピの中身・元テクスチャの取り込み結果・コードの版を含む)。
        // レシピを編集すると鍵が変わるので、古い借り手が返すまで古いテクスチャも残る。
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

        [InitializeOnLoadMethod]
        private static void Install()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
        }

        /// <summary>
        /// 借りる。作れなければ(レシピが空・壊れている・元テクスチャが読めない)null。
        /// 返すときは <paramref name="key"/> を <see cref="Release"/> に渡す。
        /// </summary>
        public static Texture2D Acquire(IrocaRecipe recipe, out string key)
        {
            key = null;
            var like = recipe != null ? recipe.sourceTexture : null;
            if (like == null) return null;
            string k = RecipeTextureBuilder.CacheKey(recipe, like);
            if (Entries.TryGetValue(k, out var entry) && entry.texture != null)
            {
                entry.refs++;
                key = k;
                return entry.texture;
            }
            var texture = RecipeTextureBuilder.Build(recipe, out _);
            if (texture == null) return null;
            texture.hideFlags = HideFlags.HideAndDontSave;
            Entries[k] = new Entry { texture = texture, refs = 1 };
            key = k;
            return texture;
        }

        public static void Release(string key)
        {
            if (key == null || !Entries.TryGetValue(key, out var entry)) return;
            if (--entry.refs > 0) return;
            if (entry.texture != null) Object.DestroyImmediate(entry.texture);
            Entries.Remove(key);
        }

        /// <summary>テスト用: 貸し出し中のテクスチャの数。</summary>
        internal static int Count => Entries.Count;

        private static void ReleaseAll()
        {
            foreach (var entry in Entries.Values)
                if (entry.texture != null) Object.DestroyImmediate(entry.texture);
            Entries.Clear();
        }
    }
}
