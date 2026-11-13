// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca {
    // MaskRegionEdit のうち、ブラシ(区間・円のスタンプ)と、マスクの土台の寸法。クラスの説明は MaskRegionEdit.cs。
    internal static unsafe partial class MaskRegionEdit {
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
