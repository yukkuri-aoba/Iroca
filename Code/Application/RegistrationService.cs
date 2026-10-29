// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 「アバターに非破壊で登録」の判断(0.4.0 の計画の 4-3)。どのレシピを使うか・登録で何が起きるかを
    /// 「計画」(<see cref="Plan"/>)にまとめ、ウィンドウは確認画面に、自動化(MCP)は警告にする。同じ判断を 2 か所に書かない。
    /// 登録先の候補選び・コンポーネントの付与・マテリアルの戻し方そのものは <see cref="RecipeRegistration"/>(Infra)。
    /// </summary>
    internal static class RegistrationService
    {
        /// <summary>
        /// 使うレシピ: <paramref name="preferred"/>(ウィンドウが結び付いているもの) → 登録先に既にある
        /// <paramref name="source"/> のレシピ(登録し直し = 更新) → このテクスチャのレシピがプロジェクトにちょうど 1 つならそれ。
        /// どれも無ければ null(新しく作る)。
        /// <paramref name="shareWithOtherAvatars"/> が false なら、最後の候補をほかのアバターが使っているときは流用しない
        /// (流用して書き換えると、そのアバターの色も一緒に変わる)。そのときは <paramref name="notShared"/> にそのレシピ、
        /// <paramref name="notSharedUsers"/> に使っている所が入る。
        /// </summary>
        internal static IrocaRecipe ChooseRecipe(GameObject target, Texture2D source, IrocaRecipe preferred,
            bool shareWithOtherAvatars, out IrocaRecipe notShared, out List<IrocaRecolor> notSharedUsers)
        {
            notShared = null;
            notSharedUsers = null;
            if (preferred != null) return preferred;
            var recipe = RecipeRegistration.ExistingFor(target, source);
            if (recipe != null) return recipe;
            var found = RecipeStore.FindForTexture(source);
            if (found.Count != 1) return null;
            if (!shareWithOtherAvatars)
            {
                var users = OtherAvatarsUsing(found[0], target);
                if (users.Count > 0)
                {
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
        internal static List<IrocaRecolor> OtherAvatarsUsing(IrocaRecipe recipe, GameObject target)
        {
            var users = new List<IrocaRecolor>();
            if (recipe == null || target == null) return users;
            var root = target.transform.root;
            foreach (var c in RecipeRegistration.OtherUsers(recipe, target))
                if (c.transform.root != root) users.Add(c);
            return users;
        }

        /// <summary>
        /// 中身が読めないレシピ(新しい版のいろかで作られた・壊れた)か。上書きすると戻せないので、土台にも書き込み先にもしない。
        /// </summary>
        internal static bool IsUnreadable(IrocaRecipe recipe)
            => recipe != null && !string.IsNullOrEmpty(recipe.sessionJson) && RecipeStore.Load(recipe) == null;

        /// <summary>登録で何が起きるか(確認画面・警告に出す)。</summary>
        internal sealed class Plan
        {
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
        }

        /// <summary><paramref name="target"/> に <paramref name="recipe"/>(null = 新しく作る)を登録するときの計画。</summary>
        internal static Plan Describe(GameObject target, Texture2D source, IrocaRecipe recipe)
        {
            var plan = new Plan { target = target, source = source, recipe = recipe };
            RecipeRegistration.Describe(target, source, recipe, out plan.replaced, out plan.components);
            plan.exports = RecipeRegistration.ExportsOf(source);
            plan.exportUsers = RecipeRegistration.MaterialsUsing(target, plan.exports);
            foreach (var m in plan.exportUsers)
                if (RecipeRegistration.IsEditableMaterial(m)) plan.switchable++;
            plan.shared = RecipeRegistration.UsedOutside(target, plan.exportUsers, out plan.sharedPlaces);
            plan.otherUsers = RecipeRegistration.OtherUsers(recipe, target);
            return plan;
        }

        /// <summary>
        /// 書き出した画像を差しているマテリアルを元のテクスチャへ戻し、<paramref name="recipe"/> を登録先のコンポーネントへ足す
        /// (呼ぶ側の Undo グループに入る)。<paramref name="keepShared"/> なら、登録先の外でも使われているマテリアルは戻さない。
        /// 戻したマテリアルの数を返す。
        /// </summary>
        internal static int SwitchAndAttach(Plan plan, IrocaRecipe recipe, bool keepShared, out IrocaRecolor component)
        {
            var switchList = new List<Material>(plan.exportUsers);
            if (keepShared) switchList.RemoveAll(plan.shared.Contains);
            int switched = RecipeRegistration.SwitchToSource(switchList, plan.source, plan.exports);
            component = RecipeRegistration.Attach(plan.target, recipe);
            return switched;
        }
    }
}
