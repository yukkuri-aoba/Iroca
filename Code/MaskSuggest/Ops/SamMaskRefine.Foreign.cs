// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // SamMaskRefine: クリックした塊と色の違う、離れた塊を提案から外す(DropForeignComponents)。
    // クラスの説明と共通の定数・下回りは SamMaskRefine.cs。
    internal static unsafe partial class SamMaskRefine {
        // SAM は 1 クリックに大・中・小の 3 候補を返し、自動は安定な候補のうち面積最大を採る(SamMaskPostprocess.SelectChannels)。
        // 大きい候補は「似た形の物の集まり」で、テクスチャの配置図では、クリックした島とつながらない別の部品
        // (肌をクリックして目玉・緑の球をクリックして隣の赤い球)まで含む。提案を連結成分に分け、クリック色に近い画素の
        // 割合が、クリックした成分の割合より明らかに低い成分を外す。同じ色の別の島(左右対称の布など)は割合が同じなので残る
        // (成分ごと捨てる「クリックした塊だけ残す」は、それを落として悪化した)。
        // 当てるのは自動・大きいの提案だけ(呼ぶ側が決める。SentisMaskSuggestService)。細かいは利用者が細部を狙って選ぶ粒度で、
        // 大きい候補が離れた別物を掴むことは起きない。逆に、影で暗い歯と光の当たった白い歯のように、同じ素材の明暗が別の成分に
        // 分かれていると、明るい側を外してしまう(細い描き込みの提案のファスナーで実測)。

        /// <summary>
        /// クリック色に近いとみなす OkLab の距離。成分の「クリック色の割合」を数えるための物差しで、割合はクリックした成分との
        /// 比で比べるので、値そのものより、陰影の付いた同じ素材を近いと数えられる幅であることが効く。
        /// </summary>
        internal const float ForeignNearColor = 0.08f;

        /// <summary>
        /// 外す成分の、クリック色の割合の上限(クリックした成分の割合に対する比)。これ未満の成分を外す。
        /// </summary>
        internal const float ForeignNearFracRatio = 0.5f;

        /// <summary>クリック色を取る窓の半径(px。5×5 の中央値)。クリックが縁取り・ノイズの 1 画素に当たっても地色を取る。</summary>
        const int ForeignRefRadius = 2;

        /// <summary>
        /// mask(下原点 w*h)の連結成分(4 近傍)のうち、クリック色に近い画素の割合が、クリック (clickX, clickY)(下原点)を含む成分の
        /// 割合の <paramref name="ratio"/> 倍未満の成分を外す(in-place)。外した画素数を返す。クリックがマスクの外・成分が 1 つなら
        /// 何もしない。精密化 4 段の後(ズームのときは貼り戻した後)に呼ぶ。
        /// </summary>
        public static int DropForeignComponents(bool* mask, Color32* pixelsBottomUp, int w, int h, int clickX, int clickY,
                                                CancellationToken token = default,
                                                float nearColor = ForeignNearColor, float ratio = ForeignNearFracRatio) {
            if (mask == null || pixelsBottomUp == null || (uint)clickX >= (uint)w || (uint)clickY >= (uint)h) {
                return 0;
            }
            if (!mask[clickY * w + clickX]) {
                return 0;
            }
            var po = MakeParallelOptions(token);
            if (!TryMaskBounds(mask, w, h, po, out int x0, out int y0, out int x1, out int y1)) {
                return 0;
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            ReferenceOklab(pixelsBottomUp, w, h, clickX, clickY, out float rL, out float ra, out float rb);
            float near2 = nearColor * nearColor;

            using var runs = RunLabeler.Label(mask, w, x0, y0, bw, bh, po, token);
            int nRuns = runs.Count;
            using var scratch = new WorkScratch();
            int* comp = scratch.Int(nRuns, clear: false);   // NumberComponents が全 run に書く
            int nComp = RunLabeler.NumberComponents(runs, comp);
            if (nComp <= 1) {
                return 0;
            }
            int[] rowStart = runs.RowStart;
            int* runX0 = runs.X0, runX1 = runs.X1;

            // run ごとに、クリック色に近い画素を数える(各 run の枠は自分だけが書く。並列でも決定的)。
            int* runNear = scratch.Int(nRuns, clear: false);   // 下のループが全 run に書く
            int ox = x0, oy = y0;
            Parallel.For(0, bh, po, ly => {
                Color32* row = pixelsBottomUp + (long)(ly + oy) * w + ox;
                for (int r = rowStart[ly]; r < rowStart[ly + 1]; r++) {
                    int n = 0;
                    for (int lx = runX0[r]; lx <= runX1[r]; lx++) {
                        var c = row[lx];
                        OkLab.RgbToOklab(c.r, c.g, c.b, out float L, out float a, out float b);
                        float dL = L - rL, da = a - ra, db = b - rb;
                        if (dL * dL + da * da + db * db <= near2) {
                            n++;
                        }
                    }
                    runNear[r] = n;
                }
            });
            var area = new long[nComp];
            var near = new long[nComp];
            for (int r = 0; r < nRuns; r++) {
                area[comp[r]] += runX1[r] - runX0[r] + 1;
                near[comp[r]] += runNear[r];
            }

            // クリックを含む成分。
            int cx = clickX - ox, cy = clickY - oy, k = -1;
            for (int r = rowStart[cy]; r < rowStart[cy + 1]; r++) {
                if (runX0[r] <= cx && cx <= runX1[r]) {
                    k = comp[r];
                    break;
                }
            }
            if (k < 0) {
                return 0;
            }

            // near[j] / area[j] < ratio * near[k] / area[k] の成分を外す(割り算をせずに掛けて比べる)。
            var drop = new bool[nComp];
            bool any = false;
            for (int j = 0; j < nComp; j++) {
                if (j != k && (double)near[j] * area[k] < ratio * (double)near[k] * area[j]) {
                    drop[j] = true;
                    any = true;
                }
            }
            if (!any) {
                return 0;
            }
            int removed = 0;
            for (int ly = 0; ly < bh; ly++) {
                for (int r = rowStart[ly]; r < rowStart[ly + 1]; r++) {
                    if (!drop[comp[r]]) {
                        continue;
                    }
                    int len = runX1[r] - runX0[r] + 1;
                    new System.Span<bool>(mask + (long)(ly + oy) * w + ox + runX0[r], len).Clear();
                    removed += len;
                }
            }
            return removed;
        }

        // クリックの周り(半径 ForeignRefRadius の正方形、画像の端で切る)の RGB のチャンネルごとの中央値を OkLab にする。
        static void ReferenceOklab(Color32* px, int w, int h, int cx, int cy, out float L, out float a, out float b)
            => OkLab.MedianAround(px, w, h, cx, cy, ForeignRefRadius, out L, out a, out b);
    }
}
