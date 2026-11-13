// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 「アバターに非破壊で登録」の判断(0.4.0 の計画の 4-3)。登録できるか・登録先・どのレシピを使うか
    /// (<see cref="Prepare"/>)と、登録で何が起きるか(「計画」<see cref="Plan"/>)をまとめ、ウィンドウは確認画面に、
    /// 自動化(MCP)は警告とエラーにする。同じ判断を 2 か所に書かない。
    /// 登録先の候補選び・コンポーネントの付与・マテリアルの戻し方そのものは <see cref="RecipeRegistration"/>(Infra)。
    /// </summary>
    internal static class RegistrationService {
        /// <summary>登録を始められない理由(<see cref="Prepare"/>)。文は呼ぶ側が持つ(ウィンドウは確認画面、自動化は返すエラー)。</summary>
        internal enum Refusal {
            None,
            /// <summary>再生中。付けたコンポーネントは再生を止めると消え、書き出した画像から元へ戻したマテリアル(アセット)だけが残る。</summary>
            Playing,
            /// <summary>有効なゾーンが無い(登録するものが無い)。</summary>
            NoZones,
            /// <summary>指した登録先がシーンの中に無い(Prefab のアセットなど)。</summary>
            NotInScene,
            /// <summary>指した登録先がアバターの外。そこに付けると、シーンのプレビューでは色が変わるのに、アップロードでは変わらない。</summary>
            OutsideAvatar,
            /// <summary>登録先を指していなくて、テクスチャの使われ方からも決まらない(使っていない・複数のアバター・アバターの外だけ)。</summary>
            Undecided,
            /// <summary>使うレシピの中身を読めない(新しい版のいろかで作られた・壊れた)。上書きすると戻せない。</summary>
            RecipeUnreadable,
            /// <summary>結び付いているレシピが外(git など)で書き換わっている。窓の古い内容で上書きせず、読み直してからにする。</summary>
            RecipeChangedOutside,
        }

        /// <summary>登録の下ごしらえ(<see cref="Prepare"/>)の結果。</summary>
        internal sealed class Preparation {
            public Refusal refusal;
            /// <summary>登録先。決まる前に断ったときは null。</summary>
            public GameObject target;
            /// <summary>使うレシピ(null = 新しく作る)。レシピが理由で断ったときは、そのレシピ。</summary>
            public IrocaRecipe recipe;
            /// <summary>ほかのアバターが使っているので流用しなかったレシピ(<see cref="ChooseRecipe"/>。無ければ null)。</summary>
            public IrocaRecipe notShared;
            /// <summary><see cref="notShared"/> を使っている所。</summary>
            public List<IrocaRecolor> notSharedUsers;
        }

        /// <summary>
        /// 登録の下ごしらえ: 登録できるかを決まった順に確かめ、登録先と使うレシピを決める。ウィンドウの「アバターに非破壊で登録」と
        /// 自動化(apply='avatar')が、同じ順・同じ判断を使う。断るときは理由を返し、文は呼ぶ側が出す。
        /// 順: 再生中 → 有効なゾーンが無い → 指した登録先がシーンに無い → アバターの外 → 登録先が決まらない → レシピを読めない →
        /// 結び付いているレシピが外で書き換わっている。
        /// </summary>
        /// <param name="selected">利用者が指した登録先(無ければ null。テクスチャを使っている Renderer の共通の親にする)。</param>
        /// <param name="bound">ウィンドウが結び付いているレシピ(無ければ null)。</param>
        /// <param name="shareWithOtherAvatars"><see cref="ChooseRecipe"/> と同じ(確認を取れない自動化は false)。</param>
        internal static Preparation Prepare(Texture2D source, GameObject selected, IrocaRecipe bound, bool hasEnabledZones,
            bool playing, bool shareWithOtherAvatars) {
            var p = new Preparation();
            if (playing) {
                p.refusal = Refusal.Playing;
                return p;
            }
            if (!hasEnabledZones) {
                p.refusal = Refusal.NoZones;
                return p;
            }
            if (selected != null && !RecipeRegistration.IsSceneObject(selected)) {
                p.refusal = Refusal.NotInScene;
                return p;
            }
            if (selected != null && !RecipeRegistration.IsInsideAvatar(selected.transform)) {
                p.refusal = Refusal.OutsideAvatar;
                return p;
            }
            p.target = RecipeRegistration.SuggestTarget(source, selected);
            if (p.target == null) {
                p.refusal = Refusal.Undecided;
                return p;
            }
            p.recipe = ChooseRecipe(p.target, source, bound, shareWithOtherAvatars, out p.notShared, out p.notSharedUsers);
            if (IsUnreadable(p.recipe)) {
                p.refusal = Refusal.RecipeUnreadable;
            } else if (p.recipe != null && p.recipe == bound && SessionPersistence.RecipeChangedOutside(p.recipe)) {
                p.refusal = Refusal.RecipeChangedOutside;
            }
            return p;
        }

        /// <summary>
        /// 使うレシピ: <paramref name="preferred"/>(ウィンドウが結び付いているもの) → 登録先に既にある
        /// <paramref name="source"/> のレシピ(登録し直し = 更新) → このテクスチャのレシピがプロジェクトにちょうど 1 つならそれ。
        /// どれも無ければ null(新しく作る)。
        /// <paramref name="shareWithOtherAvatars"/> が false なら、最後の候補をほかのアバターが使っているときは流用しない
        /// (流用して書き換えると、そのアバターの色も一緒に変わる)。そのときは <paramref name="notShared"/> にそのレシピ、
        /// <paramref name="notSharedUsers"/> に使っている所が入る。
        /// </summary>
        internal static IrocaRecipe ChooseRecipe(GameObject target, Texture2D source, IrocaRecipe preferred,
            bool shareWithOtherAvatars, out IrocaRecipe notShared, out List<IrocaRecolor> notSharedUsers) {
            notShared = null;
            notSharedUsers = null;
            if (preferred != null) {
                return preferred;
            }
            var recipe = RecipeRegistration.ExistingFor(target, source);
            if (recipe != null) {
                return recipe;
            }
            var found = RecipeStore.FindForTexture(source);
            if (found.Count != 1) {
                return null;
            }
            if (!shareWithOtherAvatars) {
                var users = OtherAvatarsUsing(found[0], target);
                if (users.Count > 0) {
                    notShared = found[0];
                    notSharedUsers = users;
                    return null;
                }
            }
            return found[0];
        }

        /// <summary>
        /// 開いているシーンで <paramref name="recipe"/> を使っている、<paramref name="target"/> とは別のアバター(ルートが違う)の
        /// <see cref="IrocaRecolor"/>。<paramref name="target"/> が null なら空。
        /// </summary>
        internal static List<IrocaRecolor> OtherAvatarsUsing(IrocaRecipe recipe, GameObject target) {
            var users = new List<IrocaRecolor>();
            if (recipe == null || target == null) {
                return users;
            }
            var root = target.transform.root;
            foreach (var c in RecipeRegistration.OtherUsers(recipe, target)) {
                if (c.transform.root != root) {
                    users.Add(c);
                }
            }
            return users;
        }

        /// <summary>
        /// 中身が読めないレシピ(新しい版のいろかで作られた・壊れた)か。上書きすると戻せないので、土台にも書き込み先にもしない。
        /// </summary>
        internal static bool IsUnreadable(IrocaRecipe recipe)
            => recipe != null && !string.IsNullOrEmpty(recipe.sessionJson) && RecipeStore.Load(recipe) == null;

        /// <summary>登録で何が起きるか(確認画面・警告に出す)。</summary>
        internal sealed class Plan {
            public GameObject target;
            public Texture2D source;
            /// <summary>使うレシピ(null = 新しく作る)。</summary>
            public IrocaRecipe recipe;
            /// <summary>置き換えられる、同じ元テクスチャの別のレシピ(無ければ null)。</summary>
            public IrocaRecipe replaced;
            /// <summary>登録先にいま付いている <see cref="IrocaRecolor"/> の数(2 以上なら 1 つにまとめる)。</summary>
            public int components;
            /// <summary>このテクスチャをいろかで書き出した画像。</summary>
            public HashSet<Texture> exports;
            /// <summary>登録先の中で、書き出した画像を差しているマテリアル(元のテクスチャへ戻す候補)。</summary>
            public List<Material> exportUsers;
            /// <summary>そのうち書き換えられるもの(Assets の .mat かシーンの中のもの)の数。</summary>
            public int switchable;
            /// <summary>そのうち登録先の外(開いているシーン)でも使われているもの。戻すとそちらも元の色に戻る。</summary>
            public List<Material> shared;
            /// <summary><see cref="shared"/> を使っている、登録先の外の Renderer の数。</summary>
            public int sharedPlaces;
            /// <summary>使うレシピを、登録先のほかに使っている所(登録した後の編集がそちらの色にも効く)。</summary>
            public List<IrocaRecolor> otherUsers;
            /// <summary>
            /// 登録先の「Iroca Recolor」にある、消えたレシピ(Missing)の欄の数。ビルドはこの欄で止まる。窓の登録は確認を取って
            /// 外し、自動化は外さずに警告する。
            /// </summary>
            public int lostSlots;
        }

        /// <summary><paramref name="target"/> に <paramref name="recipe"/>(null = 新しく作る)を登録するときの計画。</summary>
        internal static Plan Describe(GameObject target, Texture2D source, IrocaRecipe recipe) {
            var plan = new Plan { target = target, source = source, recipe = recipe };
            RecipeRegistration.Describe(target, source, recipe, out plan.replaced, out plan.components);
            plan.exports = RecipeRegistration.ExportsOf(source);
            plan.exportUsers = RecipeRegistration.MaterialsUsing(target, plan.exports);
            foreach (var m in plan.exportUsers) {
                if (RecipeRegistration.IsEditableMaterial(m)) {
                    plan.switchable++;
                }
            }
            plan.shared = RecipeRegistration.UsedOutside(target, plan.exportUsers, out plan.sharedPlaces);
            plan.otherUsers = RecipeRegistration.OtherUsers(recipe, target);
            plan.lostSlots = RecipeRegistration.LostSlots(target);
            return plan;
        }

        /// <summary>
        /// 書き出した画像を差しているマテリアルを元のテクスチャへ戻し、<paramref name="recipe"/> を登録先のコンポーネントへ足す
        /// (呼ぶ側の Undo グループに入る)。<paramref name="keepShared"/> なら、登録先の外でも使われているマテリアルは戻さない。
        /// <paramref name="replaceLost"/> なら、消えたレシピの欄を外す(<see cref="RecipeRegistration.Attach"/>)。
        /// 戻したマテリアルの数を返す。
        /// </summary>
        internal static int SwitchAndAttach(Plan plan, IrocaRecipe recipe, bool keepShared, out IrocaRecolor component,
            bool replaceLost = false) {
            var switchList = new List<Material>(plan.exportUsers);
            if (keepShared) {
                switchList.RemoveAll(plan.shared.Contains);
            }
            int switched = RecipeRegistration.SwitchToSource(switchList, plan.source, plan.exports);
            component = RecipeRegistration.Attach(plan.target, recipe, replaceLost);
            return switched;
        }
    }
}
