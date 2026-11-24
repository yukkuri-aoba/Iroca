// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // ZoneAutoTuner: 許容範囲の切り詰め。導出した許容範囲より小さい値で、クリックした部品を覆えていて、選択の境目が
    // 「入力で見える縁」に沿う所があれば、そこまで下げる。
    //
    // 【解決する問題】許容範囲の導出は「クリック色の近くの窓に入る画素の距離の 95% 点 + 余白」で、下限(有彩 ChromaTolMin・
    // 無彩 AchromaTolMin)と余白(証拠を覆える最小値の EvTolHeadroom 倍)は、どれも広げる側へ倒してある。平塗りの素材や、
    // 近い色の別の部品がある素材では、要る幅がずっと小さく、導出した幅までの間に別の部品が丸ごと入る
    // (実測 2026-11-14: 赤い布で、導出 0.08 に対して 0.015。隣の赤茶の部品が丸ごと入って設計 IoU 0.79)。
    //
    // 【考え方】人が許容範囲を下げていって止めるのは、選択の境目が部品の縁(見える線)に乗った所。境目が面の途中
    // (なだらかな陰・ほとんど同じ色の段)を横切る所では止めない。これを実マッチャーで数える:
    //   - 許容範囲 t を等比の目盛りで下げ、格子の点ごとに「最初に選ばれる目盛り」を求める。
    //   - 目盛り t で切ると選択の境目になる所 = 「t で選ばれる点」と「t では外れるが、いまの許容範囲では選ばれる点」が
    //     隣り合う組。そのうち入力の色の差が見分けられない組(継ぎ目)は、切ると出力にだけ線が出る所。
    //   - 継ぎ目が、切った後の選択の輪郭のごく一部(EvCutSeamMaxFrac)までなら、境目は見える縁に沿っている。
    // 条件(すべて満たす、いちばん小さい t):
    //   1. 証拠(クリックした部品)の芯を、いまの許容範囲が覆っている分の EvCutEvidenceHold 以上は覆う。
    //   2. 切る得がある(選択が EvCutMinGain 以上減る)。
    //   3. 継ぎ目が輪郭の EvCutSeamMaxFrac 以下。
    // いちばん小さい t を取るのは、次の色のまとまりの裾から離れるため(裾が入り始めた許容範囲では、後段がそこから広げる)。
    // 上げる方向には使わない。条件に当たる所が無ければ何もしない。
    //
    // 【切らない場合(実測で確かめた形。dev_safe の autotune_gap_2026-11-14.md)】
    //   - 同じ面の続きを切る: 布のしわの陰・肌のわずかな色の段・2 色の髪のなだらかな境。選択の数だけを見る規則(横ばいの
    //     あとの急な伸びの手前で切る)は、これを別の部品と見分けられず、設計 IoU は上がるのに陰や色の段が塗り残った。
    //
    // 【グレーモードの照合(無彩のサンプル)】境目の探し方は同じだが、切った値の使い方が違う(呼び出し側の
    // AnalyzeWithEvidence)。グレーモードでは許容範囲が縁のぼかしの幅も兼ねていて(全強度は許容範囲の
    // 1 − AchromaEdgeSoftness = 3 割まで)、許容範囲を下げると素材の内側まで薄く塗られる。そこで許容範囲は変えず、
    // 切った値を照合の上限距離(ColorZone.matchCutoff)に入れ、それより遠い画素だけを選ばない(黒い布に対する灰色の部品など)。
    // 目盛り t の照合は許容範囲を t にしただけのもので、主の照合の上限は「距離 ≥ 上限なら選ばない」なので、上限 t で外れる
    // 画素は目盛り t で外れる画素とほぼ同じ(彩度整合ゲートの罰は許容範囲に比例するので、色味の乗った灰色のサンプルでは
    // 少しずれる)。違うのは残る画素の強度で、上限のときは元の許容範囲のぼかしのまま。
    internal static unsafe partial class ZoneAutoTuner {
        // 許容範囲の目盛り(等比)。導出した値から下へこの比で刻む。
        private const float EvCutGridRatio = 1.15f;
        // 目盛りの下端。これより小さい許容範囲は 8bit の量子化の幅に近く、選択が色の点の集まりになる。
        private const float EvCutGridMin = 0.008f;
        // 切った後も、いまの許容範囲が覆っている証拠の芯の、この割合は覆う。
        private const float EvCutEvidenceHold = 0.90f;
        // いまの許容範囲でも証拠の芯をほとんど覆えていないなら、証拠と選択が重なっていないので切らない。
        private const float EvCutMinCoreCoverage = 0.02f;
        // 切る得: 選択の点の数が、いまの (1 + これ) 分の 1 以下になる。得の小さい切り詰めはしない。
        private const float EvCutMinGain = 0.10f;
        // 見分けられない色の差(OkLab の ΔE)。隣り合う 2 点の差がこれ未満なら、入力では境目が見えない。
        private const float EvCutSeamDeltaE = 0.02f;
        // 継ぎ目(切ると境目になる、入力では見分けられない組)が、切った後の選択の輪郭の組に占めてよい割合。
        private const float EvCutSeamMaxFrac = 0.01f;
        // 格子の点ごとの「最初に選ばれる目盛り」の、選ばれない印。目盛りは多くても 30 段ほど。
        private const byte EvCutNoStep = byte.MaxValue;

        /// <summary>
        /// 導出した許容範囲(result.tolerance)より小さい値で、証拠の芯をほぼ覆えていて、選択の境目が入力で見える縁に沿う所が
        /// あれば、そのいちばん小さい値を返す。無ければ result.tolerance をそのまま返す。数えるのは基底のマッチだけ
        /// (ハイライト系は入れない)。
        /// </summary>
        /// <param name="core">証拠の芯(格子の点)。</param>
        /// <param name="fillReach">穴埋めの緩和マッチが届いてよい許容範囲(切った所から上へ、境目が見える縁に沿っている間)。</param>
        /// <param name="grayMode">照合がグレーモード(無彩のサンプル)か。真なら、呼び出し側は許容範囲でなく照合の上限距離
        /// (<see cref="ColorZone.matchCutoff"/>)で切る(冒頭の説明)。切らずに返すときも、目盛りを作る前に決まっていれば入れる。</param>
        /// <param name="diag">診断(切ったか、どこからどこへ切ったか)。</param>
        private static float CutToleranceAtVisibleBoundary(ColorZone aZone, in TuneResult result, HsvGrid hsv,
            Color32* pixels, int w, int h, List<GridPt> core, bool* excluded, int maskW, int maskH,
            out float fillReach, out bool grayMode, out string diag) {
            diag = " tolCut=-";
            grayMode = false;
            float tol = result.tolerance;
            fillReach = tol;
            int m = 1;
            for (float t = tol; t / EvCutGridRatio >= EvCutGridMin; t /= EvCutGridRatio) {
                m++;
            }
            if (core.Count == 0 || m < 2) {
                return tol;
            }
            var top = BuildSimZone(aZone, result);
            Color.RGBToHSV(top.sampleColor, out _, out float topS, out float topV);
            // グレーモードでも境目の探し方は同じ(切った値を照合の上限距離に入れるのは呼び出し側。冒頭の説明)。
            grayMode =topS <= ColorZone.GrayModeEffectiveChromaThreshold(topV, top.chromaThreshold);
            // 目盛り: 上端がいまの値、下へ等比。
            var ts = new float[m];
            ts[m - 1] = tol;
            for (int i = m - 2; i >= 0; i--) {
                ts[i] = ts[i + 1] / EvCutGridRatio;
            }
            var matchers = new ColorZone.Matcher[m];
            var trial = result;
            for (int i = 0; i < m; i++) {
                trial.tolerance = ts[i];
                var sim = BuildSimZone(aZone, trial);
                sim.highlightRecovery = false;
                matchers[i] = new ColorZone.Matcher(sim);
            }

            // 格子の点ごとに「最初に選ばれる目盛り」を求める。基底のマッチは許容範囲に対して単調(距離とゲートは許容範囲に
            // 依らず、選ばれるのは距離 < 許容範囲のとき)なので、上端で選ばれない点は 1 回の照合で外れ、選ばれる点は
            // 二分探索で済む。母集団は CountOnGrid と同じ(不透明・除外マスクの外)。行ごとに自分の行だけを書く。
            int stride = hsv.stride, gw = hsv.gw;
            var ml = GridMaskLookup.Build(excluded, maskW, maskH, w, h, stride, gw, hsv.gh);
            byte* firstAt = hsv.scratch.Byte(gw * hsv.gh, false);
            new Span<byte>(firstAt, gw * hsv.gh).Fill(EvCutNoStep);
            // 「最初に選ばれる目盛り」は色だけで決まる(FirstSelectingStepMemo)ので、格子と芯で同じ控えを使う。
            int* colorId = hsv.colorId;
            byte* memo = RentStepMemo(hsv, matchers);
            int[] first = ParallelGridCounts(hsv, m, (gy, local) => {
                int y = gy * stride;
                int rowStart = y * w;
                int grow = gy * gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++) {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < PixelProcessor.OpaqueAlphaMin) {
                        continue;
                    }
                    if (ml != null && excluded[ml.Row[gy] + ml.Col[gx]]) {
                        continue;
                    }
                    int cell = grow + gx;
                    var col = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                    int id = colorId != null ? colorId[rowStart + x] : -1;
                    int k = FirstSelectingStepMemo(matchers, memo, id, hsv.h[cell], hsv.s[cell], hsv.v[cell], col, x, y, w, h);
                    if (k >= 0) {
                        local[k]++;
                        firstAt[cell] = (byte)k;
                    }
                }
            });
            // 証拠の芯。区間ごとに並列に数えて足す(整数の和なので逐次と同じ値)。
            const int Chunk = 4096;
            int nc = core.Count;
            var parts = new int[(nc + Chunk - 1) / Chunk][];
            Parallel.For(0, parts.Length, MatcherLoopOptions(hsv), ci => {
                var local = new int[m];
                int end = Mathf.Min(nc, (ci + 1) * Chunk);
                for (int i = ci * Chunk; i < end; i++) {
                    var p = core[i];
                    Color32 c = pixels[p.y * w + p.x];
                    var col = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                    int id = colorId != null ? colorId[p.y * w + p.x] : -1;
                    int k = FirstSelectingStepMemo(matchers, memo, id, hsv.h[p.gi], hsv.s[p.gi], hsv.v[p.gi], col, p.x, p.y, w, h);
                    if (k >= 0) {
                        local[k]++;
                    }
                }
                parts[ci] = local;
            });
            hsv.ReturnColorMemo(memo);
            // 累積: n[i] = 目盛り i で選ばれる点の数(1 以上に切り上げ。比を取るため)、e[i] = 証拠の芯の被覆。
            var n = new double[m];
            var e = new double[m];
            long accN = 0, accE = 0;
            for (int i = 0; i < m; i++) {
                accN += first[i];
                for (int ci = 0; ci < parts.Length; ci++) {
                    accE += parts[ci][i];
                }
                n[i] = Math.Max(1L, accN);
                e[i] = accE / (double)nc;
            }
            if (e[m - 1] < EvCutMinCoreCoverage) {
                return tol;
            }

            double hold = EvCutEvidenceHold * e[m - 1];
            CutSeamTable seams = null;   // 継ぎ目の表。最初に数えるときに 1 回だけ作る(作らずに抜けるなら作らない)
            // 数え直す目盛りの下端: 証拠を覆える最初の目盛り(下の繰り返しはここより下で数えない。e は増える一方)。
            int minCut = 0;
            while (minCut < m - 1 && e[minCut] < hold) {
                minCut++;
            }
            int cut = -1;
            long seam = 0, outline = 0;
            for (int i = 0; i < m - 1; i++) {
                if (e[i] < hold) {
                    continue;   // ここで止めると、いまの許容範囲が覆っている証拠を覆えない
                }
                if (n[m - 1] < n[i] * (1.0 + EvCutMinGain)) {
                    break;      // n は増える一方なので、ここから上は切る得が無い
                }
                seams ??= CutSeamTable.Build(hsv, pixels, w, firstAt, m, minCut);
                seams.Count(i, m - 1, out seam, out outline);
                if (seam > EvCutSeamMaxFrac * outline) {
                    continue;   // 境目が、入力では見えない所を通る(同じ面の続きを切っている)
                }
                cut = i;
                break;
            }
            if (cut < 0) {
                return tol;
            }
            // 切った所から上へ、境目がまだ見える縁に沿っている(= 切り落とした色のまとまりが入り始めていない)目盛りまで。
            int hi = cut;
            while (hi + 1 <= m - 1) {
                seams ??= CutSeamTable.Build(hsv, pixels, w, firstAt, m, minCut);
                seams.Count(hi + 1, m - 1, out long s2, out long o2);
                if (s2 > EvCutSeamMaxFrac * o2) {
                    break;
                }
                hi++;
            }
            fillReach = ts[hi];
            diag = string.Format(CultureInfo.InvariantCulture, " tolCut={0:F4}>{1:F4} cutSeam={2}/{3} eKeep={4:F3} reach={5:F4}",
                tol, ts[cut], seam, outline, e[cut] / e[m - 1], ts[hi]);
            return ts[cut];
        }

        /// <summary>
        /// 目盛り cut で切ったときの、継ぎ目の組の数 seam と、切った後の選択の輪郭の組の数 outline(どちらも格子の 4 近傍)。
        /// 輪郭 = cut 以下で選ばれる点と、そうでない点が隣り合う組。継ぎ目 = そのうち、相手が cut より上・top 以下で選ばれる点
        /// (切ると外れる点)で、入力の色の差が EvCutSeamDeltaE 未満の組。
        /// cut ごとに格子をなめ直す(1 回の解析で最大 14 回)代わりに、格子を 1 回なめて表を作り、cut ごとの数は表の和で求める。
        /// 隣り合う 2 点の「最初に選ばれる目盛り」を (小さい方 a, 大きい方 b) の組にして数える(選ばれない印は m 番目に置く)。
        /// 組が cut の輪郭になるのは a ≤ cut &lt; b のとき、継ぎ目になるのはそのうち b ≤ top で色の差が見分けられないとき。
        /// 不変の根拠: firstAt は表を作った後に書き換えないので、どの cut・top でも、cut ごとに数えたときと同じ組を数える。
        /// 数は整数の和なので、足す順に依らず同じ値になる(色の差の式・比べ方も前と同じ)。
        /// </summary>
        private sealed class CutSeamTable {
            private int n;            // 表の一辺(m + 1)
            private int[] outlineTab; // [a * n + b](a &lt; b)
            private int[] seamTab;    // [a * n + b](a &lt; b ≤ m − 1。色の差が見分けられない組)
            private int minCut;       // 数えてよい cut の下端

            /// <param name="minCut">この表で数える cut の下端。b ≤ minCut の組はどの cut ≥ minCut でも輪郭にならないので数えない
            /// (入力の色の差を求める組を減らす)。</param>
            internal static CutSeamTable Build(HsvGrid hsv, Color32* pixels, int w, byte* firstAt, int m, int minCut) {
                int gw = hsv.gw, gh = hsv.gh, stride = hsv.stride;
                int n = m + 1;
                int nn = n * n;
                float seam2 = EvCutSeamDeltaE * EvCutSeamDeltaE;
                // 前半 nn が輪郭、後半 nn が継ぎ目。
                int[] total = ParallelGridCounts(hsv, 2 * nn, (gy, local) => {
                    byte* row = firstAt + gy * gw;
                    int py = gy * stride * w;
                    for (int gx = 0; gx < gw; gx++) {
                        int f = row[gx];
                        // 右と下の隣だけを見る(組を 1 回ずつ数える)。
                        for (int dir = 0; dir < 2; dir++) {
                            int g2;
                            int p2;
                            if (dir == 0) {
                                if (gx + 1 >= gw) {
                                    continue;
                                }
                                g2 = row[gx + 1];
                                p2 = py + (gx + 1) * stride;
                            } else {
                                if (gy + 1 >= gh) {
                                    continue;
                                }
                                g2 = row[gx + gw];
                                p2 = py + stride * w + gx * stride;
                            }
                            if (f == g2) {
                                continue;   // どの目盛りで切っても同じ側(輪郭にならない)
                            }
                            int a = f < g2 ? f : g2;
                            int b = f < g2 ? g2 : f;
                            if (b == EvCutNoStep) {
                                b = m;
                            }
                            if (b <= minCut) {
                                continue;   // a ≤ cut < b になる cut ≥ minCut が無い
                            }
                            int idx = a * n + b;
                            local[idx]++;
                            if (b == m) {
                                continue;   // 相手はいまの許容範囲でも選ばれていない(継ぎ目にならない)
                            }
                            Color32 ca = pixels[py + gx * stride], cb = pixels[p2];
                            OkLab.RgbToOklab(ca.r, ca.g, ca.b, out float aL, out float aa, out float ab);
                            OkLab.RgbToOklab(cb.r, cb.g, cb.b, out float bL, out float ba, out float bb);
                            float dL = aL - bL, da = aa - ba, db = ab - bb;
                            if (dL * dL + da * da + db * db < seam2) {
                                local[nn + idx]++;
                            }
                        }
                    }
                });
                var t = new CutSeamTable { n = n, minCut = minCut, outlineTab = new int[nn], seamTab = new int[nn] };
                Array.Copy(total, 0, t.outlineTab, 0, nn);
                Array.Copy(total, nn, t.seamTab, 0, nn);
                return t;
            }

            /// <summary>目盛り cut で切ったときの継ぎ目と輪郭の組の数。top は m − 1 以下、cut は minCut 以上。</summary>
            internal void Count(int cut, int top, out long seam, out long outline) {
                if (cut < minCut) {
                    throw new ArgumentOutOfRangeException(nameof(cut));
                }
                seam = 0;
                outline = 0;
                for (int a = 0; a <= cut; a++) {
                    for (int b = cut + 1; b < n; b++) {
                        outline += outlineTab[a * n + b];
                        if (b <= top) {
                            seam += seamTab[a * n + b];
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 目盛りの順(許容範囲の小さい順)に並べた照合のうち、この画素を最初に選ぶ(strength &gt; 0)ものの添字。
        /// 上端でも選ばれなければ -1。選択が許容範囲に対して単調であることを使う(二分探索)。
        /// </summary>
        private static int FirstSelectingStep(ColorZone.Matcher[] matchers, float ph, float ps, float pv, Color col,
            int x, int y, int w, int h) {
            int hi = matchers.Length - 1;
            matchers[hi].GetMatchScoresPrecomputedHSV(ph, ps, pv, col, x, y, w, h, out float top, out _, out _);
            if (top <= 0f) {
                return -1;
            }
            int lo = 0;
            while (lo < hi) {
                int mid = (lo + hi) >> 1;
                matchers[mid].GetMatchScoresPrecomputedHSV(ph, ps, pv, col, x, y, w, h, out float s, out _, out _);
                if (s > 0f) {
                    hi = mid;
                } else {
                    lo = mid + 1;
                }
            }
            return lo;
        }

        /// <summary>
        /// <see cref="FirstSelectingStep"/> を色ごとに 1 回だけ求める版。控え(<paramref name="memo"/>。<see cref="RentStepMemo"/>)には
        /// 段の添字 + 2 を置く(0 = まだ、1 = どの段でも選ばれない)。memo が null なら毎回求める。
        /// 不変の根拠: どの段の照合の結果も色(と色から求めた HSV)だけで決まり、同じ番号の画素は同じ色(HsvGrid の説明)なので、
        /// 最初に選ばれる段も番号ごとに同じ。
        /// </summary>
        private static int FirstSelectingStepMemo(ColorZone.Matcher[] matchers, byte* memo, int id,
            float ph, float ps, float pv, Color col, int x, int y, int w, int h) {
            if (memo == null) {
                return FirstSelectingStep(matchers, ph, ps, pv, col, x, y, w, h);
            }
            int st = memo[id];
            if (st == 0) {
                st = FirstSelectingStep(matchers, ph, ps, pv, col, x, y, w, h) + 2;
                memo[id] = (byte)st;
            }
            return st - 2;
        }

        /// <summary>
        /// <see cref="FirstSelectingStepMemo"/> の控えを借りる。段の照合がどれも色だけで決まり、段の添字 + 2 が 1 バイトに収まるときだけ
        /// (収まらなければ null = 控えない)。使い終わったら hsv.ReturnColorMemo で返す。
        /// </summary>
        private static byte* RentStepMemo(HsvGrid hsv, ColorZone.Matcher[] matchers) {
            bool colorOnly = matchers.Length + 1 <= byte.MaxValue;
            for (int i = 0; i < matchers.Length && colorOnly; i++) {
                colorOnly = matchers[i].ColorOnly;
            }
            return hsv.RentColorMemo(colorOnly);
        }
    }
}
