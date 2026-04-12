using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public class VACCWindow : EditorWindow
    {
        private Texture2D sourceTexture;
        private List<ColorZone> zones = new List<ColorZone>();
        private Texture2D previewTexture;
        private Vector2 scrollPos;
        private bool saveAsNewFile = true;
        private string newFileName = "";
        private float previewZoom = 1f;
        private bool previewDirty = true;
        private Vector2 previewScrollPos;

        // Exclusion mask (full resolution, true = excluded)
        private bool[] exclusionMask;
        private int maskWidth, maskHeight;
        private int brushSize = 8;
        private bool brushEraseMode; // false = paint exclusion, true = erase exclusion
        private bool isPainting;
        private Vector2 lastPaintUV = -Vector2.one;

        // Mask overlay
        private Texture2D maskOverlayTexture;
        private bool maskDirty = true;

        // Processing settings
        private float edgeFeather = 0f;
        private int antiAliasCleanup = 3;

        // Foldouts
        private bool zonesFoldout = true;
        private bool processingFoldout = true;
        private bool maskFoldout = true;
        private bool exportFoldout = true;

        // Preview generation
        private const int PreviewMaxSize = 512;

        // Async preview state
        private volatile bool _previewGenerating;
        private volatile bool _asyncCancelled;
        private volatile int _asyncGeneration;
        private Color32[] _pendingProcessedDisplay;
        private Color32[] _pendingRawDisplay;
        private int _pendingPrevW, _pendingPrevH;
        private double _lastDirtyTime;
        private const double PreviewDebounceSeconds = 0.2;

        // Phase 2: Before/After comparison
        private Texture2D rawPreviewTexture;
        private bool comparisonMode;
        private bool diffMode;
        private Texture2D diffTexture;

        // Phase 3: Exclusion mask undo history (max 30 steps)
        private readonly List<bool[]> _undoMaskHistory = new List<bool[]>();
        private bool _maskStrokeStarted;
        private const int UndoMaskLimit = 30;

        // Phase 5: Presets
        private bool presetsFoldout;
        private string presetSaveName = "Preset";
        private bool presetStorageProject = true;
        private Vector2 presetScrollPos;

        // Phase 7: hotControl-based preview update — fires immediately when slider is released

        // Phase 8: Batch apply
        private bool batchFoldout;
        private List<Texture2D> batchTextures = new List<Texture2D>();
        private Vector2 batchScrollPos;

        // Mask paint mode: must be explicitly activated before brush strokes work
        private bool maskPaintActive;

        // Detail preview: full-resolution crop rendered when zoomed in
        private Texture2D _detailPreviewTexture;
        private Texture2D _rawDetailPreviewTexture;
        private Texture2D _detailMaskOverlayTexture;
        private volatile bool _detailGenerating;
        private volatile int _detailAsyncGeneration;
        private Color32[] _pendingDetailProcessed;
        private Color32[] _pendingDetailRaw;
        private int _pendingDetailW, _pendingDetailH;
        private int _pendingDetailOriginX, _pendingDetailOriginY; // crop origin in source coords
        private int _pendingDetailFullW, _pendingDetailFullH;     // full source dimensions
        private double _lastDetailDirtyTime;
        private Rect _lastPreviewRect;                            // stored between frames
        private const double DetailDebounceSeconds = 0.3;
        // Detail mode: activates when the display-pixel-to-source-pixel ratio > 1
        // i.e. previewZoom * (srcSize / previewSize) ≥ this value
        private const float DetailUpscaleThreshold = 1.0f;

        // Persistent detail crop origin (set when detail preview is applied, read by renderer)
        private int _detailOriginX, _detailOriginY;

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

        // ───────────────────────── Header ─────────────────────────

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

        // ───────────────────────── Texture Field ─────────────────────────

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

        // ───────────────────────── Zone List ─────────────────────────

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

        // ───────────────────────── Processing Settings ─────────────────────────

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

        // ───────────────────────── Exclusion Mask ─────────────────────────

        private void DrawMaskSection()
        {
            maskFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(maskFoldout, Localization.ExclusionMask);
            if (!maskFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            brushSize = EditorGUILayout.IntSlider(Localization.BrushSize, brushSize, 1, 64);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!brushEraseMode, Localization.Exclude, EditorStyles.miniButtonLeft) && brushEraseMode)
                brushEraseMode = false;
            if (GUILayout.Toggle(brushEraseMode, Localization.Include, EditorStyles.miniButtonRight) && !brushEraseMode)
                brushEraseMode = true;
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button(Localization.ClearMask))
            {
                PushMaskUndo();
                exclusionMask = null;
                maskDirty = true;
                previewDirty = true;
            }

            EditorGUI.BeginDisabledGroup(_undoMaskHistory.Count == 0);
            if (GUILayout.Button(Localization.UndoMask))
                UndoMaskStep();
            EditorGUI.EndDisabledGroup();

            // Paint mode toggle: brush strokes only work when this is ON
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = maskPaintActive ? new Color(1f, 0.45f, 0.45f) : Color.white;
            if (GUILayout.Button(maskPaintActive ? Localization.MaskPaintModeOn : Localization.MaskPaintModeOff,
                GUILayout.Height(24)))
            {
                maskPaintActive = !maskPaintActive;
            }
            GUI.backgroundColor = prevBg;

            EditorGUILayout.HelpBox(
                maskPaintActive ? Localization.MaskHint : Localization.MaskHintPaintOff,
                MessageType.Info);

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        private void EnsureMask()
        {
            if (sourceTexture == null) return;
            if (exclusionMask == null || exclusionMask.Length != sourceTexture.width * sourceTexture.height)
            {
                maskWidth = sourceTexture.width;
                maskHeight = sourceTexture.height;
                exclusionMask = new bool[maskWidth * maskHeight];
            }
        }

        private void PaintMask(Vector2 uvPos)
        {
            EnsureMask();
            int cx = Mathf.RoundToInt(uvPos.x * maskWidth);
            int cy = Mathf.RoundToInt(uvPos.y * maskHeight);
            // Scale brush size from preview pixels to mask pixels
            float maskScale = maskWidth / (float)Mathf.Min(maskWidth, PreviewMaxSize);
            int r = Mathf.Max(1, Mathf.RoundToInt(brushSize * maskScale));

            bool value = !brushEraseMode; // true = excluded

            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    int px = cx + dx;
                    int py = cy + dy;
                    if (px < 0 || px >= maskWidth || py < 0 || py >= maskHeight) continue;
                    exclusionMask[py * maskWidth + px] = value;
                }
            }

            maskDirty = true;
        }

        private bool IsExcluded(int x, int y, int texWidth, int texHeight)
        {
            if (exclusionMask == null) return false;
            int mx = Mathf.Clamp(x * maskWidth / texWidth, 0, maskWidth - 1);
            int my = Mathf.Clamp(y * maskHeight / texHeight, 0, maskHeight - 1);
            return exclusionMask[my * maskWidth + mx];
        }

        // ───────────────────────── Preview ─────────────────────────

        private void DrawPreview()
        {
            EditorGUILayout.LabelField(Localization.Preview, EditorStyles.boldLabel);

            if (sourceTexture == null)
            {
                EditorGUILayout.HelpBox(Localization.SetTexture, MessageType.Info);
                return;
            }

            if (!IsReadable(sourceTexture))
                return;

            // Apply results from background preview task (Texture2D API: main thread only)
            if (_pendingProcessedDisplay != null)
                ApplyPendingPreview();

            // Apply results from background detail preview task
            if (_pendingDetailProcessed != null)
                ApplyPendingDetailPreview();

            if (previewDirty)
            {
                // Record the time of the latest change. The async task starts only
                // after the user stops interacting for PreviewDebounceSeconds.
                // This keeps the main thread completely free while dragging sliders.
                _lastDirtyTime = UnityEditor.EditorApplication.timeSinceStartup;
                _asyncGeneration++; // invalidate any in-flight task
                // Do NOT reset _previewGenerating here: if a task is running it will
                // detect the generation mismatch and clean up itself via try/finally.
                // Resetting the flag from the main thread while a task is still alive
                // causes a race where the old task's finally overwrites the new task's flag.
                Repaint(); // schedule next frame check
                previewDirty = false;
            }
            else if (!_previewGenerating &&
                     (UnityEditor.EditorApplication.timeSinceStartup - _lastDirtyTime)
                         >= PreviewDebounceSeconds &&
                     _lastDirtyTime > 0)
            {
                // Debounce elapsed and no task running — start the async generation.
                _lastDirtyTime = 0;
                GeneratePreviewAsync();
            }
            else if (_lastDirtyTime > 0 || _previewGenerating)
            {
                Repaint(); // keep polling: waiting for debounce or bg task
            }

            // Rebuild mask overlay separately (lightweight, safe during painting)
            if (maskDirty && previewTexture != null)
            {
                RebuildMaskOverlay(previewTexture.width, previewTexture.height);
                maskDirty = false;
            }

            if (previewTexture == null)
            {
                if (_previewGenerating)
                    EditorGUILayout.HelpBox(Localization.GeneratingPreview, MessageType.None);
                return;
            }

            if (_previewGenerating)
                EditorGUILayout.LabelField(Localization.GeneratingPreview);

            // Zoom slider + comparison/diff toggles
            float prevZoom = previewZoom;
            previewZoom = EditorGUILayout.Slider(
                new GUIContent(Localization.Zoom, Localization.ZoomHint),
                previewZoom, 0.25f, 4f);
            if (Mathf.Abs(previewZoom - prevZoom) > 0.0001f)
            {
                _lastDetailDirtyTime = UnityEditor.EditorApplication.timeSinceStartup;
                _detailAsyncGeneration++;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(comparisonMode, Localization.ComparisonMode, EditorStyles.miniButtonLeft) != comparisonMode)
            {
                comparisonMode = !comparisonMode;
                if (comparisonMode) diffMode = false;
            }
            if (GUILayout.Toggle(diffMode, Localization.DiffMode, EditorStyles.miniButtonRight) != diffMode)
            {
                diffMode = !diffMode;
                if (diffMode) comparisonMode = false;
            }
            EditorGUILayout.EndHorizontal();

            // Compute preview scale (ratio of preview texture size to source size)
            int srcW = sourceTexture.width;
            int srcH = sourceTexture.height;
            float scale = (srcW > PreviewMaxSize || srcH > PreviewMaxSize)
                ? PreviewMaxSize / (float)Mathf.Max(srcW, srcH)
                : 1f;

            // Detail mode: active when display pixels > source pixels (scale < 1 and zoom high enough)
            bool detailActive = scale < 1f &&
                                previewZoom * scale >= DetailUpscaleThreshold &&
                                !comparisonMode && !diffMode;

            // Poll detail preview generation
            if (detailActive)
            {
                if (!_detailGenerating &&
                    _lastDetailDirtyTime > 0 &&
                    (UnityEditor.EditorApplication.timeSinceStartup - _lastDetailDirtyTime)
                        >= DetailDebounceSeconds &&
                    _lastPreviewRect.width > 0)
                {
                    _lastDetailDirtyTime = 0;
                    Color32[] srcPixels = sourceTexture.GetPixels32();
                    GenerateDetailPreviewAsync(srcW, srcH, srcPixels, scale, _lastPreviewRect);
                }
                else if (_lastDetailDirtyTime > 0 || _detailGenerating)
                {
                    Repaint();
                }
            }

            float displayW = previewTexture.width  * previewZoom;
            float displayH = previewTexture.height * previewZoom;

            float maxViewH = Mathf.Min(displayH, previewTexture.height) + 16f;

            Vector2 prevScroll = previewScrollPos;
            previewScrollPos = EditorGUILayout.BeginScrollView(
                previewScrollPos,
                GUILayout.Height(maxViewH));
            if (previewScrollPos != prevScroll)
            {
                _lastDetailDirtyTime = UnityEditor.EditorApplication.timeSinceStartup;
                _detailAsyncGeneration++;
            }

            Rect activePreviewRect = default;

            if (comparisonMode && rawPreviewTexture != null)
            {
                EditorGUILayout.BeginHorizontal();

                // Before panel
                EditorGUILayout.BeginVertical(GUILayout.Width(displayW));
                EditorGUILayout.LabelField(Localization.Before, GUILayout.Width(displayW));
                var rawRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                EditorGUI.DrawPreviewTexture(rawRect, rawPreviewTexture);
                EditorGUILayout.EndVertical();

                GUILayout.Space(8);

                // After panel
                EditorGUILayout.BeginVertical(GUILayout.Width(displayW));
                EditorGUILayout.LabelField(Localization.After, GUILayout.Width(displayW));
                activePreviewRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);
                if (maskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, maskOverlayTexture, ScaleMode.StretchToFill, true);
                EditorGUILayout.EndVertical();

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                activePreviewRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));

                // In detail mode, overlay the full-res crop on top of the low-res preview
                if (detailActive && _detailPreviewTexture != null)
                {
                    // Draw low-res base (gives context for non-visible areas)
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    // Compute screen rect that the detail crop should occupy
                    Rect detailScreenRect = ComputeDetailScreenRect(activePreviewRect, scale, srcW, srcH);
                    GUI.DrawTexture(detailScreenRect, _detailPreviewTexture,
                        ScaleMode.StretchToFill, false);

                    if (_detailMaskOverlayTexture != null)
                        GUI.DrawTexture(detailScreenRect, _detailMaskOverlayTexture,
                            ScaleMode.StretchToFill, true);
                }
                else
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    if (diffMode && diffTexture != null)
                        GUI.DrawTexture(activePreviewRect, diffTexture, ScaleMode.StretchToFill, true);
                    else if (maskOverlayTexture != null)
                        GUI.DrawTexture(activePreviewRect, maskOverlayTexture, ScaleMode.StretchToFill, true);
                }

                // Generating indicator for detail preview
                if (detailActive && _detailGenerating)
                    EditorGUI.LabelField(
                        new Rect(activePreviewRect.x + 4, activePreviewRect.y + 4, 300, 20),
                        Localization.GeneratingDetailPreview);
            }

            // Store the preview rect for use by the next detail generation tick
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                _lastPreviewRect = activePreviewRect;

            // Handle brush / wand input on the active (After) panel
            if (maskFoldout && maskPaintActive)
                HandlePreviewInput(activePreviewRect);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// Returns the screen-space rect that the detail crop should be drawn into,
        /// positioned so it aligns exactly with the corresponding region of the scrolled preview.
        /// </summary>
        private Rect ComputeDetailScreenRect(Rect activePreviewRect, float scale, int srcW, int srcH)
        {
            if (_detailPreviewTexture == null) return activePreviewRect;

            // display pixels per source pixel
            float pxPerSrc = scale * previewZoom;

            float left   = _detailOriginX * pxPerSrc - previewScrollPos.x + activePreviewRect.x;
            float top    = _detailOriginY * pxPerSrc - previewScrollPos.y + activePreviewRect.y;
            float width  = _detailPreviewTexture.width  * pxPerSrc;
            float height = _detailPreviewTexture.height * pxPerSrc;

            return new Rect(left, top, width, height);
        }

        private void HandlePreviewInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.ScrollWheel:
                    if (isInRect && e.control)
                    {
                        float oldZoom = previewZoom;
                        // 乗算的ズーム: スクロール1ノッチ(delta≈3)で約10%変化、自然な加速感
                        float newZoom = Mathf.Clamp(oldZoom * Mathf.Pow(1.1f, -e.delta.y / 3f), 0.25f, 4f);

                        // マウス位置を起点にスクロールオフセットを補正
                        if (previewTexture != null && Mathf.Abs(newZoom - oldZoom) > 0.0001f)
                        {
                            // previewRect との差 = 画像左上からのディスプレイピクセル距離
                            Vector2 mouseInImage = e.mousePosition - new Vector2(previewRect.x, previewRect.y);
                            previewScrollPos += mouseInImage * (newZoom / oldZoom - 1f);
                            previewScrollPos.x = Mathf.Max(0f, previewScrollPos.x);
                            previewScrollPos.y = Mathf.Max(0f, previewScrollPos.y);
                        }

                        previewZoom = newZoom;
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.KeyDown:
                    if (e.control && e.keyCode == KeyCode.Z && !isPainting)
                    {
                        UndoMaskStep();
                        e.Use();
                    }
                    break;

                case EventType.MouseDown:
                    if (e.button == 0 && isInRect)
                    {
                        GUIUtility.hotControl = controlId;
                        isPainting = true;
                        _maskStrokeStarted = false;
                        lastPaintUV = -Vector2.one;
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (isPainting && GUIUtility.hotControl == controlId)
                    {
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        isPainting = false;
                        _maskStrokeStarted = false;
                        lastPaintUV = -Vector2.one;
                        previewDirty = true;
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.Repaint:
                    // ブラシカーソルはペイント中のみ表示
                    // (ホバー中に表示するとUnity標準スポイトが赤円色を拾うため)
                    if (isPainting && isInRect)
                    {
                        float brushPixels = brushSize * previewZoom;
                        Handles.color = brushEraseMode
                            ? new Color(0, 1, 0, 0.5f)
                            : new Color(1, 0, 0, 0.5f);
                        Handles.DrawSolidDisc(e.mousePosition, Vector3.forward, brushPixels * 0.5f);
                    }
                    break;
            }
        }

        private void PaintAtScreenPos(Vector2 screenPos, Rect previewRect)
        {
            // Push undo state once at the start of each new brush stroke
            if (!_maskStrokeStarted)
            {
                PushMaskUndo();
                _maskStrokeStarted = true;
            }

            float u = (screenPos.x - previewRect.x) / previewRect.width;
            float v = 1f - (screenPos.y - previewRect.y) / previewRect.height; // flip Y
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);

            var currentUV = new Vector2(u, v);

            // Interpolate between last and current position for continuous strokes
            if (lastPaintUV.x >= 0f)
            {
                float dist = Vector2.Distance(lastPaintUV, currentUV);
                // Step size in UV space based on brush size relative to mask resolution
                EnsureMask();
                float step = (maskWidth > 0) ? 1f / maskWidth : 0.001f;
                if (dist > step)
                {
                    int steps = Mathf.CeilToInt(dist / step);
                    for (int i = 1; i < steps; i++)
                    {
                        Vector2 lerped = Vector2.Lerp(lastPaintUV, currentUV, (float)i / steps);
                        PaintMask(lerped);
                    }
                }
            }

            PaintMask(currentUV);
            lastPaintUV = currentUV;
        }

        private void GeneratePreviewAsync()
        {
            if (sourceTexture == null || !IsReadable(sourceTexture)) return;

            _previewGenerating = true;
            int myGen = _asyncGeneration;

            int srcW = sourceTexture.width;
            int srcH = sourceTexture.height;

            // Capture all Unity-dependent data on the main thread before handing off.
            Color32[] srcPixels = sourceTexture.GetPixels32();

            bool[] maskSnapshot = exclusionMask != null ? (bool[])exclusionMask.Clone() : null;
            int mW = maskWidth, mH = maskHeight;

            var zonesSnapshot = zones
                .Where(z => z.enabled)
                .OrderBy(z => z.layerIndex)
                .Select(CloneZone)
                .ToList();
            float feather = edgeFeather;
            int aaCleanup = antiAliasCleanup;

            float scale = 1f;
            if (srcW > PreviewMaxSize || srcH > PreviewMaxSize)
                scale = PreviewMaxSize / (float)Mathf.Max(srcW, srcH);
            int prevW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int prevH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // All heavy computation runs on the background thread.
                    // No Unity Object API is called here — only pure C# math.
                    Color32[] pixels = (Color32[])srcPixels.Clone();
                    ProcessPixelsArray(pixels, srcW, srcH, maskSnapshot, mW, mH, zonesSnapshot, feather, aaCleanup);

                    // Superseded by a newer task — discard results silently.
                    if (myGen != _asyncGeneration || _asyncCancelled)
                        return;

                    Color32[] rawDisplay = scale < 1f
                        ? BoxDownsample(srcPixels, srcW, srcH, prevW, prevH, scale)
                        : srcPixels;
                    Color32[] processedDisplay = scale < 1f
                        ? BoxDownsample(pixels, srcW, srcH, prevW, prevH, scale)
                        : pixels;

                    _pendingRawDisplay       = rawDisplay;
                    _pendingProcessedDisplay = processedDisplay;
                    _pendingPrevW            = prevW;
                    _pendingPrevH            = prevH;
                }
                finally
                {
                    // Always reset the flag and schedule a repaint so the main-thread
                    // polling loop can react — even when cancelled or an exception occurs.
                    _previewGenerating = false;
                    Repaint();
                }
            });
        }

        private void ApplyPendingPreview()
        {
            // Swap out the pending arrays before touching Texture2D objects.
            var processed = _pendingProcessedDisplay;
            var raw       = _pendingRawDisplay;
            int w = _pendingPrevW;
            int h = _pendingPrevH;
            _pendingProcessedDisplay = null;
            _pendingRawDisplay       = null;

            if (processed == null || raw == null) return;

            if (previewTexture == null || previewTexture.width != w || previewTexture.height != h)
            {
                if (previewTexture != null) DestroyImmediate(previewTexture);
                previewTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }
            previewTexture.SetPixels32(processed);
            previewTexture.Apply();

            if (rawPreviewTexture == null || rawPreviewTexture.width != w || rawPreviewTexture.height != h)
            {
                if (rawPreviewTexture != null) DestroyImmediate(rawPreviewTexture);
                rawPreviewTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }
            rawPreviewTexture.SetPixels32(raw);
            rawPreviewTexture.Apply();

            BuildDiffTexture(rawPreviewTexture, previewTexture);

            if (maskOverlayTexture == null || maskOverlayTexture.width != w || maskOverlayTexture.height != h || maskDirty)
            {
                RebuildMaskOverlay(w, h);
                maskDirty = false;
            }

            // Invalidate detail preview so it regenerates at the new crop
            _lastDetailDirtyTime = UnityEditor.EditorApplication.timeSinceStartup;
            _detailAsyncGeneration++;
        }

        // ───────────────────────── Detail Preview (full-res crop) ─────────────────────────

        /// <summary>
        /// Computes the visible crop region in source texture coordinates from the current
        /// scroll position, zoom and preview scale, then launches a background task that
        /// processes only that crop at full source resolution.
        /// </summary>
        private void GenerateDetailPreviewAsync(int srcW, int srcH, Color32[] srcPixels,
            float scale, Rect previewRect)
        {
            if (sourceTexture == null || !IsReadable(sourceTexture)) return;
            if (scale >= 1f) return; // source already fits in preview — no upscaling benefit

            // previewZoom * scale = display pixels per source pixel.
            // If < 1, the preview is still downsampled even after zoom → no detail gain yet.
            float displayPxPerSrcPx = previewZoom * scale;
            if (displayPxPerSrcPx < DetailUpscaleThreshold) return;

            // Compute visible region in source pixel coordinates (Y-flipped: texture bottom=0)
            float invZoomScale = 1f / (previewZoom * scale);
            int x0 = Mathf.FloorToInt(previewScrollPos.x * invZoomScale);
            int y0 = Mathf.FloorToInt(previewScrollPos.y * invZoomScale);
            int x1 = Mathf.CeilToInt((previewScrollPos.x + previewRect.width)  * invZoomScale);
            int y1 = Mathf.CeilToInt((previewScrollPos.y + previewRect.height) * invZoomScale);

            x0 = Mathf.Clamp(x0, 0, srcW);
            y0 = Mathf.Clamp(y0, 0, srcH);
            x1 = Mathf.Clamp(x1, 0, srcW);
            y1 = Mathf.Clamp(y1, 0, srcH);

            int cropW = x1 - x0;
            int cropH = y1 - y0;
            if (cropW <= 0 || cropH <= 0) return;

            _detailGenerating = true;
            int myGen = _detailAsyncGeneration;

            bool[] maskSnapshot = exclusionMask != null ? (bool[])exclusionMask.Clone() : null;
            int mW = maskWidth, mH = maskHeight;

            var zonesSnapshot = zones
                .Where(z => z.enabled)
                .OrderBy(z => z.layerIndex)
                .Select(CloneZone)
                .ToList();
            float feather   = edgeFeather;
            int aaCleanup   = antiAliasCleanup;
            int capX0 = x0, capY0 = y0, capSrcW = srcW, capSrcH = srcH;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Extract crop from source
                    Color32[] rawCrop       = new Color32[cropW * cropH];
                    Color32[] processedCrop = new Color32[cropW * cropH];
                    for (int cy = 0; cy < cropH; cy++)
                    {
                        int sy = capY0 + cy; // source row (texture bottom=0)
                        for (int cx = 0; cx < cropW; cx++)
                        {
                            int srcIdx = sy * capSrcW + (capX0 + cx);
                            rawCrop[cy * cropW + cx] = srcPixels[srcIdx];
                            processedCrop[cy * cropW + cx] = srcPixels[srcIdx];
                        }
                    }

                    ProcessPixelsArray(processedCrop, cropW, cropH,
                        maskSnapshot, mW, mH, zonesSnapshot, feather, aaCleanup,
                        capX0, capY0, capSrcW, capSrcH);

                    if (myGen != _detailAsyncGeneration || _asyncCancelled) return;

                    _pendingDetailRaw       = rawCrop;
                    _pendingDetailProcessed = processedCrop;
                    _pendingDetailW         = cropW;
                    _pendingDetailH         = cropH;
                    _pendingDetailOriginX   = capX0;
                    _pendingDetailOriginY   = capY0;
                    _pendingDetailFullW     = capSrcW;
                    _pendingDetailFullH     = capSrcH;
                }
                finally
                {
                    _detailGenerating = false;
                    Repaint();
                }
            });
        }

        private void ApplyPendingDetailPreview()
        {
            var processed = _pendingDetailProcessed;
            var raw       = _pendingDetailRaw;
            int w  = _pendingDetailW;
            int h  = _pendingDetailH;
            int ox = _pendingDetailOriginX;
            int oy = _pendingDetailOriginY;
            int fw = _pendingDetailFullW;
            int fh = _pendingDetailFullH;
            _pendingDetailProcessed = null;
            _pendingDetailRaw       = null;

            if (processed == null || raw == null) return;

            // Persist the crop origin so the renderer can position the texture correctly
            _detailOriginX = ox;
            _detailOriginY = oy;

            if (_detailPreviewTexture == null || _detailPreviewTexture.width != w || _detailPreviewTexture.height != h)
            {
                if (_detailPreviewTexture != null) DestroyImmediate(_detailPreviewTexture);
                _detailPreviewTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                _detailPreviewTexture.filterMode = FilterMode.Point;
            }
            _detailPreviewTexture.SetPixels32(processed);
            _detailPreviewTexture.Apply();

            if (_rawDetailPreviewTexture == null || _rawDetailPreviewTexture.width != w || _rawDetailPreviewTexture.height != h)
            {
                if (_rawDetailPreviewTexture != null) DestroyImmediate(_rawDetailPreviewTexture);
                _rawDetailPreviewTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                _rawDetailPreviewTexture.filterMode = FilterMode.Point;
            }
            _rawDetailPreviewTexture.SetPixels32(raw);
            _rawDetailPreviewTexture.Apply();

            RebuildDetailMaskOverlay(w, h, ox, oy, fw, fh);
        }

        private void RebuildDetailMaskOverlay(int cropW, int cropH,
            int originX, int originY, int fullW, int fullH)
        {
            if (exclusionMask == null)
            {
                if (_detailMaskOverlayTexture != null)
                {
                    DestroyImmediate(_detailMaskOverlayTexture);
                    _detailMaskOverlayTexture = null;
                }
                return;
            }

            if (_detailMaskOverlayTexture == null ||
                _detailMaskOverlayTexture.width  != cropW ||
                _detailMaskOverlayTexture.height != cropH)
            {
                if (_detailMaskOverlayTexture != null) DestroyImmediate(_detailMaskOverlayTexture);
                _detailMaskOverlayTexture = new Texture2D(cropW, cropH, TextureFormat.RGBA32, false);
                _detailMaskOverlayTexture.filterMode = FilterMode.Point;
            }

            var overlayPixels = new Color32[cropW * cropH];
            var excluded = new Color32(255, 60, 60, 80);
            var clear    = new Color32(0, 0, 0, 0);

            for (int i = 0; i < overlayPixels.Length; i++)
            {
                int cx = i % cropW;
                int cy = i / cropW;
                int mx = Mathf.Clamp((originX + cx) * maskWidth  / fullW, 0, maskWidth  - 1);
                int my = Mathf.Clamp((originY + cy) * maskHeight / fullH, 0, maskHeight - 1);
                bool ex = exclusionMask != null && exclusionMask[my * maskWidth + mx];
                overlayPixels[i] = ex ? excluded : clear;
            }

            _detailMaskOverlayTexture.SetPixels32(overlayPixels);
            _detailMaskOverlayTexture.Apply();
        }

        private static ColorZone CloneZone(ColorZone z) => new ColorZone
        {
            name         = z.name,
            enabled      = z.enabled,
            mode         = z.mode,
            sampleColor  = z.sampleColor,
            tolerance    = z.tolerance,
            uvRect       = z.uvRect,
            targetColor  = z.targetColor,
            valueBlend   = z.valueBlend,
            edgeSoftness = z.edgeSoftness,
            layerIndex   = z.layerIndex,
        };

        private static Color32[] BoxDownsample(Color32[] src, int srcW, int srcH,
            int dstW, int dstH, float scale)
        {
            Color32[] dst = new Color32[dstW * dstH];
            for (int y = 0; y < dstH; y++)
            {
                int sy0 = Mathf.FloorToInt(y / scale);
                int sy1 = Mathf.Min(Mathf.CeilToInt((y + 1f) / scale) - 1, srcH - 1);
                for (int x = 0; x < dstW; x++)
                {
                    int sx0 = Mathf.FloorToInt(x / scale);
                    int sx1 = Mathf.Min(Mathf.CeilToInt((x + 1f) / scale) - 1, srcW - 1);
                    int r = 0, g = 0, b = 0, a = 0, count = 0;
                    for (int ky = sy0; ky <= sy1; ky++)
                        for (int kx = sx0; kx <= sx1; kx++)
                        {
                            var p = src[ky * srcW + kx];
                            r += p.r; g += p.g; b += p.b; a += p.a;
                            count++;
                        }
                    dst[y * dstW + x] = new Color32(
                        (byte)(r / count), (byte)(g / count),
                        (byte)(b / count), (byte)(a / count));
                }
            }
            return dst;
        }

        // ───────────────────────── Pixel Processing ─────────────────────────

        // Instance wrapper used by the export path (main thread, can access instance fields directly).
        private void ProcessPixels(Texture2D tex)
        {
            var sorted = zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();
            if (sorted.Count == 0) return;
            Color32[] pixels = tex.GetPixels32();
            ProcessPixelsArray(pixels, tex.width, tex.height,
                exclusionMask, maskWidth, maskHeight, sorted, edgeFeather, antiAliasCleanup);
            tex.SetPixels32(pixels);
        }

        // Static pure-computation method — safe to run on a background thread.
        // No Texture2D, no UnityEngine.Object API, only Mathf and Color math (both thread-safe).
        // originX/Y: crop offset in the full-resolution texture (0 for whole-texture processing).
        // fullW/H: full-resolution texture dimensions (0 = same as w/h, i.e. no crop).
        private static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            bool[] mask, int maskW, int maskH,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0)
        {
            if (fullW <= 0) fullW = w;
            if (fullH <= 0) fullH = h;

            int len = w * h;
            Color32[] originalPixels = new Color32[len];
            System.Array.Copy(pixels, originalPixels, len);

            foreach (var zone in sortedZones)
            {
                // 1. Build strength map using original pixel colors
                float[] strength = new float[len];
                for (int i = 0; i < len; i++)
                {
                    int x = i % w, y = i / w;
                    int xf = x + originX, yf = y + originY;
                    if (IsExcludedStatic(xf, yf, fullW, fullH, mask, maskW, maskH)) continue;
                    strength[i] = zone.GetMatchStrength((Color)originalPixels[i], xf, yf, fullW, fullH);
                }

                // 1b. Fill isolated holes: anti-aliased edge pixels often have low saturation
                //     and get missed by satConfidence, leaving isolated dots of the original color.
                //     If a zero-strength pixel is mostly surrounded by matched pixels, fill it in.
                FillSmallHoles(strength, w, h);

                // 1c. Boundary recovery: re-evaluate unmatched pixels adjacent to matched ones
                //     using the old fixed low-saturation threshold, giving correct gradual strength.
                if (antiAliasCleanup > 0)
                    RecoverBoundaryEdges(strength, w, h, originalPixels,
                        zone.sampleColor, zone.tolerance, zone.edgeSoftness, antiAliasCleanup);

                // 2. Gaussian blur for smooth edge transitions (constrained to edges)
                if (edgeFeather > 0.01f)
                {
                    float[] preBlur = (float[])strength.Clone();
                    strength = GaussianBlur(strength, w, h, edgeFeather);
                    ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * 2.5f));
                }

                // 3. Re-apply exclusion mask: blur can spread strength INTO excluded pixels
                if (mask != null)
                {
                    for (int i = 0; i < len; i++)
                    {
                        int x = i % w, y = i / w;
                        int xf = x + originX, yf = y + originY;
                        if (IsExcludedStatic(xf, yf, fullW, fullH, mask, maskW, maskH)) strength[i] = 0f;
                    }
                }

                // 4. Apply recoloring blended by strength
                for (int i = 0; i < len; i++)
                {
                    float s = strength[i];
                    if (s <= 0.001f) continue;
                    Color original = originalPixels[i];
                    Color32 recolored = RecolorPixel(original, zone.targetColor, zone.sampleColor, zone.valueBlend);
                    pixels[i] = s >= 0.999f ? recolored : Color32.Lerp(pixels[i], recolored, s);
                }
            }
        }

        private static bool IsExcludedStatic(int x, int y, int texW, int texH,
            bool[] mask, int maskW, int maskH)
        {
            if (mask == null) return false;
            int mx = Mathf.Clamp(x * maskW / texW, 0, maskW - 1);
            int my = Mathf.Clamp(y * maskH / texH, 0, maskH - 1);
            return mask[my * maskW + mx];
        }

        private static float[] GaussianBlur(float[] map, int w, int h, float sigma)
        {
            int radius = Mathf.CeilToInt(sigma * 2.5f);
            if (radius < 1) return map;

            // Build 1D kernel
            float[] kernel = new float[radius * 2 + 1];
            float sum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                sum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= sum;

            // Horizontal pass
            float[] temp = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float val = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int nx = Mathf.Clamp(x + k, 0, w - 1);
                        val += map[y * w + nx] * kernel[k + radius];
                    }
                    temp[y * w + x] = val;
                }
            }

            // Vertical pass
            float[] result = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float val = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int ny = Mathf.Clamp(y + k, 0, h - 1);
                        val += temp[ny * w + x] * kernel[k + radius];
                    }
                    result[y * w + x] = val;
                }
            }

            return result;
        }

        private static void ConstrainBlur(float[] blurred, float[] original, int w, int h, int radius)
        {
            // Prevent blur from bleeding into areas that had no nearby match.
            // If neither this pixel nor any neighbor within blur radius had
            // original strength > 0, zero out the blurred value.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (original[idx] > 0f) continue; // was already matched

                    bool hasNearby = false;
                    int yMin = Mathf.Max(0, y - radius);
                    int yMax = Mathf.Min(h - 1, y + radius);
                    int xMin = Mathf.Max(0, x - radius);
                    int xMax = Mathf.Min(w - 1, x + radius);

                    for (int ny = yMin; ny <= yMax && !hasNearby; ny++)
                    {
                        for (int nx = xMin; nx <= xMax && !hasNearby; nx++)
                        {
                            if (original[ny * w + nx] > 0f)
                                hasNearby = true;
                        }
                    }

                    if (!hasNearby)
                        blurred[idx] = 0f;
                }
            }
        }

        /// <summary>
        /// Morphological fill: zero-strength pixels surrounded by a majority of matched
        /// neighbours get filled in with the minimum neighbour strength.
        /// This removes isolated 1-2px dot artifacts caused by anti-aliased edge pixels
        /// whose saturation is too low to pass the satConfidence gate.
        /// Three passes allow filling up to 3px-wide gaps at anti-alias boundaries.
        /// </summary>
        private static void FillSmallHoles(float[] strength, int w, int h)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                float[] filled = (float[])strength.Clone();

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (strength[idx] > 0f) continue;

                        int matched = 0;
                        int total = 0;
                        float minNeighbour = 1f;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                total++;
                                float ns = strength[ny * w + nx];
                                if (ns > 0f)
                                {
                                    matched++;
                                    if (ns < minNeighbour) minNeighbour = ns;
                                }
                            }
                        }

                        // Fill if at least 4 of the (up to 8) neighbours are matched
                        if (matched >= 4 && total >= 4)
                            filled[idx] = minNeighbour;
                    }
                }

                System.Array.Copy(filled, strength, strength.Length);
            }
        }

        /// <summary>
        /// Boundary recovery: for unmatched pixels adjacent to at least one matched pixel,
        /// re-evaluate the colour match using the original fixed low-saturation threshold
        /// (satMin=0.02, satRamp=0.08). This gives correct gradual strength values for
        /// anti-alias edge pixels that the strict dynamic satMin rejected.
        /// Each pass extends recovery by one more pixel outward from the matched boundary.
        /// The adjacency requirement prevents AO/shadow bleed in interior regions.
        /// </summary>
        private static void RecoverBoundaryEdges(
            float[] strength, int w, int h,
            Color32[] pixels, Color sampleColor, float tolerance,
            float edgeSoftness, int passes)
        {
            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            for (int pass = 0; pass < passes; pass++)
            {
                float[] updated = (float[])strength.Clone();

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (strength[idx] > 0f) continue;

                        // Check if adjacent to at least one matched pixel
                        bool hasMatchedNeighbor = false;
                        for (int dy = -1; dy <= 1 && !hasMatchedNeighbor; dy++)
                            for (int dx = -1; dx <= 1 && !hasMatchedNeighbor; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                if (strength[ny * w + nx] > 0f) hasMatchedNeighbor = true;
                            }

                        if (!hasMatchedNeighbor) continue;

                        // Re-evaluate this pixel with the relaxed fixed saturation threshold
                        float relaxed = GetRelaxedMatchStrength(
                            (Color)pixels[idx], sH, sS, tolerance, edgeSoftness);
                        if (relaxed > 0f)
                            updated[idx] = relaxed;
                    }
                }

                System.Array.Copy(updated, strength, strength.Length);
            }
        }

        /// <summary>
        /// Colour match using the original fixed low saturation threshold (satMin=0.02/satRamp=0.08).
        /// Used only for boundary pixels adjacent to already-matched regions.
        /// </summary>
        private static float GetRelaxedMatchStrength(
            Color pixelColor, float sH, float sS,
            float tolerance, float edgeSoftness)
        {
            float pH, pS, pV;
            Color.RGBToHSV(pixelColor, out pH, out pS, out pV);

            // Fixed low saturation threshold — matches original algorithm before dynamic satMin
            float satConfidence = Mathf.Clamp01((pS - 0.02f) / 0.08f);

            float hDist = Mathf.Abs(pH - sH);
            if (hDist > 0.5f) hDist = 1f - hDist;

            // Boundary pixels are expected to have reduced saturation from AA blending.
            // Use a lower sDist weight (0.15 vs 0.15 in main match) to allow recovery
            // of edge pixels whose saturation dropped due to alpha compositing.
            float sDist = Mathf.Abs(pS - sS);
            float dist = hDist + sDist * 0.15f;

            if (dist >= tolerance) return 0f;

            float softRange = tolerance * edgeSoftness;
            float hardRange = tolerance - softRange;

            float strength;
            if (softRange < 0.0001f)
                strength = 1f;
            else if (dist <= hardRange)
                strength = 1f;
            else
                strength = 1f - (dist - hardRange) / softRange;

            return strength * satConfidence;
        }

        private static Color32 RecolorPixel(Color original, Color target, Color sample, float valueBlend)
        {
            float oH, oS, oV;
            float tH, tS, tV;
            float sH, sS, sV;
            Color.RGBToHSV(original, out oH, out oS, out oV);
            Color.RGBToHSV(target,   out tH, out tS, out tV);
            Color.RGBToHSV(sample,   out sH, out sS, out sV);

            // Preserve saturation ratio: antialias boundary pixels keep their relative saturation
            float newS = (sS > 0.001f) ? Mathf.Clamp01(oS * tS / sS) : tS;

            float newV = Mathf.Lerp(tV, oV, valueBlend);

            Color result = Color.HSVToRGB(tH, newS, newV);
            result.a = original.a;
            return result;
        }

        // ───────────────────────── Export ─────────────────────────

        private void DrawExportSection()
        {
            exportFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(exportFoldout, Localization.Export);
            if (!exportFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            if (sourceTexture == null)
            {
                GUI.enabled = false;
            }

            saveAsNewFile = EditorGUILayout.Toggle(Localization.SaveAsNewFile, saveAsNewFile);
            if (saveAsNewFile)
            {
                newFileName = EditorGUILayout.TextField(Localization.FileName, newFileName);
            }

            if (GUILayout.Button(Localization.ApplyAndSave, GUILayout.Height(32)))
            {
                ApplyRecolor();
            }

            GUI.enabled = true;
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void ApplyRecolor()
        {
            if (sourceTexture == null || !IsReadable(sourceTexture))
            {
                EditorUtility.DisplayDialog(Localization.Error, Localization.TextureReadError, Localization.OK);
                return;
            }

            string srcPath = AssetDatabase.GetAssetPath(sourceTexture);
            if (string.IsNullOrEmpty(srcPath))
            {
                EditorUtility.DisplayDialog(Localization.Error, Localization.PathNotFound, Localization.OK);
                return;
            }

            // Load at full original resolution directly from the file on disk,
            // bypassing Unity's TextureImporter maxTextureSize / compression settings.
            // (sourceTexture.GetPixels32() returns the *imported* resolution which may be
            //  scaled down to 2048 or lower depending on importer settings.)
            byte[] srcBytes = File.ReadAllBytes(srcPath);
            var fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!fullTex.LoadImage(srcBytes))
            {
                DestroyImmediate(fullTex);
                EditorUtility.DisplayDialog(Localization.Error, Localization.TextureLoadError, Localization.OK);
                return;
            }

            ProcessPixels(fullTex);
            fullTex.Apply();

            byte[] pngData = fullTex.EncodeToPNG();
            DestroyImmediate(fullTex);

            string outputPath;
            if (saveAsNewFile)
            {
                string dir = Path.GetDirectoryName(srcPath);
                string safeName = string.IsNullOrWhiteSpace(newFileName) ? "recolored" : newFileName;
                // Security: prevent path traversal by taking only the filename portion
                safeName = Path.GetFileName(safeName);
                // Strip characters that are invalid in file names
                foreach (char c in Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(c.ToString(), "_");
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "recolored";
                outputPath = Path.Combine(dir, safeName + ".png");

                if (File.Exists(outputPath))
                {
                    if (!EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.FileExistsConfirm(outputPath), Localization.Overwrite, Localization.Cancel))
                    {
                        return;
                    }
                }
            }
            else
            {
                outputPath = Path.ChangeExtension(srcPath, ".png");
                if (!EditorUtility.DisplayDialog(Localization.Confirm,
                    Localization.OverwriteConfirm, Localization.Overwrite, Localization.Cancel))
                {
                    return;
                }
            }

            File.WriteAllBytes(outputPath, pngData);
            AssetDatabase.Refresh();

            Debug.Log($"[VACC] Saved: {outputPath}");
            EditorUtility.DisplayDialog(Localization.Complete, Localization.Saved(outputPath), Localization.OK);
        }

        // ───────────────────────── Utility ─────────────────────────

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

        private void RebuildMaskOverlay(int width, int height)
        {
            if (exclusionMask == null)
            {
                if (maskOverlayTexture != null)
                {
                    DestroyImmediate(maskOverlayTexture);
                    maskOverlayTexture = null;
                }
                return;
            }

            if (maskOverlayTexture == null || maskOverlayTexture.width != width || maskOverlayTexture.height != height)
            {
                if (maskOverlayTexture != null)
                    DestroyImmediate(maskOverlayTexture);
                maskOverlayTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                maskOverlayTexture.filterMode = FilterMode.Point;
            }

            var overlayPixels = new Color32[width * height];
            var excluded = new Color32(255, 60, 60, 80);  // semi-transparent red
            var clear = new Color32(0, 0, 0, 0);

            for (int i = 0; i < overlayPixels.Length; i++)
            {
                int x = i % width;
                int y = i / width;
                overlayPixels[i] = IsExcluded(x, y, width, height) ? excluded : clear;
            }

            maskOverlayTexture.SetPixels32(overlayPixels);
            maskOverlayTexture.Apply();
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
        }

        // ───────────────────────── Phase 2: Diff texture ─────────────────────────

        private void BuildDiffTexture(Texture2D before, Texture2D after)
        {
            if (before == null || after == null) return;
            int w = before.width, h = before.height;
            if (diffTexture == null || diffTexture.width != w || diffTexture.height != h)
            {
                if (diffTexture != null) DestroyImmediate(diffTexture);
                diffTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }

            Color32[] a = before.GetPixels32();
            Color32[] b = after.GetPixels32();
            Color32[] d = new Color32[w * h];
            var highlight = new Color32(255, 220, 0, 160);
            var clear     = new Color32(0, 0, 0, 0);

            for (int i = 0; i < d.Length; i++)
            {
                int diff = Mathf.Abs(a[i].r - b[i].r)
                         + Mathf.Abs(a[i].g - b[i].g)
                         + Mathf.Abs(a[i].b - b[i].b);
                d[i] = diff > 10 ? highlight : clear;
            }

            diffTexture.SetPixels32(d);
            diffTexture.Apply();
        }

        // ───────────────────────── Phase 3: Mask Undo ────────────────────────────

        private void PushMaskUndo()
        {
            bool[] snapshot = exclusionMask != null ? (bool[])exclusionMask.Clone() : null;
            _undoMaskHistory.Add(snapshot);
            if (_undoMaskHistory.Count > UndoMaskLimit)
                _undoMaskHistory.RemoveAt(0);
        }

        private void UndoMaskStep()
        {
            if (_undoMaskHistory.Count == 0) return;
            exclusionMask = _undoMaskHistory[_undoMaskHistory.Count - 1];
            _undoMaskHistory.RemoveAt(_undoMaskHistory.Count - 1);
            maskDirty = true;
            previewDirty = true;
            Repaint();
        }

        // ───────────────────────── Phase 4: Mask Persistence ────────────────────

        private string MaskSessionKey()
        {
            if (sourceTexture == null) return null;
            string path = AssetDatabase.GetAssetPath(sourceTexture);
            return string.IsNullOrEmpty(path) ? null : "VACC_Mask_" + path;
        }

        private void SaveMaskToSession()
        {
            string key = MaskSessionKey();
            if (key == null) return;

            if (exclusionMask == null || exclusionMask.Length == 0)
            {
                SessionState.EraseString(key);
                return;
            }

            // Store dimensions + bitpacked mask
            int len = exclusionMask.Length;
            int byteLen = (len + 7) / 8;
            byte[] packed = new byte[byteLen + 8]; // 4bytes W + 4bytes H + data

            System.Buffer.BlockCopy(System.BitConverter.GetBytes(maskWidth),  0, packed, 0, 4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(maskHeight), 0, packed, 4, 4);

            for (int i = 0; i < len; i++)
            {
                if (exclusionMask[i])
                    packed[8 + i / 8] |= (byte)(1 << (i % 8));
            }

            SessionState.SetString(key, System.Convert.ToBase64String(packed));
        }

        private void RestoreMaskFromSession()
        {
            string key = MaskSessionKey();
            if (key == null) return;

            string encoded = SessionState.GetString(key, null);
            if (string.IsNullOrEmpty(encoded)) return;

            try
            {
                byte[] packed = System.Convert.FromBase64String(encoded);
                if (packed.Length < 9) return;

                int w = System.BitConverter.ToInt32(packed, 0);
                int h = System.BitConverter.ToInt32(packed, 4);
                if (w <= 0 || h <= 0) return;

                int len = w * h;
                bool[] mask = new bool[len];
                for (int i = 0; i < len; i++)
                    mask[i] = (packed[8 + i / 8] & (1 << (i % 8))) != 0;

                maskWidth  = w;
                maskHeight = h;
                exclusionMask = mask;
                maskDirty = true;
            }
            catch
            {
                // 破損データは無視
                SessionState.EraseString(key);
            }
        }

        // ───────────────────────── Phase 5: Presets ──────────────────────────────

        private static string ProjectPresetFolder
            => System.IO.Path.Combine(Application.dataPath, "VACCPresets");

        private static string UserPresetFolder
            => System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "VACCPresets");

        private string ActivePresetFolder
            => presetStorageProject ? ProjectPresetFolder : UserPresetFolder;

        private void DrawPresetsSection()
        {
            presetsFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(presetsFoldout, Localization.Presets);
            if (!presetsFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(Localization.PresetTips, MessageType.Info);

            // 保存先切り替え
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(presetStorageProject, Localization.PresetStorageProject, EditorStyles.miniButtonLeft) && !presetStorageProject)
                presetStorageProject = true;
            if (GUILayout.Toggle(!presetStorageProject, Localization.PresetStorageUser, EditorStyles.miniButtonRight) && presetStorageProject)
                presetStorageProject = false;
            EditorGUILayout.EndHorizontal();

            // 保存
            EditorGUILayout.BeginHorizontal();
            presetSaveName = EditorGUILayout.TextField(Localization.PresetName, presetSaveName);
            if (GUILayout.Button(Localization.SavePreset, GUILayout.Width(48)))
                SavePreset(presetSaveName);
            EditorGUILayout.EndHorizontal();

            // インポート / エクスポート
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.ExportJson))
                ExportPresetJson();
            if (GUILayout.Button(Localization.ImportJson))
                ImportPresetJson();
            EditorGUILayout.EndHorizontal();

            // 一覧
            string folder = ActivePresetFolder;
            string[] files = System.IO.Directory.Exists(folder)
                ? System.IO.Directory.GetFiles(folder, "*.json")
                : System.Array.Empty<string>();

            if (files.Length == 0)
            {
                EditorGUILayout.HelpBox(Localization.NoPresets, MessageType.None);
            }
            else
            {
                presetScrollPos = EditorGUILayout.BeginScrollView(presetScrollPos, GUILayout.MaxHeight(120));
                foreach (string file in files)
                {
                    string pname = System.IO.Path.GetFileNameWithoutExtension(file);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(pname, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button(Localization.LoadPreset, GUILayout.Width(40)))
                        LoadPreset(file);
                    if (GUILayout.Button("×", GUILayout.Width(22)))
                    {
                        if (EditorUtility.DisplayDialog(Localization.Confirm,
                            Localization.DeletePresetConfirm(pname), Localization.OK, Localization.Cancel))
                        {
                            System.IO.File.Delete(file);
                            AssetDatabase.Refresh();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        private void SavePreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Preset";
            // ファイル名サニタイズ
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");

            string folder = ActivePresetFolder;
            if (!System.IO.Directory.Exists(folder))
                System.IO.Directory.CreateDirectory(folder);

            var data = new VACCPresetData
            {
                name = name,
                zones = new List<ColorZone>(zones),
                edgeFeather = edgeFeather
            };
            string json = JsonUtility.ToJson(data, true);
            string path = System.IO.Path.Combine(folder, name + ".json");
            System.IO.File.WriteAllText(path, json);
            if (presetStorageProject) AssetDatabase.Refresh();
        }

        private void LoadPreset(string filePath)
        {
            if (!System.IO.File.Exists(filePath)) return;
            string json = System.IO.File.ReadAllText(filePath);
            var data = JsonUtility.FromJson<VACCPresetData>(json);
            if (data == null) return;

            zones = data.zones ?? new List<ColorZone>();
            edgeFeather = data.edgeFeather;
            previewDirty = true;
        }

        private void ExportPresetJson()
        {
            string path = EditorUtility.SaveFilePanel(
                Localization.ExportJson, "", "VACC_preset", "json");
            if (string.IsNullOrEmpty(path)) return;
            SavePresetToPath(path);
        }

        private void ImportPresetJson()
        {
            string path = EditorUtility.OpenFilePanel(
                Localization.ImportJson, "", "json");
            if (string.IsNullOrEmpty(path)) return;
            LoadPreset(path);
        }

        private void SavePresetToPath(string path)
        {
            var data = new VACCPresetData
            {
                name = System.IO.Path.GetFileNameWithoutExtension(path),
                zones = new List<ColorZone>(zones),
                edgeFeather = edgeFeather
            };
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(data, true));
        }

        // ───────────────────────── Phase 8: Batch Apply ──────────────────────────

        private void DrawBatchSection()
        {
            batchFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(batchFoldout, Localization.BatchApply);
            if (!batchFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(Localization.BatchHint, MessageType.Info);

            // テクスチャ一覧
            batchScrollPos = EditorGUILayout.BeginScrollView(batchScrollPos, GUILayout.MaxHeight(120));
            int removeIdx = -1;
            for (int i = 0; i < batchTextures.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                batchTextures[i] = (Texture2D)EditorGUILayout.ObjectField(
                    batchTextures[i], typeof(Texture2D), false);
                if (GUILayout.Button("×", GUILayout.Width(22)))
                    removeIdx = i;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (removeIdx >= 0) batchTextures.RemoveAt(removeIdx);

            if (GUILayout.Button(Localization.AddBatchTexture))
                batchTextures.Add(null);

            EditorGUI.BeginDisabledGroup(batchTextures.Count == 0 || zones.Count == 0);
            if (GUILayout.Button(Localization.BatchApplyAndSave, GUILayout.Height(28)))
                RunBatchApply();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        private void RunBatchApply()
        {
            int success = 0;
            for (int i = 0; i < batchTextures.Count; i++)
            {
                var tex = batchTextures[i];
                if (tex == null) continue;

                EditorUtility.DisplayProgressBar(
                    Localization.BatchProgress,
                    tex.name,
                    (float)i / batchTextures.Count);

                if (!IsReadable(tex)) EnableReadWrite(tex);
                if (!IsReadable(tex)) continue;

                string srcPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(srcPath)) continue;

                byte[] srcBytes = System.IO.File.ReadAllBytes(srcPath);
                var fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!fullTex.LoadImage(srcBytes)) { DestroyImmediate(fullTex); continue; }

                ProcessPixels(fullTex);
                fullTex.Apply();
                byte[] pngData = fullTex.EncodeToPNG();
                DestroyImmediate(fullTex);

                string dir      = System.IO.Path.GetDirectoryName(srcPath);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(srcPath) + "_recolored";
                string outPath  = System.IO.Path.Combine(dir, baseName + ".png");
                System.IO.File.WriteAllBytes(outPath, pngData);
                success++;
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(Localization.Complete, Localization.BatchComplete(success), Localization.OK);
        }
    }
}
