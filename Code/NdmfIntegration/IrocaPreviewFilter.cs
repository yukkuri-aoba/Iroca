// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;

namespace Iroca.NdmfIntegration
{
    /// <summary>
    /// シーン上のアバターに色替えを映す NDMF のプレビュー(再生しない編集中だけ動く。元のアセットは触らない)。
    /// <list type="bullet">
    /// <item>いろかウィンドウで編集中のテクスチャは、ウィンドウのプレビューの結果をそのまま映す
    ///   (<see cref="LivePreview"/>。設定を動かすとアバターも追従する)。</item>
    /// <item>登録済み(<see cref="IrocaRecolor"/>)でウィンドウが編集していないものは、レシピの出来上がりを映す
    ///   (<see cref="RecipePreviewTextures"/>。非破壊ビルドと同じテクスチャ)。初めて映すときは作るのに数秒
    ///   かかるので、出来上がるまで <see cref="Instantiate"/> を完了させない。NDMF はその間、前の表示
    ///   (初回は元のまま)を出し続け、エディタは止まらない。</item>
    /// </list>
    /// どこに何を映すかの規則は <see cref="PreviewTargeting"/>(NDMF 非依存)。ここは NDMF への橋渡しと、
    /// 規則が依存する値の監視(変わったら NDMF が作り直す)だけ。
    /// </summary>
    internal sealed class IrocaPreviewFilter : IRenderFilter
    {
        // NDMF のプレビュー設定(Tools > NDM Framework > Configure Previews)に出るオン/オフ。
        internal static readonly TogglablePreviewNode Toggle = TogglablePreviewNode.Create(
            () => Localization.IsJapanese ? "色替え" : "Recolor",
            "com.yukkuri-aoba.iroca/Recolor");

        // いろかウィンドウが編集しているもの。変わったら NDMF が対象を選び直す。
        private static readonly PublishedValue<LivePreview.Target> Live =
            new PublishedValue<LivePreview.Target>(null, "Iroca/LiveTarget");

        [InitializeOnLoadMethod]
        private static void Install()
        {
            LivePreview.TargetChanged -= OnTargetChanged;
            LivePreview.TargetChanged += OnTargetChanged;
            Live.Value = LivePreview.Current;
        }

        private static void OnTargetChanged() => Live.Value = LivePreview.Current;

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return Toggle;
        }

