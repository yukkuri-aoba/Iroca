// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 非破壊の色替えをアバターの複製に当てる本体。NDMF のパス(Iroca.NdmfIntegration)から呼ばれるが、
    /// NDMF には依存しない(EditMode テストから直接叩けるようにするため)。
    /// <para>
    /// 規則:
    /// <list type="bullet">
    /// <item>範囲は <see cref="IrocaRecolor"/> を置いた GameObject とその子。コンポーネントはレシピを
    ///   複数持てる(テクスチャごとに 1 つ)。</item>
    /// <item>範囲内の Renderer のマテリアルのうち、レシピの元テクスチャをどこかのテクスチャ枠で参照している
    ///   ものだけを複製し、その参照を色替え済みテクスチャへ差し替える。同じマテリアルを複数の Renderer が
    ///   使っていれば複製も共有する。範囲外の Renderer は元のマテリアルのまま。</item>
    /// <item>アニメーションで切り替わるマテリアル(衣装・表情のトグルなどのキーフレーム)も、切り替える先の
    ///   Renderer が範囲内なら同じ規則で差し替える(Renderer と同じマテリアルなら同じ複製を使う)。</item>
    /// <item>入れ子で範囲が重なったら、深い(近い)コンポーネントが勝つ。先に深い方を処理すると、
    ///   差し替え済みのマテリアルは元テクスチャを参照しなくなるので、浅い方は自然に手を出さない。
    ///   同じコンポーネントに同じテクスチャのレシピが複数あれば、同じ理由で先のものが勝つ。</item>
    /// <item>最後にコンポーネントをすべて取り除く(エラーがあっても)。</item>
    /// <item>ビルドでは 2 段で当てる(<see cref="Stage"/>)。1 段目(NDMF の Transforming 段)の後に動くツール
    ///   (VRCFury は NDMF の前半と最適化段の間に動く)がトグルなどで元のマテリアルを入れ直すので、2 段目
    ///   (最適化段)で同じ規則をもう一度当てる。複製と色替え済みテクスチャは <see cref="BuildState"/> で
    ///   使い回し、同じテクスチャを 2 枚入れない。</item>
    /// </list>
    /// 元のアセット(テクスチャ・マテリアル)には一切書き込まない。
    /// </para>
    /// </summary>
    internal static class NonDestructiveApplier
    {
        internal enum Problem
        {
            /// <summary>コンポーネントにレシピが設定されていない。</summary>
            MissingRecipe,
            /// <summary>範囲内に元テクスチャを使うマテリアルが無い(何も変わらない)。</summary>
            TextureNotUsedInScope,
            /// <summary>色替え済みテクスチャを作れなかった(詳細は <see cref="RecipeTextureBuilder.Failure"/>)。</summary>
            BuildFailed,
        }

        /// <summary>ビルド環境(NDMF なら BuildContext)への橋渡し。</summary>
        internal interface IHost
        {
            /// <summary>レシピから差し替え用テクスチャを作る(失敗は null)。</summary>
            Texture2D BuildTexture(IrocaRecipe recipe, out RecipeTextureBuilder.Failure failure);
            /// <summary>生成物(テクスチャ・マテリアル)をビルドの成果物として保存する。</summary>
            void SaveAsset(Object generated);
            /// <summary>元のオブジェクトが複製に置き換わったことを知らせる。</summary>
            void RegisterReplaced(Object original, Object replacement);
            /// <summary><paramref name="recipe"/> はどのレシピの問題か(レシピが無い・空の欄なら null)。</summary>
            void Report(Problem problem, IrocaRecolor component, IrocaRecipe recipe, RecipeTextureBuilder.Failure failure);
            /// <summary>
            /// アニメーションで切り替わるマテリアルのうち、切り替える先の Renderer が <paramref name="scope"/> か
            /// その子にあるものを <paramref name="mapping"/> で書き換える(同じものが返れば触らない)。
            /// アニメーションを扱えない環境では何もしない。
            /// </summary>
            void RewriteAnimatedMaterials(Transform scope, System.Func<Material, Material> mapping);
        }

        /// <summary>ビルドの直前に呼ばれる(いろかウィンドウが購読し、編集中の内容をレシピへ書き出す)。</summary>
        internal static event System.Action BeforeApply;

        /// <summary>どの段で当てるか。</summary>
        internal enum Stage
        {
            /// <summary>1 回で全部(テスト・NDMF を通さない呼び出し)。</summary>
            Single,
            /// <summary>1 段目: 設定の問題を報告し、コンポーネントは残す。</summary>
            First,
            /// <summary>2 段目: 後から入ったマテリアルも差し替え、使われなかったレシピを報告し、コンポーネントを外す。</summary>
            Late,
        }

        /// <summary>段をまたいで持ち越す状態(NDMF では BuildContext.GetState で 1 ビルドに 1 つ)。</summary>
        internal sealed class BuildState
        {
            /// <summary>レシピ → 色替え済みテクスチャ(2 段目も同じものを使う)。</summary>
            internal readonly Dictionary<IrocaRecipe, Texture2D> built = new Dictionary<IrocaRecipe, Texture2D>();
            /// <summary>作れなかったレシピ(2 段目は試さず、報告もしない)。</summary>
            internal readonly HashSet<IrocaRecipe> failed = new HashSet<IrocaRecipe>();
            /// <summary>(コンポーネント, レシピ) ごとの 元マテリアル → 複製(null = 元テクスチャを使わないので触らない)。</summary>
            internal readonly Dictionary<(IrocaRecolor, IrocaRecipe), Dictionary<Material, Material>> clones =
                new Dictionary<(IrocaRecolor, IrocaRecipe), Dictionary<Material, Material>>();
            /// <summary>範囲内で元テクスチャが使われた (コンポーネント, レシピ)。</summary>
            internal readonly HashSet<(IrocaRecolor, IrocaRecipe)> used = new HashSet<(IrocaRecolor, IrocaRecipe)>();
        }

        /// <summary>
        /// <paramref name="root"/>(ビルド用に複製されたアバター)配下の <see cref="IrocaRecolor"/> をすべて処理する。
        /// 差し替えたマテリアルの数を返す。
        /// </summary>
        public static int Apply(GameObject root, IHost host) => Apply(root, host, new BuildState(), Stage.Single);

        public static int Apply(GameObject root, IHost host, BuildState state, Stage stage)
        {
            // 開いているいろかウィンドウの編集中の内容をレシピへ書き出させる(直前の編集をビルドに乗せる)。
            if (stage != Stage.Late) BeforeApply?.Invoke();

            var components = new List<IrocaRecolor>(root.GetComponentsInChildren<IrocaRecolor>(true));
            int replaced = 0;
            try
            {
                // 深い順(同じ深さは見つかった順)。1 つのコンポーネントのレシピは並び順。
                var order = new List<(int depth, int index, IrocaRecolor c)>();
                for (int i = 0; i < components.Count; i++)
                    order.Add((Depth(components[i].transform, root.transform), i, components[i]));
                order.Sort((a, b) => a.depth != b.depth ? b.depth.CompareTo(a.depth) : a.index.CompareTo(b.index));

                foreach (var (_, _, component) in order)
                {
                    var recipes = component.recipes;
                    if (recipes == null || recipes.Count == 0)
                    {
                        if (stage != Stage.Late)
                            host.Report(Problem.MissingRecipe, component, null, RecipeTextureBuilder.Failure.None);
                        continue;
                    }
                    foreach (var recipe in recipes)
                        replaced += ApplyOne(component, recipe, host, state, stage);
                }
            }
            finally
            {
                // 1 段目はコンポーネントを残す(2 段目が範囲を知るため)。残っても VRChat SDK が
                // EditorOnly として外すので、2 段目が走らない経路でもアバターには残らない。
                if (stage != Stage.First)
                    foreach (var c in components)
                        if (c != null) Object.DestroyImmediate(c);
            }
            return replaced;
        }

        private static int ApplyOne(IrocaRecolor component, IrocaRecipe recipe, IHost host, BuildState state,
            Stage stage)
        {
            // 設定の問題は 1 回だけ報告する(2 段目では黙って飛ばす)。
            bool reportSetup = stage != Stage.Late;
            if (recipe == null)
            {
                if (reportSetup) host.Report(Problem.MissingRecipe, component, null, RecipeTextureBuilder.Failure.None);
                return 0;
            }
            var source = recipe.sourceTexture;
            if (source == null)
            {
                if (reportSetup) host.Report(Problem.BuildFailed, component, recipe, RecipeTextureBuilder.Failure.NoSourceTexture);
                return 0;
            }
            if (state.failed.Contains(recipe)) return 0;

            // 元マテリアル → 複製(null = 元テクスチャを参照していないので触らない)。2 段目も同じ表を使う。
            var key = (component, recipe);
            if (!state.clones.TryGetValue(key, out var clones))
                state.clones[key] = clones = new Dictionary<Material, Material>();
            state.built.TryGetValue(recipe, out var recolored);
            bool failed = false;
            int replaced = 0;

            // 元テクスチャを参照していれば複製して差し替えたものを返す(参照していなければ null)。
            // Renderer の欄とアニメーションのキーフレームの両方から呼ぶ(同じマテリアルは同じ複製になる)。
            Material Swap(Material original)
            {
                if (original == null || failed) return null;
                if (clones.TryGetValue(original, out var known)) return known;
                Material clone = null;
                if (References(original, source))
                {
                    state.used.Add(key);
                    if (recolored == null)
                    {
                        recolored = host.BuildTexture(recipe, out var failure);
                        if (recolored == null)
                        {
                            failed = true;
                            state.failed.Add(recipe);
                            host.Report(Problem.BuildFailed, component, recipe, failure);
                            return null;
                        }
                        host.SaveAsset(recolored);
                        state.built[recipe] = recolored;
                    }
                    clone = Object.Instantiate(original);
                    clone.name = original.name;
                    ReplaceTexture(clone, source, recolored);
                    host.SaveAsset(clone);
                    host.RegisterReplaced(original, clone);
                    replaced++;
                }
                clones[original] = clone;
                return clone;
            }

            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var clone = Swap(mats[i]);
                    if (clone == null) continue;
                    mats[i] = clone;
                    changed = true;
                }
                if (failed) return replaced;
                if (changed) renderer.sharedMaterials = mats;
            }

            // 衣装・表情のトグルなど、アニメーションで後から切り替わるマテリアルも同じ規則で差し替える。
            host.RewriteAnimatedMaterials(component.transform, m => Swap(m) ?? m);
            if (failed) return replaced;

            // 「使っていない」は最後の段で判断する(2 段目で入ったマテリアルで使われることがある)。
            if (stage != Stage.First && !state.used.Contains(key))
                host.Report(Problem.TextureNotUsedInScope, component, recipe, RecipeTextureBuilder.Failure.None);
            return replaced;
        }

        /// <summary>マテリアルがいずれかのテクスチャ枠で <paramref name="texture"/> を参照しているか。</summary>
        internal static bool References(Material material, Texture texture)
        {
            foreach (int id in material.GetTexturePropertyNameIDs())
                if (material.GetTexture(id) == texture) return true;
            return false;
        }

        private static void ReplaceTexture(Material material, Texture from, Texture to)
        {
            foreach (int id in material.GetTexturePropertyNameIDs())
                if (material.GetTexture(id) == from) material.SetTexture(id, to);
        }

        private static int Depth(Transform t, Transform root)
        {
            int d = 0;
            for (var p = t; p != null && p != root; p = p.parent) d++;
            return d;
        }
    }
}
