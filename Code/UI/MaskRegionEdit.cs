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
    }
}
