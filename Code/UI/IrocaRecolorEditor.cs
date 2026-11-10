// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 「Iroca Recolor」のインスペクタ。レシピ(テクスチャごとに 1 つ)の一覧と、レシピごとに何が起きるか
    /// (範囲内の対象マテリアル数)・色替えされない理由(元テクスチャ無し・同じテクスチャの重複・範囲に無い・
    /// ゾーンがすべて無効・NDMF 無し)をビルド前に見せる。レシピが無い・ゾーンがすべて無効はビルドでは報告しない
    /// (色替えを外している状態なので)ので、ここだけで知らせる。旧版で同じオブジェクトに複数付いていれば 1 つに
    /// まとめる導線を出す。
    /// </summary>
    [CustomEditor(typeof(IrocaRecolor))]
    internal sealed class IrocaRecolorEditor : Editor {
        // レシピの JSON の読み直しはレシピごとに覚える(並べ替えても取り違えないよう、レシピで引く)。
        private readonly Dictionary<IrocaRecipe, RecipeSummaryCache> _summaries =
            new Dictionary<IrocaRecipe, RecipeSummaryCache>();

        public override void OnInspectorGUI() {
            var component = (IrocaRecolor)target;
            EditorGUILayout.HelpBox(Localization.RecolorInspectorHelp, MessageType.None);
#if !IROCA_NDMF_PRESENT
            EditorGUILayout.HelpBox(Localization.NdmfUnavailable(NdmfSupport.State), MessageType.Warning);
#endif
            // ビルドはアバターの中の部品だけを処理し、シーンのプレビューもそれに合わせて映さない。プレハブの編集画面
            // (衣装のプレハブなど。あとでアバターの下に置く)とプロジェクトのアセットでは言わない。
            if (RecipeRegistration.IsSceneObject(component.gameObject)
                && UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(component.gameObject) == null
                && !RecipeRegistration.IsInsideAvatar(component.transform)) {
                EditorGUILayout.HelpBox(Localization.RecolorOutsideAvatar, MessageType.Warning);
            }
            DrawMerge(component);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(IrocaRecolor.recipes)),
                TempContent.Of(Localization.RecipeField, Localization.RecipeFieldTooltip), true);
            serializedObject.ApplyModifiedProperties();

            var recipes = component.recipes;
            if (recipes == null || recipes.Count == 0) {
                EditorGUILayout.HelpBox(Localization.RecolorNoRecipe, MessageType.Info);
                return;
            }
            var seen = new HashSet<Texture2D>();
            bool empty = false;
            foreach (var recipe in recipes) {
                if (recipe == null) { empty = true; continue; }
                DrawRecipe(component, recipe, seen);
            }
            if (empty) {
                EditorGUILayout.HelpBox(Localization.RecolorEmptyEntry, MessageType.Warning);
            }
        }

        private void DrawRecipe(IrocaRecolor component, IrocaRecipe recipe, HashSet<Texture2D> seen) {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(recipe.name, EditorStyles.boldLabel);
            RecipeOutputNotice.Draw(recipe);
            var source = recipe.sourceTexture;
            if (source == null) {
                EditorGUILayout.HelpBox(Localization.RecolorNoSource, MessageType.Warning);
            } else if (!seen.Add(source)) {
                // 同じテクスチャは先のレシピしか効かない(ビルドと同じ)。
                EditorGUILayout.HelpBox(string.Format(Localization.RecolorDuplicateSourceFormat, source.name),
                    MessageType.Warning);
            } else if (!Summary(recipe).Readable(recipe)) {
                EditorGUILayout.HelpBox(Localization.RecolorUnreadable, MessageType.Warning);
            } else if (RecolorInput.CountEnabled(Summary(recipe).Get(recipe).zones) == 0) {
                // 色替えを外している状態(ビルドでは NDMF Console に出さないので、ここで知らせる)。
                EditorGUILayout.HelpBox(Localization.RecolorNoEnabledZones, MessageType.Info);
            } else {
                DrawScope(component, source);
                DrawShadowMatch(component, source, Summary(recipe).Get(recipe));
            }

            using (new EditorGUI.DisabledScope(source == null)) {
                if (GUILayout.Button(TempContent.Of(Localization.OpenInIroca, Localization.OpenInIrocaTooltip))) {
                    IrocaWindow.OpenRecipe(recipe);
                }
            }
        }

        // 範囲で元テクスチャをメインに使う、影のあるマテリアルごとの「影の色を合わせる」。設定はこのコンポーネントが持ち、
        // マテリアルそのものは変えない。
        private static void DrawShadowMatch(IrocaRecolor component, Texture2D source, IrocaSessionState state) {
            var materials = new List<Material>();
            foreach (var r in component.GetComponentsInChildren<Renderer>(true)) {
                foreach (var m in r.sharedMaterials) {
                    if (m != null && !materials.Contains(m) && MaterialShadowColors.IsEligible(m)
                        && MaterialShadowColors.MainTextureIs(m, source)) {
                        materials.Add(m);
                    }
                }
            }
            if (materials.Count == 0) {
                return;
            }
            bool hasColors = MaterialShadowColors.TryGetColors(state.zones, out var sample, out var target);

            EditorGUILayout.LabelField(Localization.ShadowMatchHeader, EditorStyles.miniBoldLabel);
            foreach (var m in materials) {
                bool on = component.shadowMatchMaterials != null && component.shadowMatchMaterials.Contains(m);
                EditorGUILayout.BeginHorizontal();
                bool next = EditorGUILayout.ToggleLeft(TempContent.Of(m.name, Localization.ShadowMatchToggleTooltip), on);
                if (on && hasColors && MaterialShadowColors.TryGetPreview(m, sample, target, out var before, out var after)) {
                    var rect = GUILayoutUtility.GetRect(48f, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(false));
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2f, rect.width / 2f, rect.height - 4f), new Color(before.r, before.g, before.b, 1f));
                    EditorGUI.DrawRect(new Rect(rect.x + rect.width / 2f, rect.y + 2f, rect.width / 2f, rect.height - 4f), new Color(after.r, after.g, after.b, 1f));
                }
                EditorGUILayout.EndHorizontal();
                // 影色テクスチャが別に描かれていると、影の色を合わせても影の部分はそのテクスチャの色のまま。
                foreach (var shadowTexture in MaterialShadowColors.SeparateShadowTextures(m, source)) {
                    EditorGUILayout.HelpBox(string.Format(Localization.ShadowColorTextureNoticeFormat, m.name, shadowTexture.name),
                        MessageType.Info);
                }
                if (next == on) {
                    continue;
                }
                Undo.RecordObject(component, Localization.ShadowMatchHeader);
                component.shadowMatchMaterials ??= new List<Material>();
                if (next) {
                    component.shadowMatchMaterials.Add(m);
                } else {
                    component.shadowMatchMaterials.Remove(m);
                }
                EditorUtility.SetDirty(component);
            }
        }

        private RecipeSummaryCache Summary(IrocaRecipe recipe) {
            if (!_summaries.TryGetValue(recipe, out var cache)) {
                _summaries[recipe] = cache = new RecipeSummaryCache();
            }
            return cache;
        }

        // 旧版(1 コンポーネント = 1 レシピ)で同じオブジェクトに複数付いているとき、1 つにまとめる導線。
        private static void DrawMerge(IrocaRecolor component) {
            var all = component.GetComponents<IrocaRecolor>();
            if (all.Length < 2) {
                return;
            }
            EditorGUILayout.HelpBox(string.Format(Localization.RecolorMergeFormat, all.Length), MessageType.Info);
            if (GUILayout.Button(TempContent.Of(Localization.RecolorMergeButton, Localization.RecolorMergeButtonTooltip))) {
                RecipeRegistration.MergeComponents(component.gameObject);
                // 表示中のコンポーネントが消えることがあるので、この回の描画はここで打ち切る。
                GUIUtility.ExitGUI();
            }
        }

        private static void DrawScope(IrocaRecolor component, Texture2D source) {
            var materials = new HashSet<Material>();
            int renderers = 0;
            // 内側(深い)の「Iroca Recolor」が同じテクスチャのレシピを持つ所は、そちらが受け持つ(ビルドでもシーンの
            // プレビューでも深い方が勝ち、内側のゾーンがすべて無効ならそこは元の色のまま)。このコンポーネントの数から除く。
            var inner = new Dictionary<IrocaRecolor, HashSet<Material>>();
            foreach (var r in component.GetComponentsInChildren<Renderer>(true)) {
                var owner = InnerOwner(component, r.transform, source);
                bool any = false;
                foreach (var m in r.sharedMaterials) {
                    if (m == null || !NonDestructiveApplier.References(m, source)) {
                        continue;
                    }
                    if (owner != null) {
                        if (!inner.TryGetValue(owner, out var handled)) {
                            inner[owner] = handled = new HashSet<Material>();
                        }
                        handled.Add(m);
                    } else {
                        materials.Add(m);
                        any = true;
                    }
                }
                if (any) {
                    renderers++;
                }
            }
            if (materials.Count == 0 && inner.Count == 0) {
                EditorGUILayout.HelpBox(string.Format(Localization.RecolorNotUsedFormat, source.name), MessageType.Warning);
            } else {
                EditorGUILayout.LabelField(string.Format(Localization.RecolorScopeFormat, materials.Count, renderers),
                    EditorStyles.miniLabel);
            }
            foreach (var kv in inner) {
                EditorGUILayout.HelpBox(string.Format(Localization.RecolorInnerScopeFormat, kv.Key.name, kv.Value.Count),
                    MessageType.Info);
            }
        }

        // t から component の手前までの間で、source のレシピを持つ最も近い「Iroca Recolor」(無ければ null)。
        private static IrocaRecolor InnerOwner(IrocaRecolor component, Transform t, Texture2D source) {
            for (; t != null && t != component.transform; t = t.parent) {
                foreach (var c in t.GetComponents<IrocaRecolor>()) {
                    if (c == component || c.recipes == null) {
                        continue;
                    }
                    foreach (var recipe in c.recipes) {
                        if (recipe != null && recipe.sourceTexture == source) {
                            return c;
                        }
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 前の版のいろかの仕上がりで確かめたレシピ(<see cref="RecipeOutputVersion"/>)の案内と「確かめた」ボタン。
    /// 「Iroca Recolor」とレシピのインスペクタが使う。
    /// </summary>
    internal static class RecipeOutputNotice {
        public static void Draw(IrocaRecipe recipe) {
            if (!RecipeOutputVersion.IsOutdated(recipe)) {
                return;
            }
            EditorGUILayout.HelpBox(string.Format(Localization.RecipeOutdatedFormat,
                RecipeOutputVersion.ReleasesOf(recipe.outputVersion)), MessageType.Info);
            if (GUILayout.Button(TempContent.Of(Localization.OutputCheckedButton, Localization.OutputCheckedButtonTooltip))) {
                RecipeStore.MarkOutputChecked(recipe, Localization.OutputCheckedButton);
            }
        }
    }

    /// <summary>
    /// インスペクタは再描画のたびに呼ばれるので、レシピの JSON(マスク込みで数百 KB になり得る)は
    /// 中身が変わったときだけ読み直す。
    /// </summary>
    internal sealed class RecipeSummaryCache {
        private string _json;
        private int _version;
        private IrocaSessionState _state;

        public IrocaSessionState Get(IrocaRecipe recipe) {
            if (!ReferenceEquals(_json, recipe.sessionJson) || _version != recipe.formatVersion) {
                _json = recipe.sessionJson;
                _version = recipe.formatVersion;
                _state = RecipeStore.Load(recipe);
            }
            return _state;
        }

        public bool Readable(IrocaRecipe recipe) => Get(recipe) != null;
    }
}
