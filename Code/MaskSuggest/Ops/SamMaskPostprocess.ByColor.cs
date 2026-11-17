// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // SamMaskPostprocess: 右クリックの提案(用途 Region・粒度 自動)の候補選び(SelectAndUpscaleByColor)。
    // クラスの説明と既定の選び方(SelectChannels)は SamMaskPostprocess.cs。
    //
    // 既定の自動は「安定な候補のうち面積最大」(SelectChannels)。テクスチャの配置図では、大きい候補ほど、クリックした部品に
    // くっついた別の部品(ベストの下の襟・パンツの上のストライプの帯から見たパンツ全体)まで含み、安定度もしきいをわずかに
    // 超えるので選ばれていた(開発側 33 被写体 × 6 クリックで、提案の 1 割以上が隣の部品だったのが 82/198 件)。
    // 部品の境目は塗り分けの縁なので、境目に沿った候補ほどロジットがはっきりして安定度が高い。隣の部品を巻き込んだ
    // 大きい候補は、巻き込んだ所の境目がぼけて安定度が下がる(実測で 0.03〜0.10 下がる)。そこで:
    //   1. 安定度がいちばん高い候補から始める(部品の境目に沿った形)。安定度がほぼ同じ(ByColorStabilityTie 以内)
    //      候補が複数あれば、その中で大きい方から始める(どちらもはっきりした形 = ツヤと部品全体のように、SAM が
    //      どちらも物として見ている。自動は部品の大きさを狙う粒度なので大きい方。小さい方は「細かい」で取れる)。
    //   2. それより大きい候補へは、増える部分の色の分布が、今の候補の色の分布と重なるときだけ広げる
    //      (同じ素材の続き・陰はそのまま広がり、色の違う隣の部品では止まる。「色が同じ・近いものは塗れて当然」の原則)。
    // 安定な候補が 1 つも無い(縁や背景の際をクリックした)ときは、既定の自動と同じにする(どれも形が定まらないので、
    // 安定度で選び分ける根拠が無い)。
    // 自動調整の証拠(MaskSuggestUse.Evidence)には当てない: 証拠の形を変えると自動調整の導き方が変わる
    // (2026-11-14、証拠をきれいにすると開発 0.7626 → 0.7540)。細かい・大きいは利用者が大きさを選んだ粒度なので変えない。
    internal static unsafe partial class SamMaskPostprocess {
        /// <summary>
        /// 色の分布を数える OkLab の升の幅。陰影で少しずれた同じ素材の色が隣の升に入る程度の幅(重なりは隣の升まで見る)。
        /// </summary>
        internal const float ByColorBinStep = 0.03f;

        /// <summary>
        /// 大きい候補へ広げてよい、増える部分と今の候補の色の分布の重なり(0..1)の下限。色の違う隣の部品は重なりが
        /// ほぼ 0(帯から見たパンツ・ベストから見た襟)。陰の付き方が違う同じ素材の別の島は 0.4 前後まで下がる。
        /// </summary>
        internal const float ByColorGrowOverlap = 0.3f;

        /// <summary>
        /// 起点を選ぶとき「安定度がほぼ同じ」とみなす差。この差以内の候補のうち大きい方から始める。
        /// </summary>
        internal const float ByColorStabilityTie = 0.02f;

        // OkLab の a・b を数える範囲(sRGB の色域は a −0.234〜0.276・b −0.312〜0.199 に収まる。外は端の升へ寄せる)。
        const float ByColorAbRange = 0.4f;

        // 色を数える画素の不透明度の下限(テクスチャの透明な所の RGB は意味を持たない)。
        const byte ByColorMinAlpha = 128;

        /// <summary>
        /// 右クリックの提案(用途 Region・粒度 自動)の候補を選んで元の寸法へ拡大する(精密化はしない。呼ぶ側が
        /// <see cref="RefineInPlace(bool*, Color32*, int, int, CancellationToken)"/> する)。選び方はファイルの先頭の説明。
        /// 妥当な候補(面積と安定度)の決め方は <see cref="SelectChannels"/> と同じ。安定な候補が無い(背景に流れた・形が
        /// 定まらない)ときと画素が無いときは、既定の自動と同じ結果を返す。
        /// growOverlap・stabilityTie は掃引用(製品は既定の定数)。
        /// </summary>
        public static Result SelectAndUpscaleByColor(float[] logits, float[] scores, int texW, int texH,
                                                     float floodRejectFrac, Color32* pixelsBottomUp,
                                                     CancellationToken token = default,
                                                     float growOverlap = ByColorGrowOverlap,
                                                     float stabilityTie = ByColorStabilityTie) {
            MeasureChannels(logits, texW, texH, out float[] area, out float[] stability);
            // 母集団は SelectChannels の安定な候補(面積が 0 でも背景洪水でもなく、安定度がしきい以上)。
            bool InPool(int c) => area[c] > 0f && area[c] < floodRejectFrac && stability[c] >= StabilityThreshold;
            if (pixelsBottomUp == null || !(InPool(1) || InPool(2) || InPool(3))) {
                return SelectAndUpscale(logits, scores, texW, texH, floodRejectFrac, (Color32*)null,
                                        MaskSuggestGranularity.Auto, token);
            }

            // 候補を元の寸法へ拡大して画素を数える。大きさは拡大した後の画素数で比べる(細い帯のような小さい部品では、
            // 低解像度の面積が 3 候補とも同じになり、どれが大きいか分からない)。
            SamImageOps.GetResizedSize(texW, texH, out int newW, out int newH);
            var masks = new NativeBools[4];
            var count = new long[4];
            try {
                float maxStability = 0f;
                for (int c = 1; c < 4; c++) {
                    if (!InPool(c)) {
                        continue;
                    }
                    masks[c] = UpscaleChannel(logits, c, texW, texH, newW, newH, token);
                    count[c] = CountTrue(masks[c].Ptr, (long)texW * texH, token);
                    if (stability[c] > maxStability) {
                        maxStability = stability[c];
                    }
                }

                // 起点: 安定度がいちばん高い候補から stabilityTie 以内の候補のうち、大きい方(同じならスコアの高い方)。
                int start = -1;
                for (int c = 1; c < 4; c++) {
                    if (masks[c] == null || stability[c] < maxStability - stabilityTie) {
                        continue;
                    }
                    if (start < 0 || count[c] > count[start] || (count[c] == count[start] && scores[c] > scores[start])) {
                        start = c;
                    }
                }

                // 起点より大きい候補へ、小さい順に、色の分布が重なる間だけ広げる。
                var larger = new System.Collections.Generic.List<int>(2);
                for (int c = 1; c < 4; c++) {
                    if (masks[c] != null && count[c] > count[start]) {
                        larger.Add(c);
                    }
                }
                larger.Sort((a, b) => count[a] != count[b] ? count[a].CompareTo(count[b]) : a.CompareTo(b));
                int chosen = start;
                foreach (int c in larger) {
                    if (IncrementColorOverlap(masks[chosen].Ptr, masks[c].Ptr, pixelsBottomUp, texW, texH, token)
                        < growOverlap) {
                        break;
                    }
                    chosen = c;
                }

                var mask = masks[chosen];
                masks[chosen] = null;   // 結果が引き取る(下の後始末で返さない)
                return new Result {
                    mask = mask,
                    channel = chosen,
                    score = scores[chosen],
                    areaFrac = area[chosen],
                    floodWarning = false,
                };
            } finally {
                for (int c = 1; c < 4; c++) {
                    masks[c]?.Release();
                }
            }
        }

        // mask(n 個)の true の数。帯ごとに数えて整数で足す(並列でも決定的)。
        static long CountTrue(bool* mask, long n, CancellationToken token) {
            var po = SamMaskRefine.MakeParallelOptions(token);
            int bands = Mathf.Max(1, CoreParallelism.Max());
            var sums = new long[bands];
            Parallel.For(0, bands, po, band => {
                long i0 = n * band / bands, i1 = n * (band + 1) / bands;
                long k = 0;
                for (long i = i0; i < i1; i++) {
                    if (mask[i]) {
                        k++;
                    }
                }
                sums[band] = k;
            });
            long total = 0;
            for (int band = 0; band < bands; band++) {
                total += sums[band];
            }
            return total;
        }

        /// <summary>
        /// 候補 cur から候補 next へ広げたときに増える部分(next にあって cur に無い不透明な画素)の色の分布と、cur の色の分布の
        /// 重なり Σ min(増える部分の割合, cur の割合を隣の升まで広げたもの)。増える部分が無ければ 1、cur に色が無ければ 0。
        /// 色は OkLab を <see cref="ByColorBinStep"/> の升で数える。整数で数えて足すので、並列でも決定的。
        /// </summary>
        internal static float IncrementColorOverlap(bool* cur, bool* next, Color32* px, int w, int h,
                                                    CancellationToken token = default) {
            int nL = Mathf.CeilToInt(1f / ByColorBinStep) + 1;
            int nAb = Mathf.CeilToInt(2f * ByColorAbRange / ByColorBinStep) + 1;
            int nBins = nL * nAb * nAb;
            var po = SamMaskRefine.MakeParallelOptions(token);
            int bands = Mathf.Max(1, Mathf.Min(h, CoreParallelism.Max()));
            var curHist = new int[bands][];
            var incHist = new int[bands][];
            Parallel.For(0, bands, po, band => {
                var hc = new int[nBins];
                var hd = new int[nBins];
                int y0 = (int)((long)h * band / bands), y1 = (int)((long)h * (band + 1) / bands);
                for (int y = y0; y < y1; y++) {
                    int row = y * w;
                    for (int x = 0; x < w; x++) {
                        int i = row + x;
                        bool inCur = cur[i];
                        if (!inCur && !next[i]) {
                            continue;
                        }
                        var c = px[i];
                        if (c.a < ByColorMinAlpha) {
                            continue;
                        }
                        OkLab.RgbToOklab(c.r, c.g, c.b, out float L, out float a, out float b);
                        int k = ColorBin(L, a, b, nL, nAb);
                        if (inCur) {
                            hc[k]++;
                        } else {
                            hd[k]++;
                        }
                    }
                }
                curHist[band] = hc;
                incHist[band] = hd;
            });
            var curSum = new long[nBins];
            var incSum = new long[nBins];
            long nCur = 0, nInc = 0;
            for (int band = 0; band < bands; band++) {
                var hc = curHist[band];
                var hd = incHist[band];
                for (int k = 0; k < nBins; k++) {
                    curSum[k] += hc[k];
                    incSum[k] += hd[k];
                }
            }
            for (int k = 0; k < nBins; k++) {
                nCur += curSum[k];
                nInc += incSum[k];
            }
            if (nInc == 0) {
                return 1f;
            }
            if (nCur == 0) {
                return 0f;
            }
            // 隣の升(3×3×3)までの cur の最大の割合と、増える部分の割合の小さい方を足す。
            double overlap = 0.0;
            for (int l = 0; l < nL; l++) {
                for (int ia = 0; ia < nAb; ia++) {
                    for (int ib = 0; ib < nAb; ib++) {
                        long d = incSum[(l * nAb + ia) * nAb + ib];
                        if (d == 0) {
                            continue;
                        }
                        long best = 0;
                        for (int dl = -1; dl <= 1; dl++) {
                            int ll = l + dl;
                            if ((uint)ll >= (uint)nL) {
                                continue;
                            }
                            for (int da = -1; da <= 1; da++) {
                                int aa = ia + da;
                                if ((uint)aa >= (uint)nAb) {
                                    continue;
                                }
                                for (int db = -1; db <= 1; db++) {
                                    int bb = ib + db;
                                    if ((uint)bb >= (uint)nAb) {
                                        continue;
                                    }
                                    long v = curSum[(ll * nAb + aa) * nAb + bb];
                                    if (v > best) {
                                        best = v;
                                    }
                                }
                            }
                        }
                        overlap += System.Math.Min(d / (double)nInc, best / (double)nCur);
                    }
                }
            }
            return (float)overlap;
        }

        // OkLab の色の升の番号(範囲の外は端の升へ寄せる)。
        static int ColorBin(float L, float a, float b, int nL, int nAb) {
            int l = Mathf.Clamp((int)(L / ByColorBinStep), 0, nL - 1);
            int ia = Mathf.Clamp((int)((a + ByColorAbRange) / ByColorBinStep), 0, nAb - 1);
            int ib = Mathf.Clamp((int)((b + ByColorAbRange) / ByColorBinStep), 0, nAb - 1);
            return (l * nAb + ia) * nAb + ib;
        }
    }
}
