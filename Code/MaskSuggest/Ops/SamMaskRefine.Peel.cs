// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // SamMaskRefine: 細い描き込みから地を外す(PeelThinSpill)。クラスの説明と共通の定数・下回りは SamMaskRefine.cs。
    internal static unsafe partial class SamMaskRefine {
        /// <summary>
        /// mask(下原点 w*h)の細い連結成分(画素の ThinBandFracMin 以上が境界から d 以内)を、地の上の描き込み
        /// そのものへ合わせ直す(in-place)。精密化の最後(SnapBoundary → ExtendFringe → IncludeAaTransition の後)に
        /// 呼ぶ: ExtendFringe は地色より暗い落ち影を「地色から遠い外側」として、IncludeAaTransition は暗い縁取りと
        /// 落ち影の間を「混色」として足すので、その後で地を外す。マスク解像度で AA 遷移包含をやり直したときも、
        /// その後に呼ぶ(MaskSuggestController のコミット)。
        /// 地色(下の「地色」)は、この時点のマスクから d より離れた画素(確信外側)のセル単位モード。
        ///   1. 地: 細い成分から GrowReachCells × d 離れた外周から、隣同士の色の差が PeelStepSq 以下の画素を
        ///      辿った領域。陰影(スライダーの下の落ち影など)は滑らかに続くので地に入り、描き込みの縁(1px で大きく
        ///      色が変わる)で止まる。
        ///      外周から辿れなくても、最も近い地の画素と同じ色(PeelLocalSameSq 以下)の画素は地とする(描き込みに
        ///      囲まれた地の袋)。
        ///   2. 成分から地を外す。地の上の描き込みと言えない成分(PeelMinDetailFrac の門)は触らない。
        ///   3. 外した成分の近く(GrowReachCells × d 以内)で地に届かない画素(描き込み)のうち、成分から
        ///      GrowGap px 以内の隙間で連なるものを足す。SAM が構造の端で切った続き(ファスナーの末端の歯)と、
        ///      帯の再分類が近くの別の明るい線を外側の色と取り違えて落とした画素を拾う。
        /// 細さはスナップ後の形で見る(SAM の形が幅広の塊だと、元マスクの奥は「確信の内側」として再分類され
        /// ずに残るため)。連結成分は行の連(ラン)の union-find で求める(大きな提案でも画素ごとの探索をしない)。
        /// 細い成分が無ければ、外接矩形の距離変換 1 回だけで戻る。
        /// 地の塗りつぶしと足す探索は逐次だが、対象は細い成分の周りだけで画素数は小さい。
        /// </summary>
        /// <param name="sizeW">帯幅を決める画像の寸法(0 なら w・h)。切り出しで回すとき全画像の寸法を渡す。</param>
        public static void PeelThinSpill(bool* mask, Color32* pixelsBottomUp, int w, int h,
                                         System.Threading.CancellationToken token = default,
                                         int sizeW = 0, int sizeH = 0) {
            if (mask == null || pixelsBottomUp == null) {
                return;
            }
            if (sizeW <= 0 || sizeH <= 0) { sizeW = w; sizeH = h; }
            var px = pixelsBottomUp;
            var po = MakeParallelOptions(token);
            int d = BandHalfWidth(sizeW, sizeH);
            int reach = GrowReachCells * d;
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;

            // マスクの外接矩形を、足す探索の届く距離と地色の窓(最大半径のセル)の分だけ広げた矩形の中で扱う。
            if (!TryMaskBounds(mask, w, h, po, out int x0, out int y0, out int x1, out int y1)) {
                return;
            }
            int margin = PeelRectMargin(d);
            x0 = Mathf.Max(0, x0 - margin); y0 = Mathf.Max(0, y0 - margin);
            x1 = Mathf.Min(w - 1, x1 + margin); y1 = Mathf.Min(h - 1, y1 + margin);
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            int ox = x0, oy = y0;   // ラムダ用の写し
            // 外接矩形の大きさの作業配列(提案が大きいと画像全体に近い)はプールから借りる。
            using var scratch = new WorkScratch();

            // 行ごとの連(マスク画素の横並び。右端を含む)と、その連結成分(RunLabeler。矩形の左上が (0, 0))。
            using var runs = RunLabeler.Label(mask, w, ox, oy, bw, bh, po, token);
            int nRuns = runs.Count;
            int[] runFirst = runs.RowStart;
            int* runX0 = runs.X0, runX1 = runs.X1, runRoot = runs.Root;
            // 成分の番号(0 = マスク外。1 から、成分の最初の画素の順)。
            int* runComp = scratch.Int(nRuns, clear: false);   // 下のループが全 run に書く
            int nComp = 1;
            for (int r = 0; r < nRuns; r++) {
                runComp[r] = runRoot[r] == r ? nComp++ : runComp[runRoot[r]];
            }

            // 細さ: 境界から d 以内の画素の割合。矩形の外はすべてマスク外なので、距離は矩形の中だけで
            // 求めれば同じ。
            bool* local = scratch.Bool(bw * bh, clear: false);   // 下で全行を写す
            Parallel.For(0, bh, po, ly =>
                new System.ReadOnlySpan<bool>(mask + (ly + oy) * w + ox, bw).CopyTo(new System.Span<bool>(local + ly * bw, bw)));
            var distIn = DistanceToOpposite(scratch, local, bw, bh, inside: true, token);
            int* runBand = scratch.Int(nRuns, clear: false);   // 下のループが全 run に書く
            Parallel.For(0, bh, po, ly => {
                for (int r = runFirst[ly]; r < runFirst[ly + 1]; r++) {
                    int n = 0, row = ly * bw;
                    for (int lx = runX0[r]; lx <= runX1[r]; lx++) {
                        if (distIn[row + lx] <= d) {
                            n++;
                        }
                    }
                    runBand[r] = n;
                }
            });
            var area = new int[nComp];
            var band = new int[nComp];
            for (int r = 0; r < nRuns; r++) {
                area[runComp[r]] += runX1[r] - runX0[r] + 1;
                band[runComp[r]] += runBand[r];
            }
            var thin = new bool[nComp];
            bool anyThin = false;
            for (int c = 1; c < nComp; c++) {
                thin[c] = band[c] >= ThinBandFracMin * area[c];
                anyThin |= thin[c];
            }
            if (!anyThin) {
                return;
            }
            token.ThrowIfCancellationRequested();

            // 成分の番号を画素へ(矩形内の添字。0 = マスク外)。
            int* label = scratch.Int(bw * bh, clear: true);
            Parallel.For(0, bh, po, ly => {
                int row = ly * bw;
                for (int r = runFirst[ly]; r < runFirst[ly + 1]; r++) {
                    int c = runComp[r];
                    for (int lx = runX0[r]; lx <= runX1[r]; lx++) {
                        label[row + lx] = c;
                    }
                }
            });

            // 地色: マスクから d より離れた画素(確信外側)のセル単位モード(SnapBoundary と同じ粗グリッド・同じ
            // セルの位置)。矩形の外にマスク画素は無いので、マスクからの距離は矩形の中だけで求めれば同じ。
            var distOut = DistanceToOpposite(scratch, local, bw, bh, inside: false, token);
            long* sumOut = scratch.Long(gw * gh * 4, clear: true);
            int* cntOut = scratch.Int(gw * gh, clear: true);
            int gy0r = oy / d, gy1r = (oy + bh - 1) / d;
            Parallel.For(gy0r, gy1r + 1, po, gy => {
                int yStart = Mathf.Max(oy, gy * d), yEnd = Mathf.Min(oy + bh, (gy + 1) * d);
                for (int y = yStart; y < yEnd; y++) {
                    int lrow = (y - oy) * bw;
                    for (int lx = 0; lx < bw; lx++) {
                        if (distOut[lrow + lx] <= d) {
                            continue;
                        }
                        int x = lx + ox;
                        int g = gy * gw + x / d, o = g * 4;
                        var c = px[y * w + x];
                        sumOut[o] += c.r; sumOut[o + 1] += c.g; sumOut[o + 2] += c.b; sumOut[o + 3] += c.a;
                        cntOut[g]++;
                    }
                }
            });
            double* meanOut = scratch.Double(gw * gh * 4, clear: true);
            bool* validOut = scratch.Bool(gw * gh, clear: true);
            ComputeCellModes(sumOut, cntOut, gw * gh, 4, meanOut, validOut);

            // 扱う範囲(near): 細い成分から reach 以内(太い成分の画素は除く)。
            Parallel.For(0, bh, po, ly => {
                int row = ly * bw;
                for (int lx = 0; lx < bw; lx++) {
                    local[row + lx] = thin[label[row + lx]];
                }
            });
            var distThin = DistanceToOpposite(scratch, local, bw, bh, inside: false, token);
            bool* near = scratch.Bool(bw * bh, clear: false);   // 下で全画素に書く
            Parallel.For(0, bh, po, ly => {
                int row = ly * bw;
                for (int lx = 0; lx < bw; lx++) {
                    int li = row + lx;
                    near[li] = distThin[li] <= reach && (label[li] == 0 || thin[label[li]]);
                }
            });

            // はっきり違う(描き込み): 細い成分の画素のうち、最大半径(MaxWindowRadius セル)の窓のすべての地色
            // (確信外側のセルモード)との距離² が PeelDistinctSq を超えるもの。窓を広く取るのは、地色が 2 種類ある所
            // — 余白の白と隣の部位の紺 — で、近くに白しか無い画素を「はっきり違う」と数えないため。
            bool* distinctPx = scratch.Bool(bw * bh, clear: true);
            Parallel.For(0, bh, po, ly => {
                for (int lx = 0; lx < bw; lx++) {
                    int li = ly * bw + lx;
                    if (label[li] == 0 || !thin[label[li]]) {
                        continue;
                    }
                    int x = lx + ox, y = ly + oy;
                    var c = px[y * w + x];
                    int gx = x / d, gy = y / d;
                    double best = double.MaxValue;
                    int wy0 = Mathf.Max(0, gy - MaxWindowRadius), wy1 = Mathf.Min(gh - 1, gy + MaxWindowRadius);
                    int wx0 = Mathf.Max(0, gx - MaxWindowRadius), wx1 = Mathf.Min(gw - 1, gx + MaxWindowRadius);
                    for (int yy = wy0; yy <= wy1 && best > PeelDistinctSq; yy++) {
                        for (int xx = wx0; xx <= wx1; xx++) {
                            int g = yy * gw + xx;
                            if (!validOut[g]) {
                                continue;
                            }
                            double dd = Dist2Mean(c, meanOut, g * 4);
                            if (dd < best) {
                                best = dd;
                            }
                        }
                    }
                    distinctPx[li] = best != double.MaxValue && best > PeelDistinctSq;
                }
            });
            token.ThrowIfCancellationRequested();

            // 地: 範囲の外周(細い成分から reach 離れた画素 = 描き込みから十分離れた地)から、範囲の中を
            // 隣同士の色の差が PeelStepSq 以下の画素へ辿る(4 近傍)。地色の画素を起点にしたり、地色どうしの
            // 差を緩めたりはしない: 陰影の濃淡がある所では、描き込み(裾の暗い帯の中の灰色の歯)が別の場所の
            // 地色(帯の明るい縁)と同じ色になり、末端の歯は本体が布の地色とほぼ同じ明るさになる(実測: 本体 50・
            // 周りの地 35)。滑らかさだけで辿れば、縁で地と切れている描き込みは地に入らない。
            bool* ground = scratch.Bool(bw * bh, clear: true);
            int* queue = scratch.Int(bw * bh, clear: false);   // 積んだ所だけ読む
            int head = 0, tail = 0;
            for (int li = 0; li < bw * bh; li++) {
                if (!near[li] || distThin[li] < reach) {
                    continue;
                }
                ground[li] = true;
                queue[tail++] = li;
            }
            while (head < tail) {
                int q = queue[head++];
                int qx = q % bw, qy = q / bw;
                var cq = px[(qy + oy) * w + qx + ox];
                for (int k = 0; k < 4; k++) {
                    int nx = qx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = qy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || nx >= bw || ny < 0 || ny >= bh) {
                        continue;
                    }
                    int ni = ny * bw + nx;
                    if (ground[ni] || !near[ni]) {
                        continue;
                    }
                    if (Dist2Px(px[(ny + oy) * w + nx + ox], cq) > PeelStepSq) {
                        continue;
                    }
                    ground[ni] = true;
                    queue[tail++] = ni;
                }
            }
            token.ThrowIfCancellationRequested();

            // 地には、外周から辿れない「近くの地と同じ色」の画素も足す: 各画素に、外周から辿れた地のうち最も近い
            // (4 近傍の歩数で)画素の色を割り当て、それとの差が PeelLocalSameSq 以下なら地とする。
            // 描き込みに囲まれた地の袋(柄の帯の中の生成り)は外周から滑らかには辿れないが、すぐ外の地と同じ色
            // なので地になる。末端の歯は本体が布の地色に近くても、すぐ周りの地(裾の暗い帯)より明るいので残る。
            int* nearestGround = scratch.Int(bw * bh, clear: false);   // 下で全画素に書く
            for (int li = 0; li < bw * bh; li++) {
                nearestGround[li] = ground[li] ? li : -1;
            }
            head = 0; tail = 0;
            for (int li = 0; li < bw * bh; li++) {
                if (ground[li]) {
                    queue[tail++] = li;
                }
            }
            while (head < tail) {
                int q = queue[head++];
                int qx = q % bw, qy = q / bw;
                for (int k = 0; k < 4; k++) {
                    int nx = qx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = qy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || nx >= bw || ny < 0 || ny >= bh) {
                        continue;
                    }
                    int ni = ny * bw + nx;
                    if (nearestGround[ni] >= 0 || !near[ni]) {
                        continue;
                    }
                    nearestGround[ni] = nearestGround[q];
                    queue[tail++] = ni;
                }
            }
            Parallel.For(0, bh, po, ly => {
                for (int lx = 0; lx < bw; lx++) {
                    int li = ly * bw + lx, g = nearestGround[li];
                    if (ground[li] || g < 0) {
                        continue;
                    }
                    ground[li] = Dist2Px(px[(ly + oy) * w + lx + ox], px[(g / bw + oy) * w + g % bw + ox])
                                 <= PeelLocalSameSq;
                }
            });

            // 成分ごとの門(PeelMinDetailFrac の説明参照): 地の上の描き込みが成分の一定割合を占め、
            // 地を外した後の残りの半分以上がそれであること。
            var peeled = new int[nComp];
            var distinct = new int[nComp];
            for (int li = 0; li < bw * bh; li++) {
                int c = label[li];
                if (c == 0 || !thin[c]) {
                    continue;
                }
                if (ground[li]) {
                    peeled[c]++;
                } else if (distinctPx[li]) {
                    distinct[c]++;
                }
            }
            var accept = new bool[nComp];
            bool anyAccept = false;
            int largest = 1;
            for (int c = 1; c < nComp; c++) {
                accept[c] = thin[c] && peeled[c] > 0 && distinct[c] >= PeelMinDetailFrac * area[c]
                            && distinct[c] >= 0.5f * (area[c] - peeled[c]);
                anyAccept |= accept[c];
                if (area[c] > area[largest]) {
                    largest = c;
                }
            }
            if (!anyAccept) {
                return;
            }
            // 提案のいちばん大きな成分が地の上の描き込みなら、提案全体が描き込みの提案。ほかの細い成分(SAM が
            // 離れた所に飛ばした小さな塊)は門を通さずに地を外す: 飛び地は落ち影だけの欠片や、提案の外にある
            // 同じ描き込み(もう 1 本のファスナー)の一部で、後者は地色のモードに数えられて「はっきり違う」画素が
            // 0 になる(実測: 右のファスナーの提案が左の上端に飛ばした 412px の塊)。部位の提案(いちばん大きな
            // 成分が太い)には効かない。
            if (accept[largest]) {
                for (int c = 1; c < nComp; c++) {
                    if (thin[c] && peeled[c] > 0) {
                        accept[c] = true;
                    }
                }
            }

            // 地を外し、残った描き込みから GrowGap px 以内の隙間で連なる「範囲の中の地でない画素」を足す。
            head = tail = 0;
            for (int li = 0; li < bw * bh; li++) {
                int c = label[li];
                if (c == 0 || !accept[c]) {
                    continue;
                }
                if (ground[li]) { mask[(li / bw + oy) * w + li % bw + ox] = false; continue; }
                queue[tail++] = li;
            }
            bool* added = local;   // 細い成分の印はもう使わないので、足した画素の印に使い回す
            new System.Span<bool>(added, bw * bh).Clear();
            while (head < tail) {
                int q = queue[head++];
                int qx = q % bw, qy = q / bw;
                for (int dy = -GrowGap; dy <= GrowGap; dy++) {
                    int ny = qy + dy;
                    if (ny < 0 || ny >= bh) {
                        continue;
                    }
                    for (int dx = -GrowGap; dx <= GrowGap; dx++) {
                        int nx = qx + dx;
                        if (nx < 0 || nx >= bw) {
                            continue;
                        }
                        int ni = ny * bw + nx;
                        if (label[ni] != 0 || added[ni] || !near[ni] || ground[ni]) {
                            continue;
                        }
                        if (!InStructure(ground, near, bw, bh, nx, ny)) {
                            continue;
                        }
                        added[ni] = true;
                        mask[(ny + oy) * w + nx + ox] = true;
                        queue[tail++] = ni;
                    }
                }
            }
        }

        /// <summary>
        /// (lx, ly) の 3x3 に、範囲の中の地でない画素が自分を含めて 3 つ以上あるか(描き込みの続きとして足す画素の条件)。
        /// 地の中の孤立した粒(生成りの布の粒状の色むら。隣と少し色が離れていて地として辿れない 1〜2 画素)を
        /// 描き込みとして連ねないため。歯のような塊や 1px の線は満たす(実測 2026-10-12: この条件が無いと
        /// 民族柄の細い提案が粒を伝って周りの生成りへ広がり、地の滲みが 10,780 → 24,105 に増えた)。
        /// </summary>
        static bool InStructure(bool* ground, bool* near, int bw, int bh, int lx, int ly) {
            int n = 0;
            for (int dy = -1; dy <= 1; dy++) {
                int y = ly + dy;
                if (y < 0 || y >= bh) {
                    continue;
                }
                for (int dx = -1; dx <= 1; dx++) {
                    int x = lx + dx;
                    if (x < 0 || x >= bw) {
                        continue;
                    }
                    int i = y * bw + x;
                    if (near[i] && !ground[i] && ++n >= 3) {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>2 画素の RGBA 距離 2 乗。</summary>
        static double Dist2Px(Color32 a, Color32 b) {
            double dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b, da = a.a - b.a;
            return dr * dr + dg * dg + db * db + da * da;
        }
    }
}
