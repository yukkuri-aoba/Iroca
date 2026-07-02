// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    // PixelProcessor: OkLab 再着色本体(RecolorPixel)・再着色アンカー推定・関連定数。
    internal static partial class PixelProcessor
    {
        // L 再マップの彩度ゲート定数。彩度が sample の何割に達したら remap をフル適用するか。
        // これ未満の低彩度画素は元 L を保持し、target が sample より明るい場合の暗部持ち上げ
        // (=ロゴ周辺の白/灰ノイズ)を防ぐ。
        private const float OklabRemapFullChromaFrac = 0.35f;

        // chroma 増幅キャップ(有彩ターゲット向け WS-R 拡張): tC > sC の色相変化で OkLab→RGB の
        // lum 感度が高まりバンディングが発生しうる(彩度比 tC/sC が大きいほど明度コントラストが増幅される)。
        // output chroma = mag*tC が sC*Factor を超えないよう mag を制限。
        private const float ChromaAmpMaxFactor = 1.0f;

        // サンプル自動補正(再着色アンカー正規化)の定数。
        // すべて領域統計に対する相対量(特定色/座標/テクスチャ非依存)。
        private const float AnchorStrengthMin   = 0.9f;   // コアマッチのみ採用(AA縁・feather裾の混色を除外)
        private const int   AnchorMinPixels     = 100;    // これ未満はフォールバック(ComputeWashSample と同基準)
        private const float AnchorBodySatrFrac  = 0.5f;   // 地色とみなす OkLab 飽和度(C/L)下限(候補中央値比)。
                                                          // C/L は乗算シェーディング不変(L,C とも k^(1/3) 比例)なので
                                                          // wash 済み明部・AA縁グレーを陰影レベル非依存に除外できる。
        private const float AnchorLPct          = 0.90f;  // 代表 L percentile(鏡面ハイライト芯の最上位~10%を除外)
        private const float AnchorLBandLoPct    = 0.80f;  // 代表 C を取る L 帯の下限 percentile
        private const float AnchorLBandHiPct    = 0.97f;  // 同上限 percentile
        private const float AnchorMinLSpread    = 0.02f;  // L の P95-P05 がこれ未満=フラット領域は補正不要
        private const float AnchorSatrHistMax   = 4f;     // 飽和度(C/L)ヒストグラムの値域上限
        private const float AnchorChromaHistMax = 0.5f;   // chroma ヒストグラムの値域上限(OkLab C は ~0.33 まで)

        /// <summary>
        /// サンプル自動補正: OkLab 再着色のアンカー (sL, sC) をマッチ領域の統計から推定する。
        ///
        /// OkLab 再着色は (sL, sC) を不動点とする相対写像のため、スポイトした画素の陰影レベルが
        /// そのまま出力全体の明度・彩度バイアスになる(明部平均 HSV-S で最大 ~0.22 のブレを実測)。
        /// 本関数は「パーツの明るい面の地色」を統計的に推定して返し、スポイト位置非依存にする。
        /// マッチング・wash には影響しない(呼び出し側がアンカー 2 値だけを置き換える)。
        ///
        /// percentile はヒストグラム離散化のため厳密値と最大 1/255 の差を許容する。
        /// </summary>
        /// <returns>false = フォールバック(スポイト色のまま従来挙動)。
        /// 条件: コア画素&lt;100 / 地色画素&lt;100 / フラット領域(L スプレッド&lt;0.02) / 推定 C≈0。</returns>
        private static bool TryComputeRecolorAnchor(
            Color32[] px, float[] strength, out float anchorL, out float anchorC)
        {
            anchorL = 0f;
            anchorC = 0f;
            int len = px.Length;

            // コア画素(strength>=AnchorStrengthMin & a>=128)の OkLab (L, C) を pass1 で一度だけ
            // 計算して圧縮配列に保存し、pass2/3 はそれを読む。従来は 3 パスとも全画素を走査して
            // 同じ画素の RgbToOklab を再計算していた(4K で計 50M 回の OkLab 変換)。出力は同値。
            // 配列は s_floatPool から借用(candCount<=len なので 2^24 までプール内)。
            float[] candL = s_floatPool.Rent(len);
            float[] candC = s_floatPool.Rent(len);
            try
            {
                // pass 1: (L, C) を保存しつつ飽和度(C/L)ヒストグラム → 中央値から地色下限を決める
                var satrHist = new int[256];
                int candCount = 0;
                for (int i = 0; i < len; i++)
                {
                    if (strength[i] < AnchorStrengthMin || px[i].a < 128) continue;
                    RgbToOklab(px[i].r, px[i].g, px[i].b,
                        out float L, out float a, out float b);
                    float C = Mathf.Sqrt(a * a + b * b);
                    candL[candCount] = L;
                    candC[candCount] = C;
                    candCount++;
                    float satr = C / Mathf.Max(L, 1e-4f);
                    int bin = Mathf.Clamp((int)(satr / AnchorSatrHistMax * 255f), 0, 255);
                    satrHist[bin]++;
                }
                if (candCount < AnchorMinPixels) return false;
                float satrFloor = AnchorBodySatrFrac *
                    HistValueAtPercentile(satrHist, candCount, 0.5f, AnchorSatrHistMax);

                // pass 2: 地色画素(飽和度 ≥ 下限)の L ヒストグラム → 代表 L と L 帯
                var lHist = new int[256];
                int bodyCount = 0;
                for (int k = 0; k < candCount; k++)
                {
                    float L = candL[k];
                    if (candC[k] / Mathf.Max(L, 1e-4f) < satrFloor) continue;
                    lHist[Mathf.Clamp((int)(L * 255f), 0, 255)]++;
                    bodyCount++;
                }
                if (bodyCount < AnchorMinPixels) return false;
                float l05 = HistValueAtPercentile(lHist, bodyCount, 0.05f, 1f);
                float l95 = HistValueAtPercentile(lHist, bodyCount, 0.95f, 1f);
                if (l95 - l05 < AnchorMinLSpread) return false;  // フラット領域: どこを取っても同じ
                anchorL = HistValueAtPercentile(lHist, bodyCount, AnchorLPct, 1f);
                float bandLo = HistValueAtPercentile(lHist, bodyCount, AnchorLBandLoPct, 1f);
                float bandHi = HistValueAtPercentile(lHist, bodyCount, AnchorLBandHiPct, 1f);

                // pass 3: L 帯内の地色画素の chroma 中央値 → 代表 C
                var cHist = new int[256];
                int bandCount = 0;
                for (int k = 0; k < candCount; k++)
                {
                    float L = candL[k];
                    float c = candC[k];
                    if (c / Mathf.Max(L, 1e-4f) < satrFloor) continue;
                    if (L < bandLo || L > bandHi) continue;
                    cHist[Mathf.Clamp((int)(c / AnchorChromaHistMax * 255f), 0, 255)]++;
                    bandCount++;
                }
                if (bandCount < 1) return false;
                anchorC = HistValueAtPercentile(cHist, bandCount, 0.5f, AnchorChromaHistMax);
                return anchorC > 1e-4f;
            }
            finally
            {
                s_floatPool.Return(candL);
                s_floatPool.Return(candC);
            }
        }

        // RecolorPixel のゾーン不変パラメータ(再着色ホットループの前に 1 回だけ確定する値)をまとめた
        // readonly struct。in 渡しで per-pixel のコピーを避ける。フィールドは旧 RecolorPixel 引数を
        // そのまま転記(型・順序・意味を保持)。約30引数の緩和=シグネチャ整理のみで数値ロジックは不変。
        private readonly struct RecolorParams
        {
            public readonly float okMagScale, okTa, okTb;
            public readonly bool okGray;
            public readonly float okGa, okGb;
            public readonly float okSL, okTL, okSC, okChromaMaxMag;
            public readonly float valueBlend, shadowDesaturation;
            public readonly float sS, tR, tG, tB;
            public readonly float washR, washG, washB, washV;
            public readonly bool applyHighlightWash;
            public readonly float achromaWeight, osat;
            public readonly bool hasRegL;
            public readonly float regLlo, regLhi;
            public RecolorParams(
                float okMagScale, float okTa, float okTb, bool okGray, float okGa, float okGb,
                float okSL, float okTL, float okSC, float okChromaMaxMag,
                float valueBlend, float shadowDesaturation, float sS, float tR, float tG, float tB,
                float washR, float washG, float washB, float washV, bool applyHighlightWash,
                float achromaWeight, float osat, bool hasRegL, float regLlo, float regLhi)
            {
                this.okMagScale = okMagScale; this.okTa = okTa; this.okTb = okTb;
                this.okGray = okGray; this.okGa = okGa; this.okGb = okGb;
                this.okSL = okSL; this.okTL = okTL; this.okSC = okSC; this.okChromaMaxMag = okChromaMaxMag;
                this.valueBlend = valueBlend; this.shadowDesaturation = shadowDesaturation;
                this.sS = sS; this.tR = tR; this.tG = tG; this.tB = tB;
                this.washR = washR; this.washG = washG; this.washB = washB; this.washV = washV;
                this.applyHighlightWash = applyHighlightWash;
                this.achromaWeight = achromaWeight; this.osat = osat;
                this.hasRegL = hasRegL; this.regLlo = regLlo; this.regLhi = regLhi;
            }
        }

        private static Color32 RecolorPixel(
            byte oRb, byte oGb, byte oBb,
            float oV, float alpha,
            in RecolorParams p,
            float regLmid)
        {
            // ゾーン不変パラメータをローカルへ展開する。以降の本体ロジックは従来のまま=出力バイト不変。
            float okMagScale = p.okMagScale, okTa = p.okTa, okTb = p.okTb;
            bool okGray = p.okGray;
            float okGa = p.okGa, okGb = p.okGb;
            float okSL = p.okSL, okTL = p.okTL, okSC = p.okSC, okChromaMaxMag = p.okChromaMaxMag;
            float valueBlend = p.valueBlend, shadowDesaturation = p.shadowDesaturation;
            float sS = p.sS, tR = p.tR, tG = p.tG, tB = p.tB;
            float washR = p.washR, washG = p.washG, washB = p.washB, washV = p.washV;
            bool applyHighlightWash = p.applyHighlightWash;
            float achromaWeight = p.achromaWeight, osat = p.osat;
            bool hasRegL = p.hasRegL;
            float regLlo = p.regLlo, regLhi = p.regLhi;
            // === OkLab 明度マップ + 彩度(向きは target 色相に均一化)リカラー ===
            // L: 2区間線形リマップ (0→0, sL→tL, 1→1)。base を target 明度へ寄せる。単調維持
            //    (リング無し)・ガンマット内(クリップ無し)・白→白/黒→黒。明度を完全保持すると
            //    暗い色→黄色が brown 化するため base は target 明度に合わせる。
            // (a,b)_new = |chroma|·(osat/sC)·(target 色相単位ベクトル) ← 大きさは元 chroma に比例、
            //    向きは target に均一化。旧版の「色相回転保持」は単色ロゴの AA縁でオレンジの色相
            //    ノイズを生んだため、向きを揃えて L を保ったまま色相を均一化する。
            RgbToOklab(oRb, oGb, oBb, out float oL, out float oa, out float ob);
            float oC = Mathf.Sqrt(oa * oa + ob * ob);   // 元画素の chroma (L 彩度ゲートでも使う)
            float na, nb;
            if (okGray)
            {
                // sample が無彩(グレー): target chroma を一律付与(旧 newS=tS 相当)
                na = okGa;
                nb = okGb;
            }
            else
            {
                float mag = oC * okMagScale;            // |chroma|/sC · osat
                // WS-R: 無彩サンプルでは oC/sC が微小彩度ノイズを増幅(脚色)。uniform target 彩度
                // (osat, oC 非依存)へ achromaWeight でフェードし増幅を止める。weight=0 で従来式。
                if (achromaWeight > 1e-4f)
                    mag = mag * (1f - achromaWeight) + osat * achromaWeight;
                // WS-R 有彩版: tC > sC のとき output chroma = mag*tC が sC*Factor を超えないよう制限。
                // achromaWeight=1 時は上記で mag=osat 固定済みなのでキャップは no-op。
                // 上限 mag はゾーン定数 okChromaMaxMag に事前算出済み(キャップ非適用時は +∞ で
                // この比較は no-op)。旧版は per-pixel で tC=sqrt(okTa²+okTb²) と maxMag を再計算していた。
                if (mag > okChromaMaxMag) mag = okChromaMaxMag;
                na = mag * okTa;                        // 向きは target 色相 (zTa, zTb)
                nb = mag * okTb;
            }
            // 2区間線形リマップ: [0,sL]→[0,tL], [sL,1]→[tL,1]。sL→tL を不動点に base を target 明度へ。
            float remapL = oL <= okSL
                ? (oL / Mathf.Max(okSL, 1e-4f)) * okTL
                : okTL + (oL - okSL) / Mathf.Max(1f - okSL, 1e-4f) * (1f - okTL);
            // 彩度ゲート付き L 再マップ (2026-06-07): 低彩度画素では remap(=明るさの持ち上げ)を抑え、
            // 元の L(暗さ)を保持する。リング/brown 化は「本来のベース色」=高彩度画素で起きる現象なので
            // remap が必要なのは高彩度画素だけ。一方、ベース×暗部/白の混色や AA 縁(低彩度)に remap を
            // かけると、target が sample より知覚的に明るいとき暗部が持ち上がり「明るい灰スペック」
            // =ロゴ周辺の白/灰ノイズになる。chroma_frac=oC/(sC·FULL_FRAC) で彩度が sample の FULL_FRAC 割に
            // 達したらフル remap、それ未満は元 L を保持。sample 彩度に対する相対量なので色非依存。
            // sample 無彩(okGray)時は従来どおり一律 remap。
            float effRemapL = remapL;
            if (!okGray && okSC > 1e-4f)
            {
                float chromaFrac = Mathf.Clamp01(oC / (okSC * OklabRemapFullChromaFrac));
                effRemapL = oL + (remapL - oL) * chromaFrac;
            }
            // valueBlend=1 でフル階調、<1 で target フラットトーンへ寄せる。
            float nL = okTL * (1f - valueBlend) + effRemapL * valueBlend;

            // WS-R: 無彩再着色パスの L。マッチ領域の L レンジ[lo,hi]を target 側ヘッドルームへ
            // 順序保存で収める(2区間リマップ・彩度ゲートを迂回)。白い三角→黒のまだら/明度崩壊を直す。
            // weight=0(有彩×有彩)では完全 no-op=バイト不変。
            if (achromaWeight > 1e-4f && hasRegL)
            {
                // 形(立体感)維持: 領域中央値を target 側の控えめ offset(center)に置き、中央値からの
                // 偏差を AchromaFormGain 倍して陰影を知覚可能な大きさへ拡張する。暗部は 0 へ、明部は
                // center 近辺の暗灰に収め、白残り(段差)は clamp で防ぐ。単調・領域統計由来で特定座標
                // 非依存。元の微小陰影をそのまま写すと暗部/明部で知覚的に平坦化(ベタ黒/ベタ白)するため、
                // 控えめに増幅する(知覚補償)。
                float center = okTL < 0.5f ? AchromaFormOffset : (1f - AchromaFormOffset);
                float rangeRemap = Mathf.Clamp(center + (oL - regLmid) * AchromaFormGain, 0f, 1f);
                float nLAchroma = okTL * (1f - valueBlend) + rangeRemap * valueBlend;
                nL = nL * (1f - achromaWeight) + nLAchroma * achromaWeight;
            }

            // 暗部脱彩 (旧 shadowDesaturation の OkLab 等価): 暗い画素の chroma を最大 50% 抑制。
            if (shadowDesaturation > 0f && oV < shadowDesaturation)
            {
                float shadowIntensity = Mathf.Clamp01((shadowDesaturation - oV) / shadowDesaturation);
                float fac = 1f - 0.5f * shadowIntensity;
                na *= fac;
                nb *= fac;
            }

            OklabToRgb(nL, na, nb, out float outR, out float outG, out float outB);
            Color result = new Color(Mathf.Clamp01(outR), Mathf.Clamp01(outG), Mathf.Clamp01(outB), 1f);

            // ハイライト合成 (P5 軸射影版・residual なし / 2026-05-26):
            // ピクセルを「sample → (1,1,1) 白直線」に射影して進行度 w を取り、
            // 同じ w を使って target 軸上の対応点を求める。residual (軸からのずれ) は
            // 捨てる — これにより青の色相歪みが target 側 (赤) にコピーされてピンク化する
            // P5 純正版の副作用を構造的に除去する。
            //
            //   proj_t = target + w * (white - target)
            //   result = Lerp(hsv_result, proj_t, valRise)
            //
            // 直感: pixel が sample から白に向けて 0.99 進んでいるなら (ハイライト中心)、
            //       target からも白に向けて 0.99 進んだ点が出力色。中心は完全な白では
            //       なく「target に向けて 1% 染まった白」(= わずかに赤い白)。
            //       周辺 (w=0.5) は target と white の中間 (例: 赤と白で薄い赤)。
            //
            // 境界 (oV ≤ sV) は valRise=0 で hsv_result に切り戻すため不連続なし。
            // wash 用サンプル(washR/G/B/V): 既定は match と同じ sample。俯瞰スポイト補正では
            // パーツ地色(同色相・低V)が渡され、ハイライト合成 (oV>washV) がドーム全体に効く。
            // match/base は sample のままなので再着色範囲は不変(新規 FP なし)。
            //
            // applyHighlightWash ゲート (2026-06-04): この白寄せ射影は既定 OFF のオプトイン。
            // OFF のときは HSV transfer のみで明部の明度・彩度構造を温存する。
            if (applyHighlightWash && sS > 0.01f && oV > washV)
            {
                float dR = 1f - washR;
                float dG = 1f - washG;
                float dB = 1f - washB;
                float dirSq = dR * dR + dG * dG + dB * dB;
                if (dirSq > 1e-6f)
                {
                    // wash 射影は 0..1 の RGB で行うため byte 入力をここで float に戻す。
                    float oR = oRb / 255f, oG = oGb / 255f, oB = oBb / 255f;
                    float pR = oR - washR;
                    float pG = oG - washG;
                    float pB = oB - washB;
                    float w = Mathf.Clamp01((pR * dR + pG * dG + pB * dB) / dirSq);

                    // target 軸上の対応点 (residual は捨てる)
                    float projTR = tR + w * (1f - tR);
                    float projTG = tG + w * (1f - tG);
                    float projTB = tB + w * (1f - tB);

                    // 軸残差フェード (2026-06-04): 元画素が wash→白 軸からどれだけ外れて
                    // いるか(resid)に応じて白寄せを減衰させる。
                    //   真の鏡面ハイライト = 地色が光で白く飛んだもの = 軸上(resid≈0) → フル白寄せ
                    //   有彩の模様        = 別色・高彩度        = 軸外(resid 大)  → 白寄せ 0
                    // 軸外で proj_t(=軸上の脱彩点)への置換を止めるので、明るい同系色の模様まで
                    // 白化して模様が壊れる過剰白化を構造的に排除する。残差を捨てる(P5)のは
                    // resid≈0 の画素に限られるためピンク化抑止特性も維持。
                    float axR = washR + w * dR;
                    float axG = washG + w * dG;
                    float axB = washB + w * dB;
                    float rr = oR - axR, rg = oG - axG, rb = oB - axB;
                    float resid = Mathf.Sqrt(rr * rr + rg * rg + rb * rb);
                    float axisFade = Mathf.Clamp01(1f - resid / HlBandAxisEps);

                    // 境界連続性のため valRise でフェード (oV=washV で valRise=0、hsv_result に戻る)
                    float valRise = Mathf.Clamp01((oV - washV) / Mathf.Max(0.05f, 1f - washV)) * axisFade;
                    result.r = Mathf.Lerp(result.r, projTR, valRise);
                    result.g = Mathf.Lerp(result.g, projTG, valRise);
                    result.b = Mathf.Lerp(result.b, projTB, valRise);
                }
            }

            result.a = alpha;
            return result;
        }
    }
}
