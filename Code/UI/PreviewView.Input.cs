// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: プレビュー上の入力処理(ズーム/パン/マスクペイント/FF シード/スポイト)。
    internal partial class PreviewView
    {
        // ───────────────────────── Preview Input ─────────────────────────

        private void HandlePreviewGlobalInput(Rect previewRect, float scale)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.ScrollWheel:
                    if (isInRect && e.control && Mathf.Abs(e.delta.y) > ZoomEpsilon)
                    {
                        // スクロール量を蓄積し、閾値ぶんたまるごとに 1 ストップだけ進める。
                        // 拡大↔縮小で向きが変わったら貯金をリセットし、逆方向の繰り越しが
                        // 残って一瞬反対に動くのを防ぐ（即応性を保つ）。
                        if (_zoomScrollAccum != 0f && Mathf.Sign(e.delta.y) != Mathf.Sign(_zoomScrollAccum))
                            _zoomScrollAccum = 0f;
                        _zoomScrollAccum += e.delta.y;

                        // 閾値を超えたぶんのストップ数。端数は次イベントへ繰り越す。
                        int steps = (int)(_zoomScrollAccum / ZoomScrollStepThreshold);
                        if (steps != 0)
                        {
                            _zoomScrollAccum -= steps * ZoomScrollStepThreshold;

                            float oldZoom = previewZoom;
                            // 上スクロール(delta.y<0 ⇒ steps<0)で拡大。
                            bool zoomIn = steps < 0;
                            int count = Mathf.Abs(steps);
                            float newZoom = oldZoom;
                            for (int i = 0; i < count; i++)
                                newZoom = StepZoom(newZoom, zoomIn, ComputeMaxZoom(scale));

                            if (previewTexture != null && Mathf.Abs(newZoom - oldZoom) > 0.0001f)
                            {
                                if (newZoom <= 1f + ZoomEpsilon)
                                {
                                    // 等倍(100%)以下はフィット表示なのでスクロールを原点へ戻す。
                                    // マウス中心アンカー補正はズームイン/アウトでマウス位置が
                                    // 一致しない限り往復で打ち消されず残差が残る。パンは zoom>1
                                    // 限定のため、残ると画像が枠から数 px ずれたまま直せない。
                                    _previewScrollPos = Vector2.zero;
                                }
                                else
                                {
                                    Vector2 mouseInImage = e.mousePosition - new Vector2(previewRect.x, previewRect.y);
                                    _previewScrollPos += mouseInImage * (newZoom / oldZoom - 1f);
                                    _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                                    _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                                }
                            }

                            previewZoom = newZoom;
                            _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                            // ズーム比が変わるとピクセル/ソース比も変わるため、
                            // 古い詳細プレビューは整合しなくなる。破棄して再生成を待つ。
                            _detailView.InvalidateDisplay();
                            _host.RequestRepaint();
                        }

                        // ストップが動かなくてもイベントは消費し、外側スクロールビューが
                        // 動かないようにする（蓄積中も含め Ctrl+スクロールはここで完結）。
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
                        // 「掴んで動かす」操作なので、ドラッグ量と画像の移動量を 1:1 にする。
                        // _previewScrollPos はコンテンツ座標(=ズーム後の displayW/H 空間)で持ち、
                        // この単位は画面ピクセルと等価。よって e.delta(画面 px)をそのまま引けば、
                        // カーソル下に掴んだ点が常にカーソルへ追従する（ズーム倍率に依らず一定）。
                        // 以前は e.delta に previewZoom を掛けて「1 ストロークで全幅横断」を狙って
                        // いたが、高ズームほど画像がカーソルの何倍も飛んで感度が高すぎたため廃止。
                        _previewScrollPos -= e.delta;
                        _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                        _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                        _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                        // パンで詳細クロップ位置が変わるので古い詳細プレビューを破棄。
                        _detailView.InvalidateDisplay();
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
                            ? IrocaColors.BrushCursorInclude
                            : IrocaColors.BrushCursorExclude;
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
            int targetZoneIndex = -1;
            int activeZoneIndex = _host._maskView.activeMaskTarget;
            if (zones != null && activeZoneIndex >= 0 && activeZoneIndex < zones.Count)
            {
                var activeZone = zones[activeZoneIndex];
                if (activeZone.enabled && activeZone.mode == SelectionMode.ColorPick && activeZone.useFloodFill)
                    targetZoneIndex = activeZoneIndex;
            }

            if (targetZoneIndex < 0)
            {
                for (int i = 0; i < zones.Count; i++)
                {
                    var zone = zones[i];
                    if (!zone.enabled || zone.mode != SelectionMode.ColorPick || !zone.useFloodFill) continue;
                    targetZoneIndex = i;
                    break;
                }
            }

            bool hasFloodFill = targetZoneIndex >= 0;

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    // 自動アンカリングが既定なので、通常クリックはパン/検分に使えるよう温存し、
                    // シード(任意の上書き=その塊だけ残す)は Shift+クリックでのみ設定する。
                    if (hasFloodFill && e.button == 0 && e.shift && isInRect && !e.control && !e.alt)
                    {
                        float u = (e.mousePosition.x - previewRect.x) / previewRect.width;
                        float v = 1f - (e.mousePosition.y - previewRect.y) / previewRect.height;
                        u = Mathf.Clamp01(u);
                        v = Mathf.Clamp01(v);

                        // 1 つ目の対象ゾーン更新の直前に Undo を登録する（記録されるのは更新前の seedUV）。
                        var newSeed = new Vector2(u, v);
                        bool changed = false;
                        var zone = zones[targetZoneIndex];
                        if (zone.seedUV != newSeed)
                        {
                            Undo.RecordObject(_host, "Set Flood Fill Seed");
                            zone.seedUV = newSeed;
                            changed = true;
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
                    if (hasFloodFill && isInRect && e.shift)
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

        // ───────────────────────── Eyedropper（プレビュー直接スポイト） ─────────────────────────

        // プレビュー上のクリックで、武装中ゾーンのサンプルカラーを実テクスチャ画素から取得する。
        // 一発取得したら自動で武装解除する（one-shot）。マスクペイント中は呼ばれない。
        private void HandleEyedropperInput(Rect previewRect, int srcW, int srcH)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    // 素のクリックのみ受ける（修飾キー付きは別操作なので拾わない）。
                    if (e.button == 0 && isInRect && !e.shift && !e.control && !e.alt)
                    {
                        float u = Mathf.Clamp01((e.mousePosition.x - previewRect.x) / previewRect.width);
                        float v = Mathf.Clamp01(1f - (e.mousePosition.y - previewRect.y) / previewRect.height);
                        if (SampleTrueSourceColor(u, v, srcW, srcH, out Color picked))
                        {
                            // 武装ゾーンは id で解決する（並べ替え・削除で index がずれても正しいゾーンに入る）。
                            var zone = _host.FindZoneById(_host.EyedropperZoneId);
                            if (zone != null)
                            {
                                if (zone.sampleColor != picked || !zone.sampleColorSet)
                                {
                                    Undo.RecordObject(_host, "Sample Color");
                                    zone.sampleColor = picked;
                                    zone.sampleColorSet = true;
                                    // 主サンプル変更で陳腐化する内部サンプルを破棄（ColorField と同じ挙動）。
                                    if (zone.extraSamples != null && zone.extraSamples.Count > 0)
                                        zone.extraSamples.Clear();
                                    previewDirty = true;
                                }
                            }
                            // one-shot: 取得したら武装解除。
                            _host.EyedropperZoneId = null;
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
                    if (isInRect)
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Link);
                    break;
            }
        }

        // u,v(0-1, v は下端=0)を実フル解像度ソース画素にマップして色を返す。
        // _trueSourcePixels は GetPixels32 由来で行 0 = 画像下端。取得不能なら false。
        private bool SampleTrueSourceColor(float u, float v, int srcW, int srcH, out Color color)
        {
            color = Color.white;
            var px = _trueSourcePixels;
            if (px == null || srcW <= 0 || srcH <= 0 || px.Length < srcW * srcH) return false;
            int x = Mathf.Clamp(Mathf.FloorToInt(u * srcW), 0, srcW - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(v * srcH), 0, srcH - 1);
            Color32 c = px[y * srcW + x];
            // サンプルカラーはマッチング基準(HSV)に使い α は無関係。スウォッチを不透明にするため a=1。
            color = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
            return true;
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
    }
}
