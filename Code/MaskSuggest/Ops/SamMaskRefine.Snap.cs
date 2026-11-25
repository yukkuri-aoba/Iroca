// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // SamMaskRefine: 境界帯の再分類(SnapBoundary)。クラスの説明と共通の定数・下回りは SamMaskRefine.cs。
    internal static unsafe partial class SamMaskRefine {
        /// <summary>
        /// mask(下原点 w*h)の境界帯を pixels の色統計で再分類する(in-place)。
        /// </summary>
        /// <param name="sizeW">帯幅を決める画像の寸法(0 なら w・h)。切り出しで回すとき全画像の寸法を渡す。</param>
        public static void SnapBoundary(bool* mask, Color32* pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default,
                                        int sizeW = 0, int sizeH = 0) {
            if (mask == null || pixelsBottomUp == null) {
                return;
            }
            if (sizeW <= 0 || sizeH <= 0) { sizeW = w; sizeH = h; }

            // 不確実帯の半幅: SAM 低解像度セルの 3/4(±半セルの理論誤差+マージン)。
            int d = BandHalfWidth(sizeW, sizeH);

            // 境界からの深さはセパラブルな L1 距離変換で求める(作業配列は ExtendFringe と同じくプールから)。
            using var scratch = new WorkScratch();
            // 反対側までの距離 1 本。mask の画素では内→外境界まで(distIn)、外の画素では外→内境界まで(distOut)。
            // 下で読むのはどれも mask0(= この時点の mask の写し)で分けた自分の側の画素だけなので、2 本の版と同じ値を読む。
            var distIn = DistanceToOpposite(scratch, mask, w, h, token);
            var distOut = distIn;

            // 再分類は元マスクのスナップショットに対して行う(書き換える順序に結果が依らないようにする。並列でも決定的)。
            bool* mask0 = scratch.CopyOf(mask, w * h);

            // 確信領域の局所色統計を粗グリッド(ストライド d)で集計。
            // 帯画素は近傍グリッド(半径 2d 相当)の合算平均と比較する。
            // 並列化はグリッド行単位(1 タスク = 画素行 d 本)でセル書き込みを共有しない=決定的。
            var po = MakeParallelOptions(token);
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            long* sumIn = scratch.Long(gw * gh * 4, clear: true);
            long* sumOut = scratch.Long(gw * gh * 4, clear: true);
            int* cntIn = scratch.Int(gw * gh, clear: true);
            int* cntOut = scratch.Int(gw * gh, clear: true);
            Parallel.For(0, gh, po, gy => {
                int gRow = gy * gw;
                int yEnd = System.Math.Min(h, (gy + 1) * d);
                for (int y = gy * d; y < yEnd; y++) {
                    int row = y * w;
                    for (int x = 0; x < w; x++) {
                        int i = row + x;
                        bool confIn = mask0[i] && distIn[i] > d;
                        bool confOut = !mask0[i] && distOut[i] > d;
                        if (!confIn && !confOut) {
                            continue;
                        }
                        int g = gRow + x / d;
                        var c = pixelsBottomUp[i];
                        if (confIn) {
                            int o = g * 4;
                            sumIn[o] += c.r; sumIn[o + 1] += c.g; sumIn[o + 2] += c.b; sumIn[o + 3] += c.a;
                            cntIn[g]++;
                        } else {
                            int o = g * 4;
                            sumOut[o] += c.r; sumOut[o + 1] += c.g; sumOut[o + 2] += c.b; sumOut[o + 3] += c.a;
                            cntOut[g]++;
                        }
                    }
                }
            });

            // セル単位モード(MinCellSamples 以上のセル)。判定はモードへの最近傍距離で行う:
            // プール平均は多峰統計(白ギャップ+隣接する別パーツ、柄の複数色)で実在しない
            // 中間色に潰れ、素の背景画素が「外側平均より内側平均に近い」と誤判定される。
            double* meanIn = scratch.Double(gw * gh * 4, clear: true);
            double* meanOut = scratch.Double(gw * gh * 4, clear: true);
            bool* validIn = scratch.Bool(gw * gh, clear: true);
            bool* validOut = scratch.Bool(gw * gh, clear: true);
            ComputeCellModes(sumIn, cntIn, gw * gh, 4, meanIn, validIn);
            ComputeCellModes(sumOut, cntOut, gw * gh, 4, meanOut, validOut);

            // 帯画素の再分類。元の mask を読みながら書き換えると統計自体は粗グリッド由来なので
            // 影響しない(確信領域は帯外で不変)。判定入力は mask0/統計(読み取り専用)、
            // 書き込みは自画素のみなので行並列で決定的。
            //
            // 窓の集計(半径・内外の合算・窓の中の有効なモードの並び)はセル (gx, gy) だけで決まり画素の色に依らないので、
            // 行の中で同じセルが続く間は 1 回だけ求めて使い回す。画素ごとに走るのは最も近いモードの距離だけで、
            // 走査の順も比較も以前と同じなので、出力はビット同一。
            int windowCells = (2 * MaxWindowRadius + 1) * (2 * MaxWindowRadius + 1);
            Parallel.For(0, h, po, y => {
                int row = y * w;
                int gy = y / d;
                int* inList = stackalloc int[windowCells];
                int* outList = stackalloc int[windowCells];
                int inLen = 0, outLen = 0;
                int cachedGx = -1;
                long ir = 0, ig = 0, ib = 0, ia = 0, or_ = 0, og = 0, ob = 0, oa = 0;
                int ic = 0, oc = 0;
                for (int x = 0; x < w; x++) {
                    int i = row + x;
                    bool inBand = mask0[i] ? distIn[i] <= d : distOut[i] <= d;
                    if (!inBand) {
                        continue;
                    }

                    // 近傍半径2セル(5x5相当)から開始し、片側でも統計不足なら半径を広げて
                    // 再集計する。先細りウェッジ等、局所幅が帯より狭い形状では片側の確信領域が
                    // 直近に無いことがあるため(過去は即座に諦めて SAM の粗い判定を残していた)。
                    int gx = x / d;
                    if (gx != cachedGx) {
                        cachedGx = gx;
                        for (int radius = MinWindowRadius; radius <= MaxWindowRadius; radius += 2) {
                            ir = ig = ib = ia = or_ = og = ob = oa = 0; ic = 0; oc = 0;
                            inLen = outLen = 0;
                            int gy0 = Mathf.Max(0, gy - radius), gy1 = Mathf.Min(gh - 1, gy + radius);
                            int gx0 = Mathf.Max(0, gx - radius), gx1 = Mathf.Min(gw - 1, gx + radius);
                            for (int yy = gy0; yy <= gy1; yy++) {
                                int gRow = yy * gw;
                                for (int xx = gx0; xx <= gx1; xx++) {
                                    int g = gRow + xx;
                                    int o = g * 4;
                                    if (cntIn[g] > 0) {
                                        ir += sumIn[o]; ig += sumIn[o + 1]; ib += sumIn[o + 2]; ia += sumIn[o + 3];
                                        ic += cntIn[g];
                                        if (validIn[g]) {
                                            inList[inLen++] = o;
                                        }
                                    }
                                    if (cntOut[g] > 0) {
                                        or_ += sumOut[o]; og += sumOut[o + 1]; ob += sumOut[o + 2]; oa += sumOut[o + 3];
                                        oc += cntOut[g];
                                        if (validOut[g]) {
                                            outList[outLen++] = o;
                                        }
                                    }
                                }
                            }
                            if (ic >= MinSamples && oc >= MinSamples) {
                                break;
                            }
                        }
                    }
                    if (ic < MinSamples || oc < MinSamples) {
                        continue; // 統計不足 → SAM の判定を維持
                    }

                    var c = pixelsBottomUp[i];
                    double minIn = double.MaxValue, minOut = double.MaxValue;
                    for (int k = 0; k < inLen; k++) {
                        double dd = Dist2Mean(c, meanIn, inList[k]);
                        if (dd < minIn) {
                            minIn = dd;
                        }
                    }
                    for (int k = 0; k < outLen; k++) {
                        double dd = Dist2Mean(c, meanOut, outList[k]);
                        if (dd < minOut) {
                            minOut = dd;
                        }
                    }

                    double dIn = minIn != double.MaxValue ? minIn : Dist2(c, ir, ig, ib, ia, ic);
                    double dOut = minOut != double.MaxValue ? minOut : Dist2(c, or_, og, ob, oa, oc);
                    if (dIn == dOut) {
                        continue;
                    }
                    mask[i] = dIn < dOut;
                }
            });

            // AA 境界画素(地色と背景の中間色)は二値分類がどちらへ転ぶか不安定で
            // ±1px の点状ノイズになる。帯内のみ 3x3 多数決で平滑化して点滅を除去する
            // (帯外は不変なので形状は保たれる)。
            MajoritySmoothBand(scratch, mask, mask0, distIn, distOut, d, w, h, token);
        }
    }
}
