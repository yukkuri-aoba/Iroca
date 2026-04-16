using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        private Texture2D previewTexture;
        private float previewZoom = 1f;
        private bool previewDirty = true;
        private Vector2 previewScrollPos;

        // プレビュー生成
        private const int PreviewMaxSize = 512;

        // 非同期プレビュー状態
        private volatile bool _previewGenerating;
        private volatile bool _asyncCancelled;
        private volatile int _asyncGeneration;
        private Color32[] _pendingProcessedDisplay;
        private Color32[] _pendingRawDisplay;
        private int _pendingPrevW, _pendingPrevH;
        private double _lastDirtyTime;
        private const double PreviewDebounceSeconds = 0.2;

        // 変更前/変更後比較
        private Texture2D rawPreviewTexture;
        private bool comparisonMode;
        private bool diffMode;
        private Texture2D diffTexture;

        // ─────────────────────── プレビュー ─────────────────────────

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

            // バックグラウンドプレビュータスクからの結果を適用（Texture2D API: メインスレッドのみ）
            if (_pendingProcessedDisplay != null)
                ApplyPendingPreview();

            // バックグラウンド詳細プレビュータスクからの結果を適用
            if (_pendingDetailProcessed != null)
                ApplyPendingDetailPreview();

            if (previewDirty)
            {
                // 最新の変更時刻を記録。非同期タスクは
                // ユーザーが PreviewDebounceSeconds 間イン операショ終了した後にのみ開始。
                // これにより、スライダーをドラッグ中メインスレッドが完全に自由になります。
                _lastDirtyTime = UnityEditor.EditorApplication.timeSinceStartup;
                _asyncGeneration++; // 進行中のタスクを無効化
                // ここで _previewGenerating をリセット「しないでください: タスクが実行中の場合、
                // 生成番号の不一致を検出し、try/finally を介して自己をクリーンアップ。
                // メインスレッドからフラグをリセットするとタスク関が実行中に
                // 古いタスクの finally が新しいタスクのフラグを上書きするレースが発生します。
                Repaint(); // 次のフレーム確認をスケジュール
                previewDirty = false;
            }
            else if (!_previewGenerating &&
                     _lastDirtyTime > 0 &&
                     (GUIUtility.hotControl == 0 ||
                      (UnityEditor.EditorApplication.timeSinceStartup - _lastDirtyTime)
                          >= PreviewDebounceSeconds))
            {
                // コントロールが解放された瞬間 or デバウンス経過後に生成開始
                _lastDirtyTime = 0;
                GeneratePreviewAsync();
            }
            else if (_lastDirtyTime > 0 || _previewGenerating)
            {
                Repaint(); // ポーリング継続: デバウンスまたはバックグラウンドタスクを待機
            }

            // マスクオーバーレイを個別に再構築（軽量、ペイント中も安全）
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

            // ズームラベル (Ctrl+スクロールで変更)
            EditorGUILayout.LabelField(
                new GUIContent(
                    string.Format(Localization.ZoomLabel, Mathf.RoundToInt(previewZoom * 100f)),
                    Localization.ZoomHint));

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

            // プレビュースケール（プレビューテクスチャサイズとソースサイズの比率）を計算
            int srcW = sourceTexture.width;
            int srcH = sourceTexture.height;
            float scale = (srcW > PreviewMaxSize || srcH > PreviewMaxSize)
                ? PreviewMaxSize / (float)Mathf.Max(srcW, srcH)
                : 1f;

            // 詳細モード: ディスプレイピクセル > ソースピクセル時にアクティブ（スケール < 1 かつズーム十分）
            bool detailActive = scale < 1f &&
                                previewZoom * scale >= DetailUpscaleThreshold &&
                                !comparisonMode;

            // 詳細プレビュー生成をポーリング
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

                // 詳細モードでは低解像度プレビューの上にフル解像度クロップをオーバーレイ
                if (detailActive && _detailPreviewTexture != null)
                {
                    // 低解像度ベースを描画（非表示領域のコンテキストを提供）
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    // 詳細クロップが占める画面レクトを計算
                    Rect detailScreenRect = ComputeDetailScreenRect(activePreviewRect, scale, srcW, srcH);
                    GUI.DrawTexture(detailScreenRect, _detailPreviewTexture,
                        ScaleMode.StretchToFill, false);

                    if (diffMode && _detailDiffTexture != null)
                        GUI.DrawTexture(detailScreenRect, _detailDiffTexture,
                            ScaleMode.StretchToFill, true);
                    else if (_detailMaskOverlayTexture != null)
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

            // プレビューレクトを格納して、次の詳細生成ティックで使用
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                _lastPreviewRect = activePreviewRect;

            // ズーム (Ctrl+スクロール) と Ctrl+Z は常にアクティブ（ペイントモード関係なし）
            HandlePreviewGlobalInput(activePreviewRect);

            // ブラシペイントはペイントモード ON の場合のみ
            if (maskFoldout && maskPaintActive)
                HandlePreviewPaintInput(activePreviewRect);
            else if (!maskPaintActive && previewZoom > 1f)
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
                            previewScrollPos += mouseInImage * (newZoom / oldZoom - 1f);
                            previewScrollPos.x = Mathf.Max(0f, previewScrollPos.x);
                            previewScrollPos.y = Mathf.Max(0f, previewScrollPos.y);
                        }

                        previewZoom = newZoom;
                        _lastDetailDirtyTime = UnityEditor.EditorApplication.timeSinceStartup;
                        _detailAsyncGeneration++;
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
                        // e.delta はウィンドウ座標系での移動量。ドラッグ方向にコンテンツを追随させる
                        previewScrollPos -= e.delta;
                        previewScrollPos.x = Mathf.Max(0f, previewScrollPos.x);
                        previewScrollPos.y = Mathf.Max(0f, previewScrollPos.y);
                        _lastDetailDirtyTime = UnityEditor.EditorApplication.timeSinceStartup;
                        _detailAsyncGeneration++;
                        e.Use();
                        Repaint();
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

            switch (e.GetTypeForControl(controlId))
            {
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

        // ───────────────────────── Preview Async Generation ─────────────────────────

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
                    // すべての重い計算はバックグラウンドスレッドで実行されます。
                    // ここでは Unity Object API は呼ばれない — 純粋な C# 計算のみ。
                    Color32[] pixels = (Color32[])srcPixels.Clone();
                    ProcessPixelsArray(pixels, srcW, srcH, maskSnapshot, mW, mH, zonesSnapshot, feather, aaCleanup);

                    // 新しいタスクに置き換えられた — 結果を静かに破棄。
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
                    // 常にフラグをリセットして再描画をスケジュール、メインスレッド
                    // ポーリングループが反応できるように — キャンセルされたり例外が発生しても。
                    // delayCall はメインスレッドで実行されるため、バックグラウンドスレッドから
                    // 安全に呼び出せる (Repaint() の直接呼び出しより確実)。
                    _previewGenerating = false;
                    UnityEditor.EditorApplication.delayCall += Repaint;
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

        // ───────────────────────── Diff Texture ─────────────────────────

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

        // ───────────────────────── Utility ─────────────────────────

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
    }
}
