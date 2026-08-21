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

        // 暗いターゲットの明部白暴走対策: 2区間リマップの上端を 1(白) でなく min(1, tL*この値) に
        // キャップする。tL≥1/MULT(=0.5) では topL=1 で完全 no-op(中〜明ターゲットは従来挙動=byte 不変)、
        // tL<0.5 の暗いターゲットだけ明部(スペキュラ)が白へ暴走せず tL×MULT に収まる。暗い色を
        // ターゲットにしても出力がその暗さに収まるための既定挙動。
        private const float HighlightLMult = 2.0f;

        // chroma 増幅キャップ(有彩ターゲット向けの無彩パス拡張): tC > sC の色相変化で OkLab→RGB の
        // lum 感度が高まりバンディングが発生しうる(彩度比 tC/sC が大きいほど明度コントラストが増幅される)。
        // output chroma = mag*tC が sC*Factor を超えないよう mag を制限。
        private const float ChromaAmpMaxFactor = 1.0f;

        // ハイライト白寄せ(wash)の軸残差フェード定数 (2026-08-15 OkLab 方向分解版)。
        // 旧実装は RGB ユークリッド残差(許容 HlBandAxisEps=0.10)で一律減衰しており、作画表現として
        // 色相を寒色にずらした手描きスペキュラ(淡青の宝石光点など)が「軸外の模様」と誤認され、
        // base 転写に落ちて明るいターゲットで光点が桃色に濁った
        // (dev_safe/docs/specular-hue-drift-2026-08.md)。残差を OkLab で
        //   彩度超過(軸予測より高彩度) / 暗化(軸予測より暗い) / 色相弧(同彩度での色相回転)
        // に分解し、前 2 成分は「模様の証拠」として減衰に使い、色相弧は「色付き光点の証拠」として
        // 白寄せから元色保持への切り替えに使う(合成本体のコメント参照)。
        // いずれも全テクスチャ共通の知覚量で、特定色・座標・テクスチャ統計に依存しない。
        //   HlWashResidEps   : 模様性残差(彩度超過・暗化)の許容 OkLab 距離。実テクスチャの実測では
        //                      模様・金属光沢の残差 ≥ 0.033、色相ずらし光点 ≤ 0.01 に分離する。
        //   HlWashTintKeepLo : 色相弧がこれ以下なら従来どおり projT へ白寄せ(軸上ドーム階調の互換)。
        //                      実測: 真の鏡面ハイライト(軸上)の色相弧は ≤ 0.007。
        //   HlWashTintKeepHi : 色相弧がこれ以上なら元色保持側へ完全に切り替え。実測: 色相ずらし
        //                      光点は ≥ 0.023。
        //   HlWashKeepWhiteLo/Hi: 元色保持を「白に近い画素(=光点の芯)」だけに絞る白距離ゲート。
        //                      ΔE_white = √((1−L)²+C²) が Lo 以下で完全保持、Hi 以上で保持ゼロ。
        //                      色相をずらした光点でも芯はほぼ白(実測 ΔE≈0.135)、その周囲の
        //                      有彩のにじみ(ドーム階調, ΔE≥0.26)まで保持すると灰色の滲みに
        //                      見えるため(販促時の目視結論「守るのは芯だけ」と同じ)、白距離で
        //                      連続に絞る。
        private const float HlWashResidEps    = 0.035f;
        private const float HlWashTintKeepLo  = 0.012f;
        private const float HlWashTintKeepHi  = 0.024f;
        private const float HlWashKeepWhiteLo = 0.15f;
        private const float HlWashKeepWhiteHi = 0.25f;

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
        // bbMinX..bbMaxY: 候補(strength>=AnchorStrengthMin)が存在しうる矩形。呼び出し側が後段 bbox を
        // 渡す。候補はこの矩形の外に 1 画素も無いので、走査を矩形に限ると 3 パスとも「読む量」だけが
        // 減り、ヒストグラム(整数加算・順序非依存)の中身は全画素走査と完全に一致する = 出力ビット不変。
        // 空矩形(マッチ皆無, bbMaxX<bbMinX)なら候補 0 件で false を返す(従来の「候補不足」と同じ)。
        // statsExclude: 統計から除外する画素(テクスチャ解像度、null=除外なし)。含めるマスクで
        // 強制追加された画素は strength=1 でコア判定を通ってしまうが、手動追加領域(別素材の
        // 可能性がある)が地色統計を汚すとゾーン全体の再着色が遠隔で変わるため、大域統計からは
        // 除外する。除外された画素も再着色自体は受ける(色マッチ画素から推定した素材モデルで
        // 「同素材として」写像される)。3 パスとも同一の判定で除外すること — pass1 で除外した
        // 画素の candL/candC は未初期化(プール再利用のゴミ)であり、pass2/3 が読むと壊れる。
        private static bool TryComputeRecolorAnchor(
            Color32[] px, float[] strength, int w,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
            out float anchorL, out float anchorC,
            bool[] statsExclude = null,
            CancellationToken ct = default)
        {
            anchorL = 0f;
            anchorC = 0f;
            int len = px.Length;

            // コア画素(strength>=AnchorStrengthMin & a>=128)の OkLab (L, C) を pass1 で一度だけ
            // 計算して保存し、pass2/3 はそれを読む。従来は 3 パスとも全画素を走査して
            // 同じ画素の RgbToOklab を再計算していた(4K で計 50M 回の OkLab 変換)。出力は同値。
            // 配列は s_floatPool から借用(len<=2^24 なのでプール内)。
            // 保存は **元画素インデックスのまま**(旧: 先頭詰めの圧縮配列)。pass2/3 は pass1 と同じ
            // コア判定で候補を選び直して読むだけになり、各パスが画素位置だけで完結する=チャンク
            // 並列化できる。集計はヒストグラム(整数加算)なので順序非依存で、逐次版とビット不変。
            float[] candL = s_floatPool.Rent(len);
            float[] candC = s_floatPool.Rent(len);
            try
            {
                // pass 1: (L, C) を保存しつつ飽和度(C/L)ヒストグラム → 中央値から地色下限を決める
                var satrHist = new int[256];
                int candCount = AccumulateHistParallelRows(bbMinY, bbMaxY + 1, satrHist, (y0, y1, hist) =>
                {
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int rowOff = y * w;
                        for (int x = bbMinX; x <= bbMaxX; x++)
                        {
                            int i = rowOff + x;
                            if (strength[i] < AnchorStrengthMin || px[i].a < 128) continue;
                            if (statsExclude != null && statsExclude[i]) continue;
                            RgbToOklab(px[i].r, px[i].g, px[i].b,
                                out float L, out float a, out float b);
                            float C = Mathf.Sqrt(a * a + b * b);
                            candL[i] = L;
                            candC[i] = C;
                            n++;
                            float satr = C / Mathf.Max(L, 1e-4f);
                            int bin = Mathf.Clamp((int)(satr / AnchorSatrHistMax * 255f), 0, 255);
                            hist[bin]++;
                        }
                    }
                    return n;
                }, ct);
                if (candCount < AnchorMinPixels) return false;
                float satrFloor = AnchorBodySatrFrac *
                    HistValueAtPercentile(satrHist, candCount, 0.5f, AnchorSatrHistMax);

                // pass 2: 地色画素(飽和度 ≥ 下限)の L ヒストグラム → 代表 L と L 帯
                var lHist = new int[256];
                int bodyCount = AccumulateHistParallelRows(bbMinY, bbMaxY + 1, lHist, (y0, y1, hist) =>
                {
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int rowOff = y * w;
                        for (int x = bbMinX; x <= bbMaxX; x++)
                        {
                            int i = rowOff + x;
                            if (strength[i] < AnchorStrengthMin || px[i].a < 128) continue;
                            if (statsExclude != null && statsExclude[i]) continue;
                            float L = candL[i];
                            if (candC[i] / Mathf.Max(L, 1e-4f) < satrFloor) continue;
                            hist[Mathf.Clamp((int)(L * 255f), 0, 255)]++;
                            n++;
                        }
                    }
                    return n;
                }, ct);
                if (bodyCount < AnchorMinPixels) return false;
                float l05 = HistValueAtPercentile(lHist, bodyCount, 0.05f, 1f);
                float l95 = HistValueAtPercentile(lHist, bodyCount, 0.95f, 1f);
                if (l95 - l05 < AnchorMinLSpread) return false;  // フラット領域: どこを取っても同じ
                anchorL = HistValueAtPercentile(lHist, bodyCount, AnchorLPct, 1f);
                float bandLo = HistValueAtPercentile(lHist, bodyCount, AnchorLBandLoPct, 1f);
                float bandHi = HistValueAtPercentile(lHist, bodyCount, AnchorLBandHiPct, 1f);

                // pass 3: L 帯内の地色画素の chroma 中央値 → 代表 C
                var cHist = new int[256];
                int bandCount = AccumulateHistParallelRows(bbMinY, bbMaxY + 1, cHist, (y0, y1, hist) =>
                {
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int rowOff = y * w;
                        for (int x = bbMinX; x <= bbMaxX; x++)
                        {
                            int i = rowOff + x;
                            if (strength[i] < AnchorStrengthMin || px[i].a < 128) continue;
                            if (statsExclude != null && statsExclude[i]) continue;
                            float L = candL[i];
                            float c = candC[i];
                            if (c / Mathf.Max(L, 1e-4f) < satrFloor) continue;
                            if (L < bandLo || L > bandHi) continue;
                            hist[Mathf.Clamp((int)(c / AnchorChromaHistMax * 255f), 0, 255)]++;
                            n++;
                        }
                    }
                    return n;
                }, ct);
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
            // L: 2区間線形リマップ (0→0, sL→tL, 1→1)。base を target 明度へ寄せる。単調維持
            //    (リング無し)・ガンマット内(クリップ無し)・白→白/黒→黒。明度を完全保持すると
            //    暗い色→黄色が brown 化するため base は target 明度に合わせる。
            // (a,b)_new = |chroma|·(osat/sC)·(target 色相単位ベクトル) ← 大きさは元 chroma に比例、
            //    向きは target に均一化。旧版の「色相回転保持」は、単色ロゴなどの AA 縁で元テクスチャに
            //    無い色相ノイズ(輪郭だけが別色へ転ぶ)を生んだため、向きを揃えて L を保ったまま色相を
            //    均一化する。
            RgbToOklab(oRb, oGb, oBb, out float oL, out float oa, out float ob);
            float oC = Mathf.Sqrt(oa * oa + ob * ob);
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
                // 無彩パス: 無彩サンプルでは oC/sC が微小彩度ノイズを増幅(脚色)。uniform target 彩度
                // (osat, oC 非依存)へ achromaWeight でフェードし増幅を止める。weight=0 で従来式。
                if (achromaWeight > 1e-4f)
                    mag = mag * (1f - achromaWeight) + osat * achromaWeight;
                // 無彩パスの有彩版: tC > sC のとき output chroma = mag*tC が sC*Factor を超えないよう制限。
                // achromaWeight=1 時は上記で mag=osat 固定済みなのでキャップは no-op。
                // 上限 mag はゾーン定数 okChromaMaxMag に事前算出済み(キャップ非適用時は +∞ で
                // この比較は no-op)。旧版は per-pixel で tC=sqrt(okTa²+okTb²) と maxMag を再計算していた。
                if (mag > okChromaMaxMag) mag = okChromaMaxMag;
                na = mag * okTa;
                nb = mag * okTb;
            }
            // 2区間線形リマップ: [0,sL]→[0,tL], [sL,1]→[tL,topL]。sL→tL を不動点に base を target 明度へ。
            // 上端は白(1)固定でなく topL=min(1,tL*HighlightLMult)。暗いターゲット(tL<0.5)では明部/
            // スペキュラが白へ暴走せず tL×MULT に収まる(暗い色ターゲットの出力をその暗さに収める)。
            // tL≥0.5 では topL=1 = 従来どおり(中〜明ターゲットは完全 no-op)。
            float topL = Mathf.Min(1f, okTL * HighlightLMult);
            float remapL = oL <= okSL
                ? (oL / Mathf.Max(okSL, 1e-4f)) * okTL
                : okTL + (oL - okSL) / Mathf.Max(1f - okSL, 1e-4f) * (topL - okTL);
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
            // valueBlend を下げるほど target のフラットな明度へ寄せる。
            float nL = okTL * (1f - valueBlend) + effRemapL * valueBlend;

            // 暗いターゲットの明部白暴走キャップ（有彩=主経路のみ）: 出力明度を topL=min(1,tL*HighlightLMult)
            // 以下に収める。彩度ゲートは低彩度画素(白いスペキュラ=低chroma高L)の元 L(白)を保持し remap を
            // 迂回するため、remap 上端キャップだけでは白が残る→ここでクランプ。**achroma ブレンドの前**に
            // 適用するのが要点: 黒/白の achroma パスは FormGain で陰影を意図的に拡張するので、ここで
            // キャップすると明るい地色→黒のような無彩変換で form(立体感)が潰れる。achroma 成分は下の
            // ブレンドで(キャップ前の値として)混ぜ、FormGain を温存する。tL≥0.5(中〜明ターゲット)では
            // topL=1 で完全 no-op。
            nL = Mathf.Min(nL, topL);

            // 無彩パスの L。マッチ領域の L レンジ[lo,hi]を target 側ヘッドルームへ順序保存で収める
            // (2区間リマップ・彩度ゲートを迂回)。白い地色→黒のような無彩変換で、彩度ゲートが白を明るく
            // 残して起きる「まだら」と、2区間リマップの明度崩壊を直す。
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
                    float oR = oRb / 255f, oG = oGb / 255f, oB = oBb / 255f;
                    float pR = oR - washR;
                    float pG = oG - washG;
                    float pB = oB - washB;
                    float w = Mathf.Clamp01((pR * dR + pG * dG + pB * dB) / dirSq);

                    // target 軸から外れる residual は意図的に捨てる。
                    float projTR = tR + w * (1f - tR);
                    float projTG = tG + w * (1f - tG);
                    float projTB = tB + w * (1f - tB);

                    // 軸残差フェード (2026-06-04 導入 / 2026-08-15 OkLab 方向分解へ改訂):
                    //   真の鏡面ハイライト = 地色が光で白く飛んだもの = 軸上     → フル白寄せ
                    //   有彩の模様        = 別色・高彩度            = 軸外れ大 → 白寄せ 0
                    // 軸外で proj_t(=軸上の脱彩点)への置換を止めるので、明るい模様まで白化して
                    // 壊れる過剰白化を構造的に排除する。残差を捨てる(P5)のは残差の小さい画素に
                    // 限られるためピンク化抑止特性も維持。
                    //
                    // 旧実装は RGB ユークリッド残差一律で「ずれの向き」を区別しなかったため、
                    // 作画表現として色相を寒色にずらした手描きスペキュラ(淡青の宝石光点など)まで
                    // 模様と誤認し、base 転写(=target 色相へ写る)に落ちて明るいターゲットで
                    // 桃色に濁った(dev_safe/docs/specular-hue-drift-2026-08.md)。残差を OkLab で
                    //   彩度超過 max(0, C_o−C_ax) / 暗化 max(0, L_ax−L_o) / 色相弧 d_tan
                    // に分解して 2 つの判定に使う:
                    //   patternFade: 模様の証拠(彩度超過・暗化)による減衰。有彩の模様・金属光沢は
                    //                従来どおり base 転写のまま守られる。
                    //   tintness   : 色相弧の量。0 = 軸上のドーム階調 → 従来どおり projT へ白寄せ。
                    //                1 = 色相をずらした光点 → projT でなく**元色を保持**する。
                    //                白寄せで直せないのが要点: この光点は軸射影 w が低く(色相ずれの
                    //                直交成分は射影に乗らない)、projT 自体が「白に遠い pale target」
                    //                になるため、フェードを直しても白くはならない(実測 sat 0.255)。
                    //                作画者が光として描いた画素は色ごと残すのが販促時に目視確認
                    //                された正解で、誤判定時も「白塗り潰し」でなく「残しすぎ」に
                    //                倒れる安全側の合成。
                    // 選択側の帯候補判定(HlBandAxisEps)は不変。
                    float axR = washR + w * dR;
                    float axG = washG + w * dG;
                    float axB = washB + w * dB;
                    RgbToOklab(axR, axG, axB, out float axL, out float axA, out float axB2);
                    float dOkA = oa - axA, dOkB = ob - axB2;
                    float axC = Mathf.Sqrt(axA * axA + axB2 * axB2);
                    float dRad = oC - axC;                      // +: 軸予測より高彩度
                    float dTanSq = Mathf.Max(dOkA * dOkA + dOkB * dOkB - dRad * dRad, 0f);
                    float chromaExcess = Mathf.Max(dRad, 0f);
                    float darkening = Mathf.Max(axL - oL, 0f);
                    float patternResid = Mathf.Sqrt(chromaExcess * chromaExcess + darkening * darkening);
                    float patternFade = Mathf.Clamp01(1f - patternResid / HlWashResidEps);
                    float dTan = Mathf.Sqrt(dTanSq);
                    float tintness = Mathf.Clamp01(
                        (dTan - HlWashTintKeepLo) / (HlWashTintKeepHi - HlWashTintKeepLo));
                    // 元色保持は「白に近い芯」だけ。有彩のにじみ(ドーム階調)まで保持すると
                    // 灰色の滲みに見える(定数コメント参照)。白距離ゲートで連続に絞り、
                    // 絞られた分は base 転写(従来の色相ずれ画素の挙動)へ落ちる。
                    float dEwhite = Mathf.Sqrt((1f - oL) * (1f - oL) + oC * oC);
                    float whiteKeep = Mathf.Clamp01(
                        (HlWashKeepWhiteHi - dEwhite) / (HlWashKeepWhiteHi - HlWashKeepWhiteLo));

                    // 境界連続性のため valRise でフェード (oV=washV で valRise=0、hsv_result に戻る)
                    float valRise = Mathf.Clamp01((oV - washV) / Mathf.Max(0.05f, 1f - washV)) * patternFade;
                    float washMix = valRise * (1f - tintness);      // target→白への射影
                    float keepMix = valRise * tintness * whiteKeep; // 色相ずれした光点の芯を保持
                    float baseMix = 1f - washMix - keepMix;
                    result.r = result.r * baseMix + projTR * washMix + oR * keepMix;
                    result.g = result.g * baseMix + projTG * washMix + oG * keepMix;
                    result.b = result.b * baseMix + projTB * washMix + oB * keepMix;
                }
            }

            result.a = alpha;
            return result;
        }
    }
}
