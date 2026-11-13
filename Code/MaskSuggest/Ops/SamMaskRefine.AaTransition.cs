// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // SamMaskRefine: AA 遷移帯の包含(IncludeAaTransition とその切り出し版)。クラスの説明と共通の定数・下回りは SamMaskRefine.cs。
    internal static unsafe partial class SamMaskRefine {
        /// <summary><see cref="IncludeAaTransition(bool*, Color32*, int, int, System.Threading.CancellationToken, int)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void IncludeAaTransition(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                               System.Threading.CancellationToken token = default,
                                               int dOverride = 0) {
            if (mask == null || mask.Length != w * h) {
                return;
            }
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) {
                return;
            }
            fixed (bool* m = mask)
            fixed (Color32* p = pixelsBottomUp) {
                IncludeAaTransition(m, p, w, h, token, dOverride);
            }
        }

        /// <summary>
        /// mask(下原点 w*h)の境界外側の AA 遷移帯(内側色と外側色の実混合画素)をマスクへ
        /// 含める(in-place)。SnapBoundary → ExtendFringe の後段で呼ぶ最終仕上げ。
        /// 除外(保護)マスクの意味論では「パーツ色が目に見えて混ざる画素」を取り残すと
        /// そこだけ再着色されて点ノイズになるため、混合率 AaBlendMin 以上の実混合を包含する。
        /// </summary>
        public static void IncludeAaTransition(bool* mask, Color32* pixelsBottomUp, int w, int h,
                                               System.Threading.CancellationToken token = default,
                                               int dOverride = 0) {
            if (mask == null || pixelsBottomUp == null) {
                return;
            }

            // dOverride: 切り出して回すとき(IncludeAaTransitionCropped と、精密化の切り出し実行)に、全画像と同じ帯幅を
            // 使うための上書き。0 なら自分の寸法から導く。
            int d = dOverride > 0 ? dOverride : BandHalfWidth(w, h);

            // soft skirt が帯幅を超える場合に境界を進めながら吸収する(追加ゼロで早期終了)。
            for (int pass = 0; pass < AaMaxPasses; pass++) {
                if (IncludeAaTransitionPass(mask, pixelsBottomUp, w, h, d, token) == 0) {
                    break;
                }
            }

            // フェーズ2: 境界 1px リングの局所ブレンド吸収。フェーズ1 の面統計は soft skirt
            // (ぼかし縁)が近傍にある素材で外側平均が skirt 色に汚染され、階段の角に残る
            // 遷移画素(隣接マスク色と背景の 1px 混合)の混合率を 0 と誤評価して取り残す
            // (実測: 各階段角に 1 画素、再着色で点ノイズ化)。ここでは面統計を使わず、
            // 画素自身の「隣接マスク画素 m ⇄ 反対側の画素 q」を両端とする局所線分で
            // p = α·m + (1-α)·q の実混合判定を行う(統計汚染と無縁・向き非依存)。
            for (int pass = 0; pass < AaMaxPasses; pass++) {
                if (AbsorbEdgeBlendPass(mask, pixelsBottomUp, w, h, token) == 0) {
                    break;
                }
            }
        }

        /// <summary>
        /// IncludeAaTransition をクロップで実行しても全画像実行とビット同一になる矩形
        /// (d 格子整列・マージン込み)を導出する。mask に true が無ければ false(AA 不要)。
        ///
        /// マージンの根拠(=クロップ実行が読むデータが全画像実行と一致する条件):
        /// - 各パスの追加は境界外側の帯(距離 ≤ d)のみ → AaMaxPasses パスで成長 ≤ AaMaxPasses·d、
        ///   フェーズ 2 のリング吸収は 1px × AaMaxPasses パス。
        /// - 帯画素が読むセル統計は半径 MaxWindowRadius セル = (MaxWindowRadius+1)d 以内。
        ///   クロップ端の欠けセルまで読まないよう +1 セル。
        /// - 距離変換は mask がクロップ内に完全に収まっていれば全画素で全画像実行と一致
        ///   (最近接 mask/非 mask 画素がクロップ内にあるため)。
        /// → 要る余白は AaMaxPasses·(d+1) + (MaxWindowRadius+2)·d(今の定数で 13d+3)。切りのよい 15d と
        ///   大きい方を使う(定数を変えたときに余白が足りなくならないよう、式からも求める)。
        /// rx0/ry0 は d の倍数に切り下げ、セル境界(x/d)を全画像実行と一致させる。
        /// </summary>
        public static bool TryDeriveAaCropRect(bool[] mask, int w, int h,
                                               out int rx0, out int ry0, out int rw, out int rh,
                                               out int d) {
            rx0 = ry0 = rw = rh = 0;
            d = BandHalfWidth(w, h);
            if (mask == null || mask.Length != w * h) {
                return false;
            }
            fixed (bool* m = mask) {
                return TryDeriveAaCropRect(m, w, h, out rx0, out ry0, out rw, out rh, out d);
            }
        }

        /// <summary><see cref="TryDeriveAaCropRect(bool[], int, int, out int, out int, out int, out int, out int)"/> のポインタ版(mask は w*h 個)。</summary>
        public static bool TryDeriveAaCropRect(bool* mask, int w, int h,
                                               out int rx0, out int ry0, out int rw, out int rh,
                                               out int d) {
            rx0 = ry0 = rw = rh = 0;
            d = BandHalfWidth(w, h);
            if (mask == null) {
                return false;
            }

            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            for (int y = 0; y < h; y++) {
                int row = y * w;
                for (int x = 0; x < w; x++) {
                    if (!mask[row + x]) {
                        continue;
                    }
                    if (x < bx0) {
                        bx0 = x;
                    }
                    if (x > bx1) {
                        bx1 = x;
                    }
                    if (y < by0) {
                        by0 = y;
                    }
                    by1 = y;
                }
            }
            if (bx1 < 0) {
                return false; // true 画素なし → AA 包含は no-op
            }

            int pad = Mathf.Max(15 * d, AaMaxPasses * (d + 1) + (MaxWindowRadius + 2) * d);
            rx0 = Mathf.Max(0, (bx0 - pad) / d * d);
            ry0 = Mathf.Max(0, (by0 - pad) / d * d);
            int rx1 = Mathf.Min(w - 1, ((bx1 + pad) / d + 1) * d - 1);
            int ry1 = Mathf.Min(h - 1, ((by1 + pad) / d + 1) * d - 1);
            rw = rx1 - rx0 + 1;
            rh = ry1 - ry0 + 1;
            return true;
        }

        /// <summary>
        /// fullMask のうち TryDeriveAaCropRect で導出した矩形だけを切り出して
        /// IncludeAaTransition(d は全画像値で上書き)を実行し、追加画素を fullMask へ書き戻す。
        /// cropPixels は同矩形の下原点画素列(呼び出し側が全体画素から行コピーで切り出す。GetPixels(rect) は使わない)。
        /// 出力は全画像実行とビット同一(根拠は TryDeriveAaCropRect のコメント)。
        /// </summary>
        public static void IncludeAaTransitionCropped(bool[] fullMask, int w, int h,
                                                      Color32[] cropPixels,
                                                      int rx0, int ry0, int rw, int rh, int d,
                                                      System.Threading.CancellationToken token = default) {
            if (fullMask == null || cropPixels == null || fullMask.Length != w * h ||
                cropPixels.Length < rw * rh || rw <= 0 || rh <= 0) {
                return;
            }
            fixed (bool* m = fullMask)
            fixed (Color32* p = cropPixels) {
                IncludeAaTransitionCropped(m, w, h, p, rx0, ry0, rw, rh, d, token);
            }
        }

        /// <summary>
        /// <see cref="IncludeAaTransitionCropped(bool[], int, int, Color32[], int, int, int, int, int, System.Threading.CancellationToken)"/>
        /// のポインタ版(fullMask は w*h 個、cropPixels は rw*rh 画素)。切り出したマスクは作業配列のプールから借りる。
        /// </summary>
        public static void IncludeAaTransitionCropped(bool* fullMask, int w, int h,
                                                      Color32* cropPixels,
                                                      int rx0, int ry0, int rw, int rh, int d,
                                                      System.Threading.CancellationToken token = default) {
            if (fullMask == null || cropPixels == null || rw <= 0 || rh <= 0) {
                return;
            }

            using var scratch = new WorkScratch();
            bool* cropMask = scratch.Bool(rw * rh, clear: false);
            for (int cy = 0; cy < rh; cy++) {
                System.Buffer.MemoryCopy(fullMask + (long)(ry0 + cy) * w + rx0, cropMask + (long)cy * rw, rw, rw);
            }

            IncludeAaTransition(cropMask, cropPixels, rw, rh, token, dOverride: d);

            // IncludeAaTransition は追加のみ(true→false は起きない)なので行コピーで書き戻せる。
            for (int cy = 0; cy < rh; cy++) {
                System.Buffer.MemoryCopy(cropMask + (long)cy * rw, fullMask + (long)(ry0 + cy) * w + rx0, rw, rw);
            }
        }

        /// <summary>
        /// 境界に接する外側画素を、隣接マスク画素 m と反対側画素 q の局所線分で実混合判定して
        /// マスクへ吸収する 1 パス。追加した画素数を返す。読みはパス開始時のスナップショット、
        /// 書きは追加のみ(決定的・順序非依存)。別色の構造(輪郭線等)は残差ゲートで残る。
        /// </summary>
        static int AbsorbEdgeBlendPass(bool* mask, Color32* pixelsBottomUp, int w, int h,
                                       System.Threading.CancellationToken token = default) {
            using var scratch = new WorkScratch();
            bool* mask0 = scratch.CopyOf(mask, w * h);
            int[] ex = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] ey = { 0, 0, 1, -1, 1, -1, 1, -1 };
            // 読みはスナップショット・書きは自画素のみなので行並列で決定的。
            // added は行ローカルに数えて最後に合算する(値も逐次実行と一致)。
            int added = 0;
            Parallel.For(0, h, MakeParallelOptions(token),
                () => 0,
                (y, _, local) => {
                    int row = y * w;
                    for (int x = 0; x < w; x++) {
                        int i = row + x;
                        if (mask0[i]) {
                            continue;
                        }
                        for (int k = 0; k < 8; k++) {
                            int mx2 = x + ex[k], my2 = y + ey[k];
                            int qx = x - ex[k], qy = y - ey[k];
                            if (mx2 < 0 || mx2 >= w || my2 < 0 || my2 >= h) {
                                continue;
                            }
                            if (qx < 0 || qx >= w || qy < 0 || qy >= h) {
                                continue;
                            }
                            int mi = my2 * w + mx2, qi = qy * w + qx;
                            if (!mask0[mi] || mask0[qi]) {
                                continue;
                            }

                            var cm = pixelsBottomUp[mi];
                            var cq = pixelsBottomUp[qi];
                            var cp = pixelsBottomUp[i];
                            double dR = cm.r - (double)cq.r, dG = cm.g - (double)cq.g,
                                   dB = cm.b - (double)cq.b, dA = cm.a - (double)cq.a;
                            double dirSq = dR * dR + dG * dG + dB * dB + dA * dA;
                            if (dirSq < AaMinContrastSq) {
                                continue; // 平坦(m≈q) → 混合が定義できない
                            }

                            double pR = cp.r - (double)cq.r, pG = cp.g - (double)cq.g,
                                   pB = cp.b - (double)cq.b, pA = cp.a - (double)cq.a;
                            double t = (pR * dR + pG * dG + pB * dB + pA * dA) / dirSq;
                            if (t < AaBlendMin) {
                                continue;          // ほぼ q(背景側) → 吸収しない
                            }
                            double residSq = pR * pR + pG * pG + pB * pB + pA * pA - t * t * dirSq;
                            if (residSq > AaResidFracSq * dirSq) {
                                continue; // 別色 → 吸収しない
                            }
                            mask[i] = true;
                            local++;
                            break;
                        }
                    }
                    return local;
                },
                local => Interlocked.Add(ref added, local));
            return added;
        }

        /// <summary>IncludeAaTransition の 1 パス。追加した画素数を返す。</summary>
        static int IncludeAaTransitionPass(bool* mask, Color32* pixelsBottomUp, int w, int h, int d,
                                           System.Threading.CancellationToken token = default) {
            using var scratch = new WorkScratch();
            var distIn = DistanceToOpposite(scratch, mask, w, h, inside: true, token);
            var distOut = DistanceToOpposite(scratch, mask, w, h, inside: false, token);

            // 確信領域の局所色統計(SnapBoundary と同じ粗グリッド集計・現マスク基準)。
            // 外側統計は近傍(d 超)と遠方(2d 超)の 2 系統を持ち、判定はユニオン(どちらかの
            // 軸で実混合なら包含)にする。近傍軸だけだと、パーツ縁の soft skirt(ぼかし縁)が
            // 帯幅を超えて伸びる素材で外側平均が skirt 自身に汚染され、skirt 画素の混合率が
            // 0 に見えて取り残される(実測: 遠方軸のみへの置換は逆に隣接パーツ汚染で悪化。
            // 軸の原点=外側平均の近傍にある画素は t≈0 で弾かれる構造のため、ユニオンは
            // どちらかの軸が汚染されても誤包含になりにくい)。
            // 並列化はグリッド行単位(1 タスク = 画素行 d 本)でセル書き込みを共有しない=決定的。
            var po = MakeParallelOptions(token);
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            long* sumIn = scratch.Long(gw * gh * 4, clear: true);
            long* sumOutNear = scratch.Long(gw * gh * 4, clear: true);
            long* sumOutFar = scratch.Long(gw * gh * 4, clear: true);
            int* cntIn = scratch.Int(gw * gh, clear: true);
            int* cntOutNear = scratch.Int(gw * gh, clear: true);
            int* cntOutFar = scratch.Int(gw * gh, clear: true);
            int farDist = 2 * d;
            Parallel.For(0, gh, po, gy => {
                int gRow = gy * gw;
                int yEnd = System.Math.Min(h, (gy + 1) * d);
                for (int y = gy * d; y < yEnd; y++) {
                    int row = y * w;
                    for (int x = 0; x < w; x++) {
                        int i = row + x;
                        bool inConf = mask[i] && distIn[i] > d;
                        bool outNear = !mask[i] && distOut[i] > d;
                        if (!inConf && !outNear) {
                            continue;
                        }
                        int g = gRow + x / d;
                        var c = pixelsBottomUp[i];
                        int o = g * 4;
                        if (inConf) {
                            sumIn[o] += c.r; sumIn[o + 1] += c.g; sumIn[o + 2] += c.b; sumIn[o + 3] += c.a;
                            cntIn[g]++;
                        } else {
                            sumOutNear[o] += c.r; sumOutNear[o + 1] += c.g;
                            sumOutNear[o + 2] += c.b; sumOutNear[o + 3] += c.a;
                            cntOutNear[g]++;
                            if (distOut[i] > farDist) {
                                sumOutFar[o] += c.r; sumOutFar[o + 1] += c.g;
                                sumOutFar[o + 2] += c.b; sumOutFar[o + 3] += c.a;
                                cntOutFar[g]++;
                            }
                        }
                    }
                }
            });

            // 外側セルモード(MinCellSamples 以上のセル)。混合軸の原点は「画素色に最も近い
            // 外側モード」を使う: プール平均だと隣接する別パーツの色が混入した窓で原点が
            // 実在しない中間色(例: 暗色+白の灰)へずれ、素の背景画素が「原点→内側」軸上の
            // 実混合に見えて層状に吸収され続ける(実測: ギャップ突出の最大増幅段だった)。
            // 最近傍モードなら素の背景画素は原点そのもの(t≈0)で弾かれる。
            double* meanNear = scratch.Double(gw * gh * 4, clear: true);
            double* meanFar = scratch.Double(gw * gh * 4, clear: true);
            bool* validNear = scratch.Bool(gw * gh, clear: true);
            bool* validFar = scratch.Bool(gw * gh, clear: true);
            ComputeCellModes(sumOutNear, cntOutNear, gw * gh, 4, meanNear, validNear);
            ComputeCellModes(sumOutFar, cntOutFar, gw * gh, 4, meanFar, validFar);

            // 判定はパス開始時のマスク由来の distOut に対して行い、書き込みは追加のみ
            // (決定的・順序非依存。統計は確信領域=帯外なので追加書き込みの影響を受けない)。
            // 行並列でも決定的: 各画素の mask 読みは自インデックスのみで、そこへ書くのは
            // 自イテレーションだけ(近傍の mask は読まない)。added は行ローカル合算。
            int added = 0;
            Parallel.For(0, h, po,
                () => 0,
                (y, _, local) => {
                    int row = y * w;
                    int gy = y / d;
                    for (int x = 0; x < w; x++) {
                        int i = row + x;
                        if (mask[i] || distOut[i] > d) {
                            continue; // 境界外側の帯のみ
                        }

                        int gx = x / d;
                        var c = pixelsBottomUp[i];
                        long ir = 0, ig = 0, ib = 0, ia = 0;
                        long nr = 0, ng = 0, nb = 0, na = 0, fr = 0, fg = 0, fb = 0, fa = 0;
                        int ic = 0, nc = 0, fc = 0;
                        double bestNearD = double.MaxValue, bestFarD = double.MaxValue;
                        int bestNearO = -1, bestFarO = -1;
                        for (int radius = MinWindowRadius; radius <= MaxWindowRadius; radius += 2) {
                            ir = ig = ib = ia = 0; ic = 0;
                            nr = ng = nb = na = 0; nc = 0;
                            fr = fg = fb = fa = 0; fc = 0;
                            bestNearD = bestFarD = double.MaxValue;
                            bestNearO = bestFarO = -1;
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
                                    }
                                    if (cntOutNear[g] > 0) {
                                        nr += sumOutNear[o]; ng += sumOutNear[o + 1];
                                        nb += sumOutNear[o + 2]; na += sumOutNear[o + 3];
                                        nc += cntOutNear[g];
                                        if (validNear[g]) {
                                            double dd = Dist2Mean(c, meanNear, o);
                                            if (dd < bestNearD) { bestNearD = dd; bestNearO = o; }
                                        }
                                    }
                                    if (cntOutFar[g] > 0) {
                                        fr += sumOutFar[o]; fg += sumOutFar[o + 1];
                                        fb += sumOutFar[o + 2]; fa += sumOutFar[o + 3];
                                        fc += cntOutFar[g];
                                        if (validFar[g]) {
                                            double dd = Dist2Mean(c, meanFar, o);
                                            if (dd < bestFarD) { bestFarD = dd; bestFarO = o; }
                                        }
                                    }
                                }
                            }
                            if (ic >= MinSamples && nc >= MinSamples) {
                                break;
                            }
                        }
                        if (ic < MinSamples) {
                            continue; // 内側統計不足 → 触らない
                        }

                        double inR = ir / (double)ic, inG = ig / (double)ic,
                               inB = ib / (double)ic, inA = ia / (double)ic;
                        bool include = false;
                        if (nc >= MinSamples) {
                            include = bestNearO >= 0
                                ? IsAaBlend(c, inR, inG, inB, inA, meanNear[bestNearO],
                                            meanNear[bestNearO + 1], meanNear[bestNearO + 2],
                                            meanNear[bestNearO + 3])
                                : IsAaBlend(c, inR, inG, inB, inA, nr / (double)nc,
                                            ng / (double)nc, nb / (double)nc, na / (double)nc);
                        }
                        if (!include && fc >= MinSamples) {
                            include = bestFarO >= 0
                                ? IsAaBlend(c, inR, inG, inB, inA, meanFar[bestFarO],
                                            meanFar[bestFarO + 1], meanFar[bestFarO + 2],
                                            meanFar[bestFarO + 3])
                                : IsAaBlend(c, inR, inG, inB, inA, fr / (double)fc,
                                            fg / (double)fc, fb / (double)fc, fa / (double)fc);
                        }
                        if (include) {
                            mask[i] = true;
                            local++;
                        }
                    }
                    return local;
                },
                local => Interlocked.Add(ref added, local));
            return added;
        }

        /// <summary>
        /// 画素 c が「外側原点色 → 内側平均色」の線分上の実混合(混合率 AaBlendMin 以上)かを
        /// 判定する。原点は呼び出し側が選ぶ(最近傍外側モード、無ければプール平均)。
        /// </summary>
        static bool IsAaBlend(Color32 c, double inR, double inG, double inB, double inA,
                              double outR, double outG, double outB, double outA) {
            double dR = inR - outR, dG = inG - outG, dB = inB - outB, dA = inA - outA;
            double dirSq = dR * dR + dG * dG + dB * dB + dA * dA;
            if (dirSq < AaMinContrastSq) {
                return false; // 低コントラスト境界 → 混合軸が無意味
            }

            double pR = c.r - outR, pG = c.g - outG, pB = c.b - outB, pA = c.a - outA;
            double t = (pR * dR + pG * dG + pB * dB + pA * dA) / dirSq;
            if (t < AaBlendMin) {
                return false;          // ほぼ背景 → 含めない
            }
            double residSq = pR * pR + pG * pG + pB * pB + pA * pA - t * t * dirSq;
            return residSq <= AaResidFracSq * dirSq;   // 線分から外れる別色は含めない
        }

        /// <summary>帯内画素を 3x3 多数決(5/9 以上)で平滑化する。読みはスナップ結果の
        /// スナップショット、書きは mask(決定的・順序非依存)。</summary>
        static void MajoritySmoothBand(WorkScratch scratch, bool* mask, bool* mask0, ushort* distIn, ushort* distOut,
                                       int d, int w, int h,
                                       System.Threading.CancellationToken token = default) {
            bool* snapped = scratch.CopyOf(mask, w * h);
            Parallel.For(1, h - 1, MakeParallelOptions(token), y => {
                int row = y * w;
                for (int x = 1; x < w - 1; x++) {
                    int i = row + x;
                    bool inBand = mask0[i] ? distIn[i] <= d : distOut[i] <= d;
                    if (!inBand) {
                        continue;
                    }
                    int n = 0;
                    if (snapped[i - w - 1]) {
                        n++;
                    }
                    if (snapped[i - w]) {
                        n++;
                    }
                    if (snapped[i - w + 1]) {
                        n++;
                    }
                    if (snapped[i - 1]) {
                        n++;
                    }
                    if (snapped[i]) {
                        n++;
                    }
                    if (snapped[i + 1]) {
                        n++;
                    }
                    if (snapped[i + w - 1]) {
                        n++;
                    }
                    if (snapped[i + w]) {
                        n++;
                    }
                    if (snapped[i + w + 1]) {
                        n++;
                    }
                    mask[i] = n >= 5;
                }
            });
        }
    }
}
