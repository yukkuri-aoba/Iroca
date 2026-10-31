// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca
{
    /// <summary>プレビューの右クリックで、選んだ範囲(AI の判定・メッシュの島)に当てる操作。</summary>
    internal enum MaskRegionOp
    {
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
        /// ここのマスクを消す: 範囲の中の「含める」と除外の両方を消す(手入れ前の色の判定に戻す)。
        /// ゾーン宛のとき、全ゾーン共通の除外は消さない(重なりは数える)。
        /// </summary>
        ClearHere,
    }

    /// <summary><see cref="MaskRegionEdit.Apply"/> の結果。</summary>
    internal struct MaskRegionEditResult
    {
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
    internal static unsafe class MaskRegionEdit
    {
        /// <param name="op">当てる操作。</param>
        /// <param name="region">範囲(マスクと同じ寸法)。</param>
        /// <param name="exclude">宛先の除外マスク。ゾーン宛ならゾーン別、共通宛なら共通。PaintHere と ClearHere では null 可(消す物が無い)。</param>
        /// <param name="include">宛先ゾーンの含めるマスク。PaintHere では必須、ほかは null 可(消す物が無い)。共通宛では null。</param>
        /// <param name="common">全ゾーン共通の除外(重なりを数えるだけ。null 可)。共通宛のときは null を渡す。</param>
        public static MaskRegionEditResult Apply(MaskRegionOp op, bool[] region,
                                                 ulong* exclude, ulong* include, ulong* common)
        {
            if (region == null) return new MaskRegionEditResult();
            fixed (bool* p = region) return Apply(op, p, region.Length, exclude, include, common);
        }

        /// <summary>
        /// <see cref="Apply(MaskRegionOp, bool[], ulong*, ulong*, ulong*)"/> の、範囲がポインタ(n 個)の版
        /// (AI 提案の範囲は Mono のヒープの外にある)。
        /// </summary>
        public static MaskRegionEditResult Apply(MaskRegionOp op, bool* region, int n,
                                                 ulong* exclude, ulong* include, ulong* common)
        {
            var r = new MaskRegionEditResult();
            if (region == null) return r;
            switch (op)
            {
                case MaskRegionOp.PaintHere:
                    for (int i = 0; i < n; i++)
                    {
                        if (!region[i]) continue;
                        int w = i >> 6;
                        ulong bit = 1UL << (i & 63);
                        if ((include[w] & bit) == 0UL) { include[w] |= bit; r.changed++; }
                        if (exclude != null && (exclude[w] & bit) != 0UL) { exclude[w] &= ~bit; r.changed++; }
                        if (common != null && (common[w] & bit) != 0UL) r.commonOverlap++;
                    }
                    break;

                case MaskRegionOp.DontPaintHere:
                    for (int i = 0; i < n; i++)
                    {
                        if (!region[i]) continue;
                        int w = i >> 6;
                        ulong bit = 1UL << (i & 63);
                        if ((exclude[w] & bit) == 0UL) { exclude[w] |= bit; r.changed++; }
                        if (include != null && (include[w] & bit) != 0UL) { include[w] &= ~bit; r.changed++; }
                    }
                    break;

                case MaskRegionOp.ClearHere:
                    return ClearRegion(region, n, exclude, include, common, write: true);

                case MaskRegionOp.OnlyThisPart:
                    for (int i = 0; i < n; i++)
                    {
                        int w = i >> 6;
                        ulong bit = 1UL << (i & 63);
                        if (region[i])
                        {
                            if ((exclude[w] & bit) != 0UL) { exclude[w] &= ~bit; r.changed++; }
                            if (common != null && (common[w] & bit) != 0UL) r.commonOverlap++;
                        }
                        else
                        {
                            if ((exclude[w] & bit) == 0UL) { exclude[w] |= bit; r.changed++; }
                            if (include != null && (include[w] & bit) != 0UL) { include[w] &= ~bit; r.changed++; }
                        }
                    }
                    break;
            }
            return r;
        }

        /// <summary>
        /// <see cref="MaskRegionOp.ClearHere"/> を当てたときの結果を、マスクを書き換えずに求める。消す物が無いときに、
        /// 層を写さず Undo に空の手も積まないよう、当てる前に数える。
        /// </summary>
        public static MaskRegionEditResult CountClear(bool* region, int n, ulong* exclude, ulong* include, ulong* common)
            => region == null ? new MaskRegionEditResult() : ClearRegion(region, n, exclude, include, common, write: false);

        private static MaskRegionEditResult ClearRegion(bool* region, int n, ulong* exclude, ulong* include, ulong* common, bool write)
        {
            var r = new MaskRegionEditResult();
            for (int i = 0; i < n; i++)
            {
                if (!region[i]) continue;
                int w = i >> 6;
                ulong bit = 1UL << (i & 63);
                if (exclude != null && (exclude[w] & bit) != 0UL) { if (write) exclude[w] &= ~bit; r.changed++; }
                if (include != null && (include[w] & bit) != 0UL) { if (write) include[w] &= ~bit; r.changed++; }
                if (common != null && (common[w] & bit) != 0UL) r.commonOverlap++;
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
        public static int PaintingZoneAt(float[][] strengths, int sampleCount)
        {
            if (strengths == null || sampleCount <= 0) return -1;
            var share = new float[strengths.Length];
            for (int i = 0; i < sampleCount; i++)
            {
                float room = 1f;
                for (int k = 0; k < strengths.Length && room > 0f; k++)
                {
                    var s = strengths[k];
                    if (s == null) continue;
                    float take = System.Math.Min(System.Math.Max(s[i], 0f), room);
                    share[k] += take;
                    room -= take;
                }
            }
            int best = -1;
            for (int k = 0; k < share.Length; k++)
                if (share[k] >= sampleCount * 0.5f && (best < 0 || share[k] > share[best])) best = k;
            return best;
        }

        /// <summary>「ここのマスクを消す」で、クリックした所にマスクがあるかを見る範囲(マスクの画素。細いマスクを外さないよう周りも見る)。</summary>
        public const int ClearProbeRadius = 2;

        /// <summary>中心 (cx, cy) から半径 radius の正方形の中に、立っている画素があるか。bits が null なら false。</summary>
        public static bool AnyBitNear(ulong* bits, int w, int h, int cx, int cy, int radius)
        {
            if (bits == null) return false;
            int y0 = System.Math.Max(0, cy - radius), y1 = System.Math.Min(h - 1, cy + radius);
            int x0 = System.Math.Max(0, cx - radius), x1 = System.Math.Min(w - 1, cx + radius);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * w + x;
                    if ((bits[i >> 6] & (1UL << (i & 63))) != 0UL) return true;
                }
            }
            return false;
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
        public static int PickClearTarget(bool[] hasMask, int painting, int active)
        {
            if (hasMask == null) return -1;
            if (painting >= 0 && painting < hasMask.Length && hasMask[painting]) return painting;
            if (active >= 0 && active < hasMask.Length && hasMask[active]) return active;
            for (int k = 0; k < hasMask.Length; k++)
                if (hasMask[k]) return k;
            return -1;
        }

        /// <summary>
        /// ブラシ 1 区間(配列の [start, end))を塗る / 消す。塗るときは反対側の層(opposite: 同じゾーンの
        /// 含めるを塗るなら除外、除外を塗るなら含める)を同じ区間で消す。右クリックと同じ「後から塗ったほうが
        /// 勝つ」で、消さないと合成の「除外が勝つ」により含めるを塗っても効かない。消すときは塗っている層だけを消す。
        /// opposite は null 可(反対側の層が無い・共通マスクを塗っている)。
        /// </summary>
        public static void BrushSpan(ulong* target, ulong* opposite, int start, int end, bool paint)
        {
            SetRange(target, start, end, paint);
            if (paint && opposite != null) SetRange(opposite, start, end, false);
        }

        // 画素 [start, end) のビットをまとめて立てる / 下ろす(語の途中は覆いで、まるごとの語は代入で)。
        private static void SetRange(ulong* bits, int start, int end, bool value)
        {
            if (end <= start) return;
            int w0 = start >> 6, w1 = (end - 1) >> 6;
            ulong head = ~0UL << (start & 63);
            ulong tail = ~0UL >> (63 - ((end - 1) & 63));
            if (w0 == w1)
            {
                ulong m = head & tail;
                bits[w0] = value ? bits[w0] | m : bits[w0] & ~m;
                return;
            }
            bits[w0] = value ? bits[w0] | head : bits[w0] & ~head;
            ulong fill = value ? ~0UL : 0UL;
            for (int k = w0 + 1; k < w1; k++) bits[k] = fill;
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
        public static void CanvasSize(int fileW, int fileH, int importW, int importH, out int w, out int h)
        {
            w = fileW > 0 && fileH > 0 ? fileW : importW;
            h = fileW > 0 && fileH > 0 ? fileH : importH;
            int longSide = System.Math.Max(w, h);
            int cap = System.Math.Max(MaskCanvasMaxLong, System.Math.Max(importW, importH));
            if (longSide > cap)
            {
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
                                     out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (target == null || w <= 0 || h <= 0) return;
            float r = System.Math.Max(0f, radius);
            float r2 = r * r;
            int ccx = (int)System.Math.Floor(cx), ccy = (int)System.Math.Floor(cy);
            int ry0 = System.Math.Max(0, (int)System.Math.Floor(cy - r - 0.5f));
            int ry1 = System.Math.Min(h - 1, (int)System.Math.Ceiling(cy + r - 0.5f));
            int bx0 = int.MaxValue, bx1 = int.MinValue, by0 = int.MaxValue, by1 = int.MinValue;
            for (int y = ry0; y <= ry1; y++)
            {
                float dy = y + 0.5f - cy;
                float rem = r2 - dy * dy;
                int sx0, sx1;
                if (rem >= 0f)
                {
                    float half = (float)System.Math.Sqrt(rem);
                    sx0 = (int)System.Math.Ceiling(cx - half - 0.5f);
                    sx1 = (int)System.Math.Floor(cx + half - 0.5f);
                }
                else if (y == ccy)
                {
                    sx0 = sx1 = ccx;   // 円が画素の中心にかからない小さなブラシでも、中心の画素は塗る
                }
                else continue;
                if (y == ccy && (ccx < sx0 || ccx > sx1)) { sx0 = System.Math.Min(sx0, ccx); sx1 = System.Math.Max(sx1, ccx); }
                sx0 = System.Math.Max(0, sx0);
                sx1 = System.Math.Min(w - 1, sx1);
                if (sx1 < sx0) continue;
                BrushSpan(target, opposite, y * w + sx0, y * w + sx1 + 1, paint);
                if (sx0 < bx0) bx0 = sx0;
                if (sx1 > bx1) bx1 = sx1;
                if (y < by0) by0 = y;
                if (y > by1) by1 = y;
            }
            if (bx1 < bx0) return;
            x0 = bx0; y0 = by0; x1 = bx1 + 1; y1 = by1 + 1;
        }
    }
}
