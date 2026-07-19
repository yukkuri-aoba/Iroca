// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の座標変換(純計算)。
    /// 系の定義:
    ///   - UV: プレビュー入力の (u,v)。v は下原点(PreviewView.Input の規約)。
    ///   - 元画像画素: 上原点 (x,y)。SAM/Python 側テストと同じ向き。
    ///   - 1024 空間: 長辺 1024 リサイズ後の上原点座標。デコーダの point_coords はこの系。
    /// </summary>
    internal static class SamCoordMapper
    {
        /// <summary>UV(下原点) → 元画像の上原点連続画素座標。</summary>
        public static void UvToOrigTopDown(float u, float v, int texW, int texH,
                                           out float x, out float y)
        {
            x = Mathf.Clamp01(u) * texW;
            y = (1f - Mathf.Clamp01(v)) * texH;
        }

        /// <summary>元画像の上原点画素座標 → 1024 リサイズ空間(ResizeLongestSide.apply_coords と同値)。</summary>
        public static void OrigTopDownTo1024(float x, float y, int texW, int texH,
                                             out float x1024, out float y1024)
        {
            SamImageOps.GetResizedSize(texW, texH, out int newW, out int newH);
            x1024 = x * (newW / (float)texW);
            y1024 = y * (newH / (float)texH);
        }

        /// <summary>UV(下原点) → 1024 空間。プレビュークリックからデコーダ入力への一括変換。</summary>
        public static void UvTo1024(float u, float v, int texW, int texH,
                                    out float x1024, out float y1024)
        {
            UvToOrigTopDown(u, v, texW, texH, out float x, out float y);
            OrigTopDownTo1024(x, y, texW, texH, out x1024, out y1024);
        }
    }
}
