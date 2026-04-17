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

        // アドバンスモード
        private bool advancedMode;
        private int holeFillPasses = 3;
        private int holeFillMinNeighbors = 4;
        private float relaxedSatMin = 0.02f;
        private float relaxedSatRamp = 0.08f;

        // Foldouts
        private bool zonesFoldout = true;
        private bool processingFoldout = true;

        // 横並びレイアウトの左右カラム用スクロール
        private Vector2 leftScrollPos;

        // 横並びレイアウトの最小ウィンドウ幅閾値（これ以下は縦並びにフォールバック）
        private const float SideBySideMinWidth = 600f;

        [MenuItem("Tools/VRC AvatarColorChanger")]
        public static void ShowWindow()
        {
            var window = GetWindow<VACCWindow>(Localization.WindowTitle);
            window.minSize = new Vector2(340, 500);
            if (window.position.width < 800 || window.position.height < 700)
                window.position = new Rect(window.position.x, window.position.y, 800, 700);
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

            bool sideBySide = position.width >= SideBySideMinWidth;

            if (sideBySide)
            {
                // ── 上部: テクスチャフィールド（フル幅） ──
                EditorGUI.BeginChangeCheck();
                DrawTextureField();

                // ── 横並び: 左（設定）＋ 右（プレビュー） ──
                EditorGUILayout.BeginHorizontal();

                // 左カラム: ゾーン設定 + 処理設定 + マスク + プリセット
                float leftWidth = Mathf.Clamp(position.width * 0.4f, 280f, 450f);
                EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
                leftScrollPos = EditorGUILayout.BeginScrollView(leftScrollPos);

                DrawZoneList();
                DrawProcessingSection();
                DrawMaskSection();

                if (EditorGUI.EndChangeCheck())
                {
                    previewDirty = true;
                }

                DrawPresetsSection();
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();

                // 右カラム: プレビュー
                EditorGUILayout.BeginVertical();
                DrawPreview();
                EditorGUILayout.EndVertical();

                EditorGUILayout.EndHorizontal();

                // ── 下部: 一括適用 + エクスポート（フル幅） ──
                DrawBatchSection();
                DrawExportSection();
            }
            else
            {
                // ── 従来の縦並びレイアウト（ウィンドウ幅が狭い場合） ──
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
                // テクスチャが変わったのでソースピクセルキャッシュを無効化
                _cachedSourceTexture = null;
                _cachedSrcPixels = null;
                _cachedRawDisplay = null;
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

                if (advancedMode)
                {
                    zone.valueWeight = EditorGUILayout.Slider(
                        new GUIContent(Localization.ValueWeight, Localization.ValueWeightTooltip),
                        zone.valueWeight, 0f, 1f);
                    zone.satDistWeight = EditorGUILayout.Slider(
                        new GUIContent(Localization.SatDistWeight, Localization.SatDistWeightTooltip),
                        zone.satDistWeight, 0f, 1f);
                    zone.satRampScale = EditorGUILayout.Slider(
                        new GUIContent(Localization.SatRampScale, Localization.SatRampScaleTooltip),
                        zone.satRampScale, 0.01f, 0.5f);
                }

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

            advancedMode = EditorGUILayout.Toggle(
                new GUIContent(Localization.AdvancedMode, Localization.AdvancedModeTooltip),
                advancedMode);

            if (advancedMode)
            {
                EditorGUI.indentLevel++;
                holeFillPasses = EditorGUILayout.IntSlider(
                    new GUIContent(Localization.HoleFillPasses, Localization.HoleFillPassesTooltip),
                    holeFillPasses, 0, 10);
                holeFillMinNeighbors = EditorGUILayout.IntSlider(
                    new GUIContent(Localization.HoleFillMinNeighbors, Localization.HoleFillMinNeighborsTooltip),
                    holeFillMinNeighbors, 1, 8);
                relaxedSatMin = EditorGUILayout.Slider(
                    new GUIContent(Localization.RelaxedSatMin, Localization.RelaxedSatMinTooltip),
                    relaxedSatMin, 0f, 0.2f);
                relaxedSatRamp = EditorGUILayout.Slider(
                    new GUIContent(Localization.RelaxedSatRamp, Localization.RelaxedSatRampTooltip),
                    relaxedSatRamp, 0.01f, 0.3f);
                EditorGUI.indentLevel--;
            }

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
