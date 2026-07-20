// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案のズームイン再推論(純計算)。
    ///
    /// SAM のデコーダは常に 256² ロジット固定のため、全体推論では 1 セル =
    /// texLong/256 px(4096² で 16px)。bbox 長辺が数十 px の小パーツはロジット数セル分
    /// しかなく形状表現が原理的に不可能(粒度切替・境界スナップでは救えない)。
    /// 対策 = 第 1 段(全体)の提案からクリック成分の大きさを測り、小さければクリック周辺の
    /// 正方形クロップを再エンコード・再デコードして実効解像度を上げる。
    /// 計測(dev_safe/ml/zoom_infer_spike*.py)でバンダナ三角(36-58px @4096²)の IoU が
    /// 0.04-0.10 → 0.93-0.97 に改善し、クロップ辺 256〜1024 の選択に鈍感なことを確認済み。
    ///
    /// 座標規約: マスク・画素・クロップ矩形はすべて下原点(GetPixels32 順)。
    /// </summary>
    internal static class SamZoomOps
    {
        /// <summary>クロップ辺の下限(これ未満に絞っても利得がなく、文脈が減るだけ)。</summary>
        public const int MinCropSide = 256;

        /// <summary>クリック成分 bbox 長辺に対するクロップ辺の倍率(対象+周辺文脈)。</summary>
        public const int ZoomFactor = 4;

        /// <summary>クロップ位置のスナップ粒度の分母(side/8 グリッド)。近接クリックで
        /// 同一矩形に揃え、クロップ埋め込みキャッシュを効かせるため。</summary>
        public const int PositionGridDenom = 8;

        /// <summary>
        /// クリック画素を含む連結成分の bbox 長辺(px)。クリックがマスク外なら
        /// マスク全体の bbox、マスクが空なら 0 を返す。
        /// 計測はズーム判定に必要な精度に限る: bbox がテクスチャ長辺/ZoomFactor
        /// (=ズームがスキップされる大きさ)に達したら打ち切り、その時点の値を返す
        /// (巨大成分でもフラッドフィルが窓内で止まり、コスト・メモリが有界になる)。
        /// </summary>
        public static int ClickComponentBBoxLong(bool[] maskBottomUp, int w, int h,
                                                 int clickX, int clickY)
        {
            if (maskBottomUp == null || maskBottomUp.Length != w * h) return 0;
            clickX = Mathf.Clamp(clickX, 0, w - 1);
            clickY = Mathf.Clamp(clickY, 0, h - 1);

            if (maskBottomUp[clickY * w + clickX])
                return ComponentBBoxLongWindowed(maskBottomUp, w, h, clickX, clickY);

            // クリックがマスク外(境界ぎわ等): マスク全体の bbox
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (!maskBottomUp[row + x]) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < minX) return 0;
            return Mathf.Max(maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>クリック画素から 4 近傍フラッドフィルで成分 bbox 長辺を測る。
        /// 探索域はクリック中心 ±cap の窓に限定(窓縁到達 = bbox ≥ cap = ズーム対象外が
        /// 確定するため、それ以上の正確さは不要)。</summary>
        static int ComponentBBoxLongWindowed(bool[] mask, int w, int h, int sx, int sy)
        {
            int cap = (Mathf.Max(w, h) + ZoomFactor - 1) / ZoomFactor;
            int wx0 = Mathf.Max(0, sx - cap), wx1 = Mathf.Min(w - 1, sx + cap);
            int wy0 = Mathf.Max(0, sy - cap), wy1 = Mathf.Min(h - 1, sy + cap);
            int ww = wx1 - wx0 + 1, wh = wy1 - wy0 + 1;

            var visited = new bool[ww * wh];
            var stack = new System.Collections.Generic.Stack<int>(256);
            visited[(sy - wy0) * ww + (sx - wx0)] = true;
            stack.Push(sy * w + sx); // フル座標のまま積む(近傍判定が単純になる)
            int minX = sx, maxX = sx, minY = sy, maxY = sy;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int y = i / w, x = i - y * w;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (maxX - minX >= cap || maxY - minY >= cap)
                    return Mathf.Max(maxX - minX + 1, maxY - minY + 1); // ズーム対象外確定

                // 4 近傍(窓内のみ)
                if (x > wx0 && mask[i - 1] && !visited[(y - wy0) * ww + (x - 1 - wx0)])
                { visited[(y - wy0) * ww + (x - 1 - wx0)] = true; stack.Push(i - 1); }
                if (x < wx1 && mask[i + 1] && !visited[(y - wy0) * ww + (x + 1 - wx0)])
                { visited[(y - wy0) * ww + (x + 1 - wx0)] = true; stack.Push(i + 1); }
                if (y > wy0 && mask[i - w] && !visited[(y - 1 - wy0) * ww + (x - wx0)])
                { visited[(y - 1 - wy0) * ww + (x - wx0)] = true; stack.Push(i - w); }
                if (y < wy1 && mask[i + w] && !visited[(y + 1 - wy0) * ww + (x - wx0)])
                { visited[(y + 1 - wy0) * ww + (x - wx0)] = true; stack.Push(i + w); }
            }
            return Mathf.Max(maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>
        /// ズームインクロップ矩形(下原点)を導出する。false = ズーム利得なし
        /// (成分が空/クロップ辺がテクスチャ長辺以上=解像度が上がらない)。
        /// 辺は bbox×ZoomFactor 以上の最小 2 冪(下限 MinCropSide)、位置はクリック中心を
        /// side/8 グリッドへスナップし境界内へクランプする。
        /// </summary>
        public static bool TryDeriveCropRect(int bboxLong, int clickX, int clickY, int w, int h,
                                             out int x0, out int y0, out int side)
        {
            x0 = y0 = side = 0;
            if (bboxLong <= 0) return false;
            long s = MinCropSide;
            while (s < (long)bboxLong * ZoomFactor) s *= 2;
            if (s >= Mathf.Max(w, h)) return false; // 全体推論と実効解像度が変わらない
            side = (int)System.Math.Min(s, Mathf.Min(w, h));
            int grid = Mathf.Max(1, side / PositionGridDenom);
            x0 = (clickX - side / 2) / grid * grid;
            y0 = (clickY - side / 2) / grid * grid;
            x0 = Mathf.Clamp(x0, 0, w - side);
            y0 = Mathf.Clamp(y0, 0, h - side);
            return true;
        }

        /// <summary>下原点画素列からクロップ矩形を切り出す(下原点のまま)。</summary>
        public static Color32[] ExtractCrop(Color32[] pixelsBottomUp, int w, int h,
                                            int x0, int y0, int side)
        {
            var crop = new Color32[side * side];
            for (int y = 0; y < side; y++)
                System.Array.Copy(pixelsBottomUp, (y0 + y) * w + x0, crop, y * side, side);
            return crop;
        }

        /// <summary>クロップマスク(下原点 side²)をフル寸(下原点 w*h)へ貼り戻す。
        /// クロップ外は false。true の画素数も返す(areaFrac 再計算用)。</summary>
        public static bool[] PasteCrop(bool[] cropMaskBottomUp, int side, int w, int h,
                                       int x0, int y0, out int trueCount)
        {
            var full = new bool[w * h];
            int count = 0;
            for (int y = 0; y < side; y++)
            {
                int srcRow = y * side, dstRow = (y0 + y) * w + x0;
                for (int x = 0; x < side; x++)
                {
                    if (!cropMaskBottomUp[srcRow + x]) continue;
                    full[dstRow + x] = true;
                    count++;
                }
            }
            trueCount = count;
            return full;
        }
    }
}
