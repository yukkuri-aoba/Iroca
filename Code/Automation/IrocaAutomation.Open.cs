// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    public static partial class IrocaAutomation
    {
        [Serializable]
        internal class OpenResult
        {
            public bool ok;
            public string error = "";
            public string texture = "";
            public string recipe = "";
            public string note = "";
        }

        /// <summary>
        /// いろかの窓で開く(人が手直しする入口)。<paramref name="target"/> はレシピ・テクスチャ・マテリアルのアセットパス、
        /// またはレシピの付いたシーンのオブジェクト(付いているレシピが 1 つのとき)。
        /// </summary>
        public static string Open(string target)
        {
            var r = new OpenResult();
            try
            {
                if (!ResolveOpenTarget(target, out var tex, out var recipe, out string error))
                {
                    r.error = error;
                    return JsonUtility.ToJson(r);
                }
                if (recipe != null) IrocaWindow.OpenRecipe(recipe);
                else IrocaWindow.OpenTexture(tex);
                r.ok = true;
                r.texture = AssetDatabase.GetAssetPath(tex);
                r.recipe = recipe != null ? AssetDatabase.GetAssetPath(recipe) : "";
                r.note = "The Iroca window shows the texture. Changes made there are saved to the recipe automatically.";
            }
            catch (Exception ex)
            {
                r.ok = false;
                r.error = $"{ex.GetType().Name}: {ex.Message}";
            }
            return JsonUtility.ToJson(r);
        }

        /// <summary><see cref="Open"/> が開くもの。開けなければ false と理由。</summary>
        internal static bool ResolveOpenTarget(string target, out Texture2D tex, out IrocaRecipe recipe, out string error)
        {
            tex = null; recipe = null; error = "";
            if (string.IsNullOrWhiteSpace(target)) { error = "target is required."; return false; }

            var asset = LoadAssetByPath(target);
            if (asset is IrocaRecipe rec)
            {
                if (rec.sourceTexture == null) { error = $"the recipe has no source texture: {target}"; return false; }
                recipe = rec;
                tex = rec.sourceTexture;
                return true;
            }
            if (asset is Material mat) asset = MeshUvLocator.MainTexture(mat);
            if (asset is Texture2D t)
            {
                tex = MeshUvLocator.EditableSourceOf(t) ?? t;
                return true;
            }
            if (asset != null) { error = $"cannot open {asset.GetType().Name}: {target}"; return false; }

            var go = FindSceneObject(target, out string findError);
            if (go == null) { error = findError; return false; }
            var recipes = go.GetComponentsInChildren<IrocaRecolor>(true)
                .Where(c => c.recipes != null).SelectMany(c => c.recipes)
                .Where(x => x != null && x.sourceTexture != null).Distinct().ToList();
            if (recipes.Count == 1) { recipe = recipes[0]; tex = recipe.sourceTexture; return true; }
            error = recipes.Count == 0
                ? $"no Iroca recipe on '{HierarchyPath(go.transform)}'; pass a texture path instead."
                : $"'{HierarchyPath(go.transform)}' has {recipes.Count} recipes; pass one of: "
                  + string.Join(", ", recipes.Select(x => AssetDatabase.GetAssetPath(x)));
            return false;
        }
    }
}