        public bool IsEnabled(ComputeContext context) => context.Observe(Toggle.IsEnabled);

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context) => FindTargets(context).ToImmutableList();

        /// <summary>
        /// 差し替えるものがある Renderer ごとのグループ。テストからも呼ぶ(テストのアセンブリは
        /// System.Collections.Immutable を参照しないので、ImmutableList を返す口とは分けてある)。
        /// </summary>
        internal List<RenderGroup> FindTargets(ComputeContext context)
        {
            var groups = new List<RenderGroup>();
            var plan = ObservePlan(context);
            if (plan.IsEmpty) return groups;

            foreach (var renderer in context.GetComponentsByType<Renderer>())
            {
                // NDMF のプレビューが扱えるのはこの 2 種だけ。
                if (renderer == null || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;
                if (HasReplacement(context, plan, renderer)) groups.Add(RenderGroup.For(renderer));
            }
            return groups;
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var plan = ObservePlan(context);
            var node = new Node();
            var proxies = new List<Renderer>();
            foreach (var (original, proxy) in proxyPairs)
            {
                if (original == null || proxy == null) continue;
                // 元の Renderer の素材と親子関係が変わったら作り直す(上流の差し替えは NDMF が Refresh で知らせる)。
                HasReplacement(context, plan, original);
                foreach (var m in proxy.sharedMaterials)
                    if (m != null) node.Borrow(context, plan, original.transform, m);
                proxies.Add(proxy);
            }

            var ready = node.Ready;
            if (ready.IsCompleted) return Task.FromResult<IRenderFilterNode>(node.Swap(proxies));
            // 作りかけのレシピを待つ。完了はメインスレッドだが、待ち合わせ(WhenAll)の続きはどこで走るか
            // 決まらないので、差し替えはメインスレッドへ戻してから行う。
            var result = new TaskCompletionSource<IRenderFilterNode>();
            ready.ContinueWith(_ => PreviewJobMainThread.Post(() => result.SetResult(node.Swap(proxies))),
                TaskScheduler.Default);
            return result.Task;
        }

        /// <summary>規則の入力(ウィンドウの編集対象・シーンのコンポーネント・レシピ・親子関係)を監視しつつ読む。</summary>
        private static PreviewTargeting.Plan ObservePlan(ComputeContext context)
        {
            var live = context.Observe(Live, t => t, (a, b) => Equals(a, b));
            var registered = new List<PreviewTargeting.Registered>();
            foreach (var component in context.GetComponentsByType<IrocaRecolor>())
            {
                if (component == null) continue;
                var recipes = context.Observe(component,
                    c => c.recipes != null ? c.recipes.ToArray() : System.Array.Empty<IrocaRecipe>(),
                    (a, b) => a.SequenceEqual(b));
                // 付け替えると範囲が変わる(ObservePath は呼んだ時点で監視を始める)。
                context.ObservePath(component.transform);
                // 並び順のまま渡す(同じテクスチャのレシピが重なったら先が勝つ。ビルドと同じ)。
                foreach (var recipe in recipes)
                {
                    if (recipe == null) continue;
                    var source = context.Observe(recipe, r => r.sourceTexture);
                    if (source == null) continue;
                    registered.Add(new PreviewTargeting.Registered(component.gameObject, recipe, source));
                }
            }
            return new PreviewTargeting.Plan(registered, live);
        }

        private static bool HasReplacement(ComputeContext context, PreviewTargeting.Plan plan, Renderer renderer)
        {
            var mats = context.Observe(renderer, r => r.sharedMaterials, (a, b) => a.SequenceEqual(b));
            bool candidate = false;
            foreach (var m in mats)
            {
                if (m == null) continue;
                // マテリアルのテクスチャを差し替えられたら選び直す。
                foreach (var t in context.Observe(m, TexturesOf, (a, b) => a.SequenceEqual(b)))
                    candidate |= plan.IsCandidate(t);
            }
            if (!candidate) return false;

            context.ObservePath(renderer.transform);
            foreach (var m in mats)
            {
                if (m == null) continue;
                foreach (var t in TexturesOf(m))
                    if (plan.TryResolve(renderer.transform, t, out _)) return true;
            }
            return false;
        }

        private static Texture[] TexturesOf(Material material)
        {
            var ids = material.GetTexturePropertyNameIDs();
            var textures = new Texture[ids.Length];
            for (int i = 0; i < ids.Length; i++) textures[i] = material.GetTexture(ids[i]);
            return textures;
        }

        private sealed class Node : IRenderFilterNode
        {
            // 差し替え 1 つぶん: マテリアルのテクスチャ欄と、入れるテクスチャ(ライブは即時、レシピは作りかけのことがある)。
            private struct Slot
            {
                public Material material;
                public int property;
                public Texture live;
                public Task<Texture2D> recipe;
            }

            private readonly List<Slot> _slots = new List<Slot>();
            private readonly HashSet<Material> _borrowed = new HashSet<Material>();
            private readonly Dictionary<Material, Material> _clones = new Dictionary<Material, Material>();
            private readonly List<Texture2D> _live = new List<Texture2D>();
            private readonly List<string> _recipeKeys = new List<string>();

            public RenderAspects WhatChanged => RenderAspects.Material | RenderAspects.Texture;

            /// <summary>借りたテクスチャがすべて出来上がったら完了する。</summary>
            public Task Ready
            {
                get
                {
                    var pending = new List<Task>();
                    foreach (var s in _slots)
                        if (s.recipe != null && !s.recipe.IsCompleted) pending.Add(s.recipe);
                    return pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(pending);
                }
            }

            /// <summary>このマテリアルの差し替え先テクスチャを借りる(複製はまだ作らない)。</summary>
            public void Borrow(ComputeContext context, PreviewTargeting.Plan plan, Transform owner, Material material)
            {
                if (!_borrowed.Add(material)) return;
                foreach (int id in material.GetTexturePropertyNameIDs())
                {
                    if (!plan.TryResolve(owner, material.GetTexture(id), out var replacement)) continue;
                    var slot = new Slot { material = material, property = id };
                    if (replacement.IsLive)
                    {
                        slot.live = LivePreview.Acquire(replacement.source);
                        if (slot.live == null) continue;
                        _live.Add(replacement.source);
                    }
                    else
                    {
                        // レシピの中身が変わったら作り直す(ウィンドウがレシピへ保存したとき)。
                        context.Observe(replacement.recipe, r => r.sessionJson);
                        slot.recipe = RecipePreviewTextures.Acquire(replacement.recipe, out var key);
                        if (key != null) _recipeKeys.Add(key);
                    }
                    _slots.Add(slot);
                }
            }

            /// <summary>
            /// 借りたテクスチャを入れたマテリアルの複製を作り、組み立て用のプロキシへ差し替える
            /// (<see cref="Ready"/> の完了後に、メインスレッドで呼ぶ)。
            /// </summary>
            public Node Swap(List<Renderer> proxies)
            {
                foreach (var s in _slots)
                {
                    var texture = s.recipe != null ? s.recipe.Result : s.live;
                    if (texture == null) continue;
                    if (!_clones.TryGetValue(s.material, out var clone))
                    {
                        // hideFlags は既定のまま(DontSave にすると、ノードが片付けられずに残ったとき
                        // 未使用アセットの掃除でも消えなくなる)。プロキシは保存されないので複製も保存されない。
                        clone = Object.Instantiate(s.material);
                        clone.name = s.material.name + " (Iroca)";
                        _clones[s.material] = clone;
                    }
                    clone.SetTexture(s.property, texture);
                }
                foreach (var proxy in proxies) Replace(proxy);
                return this;
            }

            // NDMF は毎フレーム、描画用のプロキシのマテリアルを元の Renderer のものへ戻してから各ノードを呼ぶ
            // (Instantiate で触るのは組み立て用のプロキシで、下流のノードに差し替えを見せるためのもの)。
            // なので描画のたびに、組み立て時に作った複製へ差し替え直す。上流のノードが差し替えたマテリアルも
            // 組み立て時と同じものなので、同じ対応表で引ける。
            public void OnFrame(Renderer original, Renderer proxy) => Replace(proxy);

            private void Replace(Renderer proxy)
            {
                if (_clones.Count == 0 || proxy == null) return;
                var mats = proxy.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !_clones.TryGetValue(mats[i], out var clone) || clone == null) continue;
                    mats[i] = clone;
                    changed = true;
                }
                if (changed) proxy.sharedMaterials = mats;
            }

            public void Dispose()
            {
                foreach (var clone in _clones.Values)
                    if (clone != null) Object.DestroyImmediate(clone);
                _clones.Clear();
                _slots.Clear();
                foreach (var source in _live) LivePreview.Release(source);
                _live.Clear();
                foreach (var key in _recipeKeys) RecipePreviewTextures.Release(key);
                _recipeKeys.Clear();
            }
        }
    }
}
