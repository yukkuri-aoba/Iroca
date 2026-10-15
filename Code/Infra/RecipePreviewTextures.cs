// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// シーン上のプレビューで、登録済みのレシピ(いろかウィンドウで編集していないもの)を映すための
    /// 色替え済みテクスチャの貸し借り。作り方は非破壊ビルドと同じで(<see cref="RecipeTextureBuilder.BuildAsync"/>)、
    /// ディスクのキャッシュ(Library/Iroca/RecipeTextureCache)も共有する(プレビューで作ったものは次の再生でも使われる)。
    /// 初めて映すときは色替えの計算に数秒かかるので、作りかけのまま貸し(完了する Task)、その間エディタは止めない。
    /// 同じ中身のレシピは 1 枚を共有し(作りかけも共有する)、最後の借り手が返したら捨てる。
    /// </summary>
    internal static class RecipePreviewTextures
    {
        private sealed class Entry
        {
            public Texture2D texture;
            // 出来上がり(作れなければ null)。完了はメインスレッド。
            public Task<Texture2D> ready;
            public int refs;
        }

        private static readonly Task<Texture2D> Nothing = Task.FromResult<Texture2D>(null);

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
        /// 借りる。出来上がりは返す Task の結果で、作れなければ(レシピが空・壊れている・元テクスチャが
        /// 読めない)null。キャッシュがあれば完了済みの Task を返す。返すときは <paramref name="key"/>
        /// (作れないと最初から分かっているときは null)を <see cref="Release"/> に渡す。
        /// </summary>
        public static Task<Texture2D> Acquire(IrocaRecipe recipe, out string key)
        {
            key = null;
            var like = recipe != null ? recipe.sourceTexture : null;
            if (like == null) return Nothing;
            string k = RecipeTextureBuilder.CacheKey(recipe, like);
            if (Entries.TryGetValue(k, out var entry))
            {
                // 手元にある・作っている途中なら、それを共有する。
                if (entry.texture != null || !entry.ready.IsCompleted)
                {
                    entry.refs++;
                    key = k;
                    return entry.ready;
                }
                // 作れなかった(または外から消された)ものは作り直す。借り手の数はそのまま引き継ぐ。
            }
            else
            {
                entry = new Entry();
                Entries[k] = entry;
            }
            entry.refs++;

            var ready = new TaskCompletionSource<Texture2D>();
            entry.ready = ready.Task;
            RecipeTextureBuilder.BuildAsync(recipe, texture =>
            {
                // 作っている間に借り手が全員返していたら(またはまとめて捨てられたら)、出来上がりは捨てる。
                if (!Entries.TryGetValue(k, out var current) || current != entry)
                {
                    if (texture != null) Object.DestroyImmediate(texture);
                    ready.SetResult(null);
                    return;
                }
                if (texture != null) texture.hideFlags = HideFlags.HideAndDontSave;
                entry.texture = texture;
                ready.SetResult(texture);
            });

            if (ready.Task.IsCompleted && ready.Task.Result == null)
            {
                // その場で作れないと分かった。借りたことにしない。
                Release(k);
                return Nothing;
            }
            key = k;
            return ready.Task;
        }

        public static void Release(string key)
        {
            if (key == null || !Entries.TryGetValue(key, out var entry)) return;
            if (--entry.refs > 0) return;
            if (entry.texture != null) Object.DestroyImmediate(entry.texture);
            Entries.Remove(key);
        }

        /// <summary>テスト用: 貸し出し中(作りかけを含む)のテクスチャの数。</summary>
        internal static int Count => Entries.Count;

        private static void ReleaseAll()
        {
            foreach (var entry in Entries.Values)
                if (entry.texture != null) Object.DestroyImmediate(entry.texture);
            Entries.Clear();
        }
    }
}
