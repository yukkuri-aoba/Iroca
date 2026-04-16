using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow : EditorWindow
    {
        private Texture2D sourceTexture;
        private List<ColorZone> zones = new List<ColorZone>();
        private Vector2 scrollPos;
        private bool saveAsNewFile = true;
        private string newFileName = "";

        // Processing settings
        private float edgeFeather = 0f;
        private int antiAliasCleanup = 3;

        // Foldouts
        private bool zonesFoldout = true;
        private bool processingFoldout = true;

        [MenuItem("Tools/VRC AvatarColorChanger")]
        public static void ShowWindow()
        {
            var window = GetWindow<VACCWindow>(Localization.WindowTitle);
            window.minSize = new Vector2(340, 500);
        }

        private void OnEnable()
        {
            RestoreMaskFromSession();
        }

        private void OnDisable()
        {
            SaveMaskToSession();
        }

        private void OnGUI()
        {
            DrawHeader();
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            EditorGUI.BeginChangeCheck();

            DrawTextureField();
            DrawZoneList();
            DrawProcessingSection();
            DrawMaskSection();

            if (EditorGUI.EndChangeCheck())
            {
                previewDirty = true;
            }

            DrawPresetsSection();
            DrawPreview();
            DrawBatchSection();
            DrawExportSection();

            EditorGUILayout.EndScrollView();
        }

        // ───────────────────────── ヘッダー ───────────────────────────

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Language selector
            var labels = new[] { Localization.LangAuto, Localization.LangJapanese, Localization.LangEnglish };
            int current = (int)Localization.CurrentLanguage;
            int next = GUILayout.Toolbar(current, labels, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            if (next != current)
            {
                Localization.CurrentLanguage = (LanguageMode)next;
                Repaint();
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(Localization.Credit, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
            {
                EditorUtility.DisplayDialog(Localization.CreditTitle, Localization.CreditBody, Localization.OK);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ───────────────────────── テクスチャフィールド ───────────────────────────

        private void DrawTextureField()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localization.SourceTexture, EditorStyles.boldLabel);

            var newTex = (Texture2D)EditorGUILayout.ObjectField(
                Localization.Texture, sourceTexture, typeof(Texture2D), false);
            if (newTex != sourceTexture)
            {
                SaveMaskToSession();                         // persist mask for old texture
                sourceTexture = newTex;
                previewDirty = true;
                exclusionMask = null;
                _undoMaskHistory.Clear();
                if (sourceTexture != null)
                {
                    var path = AssetDatabase.GetAssetPath(sourceTexture);
                    newFileName = Path.GetFileNameWithoutExtension(path) + "_recolored";
                }
                RestoreMaskFromSession();                    // load mask for new texture
            }

            if (sourceTexture != null && !IsReadable(sourceTexture))
            {
                EditorGUILayout.HelpBox(Localization.ReadWriteError, MessageType.Error);

                if (GUILayout.Button(Localization.EnableReadWrite))
                {
                    EnableReadWrite(sourceTexture);
                }
            }

            EditorGUILayout.Space(4);
        }

        // ───────────────────────── ゾーンリスト ───────────────────────────

        private void DrawZoneList()
        {
            zonesFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(zonesFoldout, Localization.ColorZones);
            if (!zonesFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            int removeIndex = -1;
            for (int i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Header row
                EditorGUILayout.BeginHorizontal();
                zone.enabled = EditorGUILayout.ToggleLeft("", zone.enabled, GUILayout.Width(16));
                zone.name = EditorGUILayout.TextField(zone.name);
                EditorGUILayout.LabelField(Localization.LayerIndex, GUILayout.Width(14));
                zone.layerIndex = Mathf.Max(0, EditorGUILayout.IntField(zone.layerIndex, GUILayout.Width(30)));
                if (GUILayout.Button("×", GUILayout.Width(22)))
                {
                    removeIndex = i;
                }
                EditorGUILayout.EndHorizontal();

                zone.mode = (SelectionMode)EditorGUILayout.EnumPopup(
                    Localization.SelectionMode, zone.mode);

                if (zone.mode == SelectionMode.ColorPick)
                {
                    zone.sampleColor = EditorGUILayout.ColorField(
                        Localization.SampleColor, zone.sampleColor);
                    zone.tolerance = EditorGUILayout.Slider(
                        Localization.Tolerance, zone.tolerance, 0f, 1f);
                }
                else
                {
                    EditorGUILayout.LabelField(Localization.UVRect);
                    EditorGUI.indentLevel++;
                    float x = EditorGUILayout.Slider("X", zone.uvRect.x, 0f, 1f);
                    float y = EditorGUILayout.Slider("Y", zone.uvRect.y, 0f, 1f);
                    float w = EditorGUILayout.Slider("W", zone.uvRect.width, 0f, 1f);
                    float h = EditorGUILayout.Slider("H", zone.uvRect.height, 0f, 1f);
                    zone.uvRect = new Rect(x, y, w, h);
                    EditorGUI.indentLevel--;
                }

                zone.targetColor = EditorGUILayout.ColorField(
                    Localization.TargetColor, zone.targetColor);
                zone.valueBlend = EditorGUILayout.Slider(
                    new GUIContent(Localization.PatternPreserve, Localization.PatternPreserveTooltip),
                    zone.valueBlend, 0f, 1f);
                zone.edgeSoftness = EditorGUILayout.Slider(
                    new GUIContent(Localization.EdgeSoftness, Localization.EdgeSoftnessTooltip),
                    zone.edgeSoftness, 0f, 1f);
                zone.saturationStrictness = EditorGUILayout.Slider(
                    new GUIContent(Localization.SaturationStrictness, Localization.SaturationStrictnessTooltip),
                    zone.saturationStrictness, 0f, 1f);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            if (removeIndex >= 0)
            {
                zones.RemoveAt(removeIndex);
                previewDirty = true;
            }

            if (GUILayout.Button(Localization.AddZone))
            {
                zones.Add(new ColorZone());
                previewDirty = true;
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        // ───────────────────────── 処理設定 ───────────────────────────

        private void DrawProcessingSection()
        {
            processingFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(processingFoldout, Localization.Processing);
            if (!processingFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            edgeFeather = EditorGUILayout.Slider(
                new GUIContent(Localization.EdgeFeather, Localization.EdgeFeatherTooltip),
                edgeFeather, 0f, 5f);

            antiAliasCleanup = EditorGUILayout.IntSlider(
                new GUIContent(Localization.AntiAliasCleanup, Localization.AntiAliasCleanupTooltip),
                antiAliasCleanup, 0, 5);

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        // ───────────────────────── ユーティリティ ───────────────────────────

        private static bool IsReadable(Texture2D tex)
        {
            try
            {
                tex.GetPixel(0, 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnableReadWrite(Texture2D tex)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        private void OnDestroy()
        {
            _asyncCancelled = true;  // stop background task from writing results or calling Repaint
            SaveMaskToSession();
            if (previewTexture != null)    DestroyImmediate(previewTexture);
            if (rawPreviewTexture != null) DestroyImmediate(rawPreviewTexture);
            if (diffTexture != null)       DestroyImmediate(diffTexture);
            if (maskOverlayTexture != null) DestroyImmediate(maskOverlayTexture);
            if (_detailPreviewTexture != null)    DestroyImmediate(_detailPreviewTexture);
            if (_rawDetailPreviewTexture != null) DestroyImmediate(_rawDetailPreviewTexture);
            if (_detailMaskOverlayTexture != null) DestroyImmediate(_detailMaskOverlayTexture);
            if (_detailDiffTexture != null)        DestroyImmediate(_detailDiffTexture);
        }
    }
}
