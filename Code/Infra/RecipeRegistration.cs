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
        /// <paramref name="texture"/> を使っている Renderer の共通の親(複数のアバター = ルートが
        /// 複数にまたがるときは決めない)。決まらなければ null。
        /// </summary>
        public static GameObject SuggestTarget(Texture2D texture, GameObject selected)
        {
            if (selected != null) return IsSceneObject(selected) ? selected : null;
            if (texture == null) return null;

            var users = new List<Transform>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsSceneObject(r.gameObject)) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null && NonDestructiveApplier.References(m, texture))
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

        /// <summary>
        /// <paramref name="target"/> に <see cref="IrocaRecolor"/> を付けて <paramref name="recipe"/> を設定する
        /// (Undo 可)。同じレシピのコンポーネントが既にあればそれを返す。
        /// </summary>
        public static IrocaRecolor Attach(GameObject target, IrocaRecipe recipe)
        {
            foreach (var existing in target.GetComponents<IrocaRecolor>())
                if (existing.recipe == recipe) return existing;
            int group = Undo.GetCurrentGroup();
            var c = Undo.AddComponent<IrocaRecolor>(target);
            Undo.RecordObject(c, "Iroca: Register Recolor");
            c.recipe = recipe;
            Undo.CollapseUndoOperations(group);   // 付与とレシピ設定を 1 回の Undo にまとめる
            EditorUtility.SetDirty(c);
            return c;
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
