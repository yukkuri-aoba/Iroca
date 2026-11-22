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
        /// 右クリックの「ここのマスクを消す」そのものは範囲を使わない: その場所を塗った操作の記録
        /// (<see cref="MaskRegionEdit.ErasePatch"/>)を、記録が無ければマスクのつながったかたまり
        /// (<see cref="MaskRegionEdit.ErasePieceAt"/>)を消す(MaskWorkspace.ClearMaskAt)。これは、範囲を指して消すときの道。
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
    internal static unsafe partial class MaskRegionEdit {
        /// <summary>範囲(n 個。AI 提案の範囲は Mono のヒープの外にある)の操作を、宛先のマスクへ当てる。</summary>
        /// <param name="op">当てる操作。</param>
        /// <param name="region">範囲(マスクと同じ寸法)。</param>
        /// <param name="exclude">宛先の除外マスク。ゾーン宛ならゾーン別、共通宛なら共通。PaintHere と ClearHere では null 可(消す物が無い)。</param>
        /// <param name="include">宛先ゾーンの含めるマスク。PaintHere では必須、ほかは null 可(消す物が無い)。共通宛では null。</param>
        /// <param name="common">全ゾーン共通の除外(重なりを数えるだけ。null 可)。共通宛のときは null を渡す。</param>
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
            var po = CoreParallelism.Options();
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
        /// 右クリックした所が「塗れているか」を見る窓の半径(画素。5×5)。1 画素だけだと、塗られた面の中の細い線や穴に当たって外れる。
        /// </summary>
        public const int PaintedProbeRadius = 2;

        /// <summary>
        /// UV(下原点)の点の周り <see cref="PaintedProbeRadius"/> の正方形(画像の端で切る)の画素の添字(行 0 = 下端)。
        /// 右クリックの宛先(<see cref="PaintingZoneAt"/>)と「ここも塗る」の押した所(<see cref="ZonePaintsAt"/>)が同じ窓で見る。
        /// </summary>
        public static int[] PaintedProbeIndices(float u, float v, int w, int h) {
            int cx = System.Math.Min(System.Math.Max((int)(u * w), 0), w - 1);
            int cy = System.Math.Min(System.Math.Max((int)(v * h), 0), h - 1);
            int x0 = System.Math.Max(0, cx - PaintedProbeRadius), x1 = System.Math.Min(w - 1, cx + PaintedProbeRadius);
            int y0 = System.Math.Max(0, cy - PaintedProbeRadius), y1 = System.Math.Min(h - 1, cy + PaintedProbeRadius);
            var indices = new int[(x1 - x0 + 1) * (y1 - y0 + 1)];
            int n = 0;
            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    indices[n++] = y * w + x;
                }
            }
            return indices;
        }

        /// <summary>
        /// 1 つのゾーンが、右クリックした所をもう塗っているか。strength は押した所の窓(<see cref="PaintedProbeIndices"/>)の
        /// 画素ごとの選択の強さ(null なら塗っていない)。規則は <see cref="PaintingZoneAt"/> と同じ(強さの合計が画素数の半分以上)。
        /// 「ここも塗る」は、塗れている所で押したときだけ範囲を押した所の色に近い画素へ絞る(SamProposalSteps.RegionUse)。
        /// 窓・自動化・ハーネスがこれで決める。
        /// </summary>
        public static bool ZonePaintsAt(float[] strength, int count)
            => strength != null && PaintingZoneAt(new[] { strength }, count) == 0;

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
    }
}
