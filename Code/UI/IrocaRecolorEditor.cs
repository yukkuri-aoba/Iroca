// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 「Iroca Recolor」のインスペクタ。何が起きるか(範囲内の対象マテリアル数)と、
    /// 色替えされない理由(レシピ未設定・元テクスチャ無し・範囲に無い・NDMF 無し)をビルド前に見せる。
    /// </summary>
    [CustomEditor(typeof(IrocaRecolor))]
    internal sealed class IrocaRecolorEditor : Editor
    {
        private readonly RecipeSummaryCache _summary = new RecipeSummaryCache();

        public override void OnInspectorGUI()
        {
            var component = (IrocaRecolor)target;
            EditorGUILayout.HelpBox(Localization.RecolorInspectorHelp, MessageType.None);
#if !IROCA_NDMF_PRESENT
            EditorGUILayout.HelpBox(Localization.NdmfMissing, MessageType.Warning);
#endif
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(IrocaRecolor.recipe)),
                new GUIContent(Localization.RecipeField, Localization.RecipeFieldTooltip));
            serializedObject.ApplyModifiedProperties();

            var recipe = component.recipe;
            if (recipe == null)
            {
                EditorGUILayout.HelpBox(Localization.RecolorNoRecipe, MessageType.Info);
                return;
            }
            var source = recipe.sourceTexture;
            if (source == null)
                EditorGUILayout.HelpBox(Localization.RecolorNoSource, MessageType.Warning);
            else if (!_summary.Readable(recipe))
                EditorGUILayout.HelpBox(Localization.RecolorUnreadable, MessageType.Warning);
            else
                DrawScope(component, source);

            using (new EditorGUI.DisabledScope(source == null))
            {
                if (GUILayout.Button(new GUIContent(Localization.OpenInIroca, Localization.OpenInIrocaTooltip)))
                    IrocaWindow.OpenRecipe(recipe);
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
