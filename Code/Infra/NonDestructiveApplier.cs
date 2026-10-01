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
    /// <item>範囲は <see cref="IrocaRecolor"/> を置いた GameObject とその子。</item>
    /// <item>範囲内の Renderer のマテリアルのうち、レシピの元テクスチャをどこかのテクスチャ枠で参照している
    ///   ものだけを複製し、その参照を色替え済みテクスチャへ差し替える。同じマテリアルを複数の Renderer が
    ///   使っていれば複製も共有する。範囲外の Renderer は元のマテリアルのまま。</item>
    /// <item>入れ子で範囲が重なったら、深い(近い)コンポーネントが勝つ。先に深い方を処理すると、
    ///   差し替え済みのマテリアルは元テクスチャを参照しなくなるので、浅い方は自然に手を出さない。</item>
    /// <item>最後にコンポーネントをすべて取り除く(エラーがあっても)。</item>
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
            void Report(Problem problem, IrocaRecolor component, RecipeTextureBuilder.Failure failure);
        }

        /// <summary>ビルドの直前に呼ばれる(いろかウィンドウが購読し、編集中の内容をレシピへ書き出す)。</summary>
        internal static event System.Action BeforeApply;

        /// <summary>
        /// <paramref name="root"/>(ビルド用に複製されたアバター)配下の <see cref="IrocaRecolor"/> をすべて処理する。
        /// 差し替えたマテリアルの数を返す。
        /// </summary>
        public static int Apply(GameObject root, IHost host)
        {
            // 開いているいろかウィンドウの編集中の内容をレシピへ書き出させる(直前の編集をビルドに乗せる)。
            BeforeApply?.Invoke();

            var components = new List<IrocaRecolor>(root.GetComponentsInChildren<IrocaRecolor>(true));
            int replaced = 0;
            try
            {
                // 深い順(同じ深さは見つかった順)。
                var order = new List<(int depth, int index, IrocaRecolor c)>();
                for (int i = 0; i < components.Count; i++)
                    order.Add((Depth(components[i].transform, root.transform), i, components[i]));
                order.Sort((a, b) => a.depth != b.depth ? b.depth.CompareTo(a.depth) : a.index.CompareTo(b.index));

                var built = new Dictionary<IrocaRecipe, Texture2D>();
                foreach (var (_, _, component) in order)
                    replaced += ApplyOne(component, host, built);
            }
            finally
            {
                foreach (var c in components)
                    if (c != null) Object.DestroyImmediate(c);
            }
            return replaced;
        }

        private static int ApplyOne(IrocaRecolor component, IHost host, Dictionary<IrocaRecipe, Texture2D> built)
        {
            var recipe = component.recipe;
            if (recipe == null)
            {
                host.Report(Problem.MissingRecipe, component, RecipeTextureBuilder.Failure.None);
                return 0;
            }
            var source = recipe.sourceTexture;
            if (source == null)
            {
                host.Report(Problem.BuildFailed, component, RecipeTextureBuilder.Failure.NoSourceTexture);
                return 0;
            }

            // 元マテリアル → 複製(null = 元テクスチャを参照していないので触らない)。
            var clones = new Dictionary<Material, Material>();
            Texture2D recolored = null;
            bool usedInScope = false;
            int replaced = 0;

            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var original = mats[i];
                    if (original == null) continue;
                    if (!clones.TryGetValue(original, out var clone))
                    {
                        clone = null;
                        if (References(original, source))
                        {
                            usedInScope = true;
                            if (recolored == null && !built.TryGetValue(recipe, out recolored))
                            {
                                recolored = host.BuildTexture(recipe, out var failure);
                                if (recolored == null)
                                {
                                    host.Report(Problem.BuildFailed, component, failure);
                                    return replaced;
                                }
                                host.SaveAsset(recolored);
                                built[recipe] = recolored;
                            }
                            clone = Object.Instantiate(original);
                            clone.name = original.name;
                            ReplaceTexture(clone, source, recolored);
                            host.SaveAsset(clone);
                            host.RegisterReplaced(original, clone);
                            replaced++;
                        }
                        clones[original] = clone;
                    }
                    if (clone != null)
                    {
                        mats[i] = clone;
                        changed = true;
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }

            if (!usedInScope)
                host.Report(Problem.TextureNotUsedInScope, component, RecipeTextureBuilder.Failure.None);
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
