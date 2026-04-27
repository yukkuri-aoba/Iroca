using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow : EditorWindow
    {
        // ── ユーザー入力・設定 ──
        // [SerializeField] を付けることで、スクリプト再コンパイル時に Unity が
        // EditorWindow の状態をシリアライズ/復元し、入力内容が失われにくくなる。
        [SerializeField] private Texture2D sourceTexture;
        [SerializeField] private List<ColorZone> zones = new List<ColorZone>();
        private Vector2 scrollPos;
        [SerializeField] private bool saveAsNewFile = true;
        [SerializeField] private string newFileName = "";
        [SerializeField] private bool inheritImportSettings = true;

        // Processing settings
        [SerializeField] private float edgeFeather = 0f;
        [SerializeField] private int antiAliasCleanup = 3;

        // Edge decontamination (alpha matting): AA境界での halo を構造的に除去する。
        // dev_safe/docs/edge_decontamination.md を参照。
        [SerializeField] private bool useDecontamination = true;
        [SerializeField] private int decontaminationRadius = 4;

        // アドバンスモード
        [SerializeField] private bool advancedMode;
        // 既定 5: バンダナ装飾の細かい三角形など 6px 程度までの細部の取りこぼしを抑える。
        // 必要なら IntSlider で 0..10 に調整可能。
        [SerializeField] private int holeFillPasses = 5;
        [SerializeField] private int holeFillMinNeighbors = 4;
        [SerializeField] private float relaxedSatMin = 0.02f;
        [SerializeField] private float relaxedSatRamp = 0.08f;

        // Foldouts
        [SerializeField] private bool zonesFoldout = true;
        [SerializeField] private bool processingFoldout = true;

        // 横並びレイアウトの左右カラム用スクロール
        private Vector2 leftScrollPos;

        // 横並びレイアウトの最小ウィンドウ幅閾値（これ以下は縦並びにフォールバック）
        private const float SideBySideMinWidth = 600f;

        [MenuItem("Tools/VRC AvatarColorChanger")]
        public static void ShowWindow()
        {
            var window = GetWindow<VACCWindow>(Localization.WindowTitle);
            window.minSize = new Vector2(340, 500);
            if (window.position.width < 800 || window.position.height < 800)
                window.position = new Rect(window.position.x, window.position.y, 800, 800);
        }

        private void OnEnable()
        {
            EnsureAllZoneIds();
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
                zoneMasks.Clear();
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
                zone.EnsureId();
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Header row
                EditorGUILayout.BeginHorizontal();
                zone.enabled = EditorGUILayout.ToggleLeft(
                    new GUIContent("", Localization.ZoneEnabledTooltip),
                    zone.enabled, GUILayout.Width(16));
                zone.name = EditorGUILayout.TextField(
                    new GUIContent("", Localization.ZoneNameTooltip),
                    zone.name);
                EditorGUILayout.LabelField(
                    new GUIContent(Localization.LayerIndex, Localization.LayerIndexTooltip),
                    GUILayout.Width(14));
                zone.layerIndex = Mathf.Max(0, EditorGUILayout.IntField(zone.layerIndex, GUILayout.Width(30)));
                if (GUILayout.Button(new GUIContent("×", Localization.RemoveZoneTooltip), GUILayout.Width(22)))
                {
                    removeIndex = i;
                }
                EditorGUILayout.EndHorizontal();

                // ソーンマスク編集ボタン（フル幅・状態連動）
                {
                    bool isActive = activeMaskTarget == i;
                    var prevBg = GUI.backgroundColor;
                    if (isActive) GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
                    string label = isActive ? Localization.EditMaskActiveLabel : Localization.EditMaskInactiveLabel;
                    if (GUILayout.Button(new GUIContent(label, Localization.EditMaskTooltip)))
                    {
                        activeMaskTarget = isActive ? -1 : i;
                        maskFoldout = true;
                        maskDirty = true;
                        Repaint();
                    }
                    GUI.backgroundColor = prevBg;
                }

                zone.mode = (SelectionMode)EditorGUILayout.EnumPopup(
                    new GUIContent(Localization.SelectionMode, Localization.SelectionModeTooltip),
                    zone.mode);

                if (zone.mode == SelectionMode.ColorPick)
                {
                    zone.sampleColor = EditorGUILayout.ColorField(
                        new GUIContent(Localization.SampleColor, Localization.SampleColorTooltip),
                        zone.sampleColor);
                    zone.tolerance = EditorGUILayout.Slider(
                        new GUIContent(Localization.Tolerance, Localization.ToleranceTooltip),
                        zone.tolerance, 0f, 1f);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        new GUIContent(Localization.UVRect, Localization.UVRectTooltip));
                    EditorGUI.indentLevel++;
                    float x = EditorGUILayout.Slider("X", zone.uvRect.x, 0f, 1f);
                    float y = EditorGUILayout.Slider("Y", zone.uvRect.y, 0f, 1f);
                    float w = EditorGUILayout.Slider("W", zone.uvRect.width, 0f, 1f);
                    float h = EditorGUILayout.Slider("H", zone.uvRect.height, 0f, 1f);
                    zone.uvRect = new Rect(x, y, w, h);
                    EditorGUI.indentLevel--;
                }

                zone.targetColor = EditorGUILayout.ColorField(
                    new GUIContent(Localization.TargetColor, Localization.TargetColorTooltip),
                    zone.targetColor);
                zone.valueBlend = EditorGUILayout.Slider(
                    new GUIContent(Localization.PatternPreserve, Localization.PatternPreserveTooltip),
                    zone.valueBlend, 0f, 1f);
                zone.edgeSoftness = EditorGUILayout.Slider(
                    new GUIContent(Localization.EdgeSoftness, Localization.EdgeSoftnessTooltip),
                    zone.edgeSoftness, 0f, 1f);
                zone.saturationStrictness = EditorGUILayout.Slider(
                    new GUIContent(Localization.SaturationStrictness, Localization.SaturationStrictnessTooltip),
                    zone.saturationStrictness, 0f, 1f);

                zone.highlightRecovery = EditorGUILayout.Toggle(
                    new GUIContent(Localization.HighlightRecovery, Localization.HighlightRecoveryTooltip),
                    zone.highlightRecovery);

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
                OnZoneAboutToBeRemoved(removeIndex);
                zones.RemoveAt(removeIndex);
                previewDirty = true;
            }

            if (GUILayout.Button(Localization.AddZone))
            {
                var newZone = new ColorZone();
                newZone.EnsureId();
                zones.Add(newZone);
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

            useDecontamination = EditorGUILayout.Toggle(
                new GUIContent(Localization.UseDecontamination, Localization.UseDecontaminationTooltip),
                useDecontamination);

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
                using (new EditorGUI.DisabledScope(!useDecontamination))
                {
                    decontaminationRadius = EditorGUILayout.IntSlider(
                        new GUIContent(Localization.DecontaminationRadius, Localization.DecontaminationRadiusTooltip),
                        decontaminationRadius, 1, 12);
                }
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
            // CancellationToken 経由でバックグラウンドタスクを即時中断。
            // Cancel() 後も Dispose() するまでは OperationCanceledException を投げ続ける。
            try { _previewCts?.Cancel(); } catch { /* ignore */ }
            try { _detailCts?.Cancel();  } catch { /* ignore */ }
            SaveMaskToSession();
            if (previewTexture != null)    DestroyImmediate(previewTexture);
            if (rawPreviewTexture != null) DestroyImmediate(rawPreviewTexture);
            if (diffTexture != null)       DestroyImmediate(diffTexture);
            if (maskOverlayTexture != null) DestroyImmediate(maskOverlayTexture);
            if (zoneMaskOverlayTexture != null) DestroyImmediate(zoneMaskOverlayTexture);
            if (_detailPreviewTexture != null)    DestroyImmediate(_detailPreviewTexture);
            if (_rawDetailPreviewTexture != null) DestroyImmediate(_rawDetailPreviewTexture);
            if (_detailMaskOverlayTexture != null) DestroyImmediate(_detailMaskOverlayTexture);
            if (_detailDiffTexture != null)        DestroyImmediate(_detailDiffTexture);
        }
    }
}
