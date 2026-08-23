// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// プレビュー矩形内のスクリーン座標・UV・ソース画素座標の相互変換（単一の正）。
    /// 規約: UV は下原点（v=0 が画像下端）。IMGUI のスクリーン y は下向き正なので
    /// スクリーン⇔UV で v を反転する。画素は GetPixels32 順（行 0 = 画像下端）。
    /// スポイト・シード設定・マスクペイント・AI 提案クリックとそれらのオーバーレイ描画が
    /// 全てここを通る。ここが壊れると「クリックした場所と違う画素を拾う」のに、
    /// 処理系のテストには一切かからない — そのため headless ハーネス（--previewcoords）から
    /// 直接検証する。変換をインライン再実装しないこと。
    /// </summary>
    internal static class PreviewCoords
    {
        /// <summary>スクリーン座標 → UV（下原点、0..1 クランプ）。</summary>
        public static Vector2 ScreenToUv(Vector2 screenPos, Rect previewRect)
        {
            return new Vector2(
                Mathf.Clamp01((screenPos.x - previewRect.x) / previewRect.width),
                Mathf.Clamp01(1f - (screenPos.y - previewRect.y) / previewRect.height));
        }

        /// <summary>UV（下原点）→ スクリーン座標（オーバーレイ描画用）。</summary>
        public static Vector2 UvToScreen(Vector2 uv, Rect previewRect)
        {
            return new Vector2(
                previewRect.x + uv.x * previewRect.width,
                previewRect.y + (1f - uv.y) * previewRect.height);
        }

        /// <summary>
        /// UV（下原点）→ ソース画素座標（行 0 = 下端、floor + クランプ）。
        /// GetPixels32 配列へのアクセスは index = y * w + x。
        /// </summary>
        public static void UvToPixel(float u, float v, int w, int h, out int x, out int y)
        {
            x = Mathf.Clamp(Mathf.FloorToInt(u * w), 0, w - 1);
            y = Mathf.Clamp(Mathf.FloorToInt(v * h), 0, h - 1);
        }
    }
}
