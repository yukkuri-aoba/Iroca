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
    /// 右クリックの「ここも塗る / ここは塗らない / この部分だけ塗る」を、マスク配列へ当てる純粋な部品
    /// (Unity に依存しない。scripts/unit-run で検査する)。
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
    internal static class MaskRegionEdit
    {
        /// <param name="op">当てる操作。</param>
        /// <param name="region">範囲(マスクと同じ寸法)。</param>
        /// <param name="exclude">宛先の除外マスク。ゾーン宛ならゾーン別、共通宛なら共通。PaintHere では null 可(消す物が無い)。</param>
        /// <param name="include">宛先ゾーンの含めるマスク。PaintHere では必須、ほかは null 可(消す物が無い)。共通宛では null。</param>
        /// <param name="common">全ゾーン共通の除外(重なりを数えるだけ。null 可)。共通宛のときは null を渡す。</param>
        public static MaskRegionEditResult Apply(MaskRegionOp op, bool[] region,
                                                 bool[] exclude, bool[] include, bool[] common)
        {
            var r = new MaskRegionEditResult();
            if (region == null) return r;
            int n = region.Length;
            switch (op)
            {
                case MaskRegionOp.PaintHere:
                    for (int i = 0; i < n; i++)
                    {
                        if (!region[i]) continue;
                        if (!include[i]) { include[i] = true; r.changed++; }
                        if (exclude != null && exclude[i]) { exclude[i] = false; r.changed++; }
                        if (common != null && common[i]) r.commonOverlap++;
                    }
                    break;

                case MaskRegionOp.DontPaintHere:
                    for (int i = 0; i < n; i++)
                    {
                        if (!region[i]) continue;
                        if (!exclude[i]) { exclude[i] = true; r.changed++; }
                        if (include != null && include[i]) { include[i] = false; r.changed++; }
                    }
                    break;

                case MaskRegionOp.OnlyThisPart:
                    for (int i = 0; i < n; i++)
                    {
                        if (region[i])
                        {
                            if (exclude[i]) { exclude[i] = false; r.changed++; }
                            if (common != null && common[i]) r.commonOverlap++;
                        }
                        else
                        {
                            if (!exclude[i]) { exclude[i] = true; r.changed++; }
                            if (include != null && include[i]) { include[i] = false; r.changed++; }
                        }
                    }
                    break;
            }
            return r;
        }

        /// <summary>
        /// ブラシ 1 区間(配列の [start, end))を塗る / 消す。塗るときは反対側の層(opposite: 同じゾーンの
        /// 含めるを塗るなら除外、除外を塗るなら含める)を同じ区間で消す。右クリックと同じ「後から塗ったほうが
        /// 勝つ」で、消さないと合成の「除外が勝つ」により含めるを塗っても効かない。消すときは塗っている層だけを消す。
        /// opposite は null 可(反対側の層が無い・共通マスクを塗っている)。
        /// </summary>
        public static void BrushSpan(bool[] target, bool[] opposite, int start, int end, bool paint)
        {
            for (int i = start; i < end; i++) target[i] = paint;
            if (paint && opposite != null)
                for (int i = start; i < end; i++) opposite[i] = false;
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
        public static void StampDisk(bool[] target, bool[] opposite, int w, int h,
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
