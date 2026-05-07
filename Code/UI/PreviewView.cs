using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// メインプレビュー描画、比較 / 差分モード、ズーム、プレビュー生成ジョブの起動、
    /// 表示用 Texture の所有を担当する。詳細プレビューは DetailPreviewView を保持する補助形式。
    /// </summary>
    [System.Serializable]
    internal class PreviewView
    {
        // ─── シリアライズ対象（UI 状態） ─────────────────────────────
        public float previewZoom = 1f;
        public bool comparisonMode;
        public bool diffMode;

        // ─── 実行時状態（NonSerialized） ──────────────────────────
        [System.NonSerialized] public Texture2D previewTexture;
        [System.NonSerialized] public Texture2D rawPreviewTexture;
        [System.NonSerialized] public Texture2D diffTexture;
        [System.NonSerialized] public bool previewDirty = true;
        [System.NonSerialized] private Vector2 _previewScrollPos;

        // 非同期プレビュー状態
        [System.NonSerialized] private readonly PreviewJob<Color32[]> _previewJob = new PreviewJob<Color32[]>();
        [System.NonSerialized] private Color32[] _pendingProcessedDisplay;
        [System.NonSerialized] private Color32[] _pendingRawDisplay;
        [System.NonSerialized] private int _pendingPrevW, _pendingPrevH;
        [System.NonSerialized] private double _lastDirtyTime;
        private const double PreviewDebounceSeconds = 0.2;

        // ソースピクセルキャッシュ（テクスチャが変わったときのみ再取得）
        [System.NonSerialized] private Texture2D _cachedSourceTexture;
        [System.NonSerialized] private Color32[] _cachedSrcPixels;
        [System.NonSerialized] private Color32[] _cachedRawDisplay;
        [System.NonSerialized] private int _cachedSrcW, _cachedSrcH;
        [System.NonSerialized] private int _cachedPrevW, _cachedPrevH;

        // 詳細プレビューは PreviewView の補助。
        [System.NonSerialized] private DetailPreviewView _detailView;

        [System.NonSerialized] private VACCWindow _host;

        public void Initialize(VACCWindow host)
        {
            _host = host;
            _detailView ??= new DetailPreviewView();
            _detailView.Initialize(host);
        }

        public DetailPreviewView Detail => _detailView;
        public bool IsPreviewJobRunning => _previewJob.IsRunning;

        public void MarkDirty() => previewDirty = true;

        /// <summary>
        /// テクスチャが変わったときにソースピクセルキャッシュを無効化する。
        /// </summary>
        public void InvalidateSourceCache()
        {
            _cachedSourceTexture = null;
            _cachedSrcPixels = null;
            _cachedRawDisplay = null;
        }

        public void Dispose()
        {
            _previewJob.Dispose();
            _detailView?.Dispose();
            TextureSlot.Release(ref previewTexture);
            TextureSlot.Release(ref rawPreviewTexture);
            TextureSlot.Release(ref diffTexture);
        }

        // ─────────────────────── プレビュー ─────────────────────────

        public void Draw()
        {
            EditorGUILayout.LabelField(Localization.Preview, EditorStyles.boldLabel);

            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null)
            {
                EditorGUILayout.HelpBox(Localization.SetTexture, MessageType.Info);
                return;
            }

            if (!VACCWindow.IsReadable(sourceTexture))
                return;

            // バックグラウンドプレビュータスクからの結果を適用（Texture2D API: メインスレッドのみ）
            if (_pendingProcessedDisplay != null)
                ApplyPendingPreview();

            // バックグラウンド詳細プレビュータスクからの結果を適用
            if (_detailView.HasPendingResult)
                _detailView.ApplyPendingResult();

            if (previewDirty)
            {
                _lastDirtyTime = EditorApplication.timeSinceStartup;
                _previewJob.Cancel();
                _host.RequestRepaint();
                previewDirty = false;
            }
            else if (!_previewJob.IsRunning &&
                     _lastDirtyTime > 0 &&
                     (GUIUtility.hotControl == 0 ||
                      (EditorApplication.timeSinceStartup - _lastDirtyTime)
                          >= PreviewDebounceSeconds))
            {
                _lastDirtyTime = 0;
                GeneratePreviewAsync();
            }
            else if (_lastDirtyTime > 0 || _previewJob.IsRunning)
            {
                _host.RequestRepaint();
            }

            var maskView = _host._maskView;

            // マスクオーバーレイを個別に再構築（軽量、ペイント中も安全）
            if (maskView.maskDirty && previewTexture != null)
            {
                maskView.RebuildMaskOverlay(previewTexture.width, previewTexture.height);
                maskView.maskDirty = false;
            }

            if (previewTexture == null)
            {
                if (_previewJob.IsRunning)
                    EditorGUILayout.HelpBox(Localization.GeneratingPreview, MessageType.None);
                return;
            }

            if (_previewJob.IsRunning)
                EditorGUILayout.LabelField(Localization.GeneratingPreview);

            EditorGUILayout.LabelField(
                new GUIContent(
                    string.Format(Localization.ZoomLabel, Mathf.RoundToInt(previewZoom * 100f)),
                    Localization.ZoomHint));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(comparisonMode, new GUIContent(Localization.ComparisonMode, Localization.ComparisonModeTooltip), EditorStyles.miniButtonLeft) != comparisonMode)
            {
                comparisonMode = !comparisonMode;
                if (comparisonMode) diffMode = false;
            }
            if (GUILayout.Toggle(diffMode, new GUIContent(Localization.DiffMode, Localization.DiffModeTooltip), EditorStyles.miniButtonRight) != diffMode)
            {
                diffMode = !diffMode;
                if (diffMode) comparisonMode = false;
            }
            EditorGUILayout.EndHorizontal();

            int srcW = sourceTexture.width;
            int srcH = sourceTexture.height;
            float scale = (srcW > VACCConsts.Preview.MaxSize || srcH > VACCConsts.Preview.MaxSize)
                ? VACCConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH)
                : 1f;

            // 詳細モード: ディスプレイピクセル > ソースピクセル時にアクティブ
            bool detailActive = scale < 1f &&
                                previewZoom * scale >= DetailPreviewView.DetailUpscaleThreshold &&
                                !comparisonMode;

            // 詳細プレビュー生成をポーリング
            if (detailActive)
            {
                if (!_detailView.detailJob.IsRunning &&
                    _detailView.lastDetailDirtyTime > 0 &&
                    (EditorApplication.timeSinceStartup - _detailView.lastDetailDirtyTime)
                        >= DetailPreviewView.DetailDebounceSeconds &&
                    _detailView.lastPreviewRect.width > 0)
                {
                    _detailView.lastDetailDirtyTime = 0;
                    Color32[] srcPixels = sourceTexture.GetPixels32();
                    _detailView.GenerateDetailPreviewAsync(srcW, srcH, srcPixels, scale, previewZoom, _previewScrollPos, _detailView.lastPreviewRect);
                }
                else if (_detailView.lastDetailDirtyTime > 0 || _detailView.detailJob.IsRunning)
                {
                    _host.RequestRepaint();
                }
            }

            float displayW = previewTexture.width  * previewZoom;
            float displayH = previewTexture.height * previewZoom;

            float maxViewH = Mathf.Min(displayH, previewTexture.height) + 16f;
            int panelCount = (comparisonMode && rawPreviewTexture != null) ? 2 : 1;
            float maxViewW = Mathf.Min(displayW * panelCount + (panelCount - 1) * 8f,
                previewTexture.width * panelCount + (panelCount - 1) * 8f) + 16f;

            Vector2 prevScroll = _previewScrollPos;
            _previewScrollPos = EditorGUILayout.BeginScrollView(
                _previewScrollPos,
                GUILayout.Height(maxViewH),
                GUILayout.MaxWidth(maxViewW));
            if (_previewScrollPos != prevScroll)
            {
                _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                _detailView.detailJob.Cancel();
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
                if (maskView.maskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, maskView.maskOverlayTexture, ScaleMode.StretchToFill, true);
                if (maskView.zoneMaskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, maskView.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
                EditorGUILayout.EndVertical();

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                activePreviewRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));

                if (detailActive && _detailView.detailPreviewTexture != null)
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    Rect detailScreenRect = _detailView.ComputeDetailScreenRect(activePreviewRect, scale, previewZoom, _previewScrollPos, srcW, srcH);
                    GUI.DrawTexture(detailScreenRect, _detailView.detailPreviewTexture,
                        ScaleMode.StretchToFill, false);

                    if (diffMode && _detailView.detailDiffTexture != null)
                        GUI.DrawTexture(detailScreenRect, _detailView.detailDiffTexture,
                            ScaleMode.StretchToFill, true);
                    else if (_detailView.detailMaskOverlayTexture != null)
                        GUI.DrawTexture(detailScreenRect, _detailView.detailMaskOverlayTexture,
                            ScaleMode.StretchToFill, true);
                }
                else
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    if (diffMode && diffTexture != null)
                        GUI.DrawTexture(activePreviewRect, diffTexture, ScaleMode.StretchToFill, true);
                    else
                    {
                        if (maskView.maskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, maskView.maskOverlayTexture, ScaleMode.StretchToFill, true);
                        if (maskView.zoneMaskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, maskView.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
                    }
                }

                if (detailActive && _detailView.detailJob.IsRunning)
                    EditorGUI.LabelField(
                        new Rect(activePreviewRect.x + 4, activePreviewRect.y + 4, 300, 20),
                        Localization.GeneratingDetailPreview);
            }

            // Flood Fill シード点をプレビュー上に × 印としてオーバーレイ描画
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                DrawFloodFillSeedOverlay(activePreviewRect);

            // プレビューレクトを格納して、次の詳細生成ティックで使用
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                _detailView.lastPreviewRect = activePreviewRect;

            HandlePreviewGlobalInput(activePreviewRect);

            if (!maskView.maskPaintActive)
                HandleFloodFillSeedInput(activePreviewRect);

            if (maskView.maskFoldout && maskView.maskPaintActive)
                HandlePreviewPaintInput(activePreviewRect);
            else if (!maskView.maskPaintActive && previewZoom > 1f)
                HandlePreviewPanInput(activePreviewRect);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
        }

        // ───────────────────────── Preview Input ─────────────────────────

        private void HandlePreviewGlobalInput(Rect previewRect)
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
                        float newZoom = Mathf.Clamp(oldZoom * Mathf.Pow(1.1f, -e.delta.y / 3f), 0.25f, 4f);

                        if (previewTexture != null && Mathf.Abs(newZoom - oldZoom) > 0.0001f)
                        {
                            Vector2 mouseInImage = e.mousePosition - new Vector2(previewRect.x, previewRect.y);
                            _previewScrollPos += mouseInImage * (newZoom / oldZoom - 1f);
                            _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                            _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                        }

                        previewZoom = newZoom;
                        _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                        _detailView.detailJob.Cancel();
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

            }
        }

        private void HandlePreviewPanInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && isInRect)
                    {
                        GUIUtility.hotControl = controlId;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        _previewScrollPos -= e.delta;
                        _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                        _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                        _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                        _detailView.detailJob.Cancel();
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    if (isInRect)
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Pan);
                    break;
            }
        }

        private void HandlePreviewPaintInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            var maskView = _host._maskView;

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && isInRect)
                    {
                        GUIUtility.hotControl = controlId;
                        maskView.isPainting = true;
                        maskView._maskStrokeStarted = false;
                        maskView.lastPaintUV = -Vector2.one;
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (maskView.isPainting && GUIUtility.hotControl == controlId)
                    {
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        maskView.isPainting = false;
                        // ストローク終了: bool[] バッファを _session.maskState に書き戻す。
                        maskView.EndStroke();
                        maskView.lastPaintUV = -Vector2.one;
                        previewDirty = true;
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.Repaint:
                    if (maskView.isPainting && isInRect)
                    {
                        float brushPixels = maskView.brushSize * previewZoom;
                        var cursorColor = maskView.brushEraseMode
                            ? VACCColors.BrushCursorInclude
                            : VACCColors.BrushCursorExclude;
                        float r = brushPixels * 0.5f;
                        EditorGUI.DrawRect(
                            new Rect(e.mousePosition.x - r, e.mousePosition.y - r, r * 2f, r * 2f),
                            cursorColor);
                    }
                    break;
            }
        }

        private void HandleFloodFillSeedInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            var zones = _host.Session.zones;
            bool hasFloodFill = false;
            foreach (var z in zones)
                if (z.enabled && z.mode == SelectionMode.ColorPick && z.useFloodFill)
                { hasFloodFill = true; break; }

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (hasFloodFill && e.button == 0 && isInRect && !e.control && !e.alt)
                    {
                        float u = (e.mousePosition.x - previewRect.x) / previewRect.width;
                        float v = 1f - (e.mousePosition.y - previewRect.y) / previewRect.height;
                        u = Mathf.Clamp01(u);
                        v = Mathf.Clamp01(v);

                        bool changed = false;
                        foreach (var z in zones)
                        {
                            if (z.enabled && z.mode == SelectionMode.ColorPick && z.useFloodFill)
                            {
                                z.seedUV = new Vector2(u, v);
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            previewDirty = true;
                            GUIUtility.hotControl = controlId;
                            e.Use();
                            _host.RequestRepaint();
                        }
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    if (hasFloodFill && isInRect)
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Link);
                    break;
            }
        }

        private void DrawFloodFillSeedOverlay(Rect previewRect)
        {
            foreach (var z in _host.Session.zones)
            {
                if (!z.enabled || z.mode != SelectionMode.ColorPick || !z.useFloodFill) continue;
                if (z.seedUV.x < 0f) continue;

                float sx = previewRect.x + z.seedUV.x * previewRect.width;
                float sy = previewRect.y + (1f - z.seedUV.y) * previewRect.height;

                const float armLen = 7f;
                const float thickness = 2f;
                var color = new Color(1f, 0.85f, 0f, 0.9f);
                EditorGUI.DrawRect(new Rect(sx - armLen, sy - thickness * 0.5f, armLen * 2f, thickness), color);
                EditorGUI.DrawRect(new Rect(sx - thickness * 0.5f, sy - armLen, thickness, armLen * 2f), color);
            }
        }

        private void PaintAtScreenPos(Vector2 screenPos, Rect previewRect)
        {
            var maskView = _host._maskView;
            // ストローク開始時に1度だけ Unity Undo を登録する。
            maskView.BeginStroke();

            float u = (screenPos.x - previewRect.x) / previewRect.width;
            float v = 1f - (screenPos.y - previewRect.y) / previewRect.height;
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);

            var currentUV = new Vector2(u, v);

            if (maskView.lastPaintUV.x >= 0f)
            {
                float dist = Vector2.Distance(maskView.lastPaintUV, currentUV);
                maskView.EnsureMasks();
                float step = (maskView.maskWidth > 0) ? 1f / maskView.maskWidth : 0.001f;
                if (dist > step)
                {
                    int steps = Mathf.CeilToInt(dist / step);
                    for (int i = 1; i < steps; i++)
                    {
                        Vector2 lerped = Vector2.Lerp(maskView.lastPaintUV, currentUV, (float)i / steps);
                        maskView.PaintMask(lerped);
                    }
                }
            }

            maskView.PaintMask(currentUV);
            maskView.lastPaintUV = currentUV;
        }

        // ───────────────────────── Preview Async Generation ─────────────────────────

        private void GeneratePreviewAsync()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !VACCWindow.IsReadable(sourceTexture)) return;

            int srcW = sourceTexture.width;
            int srcH = sourceTexture.height;

            float scale = 1f;
            if (srcW > VACCConsts.Preview.MaxSize || srcH > VACCConsts.Preview.MaxSize)
                scale = VACCConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH);
            int prevW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int prevH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            Color32[] srcPixels;
            Color32[] rawDisplay;
            if (_cachedSourceTexture == sourceTexture &&
                _cachedSrcPixels != null &&
                _cachedRawDisplay != null &&
                _cachedSrcW == srcW && _cachedSrcH == srcH &&
                _cachedPrevW == prevW && _cachedPrevH == prevH)
            {
                srcPixels = _cachedSrcPixels;
                rawDisplay = _cachedRawDisplay;
            }
            else
            {
                srcPixels = sourceTexture.GetPixels32();
                rawDisplay = scale < 1f
                    ? PixelProcessor.BoxDownsample(srcPixels, srcW, srcH, prevW, prevH, scale)
                    : srcPixels;

                _cachedSourceTexture = sourceTexture;
                _cachedSrcPixels     = srcPixels;
                _cachedRawDisplay    = rawDisplay;
                _cachedSrcW          = srcW;
                _cachedSrcH          = srcH;
                _cachedPrevW         = prevW;
                _cachedPrevH         = prevH;
            }

            var maskSnap = _host._maskView.BuildSnapshot();

            var session = _host.Session;
            var zonesSnapshot = session.zones
                .Where(z => z.enabled)
                .OrderBy(z => z.layerIndex)
                .Select(z => z.Clone())
                .ToList();
            float feather = session.edgeFeather;
            int aaCleanup = session.antiAliasCleanup;
            int hfPasses = session.holeFillPasses;
            int hfMinNeighbors = session.holeFillMinNeighbors;
            float rSatMin = session.relaxedSatMin;
            float rSatRamp = session.relaxedSatRamp;
            bool useDecontam = session.useDecontamination;
            int decontamRadius = session.decontaminationRadius;

            var srcPixelsForTask  = srcPixels;
            var rawDisplayForTask = rawDisplay;
            float scaleForTask = scale;
            int prevWForTask = prevW;
            int prevHForTask = prevH;

            _previewJob.Schedule(
                work: token =>
                {
                    Color32[] pixels = (Color32[])srcPixelsForTask.Clone();
                    PixelProcessor.ProcessPixelsArray(pixels, srcW, srcH, maskSnap, zonesSnapshot, feather, aaCleanup,
                        hfPasses, hfMinNeighbors, rSatMin, rSatRamp,
                        0, 0, 0, 0, token,
                        useDecontam, decontamRadius);

                    return scaleForTask < 1f
                        ? PixelProcessor.BoxDownsample(pixels, srcW, srcH, prevWForTask, prevHForTask, scaleForTask)
                        : pixels;
                },
                apply: processedDisplay =>
                {
                    _pendingRawDisplay       = rawDisplayForTask;
                    _pendingProcessedDisplay = processedDisplay;
                    _pendingPrevW            = prevWForTask;
                    _pendingPrevH            = prevHForTask;
                    _host.RequestRepaint();
                });
        }

        private void ApplyPendingPreview()
        {
            var processed = _pendingProcessedDisplay;
            var raw       = _pendingRawDisplay;
            int w = _pendingPrevW;
            int h = _pendingPrevH;
            _pendingProcessedDisplay = null;
            _pendingRawDisplay       = null;

            if (processed == null || raw == null) return;

            TextureSlot.Resize(ref previewTexture, w, h);
            previewTexture.SetPixels32(processed);
            previewTexture.Apply();

            TextureSlot.Resize(ref rawPreviewTexture, w, h);
            rawPreviewTexture.SetPixels32(raw);
            rawPreviewTexture.Apply();

            BuildDiffTexture(rawPreviewTexture, previewTexture);

            var maskView = _host._maskView;
            if (maskView.maskOverlayTexture == null
                || maskView.maskOverlayTexture.width != w
                || maskView.maskOverlayTexture.height != h
                || maskView.maskDirty)
            {
                maskView.RebuildMaskOverlay(w, h);
                maskView.maskDirty = false;
            }

            // Invalidate detail preview so it regenerates at the new crop
            _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
            _detailView.detailJob.Cancel();
        }

        // ───────────────────────── Diff Texture ─────────────────────────

        private void BuildDiffTexture(Texture2D before, Texture2D after)
        {
            if (before == null || after == null) return;
            int w = before.width, h = before.height;
            TextureSlot.Resize(ref diffTexture, w, h);

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
    }
}
