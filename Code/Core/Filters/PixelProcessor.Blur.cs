// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    // ぼかし(箱フィルタの和・ガウシアン・ぼかしの流出止め)。選択のぼかしと、デコンタミの近傍の和が使う。
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 分離型ボックス和フィルタ。各ピクセル位置で (2r+1)×(2r+1) 窓内の合計を dst に書き込む
        /// （境界はゼロ拡張：画像外の寄与を 0 として無視）。スライディングウィンドウで O(N) で計算。
        /// 内部 temp バッファは作業配列のプール(ネイティブメモリ)から借りて返すので Mono のヒープを使わない。
        /// dst は呼び出し元が用意すること（作業配列のプールから借りる）。
        ///
        /// bbox 未指定(boxMaxX&lt;0)なら全画素。指定時は dst をその矩形内だけ埋める(GaussianBlur と
        /// 同じ形)。src は矩形を r だけ広げた範囲 [boxMinX-r, boxMaxX+r]×[boxMinY-r, boxMaxY+r] が
        /// 正しく用意されていれば足り、その外は読まれない。
        /// 矩形指定時はスライディング和の開始位置が変わるため、加算順序が全画素版と一致するのは
        /// **src が整数値(0..255 のバイト値 / 0-1 マスク)で窓和が float の整数精度に収まる**用途に限る
        /// (現在の呼び出し元はすべてこれを満たす)。その場合は丸め差が原理的に生じず出力ビット不変。
        /// </summary>
        private static void BoxFilterSum(float* src, float* dst, int w, int h, int r, CancellationToken ct = default,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1)
        {
            int len = w * h;
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }
            float* temp = s_floatPool.Rent(len);
            var filterPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
                // 水平パス。垂直パスが読む行は [boxMinY-r, boxMaxY+r] なので、その行範囲だけ作る。
                int hMinY = Mathf.Max(0, boxMinY - r);
                int hMaxY = Mathf.Min(h - 1, boxMaxY + r);
                Parallel.For(hMinY, hMaxY + 1, filterPo, y =>
                {
                    int rowOff = y * w;
                    float sum = 0f;
                    int initBeg = Mathf.Max(0, boxMinX - r);
                    int initEnd = Mathf.Min(w - 1, boxMinX + r);
                    for (int k = initBeg; k <= initEnd; k++) sum += src[rowOff + k];
                    temp[rowOff + boxMinX] = sum;
                    for (int x = boxMinX + 1; x <= boxMaxX; x++)
                    {
                        int subIdx = x - 1 - r;
                        int addIdx = x + r;
                        if (subIdx >= 0) sum -= src[rowOff + subIdx];
                        if (addIdx < w) sum += src[rowOff + addIdx];
                        temp[rowOff + x] = sum;
                    }
                });

                // 垂直パス。1 反復 = 1 列の縦走査だと、4K ではストライド 16KB でキャッシュラインの
                // 1/16 しか使えない。VBlock 列ぶんの走査和をまとめて持ち行方向に進むことで、1 行の
                // アクセスが連続 16 要素(=1 キャッシュライン)になる。各列の加算順序は従来と同一
                // (初期窓を昇順に加算 → 行ごとに sub → add)なので出力ビット不変。
                const int VBlock = 16;
                int blockCount = (boxMaxX - boxMinX + VBlock) / VBlock;
                Parallel.For(0, blockCount, filterPo, bi =>
                {
                    int xs = boxMinX + bi * VBlock;
                    int xe = Mathf.Min(xs + VBlock - 1, boxMaxX);
                    var sums = new float[VBlock];
                    int initBeg = Mathf.Max(0, boxMinY - r);
                    int initEnd = Mathf.Min(h - 1, boxMinY + r);
                    for (int k = initBeg; k <= initEnd; k++)
                    {
                        int krb = k * w;
                        for (int x = xs; x <= xe; x++) sums[x - xs] += temp[krb + x];
                    }
                    int drb = boxMinY * w;
                    for (int x = xs; x <= xe; x++) dst[drb + x] = sums[x - xs];
                    for (int y = boxMinY + 1; y <= boxMaxY; y++)
                    {
                        int subIdx = y - 1 - r;
                        int addIdx = y + r;
                        if (subIdx >= 0)
                        {
                            int srb = subIdx * w;
                            for (int x = xs; x <= xe; x++) sums[x - xs] -= temp[srb + x];
                        }
                        if (addIdx < h)
                        {
                            int arb = addIdx * w;
                            for (int x = xs; x <= xe; x++) sums[x - xs] += temp[arb + x];
                        }
                        int yrb = y * w;
                        for (int x = xs; x <= xe; x++) dst[yrb + x] = sums[x - xs];
                    }
                });
            }
            finally
            {
                s_floatPool.Return(temp);
            }
        }

        /// <summary>
        /// ガウシアンブラーを src に適用して dst に書き込む。
        /// 内部 temp バッファは作業配列のプール(ネイティブメモリ)から借りて返すので Mono のヒープを使わない。
        /// dst は呼び出し元が用意すること（作業配列のプールから借りる）。
        /// radius &lt; 1 のとき何もしない（dst は未定義のまま）。
        /// 戻り値: ブラー処理を行った場合 true、スキップした場合 false。
        /// </summary>
        private static bool GaussianBlur(float* src, float* dst, int w, int h, float sigma,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            CancellationToken ct = default)
        {
            int radius = Mathf.CeilToInt(sigma * GaussianRadiusPerSigma);
            if (radius < 1) return false;

            float[] kernel = new float[radius * 2 + 1];
            float kernelSum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                kernelSum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= kernelSum;

            int len = w * h;
            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけブラーする(P2-7)。矩形は
            // 呼び出し側が「src の非0 が矩形より radius 以上内側」を保証するため、矩形外の真のブラー出力は
            // 常に 0。dst を全クリアしておけば矩形内だけ計算しても全画素ブラーと出力ビット一致する。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            float* temp = s_floatPool.Rent(len);
            var gaussPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
                ClearWork(dst, 0, len);   // 矩形外は 0(全画素ブラーの src=0 領域と一致)
                // 垂直パスが読む行は [boxMinY-radius, boxMaxY+radius] なので、水平パスはその行範囲ぶん
                // 上下に広げて temp を用意する(列は矩形のまま。垂直は矩形列しか読まない)。
                int hMinY = Mathf.Max(0, boxMinY - radius);
                int hMaxY = Mathf.Min(h - 1, boxMaxY + radius);
                Parallel.For(hMinY, hMaxY + 1, gaussPo, y =>
                {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int nx = Mathf.Clamp(x + k, 0, w - 1);
                            val += src[rb + nx] * kernel[k + radius];
                        }
                        temp[rb + x] = val;
                    }
                });

                Parallel.For(boxMinY, boxMaxY + 1, gaussPo, y =>
                {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int ny = Mathf.Clamp(y + k, 0, h - 1);
                            val += temp[ny * w + x] * kernel[k + radius];
                        }
                        dst[rb + x] = val;
                    }
                });
            }
            finally
            {
                s_floatPool.Return(temp);
            }
            return true;
        }

        private static void ConstrainBlur(float* blurred, float* original, int w, int h, int radius, CancellationToken ct = default)
        {
            // マッチがなかった領域へのブラーの流出を防止。
            // original > 0 を float マスクに変換して BoxFilterSum に流すことで
            // 近傍チェックを O(N·r²) から O(N) に削減。
            int len = w * h;
            float* mask = null, neighborSum = null;
            try
            {
                mask = s_floatPool.Rent(len);
                neighborSum = s_floatPool.Rent(len);
                // 行並列(要素独立・自 index 書き込みのみ=出力ビット不変)。per-index デリゲートを避ける。
                var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
                var maskL = mask;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        maskL[i] = original[i] > 0f ? 1f : 0f;
                    }
                });

                BoxFilterSum(mask, neighborSum, w, h, radius, ct);

                var neighborSumL = neighborSum;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        if (original[i] > 0f) continue;
                        if (neighborSumL[i] <= 0f)
                            blurred[i] = 0f;
                    }
                });
            }
            finally
            {
                if (neighborSum != null) s_floatPool.Return(neighborSum);
                if (mask        != null) s_floatPool.Return(mask);
            }
        }
    }
}
