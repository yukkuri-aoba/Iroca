// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 「アバターに非破壊で登録」の登録先選びと、コンポーネントの付与。ウィンドウ(UI)から切り離してある。
    /// </summary>
    internal static class RecipeRegistration
    {
        /// <summary>
        /// 登録先の候補。シーン上の GameObject が選ばれていればそれ。無ければ、開いているシーンで
        /// <paramref name="texture"/>(か、それをいろかで書き出した画像)を使っている Renderer の共通の親
        /// (複数のアバター = ルートが複数にまたがるときは決めない)。決まらなければ null。
        /// </summary>
        public static GameObject SuggestTarget(Texture2D texture, GameObject selected)
        {
            if (selected != null) return IsSceneObject(selected) ? selected : null;
            if (texture == null) return null;

            var exports = ExportsOf(texture);
            var users = new List<Transform>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsSceneObject(r.gameObject)) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null && (NonDestructiveApplier.References(m, texture) || UsesAny(m, exports)))
                    {
                        users.Add(r.transform);
                        break;
                    }
                }
            }
            if (users.Count == 0) return null;
            var root = users[0].root;
            foreach (var t in users)
                if (t.root != root) return null;
            return CommonAncestor(users).gameObject;
        }

        /// <summary><paramref name="target"/> に既に登録されている、<paramref name="source"/> のレシピ(無ければ null)。</summary>
        public static IrocaRecipe ExistingFor(GameObject target, Texture2D source)
        {
            if (target == null || source == null) return null;
            foreach (var r in Recipes(target.GetComponents<IrocaRecolor>()))
                if (r != null && r.sourceTexture == source) return r;
            return null;
        }

        /// <summary>
        /// 登録で <paramref name="target"/> に何が起きるか(確認画面に出す)。
        /// <paramref name="replaced"/> = 置き換えられる、同じ元テクスチャの別のレシピ(無ければ null)。
        /// <paramref name="components"/> = いま付いている <see cref="IrocaRecolor"/> の数(2 以上なら 1 つにまとめる)。
        /// </summary>
        public static void Describe(GameObject target, Texture2D source, IrocaRecipe recipe,
            out IrocaRecipe replaced, out int components)
        {
            replaced = null;
            var existing = target.GetComponents<IrocaRecolor>();
            components = existing.Length;
            foreach (var r in Recipes(existing))
            {
                if (recipe != null && r == recipe) { replaced = null; return; }   // 登録済み(何も置き換えない)
                if (replaced == null && r != null && source != null && r.sourceTexture == source) replaced = r;
            }
        }

        /// <summary>
        /// <paramref name="target"/> の <see cref="IrocaRecolor"/> に <paramref name="recipe"/> を足す(Undo 可。
        /// 1 回の Undo で戻る)。コンポーネントは 1 つにまとめる: 無ければ付け、旧版で複数付いていれば
        /// 先頭へまとめる。同じ元テクスチャの別のレシピがあればその場所で置き換える(同じテクスチャを
        /// 1 つのコンポーネントで 2 回色替えしても、先のものしか効かないため)。
        /// </summary>
        public static IrocaRecolor Attach(GameObject target, IrocaRecipe recipe)
        {
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Iroca: Register Recolor");
            var c = MergeComponents(target);
            if (c == null) c = Undo.AddComponent<IrocaRecolor>(target);
            Undo.RecordObject(c, "Iroca: Register Recolor");
            c.recipes ??= new List<IrocaRecipe>();
            if (!c.recipes.Contains(recipe))
            {
                var source = recipe != null ? recipe.sourceTexture : null;
                int same = c.recipes.FindIndex(r => r != null && source != null && r.sourceTexture == source);
                if (same >= 0) c.recipes[same] = recipe;
                else c.recipes.Add(recipe);
            }
            Undo.CollapseUndoOperations(group);
            EditorUtility.SetDirty(c);
            return c;
        }

        /// <summary>
        /// <paramref name="target"/> に付いている <see cref="IrocaRecolor"/> を先頭の 1 つへまとめる(Undo 可)。
        /// レシピは付いていた順に並べ、同じ元テクスチャのレシピは先のものだけ残す(ビルドでも先のものしか
        /// 効かないので、結果は変わらない)。空の欄は落とす。1 つも無ければ null。
        /// </summary>
        public static IrocaRecolor MergeComponents(GameObject target)
        {
            var components = target.GetComponents<IrocaRecolor>();
            if (components.Length == 0) return null;
            var keep = components[0];
            if (components.Length == 1) return keep;

            Undo.RecordObject(keep, "Iroca: Merge Recolor");
            var merged = new List<IrocaRecipe>();
            var sources = new HashSet<Texture2D>();
            foreach (var r in Recipes(components))
            {
                if (r == null || merged.Contains(r)) continue;
                var source = r.sourceTexture;
                if (source != null && !sources.Add(source)) continue;
                merged.Add(r);
            }
            keep.recipes = merged;
            for (int i = 1; i < components.Length; i++)
                Undo.DestroyObjectImmediate(components[i]);
            EditorUtility.SetDirty(keep);
            return keep;
        }

        private static IEnumerable<IrocaRecipe> Recipes(IrocaRecolor[] components)
        {
            foreach (var c in components)
                if (c.recipes != null)
                    foreach (var r in c.recipes) yield return r;
        }

        // ───────────── 書き出し済みの画像から非破壊へ移る ─────────────
        // 「適用して保存」で書き出した画像(元の名前_recolored*.png)をマテリアルに差していると、非破壊の色替えは
        // 元のテクスチャを探すので効かない。登録のときに、範囲内のマテリアルを元のテクスチャへ戻す。

        /// <summary><paramref name="source"/> をいろかで書き出した画像(同じフォルダの「名前_recolored*」)。</summary>
        internal static HashSet<Texture> ExportsOf(Texture2D source)
        {
            if (source == null) return new HashSet<Texture>();
            var set = MeshUvLocator.TargetTextures(source);
            set.Remove(source);
            return set;
        }

        /// <summary><paramref name="scope"/> とその子のマテリアルのうち、<paramref name="exports"/> のどれかを使っているもの。</summary>
        internal static List<Material> MaterialsUsing(GameObject scope, HashSet<Texture> exports)
        {
            var found = new List<Material>();
            if (scope == null || exports == null || exports.Count == 0) return found;
            foreach (var r in scope.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && !found.Contains(m) && UsesAny(m, exports)) found.Add(m);
            return found;
        }

        /// <summary>開いているシーン全体で、<paramref name="exports"/> のどれかを使っているマテリアルの数。</summary>
        internal static int CountSceneMaterialsUsing(HashSet<Texture> exports)
        {
            if (exports == null || exports.Count == 0) return 0;
            var found = new HashSet<Material>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsSceneObject(r.gameObject)) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && UsesAny(m, exports)) found.Add(m);
            }
            return found.Count;
        }

        /// <summary>
        /// 書き換えてよいマテリアルか。Assets 配下の .mat かシーンの中のもの。FBX の中のマテリアル・
        /// パッケージのものは書き換えても保存されない(か、書き換えるべきでない)。
        /// </summary>
        internal static bool IsEditableMaterial(Material m)
        {
            if (m == null) return false;
            string path = AssetDatabase.GetAssetPath(m);
            if (string.IsNullOrEmpty(path)) return true;
            return path.StartsWith("Assets/", System.StringComparison.Ordinal)
                   && path.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 書き出した画像の参照を元のテクスチャへ戻す(Undo 可。呼び出し側の Undo グループに入る)。
        /// 書き換えられないマテリアルは飛ばす。戻したマテリアルの数を返す。
        /// </summary>
        internal static int SwitchToSource(IEnumerable<Material> materials, Texture2D source, HashSet<Texture> exports)
        {
            int switched = 0;
            foreach (var m in materials)
            {
                if (!IsEditableMaterial(m)) continue;
                Undo.RecordObject(m, "Iroca: Switch To Source Texture");
                foreach (int id in m.GetTexturePropertyNameIDs())
                    if (exports.Contains(m.GetTexture(id))) m.SetTexture(id, source);
                EditorUtility.SetDirty(m);
                switched++;
            }
            return switched;
        }

        private static bool UsesAny(Material m, HashSet<Texture> textures)
        {
            if (textures.Count == 0) return false;
            foreach (int id in m.GetTexturePropertyNameIDs())
            {
                var t = m.GetTexture(id);
                if (t != null && textures.Contains(t)) return true;
            }
            return false;
        }

        internal static bool IsSceneObject(GameObject go)
            => go != null && !EditorUtility.IsPersistent(go) && go.scene.IsValid();

        private static Transform CommonAncestor(List<Transform> nodes)
        {
            var path = new List<Transform>();
            for (var t = nodes[0]; t != null; t = t.parent) path.Add(t);
            path.Reverse();   // ルート → 葉
            int depth = path.Count;
            foreach (var n in nodes)
            {
                var chain = new List<Transform>();
                for (var t = n; t != null; t = t.parent) chain.Add(t);
                chain.Reverse();
                int k = 0;
                while (k < depth && k < chain.Count && chain[k] == path[k]) k++;
                depth = k;
            }
            return path[Mathf.Max(0, depth - 1)];
        }
    }
}
