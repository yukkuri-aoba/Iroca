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
    /// <para>
    /// 監視は軽く保つ(エディタにフォーカスがある間、NDMF は値を取り出す監視を毎フレーム読み直す。いろかを
    /// 使っていない物や後段のツールのプレビューに負担を掛けないため):
    /// <list type="bullet">
    /// <item>対象を探すのは Iroca Recolor の範囲(付けた GameObject とその子)だけ。持ち主のいない Renderer には
    ///   何も映さない規則なので、シーン全体を見るのは、ウィンドウが登録の無いテクスチャを編集している間だけ。</item>
    /// <item>Renderer は素材の並びが変わったか、マテリアルは「候補のテクスチャを含むか」だけを見る(配列を作らない)。</item>
    /// <item>どの元テクスチャを何に差し替えるか(<see cref="PreviewTargeting.Routes"/>)は対象を選ぶときに決めて
    ///   グループに持たせる。変われば NDMF が別のグループとしてノードを作り直す。ノード自身が監視するのは、
    ///   自分の Renderer の素材とそのテクスチャ欄、借りたレシピの中身、作業解像度だけ(シーン全体の部品の増減を
    ///   見張ると、関係ない編集でもノードが作り直され、同じ Renderer に掛かる後段のツールのプレビューまで
    ///   作り直しになる)。</item>
    /// <item>上流の変化がメッシュ・ブレンドシェイプだけなら、ノードを作り直さずに使い回す(<see cref="Node.Refresh"/>)。</item>
    /// </list>
    /// </para>
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

        // 「元の解像度で編集」。レシピの出来上がりが変わるので、変わったら作り直す。
        private static readonly PublishedValue<bool> AtSource =
            new PublishedValue<bool>(false, "Iroca/AtSourceResolution");

        // 監視の読み出しで使い回す入れ物(メインスレッドからしか使わない。毎フレーム配列を作らないため)。
        private static readonly List<Material> s_materials = new List<Material>();
        private static readonly List<int> s_textureIds = new List<int>();

        [InitializeOnLoadMethod]
        private static void Install()
        {
            LivePreview.TargetChanged -= OnTargetChanged;
            LivePreview.TargetChanged += OnTargetChanged;
            Live.Value = LivePreview.Current;
            WorkingResolution.Changed -= OnWorkingResolutionChanged;
            WorkingResolution.Changed += OnWorkingResolutionChanged;
            // メインスレッドで一度読んでおく(設定は EditorPrefs にあり、レシピのキャッシュの鍵もこれを読む)。
            AtSource.Value = WorkingResolution.AtSourceResolution;
        }

        private static void OnTargetChanged() => Live.Value = LivePreview.Current;

        private static void OnWorkingResolutionChanged() => AtSource.Value = WorkingResolution.AtSourceResolution;

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return Toggle;
        }

        public bool IsEnabled(ComputeContext context) => context.Observe(Toggle.IsEnabled);

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context) => FindTargets(context).ToImmutableList();

        /// <summary>
        /// 差し替えるものがある Renderer ごとのグループ(差し替え先を持たせてある)。テストからも呼ぶ(テストの
        /// アセンブリは System.Collections.Immutable を参照しないので、ImmutableList を返す口とは分けてある)。
        /// </summary>
        internal List<RenderGroup> FindTargets(ComputeContext context)
        {
            var groups = new List<RenderGroup>();
            var plan = ObservePlan(context);
            if (plan.IsEmpty) return groups;

            // 同じマテリアルを何個の Renderer が使っていても、テクスチャ欄の監視は 1 つにする。
            var usesCandidate = new Dictionary<Material, int>();
            foreach (var renderer in RenderersToSearch(context, plan))
            {
                // NDMF のプレビューが扱えるのはこの 2 種だけ。
                if (renderer == null || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;
                var routes = ObserveRoutes(context, plan, renderer, usesCandidate);
                if (routes != null) groups.Add(WithRoutes(RenderGroup.For(renderer), routes));
            }
            return groups;
        }

        /// <summary>テスト用: <paramref name="group"/> が <paramref name="renderer"/> だけのグループか。</summary>
        internal static bool IsGroupOf(RenderGroup group, Renderer renderer) =>
            group.Renderers.Count == 1 && group.Renderers[0] == renderer;

        // 映しうる Renderer。ふだんは Iroca Recolor の範囲だけ(部品の増減の監視もその下だけになる)。
        private static IEnumerable<Renderer> RenderersToSearch(ComputeContext context, PreviewTargeting.Plan plan)
        {
            if (plan.ShowsLiveAnywhere) return context.GetComponentsByType<Renderer>();
            var found = new List<Renderer>();
            var seen = new HashSet<Renderer>();
            foreach (var owner in plan.Owners)
            {
                if (owner == null) continue;
                foreach (var r in context.GetComponentsInChildren<Renderer>(owner, true))
                    if (seen.Add(r)) found.Add(r);
            }
            return found;
        }

        // Renderer の位置での差し替え先(差し替えるものが無ければ null)。素材の並び・テクスチャ欄・親子関係を監視する。
        private static PreviewTargeting.Routes ObserveRoutes(ComputeContext context, PreviewTargeting.Plan plan,
            Renderer renderer, Dictionary<Material, int> usesCandidate)
        {
            var materials = renderer.sharedMaterials;
            // 素材を差し替えられたら選び直す。
            context.Observe(renderer, r => SameMaterials(r, materials), (a, b) => a == b);
            int uses = 0;
            foreach (var m in materials)
            {
                if (m == null) continue;
                if (!usesCandidate.TryGetValue(m, out var u))
                {
                    // マテリアルのテクスチャを差し替えられたら選び直す。
                    u = context.Observe(m, mm => CandidateUse(mm, plan), (a, b) => a == b);
                    usesCandidate[m] = u;
                }
                uses |= u;
            }
            if (uses == 0) return null;

            // 差し替え先は持ち主(祖先の Iroca Recolor)で決まるので、付け替えたら選び直す。
            context.ObservePath(renderer.transform);
            var routes = plan.RoutesFor(renderer.transform, (uses & UsesLive) != 0);
            foreach (var m in materials)
                if (m != null && Uses(m, routes)) return routes;
            return null;
        }

        // いまの素材の並びが expected と同じか(配列を作らずに比べる)。
        private static bool SameMaterials(Renderer renderer, Material[] expected)
        {
            renderer.GetSharedMaterials(s_materials);
            bool same = s_materials.Count == expected.Length;
            for (int i = 0; same && i < expected.Length; i++) same = ReferenceEquals(s_materials[i], expected[i]);
            s_materials.Clear();
            return same;
        }

        // いまのテクスチャ欄の中身が expected と同じか(配列を作らずに比べる)。
        private static bool SameTextures(Material material, Texture[] expected)
        {
            material.GetTexturePropertyNameIDs(s_textureIds);
            bool same = s_textureIds.Count == expected.Length;
            for (int i = 0; same && i < expected.Length; i++)
                same = ReferenceEquals(material.GetTexture(s_textureIds[i]), expected[i]);
            s_textureIds.Clear();
            return same;
        }

        private static Texture[] TexturesOf(Material material)
        {
            var ids = material.GetTexturePropertyNameIDs();
            var textures = new Texture[ids.Length];
            for (int i = 0; i < ids.Length; i++) textures[i] = material.GetTexture(ids[i]);
            return textures;
        }

        // CandidateUse の値: ウィンドウで編集中のテクスチャを使う / それ以外の登録済みのテクスチャを使う。
        private const int UsesLive = 1, UsesRegistered = 2;

        // マテリアルがテクスチャ欄で差し替えの候補のテクスチャを使っているか(上の値の組み合わせ。使わなければ 0)。
        private static int CandidateUse(Material material, PreviewTargeting.Plan plan)
        {
            material.GetTexturePropertyNameIDs(s_textureIds);
            int uses = 0;
            foreach (int id in s_textureIds)
            {
                var texture = material.GetTexture(id);
                if (plan.IsLive(texture)) uses |= UsesLive;
                else if (plan.IsCandidate(texture)) uses |= UsesRegistered;
                if (uses == (UsesLive | UsesRegistered)) break;
            }
            s_textureIds.Clear();
            return uses;
        }

        private static bool Uses(Material material, PreviewTargeting.Routes routes)
        {
            material.GetTexturePropertyNameIDs(s_textureIds);
            bool uses = false;
            foreach (int id in s_textureIds)
            {
                if (!routes.TryGet(material.GetTexture(id), out _)) continue;
                uses = true;
                break;
            }
            s_textureIds.Clear();
            return uses;
        }

        // 差し替え先をグループに持たせる。NDMF 1.13 で等価の規則を明示する多重定義が足され、引数 1 つのこれは
        // 非推奨になったが、下限の 1.8.0 にはこれしか無い。Routes は IEquatable なので、どの版でも Routes.Equals で
        // 比べられる(NDMF 2.0 には対応していない)。
#pragma warning disable CS0618
        private static RenderGroup WithRoutes(RenderGroup group, PreviewTargeting.Routes routes) => group.WithData(routes);
#pragma warning restore CS0618

        // グループに持たせた差し替え先(持たせていないグループなら null)。
        private static PreviewTargeting.Routes RoutesOf(RenderGroup group)
        {
            try
            {
                return group.GetData<PreviewTargeting.Routes>();
            }
            catch (System.InvalidCastException)
            {
                return null;
            }
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            // 差し替え先は対象を選ぶときに決めてある。持たせていないグループ(NDMF の外から呼んだとき)だけ、
            // ここで規則の入力を監視しつつ求める。
            var routes = RoutesOf(group);
            PreviewTargeting.Plan plan = null;
            var node = new Node();
            var proxies = new List<Renderer>();
            foreach (var (original, proxy) in proxyPairs)
            {
                if (original == null || proxy == null) continue;
                node.Track(original);
                var r = routes;
                if (r == null)
                {
                    plan ??= ObservePlan(context);
                    context.ObservePath(original.transform);
                    r = plan.RoutesFor(original.transform, usesLive: true);
                }
                foreach (var m in proxy.sharedMaterials)
                    if (m != null) node.Borrow(r, m);
                proxies.Add(proxy);
            }
            node.ObserveInputs(context);

            var ready = node.Ready;
            if (ready.IsCompleted) return Task.FromResult<IRenderFilterNode>(node.Swap(proxies));
            // 作りかけのレシピを待つ。完了はメインスレッドだが、待ち合わせ(WhenAll)の続きはどこで走るか
            // 決まらないので、差し替えはメインスレッドへ戻してから行う。
            // NDMF は全部のノードがそろってから次の表示へ切り替えるので、この Task は何があっても完了させる
            // (残すと、ほかのツールの分も含めてシーンのプレビューが古い表示のまま止まる)。
            var result = new TaskCompletionSource<IRenderFilterNode>();
            ready.ContinueWith(_ => PreviewJobMainThread.Post(() =>
            {
                try
                {
                    result.TrySetResult(node.Swap(proxies));
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                    result.TrySetResult(node);
                }
            }), TaskScheduler.Default);
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
                var recipes = component.recipes != null ? component.recipes.ToArray() : System.Array.Empty<IrocaRecipe>();
                // レシピの欄が変わったら選び直す。
                context.Observe(component, c => SameRecipes(c, recipes), (a, b) => a == b);
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

        // いまのレシピの欄が expected と同じか(配列を作らずに比べる)。
        private static bool SameRecipes(IrocaRecolor component, IrocaRecipe[] expected)
        {
            var recipes = component.recipes;
            int count = recipes != null ? recipes.Count : 0;
            if (count != expected.Length) return false;
            for (int i = 0; i < count; i++)
                if (!ReferenceEquals(recipes[i], expected[i])) return false;
            return true;
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
            // 借りたレシピ(中身が変わったら作り直す)。
            private readonly List<IrocaRecipe> _recipes = new List<IrocaRecipe>();
            // 元の Renderer と、作ったときの素材の並び・各素材のテクスチャ欄(変わったら作り直す)。
            private readonly List<(Renderer renderer, Material[] materials, Texture[][] textures)> _originals =
                new List<(Renderer, Material[], Texture[][])>();
            // いまこのノードを使っている NDMF の文脈(入力が変わったら無効になる)。
            private ComputeContext _context;

            // 使い回すとき(Refresh で自分を返す)は何も変えていない。作り直すときは新しいノードになり、
            // 最初の世代のこの値は NDMF が見ない。
            public RenderAspects WhatChanged => 0;

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
            public void Borrow(PreviewTargeting.Routes routes, Material material)
            {
                if (!_borrowed.Add(material)) return;
                foreach (int id in material.GetTexturePropertyNameIDs())
                {
                    if (!routes.TryGet(material.GetTexture(id), out var replacement)) continue;
                    var slot = new Slot { material = material, property = id };
                    if (replacement.IsLive)
                    {
                        slot.live = LivePreview.Acquire(replacement.source);
                        if (slot.live == null) continue;
                        _live.Add(replacement.source);
                    }
                    else
                    {
                        slot.recipe = RecipePreviewTextures.Acquire(replacement.recipe, out var key);
                        if (key != null) _recipeKeys.Add(key);
                        if (!_recipes.Contains(replacement.recipe)) _recipes.Add(replacement.recipe);
                    }
                    _slots.Add(slot);
                }
            }

            /// <summary>元の Renderer の、いまの素材の並びと各素材のテクスチャ欄を覚えておく(<see cref="ObserveInputs"/> で監視する)。</summary>
            public void Track(Renderer original)
            {
                var materials = original.sharedMaterials;
                var textures = new Texture[materials.Length][];
                for (int i = 0; i < materials.Length; i++)
                    textures[i] = materials[i] != null ? TexturesOf(materials[i]) : null;
                _originals.Add((original, materials, textures));
            }

            /// <summary>
            /// このノードの入力を <paramref name="context"/> で監視する。元の Renderer の素材の並びとテクスチャ欄
            /// (アニメーションのプレビューでの切り替えなど、NDMF の変更通知が来ない変化も拾う)、借りたレシピの中身と
            /// 作業解像度(ウィンドウがレシピへ保存したとき・「元の解像度で編集」を切り替えたとき)。
            /// 比べる相手はノードを作ったときの値(使い回すときも同じ)。
            /// </summary>
            public void ObserveInputs(ComputeContext context)
            {
                _context = context;
                foreach (var (renderer, materials, textures) in _originals)
                {
                    context.Observe(renderer, r => SameMaterials(r, materials), (a, b) => a == b);
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] == null) continue;
                        var expected = textures[i];
                        context.Observe(materials[i], m => SameTextures(m, expected), (a, b) => a == b);
                    }
                }
                foreach (var recipe in _recipes)
                    context.Observe(recipe, r => r.sessionJson);
                if (_recipes.Count > 0) context.Observe(AtSource);
            }

            // 作ったときから元の Renderer の素材・テクスチャ欄が変わっていないか。
            private bool OriginalsUnchanged()
            {
                foreach (var (renderer, materials, textures) in _originals)
                {
                    if (renderer == null || !SameMaterials(renderer, materials)) return false;
                    for (int i = 0; i < materials.Length; i++)
                        if (materials[i] != null && !SameTextures(materials[i], textures[i])) return false;
                }
                return true;
            }

            /// <summary>
            /// 借りたテクスチャを入れたマテリアルの複製を作り、組み立て用のプロキシへ差し替える
            /// (<see cref="Ready"/> の完了後に、メインスレッドで呼ぶ)。
            /// </summary>
            public Node Swap(List<Renderer> proxies)
            {
                foreach (var s in _slots)
                {
                    // 作れなかったレシピ・待っている間に消えたマテリアルは飛ばす(ほかの差し替えは続ける)。
                    var texture = s.recipe != null
                        ? (s.recipe.Status == TaskStatus.RanToCompletion ? s.recipe.Result : null)
                        : s.live;
                    if (texture == null || s.material == null) continue;
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

            /// <summary>
            /// 上流が変わって NDMF がパイプラインを組み直すとき。自分の入力(元の素材・レシピの中身・作業解像度)が
            /// 変わった、または上流でマテリアル・テクスチャが変わったら null を返して作り直してもらう(差し替えの
            /// 対応表は上流のマテリアルを鍵にしている)。メッシュ・ブレンドシェイプだけの変化なら、同じ複製を新しい
            /// プロキシに差し直して自分を返す(後段のツールにも「何も変えていない」と伝わり、作り直しが連鎖しない)。
            /// </summary>
            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context,
                RenderAspects updatedAspects)
            {
                if (_context == null || _context.IsInvalidated
                    || (updatedAspects & (RenderAspects.Material | RenderAspects.Texture)) != 0
                    || !OriginalsUnchanged())
                    return Task.FromResult<IRenderFilterNode>(null);
                ObserveInputs(context);
                foreach (var (_, proxy) in proxyPairs) Replace(proxy);
                return Task.FromResult<IRenderFilterNode>(this);
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
                _recipes.Clear();
                _originals.Clear();
            }
        }
    }
}
