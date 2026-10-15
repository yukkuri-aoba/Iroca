// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 「Iroca Recolor」のインスペクタ。レシピ(テクスチャごとに 1 つ)の一覧と、レシピごとに何が起きるか
    /// (範囲内の対象マテリアル数)・色替えされない理由(元テクスチャ無し・同じテクスチャの重複・範囲に無い・
    /// NDMF 無し)をビルド前に見せる。旧版で同じオブジェクトに複数付いていれば 1 つにまとめる導線を出す。
    /// </summary>
    [CustomEditor(typeof(IrocaRecolor))]
    internal sealed class IrocaRecolorEditor : Editor
    {
        // レシピの JSON の読み直しはレシピごとに覚える(並べ替えても取り違えないよう、レシピで引く)。
        private readonly Dictionary<IrocaRecipe, RecipeSummaryCache> _summaries =
            new Dictionary<IrocaRecipe, RecipeSummaryCache>();

        public override void OnInspectorGUI()
        {
            var component = (IrocaRecolor)target;
            EditorGUILayout.HelpBox(Localization.RecolorInspectorHelp, MessageType.None);
#if !IROCA_NDMF_PRESENT
            EditorGUILayout.HelpBox(Localization.NdmfMissing, MessageType.Warning);
#endif
            DrawMerge(component);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(IrocaRecolor.recipes)),
                new GUIContent(Localization.RecipeField, Localization.RecipeFieldTooltip), true);
            serializedObject.ApplyModifiedProperties();

            var recipes = component.recipes;
            if (recipes == null || recipes.Count == 0)
            {
                EditorGUILayout.HelpBox(Localization.RecolorNoRecipe, MessageType.Info);
                return;
            }
            var seen = new HashSet<Texture2D>();
            bool empty = false;
            foreach (var recipe in recipes)
            {
                if (recipe == null) { empty = true; continue; }
                DrawRecipe(component, recipe, seen);
            }
            if (empty) EditorGUILayout.HelpBox(Localization.RecolorEmptyEntry, MessageType.Warning);
        }

        private void DrawRecipe(IrocaRecolor component, IrocaRecipe recipe, HashSet<Texture2D> seen)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(recipe.name, EditorStyles.boldLabel);
            var source = recipe.sourceTexture;
            if (source == null)
                EditorGUILayout.HelpBox(Localization.RecolorNoSource, MessageType.Warning);
            else if (!seen.Add(source))
                // 同じテクスチャは先のレシピしか効かない(ビルドと同じ)。
                EditorGUILayout.HelpBox(string.Format(Localization.RecolorDuplicateSourceFormat, source.name),
                    MessageType.Warning);
            else if (!Summary(recipe).Readable(recipe))
                EditorGUILayout.HelpBox(Localization.RecolorUnreadable, MessageType.Warning);
            else
                DrawScope(component, source);

            using (new EditorGUI.DisabledScope(source == null))
            {
                if (GUILayout.Button(new GUIContent(Localization.OpenInIroca, Localization.OpenInIrocaTooltip)))
                    IrocaWindow.OpenRecipe(recipe);
            }
        }

        private RecipeSummaryCache Summary(IrocaRecipe recipe)
        {
            if (!_summaries.TryGetValue(recipe, out var cache))
                _summaries[recipe] = cache = new RecipeSummaryCache();
            return cache;
        }

        // 旧版(1 コンポーネント = 1 レシピ)で同じオブジェクトに複数付いているとき、1 つにまとめる導線。
        private static void DrawMerge(IrocaRecolor component)
        {
            var all = component.GetComponents<IrocaRecolor>();
            if (all.Length < 2) return;
            EditorGUILayout.HelpBox(string.Format(Localization.RecolorMergeFormat, all.Length), MessageType.Info);
            if (GUILayout.Button(new GUIContent(Localization.RecolorMergeButton, Localization.RecolorMergeButtonTooltip)))
            {
                RecipeRegistration.MergeComponents(component.gameObject);
                // 表示中のコンポーネントが消えることがあるので、この回の描画はここで打ち切る。
                GUIUtility.ExitGUI();
            }
        }

        private static void DrawScope(IrocaRecolor component, Texture2D source)
        {
            var materials = new HashSet<Material>();
            int renderers = 0;
            foreach (var r in component.GetComponentsInChildren<Renderer>(true))
            {
                bool any = false;
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null && NonDestructiveApplier.References(m, source))
                    {
                        materials.Add(m);
                        any = true;
                    }
                }
                if (any) renderers++;
            }
            if (materials.Count == 0)
                EditorGUILayout.HelpBox(string.Format(Localization.RecolorNotUsedFormat, source.name), MessageType.Warning);
            else
                EditorGUILayout.LabelField(string.Format(Localization.RecolorScopeFormat, materials.Count, renderers),
                    EditorStyles.miniLabel);
        }
    }

    /// <summary>
    /// インスペクタは再描画のたびに呼ばれるので、レシピの JSON(マスク込みで数百 KB になり得る)は
    /// 中身が変わったときだけ読み直す。
    /// </summary>
    internal sealed class RecipeSummaryCache
    {
        private string _json;
        private int _version;
        private IrocaSessionState _state;

        public IrocaSessionState Get(IrocaRecipe recipe)
        {
            if (!ReferenceEquals(_json, recipe.sessionJson) || _version != recipe.formatVersion)
            {
                _json = recipe.sessionJson;
                _version = recipe.formatVersion;
                _state = RecipeStore.Load(recipe);
            }
            return _state;
        }

        public bool Readable(IrocaRecipe recipe) => Get(recipe) != null;
    }
}
