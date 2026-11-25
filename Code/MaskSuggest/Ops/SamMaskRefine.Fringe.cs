// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // SamMaskRefine: 房の外郭までの拡張(ExtendFringe)。クラスの説明と共通の定数・下回りは SamMaskRefine.cs。
    internal static unsafe partial class SamMaskRefine {
        /// <summary>
        /// mask(下原点 w*h)の境界を、房 strands を覆うように実テクスチャの信号で外郭まで拡張する(in-place)。
        /// SnapBoundary の後段で呼ぶ。房が無い部位ではほとんど成長しない(precision 影響 &lt;=0.004)。
        /// </summary>
        /// <param name="sizeW">帯幅などの定数を決める画像の寸法(0 なら w・h)。切り出しで回すとき全画像の寸法を渡す
        /// (<see cref="TryDeriveRefineCropRect"/>)。</param>
        public static void ExtendFringe(bool* mask, Color32* pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default,
                                        int sizeW = 0, int sizeH = 0) {
            if (mask == null || pixelsBottomUp == null) {
                return;
            }
            if (sizeW <= 0 || sizeH <= 0) { sizeW = w; sizeH = h; }

            int maxDim = Mathf.Max(sizeW, sizeH);
            // グリッド幅 d は SnapBoundary と同一(低解像度セルの 3/4)。reach/strand 幅は解像度比例
            // (4096² で d=12 / reach=50 / strandHalf=10)。
            int d = BandHalfWidth(sizeW, sizeH);
            int reach = FringeReach(sizeW, sizeH, d);
            int strandHalf = Mathf.Max(2, Mathf.RoundToInt(maxDim / 410f));
            float thr2 = FringeColorThresh * FringeColorThresh;

            // 画像全体の大きさの作業配列(距離 2 本・印 3 本、4K で計 112MB)は作業配列のプールから借りる
            // (毎クリック new すると Mono のヒープが膨らみ、縮まない)。抜けるときにまとめて返す。
            using var scratch = new WorkScratch();
            var distOut = DistanceToOpposite(scratch, mask, w, h, inside: false, token);
            var distIn = DistanceToOpposite(scratch, mask, w, h, inside: true, token);
            var po = MakeParallelOptions(token);

            // 局所背景色 = mask 直外の確信領域(distOut>d)を粗グリッド集計 → 各画素 5x5 グリッド窓合算平均。
            // 内側確信領域(distIn>d)も同時に集計する(背景モードの生地類似判定に使う)。
            // 並列化はグリッド行単位: 1 タスク = 1 グリッド行(画素行 d 本)で、同一セルへの
            // 書き込みがタスク間で共有されない(long 加算の順序も行内逐次のまま=決定的)。
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            long* sum = scratch.Long(gw * gh * 3, clear: true);
            int* cnt = scratch.Int(gw * gh, clear: true);
            long* sumIn = scratch.Long(gw * gh * 3, clear: true);
            int* cntIn = scratch.Int(gw * gh, clear: true);
            Parallel.For(0, gh, po, gy => {
                int gRow = gy * gw;
                int yEnd = System.Math.Min(h, (gy + 1) * d);
                for (int y = gy * d; y < yEnd; y++) {
                    int row = y * w;
                    for (int x = 0; x < w; x++) {
                        int i = row + x;
                        bool confOut = !mask[i] && distOut[i] > d;
                        bool confIn = mask[i] && distIn[i] > d;
                        if (!confOut && !confIn) {
                            continue;
                        }
                        int g = gRow + x / d;
                        var c = pixelsBottomUp[i];
                        if (confOut) {
                            int o = g * 3;
                            sum[o] += c.r; sum[o + 1] += c.g; sum[o + 2] += c.b; cnt[g]++;
                        } else {
                            int o = g * 3;
                            sumIn[o] += c.r; sumIn[o + 1] += c.g; sumIn[o + 2] += c.b; cntIn[g]++;
                        }
                    }
                }
            });

            // セル単位の背景モード(MinCellSamples 以上のセルのみ)。距離判定は最近傍モードで
            // 行う: 窓プール平均だと隣接する別パーツの色が混入した瞬間に平均が実在しない
            // 中間色へずれ、素の背景画素が「背景から遠い=生地」と誤判定されて提案が
            // ギャップへ成長する(多峰背景の平均は無意味)。
            double* cellMean = scratch.Double(gw * gh * 3, clear: true);
            bool* cellValid = scratch.Bool(gw * gh, clear: true);
            double* inMean = scratch.Double(gw * gh * 3, clear: true);
            bool* inValid = scratch.Bool(gw * gh, clear: true);
            ComputeCellModes(sum, cnt, gw * gh, 3, cellMean, cellValid);
            ComputeCellModes(sumIn, cntIn, gw * gh, 3, inMean, inValid);

            // 生地類似の外側モードを無効化: マスク直外の確信領域には SAM が切り落とした
            // 生地(房 strands)自体が含まれ得る。その色クラスタを背景モードとして信用すると
            // 房画素が「背景に近い」と誤判定されて成長が止まる。近傍 5x5 セル内のどれかの
            // 内側モードと同色(しきい以内)の外側モードは生地の可能性が高いため除外する
            // (プール平均時代は希釈で偶然無害だった汚染の、モード化に伴う明示対処)。
            for (int gy = 0; gy < gh; gy++) {
                int gy0 = Mathf.Max(0, gy - FringeWindowRadius), gy1 = Mathf.Min(gh - 1, gy + FringeWindowRadius);
                for (int gx = 0; gx < gw; gx++) {
                    int g = gy * gw + gx;
                    if (!cellValid[g]) {
                        continue;
                    }
                    int o = g * 3;
                    int gx0 = Mathf.Max(0, gx - FringeWindowRadius), gx1 = Mathf.Min(gw - 1, gx + FringeWindowRadius);
                    bool fabricLike = false;
                    for (int yy = gy0; yy <= gy1 && !fabricLike; yy++) {
                        int gr = yy * gw;
                        for (int xx = gx0; xx <= gx1; xx++) {
                            int g2 = gr + xx;
                            if (!inValid[g2]) {
                                continue;
                            }
                            int o2 = g2 * 3;
                            double dr = cellMean[o] - inMean[o2],
                                   dg = cellMean[o + 1] - inMean[o2 + 1],
                                   db = cellMean[o + 2] - inMean[o2 + 2];
                            if (dr * dr + dg * dg + db * db <= thr2) { fabricLike = true; break; }
                        }
                    }
                    if (fabricLike) {
                        cellValid[g] = false;
                    }
                }
            }

            // far[i]: どの局所背景モードからも色が遠い(生地/strand)。near は strand-like gate 用に横合算する。
            // 全画素 × 5x5 セル窓の走査で ExtendFringe 最大のホットループ(4K 実測 ~3s)。
            // 読み取り専用入力から far/nearBg の自画素のみへ書くため行並列で決定的。
            //
            // 境界帯限定(出力ビット同一): この 2 配列は後段の strand-like gate でしか読まれず、
            // 読まれる位置は
            //   far[i]    … `!mask[i] && distOut[i] <= reach` の候補画素のみ
            //   nearBg[j] … その候補と同じ行で横 ±strandHalf 内の画素のみ
            // に限られる。distIn/distOut は厳密な L1 距離変換なので画素間 L1 距離に対し
            // 1-Lipschitz であり、候補 i(非 mask, distOut<=reach)と L1 距離 strandHalf 以内の j は
            //   ・非 mask なら distOut[j] <= distOut[i] + strandHalf <= reach + strandHalf
            //   ・mask なら i が非 mask なので distIn[j] <= strandHalf
            // を必ず満たす。よって下の帯の外は「読まれない」ことが厳密に言え、false のまま
            // 残しても出力は変わらない。房は境界近傍にしか出ないので実効面積は典型 1 割未満。
            //
            // 窓の合計(sr・sg・sb・n)と窓の中の有効なモードの並びはセル (gx, gy) だけで決まり、画素の色に依らない。
            // そこで行の中で同じセルが続く間は 1 回だけ集計して使い回し、画素ごとには「有効なモードとの距離の最小」
            // だけを求める。合計は同じ値の整数の和、モードの走査の順(yy→xx)と比較 `<` も以前と同じなので、
            // minD・n・平均は以前とビット同一になる。
            int fringeBand = reach + strandHalf;
            int fringeWindowCells = (2 * FringeWindowRadius + 1) * (2 * FringeWindowRadius + 1);
            bool* far = scratch.Bool(w * h, clear: true);
            bool* nearBg = scratch.Bool(w * h, clear: true);
            Parallel.For(0, h, po, y => {
                int gy = y / d, row = y * w;
                int gy0 = Mathf.Max(0, gy - FringeWindowRadius), gy1 = Mathf.Min(gh - 1, gy + FringeWindowRadius);
                int* modeList = stackalloc int[fringeWindowCells];
                int modeLen = 0;
                int cachedGx = -1;
                long sr = 0, sg = 0, sb = 0; int n = 0;
                for (int x = 0; x < w; x++) {
                    int i = row + x;
                    if (mask[i] ? distIn[i] > strandHalf : distOut[i] > fringeBand) {
                        continue;
                    }
                    int gx = x / d;
                    if (gx != cachedGx) {
                        cachedGx = gx;
                        int gx0 = Mathf.Max(0, gx - FringeWindowRadius), gx1 = Mathf.Min(gw - 1, gx + FringeWindowRadius);
                        sr = 0; sg = 0; sb = 0; n = 0;
                        modeLen = 0;
                        for (int yy = gy0; yy <= gy1; yy++) {
                            int gr = yy * gw;
                            for (int xx = gx0; xx <= gx1; xx++) {
                                int g = gr + xx, o = g * 3;
                                sr += sum[o]; sg += sum[o + 1]; sb += sum[o + 2]; n += cnt[g];
                                if (cellValid[g]) {
                                    modeList[modeLen++] = o;
                                }
                            }
                        }
                    }
                    var c = pixelsBottomUp[i];
                    double minD = double.MaxValue;
                    for (int k = 0; k < modeLen; k++) {
                        int o = modeList[k];
                        double dr = c.r - cellMean[o], dg = c.g - cellMean[o + 1],
                               db = c.b - cellMean[o + 2];
                        double dd = dr * dr + dg * dg + db * db;
                        if (dd < minD) {
                            minD = dd;
                        }
                    }
                    if (n < MinSamples) { nearBg[i] = true; continue; } // bg 不明 → 育てない側に倒す
                    double dist2;
                    if (minD != double.MaxValue) {
                        dist2 = minD;
                    } else {
                        // モードを成すセルが無い散在サンプル → 従来のプール平均で判定
                        double mr = sr / (double)n, mg = sg / (double)n, mb = sb / (double)n;
                        double dr = c.r - mr, dg = c.g - mg, db = c.b - mb;
                        dist2 = dr * dr + dg * dg + db * db;
                    }
                    if (dist2 > thr2) {
                        far[i] = true;
                    } else {
                        nearBg[i] = true;
                    }
                }
            });

            // strand-like gate: 横 ±strandHalf 内に nearBg がある far 画素のみ育てる
            // (縦 strand は左右に背景 → 通す。solid 縁/文字は横に背景無し → 弾く)。
            // 同じループで「mask に 4 近傍が接する growZone 画素」を行ごとの種リストへ収集する
            // (mask はこのループでは読み取り専用=判定は決定的。従来は種のために全画素をもう
            //  1 パス逐次走査しており、そのパスを丸ごと削減する)。
            bool* growZone = scratch.Bool(w * h, clear: true);
            var rowSeeds = new System.Collections.Generic.List<int>[h];
            var rowGrow = new int[h];   // 行ごとの growZone の画素数(下のキューの長さを決める)
            Parallel.For(0, h, po, y => {
                int row = y * w;
                int grow = 0;
                System.Collections.Generic.List<int> seeds = null;
                for (int x = 0; x < w; x++) {
                    int i = row + x;
                    if (mask[i] || !far[i] || distOut[i] > reach) {
                        continue;
                    }
                    int x0 = Mathf.Max(0, x - strandHalf), x1 = Mathf.Min(w - 1, x + strandHalf);
                    bool horizNear = false;
                    for (int xx = x0; xx <= x1; xx++) {
                        if (nearBg[row + xx]) { horizNear = true; break; }
                    }
                    if (!horizNear) {
                        continue;
                    }
                    growZone[i] = true;
                    grow++;
                    if ((x > 0 && mask[i - 1]) || (x < w - 1 && mask[i + 1]) ||
                        (y > 0 && mask[i - w]) || (y < h - 1 && mask[i + w])) {
                        (seeds ??= new System.Collections.Generic.List<int>()).Add(x);
                    }
                }
                rowSeeds[y] = seeds;
                rowGrow[y] = grow;
            });

            // grow_zone を mask 境界から 4 連結でフラッドして房を覆う(順序非依存の fixpoint)。
            // 種を「元 mask に隣接する growZone」に限っても、旧実装の種スキャン中に連鎖で
            // 種化していた画素は BFS で到達するため、最終集合はビット同一。
            // キュー要素は (y<<16)|x のパック座標(デキューごとの %w・/w を除去。Unity の
            // 最大テクスチャ 16384 でも 16bit に収まる)。
            // キューは作業配列から借りた並び(頭と尻の添字で先入れ先出し。取り出す順は以前の Queue<int> と同じ)。
            // 積むのは growZone の画素を 1 回ずつ(積むときに印を下ろす)なので、長さはその画素数で足りる。
            long growTotal = 0;
            for (int y = 0; y < h; y++) {
                growTotal += rowGrow[y];
            }
            if (growTotal == 0) {
                return;
            }
            int* queue = scratch.Int((int)growTotal, clear: false);   // 積んだ所だけ読む
            int qHead = 0, qTail = 0;
            for (int y = 0; y < h; y++) {
                var seeds = rowSeeds[y];
                if (seeds == null) {
                    continue;
                }
                int row = y * w;
                for (int k = 0; k < seeds.Count; k++) {
                    int x = seeds[k];
                    int i = row + x;
                    mask[i] = true; growZone[i] = false; queue[qTail++] = (y << 16) | x;
                }
            }
            int visited = 0;
            while (qHead < qTail) {
                int packed = queue[qHead++];
                int x = packed & 0xFFFF, y = packed >> 16;
                int i = y * w + x;
                if (x > 0 && growZone[i - 1]) { mask[i - 1] = true; growZone[i - 1] = false; queue[qTail++] = (y << 16) | (x - 1); }
                if (x < w - 1 && growZone[i + 1]) { mask[i + 1] = true; growZone[i + 1] = false; queue[qTail++] = (y << 16) | (x + 1); }
                if (y > 0 && growZone[i - w]) { mask[i - w] = true; growZone[i - w] = false; queue[qTail++] = ((y - 1) << 16) | x; }
                if (y < h - 1 && growZone[i + w]) { mask[i + w] = true; growZone[i + w] = false; queue[qTail++] = ((y + 1) << 16) | x; }
                if ((++visited & 0xFFFF) == 0) {
                    token.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
