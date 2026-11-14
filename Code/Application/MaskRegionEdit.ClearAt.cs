// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca {
    // MaskRegionEdit のうち、「ここのマスクを消す」(クリックした所のマスクを探す・塗った操作の記録やつながったかたまりを消す)と、
    // 範囲で消した後の切れ端の掃除。クラスの説明と、範囲の操作・宛先の選び方は MaskRegionEdit.cs。
    internal static unsafe partial class MaskRegionEdit {
        /// <summary>「ここのマスクを消す」で、クリックした所にマスクがあるかを見る範囲(マスクの画素。細いマスクを外さないよう周りも見る)。</summary>
        public const int ClearProbeRadius = 2;

        /// <summary>中心 (cx, cy) から半径 radius の正方形の中に、立っている画素があるか。bits が null なら false。</summary>
        public static bool AnyBitNear(ulong* bits, int w, int h, int cx, int cy, int radius) {
            if (bits == null) {
                return false;
            }
            int y0 = System.Math.Max(0, cy - radius), y1 = System.Math.Min(h - 1, cy + radius);
            int x0 = System.Math.Max(0, cx - radius), x1 = System.Math.Min(w - 1, cx + radius);
            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    int i = y * w + x;
                    if ((bits[i >> 6] & (1UL << (i & 63))) != 0UL) {
                        return true;
                    }
                }
            }
            return false;
        }

        // 「ここのマスクを消す」でクリックの狙いの外れを許す幅(画面の画素)。縮小表示では画面の 1 画素がマスクの何画素にも
        // なるので、マスクの画素で数えた ClearProbeRadius だけだと、細いマスクを狙っても当たらない。
        private const int ClearProbeScreenPixels = 3;
        // 見る範囲の上限(マスクの画素)。8K を 500 画素ほどに縮めて見ているときの 3 画素ぶん。
        private const int ClearProbeRadiusMax = 64;

        /// <summary>
        /// 「ここのマスクを消す」でマスクを探す範囲(マスクの画素)。画面の 1 画素がマスクの何画素か
        /// (<paramref name="maskPixelsPerScreenPixel"/>)から決める。<see cref="ClearProbeRadius"/> より小さくはしない。
        /// </summary>
        public static int ClearProbeRadiusFor(float maskPixelsPerScreenPixel) {
            if (!(maskPixelsPerScreenPixel > 0f)) {
                return ClearProbeRadius;
            }
            int r = (int)System.Math.Ceiling(ClearProbeScreenPixels * (double)maskPixelsPerScreenPixel);
            return System.Math.Min(ClearProbeRadiusMax, System.Math.Max(ClearProbeRadius, r));
        }

        private static bool Bit(ulong* bits, int i) => (bits[i >> 6] & (1UL << (i & 63))) != 0UL;

        /// <summary>
        /// 中心 (cx, cy) にいちばん近い立っている画素の番号(y * w + x)と、そこまでの距離(縦横の大きいほう。0 = 中心が
        /// 立っている)。半径 radius の正方形の中に無ければ(bits が null でも)どちらも -1。同じ距離に何個もあれば、
        /// 下の行・左の画素を先に選ぶ。
        /// </summary>
        public static int NearestBit(ulong* bits, int w, int h, int cx, int cy, int radius, out int distance) {
            distance = -1;
            if (bits == null || w <= 0 || h <= 0) {
                return -1;
            }
            for (int r = 0; r <= radius; r++) {
                int found = -1;
                VisitRing(w, h, cx, cy, r, i => {
                    if (!Bit(bits, i)) {
                        return false;
                    }
                    found = i;
                    return true;
                });
                if (found >= 0) {
                    distance = r;
                    return found;
                }
            }
            return -1;
        }

        // 中心から距離 r(縦横の大きいほう)ちょうどの画素を順に渡す。画像の外は飛ばす。visit が true を返したら止める。
        private static bool VisitRing(int w, int h, int cx, int cy, int r, System.Func<int, bool> visit) {
            int x0 = cx - r, x1 = cx + r, y0 = cy - r, y1 = cy + r;
            for (int y = System.Math.Max(0, y0); y <= System.Math.Min(h - 1, y1); y++) {
                if (y == y0 || y == y1) {
                    for (int x = System.Math.Max(0, x0); x <= System.Math.Min(w - 1, x1); x++) {
                        if (visit(y * w + x)) {
                            return true;
                        }
                    }
                    continue;
                }
                // 上下の辺の間の行は左右の端だけ(ここへ来るのは r >= 1 なので、左右の端は別の画素)。
                if (x0 >= 0 && visit(y * w + x0)) {
                    return true;
                }
                if (x1 < w && visit(y * w + x1)) {
                    return true;
                }
            }
            return false;
        }

        // 立っているビットの数(Unity の Mono には BitOperations が無い)。
        private static int PopCount(ulong v) {
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }

        /// <summary>
        /// 「ここのマスクを消す」の本体(1): その場所を塗った操作が塗った画素(patch。マスクと同じ寸法のビット)を、マスクから消す。
        /// <para>
        /// 以前は消す範囲を AI に見分け直させていた。塗ったときとは別のクリックなので同じ範囲にならず、同じ部分を押しても
        /// 半分以上の場合で縁や離れた所に細かい消し残りが出た(2026-11-12 の計測。84 組中 45 組)。右クリックの 1 回で塗られる
        /// 範囲は、本体のほかに離れた島や細かい点を含むので、つながりだけでも決められない(60 件中 44 件が複数のかたまり)。
        /// 何を塗ったかは塗ったときに覚えておけるので、推定を挟まずにそれを使う(MaskPatch。マスクと一緒に保存する)。
        /// </para>
        /// n はマスクの画素数。消した画素数と、そのうち全ゾーン共通の除外(common。null 可)が重なる画素数を
        /// <paramref name="r"/> に足す。patch のうち、いまはもう立っていない画素(後の操作で消した所)は何もしない。
        /// </summary>
        public static void ErasePatch(ulong* bits, ulong* patch, int n, ulong* common, ref MaskRegionEditResult r) {
            if (bits == null || patch == null) {
                return;
            }
            int words = (n + 63) >> 6;
            for (int k = 0; k < words; k++) {
                ulong hit = bits[k] & patch[k];
                if (hit == 0UL) {
                    continue;
                }
                bits[k] &= ~hit;
                r.changed += PopCount(hit);
                if (common != null) {
                    r.commonOverlap += PopCount(hit & common[k]);
                }
            }
        }

        /// <summary>
        /// 「ここのマスクを消す」の本体(2): クリックした所のマスクの、つながっているひとかたまり(縦横斜めの 8 方向)を消す。
        /// 何が塗ったか分からないマスク(ブラシで塗った・記録を持たない版で入れた)に使う。
        /// 中心 (cx, cy) から距離 distance(<see cref="NearestBit"/> が返す距離)にある立っている画素を起点にする。
        /// 消した画素数と、そのうち全ゾーン共通の除外(common。null 可)が重なる画素数を <paramref name="r"/> に足す。
        /// work は起点の置き場(中身は捨てる。呼び出しをまたいで使い回せる)。
        /// </summary>
        public static void ErasePieceAt(ulong* bits, int w, int h, int cx, int cy, int distance, ulong* common,
                                        System.Collections.Generic.List<int> work, ref MaskRegionEditResult r) {
            if (bits == null || w <= 0 || h <= 0 || distance < 0) {
                return;
            }
            work.Clear();
            VisitRing(w, h, cx, cy, distance, i => {
                if (Bit(bits, i)) {
                    work.Add(i);
                }
                return false;
            });
            // 1 行の連なりごとに消す(消したことが「見た」印になる)。上下の行は、連なりの両端の 1 画素外まで見る(斜めのつながり)。
            while (work.Count > 0) {
                int i = work[work.Count - 1];
                work.RemoveAt(work.Count - 1);
                if (!Bit(bits, i)) {
                    continue;
                }
                int y = i / w, row = y * w, x = i - row;
                int xl = x, xr = x;
                while (xl > 0 && Bit(bits, row + xl - 1)) {
                    xl--;
                }
                while (xr < w - 1 && Bit(bits, row + xr + 1)) {
                    xr++;
                }
                SetRange(bits, row + xl, row + xr + 1, false);
                r.changed += xr - xl + 1;
                if (common != null) {
                    for (int k = row + xl; k <= row + xr; k++) {
                        if (Bit(common, k)) {
                            r.commonOverlap++;
                        }
                    }
                }
                int a = System.Math.Max(0, xl - 1), b = System.Math.Min(w - 1, xr + 1);
                for (int ny = y - 1; ny <= y + 1; ny += 2) {
                    if (ny < 0 || ny >= h) {
                        continue;
                    }
                    bool inRun = false;
                    for (int j = ny * w + a, end = ny * w + b; j <= end; j++) {
                        bool on = Bit(bits, j);
                        if (on && !inRun) {
                            work.Add(j);
                        }
                        inRun = on;
                    }
                }
            }
        }

        // AI(MobileSAM)が部分を見分けるマスクの格子の数(長辺)。元画像の長辺 ÷ これが、AI の境目の 1 目盛り。
        private const int AiMaskCells = 256;

        /// <summary>
        /// 範囲(AI の判定・メッシュの島)で消したあとに残る「切れ端」とみなす幅(マスクの画素)。範囲の境目は、AI が同じ部分を
        /// 見分け直しても 1 目盛り(元画像の長辺 ÷ <see cref="AiMaskCells"/>)ほどはぶれるので、その幅に収まる残りを切れ端とする。
        /// </summary>
        public static int CrumbReach(int w, int h)
            => System.Math.Max(1, (System.Math.Max(w, h) + AiMaskCells - 1) / AiMaskCells);

        // 切れ端の掃除(RegionBounds・MarkNear)を並列にするときの帯。範囲で消すのはメインスレッドで、大きな範囲では窓が画像の
        // 全面近くになる(8K で 6,700 万画素を、外接矩形・横 2 回・縦 2 回なぞる)。帯ごとに読み書きする所が別で、まとめるのは
        // 最小・最大だけなので、結果は 1 本と同じ。小さい窓(ParallelMinPixels 未満)は 1 本のまま。
        // 縦の帯の幅(列)。64 の倍数にして、隣の帯と同じキャッシュの行を書き合いにくくする。
        private const int ColumnBandWidth = 256;

        // 帯 [0, bands) を順に、または並列に body へ渡す(pixels は全体の画素数。ParallelMinPixels 未満なら 1 本)。
        private static void ForBands(long pixels, int bands, System.Action<int> body) {
            if (bands <= 1 || pixels < ParallelMinPixels) {
                for (int b = 0; b < bands; b++) {
                    body(b);
                }
                return;
            }
            System.Threading.Tasks.Parallel.For(0, bands, CoreParallelism.Options(), body);
        }

        // 1 帯あたりの行数(1 帯がだいたい RangePixels 画素になるように)。
        private static int RowsPerBand(int width) => System.Math.Max(1, RangePixels / System.Math.Max(1, width));

        // 1 行(p から w バイト)の、立っている画素の最初と最後の x。無ければ false。
        private static bool RowExtent(byte* p, int w, out int first, out int last) {
            first = -1;
            last = -1;
            int x = 0;
            for (; x + 8 <= w; x += 8) {
                if (*(ulong*)(p + x) == 0UL) {
                    continue;
                }
                for (int k = x; k < x + 8; k++) {
                    if (p[k] != 0) {
                        if (first < 0) {
                            first = k;
                        }
                        last = k;
                    }
                }
            }
            for (; x < w; x++) {
                if (p[x] != 0) {
                    if (first < 0) {
                        first = x;
                    }
                    last = x;
                }
            }
            return first >= 0;
        }

        /// <summary>範囲の外接矩形 [x0, x1) × [y0, y1)。範囲が空なら false。大きな範囲は行の帯に分けて並列に見る。</summary>
        public static bool RegionBounds(bool* region, int w, int h, out int x0, out int y0, out int x1, out int y1) {
            int rows = RowsPerBand(w);
            int bands = h <= 0 ? 0 : (h + rows - 1) / rows;
            // 帯ごとの外接矩形(左・右・下・上。空なら右が -1)。まとめるのは最小・最大なので、並べる順に依らない。
            var ext = new int[bands * 4];
            ForBands((long)w * h, bands, b => {
                int bx0 = int.MaxValue, bx1 = -1, by0 = int.MaxValue, by1 = -1;
                for (int y = b * rows, yEnd = System.Math.Min(h, y + rows); y < yEnd; y++) {
                    if (!RowExtent((byte*)region + (long)y * w, w, out int first, out int last)) {
                        continue;
                    }
                    bx0 = System.Math.Min(bx0, first);
                    bx1 = System.Math.Max(bx1, last);
                    by0 = System.Math.Min(by0, y);
                    by1 = y;
                }
                ext[b * 4] = bx0;
                ext[b * 4 + 1] = bx1;
                ext[b * 4 + 2] = by0;
                ext[b * 4 + 3] = by1;
            });
            int ax0 = int.MaxValue, ax1 = -1, ay0 = int.MaxValue, ay1 = -1;
            for (int b = 0; b < bands; b++) {
                if (ext[b * 4 + 1] < 0) {
                    continue;
                }
                ax0 = System.Math.Min(ax0, ext[b * 4]);
                ax1 = System.Math.Max(ax1, ext[b * 4 + 1]);
                ay0 = System.Math.Min(ay0, ext[b * 4 + 2]);
                ay1 = System.Math.Max(ay1, ext[b * 4 + 3]);
            }
            x0 = ax0; y0 = ay0; x1 = ax1 + 1; y1 = ay1 + 1;
            return ax1 >= 0;
        }

        // near(EraseCrumbs の作業用。1 画素 1 バイト)の値。
        private const byte NearNo = 0;       // 範囲から遠い
        private const byte NearRow = 1;      // 同じ行の範囲の画素から reach 以内(範囲そのものを含む)
        private const byte NearColumn = 2;   // NearRow の画素から縦に reach 以内
        private const byte NearSeen = 3;     // 調べ終えた(これ以降の値は層ごとに 1 つずつ使う)

        /// <summary>
        /// 切れ端を探す窓 [wx0, wx1) × [wy0, wy1)。範囲の外接矩形 [x0, x1) × [y0, y1) を reach + 1 だけ広げて、画像で切ったもの。
        /// 印の置き場(near)はこの窓の大きさだけあればよい(以前は画像全体の大きさを確保していた。8K で 64MiB)。
        /// </summary>
        public static void CrumbWindow(int w, int h, int x0, int y0, int x1, int y1, int reach,
                                       out int wx0, out int wy0, out int wx1, out int wy1) {
            wx0 = System.Math.Max(0, x0 - reach - 1);
            wy0 = System.Math.Max(0, y0 - reach - 1);
            wx1 = System.Math.Min(w, x1 + reach + 1);
            wy1 = System.Math.Min(h, y1 + reach + 1);
        }

        /// <summary>
        /// 範囲から縦横とも reach 画素以内の画素に印を付ける(<see cref="EraseCrumbs"/> の下ごしらえ)。印を付けるのは
        /// 窓(<see cref="CrumbWindow"/>)の中だけ。near は窓の大きさ((wx1 - wx0) × (wy1 - wy0) バイト。行ごとに窓の幅で並ぶ)で、
        /// 中身は問わない(全部を書く)。w は範囲(region)の 1 行の画素数。
        /// </summary>
        public static void MarkNear(bool* region, int w, int reach, byte* near, int wx0, int wy0, int wx1, int wy1) {
            int ww = wx1 - wx0, wh = wy1 - wy0;
            if (ww <= 0 || wh <= 0) {
                return;
            }
            long pixels = (long)ww * wh;
            // 横: 行ごとに、左からと右からの 2 回なぞる(範囲の画素を見たら残りを reach + 1 に戻し、残りがある間は印)。
            // 行どうしは別の所を読み書きするので、行の帯に分けて並列にする。
            int rows = RowsPerBand(ww);
            ForBands(pixels, (wh + rows - 1) / rows, b => {
                for (int y = wy0 + b * rows, yEnd = System.Math.Min(wy1, y + rows); y < yEnd; y++) {
                    byte* n = near + (long)(y - wy0) * ww;
                    bool* g = region + (long)y * w + wx0;
                    int left = 0;
                    for (int x = 0; x < ww; x++) {
                        left = g[x] ? reach + 1 : left - 1;
                        n[x] = left > 0 ? NearRow : NearNo;
                    }
                    left = 0;
                    for (int x = ww - 1; x >= 0; x--) {
                        left = g[x] ? reach + 1 : left - 1;
                        if (left > 0) {
                            n[x] = NearRow;
                        }
                    }
                }
            });
            // 縦: 列ごとに同じことを、横の印(NearRow)を起点にして行う。足した印は NearColumn にして、起点と区別する。
            // 列は自分の列の NearRow だけを読み、自分の列の NearNo だけを書くので、列どうしは独立。列の帯に分けて並列にし、
            // 帯の中は行の順に進める(列ごとの残りを並べて持つ。列ごとに縦へ飛び飛びに読むよりキャッシュに乗る)。
            // 1 列の中で下から → 上からの順は変わらないので、結果は列ごとになぞったときと同じ。
            ForBands(pixels, (ww + ColumnBandWidth - 1) / ColumnBandWidth, b => {
                int c0 = b * ColumnBandWidth, c1 = System.Math.Min(ww, c0 + ColumnBandWidth), cw = c1 - c0;
                int* left = stackalloc int[cw];
                for (int k = 0; k < cw; k++) {
                    left[k] = 0;
                }
                for (int y = 0; y < wh; y++) {
                    byte* n = near + (long)y * ww + c0;
                    for (int k = 0; k < cw; k++) {
                        left[k] = n[k] == NearRow ? reach + 1 : left[k] - 1;
                        if (left[k] > 0 && n[k] == NearNo) {
                            n[k] = NearColumn;
                        }
                    }
                }
                for (int k = 0; k < cw; k++) {
                    left[k] = 0;
                }
                for (int y = wh - 1; y >= 0; y--) {
                    byte* n = near + (long)y * ww + c0;
                    for (int k = 0; k < cw; k++) {
                        left[k] = n[k] == NearRow ? reach + 1 : left[k] - 1;
                        if (left[k] > 0 && n[k] == NearNo) {
                            n[k] = NearColumn;
                        }
                    }
                }
            });
        }

        /// <summary>
        /// 範囲(AI の判定・メッシュの島)でマスクを消すときに、縁に残る細い切れ端も消す。範囲の中を消す前に呼ぶ
        /// (切れ端かどうかは、消す前のつながりで決める)。
        /// <para>
        /// 範囲の境目は、マスクを塗ったときの境目と数画素ずれる(AI は同じ部分でもクリックごとに少し違う範囲を返す)。
        /// 範囲の中だけを消すと、ずれた分が縁に細く残り、そこだけ塗る・塗らないが効き続けて点や線に見える。
        /// </para>
        /// 切れ端とするのは、範囲の外に残るマスクのかたまり(8 方向)のうち、(1) これから消す画素に接していて、
        /// (2) 全体が範囲の近く(<see cref="MarkNear"/> の印の中)に収まるもの。隣の部分のマスクは範囲から遠くまで
        /// 続くので残る。消した画素数を返す。near は <see cref="MarkNear"/> が書いたもの(窓の大きさ)、layer は何番目の層か
        /// (0 から。同じ near を層ごとに使い回すため)、work は作業用。
        /// </summary>
        public static int EraseCrumbs(bool* region, ulong* bits, int w, int h, byte* near,
                                      int wx0, int wy0, int wx1, int wy1, int layer,
                                      System.Collections.Generic.List<int> work) {
            if (bits == null) {
                return 0;
            }
            byte seen = (byte)(NearSeen + layer);
            int ww = wx1 - wx0;
            int erased = 0;
            for (int y = wy0; y < wy1; y++) {
                for (int x = wx0; x < wx1; x++) {
                    int start = y * w + x;
                    // 層の語(64 画素)が 0 の所は始点にならない(マスクが立っていない)ので、語の終わりまで飛ばす。たどる間に
                    // ビットが増えることは無い(切れ端を消すだけ)ので、飛ばしても 1 画素ずつ見たときと同じ。
                    if (bits[start >> 6] == 0UL) {
                        x += 63 - (start & 63);
                        continue;
                    }
                    byte* nearStart = near + (long)(y - wy0) * ww + (x - wx0);
                    if (*nearStart == NearNo || *nearStart == seen || region[start] || !Bit(bits, start)) {
                        continue;
                    }
                    // 範囲の近くにある残りを、近くの中だけでたどる。遠くへ続いていたら(escaped)切れ端ではない。
                    work.Clear();
                    work.Add(start);
                    *nearStart = seen;
                    bool escaped = false, touchesCleared = false;
                    for (int head = 0; head < work.Count; head++) {
                        int i = work[head];
                        int py = i / w, px = i - py * w;
                        for (int ny = System.Math.Max(0, py - 1); ny <= System.Math.Min(h - 1, py + 1); ny++) {
                            for (int nx = System.Math.Max(0, px - 1); nx <= System.Math.Min(w - 1, px + 1); nx++) {
                                int j = ny * w + nx;
                                if (j == i || !Bit(bits, j)) {
                                    continue;
                                }
                                if (region[j]) {
                                    touchesCleared = true;   // 範囲の中のマスク = これから消す画素
                                    continue;
                                }
                                // 印(near)があるのは窓の中だけ。窓の外は「遠い」。
                                if (nx < wx0 || nx >= wx1 || ny < wy0 || ny >= wy1) {
                                    escaped = true;
                                    continue;
                                }
                                byte* nearJ = near + (long)(ny - wy0) * ww + (nx - wx0);
                                if (*nearJ == NearNo) {
                                    escaped = true;
                                } else if (*nearJ != seen) {
                                    *nearJ = seen;
                                    work.Add(j);
                                }
                            }
                        }
                    }
                    if (escaped || !touchesCleared) {
                        continue;
                    }
                    foreach (int i in work) {
                        bits[i >> 6] &= ~(1UL << (i & 63));
                    }
                    erased += work.Count;
                }
            }
            return erased;
        }
    }
}
