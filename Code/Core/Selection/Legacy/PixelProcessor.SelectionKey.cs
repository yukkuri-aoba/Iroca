// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    // 旧の選択の、選択キャッシュを使ってよいかの判定: 選択に効く入力の鍵、除外が変わっても照合が同じかの
    // 確かめ、保存する「残す」のビット。
    internal static unsafe partial class PixelProcessor
    {
        // 「選択(マッチ→ブラー)に影響する入力だけ」から決定論的なキーを作る。
        // ターゲット色・valueBlend・出力彩度・シャドウ脱彩・wash・autoRecolorAnchor は **含めない**
        // (これらは再着色のみに効くので、変えてもキーは同じ=選択キャッシュがヒットする)。float は
        // ビット表現で完全一致判定(丸め衝突を避ける)。曖昧なものは安全側で含める(ミスが増えるだけ)。
        // マスクの中身も含めない: 含めるマスクは保存する選択に効かず、除外マスクは保存時の配列と比べて
        // 使えるかを判定する(SelectionCache・ExclusionChangeKeepsMatch)。
        // internal はハーネスの --selkey-audit が直接呼ぶため(名前の文字列で反射して呼ぶと、改名がコンパイルで分からない)。
        internal static string BuildSelectionKey(
            ColorZone z, float edgeFeather, int aaCleanup, int holeFillPasses, int holeFillMinNeighbors,
            float relaxedSatMin, float relaxedSatRamp, int maskW, int maskH, bool mixtureBand)
        {
            var sb = new StringBuilder(320);
            void F(float v) { sb.Append(BitConverter.SingleToInt32Bits(v)); sb.Append(','); }
            void I(int v) { sb.Append(v); sb.Append(','); }
            void B(bool v) { sb.Append(v ? '1' : '0'); sb.Append(','); }
            void C(Color c) { F(c.r); F(c.g); F(c.b); F(c.a); }

            I((int)z.mode); B(z.enabled);
            C(z.sampleColor);
            int extraN = z.extraSamples?.Count ?? 0;
            I(extraN);
            for (int i = 0; i < extraN; i++) C(z.extraSamples[i]);
            F(z.tolerance);
            F(z.uvRect.x); F(z.uvRect.y); F(z.uvRect.width); F(z.uvRect.height);
            B(z.useFloodFill); F(z.seedUV.x); F(z.seedUV.y); F(z.sampleUV.x); F(z.sampleUV.y);
            F(z.edgeSoftness); F(z.saturationStrictness); F(z.valueWeight); F(z.satDistWeight);
            F(z.satRampScale); F(z.shadowForgivenessSatMin); F(z.shadowValueFloor); F(z.partSatCeiling); F(z.partHueBand); F(z.chromaCeiling); F(z.chromaThreshold); F(z.saturationGuard);
            F(z.holeFillMargin);
            B(z.highlightRecovery); B(z.highlightBandExpand);
            F(edgeFeather); I(aaCleanup); I(holeFillPasses); I(holeFillMinNeighbors);
            F(relaxedSatMin); F(relaxedSatRamp);
            // 混色帯モード(IsMixtureBandZone)では穴埋め・境界回復を行わない = 選択そのものが変わる。
            // 境界クリーンアップの ON/OFF で切り替わるので、キーに入れる。
            B(mixtureBand);
            // マスクの寸法。マスクは packed ulong[] で寸法が別持ちのため、寸法が変われば同じビット列でも
            // 指す画素が変わる（レビュー §4 低 / 監査 L-6）。除外マスクの比べ直しも同じ寸法どうしでしか成り立たない。
            I(maskW); I(maskH);
            return sb.ToString();
        }

        /// <summary>
        /// 除外(共通 ∪ ゾーン)が old から now に変わったとき、変わった画素(除外になった・外れた)のどれでも
        /// このゾーンの照合(strength と、使うなら highlightPot・matchConf)が +0 か。+0 なら照合の直後の配列は
        /// old で計算したときと同じ(除外の画素には +0 を書くので)で、その後の段はマスクを読まないため、
        /// マスクを掛ける前の選択も old のときと同じになる。変わった画素が全体の 1/16 を超えたら調べずに false
        /// (調べるより計算し直す方が速い)。
        /// 照合の式はゾーンのループの照合と同じでなければならない(量子化をならした色が取れればその色、
        /// 無ければ画素の色と HSV)。ループが使う色の表の値は、同じ色・同じ HSV で同じ関数を呼んだものなので
        /// (ColorPick は位置を見ない)、ここでは表を使わない。
        /// </summary>
        private static bool ExclusionChangeKeepsMatch(ColorZone zone, Color32* originalPixels, PixelHsv hsv,
            int w, int h, int[] colOf, int[] rowBase,
            int maskW, int maskH, ulong[] oldCommon, ulong[] oldZone, ulong[] newCommon, ulong[] newZone,
            bool usesMatchConf, ParallelOptions po)
        {
            if (colOf == null) return true;   // マスクの表が無い = どちらでも除外なし
            var rows = new bool[maskH];
            int words = MaskWordCount(maskW, maskH);
            long changed = 0;
            for (int wi = 0; wi < words; wi++)
            {
                ulong d = (MaskWord(oldCommon, wi) | MaskWord(oldZone, wi))
                        ^ (MaskWord(newCommon, wi) | MaskWord(newZone, wi));
                if (d == 0UL) continue;
                changed += Bits.PopCount(d);
                MarkMaskRows(rows, wi, maskW, maskH);
            }
            if (changed == 0) return true;
            if (changed > (long)maskW * maskH / 16) return false;

            bool usesPot = zone.highlightRecovery;
            int failed = 0;
            Parallel.For(0, h, po, (y, loop) =>
            {
                if (!rows[rowBase[y] / maskW] || Volatile.Read(ref failed) != 0) return;
                int rowOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = rowBase[y] + colOf[x];
                    bool was = MaskSnapshot.GetBit(oldCommon, idx) || MaskSnapshot.GetBit(oldZone, idx);
                    bool now = MaskSnapshot.GetBit(newCommon, idx) || MaskSnapshot.GetBit(newZone, idx);
                    if (was == now) continue;
                    int i = rowOff + x;
                    float s, hPot, mc;
                    if (zone.mode == SelectionMode.ColorPick
                        && TryQuantSmoothedColor(originalPixels, x, y, w, h, zone.tolerance, out Color qc))
                    {
                        Color.RGBToHSV(qc, out float qh, out float qs, out float qv);
                        zone.GetMatchScoresPrecomputedHSV(qh, qs, qv, qc, x, y, w, h,
                            out s, out hPot, out mc);
                    }
                    else
                    {
                        int k = HsvSlot(hsv.Index, i);
                        zone.GetMatchScoresPrecomputedHSV(hsv.H[k], hsv.S[k], hsv.V[k], (Color)originalPixels[i],
                            x, y, w, h, out s, out hPot, out mc);
                    }
                    if (BitConverter.SingleToInt32Bits(s) != 0
                        || (usesPot && BitConverter.SingleToInt32Bits(hPot) != 0)
                        || (usesMatchConf && BitConverter.SingleToInt32Bits(mc) != 0))
                    {
                        Volatile.Write(ref failed, 1);
                        loop.Stop();
                        return;
                    }
                }
            });
            return failed == 0;
        }

        /// <summary>
        /// src[0..len) のうち値が正(&gt; 0)の画素のビットを立てた ulong 配列(64 画素で 1 語、
        /// 画素 i は語 i &gt;&gt; 6 のビット i &amp; 63)を返す。フル画像で解いた keep(strength&gt;0)を
        /// パリティキャッシュ・選択キャッシュへ渡すときに使う。
        /// </summary>
        private static ulong[] PackPositiveBits(float* src, int len, ParallelOptions po)
        {
            // 64 画素ごとの語は互いに独立なので、語のまとまりごとに並列で作る(出力ビット不変)。
            int nWords = (len + 63) >> 6;
            var keepBits = new ulong[nWords];
            Parallel.For(0, (nWords + 1023) >> 10, po, c =>
            {
                int w0 = c << 10, w1 = Math.Min(nWords, w0 + 1024);
                for (int wi = w0; wi < w1; wi++)
                {
                    int b = wi << 6, e = Math.Min(len, b + 64);
                    ulong bits = 0UL;
                    for (int i = b; i < e; i++)
                        if (src[i] > 0f) bits |= 1UL << (i - b);
                    keepBits[wi] = bits;
                }
            });
            return keepBits;
        }
    }
}
