// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
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
    /// <para>
    /// 作りかけを待つ借り手が全員待つのをやめたら(<see cref="Abandon"/>)、作りかけを取り消す。NDMF は作りかけの
    /// パイプラインを最後まで待ってから次の表示へ進むので、要らなくなった計算を最後まで回すと、ほかのツールの分も
    /// 含めてシーンのプレビュー全体の更新がそのぶん遅れる。取り消した分はすぐ null で終わり、次に借りられたときに
    /// 作り直す(取り消した作りかけを、後から借りた人に貸さない)。
    /// </para>
    /// </summary>
    internal static class RecipePreviewTextures
    {
        private sealed class Entry
        {
            public Texture2D texture;
            // 出来上がり(作れなければ null)。完了はメインスレッド。
            public Task<Texture2D> ready;
            public int refs;
            // 作りかけ(ready)を待っている借り手の数(待つのをやめた借り手は数えない)と、その取り消し。
            public int waiting;
            public CancellationTokenSource cancel;

            public bool Building => !ready.IsCompleted && !cancel.IsCancellationRequested;
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
                if (entry.texture != null || entry.Building)
                {
                    entry.refs++;
                    if (!entry.ready.IsCompleted) entry.waiting++;
                    key = k;
                    return entry.ready;
                }
                // 作れなかった・取り消した(または外から消された)ものは作り直す。借り手の数はそのまま引き継ぐ。
            }
            else
            {
                entry = new Entry();
                Entries[k] = entry;
            }
            entry.refs++;

            var ready = new TaskCompletionSource<Texture2D>();
            var cancel = new CancellationTokenSource();
            entry.ready = ready.Task;
            entry.cancel = cancel;
            entry.waiting = 1;
            RecipeTextureBuilder.BuildAsync(recipe, cancel.Token, texture =>
            {
                // 作っている間に借り手が全員返していたら(またはまとめて捨てられたら)、または取り消して作り直して
                // いる(別の作りかけに替わった)なら、出来上がりは捨てる。
                if (!Entries.TryGetValue(k, out var current) || current != entry || current.ready != ready.Task)
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
            if (!entry.ready.IsCompleted) entry.cancel.Cancel();   // 誰も使わない作りかけは止める
            if (entry.texture != null) Object.DestroyImmediate(entry.texture);
            Entries.Remove(key);
        }

        /// <summary>
        /// 借りたまま、作りかけの出来上がりを待つのをやめる(<paramref name="ready"/> は <see cref="Acquire"/> が
        /// 返したもの。借りた 1 回につき 1 回まで。返すのはこれまでどおり <see cref="Release"/>)。待っている借り手が
        /// いなくなったら作りかけを取り消し、<paramref name="ready"/> はすぐ null で完了する。ほかの借り手が待って
        /// いれば取り消さない。出来上がった後や、作り直した後の作りかけには何もしない。
        /// </summary>
        public static void Abandon(string key, Task<Texture2D> ready)
        {
            if (key == null || ready == null || ready.IsCompleted) return;
            if (!Entries.TryGetValue(key, out var entry) || entry.ready != ready) return;
            if (--entry.waiting > 0) return;
            entry.cancel.Cancel();
        }

        /// <summary>テスト用: 貸し出し中(作りかけを含む)のテクスチャの数。</summary>
        internal static int Count => Entries.Count;

        // リロードの前に呼ばれる。1 つ捨てるのに失敗しても残りを捨て、例外を外へ出さない(Unity は購読者を順に
        // 呼ぶだけで、ここで投げると他ツールのリロード前の後始末が呼ばれない)。
        private static void ReleaseAll()
        {
            try
            {
                foreach (var entry in Entries.Values)
                {
                    try
                    {
                        // 作りかけの計算も止める(リロードの後には受け取り手がいない)。
                        if (!entry.ready.IsCompleted) entry.cancel.Cancel();
                        if (entry.texture != null) Object.DestroyImmediate(entry.texture);
                    }
                    catch (System.Exception e) { Debug.LogException(e); }
                }
            }
            finally { Entries.Clear(); }
        }
    }
}
