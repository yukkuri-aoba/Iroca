// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // ぼかし(ガウシアン)。選択の縁のぼかしが使う。デコンタミ・フチ消しの近傍の和は、それぞれの中で整数で数える
    // (以前ここにあった箱フィルタの和は、呼び出しが無くなったので消した)。
    internal static unsafe partial class PixelProcessor {
        /// <summary>
        /// ガウシアンブラーを src に適用して dst に書き込む。
        /// 内部 temp バッファは作業配列のプール(ネイティブメモリ)から借りて返すので Mono のヒープを使わない。
        /// dst は呼び出し元が用意すること（作業配列のプールから借りる）。
        /// radius &lt; 1 のとき何もしない（dst は未定義のまま）。
        /// 戻り値: ブラー処理を行った場合 true、スキップした場合 false。
        /// </summary>
        private static bool GaussianBlur(float* src, float* dst, int w, int h, float sigma,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            CancellationToken ct = default) {
            int radius = Mathf.CeilToInt(sigma * GaussianRadiusPerSigma);
            if (radius < 1) {
                return false;
            }

            float[] kernel = new float[radius * 2 + 1];
            float kernelSum = 0f;
            for (int i = -radius; i <= radius; i++) {
                kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                kernelSum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++) {
                kernel[i] /= kernelSum;
            }

            int len = w * h;
            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけブラーする(P2-7)。矩形は
            // 呼び出し側が「src の非0 が矩形より radius 以上内側」を保証するため、矩形外の真のブラー出力は
            // 常に 0。dst を全クリアしておけば矩形内だけ計算しても全画素ブラーと出力ビット一致する。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            float* temp = s_floatPool.Rent(len);
            var gaussPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try {
                ClearWork(dst, 0, len);   // 矩形外は 0(全画素ブラーの src=0 領域と一致)
                // 垂直パスが読む行は [boxMinY-radius, boxMaxY+radius] なので、水平パスはその行範囲ぶん
                // 上下に広げて temp を用意する(列は矩形のまま。垂直は矩形列しか読まない)。
                int hMinY = Mathf.Max(0, boxMinY - radius);
                int hMaxY = Mathf.Min(h - 1, boxMaxY + radius);
                Parallel.For(hMinY, hMaxY + 1, gaussPo, y => {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++) {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++) {
                            int nx = Mathf.Clamp(x + k, 0, w - 1);
                            val += src[rb + nx] * kernel[k + radius];
                        }
                        temp[rb + x] = val;
                    }
                });

                Parallel.For(boxMinY, boxMaxY + 1, gaussPo, y => {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++) {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++) {
                            int ny = Mathf.Clamp(y + k, 0, h - 1);
                            val += temp[ny * w + x] * kernel[k + radius];
                        }
                        dst[rb + x] = val;
                    }
                });
            } finally {
                s_floatPool.Return(temp);
            }
            return true;
        }
    }
}
