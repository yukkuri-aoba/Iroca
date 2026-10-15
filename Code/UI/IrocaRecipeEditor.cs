// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>レシピ(<see cref="IrocaRecipe"/>)のインスペクタ。中身の要約と「いろかで開く」。</summary>
    [CustomEditor(typeof(IrocaRecipe))]
    internal sealed class IrocaRecipeEditor : Editor
    {
        private readonly RecipeSummaryCache _summary = new RecipeSummaryCache();

        public override void OnInspectorGUI()
        {
            var recipe = (IrocaRecipe)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(IrocaRecipe.sourceTexture)),
                new GUIContent(Localization.RecipeSourceField, Localization.RecipeSourceFieldTooltip));
            serializedObject.ApplyModifiedProperties();

            var state = _summary.Get(recipe);
            if (recipe.sourceTexture == null)
                EditorGUILayout.HelpBox(Localization.RecolorNoSource, MessageType.Warning);
            if (state == null)
            {
                EditorGUILayout.HelpBox(Localization.RecolorUnreadable, MessageType.Warning);
            }
            else
            {
                int enabled = SessionRecolor.CountEnabled(state.zones);
                EditorGUILayout.LabelField(string.Format(Localization.RecipeInspectorZonesFormat,
                    state.zones.Count, enabled), EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(recipe.sourceTexture == null))
            {
                if (GUILayout.Button(new GUIContent(Localization.OpenInIroca, Localization.OpenInIrocaTooltip)))
                    IrocaWindow.OpenRecipe(recipe);
            }
        }
    }
}
