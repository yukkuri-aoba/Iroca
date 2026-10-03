// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// シーン上のプレビュー(NDMF)で、どのテクスチャを何に差し替えるかの規則(NDMF 非依存。EditMode テストから叩く)。
    /// <para>
    /// 規則:
    /// <list type="bullet">
    /// <item>テクスチャ T の「持ち主」= Renderer の祖先(自分を含む)のうち、レシピの元テクスチャが T の
    ///   <see cref="IrocaRecolor"/> で最も深いもの。非破壊ビルド(<see cref="NonDestructiveApplier"/>)と同じ。</item>
    /// <item>持ち主がいる: いろかウィンドウが T を編集していて、結び付いたレシピが持ち主のレシピなら、編集中の内容
    ///   (ライブ)を映す。そうでなければ持ち主のレシピの出来上がりを映す。</item>
    /// <item>持ち主がいない: ウィンドウが T を編集していて、シーンに T のレシピを持つコンポーネントが 1 つも
    ///   無ければライブを映す(登録前に試せるように)。T が登録済みなら、範囲外はビルドでも色替えされないので映さない。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class PreviewTargeting
    {
        /// <summary>シーンにある <see cref="IrocaRecolor"/> 1 つ分(レシピと元テクスチャを読み出し済み)。</summary>
        internal readonly struct Registered
        {
            public readonly GameObject owner;
            public readonly IrocaRecipe recipe;
            public readonly Texture2D source;

            public Registered(GameObject owner, IrocaRecipe recipe, Texture2D source)
            {
                this.owner = owner;
                this.recipe = recipe;
                this.source = source;
            }
        }

        /// <summary>差し替え先。<see cref="recipe"/> が null ならライブ(ウィンドウで編集中の内容)。</summary>
        internal readonly struct Replacement
        {
            public readonly Texture2D source;
            public readonly IrocaRecipe recipe;

            public Replacement(Texture2D source, IrocaRecipe recipe)
            {
                this.source = source;
                this.recipe = recipe;
            }

            public bool IsLive => recipe == null;
        }

        internal sealed class Plan
        {
            private readonly Dictionary<GameObject, List<Registered>> _byOwner = new Dictionary<GameObject, List<Registered>>();
            private readonly HashSet<Texture> _registeredSources = new HashSet<Texture>();
            private readonly Texture2D _liveSource;
            private readonly IrocaRecipe _liveRecipe;

            public Plan(IEnumerable<Registered> components, LivePreview.Target live)
            {
                foreach (var c in components)
                {
                    if (c.owner == null || c.recipe == null || c.source == null) continue;
                    if (!_byOwner.TryGetValue(c.owner, out var list))
                        _byOwner[c.owner] = list = new List<Registered>();
                    list.Add(c);
                    _registeredSources.Add(c.source);
                }
                if (live != null && live.source != null)
                {
                    _liveSource = live.source;
                    _liveRecipe = live.boundRecipe;
                }
            }

            /// <summary>何も差し替えない(シーンを調べる必要が無い)。</summary>
            public bool IsEmpty => _liveSource == null && _registeredSources.Count == 0;

            /// <summary>差し替えの候補になるテクスチャか(Renderer ごとの判定の前の早い足切り)。</summary>
            public bool IsCandidate(Texture texture) =>
                texture != null && (texture == _liveSource || _registeredSources.Contains(texture));

            /// <summary>
            /// <paramref name="renderer"/> が使う <paramref name="texture"/> の差し替え先。差し替えないなら false。
            /// </summary>
            public bool TryResolve(Transform renderer, Texture texture, out Replacement replacement)
            {
                replacement = default;
                if (renderer == null || !IsCandidate(texture)) return false;

                for (var t = renderer; t != null; t = t.parent)
                {
                    if (!_byOwner.TryGetValue(t.gameObject, out var list)) continue;
                    foreach (var c in list)
                    {
                        if (c.source != texture) continue;
                        bool live = texture == _liveSource && _liveRecipe != null && c.recipe == _liveRecipe;
                        replacement = new Replacement(c.source, live ? null : c.recipe);
                        return true;
                    }
                }

                if (texture == _liveSource && !_registeredSources.Contains(texture))
                {
                    replacement = new Replacement(_liveSource, null);
                    return true;
                }
                return false;
            }
        }
    }
}
