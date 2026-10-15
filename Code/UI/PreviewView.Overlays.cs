// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: プレビュー上の目印・輪郭の描画(シード十字/スポイト位置の菱形/UV の島/AI 提案の待ち)。
    internal partial class PreviewView
    {
        /// <summary>
        /// 連続領域モードのシード位置を十字で描く。色はゾーン識別色（マスクオーバーレイと
        /// 同じ黄金比生成）にして、複数ゾーンがシードを持っていても対応が分かるようにする。
        /// 以前は全ゾーン同じ黄色で、どのシードがどのゾーンのものか読み取れなかった。
        /// </summary>
        private void DrawFloodFillSeedOverlay(Rect previewRect)
        {
            var zones = _host.Session.zones;
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                if (z == null || !z.enabled || z.mode != SelectionMode.ColorPick || !z.useFloodFill) continue;
                if (z.seedUV.x < 0f) continue;

                var sp = PreviewCoords.UvToScreen(z.seedUV, previewRect);
                DrawMarkerCross(sp.x, sp.y, ZoneMarkerColor(i));
            }
        }

        /// <summary>
        /// スポイトで色を取った位置を菱形で描く。自動調整はこの位置に AI マスク提案をかけて
        /// 証拠にするため、「どこを取ったか」が見えないと結果の当たり外れを説明できなかった。
        /// </summary>
        private void DrawSampleUvOverlay(Rect previewRect)
        {
            var zones = _host.Session.zones;
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                if (z == null || !z.enabled || !z.HasSampleUV) continue;

                var sp = PreviewCoords.UvToScreen(z.sampleUV, previewRect);
                DrawMarkerDiamond(sp.x, sp.y, ZoneMarkerColor(i));
            }
        }

        // UV の島の輪郭の描画用バッファ(線分の端点をスクリーン座標へ直したもの。毎回確保しない)。
        [System.NonSerialized] private Vector3[] _islandLineBuffer;

        /// <summary>
        /// メッシュの UV の島の輪郭を線で重ねる(マスク欄の「UV の島をプレビューに表示」)。
        /// 右クリックの「メッシュの形で」で当てる範囲がどこまでかを、選ぶ前に見せるため。
        /// テクスチャに焼くとズームで太ったり縮小で途切れたりするので、毎回スクリーン座標の線で描く
        /// (Handles の線は IMGUI のクリップに従うので、プレビュー枠の外へははみ出さない)。
        /// </summary>
        private void DrawMeshIslandOverlay(Rect previewRect)
        {
            if (!MeshPartSection.ShowIslands) return;
            var parts = _host._maskView?.MeshParts;
            var segments = parts != null && parts.HasMesh ? parts.IslandOutline() : null;
            if (segments == null || segments.Length < 2) return;

            GUI.BeginClip(previewRect);
            var local = new Rect(0f, 0f, previewRect.width, previewRect.height);
            DrawUvSegments(segments, local, ref _islandLineBuffer, IrocaColors.MeshIslandLine);
            GUI.EndClip();
        }

        /// <summary>
        /// UV の線分(端点の組)をスクリーン座標へ直し、影(右下へ 1px ずらした暗色) → 本体色の順に線で描く。
        /// UV の島の輪郭と Scene の強調の輪郭で共通。local は枠内座標の矩形なので、
        /// 必ず GUI.BeginClip と GUI.EndClip の間で呼ぶ。buffer は呼び出し側ごとに別のものを渡す(毎回確保しない)。
        /// </summary>
        private static void DrawUvSegments(Vector2[] segs, Rect local, ref Vector3[] buffer, Color lineColor)
        {
            if (buffer == null || buffer.Length != segs.Length)
                buffer = new Vector3[segs.Length];
            for (int i = 0; i < segs.Length; i++)
                buffer[i] = PreviewCoords.UvToScreen(segs[i], local);
            using (new Handles.DrawingScope(IrocaColors.MeshIslandLineShadow, Matrix4x4.Translate(new Vector3(1f, 1f, 0f))))
                Handles.DrawLines(buffer);
            using (new Handles.DrawingScope(lineColor))
                Handles.DrawLines(buffer);
        }

        /// <summary>ゾーン識別色（マスクオーバーレイと共通）を不透明寄りの Color で返す。</summary>
        private static Color ZoneMarkerColor(int zoneIndex)
        {
            Color32 c = MaskPaintView.OverlayColorForZone(zoneIndex);
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 0.95f);
        }

        // マーカーは明るい生地でも暗い生地でも沈まないよう、暗い縁取りの上に本体を重ねる
        // （AI 提案の反映待ちドットと同じ方針）。
        private static void DrawMarkerCross(float sx, float sy, Color color)
        {
            const float armLen = 7f;
            const float thickness = 2f;
            var outline = new Color(0f, 0f, 0f, 0.55f);
            EditorGUI.DrawRect(new Rect(sx - armLen - 1f, sy - thickness * 0.5f - 1f, (armLen + 1f) * 2f, thickness + 2f), outline);
            EditorGUI.DrawRect(new Rect(sx - thickness * 0.5f - 1f, sy - armLen - 1f, thickness + 2f, (armLen + 1f) * 2f), outline);
            EditorGUI.DrawRect(new Rect(sx - armLen, sy - thickness * 0.5f, armLen * 2f, thickness), color);
            EditorGUI.DrawRect(new Rect(sx - thickness * 0.5f, sy - armLen, thickness, armLen * 2f), color);
        }

        // 菱形は「幅を変えながら積む横線」で描く（IMGUI に多角形塗りが無いため）。
        // 十字（シード）と形で区別が付けば十分なので 5px 相当の粗さで足りる。
        private static void DrawMarkerDiamond(float sx, float sy, Color color)
        {
            const int half = 5;
            var outline = new Color(0f, 0f, 0f, 0.55f);
            for (int dy = -half - 1; dy <= half + 1; dy++)
            {
                int w = (half + 1) - Mathf.Abs(dy);
                if (w <= 0) continue;
                EditorGUI.DrawRect(new Rect(sx - w, sy + dy, w * 2f, 1f), outline);
            }
            for (int dy = -half + 1; dy <= half - 1; dy++)
            {
                int w = (half - 1) - Mathf.Abs(dy);
                if (w <= 0) continue;
                EditorGUI.DrawRect(new Rect(sx - w, sy + dy, w * 2f, 1f), color);
            }
        }

        // AI 提案の反映待ちクリック位置に目印を描く。推論が追いつくまで「押したのに未反映」の
        // 時間があるため、受理済みクリックを可視化して二度押し・押し忘れの混乱を防ぐ。
        // 先頭(処理中)は明るく、待ちの分は薄く描いて進行が分かるようにする。
        private void DrawAiSuggestPendingOverlay(Rect previewRect)
        {
            var maskView = _host._maskView;
            var ctl = maskView != null ? maskView.SuggestControllerIfCreated : null;
            if (ctl == null) return;
            var clicks = ctl.PendingClicks;
            if (clicks == null || clicks.Count == 0) return;

            for (int i = 0; i < clicks.Count; i++)
            {
                var sp = PreviewCoords.UvToScreen(clicks[i], previewRect);
                float sx = sp.x, sy = sp.y;
                float alpha = i == 0 ? 0.95f : 0.55f;
                const float half = 4f;
                // 明るい生地でも暗い生地でも沈まないよう、暗い縁取りの上に明色ドットを重ねる
                EditorGUI.DrawRect(
                    new Rect(sx - half - 1f, sy - half - 1f, (half + 1f) * 2f, (half + 1f) * 2f),
                    new Color(0f, 0f, 0f, alpha * 0.6f));
                EditorGUI.DrawRect(
                    new Rect(sx - half, sy - half, half * 2f, half * 2f),
                    new Color(0.3f, 0.85f, 1f, alpha));
            }
        }
    }
}
