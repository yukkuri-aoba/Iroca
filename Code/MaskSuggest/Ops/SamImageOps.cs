// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案のエンコーダ入力前処理(純計算・Unity Editor 非依存)。
    /// SAM 規約: 長辺を 1024 にリサイズ → (x-mean)/std 正規化 → 右下 0 パディングで
    /// 1024x1024 の CHW float テンソルを作る。パディング領域は正規化後 0(= SamPredictor と同一)。
    ///
    /// 座標規約: 入力 Color32[] は GetPixels32 順(行 0 = 画像下端)。SAM は上原点画像を
    /// 前提とするため、ここで上下反転しながら読み出す。出力 CHW は行 0 = 画像上端。
    /// リサイズは縮小 = 面積平均(box)、拡大 = bilinear。float のまま量子化しない
    /// (uint8 経由の PIL/cv2 とは丸め差が出るが、点プロンプト推論には影響しない
    ///  ことを dev_safe/ml/onnx_parity.py の感度計測で確認して採用する)。
    /// </summary>
    internal static class SamImageOps
    {
        /// <summary>SAM 入力正方形の一辺。</summary>
        public const int InputSize = 1024;

        // SamPredictor の pixel_mean / pixel_std (RGB)
        static readonly float[] Mean = { 123.675f, 116.28f, 103.53f };
        static readonly float[] Std = { 58.395f, 57.12f, 57.375f };

        /// <summary>
        /// ResizeLongestSide.get_preprocess_shape と同値: 長辺を 1024 に合わせた
        /// リサイズ後サイズ(round(dim * scale + 0.5) 相当の int(x+0.5))。
        /// </summary>
        public static void GetResizedSize(int w, int h, out int newW, out int newH)
        {
            float scale = InputSize / (float)Mathf.Max(w, h);
            newW = (int)(w * scale + 0.5f);
            newH = (int)(h * scale + 0.5f);
        }

        /// <summary>
        /// 下原点 RGBA 画素列から [1,3,1024,1024] CHW float テンソル(平坦配列)を作る。
        /// token はキャンセル観測のみ(完走時の出力には影響しない)。
        /// </summary>
        public static float[] BuildEncoderInput(Color32[] pixelsBottomUp, int w, int h,
                                                System.Threading.CancellationToken token = default)
        {
            GetResizedSize(w, h, out int newW, out int newH);
            // resized: 上原点 RGB interleaved float(0..255 スケール)
            float[] resized = ResizeTopDown(pixelsBottomUp, w, h, newW, newH, token);

            var chw = new float[3 * InputSize * InputSize]; // パディング領域は 0 のまま
            // (c, y) を平坦化した行並列。各反復は自分の出力行だけに書き、読みは resized の
            // 読み取り専用参照のみなので決定的(出力ビット同一)。
            Parallel.For(0, 3 * newH, SamMaskRefine.MakeParallelOptions(token), i =>
            {
                int c = i / newH, y = i % newH;
                float mean = Mean[c], std = Std[c];
                int rowOff = y * newW * 3;
                int dstOff = c * InputSize * InputSize + y * InputSize;
                for (int x = 0; x < newW; x++)
                    chw[dstOff + x] = (resized[rowOff + x * 3 + c] - mean) / std;
            });
            return chw;
        }

        /// <summary>
        /// 下原点 Color32[] → 上原点 RGB interleaved float(0..255)。
        /// 縮小軸は面積平均、拡大軸は bilinear(align_corners=False 相当)。
        /// </summary>
        internal static float[] ResizeTopDown(Color32[] pixelsBottomUp, int w, int h, int newW, int newH,
                                              System.Threading.CancellationToken token = default)
        {
            var dst = new float[newW * newH * 3];
            bool downX = newW < w, downY = newH < h;

            // 行並列(4K で実測 300-400ms の単スレッドがクリティカルパスだった)。全パスとも
            // 「読みは読み取り専用・書きは自分の出力行のみ・行内の加算順序は不変」なので
            // 並列化しても出力ビット同一(a791ac5 と同じ論法)。
            var po = SamMaskRefine.MakeParallelOptions(token);
            if (newW == w && newH == h)
            {
                Parallel.For(0, newH, po, y =>
                {
                    int srcRow = (h - 1 - y) * w;
                    for (int x = 0; x < newW; x++)
                    {
                        var p = pixelsBottomUp[srcRow + x];
                        int o = (y * newW + x) * 3;
                        dst[o] = p.r; dst[o + 1] = p.g; dst[o + 2] = p.b;
                    }
                });
                return dst;
            }

            // 軸ごとの分離適用: まず X 軸 → 次に Y 軸(面積平均/bilinear どちらも分離可能)。
            var mid = new float[newW * h * 3];
            Parallel.For(0, h, po, ty =>
            {
                int srcRow = (h - 1 - ty) * w; // 上下反転しつつ読む
                int midRow = ty * newW * 3;
                if (downX) ResampleRowArea(pixelsBottomUp, srcRow, w, mid, midRow, newW);
                else ResampleRowBilinear(pixelsBottomUp, srcRow, w, mid, midRow, newW);
            });
            // mid は上原点なので、Y 軸処理での反転は不要。
            if (downY) ResampleColsArea(mid, h, newW, dst, newH, token);
            else ResampleColsBilinear(mid, h, newW, dst, newH, token);
            return dst;
        }

        static void ResampleRowArea(Color32[] src, int srcOff, int srcW, float[] dst, int dstOff, int dstW)
        {
            double scale = srcW / (double)dstW; // >1
            for (int x = 0; x < dstW; x++)
            {
                double s0 = x * scale, s1 = (x + 1) * scale;
                int i0 = (int)s0, i1 = System.Math.Min((int)System.Math.Ceiling(s1), srcW);
                double r = 0, g = 0, b = 0, wsum = 0;
                for (int i = i0; i < i1; i++)
                {
                    double cover = System.Math.Min(s1, i + 1.0) - System.Math.Max(s0, (double)i);
                    if (cover <= 0) continue;
                    var p = src[srcOff + i];
                    r += p.r * cover; g += p.g * cover; b += p.b * cover; wsum += cover;
                }
                int o = dstOff + x * 3;
                dst[o] = (float)(r / wsum); dst[o + 1] = (float)(g / wsum); dst[o + 2] = (float)(b / wsum);
            }
        }

        static void ResampleRowBilinear(Color32[] src, int srcOff, int srcW, float[] dst, int dstOff, int dstW)
        {
            double scale = srcW / (double)dstW; // <1 (拡大)
            for (int x = 0; x < dstW; x++)
            {
                double sx = (x + 0.5) * scale - 0.5;
                if (sx < 0) sx = 0; if (sx > srcW - 1) sx = srcW - 1;
                int x0 = (int)sx; int x1 = System.Math.Min(x0 + 1, srcW - 1);
                float f = (float)(sx - x0);
                var p0 = src[srcOff + x0]; var p1 = src[srcOff + x1];
                int o = dstOff + x * 3;
                dst[o] = p0.r + (p1.r - p0.r) * f;
                dst[o + 1] = p0.g + (p1.g - p0.g) * f;
                dst[o + 2] = p0.b + (p1.b - p0.b) * f;
            }
        }

        static void ResampleColsArea(float[] src, int srcH, int width, float[] dst, int dstH,
                                     System.Threading.CancellationToken token = default)
        {
            double scale = srcH / (double)dstH; // >1
            int stride = width * 3;
            // 出力行単位の並列。行内の加算順序(i 昇順・k 昇順)は逐次版と同一。
            Parallel.For(0, dstH, SamMaskRefine.MakeParallelOptions(token), y =>
            {
                double s0 = y * scale, s1 = (y + 1) * scale;
                int i0 = (int)s0, i1 = System.Math.Min((int)System.Math.Ceiling(s1), srcH);
                int dstRow = y * stride;
                for (int k = 0; k < stride; k++) dst[dstRow + k] = 0f;
                double wsum = 0;
                for (int i = i0; i < i1; i++)
                {
                    double cover = System.Math.Min(s1, i + 1.0) - System.Math.Max(s0, (double)i);
                    if (cover <= 0) continue;
                    wsum += cover;
                    int srcRow = i * stride;
                    for (int k = 0; k < stride; k++)
                        dst[dstRow + k] += (float)(src[srcRow + k] * cover);
                }
                float inv = (float)(1.0 / wsum);
                for (int k = 0; k < stride; k++) dst[dstRow + k] *= inv;
            });
        }

        static void ResampleColsBilinear(float[] src, int srcH, int width, float[] dst, int dstH,
                                         System.Threading.CancellationToken token = default)
        {
            double scale = srcH / (double)dstH; // <1 (拡大)
            int stride = width * 3;
            Parallel.For(0, dstH, SamMaskRefine.MakeParallelOptions(token), y =>
            {
                double sy = (y + 0.5) * scale - 0.5;
                if (sy < 0) sy = 0; if (sy > srcH - 1) sy = srcH - 1;
                int y0 = (int)sy; int y1 = System.Math.Min(y0 + 1, srcH - 1);
                float f = (float)(sy - y0);
                int r0 = y0 * stride, r1 = y1 * stride, dr = y * stride;
                for (int k = 0; k < stride; k++)
                    dst[dr + k] = src[r0 + k] + (src[r1 + k] - src[r0 + k]) * f;
            });
        }
    }
}
