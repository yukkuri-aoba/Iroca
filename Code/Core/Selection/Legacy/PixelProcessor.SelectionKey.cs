// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // 旧の選択の、選択キャッシュを使ってよいかの判定: 選択に効く入力の鍵、除外が変わっても照合が同じかの
    // 確かめ、保存する「残す」のビット。
    internal static unsafe partial class PixelProcessor {
        // 「選択(マッチ→ブラー)に影響する入力だけ」から決定論的なキーを作る。
        // 出力彩度・シャドウ脱彩・wash・autoRecolorAnchor は **含めない**(再着色のみに効くので、変えてもキーは
        // 同じ=選択キャッシュがヒットする)。ターゲット色・valueBlend もそのものは含めないが、白へ抜ける途中のほぼ白を
        // 戻すか(NearWhiteRecolorIsGentle)を通して選択を変えるので、その bool を含める(色を動かすたびに外れないように。
        // ハーネスの --selkey-audit が SELKEY-DERIVED で確かめる)。float は
        // ビット表現で完全一致判定(丸め衝突を避ける)。曖昧なものは安全側で含める(ミスが増えるだけ)。
        // マスクの中身も含めない: 含めるマスクは保存する選択に効かず、除外マスクは保存時の配列と比べて
        // 使えるかを判定する(SelectionCache・ExclusionUnchanged。除外が 1 画素でも変われば計算し直す)。
        // internal はハーネスの --selkey-audit が直接呼ぶため(名前の文字列で反射して呼ぶと、改名がコンパイルで分からない)。
        internal static string BuildSelectionKey(
            ColorZone z, float edgeFeather, int aaCleanup, int holeFillPasses, int holeFillMinNeighbors,
            float relaxedSatMin, float relaxedSatRamp, int maskW, int maskH, bool mixtureBand) {
            var sb = new StringBuilder(320);
            void F(float v) { sb.Append(BitConverter.SingleToInt32Bits(v)); sb.Append(','); }
            void I(int v) { sb.Append(v); sb.Append(','); }
            void B(bool v) { sb.Append(v ? '1' : '0'); sb.Append(','); }
            void C(Color c) { F(c.r); F(c.g); F(c.b); F(c.a); }

            I((int)z.mode); B(z.enabled);
            C(z.sampleColor);
            int extraN = z.extraSamples?.Count ?? 0;
            I(extraN);
            for (int i = 0; i < extraN; i++) {
                C(z.extraSamples[i]);
            }
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
            // 白へ抜ける途中のほぼ白を戻すかは塗り先の色で変わる(RecoverEnclosedNearWhite の 2 回目)。ターゲット色そのものは
            // 入れない(色を動かすたびにキャッシュが外れないように)。
            B(NearWhiteRecolorIsGentle(z));
            // マスクの寸法。マスクは packed ulong[] で寸法が別持ちのため、寸法が変われば同じビット列でも
            // 指す画素が変わる（レビュー §4 低 / 監査 L-6）。除外マスクの比べ直しも同じ寸法どうしでしか成り立たない。
            I(maskW); I(maskH);
            return sb.ToString();
        }

        /// <summary>
        /// 除外(共通 ∪ ゾーン)が old から now で 1 画素も変わっていないか(別の配列でも中身が同じなら true)。
        /// 保存した選択を使い回してよいのは、この場合だけ。除外は照合だけでなく、連結成分の直前にも掛け直す
        /// (線の向こうの成分を切る)ので、除外が変われば、変わった画素の照合が 0 でも、伝播・帯成長・閉領域で
        /// 選択になる画素の連結が変わり、選択が変わりうる。変わった画素の照合を調べて使い回す近道は、この前提
        /// 「照合が 0 の画素なら後の段はマスクを読まない」に頼っていたため、2026-11-11 に外した。
        /// </summary>
        private static bool ExclusionUnchanged(int maskW, int maskH,
            ulong* oldCommon, ulong* oldZone, ulong* newCommon, ulong* newZone) {
            int words = MaskWordCount(maskW, maskH);
            for (int wi = 0; wi < words; wi++) {
                ulong d = (MaskWord(oldCommon, wi) | MaskWord(oldZone, wi))
                        ^ (MaskWord(newCommon, wi) | MaskWord(newZone, wi));
                if (d != 0UL) {
                    return false;
                }
            }
            return true;
        }
    }
}
