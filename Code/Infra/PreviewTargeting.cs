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

            /// <summary>
            /// シーンのどこにでも映しうるか(ウィンドウが登録の無いテクスチャを編集している)。false なら、映しうるのは
            /// <see cref="Owners"/>(とその子)の Renderer だけ(持ち主のいない Renderer には何も映さない規則なので)。
            /// </summary>
            public bool ShowsLiveAnywhere => _liveSource != null && !_registeredSources.Contains(_liveSource);

            /// <summary>コンポーネントを付けた GameObject(範囲の根)。</summary>
            public IEnumerable<GameObject> Owners => _byOwner.Keys;

            /// <summary>
            /// <paramref name="renderer"/> の位置での差し替え先(<see cref="TryResolve"/> の結果をまとめたもの)。
            /// 登録済みのテクスチャは、Renderer が使っているかに関係なく、持ち主(祖先のコンポーネント)で決まる分を
            /// すべて入れる(上流のツールが別のマテリアルへ差し替えても引けるように)。ウィンドウで編集中のテクスチャを
            /// ライブで映すのは、<paramref name="usesLive"/>(Renderer の素材がそれを使っている)ときだけ。使っていない
            /// Renderer ではレシピのままにして、ウィンドウで別のテクスチャを開くたびに差し替え先が変わらないようにする。
            /// </summary>
            public Routes RoutesFor(Transform renderer, bool usesLive)
            {
                var found = new List<(Texture, Replacement)>();
                foreach (var texture in _registeredSources)
                {
                    if (!TryResolve(renderer, texture, out var replacement)) continue;
                    if (replacement.IsLive && !usesLive) replacement = new Replacement(replacement.source, _liveRecipe);
                    found.Add((texture, replacement));
                }
                if (usesLive && ShowsLiveAnywhere && TryResolve(renderer, _liveSource, out var live))
                    found.Add((_liveSource, live));
                return new Routes(found);
            }

            /// <summary>差し替えの候補になるテクスチャか(Renderer ごとの判定の前の早い足切り)。</summary>
            public bool IsCandidate(Texture texture) =>
                texture != null && (texture == _liveSource || _registeredSources.Contains(texture));

            /// <summary>ウィンドウで編集中のテクスチャか。</summary>
            public bool IsLive(Texture texture) => texture != null && texture == _liveSource;

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

        /// <summary>
        /// ある Renderer の位置での差し替え先の一覧(元テクスチャ → <see cref="Replacement"/>)。シーンのプレビューは
        /// これを NDMF の対象グループに持たせ、中身が変わったときだけノードを作り直させる(ノード自身はシーン全体の
        /// 部品を見張らない)。中身で比べる(元テクスチャ・レシピが同じなら等しい)。
        /// </summary>
        internal sealed class Routes : System.IEquatable<Routes>
        {
            private readonly Texture[] _textures;
            private readonly Replacement[] _replacements;

            public Routes(List<(Texture texture, Replacement replacement)> routes)
            {
                // 見つけた順はハッシュの並びなので、比べられるように並べ替える。
                routes.Sort((a, b) => a.texture.GetInstanceID().CompareTo(b.texture.GetInstanceID()));
                _textures = new Texture[routes.Count];
                _replacements = new Replacement[routes.Count];
                for (int i = 0; i < routes.Count; i++)
                {
                    _textures[i] = routes[i].texture;
                    _replacements[i] = routes[i].replacement;
                }
            }

            public bool IsEmpty => _textures.Length == 0;

            public bool TryGet(Texture texture, out Replacement replacement)
            {
                if (texture != null)
                {
                    for (int i = 0; i < _textures.Length; i++)
                    {
                        if (_textures[i] != texture) continue;
                        replacement = _replacements[i];
                        return true;
                    }
                }
                replacement = default;
                return false;
            }

            public bool Equals(Routes other)
            {
                if (ReferenceEquals(this, other)) return true;
                if (other == null || other._textures.Length != _textures.Length) return false;
                for (int i = 0; i < _textures.Length; i++)
                {
                    if (!ReferenceEquals(_textures[i], other._textures[i])
                        || !ReferenceEquals(_replacements[i].source, other._replacements[i].source)
                        || !ReferenceEquals(_replacements[i].recipe, other._replacements[i].recipe))
                        return false;
                }
                return true;
            }

            public override bool Equals(object obj) => Equals(obj as Routes);

            // 消えたオブジェクトでも値が変わらないよう、instance ID で(Unity の == は消えると null 扱いになる)。
            public override int GetHashCode()
            {
                int hash = _textures.Length;
                for (int i = 0; i < _textures.Length; i++)
                {
                    hash = hash * 31 + IdOf(_textures[i]);
                    hash = hash * 31 + IdOf(_replacements[i].recipe);
                }
                return hash;
            }

            private static int IdOf(Object o) => ReferenceEquals(o, null) ? 0 : o.GetInstanceID();
        }
    }
}
