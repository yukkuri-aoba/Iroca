// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の境界色スナップ(純計算)。
    ///
    /// SAM のマスクは 256² の低解像度ロジット由来のため、元寸では 1 セル =
    /// texLong/256 px(4096² で 16px)の粒度しかなく、境界が階段状に ±半セル程度
    /// はみ出す。ここでは境界の不確実帯(幅=解像度から構造的に導出)に限り、
    /// 「帯の内側の確信領域 / 外側の確信領域」の局所平均色(RGBA)への近さで画素を
    /// 再分類し、境界を実テクスチャの色エッジへ吸着させる。
    /// 統計は対象テクスチャ自身から局所的に導出し、特定の色・座標・素材への
    /// 依存はない(デコンタミの局所ドナー統計と同じ思想)。
    /// </summary>
    internal static class SamMaskRefine
    {
        /// <summary>確信領域の平均色に必要な最小画素数(これ未満の側があれば再分類しない)。</summary>
        const int MinSamples = 16;

        /// <summary>
        /// mask(下原点 w*h)の境界帯を pixels の色統計で再分類する(in-place)。
        /// </summary>
        public static void SnapBoundary(bool[] mask, Color32[] pixelsBottomUp, int w, int h)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h ||
                pixelsBottomUp.Length < w * h) return;

            // 不確実帯の半幅: SAM 低解像度セルの 3/4(±半セルの理論誤差+マージン)。
            int d = Mathf.Max(2, Mathf.CeilToInt(
                Mathf.Max(w, h) / (float)SamMaskPostprocess.LowRes * 0.75f));

            // L1 距離変換で「境界からの深さ」を測る(セパラブル2パス・O(N))
            var distIn = DistanceToOpposite(mask, w, h, inside: true);   // mask 内→外境界までの距離
            var distOut = DistanceToOpposite(mask, w, h, inside: false); // mask 外→内境界までの距離

            // 再分類は元マスクのスナップショットに対して行う(書き換え順序への依存を排除し、
            // NumPy リファレンスと決定的に一致させるため)。
            var mask0 = (bool[])mask.Clone();

            // 確信領域の局所色統計を粗グリッド(ストライド d)で集計。
            // 帯画素は近傍グリッド(半径 2d 相当)の合算平均と比較する。
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            var sumIn = new long[gw * gh * 4];
            var sumOut = new long[gw * gh * 4];
            var cntIn = new int[gw * gh];
            var cntOut = new int[gw * gh];
            for (int y = 0; y < h; y++)
            {
                int gRow = (y / d) * gw;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    bool confIn = mask0[i] && distIn[i] > d;
                    bool confOut = !mask0[i] && distOut[i] > d;
                    if (!confIn && !confOut) continue;
                    int g = gRow + x / d;
                    var c = pixelsBottomUp[i];
                    if (confIn)
                    {
                        int o = g * 4;
                        sumIn[o] += c.r; sumIn[o + 1] += c.g; sumIn[o + 2] += c.b; sumIn[o + 3] += c.a;
                        cntIn[g]++;
                    }
                    else
                    {
                        int o = g * 4;
                        sumOut[o] += c.r; sumOut[o + 1] += c.g; sumOut[o + 2] += c.b; sumOut[o + 3] += c.a;
                        cntOut[g]++;
                    }
                }
            }

            // 帯画素の再分類。元の mask を読みながら書き換えると統計自体は粗グリッド由来なので
            // 影響しない(確信領域は帯外で不変)。
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int gy = y / d;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    bool inBand = mask0[i] ? distIn[i] <= d : distOut[i] <= d;
                    if (!inBand) continue;

                    int gx = x / d;
                    long ir = 0, ig = 0, ib = 0, ia = 0, or_ = 0, og = 0, ob = 0, oa = 0;
                    int ic = 0, oc = 0;
                    int gy0 = Mathf.Max(0, gy - 2), gy1 = Mathf.Min(gh - 1, gy + 2);
                    int gx0 = Mathf.Max(0, gx - 2), gx1 = Mathf.Min(gw - 1, gx + 2);
                    for (int yy = gy0; yy <= gy1; yy++)
                    {
                        int gRow = yy * gw;
                        for (int xx = gx0; xx <= gx1; xx++)
                        {
                            int g = gRow + xx;
                            int o = g * 4;
                            if (cntIn[g] > 0)
                            {
                                ir += sumIn[o]; ig += sumIn[o + 1]; ib += sumIn[o + 2]; ia += sumIn[o + 3];
                                ic += cntIn[g];
                            }
                            if (cntOut[g] > 0)
                            {
                                or_ += sumOut[o]; og += sumOut[o + 1]; ob += sumOut[o + 2]; oa += sumOut[o + 3];
                                oc += cntOut[g];
                            }
                        }
                    }
                    if (ic < MinSamples || oc < MinSamples) continue; // 統計不足 → SAM の判定を維持

                    var c = pixelsBottomUp[i];
                    double dIn = Dist2(c, ir, ig, ib, ia, ic);
                    double dOut = Dist2(c, or_, og, ob, oa, oc);
                    if (dIn == dOut) continue;
                    mask[i] = dIn < dOut;
                }
            }

            // AA 境界画素(地色と背景の中間色)は二値分類がどちらへ転ぶか不安定で
            // ±1px の点状ノイズになる。帯内のみ 3x3 多数決で平滑化して点滅を除去する
            // (帯外は不変なので形状は保たれる)。
            MajoritySmoothBand(mask, mask0, distIn, distOut, d, w, h);
        }

        /// <summary>帯内画素を 3x3 多数決(5/9 以上)で平滑化する。読みはスナップ結果の
        /// スナップショット、書きは mask(決定的・順序非依存)。</summary>
        static void MajoritySmoothBand(bool[] mask, bool[] mask0, int[] distIn, int[] distOut,
                                       int d, int w, int h)
        {
            var snapped = (bool[])mask.Clone();
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    bool inBand = mask0[i] ? distIn[i] <= d : distOut[i] <= d;
                    if (!inBand) continue;
                    int n = 0;
                    if (snapped[i - w - 1]) n++;
                    if (snapped[i - w]) n++;
                    if (snapped[i - w + 1]) n++;
                    if (snapped[i - 1]) n++;
                    if (snapped[i]) n++;
                    if (snapped[i + 1]) n++;
                    if (snapped[i + w - 1]) n++;
                    if (snapped[i + w]) n++;
                    if (snapped[i + w + 1]) n++;
                    mask[i] = n >= 5;
                }
            }
        }

        static double Dist2(Color32 c, long sr, long sg, long sb, long sa, int n)
        {
            double mr = sr / (double)n, mg = sg / (double)n, mb = sb / (double)n, ma = sa / (double)n;
            double dr = c.r - mr, dg = c.g - mg, db = c.b - mb, da = c.a - ma;
            return dr * dr + dg * dg + db * db + da * da;
        }

        /// <summary>
        /// inside=true: mask 内の各画素について最も近い mask 外画素までの L1 距離(外は 0)。
        /// inside=false: 逆。セパラブル 2 パスの city-block 距離変換。
        /// </summary>
        internal static int[] DistanceToOpposite(bool[] mask, int w, int h, bool inside)
        {
            const int Inf = 1 << 28;
            var dist = new int[w * h];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = (mask[i] == inside) ? Inf : 0;

            // forward (左・下 = 配列順)
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (dist[i] == 0) continue;
                    int best = dist[i];
                    if (x > 0 && dist[i - 1] + 1 < best) best = dist[i - 1] + 1;
                    if (y > 0 && dist[i - w] + 1 < best) best = dist[i - w] + 1;
                    dist[i] = best;
                }
            }
            // backward (右・上)
            for (int y = h - 1; y >= 0; y--)
            {
                int row = y * w;
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = row + x;
                    if (dist[i] == 0) continue;
                    int best = dist[i];
                    if (x < w - 1 && dist[i + 1] + 1 < best) best = dist[i + 1] + 1;
                    if (y < h - 1 && dist[i + w] + 1 < best) best = dist[i + w] + 1;
                    dist[i] = best;
                }
            }
            return dist;
        }
    }
}
