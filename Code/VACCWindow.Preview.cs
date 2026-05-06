using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // プレビュー用テクスチャ・スクロール座標はキャッシュであり、再コンパイルで破棄されても
        // 次回OnGUIで再生成されるためSerializeFieldにしない。
        private Texture2D previewTexture;
        [SerializeField] private float previewZoom = 1f;
        private bool previewDirty = true;
        private Vector2 previewScrollPos;

        // プレビュー生成
        private const int PreviewMaxSize = 512;

        // 非同期プレビュー状態
        private volatile bool _previewGenerating;
        private volatile bool _asyncCancelled;
        private volatile int _asyncGeneration;
        private CancellationTokenSource _previewCts;
        private Color32[] _pendingProcessedDisplay;
        private Color32[] _pendingRawDisplay;
        private int _pendingPrevW, _pendingPrevH;
        private double _lastDirtyTime;
        private const double PreviewDebounceSeconds = 0.2;

        // ソースピクセルキャッシュ（テクスチャが変わったときのみ再取得）
        private Texture2D _cachedSourceTexture;
        private Color32[] _cachedSrcPixels;
        private Color32[] _cachedRawDisplay;
        private int _cachedSrcW, _cachedSrcH;
        private int _cachedPrevW, _cachedPrevH;

        // 変更前/変更後比較
        private Texture2D rawPreviewTexture;
        [SerializeField] private bool comparisonMode;
        [SerializeField] private bool diffMode;
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

            // スクロールビューの高さをソーステクスチャサイズに制限（ズーム時にレイアウトが崩れないよう）
            float maxViewH = Mathf.Min(displayH, previewTexture.height) + 16f;
            // スクロールビューの幅も同様に制限（比較モードは2パネル分を考慮）
            int panelCount = (comparisonMode && rawPreviewTexture != null) ? 2 : 1;
            float maxViewW = Mathf.Min(displayW * panelCount + (panelCount - 1) * 8f,
                previewTexture.width * panelCount + (panelCount - 1) * 8f) + 16f;

            Vector2 prevScroll = previewScrollPos;
            previewScrollPos = EditorGUILayout.BeginScrollView(
                previewScrollPos,
                GUILayout.Height(maxViewH),
                GUILayout.MaxWidth(maxViewW));
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
                if (zoneMaskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
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
                    else
                    {
                        if (maskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, maskOverlayTexture, ScaleMode.StretchToFill, true);
                        if (zoneMaskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
                    }
                }

                // Generating indicator for detail preview
                if (detailActive && _detailGenerating)
                    EditorGUI.LabelField(
                        new Rect(activePreviewRect.x + 4, activePreviewRect.y + 4, 300, 20),
                        Localization.GeneratingDetailPreview);
            }

            // Flood Fill シード点をプレビュー上に × 印としてオーバーレイ描画
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                DrawFloodFillSeedOverlay(activePreviewRect);

            // プレビューレクトを格納して、次の詳細生成ティックで使用
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                _lastPreviewRect = activePreviewRect;

            // ズーム (Ctrl+スクロール) と Ctrl+Z は常にアクティブ（ペイントモード関係なし）
            HandlePreviewGlobalInput(activePreviewRect);

            // Flood Fill シード点クリック（ペイントモード OFF のとき）
            if (!maskPaintActive)
                HandleFloodFillSeedInput(activePreviewRect);

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
                        var cursorColor = brushEraseMode
                            ? new Color(0, 1, 0, 0.5f)
                            : new Color(1, 0, 0, 0.5f);
                        // Handles.DrawSolidDisc は EditorWindow の ScrollView 内で
                        // GUIクリッピングや行列の不整合が発生するため、
                        // EditorGUI.DrawRect で充填正方形近似に置き換える。
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

            // GetControlID は常に呼ぶ（呼び出し順が毎フレーム一定でないと IMGUI が壊れる）
            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            // Flood Fill が有効なゾーンが存在しなければ入力処理はしない
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

                        // 有効な FloodFill ゾーン全員に適用（将来的に「選択中ゾーン」に絞ることも可）
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
                            Repaint();
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
            // Handles は EditorWindow の ScrollView 内で GUIClips を壊すため
            // EditorGUI.DrawRect による純粋な GUI 描画で + 印を描く
            foreach (var z in zones)
            {
                if (!z.enabled || z.mode != SelectionMode.ColorPick || !z.useFloodFill) continue;
                if (z.seedUV.x < 0f) continue;

                float sx = previewRect.x + z.seedUV.x * previewRect.width;
                float sy = previewRect.y + (1f - z.seedUV.y) * previewRect.height;

                const float armLen = 7f;
                const float thickness = 2f;
                var color = new Color(1f, 0.85f, 0f, 0.9f);
                // 横バー
                EditorGUI.DrawRect(new Rect(sx - armLen, sy - thickness * 0.5f, armLen * 2f, thickness), color);
                // 縦バー
                EditorGUI.DrawRect(new Rect(sx - thickness * 0.5f, sy - armLen, thickness, armLen * 2f), color);
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
                EnsureMasks();
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

            // 前回のプレビュータスクをキャンセル
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;

            _previewGenerating = true;
            int myGen = _asyncGeneration;

            int srcW = sourceTexture.width;
            int srcH = sourceTexture.height;

            float scale = 1f;
            if (srcW > PreviewMaxSize || srcH > PreviewMaxSize)
                scale = PreviewMaxSize / (float)Mathf.Max(srcW, srcH);
            int prevW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int prevH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            // ソースピクセルとダウンサンプル済みrawDisplayをキャッシュ
            // テクスチャが変わっていなければ GetPixels32() と BoxDownsample を省略
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
                // Capture all Unity-dependent data on the main thread before handing off.
                srcPixels = sourceTexture.GetPixels32();
                rawDisplay = scale < 1f
                    ? BoxDownsample(srcPixels, srcW, srcH, prevW, prevH, scale)
                    : srcPixels;

                _cachedSourceTexture = sourceTexture;
                _cachedSrcPixels     = srcPixels;
                _cachedRawDisplay    = rawDisplay;
                _cachedSrcW          = srcW;
                _cachedSrcH          = srcH;
                _cachedPrevW         = prevW;
                _cachedPrevH         = prevH;
            }

            var maskSnap = BuildMaskSnapshot();

            var zonesSnapshot = zones
                .Where(z => z.enabled)
                .OrderBy(z => z.layerIndex)
                .Select(CloneZone)
                .ToList();
            float feather = edgeFeather;
            int aaCleanup = antiAliasCleanup;
            int hfPasses = holeFillPasses;
            int hfMinNeighbors = holeFillMinNeighbors;
            float rSatMin = relaxedSatMin;
            float rSatRamp = relaxedSatRamp;
            bool useDecontam = useDecontamination;
            int decontamRadius = decontaminationRadius;

            // バックグラウンドスレッドに渡す前にローカル変数として固定
            var srcPixelsForTask  = srcPixels;
            var rawDisplayForTask = rawDisplay;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // すべての重い計算はバックグラウンドスレッドで実行されます。
                    // ここでは Unity Object API は呼ばれない — 純粋な C# 計算のみ。
                    Color32[] pixels = (Color32[])srcPixelsForTask.Clone();
                    ProcessPixelsArray(pixels, srcW, srcH, maskSnap, zonesSnapshot, feather, aaCleanup,
                        hfPasses, hfMinNeighbors, rSatMin, rSatRamp,
                        0, 0, 0, 0, token,
                        useDecontam, decontamRadius);

                    // キャンセルされた場合は結果を破棄
                    token.ThrowIfCancellationRequested();

                    // 新しいタスクに置き換えられた — 結果を静かに破棄。
                    // CancellationToken によるキャンセルは上の ThrowIfCancellationRequestedで捕捉される。
                    // myGen チェックは新準 Task に差し替えられた場合のガード。
                    if (myGen != _asyncGeneration)
                        return;

                    Color32[] processedDisplay = scale < 1f
                        ? BoxDownsample(pixels, srcW, srcH, prevW, prevH, scale)
                        : pixels;

                    _pendingRawDisplay       = rawDisplayForTask;
                    _pendingProcessedDisplay = processedDisplay;
                    _pendingPrevW            = prevW;
                    _pendingPrevH            = prevH;
                }
                catch (System.OperationCanceledException)
                {
                    // キャンセルされた — 結果を安全に破棄
                }
                finally
                {
                    // 現世代タスクの場合のみフラグをリセット。
                    // 古いタスク（myGen != _asyncGeneration）が新タスクの _previewGenerating=true を
                    // 上書きするレースを防ぐ。
                    if (myGen == _asyncGeneration)
                        _previewGenerating = false;
                    if (!_asyncCancelled)
                        UnityEditor.EditorApplication.delayCall += Repaint;
                }
            });
        }

        /// <summary>
        /// OnGUI 内から呪うまれても IMGUI のコントロール ID を壊さないよう、
        /// DestroyImmediate を EditorApplication.delayCall で次フレームに遅延・実行する。
        /// </summary>
        private static void ScheduleDestroy(Texture2D tex)
        {
            if (tex != null)
                EditorApplication.delayCall += () => { if (tex != null) DestroyImmediate(tex); };
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
                ScheduleDestroy(previewTexture);
                previewTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }
            previewTexture.SetPixels32(processed);
            previewTexture.Apply();

            if (rawPreviewTexture == null || rawPreviewTexture.width != w || rawPreviewTexture.height != h)
            {
                ScheduleDestroy(rawPreviewTexture);
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
                ScheduleDestroy(diffTexture);
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

        // ColorZoneのディープコピー。
        // ColorZone.Clone() が JsonUtility を使って自動的に全フィールドをコピーするため、
        // フィールド追加時にこのメソッドを更新する必要はなくなった。
        private static ColorZone CloneZone(ColorZone z) => z.Clone();

        private static Color32[] BoxDownsample(Color32[] src, int srcW, int srcH,
            int dstW, int dstH, float scale)
        {
            Color32[] dst = new Color32[dstW * dstH];
            // 合計値が int の範囲を超えないように long を使用。
            // 例: 8192x8192 の画像を 512x512 に縮小すると 1 ピクセル当たり 256 サンプル以上、
            // 合計が byte(255) * 256 = 65280 を超え、バッチサイズ次第では int でも桁数が
            // 増えるため安全側に倒す。
            for (int y = 0; y < dstH; y++)
            {
                int sy0 = Mathf.FloorToInt(y / scale);
                int sy1 = Mathf.Min(Mathf.CeilToInt((y + 1f) / scale) - 1, srcH - 1);
                for (int x = 0; x < dstW; x++)
                {
                    int sx0 = Mathf.FloorToInt(x / scale);
                    int sx1 = Mathf.Min(Mathf.CeilToInt((x + 1f) / scale) - 1, srcW - 1);
                    long r = 0, g = 0, b = 0, a = 0;
                    int count = 0;
                    for (int ky = sy0; ky <= sy1; ky++)
                        for (int kx = sx0; kx <= sx1; kx++)
                        {
                            var p = src[ky * srcW + kx];
                            r += p.r; g += p.g; b += p.b; a += p.a;
                            count++;
                        }
                    if (count <= 0) count = 1; // ゼロ除算ガード（理論上は到達しないが念のため）
                    dst[y * dstW + x] = new Color32(
                        (byte)(r / count), (byte)(g / count),
                        (byte)(b / count), (byte)(a / count));
                }
            }
            return dst;
        }
    }
}
