// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca {
    /// <summary>プレビューの右クリックで、選んだ範囲(AI の判定・メッシュの島)に当てる操作。</summary>
    internal enum MaskRegionOp {
        /// <summary>ここも塗る: 範囲を「含める」へ足し、同じ範囲の「塗らない」(ゾーン別の除外)を消す。</summary>
        PaintHere,
        /// <summary>ここは塗らない: 範囲を除外へ足し、同じ範囲の「含める」を消す。</summary>
        DontPaintHere,
        /// <summary>
        /// この部分だけ塗る: 範囲の外をすべて除外し、範囲の中の除外と範囲の外の「含める」を消す。
        /// 範囲の中は今までどおり色で選ぶ(含めるには足さない)。
        /// </summary>
        OnlyThisPart,
        /// <summary>
        /// 範囲(AI の判定・メッシュの島)でマスクを消す: 範囲の中の「含める」と除外の両方を消す(手入れ前の色の判定に戻す)。
        /// ゾーン宛のとき、全ゾーン共通の除外は消さない(重なりは数える)。
        /// 右クリックの「ここのマスクを消す」そのものは範囲を使わず、マスクのつながったかたまりを消す
        /// (<see cref="MaskRegionEdit.ErasePieceAt"/>)。これは、かたまりの一部だけを消したいときの道。
        /// </summary>
        ClearHere,
    }

    /// <summary><see cref="MaskRegionEdit.Apply"/> の結果。</summary>
    internal struct MaskRegionEditResult {
        /// <summary>書き換えた画素数(含める・除外の両方を合わせた延べ)。0 = 何も変わっていない。</summary>
        public int changed;
        /// <summary>範囲の中で、全ゾーン共通の除外が重なっている画素数(そこは塗られないので知らせる)。</summary>
        public int commonOverlap;
    }

    /// <summary>
    /// 右クリックの「ここも塗る / ここは塗らない / この部分だけ塗る」とブラシを、マスクへ当てる純粋な部品
    /// (Unity に依存しない。scripts/unit-run で検査する)。マスクは 1 画素 1 ビットの語の並び
    /// (<see cref="NativeBitMask.Bits"/>。画素 i は語 i &gt;&gt; 6 の i &amp; 63 ビット目)で受け取り、書く側は呼び出し側が
    /// 自分だけのものにしておく(<see cref="NativeBitMask.Writable"/>)。
    /// <para>
    /// 規則は「後から言ったほうが勝つ」。製品の合成は「除外が含めるに勝つ」(PixelProcessor の includedPx。
    /// 重なると含めるが無効)なので、含めるを足すだけでは、前に外した場所で「塗ったのに効かない」ことになる。
    /// 操作のたびに反対側のマスクの同じ範囲を消して、最後の指示どおりに見えるようにする。合成の規則自体は変えない。
    /// </para>
    /// <para>
    /// 全ゾーン共通の除外はここでは消さない(ほかのゾーンにも効いているため)。重なった画素数だけ返し、
    /// 呼び出し側が知らせる。
    /// </para>
    /// </summary>
    internal static unsafe class MaskRegionEdit {
        /// <param name="op">当てる操作。</param>
        /// <param name="region">範囲(マスクと同じ寸法)。</param>
        /// <param name="exclude">宛先の除外マスク。ゾーン宛ならゾーン別、共通宛なら共通。PaintHere と ClearHere では null 可(消す物が無い)。</param>
        /// <param name="include">宛先ゾーンの含めるマスク。PaintHere では必須、ほかは null 可(消す物が無い)。共通宛では null。</param>
        /// <param name="common">全ゾーン共通の除外(重なりを数えるだけ。null 可)。共通宛のときは null を渡す。</param>
        public static MaskRegionEditResult Apply(MaskRegionOp op, bool[] region,
                                                 ulong* exclude, ulong* include, ulong* common) {
            if (region == null) {
                return new MaskRegionEditResult();
            }
            fixed (bool* p = region) {
                return Apply(op, p, region.Length, exclude, include, common);
            }
        }

        /// <summary>
        /// <see cref="Apply(MaskRegionOp, bool[], ulong*, ulong*, ulong*)"/> の、範囲がポインタ(n 個)の版
        /// (AI 提案の範囲は Mono のヒープの外にある)。
        /// </summary>
        public static MaskRegionEditResult Apply(MaskRegionOp op, bool* region, int n,
                                                 ulong* exclude, ulong* include, ulong* common) {
            if (region == null) {
                return new MaskRegionEditResult();
            }
            if (op == MaskRegionOp.ClearHere) {
                return ForWordRanges(n, (i0, i1) => ClearRegion(region, i0, i1, exclude, include, common, write: true));
            }
            return ForWordRanges(n, (i0, i1) => ApplyRange(op, region, i0, i1, exclude, include, common));
        }

        // 画素 [i0, i1) に op を当てる(ClearHere 以外)。
        private static MaskRegionEditResult ApplyRange(MaskRegionOp op, bool* region, int i0, int i1,
                                                       ulong* exclude, ulong* include, ulong* common) {
            var r = new MaskRegionEditResult();
            switch (op) {
                case MaskRegionOp.PaintHere:
                    for (int i = i0; i < i1; i++) {
                        if (!region[i]) {
                            continue;
                        }
                        int w = i >> 6;
                        ulong bit = 1UL << (i & 63);
                        if ((include[w] & bit) == 0UL) { include[w] |= bit; r.changed++; }
                        if (exclude != null && (exclude[w] & bit) != 0UL) { exclude[w] &= ~bit; r.changed++; }
                        if (common != null && (common[w] & bit) != 0UL) {
                            r.commonOverlap++;
                        }
                    }
                    break;

                case MaskRegionOp.DontPaintHere:
                    for (int i = i0; i < i1; i++) {
                        if (!region[i]) {
                            continue;
                        }
                        int w = i >> 6;
                        ulong bit = 1UL << (i & 63);
                        if ((exclude[w] & bit) == 0UL) { exclude[w] |= bit; r.changed++; }
                        if (include != null && (include[w] & bit) != 0UL) { include[w] &= ~bit; r.changed++; }
                    }
                    break;

                case MaskRegionOp.OnlyThisPart:
                    for (int i = i0; i < i1; i++) {
                        int w = i >> 6;
                        ulong bit = 1UL << (i & 63);
                        if (region[i]) {
                            if ((exclude[w] & bit) != 0UL) { exclude[w] &= ~bit; r.changed++; }
                            if (common != null && (common[w] & bit) != 0UL) {
                                r.commonOverlap++;
                            }
                        } else {
                            if ((exclude[w] & bit) == 0UL) { exclude[w] |= bit; r.changed++; }
                            if (include != null && (include[w] & bit) != 0UL) { include[w] &= ~bit; r.changed++; }
                        }
                    }
                    break;
            }
            return r;
        }

        // 範囲の画素数がこれ未満なら 1 本で回す(スレッドを起こす方が高い)。
        private const int ParallelMinPixels = 1 << 18;
        // 並列にするときの 1 区間の画素数。64 の倍数(区間の境目が語の境目にそろい、2 本のスレッドが同じ語を書かない)。
        private const int RangePixels = 1 << 16;

        /// <summary>
        /// 画素 [0, n) を語の境目でそろえた区間に分けて body(i0, i1) を並列に呼び、結果の画素数を足す。
        /// 右クリックの範囲の適用はメインスレッドで全画素(4K 1,677 万・8K 6,700 万)を回すので、1 本では
        /// その分だけ操作が止まる。区間ごとに書く語が別で、数えるのは整数の和なので、結果は 1 本と同じ。
        /// </summary>
        private static MaskRegionEditResult ForWordRanges(int n, System.Func<int, int, MaskRegionEditResult> body) {
            if (n <= 0) {
                return new MaskRegionEditResult();
            }
            if (n < ParallelMinPixels) {
                return body(0, n);
            }
            int chunks = (n + RangePixels - 1) / RangePixels;
            int changed = 0, overlap = 0;
            var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = CoreParallelism.Max() };
            System.Threading.Tasks.Parallel.For(0, chunks, po,
                () => new MaskRegionEditResult(),
                (c, _, acc) => {
                    int i0 = c * RangePixels;
                    var r = body(i0, System.Math.Min(n, i0 + RangePixels));
                    acc.changed += r.changed;
                    acc.commonOverlap += r.commonOverlap;
                    return acc;
                },
                acc => {
                    System.Threading.Interlocked.Add(ref changed, acc.changed);
                    System.Threading.Interlocked.Add(ref overlap, acc.commonOverlap);
                });
            return new MaskRegionEditResult { changed = changed, commonOverlap = overlap };
        }

        /// <summary>
        /// <see cref="MaskRegionOp.ClearHere"/> を当てたときの結果を、マスクを書き換えずに求める。消す物が無いときに、
        /// 層を写さず Undo に空の手も積まないよう、当てる前に数える。
        /// </summary>
        public static MaskRegionEditResult CountClear(bool* region, int n, ulong* exclude, ulong* include, ulong* common)
            => region == null
                ? new MaskRegionEditResult()
                : ForWordRanges(n, (i0, i1) => ClearRegion(region, i0, i1, exclude, include, common, write: false));

        private static MaskRegionEditResult ClearRegion(bool* region, int i0, int i1, ulong* exclude, ulong* include, ulong* common, bool write) {
            var r = new MaskRegionEditResult();
            for (int i = i0; i < i1; i++) {
                if (!region[i]) {
                    continue;
                }
                int w = i >> 6;
                ulong bit = 1UL << (i & 63);
                if (exclude != null && (exclude[w] & bit) != 0UL) {
                    if (write) {
                        exclude[w] &= ~bit;
                    }
                    r.changed++;
                }
                if (include != null && (include[w] & bit) != 0UL) {
                    if (write) {
                        include[w] &= ~bit;
                    }
                    r.changed++;
                }
                if (common != null && (common[w] & bit) != 0UL) {
                    r.commonOverlap++;
                }
            }
            return r;
        }

        /// <summary>
        /// 右クリックした場所をいま塗っているゾーン(並び順の番号)。どのゾーンも塗っていなければ -1。
        /// <para>
        /// 右クリックの宛先は「いま直しているゾーン」(最後に触ったゾーン)だけで決めていたので、ゾーンが 2 つ以上あると、
        /// 別のゾーンが塗っている所に「ここは塗らない」を当てても見た目が変わらなかった(宛先のゾーンはそこを
        /// 塗っていない。通知は成功と出る)。塗られている所では、塗っているゾーンを宛先にする(PreviewView.ContextMenu)。
        /// </para>
        /// <para>
        /// strengths[k] は並び順 k のゾーンの、クリックした所の画素ごとの選択の強さ(0..1、長さ sampleCount)。
        /// 無効のゾーンや値の取れないゾーンは null。合成と同じく上のゾーンから順に画素の残りを取り合い、
        /// 取り分の合計がいちばん大きいゾーンを返す。それが画素数の半分に届かなければ、塗られていない所とみなす。
        /// </para>
        /// </summary>
        public static int PaintingZoneAt(float[][] strengths, int sampleCount) {
            if (strengths == null || sampleCount <= 0) {
                return -1;
            }
            var share = new float[strengths.Length];
            for (int i = 0; i < sampleCount; i++) {
                float room = 1f;
                for (int k = 0; k < strengths.Length && room > 0f; k++) {
                    var s = strengths[k];
                    if (s == null) {
                        continue;
                    }
                    float take = System.Math.Min(System.Math.Max(s[i], 0f), room);
                    share[k] += take;
                    room -= take;
                }
            }
            int best = -1;
            for (int k = 0; k < share.Length; k++) {
                if (share[k] >= sampleCount * 0.5f && (best < 0 || share[k] > share[best])) {
                    best = k;
                }
            }
            return best;
        }

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
        /// 中心 (cx, cy) にいちばん近い立っている画素までの距離(縦横の大きいほう。0 = 中心が立っている)。
        /// 半径 radius の正方形の中に無ければ -1。bits が null でも -1。
        /// </summary>
        public static int NearestBitDistance(ulong* bits, int w, int h, int cx, int cy, int radius) {
            NearestBit(bits, w, h, cx, cy, radius, out int distance);
            return distance;
        }

        /// <summary>
        /// 中心 (cx, cy) にいちばん近い立っている画素の番号(y * w + x)と、そこまでの距離(<see cref="NearestBitDistance"/>)。
        /// 半径の中に無ければどちらも -1。同じ距離に何個もあれば、下の行・左の画素を先に選ぶ。
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
        /// 何を塗ったかは Undo の履歴が持っているので、推定を挟まずにそれを使う(MaskHistory.PaintedBy)。
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
        /// 何が塗ったか分からないマスク(ブラシで塗った・履歴が残っていない)に使う。
        /// 中心 (cx, cy) から距離 distance(<see cref="NearestBitDistance"/> の戻り値)にある立っている画素を起点にする。
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

        /// <summary>範囲の外接矩形 [x0, x1) × [y0, y1)。範囲が空なら false。</summary>
        public static bool RegionBounds(bool* region, int w, int h, out int x0, out int y0, out int x1, out int y1) {
            int bx0 = int.MaxValue, bx1 = -1, by0 = int.MaxValue, by1 = -1;
            for (int y = 0; y < h; y++) {
                byte* p = (byte*)region + (long)y * w;
                int first = -1, last = -1, x = 0;
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
                if (first < 0) {
                    continue;
                }
                bx0 = System.Math.Min(bx0, first);
                bx1 = System.Math.Max(bx1, last);
                by0 = System.Math.Min(by0, y);
                by1 = y;
            }
            x0 = bx0; y0 = by0; x1 = bx1 + 1; y1 = by1 + 1;
            return bx1 >= 0;
        }

        // near(EraseCrumbs の作業用。1 画素 1 バイト)の値。
        private const byte NearNo = 0;       // 範囲から遠い
        private const byte NearRow = 1;      // 同じ行の範囲の画素から reach 以内(範囲そのものを含む)
        private const byte NearColumn = 2;   // NearRow の画素から縦に reach 以内
        private const byte NearSeen = 3;     // 調べ終えた(これ以降の値は層ごとに 1 つずつ使う)

        /// <summary>
        /// 範囲から縦横とも reach 画素以内の画素に印を付ける(<see cref="EraseCrumbs"/> の下ごしらえ)。印を付けるのは、範囲の
        /// 外接矩形 [x0, x1) × [y0, y1) を reach + 1 だけ広げた窓の中だけで、窓を [wx0, wx1) × [wy0, wy1) に返す。
        /// near は w * h バイト(中身は問わない。窓の中だけを書く)。
        /// </summary>
        public static void MarkNear(bool* region, int w, int h, int x0, int y0, int x1, int y1, int reach, byte* near,
                                    out int wx0, out int wy0, out int wx1, out int wy1) {
            wx0 = System.Math.Max(0, x0 - reach - 1);
            wy0 = System.Math.Max(0, y0 - reach - 1);
            wx1 = System.Math.Min(w, x1 + reach + 1);
            wy1 = System.Math.Min(h, y1 + reach + 1);
            // 横: 行ごとに、左からと右からの 2 回なぞる(範囲の画素を見たら残りを reach + 1 に戻し、残りがある間は印)。
            for (int y = wy0; y < wy1; y++) {
                byte* n = near + (long)y * w;
                bool* g = region + (long)y * w;
                int left = 0;
                for (int x = wx0; x < wx1; x++) {
                    left = g[x] ? reach + 1 : left - 1;
                    n[x] = left > 0 ? NearRow : NearNo;
                }
                left = 0;
                for (int x = wx1 - 1; x >= wx0; x--) {
                    left = g[x] ? reach + 1 : left - 1;
                    if (left > 0) {
                        n[x] = NearRow;
                    }
                }
            }
            // 縦: 列ごとに同じことを、横の印(NearRow)を起点にして行う。足した印は NearColumn にして、起点と区別する。
            for (int x = wx0; x < wx1; x++) {
                int left = 0;
                for (int y = wy0; y < wy1; y++) {
                    byte* n = near + (long)y * w + x;
                    left = *n == NearRow ? reach + 1 : left - 1;
                    if (left > 0 && *n == NearNo) {
                        *n = NearColumn;
                    }
                }
                left = 0;
                for (int y = wy1 - 1; y >= wy0; y--) {
                    byte* n = near + (long)y * w + x;
                    left = *n == NearRow ? reach + 1 : left - 1;
                    if (left > 0 && *n == NearNo) {
                        *n = NearColumn;
                    }
                }
            }
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
        /// 続くので残る。消した画素数を返す。near は <see cref="MarkNear"/> が書いたもの、layer は何番目の層か(0 から。
        /// 同じ near を層ごとに使い回すため)、work は作業用。
        /// </summary>
        public static int EraseCrumbs(bool* region, ulong* bits, int w, int h, byte* near,
                                      int wx0, int wy0, int wx1, int wy1, int layer,
                                      System.Collections.Generic.List<int> work) {
            if (bits == null) {
                return 0;
            }
            byte seen = (byte)(NearSeen + layer);
            int erased = 0;
            for (int y = wy0; y < wy1; y++) {
                for (int x = wx0; x < wx1; x++) {
                    int start = y * w + x;
                    if (near[start] == NearNo || near[start] == seen || region[start] || !Bit(bits, start)) {
                        continue;
                    }
                    // 範囲の近くにある残りを、近くの中だけでたどる。遠くへ続いていたら(escaped)切れ端ではない。
                    work.Clear();
                    work.Add(start);
                    near[start] = seen;
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
                                bool inWindow = nx >= wx0 && nx < wx1 && ny >= wy0 && ny < wy1;
                                if (region[j]) {
                                    touchesCleared = true;   // 範囲の中のマスク = これから消す画素
                                } else if (!inWindow || near[j] == NearNo) {
                                    escaped = true;
                                } else if (near[j] != seen) {
                                    near[j] = seen;
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

        /// <summary>
        /// 「ここのマスクを消す」の宛先(並び順の番号)。hasMask[k] は、クリックした所に並び順 k のゾーンのマスクがあるか。
        /// <para>
        /// ほかの操作の宛先は「いま塗っているゾーン」(<see cref="PaintingZoneAt"/>)だが、消したいマスクの持ち主はそのゾーンとは
        /// 限らない。ゾーン A が「塗らない」にした所を別のゾーン B が塗っていると、宛先が B になり、A のマスクが残る。
        /// 塗っているゾーン(painting)→ いま直しているゾーン(active)→ 並びの順で、マスクを持つ最初のゾーンを選ぶ。
        /// どのゾーンにも無ければ -1(呼び出し側は、ほかの操作と同じ宛先にする)。
        /// </para>
        /// </summary>
        public static int PickClearTarget(bool[] hasMask, int painting, int active) {
            if (hasMask == null) {
                return -1;
            }
            if (painting >= 0 && painting < hasMask.Length && hasMask[painting]) {
                return painting;
            }
            if (active >= 0 && active < hasMask.Length && hasMask[active]) {
                return active;
            }
            for (int k = 0; k < hasMask.Length; k++) {
                if (hasMask[k]) {
                    return k;
                }
            }
            return -1;
        }

        /// <summary>
        /// ブラシ 1 区間(配列の [start, end))を塗る / 消す。塗るときは反対側の層(opposite: 同じゾーンの
        /// 含めるを塗るなら除外、除外を塗るなら含める)を同じ区間で消す。右クリックと同じ「後から塗ったほうが
        /// 勝つ」で、消さないと合成の「除外が勝つ」により含めるを塗っても効かない。消すときは塗っている層だけを消す。
        /// opposite は null 可(反対側の層が無い・共通マスクを塗っている)。
        /// </summary>
        public static void BrushSpan(ulong* target, ulong* opposite, int start, int end, bool paint) {
            SetRange(target, start, end, paint);
            if (paint && opposite != null) {
                SetRange(opposite, start, end, false);
            }
        }

        // 画素 [start, end) のビットをまとめて立てる / 下ろす(語の途中は覆いで、まるごとの語は代入で)。
        private static void SetRange(ulong* bits, int start, int end, bool value) {
            if (end <= start) {
                return;
            }
            int w0 = start >> 6, w1 = (end - 1) >> 6;
            ulong head = ~0UL << (start & 63);
            ulong tail = ~0UL >> (63 - ((end - 1) & 63));
            if (w0 == w1) {
                ulong m = head & tail;
                bits[w0] = value ? bits[w0] | m : bits[w0] & ~m;
                return;
            }
            bits[w0] = value ? bits[w0] | head : bits[w0] & ~head;
            ulong fill = value ? ~0UL : 0UL;
            for (int k = w0 + 1; k < w1; k++) {
                bits[k] = fill;
            }
            bits[w1] = value ? bits[w1] | tail : bits[w1] & ~tail;
        }

        /// <summary>マスクの土台の長辺の上限。これを超える元画像では縦横比を保って縮める
        /// (8K を超える元画像はメモリの知らせも出す境目。マスク 1 枚 = 長辺² バイト)。</summary>
        public const int MaskCanvasMaxLong = 8192;

        /// <summary>
        /// マスクの土台の寸法。元画像(ファイル)の寸法に合わせる: 処理と書き出しは元画像の解像度で行うので、
        /// インポートの最大サイズで縮んだ寸法に合わせると、塗った境目が書き出しで粗くなる(4K の元画像を
        /// 2048 で取り込むと 2×2 画素単位)。元画像の寸法が分からないとき(生成テクスチャ等)は取り込み後の寸法。
        /// 長辺が <see cref="MaskCanvasMaxLong"/> を超えるときは縦横比を保って縮めるが、取り込み後の寸法より
        /// 小さくはしない。
        /// </summary>
        public static void CanvasSize(int fileW, int fileH, int importW, int importH, out int w, out int h) {
            w = fileW > 0 && fileH > 0 ? fileW : importW;
            h = fileW > 0 && fileH > 0 ? fileH : importH;
            int longSide = System.Math.Max(w, h);
            int cap = System.Math.Max(MaskCanvasMaxLong, System.Math.Max(importW, importH));
            if (longSide > cap) {
                w = System.Math.Max(1, (int)System.Math.Round((double)w * cap / longSide));
                h = System.Math.Max(1, (int)System.Math.Round((double)h * cap / longSide));
            }
        }

        /// <summary>
        /// ブラシ 1 スタンプ: 中心 (cx, cy)・半径 radius(どれもマスクの画素単位。画素 (x, y) の中心は (x+0.5, y+0.5))
        /// の円の中にある画素を塗る / 消す。円が小さくても中心の画素は必ず塗る。塗るときは反対側の層(opposite)の
        /// 同じ画素を消す(<see cref="BrushSpan"/> と同じ「後から塗ったほうが勝つ」)。
        /// 塗った範囲の外接矩形 [x0, x1) × [y0, y1) を返す(画像の外にかかる所は切る。何も塗らなければ空)。
        /// </summary>
        public static void StampDisk(ulong* target, ulong* opposite, int w, int h,
                                     float cx, float cy, float radius, bool paint,
                                     out int x0, out int y0, out int x1, out int y1) {
            x0 = y0 = x1 = y1 = 0;
            if (target == null || w <= 0 || h <= 0) {
                return;
            }
            float r = System.Math.Max(0f, radius);
            float r2 = r * r;
            int ccx = (int)System.Math.Floor(cx), ccy = (int)System.Math.Floor(cy);
            int ry0 = System.Math.Max(0, (int)System.Math.Floor(cy - r - 0.5f));
            int ry1 = System.Math.Min(h - 1, (int)System.Math.Ceiling(cy + r - 0.5f));
            int bx0 = int.MaxValue, bx1 = int.MinValue, by0 = int.MaxValue, by1 = int.MinValue;
            for (int y = ry0; y <= ry1; y++) {
                float dy = y + 0.5f - cy;
                float rem = r2 - dy * dy;
                int sx0, sx1;
                if (rem >= 0f) {
                    float half = (float)System.Math.Sqrt(rem);
                    sx0 = (int)System.Math.Ceiling(cx - half - 0.5f);
                    sx1 = (int)System.Math.Floor(cx + half - 0.5f);
                } else if (y == ccy) {
                    sx0 = sx1 = ccx;   // 円が画素の中心にかからない小さなブラシでも、中心の画素は塗る
                } else {
                    continue;
                }
                if (y == ccy && (ccx < sx0 || ccx > sx1)) { sx0 = System.Math.Min(sx0, ccx); sx1 = System.Math.Max(sx1, ccx); }
                sx0 = System.Math.Max(0, sx0);
                sx1 = System.Math.Min(w - 1, sx1);
                if (sx1 < sx0) {
                    continue;
                }
                BrushSpan(target, opposite, y * w + sx0, y * w + sx1 + 1, paint);
                if (sx0 < bx0) {
                    bx0 = sx0;
                }
                if (sx1 > bx1) {
                    bx1 = sx1;
                }
                if (y < by0) {
                    by0 = y;
                }
                if (y > by1) {
                    by1 = y;
                }
            }
            if (bx1 < bx0) {
                return;
            }
            x0 = bx0; y0 = by0; x1 = bx1 + 1; y1 = by1 + 1;
        }
    }
}
