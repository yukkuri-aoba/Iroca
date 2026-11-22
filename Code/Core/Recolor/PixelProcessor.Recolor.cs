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

namespace Iroca {
    // PixelProcessor: OkLab 再着色本体(RecolorPixel)・再着色アンカー推定・関連定数。
    internal static unsafe partial class PixelProcessor {
        // L 再マップの彩度ゲート定数。彩度が sample の何割に達したら remap をフル適用するか。
        // これ未満の低彩度画素は元 L を保持し、target が sample より明るい場合の暗部持ち上げ
        // (=ロゴ周辺の白/灰ノイズ)を防ぐ。
        private const float OklabRemapFullChromaFrac = 0.35f;

        // 暗いターゲットの明部白暴走対策: 2区間リマップの上端を 1(白) でなく min(1, tL*この値) に
        // キャップする。tL≥1/MULT(=0.5) では topL=1 で完全 no-op(中〜明ターゲットは従来挙動=byte 不変)、
        // tL<0.5 の暗いターゲットだけ明部(スペキュラ)が白へ暴走せず tL×MULT に収まる。暗い色を
        // ターゲットにしても出力がその暗さに収まるための既定挙動。
        private const float HighlightLMult = 2.0f;

        // 彩度の写し方(2026-10-24): 画素の彩度 oC を、明度の 2 区間リマップと同じ折れ線で予想できる分 Ĉ と残り oC − Ĉ に分ける。
        //   Ĉ = sC·oL/sL(oL ≤ sL: 陰影・黒との混色) / sC·(1 − oL)/(1 − sL)(oL > sL: 白との混色)
        // 乗算の陰影や、白・黒との混色(輪郭の AA・白へ抜けるグラデ・ツヤ)では、彩度は明度と同じ割合で動くので Ĉ に一致する。
        // 残りは素材そのものの彩度の揺れ(布目・染めむら・ノイズ)。
        //   出力の彩度 = osat·tC·Ĉ/sC + κ·(oC − Ĉ)、κ = min(1, osat·tC/sC)
        //   - 塗り先が元より鮮やか(osat·tC > sC、okSplitChroma): 予想できる分は塗り先の彩度に比例させ(面 = アンカーで
        //     塗り先の彩度そのもの、混色・陰影は同じ割合)、残りは 1 倍のまま写す。全体を比例にすると残りの揺れが tC/sC 倍に
        //     増幅されて、地味な素材を鮮やかな色にしたときにざらつく。全体を r^γ で圧縮する案(10-23 の「方法 3」)は揺れを
        //     増幅しないが、低い彩度を持ち上げるので混色を鮮やかにしすぎ、白へ抜けるグラデの選択の境目に硬い継ぎ目が出た。
        //   - 塗り先が元と同じかより地味: κ = osat·tC/sC で、式は全体の比例 osat·tC·oC/sC と同じ(従来どおり)。
        // 以前は「塗った後の彩度 ≤ アンカーの彩度」の上限(ChromaAmpMaxFactor = 1。2026-06-16、Python の代理実装で見えた
        // バンディング対策。実 C# にはそのバンディングは無かった)で増幅を止めていたため、元が地味な素材ほど指定した色に
        // 届かなかった(明るい面で目標の 0.78 倍、茶髪 → 青で 0.33 倍)。上限を外して増えた sRGB の範囲外の色は、
        // OklabToRgbInGamut が明度と色相を保って彩度だけ縮める。
        // 残りの頭打ち(2026-11-09): 残りが素材の彩度 sC の ResidualSoftLimitFrac 倍を超える分は、布目・染めむらでなく範囲に
        // 入った別の鮮やかな所なので、ρ·tanh((oC − Ĉ)/ρ)(ρ = max(ResidualSoftLimitFrac·sC, AchromaSampleC))で頭打ちに
        // する。1 倍で写すと、暗い赤の布の中の鮮やかな赤を紺にしたときなどに、塗り先より鮮やかな青紫になった。ρ の下限は
        // 「鮮やか」と言える最小の彩度(無彩の境)で、白に近い素材の布目の揺れは抑えない。
        private const float ResidualSoftLimitFrac = 2f;

        // 塗り先に合わせる所(2026-11-09): 基準色の自動補正はクリックした場所に依らず、素材の代表の色(アンカー)を塗り先の色に
        // する。ところがクリックした色が、アンカーの素材を黒(暗い側)・白(明るい側)と混ぜた色で説明できないほど地味なときは、
        // 範囲に入った別の色をクリックしている(生成りの部品をクリックしたのに、範囲の大半を占める鮮やかな黄色がアンカーに
        // 選ばれる、など)。そのときは、クリックした画素が塗り先の彩度に届くように彩度の倍率を全体に掛け、上げた分は塗り先の
        // 彩度で頭打ちにする(1.25 倍まで許すと、範囲の大半を占める素材が塗り先を超えた)。
        //   q = クリックの彩度 / 同じ明るさでの混色の予想の彩度(アンカーの彩度 × クリックの明度 / アンカーの明度、明るい側は
        //   × (1 − クリックの明度) / (1 − アンカーの明度))。q ≥ AnchorIntentQHi で従来どおり、q ≤ AnchorIntentQLo でクリックした
        //   画素がちょうど塗り先の彩度。間は倍率を対数で補間する。白へ抜ける布の淡い所のクリックは q 0.85 以上(混色の線の上)、
        //   別の地味な色のクリックは 0.4 前後だった。淡いクリックでも混色の線の上なら上げないので、白へ抜ける布の濃淡は
        //   クリックの場所に依らない。
        private const float AnchorIntentQLo = 0.5f;
        private const float AnchorIntentQHi = 0.75f;

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
        private const float HlWashResidEps = 0.035f;
        private const float HlWashTintKeepLo = 0.012f;
        private const float HlWashTintKeepHi = 0.024f;
        private const float HlWashKeepWhiteLo = 0.15f;
        private const float HlWashKeepWhiteHi = 0.25f;

        // サンプル自動補正(再着色アンカー正規化)の定数。
        // すべて領域統計に対する相対量(特定色/座標/テクスチャ非依存)。
        private const float AnchorStrengthMin = 0.9f;   // コアマッチのみ採用(AA縁・feather裾の混色を除外)
        // スポイトの補正を行う HighlightSampleCorrector の地色の最小画素数もこの定数を読む(基準色の推定はもう使わない)。
        internal const int AnchorMinPixels = 100;
        // 基準色の推定(2026-11-19)。基準 = 「サンプルと同じ素材のうち、平らで広い面ほど重く数えた、明るい側 8 割の位置」を、切り替えを持たない 1 本の式で求める。
        //   「いちばん明るい面」でも「いちばん多い明るさ」でもない理由: 明るい面を基準にすると、明るい面が 1 割ほどしか無い絵で全体が指定色より
        //   暗く沈む。いちばん多い明るさを基準にすると、淡い面と濃い面がはっきり分かれた服で淡い面が白く抜ける。
        //   m = exp(−|(a,b) − qs·L|² / (2σ²))、qs = サンプルの (a/L, b/L)、σ = AnchorMaterialKappa·|qs|·L + AnchorMaterialSigmaFloor。
        //   m は「サンプルと同じ素材の線(原点とサンプルの色を結ぶ線。陰影・白との混色もこの線の近く)からの近さ」で 0〜1 に滑らかに減る重み。
        //   明るさ L の 256 段のヒストグラムへ m を足し(H)、H を ±AnchorDensityHalfBins 段でならした Hs との積 H·Hs を重みとして、
        //   累積が全体の AnchorCumulativeFrac に初めて届く段 pBin を求める。基準の L は pBin の m 重みつき平均、
        //   基準の C は pBin ± AnchorModeHalfBins 段の、重み m² の中央値(下の「基準の代表 C」の説明)。
        // 旧: 地色の山を探す・飽和度の床で絞る・平らかを見る・別の素材が多ければ推定し直す、の切り替えが重なっていた。条件の境で
        //   基準が別の候補へ乗り換わり、許容範囲を少し動かすだけで出力の明るさや彩度が跳んだ。1 本の式なら、素材の重みも累積の位置も
        //   選択が少し変わっただけでは少しずつしか動かない。H·Hs は、まばらな外れ(ツヤの芯・別の色の飾り)より、
        //   同じ明るさに画素が固まった面(平らな地色)に重みを寄せる。
        // AnchorMaterialKappa: σ の比例分。0.25・0.35・0.5 を比べ、0.5 は別の素材の混入の回帰(淡い色と濃い色が並ぶ服)が落ち、
        //   0.25 が指定色とのずれ最小だった(2026-11-18)。
        // AnchorMaterialSigmaFloor: σ の床。暗い所(L が小さく比例分が 0 に近い所)でも 8bit の量子化の揺れを素材の線の近くに入れる。
        //   試作の値のまま(感度は未掃引)。
        // AnchorDensityHalfBins: H·Hs のならしの半幅。試作の値のまま(感度は未掃引)。
        // AnchorCumulativeFrac: 累積の位置。0.70・0.75・0.80・0.85・0.90 を測り、0.75〜0.85 は「目で選んだ基準の幅に入る例の数」
        //   (11 例中 9)も指定色とのずれも変わらない台地で、その中ほど(2026-11-19)。
        private const float AnchorMaterialKappa = 0.25f;
        private const float AnchorMaterialSigmaFloor = 0.015f;
        private const float AnchorSampleLMin = 1e-3f;   // qs = (a/L, b/L) のゼロ割り防止(真っ黒のサンプル)
        private const int AnchorDensityHalfBins = 2;
        private const double AnchorCumulativeFrac = 0.80;
        // 重みと L を整数のヒストグラムに入れるための倍率(整数の加算なので画素を足す順に依らず、2 つの経路と並列の分け方で結果が同じ)。
        private const float AnchorWeightScale = 65536f;
        private const float AnchorLScale = 65536f;

        // 基準の代表 C は、窓の中の C を重み m²(q2 = (q·q) >> 16。q は m の整数表現)で数えた 256 ビンの中央値。
        // なぜ m² か:
        //  (1) 明るさの推定は「どの明るさにどれだけ面があるか」を数えるので m のまま。彩度は「その素材自身の彩度」を知りたいので、
        //      素材の線から離れた画素(弱く通った別の素材)は、面積の割合より軽く数える。
        //  (2) m のままの中央値は、窓の C が二山(無彩に近い暗い画素と地色)に分かれる暗い布(柄入り)で、山の重みの比が半々の近くにあり、
        //      処理する解像度で比が 0.42 / 0.49 と動いて中央値が 0.061 / 0.051 と変わった(プレビューと確定で色がずれる)。
        //      m² では低い山が軽くなり、0.0667 / 0.0667 で動かない。
        //  (3) 平均は二山では安定するが、窓の C がほぼ 0 の白い布で基準を採るようになり(中央値は 0 で false になり採らない)、
        //      元に無い斑点が出たので採らない。
        //  (4) 指数を 3 にしてもほぼ同じ(感度が低い)ので、整数で簡単に書ける 2 にした。
        // q2 = 0 の画素(q < 256)は数えない。整数なので足す順にも並列の分け方にも依らない。
        // 整数の和: q2 ≤ 65536 なので、1 ビンの和は 2.7 億画素(16K)でも 65536 × 2.7e8 ≈ 1.8e13 で long(9.2e18)に収まる。
        // 基準の代表 C を取る明るさの窓(pBin の前後)の片側のビン数(L で 1 ビン = 1/255)。窓の幅 5 ビン ≈ 0.02 は
        // 「同じ明るさとみなす幅」(2026-10-29 に地色の山の窓として入れた値を、代表 C を取る窓に引き継いだ)。
        private const int AnchorModeHalfBins = 2;
        private const float AnchorChromaHistMax = 0.5f;   // chroma ヒストグラムの値域上限(OkLab C は ~0.33 まで)
        private const int AnchorCountBudget = 1 << 21; // 色の表の経路で、色ごとの個数を数える配列の合計(int 数、8MB)

        /// <summary>
        /// サンプル自動補正: OkLab 再着色のアンカー (sL, sC) をマッチ領域の統計から推定する。
        ///
        /// OkLab 再着色は (sL, sC) を不動点とする相対写像のため、スポイトした画素の陰影レベルが
        /// そのまま出力全体の明度・彩度バイアスになる(明部平均 HSV-S で最大 ~0.22 のブレを実測)。
        /// 本関数は「パーツの地色」(サンプルと同じ素材のうち、平らで広い面ほど重く数えた明るい側 8 割の位置)を統計的に推定して返し、
        /// スポイト位置非依存にする。式は AnchorMaterialKappa の上の説明を参照。
        /// マッチング・wash には影響しない(呼び出し側がアンカー 2 値だけを置き換える)。
        ///
        /// 画素ごとの経路と色の表の経路があり、結果はビット単位で同じ(意図した変更では両方を同時に直すこと)。
        /// </summary>
        /// <param name="sampleL">サンプル(スポイト色)の OkLab L。</param>
        /// <param name="sampleA">サンプルの OkLab a。</param>
        /// <param name="sampleB">サンプルの OkLab b。</param>
        /// <returns>false = フォールバック(スポイト色のまま従来挙動)。
        /// 条件: 素材の重みの合計が 0 以下 / 推定 C≈0。</returns>
        private static bool TryComputeRecolorAnchor(
            Color32* px, float* strength, int w,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
            out float anchorL, out float anchorC,
            float sampleL, float sampleA, float sampleB,
            float* claimed = null,
            CancellationToken ct = default,
            ColorPalette palette = null,
            AnchorCoreCounts pre = null) {
            anchorL = 0f;
            anchorC = 0f;
            // 空矩形(選択 0 画素。後段の範囲は (0,0,-1,-1))は候補 0 件。
            if (bbMaxX < bbMinX || bbMaxY < bbMinY) {
                return false;
            }
            // hs[0..255] = H(段ごとの m の和)、hs[256..511] = SL(段ごとの m·L の和)。cw = 代表 C の窓の中の C の m² 重みつきヒストグラム。
            var hs = new long[512];
            var cw = new long[256];
            int centerBin;
            float aL;
            if (palette != null) {
                if (!AccumulateAnchorByColor(px, strength, w, bbMinX, bbMinY, bbMaxX, bbMaxY,
                        sampleL, sampleA, sampleB, claimed, ct, palette, pre,
                        hs, cw, out centerBin, out aL)) {
                    return false;
                }
            } else {
                if (!AccumulateAnchorByPixel(px, strength, w, bbMinX, bbMinY, bbMaxX, bbMaxY,
                        sampleL, sampleA, sampleB, claimed, ct,
                        hs, cw, out centerBin, out aL)) {
                    return false;
                }
            }
            float aC = WeightedMedianChroma(cw);
            if (aC <= 1e-4f) {
                return false;
            }
            anchorL = aL;
            anchorC = aC;
            return true;
        }

        // 基準色の統計に入れるコア画素か。選択の強さが AnchorStrengthMin 以上・不透明であることに加えて、上位のゾーンが
        // 先に塗った分(claimed、占有率)を差し引いた実効の強さ min(strength, 1 − claimed) でも AnchorStrengthMin 以上で
        // あること。基準はこのゾーンが実際に塗る画素から取る。
        // 濃い部品のゾーンが同じ色相の淡い布まで選び、布は上のゾーンが塗っているとき、布の明るさが基準になって部品が
        // 指定色の明るさに届かなかった(2026-11-02)。ゾーンが 1 つ・重ならない画素では claimed は 0 で、従来と同じ。
        // 含めるマスクの画素は、ここを読んだ後で強さ 1 になる(ApplyInclude)ので、含める前の強さで判定される。
        private static bool IsAnchorCore(Color32* px, float* strength, float* claimed, int i)
            => strength[i] >= AnchorStrengthMin && px[i].a >= OpaqueAlphaMin
               && (claimed == null || 1f - claimed[i] >= AnchorStrengthMin);

        // コア画素(地色の推定と同じ条件)の彩度の中央値と分位 q(0..1)を、1 つのヒストグラムから求める。コア画素が無ければ
        // どちらも 0。色の表があれば彩度は表から読む(同じ色に同じ変換なので値は同じ)。
        private static void ComputeCoreChromaMedianAndPercentile(
            Color32* px, float* strength, int w,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
            float* claimed, CancellationToken ct, ColorPalette palette, float q,
            out float median, out float percentile, AnchorCoreCounts pre = null) {
            if (palette != null) {
                palette.EnsureOklab(CoreParallelism.Options(ct));
            }
            var palIdx = palette != null ? palette.Index : null;
            var palC = palette != null ? palette.OkC : null;
            var cHist = new int[256];
            if (palette != null && pre != null && pre.ColorCount == palette.Count) {
                // 数えてあるコア画素の色ごとの個数から作る(画素ごとに 1 ずつ足すのと同じ整数の和。ビンの式も同じ)。
                int total = 0;
                for (int k = 0; k < pre.ColorCount; k++) {
                    int n = pre.Counts[k];
                    if (n == 0) {
                        continue;
                    }
                    cHist[Mathf.Clamp((int)(palC[k] / AnchorChromaHistMax * 255f), 0, 255)] += n;
                    total += n;
                }
                median = total > 0 ? Histogram.ValueAtPercentile(cHist, total, 0.5f, AnchorChromaHistMax) : 0f;
                percentile = total > 0 ? Histogram.ValueAtPercentile(cHist, total, q, AnchorChromaHistMax) : 0f;
                return;
            }
            int count = Histogram.AccumulateParallelRows(bbMinY, bbMaxY + 1, cHist, (y0, y1, hist) => {
                int n = 0;
                for (int y = y0; y < y1; y++) {
                    int rowOff = y * w;
                    for (int x = bbMinX; x <= bbMaxX; x++) {
                        int i = rowOff + x;
                        if (!IsAnchorCore(px, strength, claimed, i)) {
                            continue;
                        }
                        float c;
                        if (palC != null) {
                            c = palC[palIdx[i]];
                        } else {
                            OkLab.RgbToOklab(px[i].r, px[i].g, px[i].b, out _, out float a, out float b);
                            c = Mathf.Sqrt(a * a + b * b);
                        }
                        hist[Mathf.Clamp((int)(c / AnchorChromaHistMax * 255f), 0, 255)]++;
                        n++;
                    }
                }
                return n;
            }, ct);
            median = count > 0 ? Histogram.ValueAtPercentile(cHist, count, 0.5f, AnchorChromaHistMax) : 0f;
            percentile = count > 0 ? Histogram.ValueAtPercentile(cHist, count, q, AnchorChromaHistMax) : 0f;
        }

        /// <summary>
        /// コア画素(<see cref="IsAnchorCore"/>)の色ごとの個数(色の表がある場合)。基準色の推定と、
        /// 彩度の中央値・分位(<see cref="ComputeCoreChromaMedianAndPercentile"/>)は同じコア画素を数えるので、1 回数えて
        /// 使い回す(旧: 同じ範囲を最大 3 回走査していた。性能の候補 K3)。個数は整数で、どこで数えても同じ。
        /// 個数の配列は作業配列のプールから借り、<see cref="Dispose"/> で返す。使う側は書き換えない。
        /// </summary>
        private sealed class AnchorCoreCounts : IDisposable {
            private readonly WorkScratch _scratch = new WorkScratch();
            internal int* Counts;
            internal int Total;
            internal int ColorCount;

            internal static AnchorCoreCounts Count(Color32* px, float* strength, int w,
                int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
                float* claimed, CancellationToken ct, ColorPalette palette) {
                if (palette == null || bbMaxX < bbMinX || bbMaxY < bbMinY) {
                    return null;
                }
                var c = new AnchorCoreCounts { ColorCount = palette.Count };
                try {
                    c.Counts = c._scratch.Int(palette.Count, clear: true);
                    c.Total = CountAnchorCoreInto(c.Counts, px, strength, w, bbMinX, bbMinY, bbMaxX, bbMaxY,
                        claimed, ct, palette.Index, palette.Count);
                    return c;
                } catch {
                    c.Dispose();
                    throw;
                }
            }

            public void Dispose() {
                Counts = null;
                _scratch.Dispose();
            }
        }

        // コア画素を色ごとに数えて cnt(色の数の長さ・0 で埋めてあること)へ足し、コア画素の数を返す。
        // ワーカーごとに色数ぶんの配列へ数えてから足し合わせる。色が多い表(最大 2^20 色)で
        // ワーカー全員が持つと 1 回で 50MB を超えるので、配列の合計が AnchorCountBudget に収まる人数で数える
        // (普通のテクスチャは数千〜数万色で、人数は変わらない)。整数の加算なので人数によらず結果は同じ。
        private static int CountAnchorCoreInto(int* cnt, Color32* px, float* strength, int w,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
            float* claimed, CancellationToken ct, int* pIdx, int colorCount) {
            int workers = Math.Max(1, AnchorCountBudget / Math.Max(1, colorCount));
            return Histogram.AccumulateParallelRowsNative(bbMinY, bbMaxY + 1, cnt, colorCount, (y0, y1, local) => {
                int n = 0;
                for (int y = y0; y < y1; y++) {
                    int rowOff = y * w;
                    for (int x = bbMinX; x <= bbMaxX; x++) {
                        int i = rowOff + x;
                        if (!IsAnchorCore(px, strength, claimed, i)) {
                            continue;
                        }
                        local[pIdx[i]]++;
                        n++;
                    }
                }
                return n;
            }, ct, workers);
        }

        // 素材の重み m(AnchorMaterialKappa の上の説明)を整数(AnchorWeightScale 倍して四捨五入)で返す。
        // 画素ごとの経路と色の表の経路が同じ式を通る(ビット一致のため)。
        private static long AnchorMaterialWeight(float L, float a, float b, in AnchorMaterialLine line) {
            float sig = line.SigmaSlope * L + AnchorMaterialSigmaFloor;
            float da = a - line.Qa * L, db = b - line.Qb * L;
            float wgt = Mathf.Exp(-(da * da + db * db) / (2f * sig * sig));
            return (long)(wgt * AnchorWeightScale + 0.5f);
        }

        // サンプルだけで決まる分(画素ごとに計算し直さない)。qs = (a/L, b/L)、σ の傾き = AnchorMaterialKappa·|qs|。
        // 掛ける順は式のまま (κ·|qs|)·L なので、画素ごとに計算していたときと同じ値になる。
        private readonly struct AnchorMaterialLine {
            public readonly float Qa, Qb, SigmaSlope;

            public AnchorMaterialLine(float sampleL, float sampleA, float sampleB) {
                float sLs = Mathf.Max(sampleL, AnchorSampleLMin);
                Qa = sampleA / sLs;
                Qb = sampleB / sLs;
                SigmaSlope = AnchorMaterialKappa * Mathf.Sqrt(Qa * Qa + Qb * Qb);
            }
        }

        private static long AnchorLQuantized(float L) {
            return (long)(L * AnchorLScale + 0.5f);
        }

        // 彩度の中央値に使う重み m²。q は AnchorMaterialWeight の戻り(m × AnchorWeightScale)。
        private static long AnchorChromaWeight(long q) {
            return (q * q) >> 16;
        }

        // 重みつき chroma ヒストグラム(AnchorChromaHistMax までの 256 ビン)の中央値。重みの合計が 0 なら −1(false 扱いになる)。
        private static float WeightedMedianChroma(long[] cw) {
            long total = 0;
            for (int b = 0; b < 256; b++) {
                total += cw[b];
            }
            if (total <= 0) {
                return -1f;
            }
            long target = Math.Max(1, (total + 1) / 2);
            long cum = 0;
            for (int b = 0; b < 256; b++) {
                cum += cw[b];
                if (cum >= target) {
                    return b / 255f * AnchorChromaHistMax;
                }
            }
            return AnchorChromaHistMax;
        }

        // hs[0..255] = H、hs[256..511] = SL から、代表 C の窓の中心の段と基準 L を決める(2 つの経路で共有)。
        // 重みの合計が 0 以下なら false。
        private static bool PickAnchorBin(long[] hs, out int centerBin, out float anchorL) {
            centerBin = 0;
            anchorL = 0f;
            long total = 0;
            for (int b = 0; b < 256; b++) {
                total += hs[b];
            }
            if (total <= 0) {
                return false;
            }
            var wt = new double[256];
            double wtotal = 0;
            for (int b = 0; b < 256; b++) {
                double v = 0;
                if (hs[b] > 0) {
                    long box = 0;
                    for (int k = Math.Max(0, b - AnchorDensityHalfBins); k <= Math.Min(255, b + AnchorDensityHalfBins); k++) {
                        box += hs[k];
                    }
                    v = (double)hs[b] * (double)box;
                }
                wt[b] = v;
                wtotal += v;
            }
            double cum = 0;
            int pb = 255;
            for (int b = 0; b < 256; b++) {
                cum += wt[b];
                if (wt[b] > 0 && cum >= wtotal * AnchorCumulativeFrac) {
                    pb = b;
                    break;
                }
            }
            centerBin = pb;
            anchorL = Mathf.Clamp01((float)((double)hs[256 + pb] / ((double)hs[pb] * AnchorLScale)));
            return true;
        }

        // 画素ごとの経路。bbMinX..bbMaxY: 候補(strength>=AnchorStrengthMin)が存在しうる矩形。呼び出し側が後段 bbox を渡す。
        // 候補はこの矩形の外に 1 画素も無いので、走査を矩形に限っても結果は全画素走査と同じ(整数加算で順序に依らない)。
        // 含めるマスクで足した画素は、ここを読んだ後で強さ 1 になる(ApplyInclude)ので数えない(手動で足した所は別素材の
        // ことがあり、地色の統計を汚すと、ゾーン全体の再着色が遠隔で変わる。足した画素も再着色自体は受ける)。
        // 1 パス目で H・SL を数えて pBin を決め、2 パス目で pBin ± AnchorModeHalfBins 段の C を数える(窓が先に決まらないため)。
        private static bool AccumulateAnchorByPixel(
            Color32* px, float* strength, int w,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
            float sampleL, float sampleA, float sampleB,
            float* claimed, CancellationToken ct,
            long[] hs, long[] cw, out int centerBin, out float anchorL) {
            var line = new AnchorMaterialLine(sampleL, sampleA, sampleB);
            Histogram.AccumulateParallelRowsLong(bbMinY, bbMaxY + 1, hs, (y0, y1, local) => {
                for (int y = y0; y < y1; y++) {
                    int rowOff = y * w;
                    for (int x = bbMinX; x <= bbMaxX; x++) {
                        int i = rowOff + x;
                        if (!IsAnchorCore(px, strength, claimed, i)) {
                            continue;
                        }
                        OkLab.RgbToOklab(px[i].r, px[i].g, px[i].b, out float L, out float a, out float b);
                        long q = AnchorMaterialWeight(L, a, b, line);
                        if (q == 0) {
                            continue;
                        }
                        int lb = Mathf.Clamp((int)(L * 255f), 0, 255);
                        local[lb] += q;
                        local[256 + lb] += q * AnchorLQuantized(L);
                    }
                }
            }, ct);
            if (!PickAnchorBin(hs, out centerBin, out anchorL)) {
                return false;
            }
            int lo = centerBin - AnchorModeHalfBins, hi = centerBin + AnchorModeHalfBins;
            Histogram.AccumulateParallelRowsLong(bbMinY, bbMaxY + 1, cw, (y0, y1, local) => {
                for (int y = y0; y < y1; y++) {
                    int rowOff = y * w;
                    for (int x = bbMinX; x <= bbMaxX; x++) {
                        int i = rowOff + x;
                        if (!IsAnchorCore(px, strength, claimed, i)) {
                            continue;
                        }
                        OkLab.RgbToOklab(px[i].r, px[i].g, px[i].b, out float L, out float a, out float b);
                        int lBin = Mathf.Clamp((int)(L * 255f), 0, 255);
                        if (lBin < lo || lBin > hi) {
                            continue;
                        }
                        long q = AnchorMaterialWeight(L, a, b, line);
                        if (q == 0) {
                            continue;
                        }
                        float c = Mathf.Sqrt(a * a + b * b);
                        long wq = AnchorChromaWeight(q);
                        if (wq == 0) {
                            continue;
                        }
                        local[Mathf.Clamp((int)(c / AnchorChromaHistMax * 255f), 0, 255)] += wq;
                    }
                }
            }, ct);
            return true;
        }

        // 色の表の経路。(L, a, b) は色の番号だけで決まるので、コア画素を色ごとに 1 回の走査で数え、
        // ヒストグラムは色ごとの値と個数から作る(画素ごとに足すのと同じ整数加算で、ビンの式も同じ = 結果はビット単位で同じ。
        // 出力の一致は scripts/golden/test_palette_parity.py が確かめる)。
        // 色ごとの個数(色の数の長さ)は作業配列のプールから借り、抜けるときに返す(0.4.0 の 3-12)。
        // 呼び出し側が数えてあれば(pre。基準色の推定と彩度の分位で同じ数え上げを使い回す)数え直さない。
        private static bool AccumulateAnchorByColor(
            Color32* px, float* strength, int w,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
            float sampleL, float sampleA, float sampleB,
            float* claimed, CancellationToken ct,
            ColorPalette palette, AnchorCoreCounts pre,
            long[] hs, long[] cw, out int centerBin, out float anchorL) {
            centerBin = 0;
            anchorL = 0f;
            var line = new AnchorMaterialLine(sampleL, sampleA, sampleB);
            palette.EnsureOklab(CoreParallelism.Options(ct));
            int colorCount = palette.Count;
            using var scratch = new WorkScratch();
            int* cnt;
            if (pre != null && pre.ColorCount == colorCount) {
                cnt = pre.Counts;
            } else {
                cnt = scratch.Int(colorCount, clear: true);
                CountAnchorCoreInto(cnt, px, strength, w, bbMinX, bbMinY, bbMaxX, bbMaxY,
                    claimed, ct, palette.Index, colorCount);
            }
            Color32* pCol = palette.Colors;
            for (int k = 0; k < colorCount; k++) {
                int n = cnt[k];
                if (n == 0) {
                    continue;
                }
                Color32 pk = pCol[k];
                OkLab.RgbToOklab(pk.r, pk.g, pk.b, out float L, out float a, out float b);
                long q = AnchorMaterialWeight(L, a, b, line);
                if (q == 0) {
                    continue;
                }
                int lb = Mathf.Clamp((int)(L * 255f), 0, 255);
                hs[lb] += q * n;
                hs[256 + lb] += q * AnchorLQuantized(L) * n;
            }
            if (!PickAnchorBin(hs, out centerBin, out anchorL)) {
                return false;
            }
            for (int k = 0; k < colorCount; k++) {
                int n = cnt[k];
                if (n == 0) {
                    continue;
                }
                Color32 pk = pCol[k];
                OkLab.RgbToOklab(pk.r, pk.g, pk.b, out float L, out float a, out float b);
                int lBin = Mathf.Clamp((int)(L * 255f), 0, 255);
                if (lBin < centerBin - AnchorModeHalfBins || lBin > centerBin + AnchorModeHalfBins) {
                    continue;
                }
                long q = AnchorMaterialWeight(L, a, b, line);
                if (q == 0) {
                    continue;
                }
                float c = Mathf.Sqrt(a * a + b * b);
                long wq = AnchorChromaWeight(q);
                if (wq == 0) {
                    continue;
                }
                cw[Mathf.Clamp((int)(c / AnchorChromaHistMax * 255f), 0, 255)] += wq * n;
            }
            return true;
        }

        // RecolorPixel のゾーン不変パラメータ(再着色ホットループの前に 1 回だけ確定する値)をまとめた
        // readonly struct。in 渡しで per-pixel のコピーを避ける。フィールドは旧 RecolorPixel 引数を
        // そのまま転記(型・順序・意味を保持)。約30引数の緩和=シグネチャ整理のみで数値ロジックは不変。
        private readonly struct RecolorParams {
            public readonly float okMagScale, okTa, okTb;
            public readonly bool okGray;
            public readonly float okGa, okGb;
            public readonly float okSL, okTL, okSC;
            public readonly float okGateC;        // 明度の彩度ゲートの基準(地色の彩度。okSC は塗る範囲の中央値まで上がるが、こちらは上げない。上げると無彩に近い布で画素ごとの彩度の揺れが明度へ写り、織り目が強く出る)
            public readonly bool okSplitChroma;  // 彩度を予想できる分と残りに分けて写すか(osat·tC > sC)
            public readonly float okInvTC;        // 1 / tC(okSplitChroma のときだけ使う)
            public readonly float valueBlend, shadowDesaturation;
            public readonly float sS, tR, tG, tB;
            public readonly float washR, washG, washB, washV;
            public readonly bool applyHighlightWash;
            public readonly float achromaWeight, achromaSampleWeight, achromaLightWeight, osat;
            public readonly bool hasRegL;
            public readonly float okClickGain;   // クリックした色を塗り先に合わせる彩度の倍率(1 で無効。AnchorIntentQLo のコメント)
            public readonly float okResidRho;    // 残りの揺れの頭打ちの幅 ρ(0 で無効。ResidualSoftLimitFrac のコメント)
            public RecolorParams(
                float okMagScale, float okTa, float okTb, bool okGray, float okGa, float okGb,
                float okSL, float okTL, float okSC, float okGateC, bool okSplitChroma, float okInvTC,
                float valueBlend, float shadowDesaturation, float sS, float tR, float tG, float tB,
                float washR, float washG, float washB, float washV, bool applyHighlightWash,
                float achromaWeight, float achromaSampleWeight, float achromaLightWeight, float osat, bool hasRegL,
                float okClickGain, float okResidRho) {
                this.okMagScale = okMagScale; this.okTa = okTa; this.okTb = okTb;
                this.okGray = okGray; this.okGa = okGa; this.okGb = okGb;
                this.okSL = okSL; this.okTL = okTL; this.okSC = okSC; this.okGateC = okGateC;
                this.okSplitChroma = okSplitChroma; this.okInvTC = okInvTC;
                this.valueBlend = valueBlend; this.shadowDesaturation = shadowDesaturation;
                this.sS = sS; this.tR = tR; this.tG = tG; this.tB = tB;
                this.washR = washR; this.washG = washG; this.washB = washB; this.washV = washV;
                this.applyHighlightWash = applyHighlightWash;
                this.achromaWeight = achromaWeight; this.achromaSampleWeight = achromaSampleWeight; this.achromaLightWeight = achromaLightWeight; this.osat = osat;
                this.hasRegL = hasRegL;
                this.okClickGain = okClickGain; this.okResidRho = okResidRho;
            }
        }

        // 正の残りの揺れを ρ·tanh(r/ρ) で頭打ちにする(ResidualSoftLimitFrac のコメント)。ρ ≤ 0 と負の残りは素通し。
        private static float SoftResidual(float r, float rho) {
            if (rho <= 0f || r <= 0f) {
                return r;
            }
            return rho * (float)Math.Tanh(r / rho);
        }

        private static Color32 RecolorPixel(
            byte oRb, byte oGb, byte oBb,
            float oV, float alpha,
            in RecolorParams p,
            float regLmid, float regUp) {   // regUp: 白側の余白(BuildComponentMedianLMap の upMap、地図が無ければ AchromaFormOffset)
            // ゾーン不変パラメータをローカルへ展開する。以降の本体ロジックは従来のまま=出力バイト不変。
            float okMagScale = p.okMagScale, okTa = p.okTa, okTb = p.okTb;
            bool okGray = p.okGray;
            float okGa = p.okGa, okGb = p.okGb;
            float okSL = p.okSL, okTL = p.okTL, okSC = p.okSC;
            bool okSplitChroma = p.okSplitChroma;
            float okInvTC = p.okInvTC;
            float valueBlend = p.valueBlend, shadowDesaturation = p.shadowDesaturation;
            float sS = p.sS, tR = p.tR, tG = p.tG, tB = p.tB;
            float washR = p.washR, washG = p.washG, washB = p.washB, washV = p.washV;
            bool applyHighlightWash = p.applyHighlightWash;
            float achromaWeight = p.achromaWeight, osat = p.osat;
            bool hasRegL = p.hasRegL;
            // L: 2区間線形リマップ (0→0, sL→tL, 1→1)。base を target 明度へ寄せる。単調維持
            //    (リング無し)・ガンマット内(クリップ無し)・白→白/黒→黒。明度を完全保持すると
            //    暗い色→黄色が brown 化するため base は target 明度に合わせる。
            // (a,b)_new = mag·(target の a,b) ← 大きさは元 chroma から(塗り先が元より鮮やかなら、陰影・混色で予想できる分は
            //    比例、残りの揺れは 1 倍。ファイル先頭の「彩度の写し方」参照)、向きは target に均一化。旧版の「色相回転保持」は、単色ロゴなどの AA 縁で元テクスチャに
            //    無い色相ノイズ(輪郭だけが別色へ転ぶ)を生んだため、向きを揃えて L を保ったまま色相を
            //    均一化する。
            OkLab.RgbToOklab(oRb, oGb, oBb, out float oL, out float oa, out float ob);
            float oC = Mathf.Sqrt(oa * oa + ob * ob);
            float na, nb;
            if (okGray) {
                // sample が無彩(グレー): target chroma を一律付与(旧 newS=tS 相当)
                na = okGa;
                nb = okGb;
            } else {
                // 彩度の大きさ(ファイル先頭の「彩度の写し方」参照。mag は塗り先の (a, b) に掛ける倍率 = 出力の彩度 / tC)。
                // 写し方は **サンプル相対項** にだけ掛け、下の無彩ブレンドの uniform 項(osat)には掛けない
                // (uniform 項は oC に依らず、揺れを増幅しないため。以前の上限も同じ理由でここに置いていた)。
                float mag;
                if (!okSplitChroma) {
                    mag = oC * okMagScale;              // |chroma|/sC · osat(全体の比例)
                } else {
                    float cHat = oL <= okSL
                        ? okSC * oL / Mathf.Max(okSL, 1e-4f)
                        : okSC * (1f - oL) / Mathf.Max(1f - okSL, 1e-4f);
                    // osat·tC·Ĉ/sC + (oC − Ĉ) を tC で割った値。osat·tC > sC なので oC ≥ 0 なら負にならないが、丸めに備える。
                    mag = Mathf.Max(0f, cHat * okMagScale + SoftResidual(oC - cHat, p.okResidRho) * okInvTC);
                }
                // 無彩パス: 無彩サンプルでは oC/sC が微小彩度ノイズを増幅(脚色)。uniform target 彩度
                // (osat, oC 非依存)へサンプルの無彩の重みでフェードし増幅を止める。weight=0 で従来式。
                // weight=1 で mag=osat = okGray 経路(na=zTa·osat)と一致する。
                // 塗り先の無彩の重みは彩度に使わない(明度の無彩パスだけ)。一律の彩度は淡い所にも同じ色を乗せるので、
                // 有彩のサンプルで淡い色へ塗ると元の濃淡が潰れる。塗り先が本当に無彩なら tC≈0 で、どちらでも色は付かない。
                float chromaW = p.achromaSampleWeight;
                if (chromaW > AchromaWeightActiveMin) {
                    mag = mag * (1f - chromaW) + osat * chromaW;
                }
                // クリックした色を塗り先に合わせる(AnchorIntentQLo のコメント)。上げた分は塗り先の彩度(osat)で頭打ちにし、
                // 元から塗り先より鮮やかな所は下げない。
                if (p.okClickGain > 1f) {
                    mag = Mathf.Max(mag, Mathf.Min(mag * p.okClickGain, osat));
                }
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
            // =ロゴ周辺の白/灰ノイズになる。chromaFrac=oC/(sC·OklabRemapFullChromaFrac) で彩度が sample の
            // OklabRemapFullChromaFrac 割に達したらフル remap、それ未満は元 L を保持。sample 彩度に対する
            // 相対量なので色非依存。
            // sample 無彩(okGray)時は従来どおり一律 remap。
            float effRemapL = remapL;
            if (!okGray && p.okGateC > 1e-4f) {
                float chromaFrac = Mathf.Clamp01(oC / (p.okGateC * OklabRemapFullChromaFrac));
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

            // 無彩パスの L。成分の地色基準 regLmid(BuildComponentMedianLMap、無ければ領域中央値)を center に
            // 置き、偏差を AchromaFormGain 倍する(2区間リマップ・彩度ゲートを迂回)。白い地色→黒のような
            // 無彩変換で、彩度ゲートが白を明るく残して起きる「まだら」と、2区間リマップの明度崩壊を直す。
            // weight=0(有彩×有彩)では完全 no-op=バイト不変。
            if (achromaWeight > AchromaWeightActiveMin && hasRegL) {
                // 形(立体感)維持: 領域の地色基準を target の明度(center)に置き、基準からの
                // 偏差を AchromaFormGain 倍して陰影を知覚可能な大きさへ拡張する。暗部は 0 へ、明部は
                // center 近辺に収め、白残り(段差)は clamp で防ぐ。単調・領域統計由来で特定座標
                // 非依存。元の微小陰影をそのまま写すと暗部/明部で知覚的に平坦化(ベタ黒/ベタ白)するため、
                // 控えめに増幅する(知覚補償)。
                // center は target 明度そのもの。ただし黒/白に近い target では陰影を描く余白が無くなる
                // (ベタ黒/ベタ白)ので、端から内側へ寄せる。黒側は AchromaFormOffset(0.16)。白側は regUp
                // (成分の明部の陰影の幅から決めた余白、最大 AchromaFormOffset)で、べた塗りの成分は白に乗る。
                // 以前は「tL<0.5 なら 0.16、以上なら 0.84」の二択で、有彩の中間明度 target でも地色が
                // 黒寄り/白寄りの固定位置に置かれていた(白い布→青緑が黒に近い紺、濃い灰→赤が薄い
                // サーモンピンク。tL 0.496 と 0.502 で仕上がりが反転する崖もあった)。target 明度に
                // 連続で従わせることで、無彩の素材でも指定色どおりの明るさに仕上がる。黒 target
                // (tL が 0.16 以下)は従来と同値。
                float center = Mathf.Clamp(okTL, AchromaFormOffset, 1f - regUp);
                float rangeRemap = Mathf.Clamp(center + (oL - regLmid) * AchromaFormGain, 0f, 1f);
                float nLAchroma = okTL * (1f - valueBlend) + rangeRemap * valueBlend;
                // 明度の重みは ComputeAchromaLightWeight(色味のある淡い塗り先では、部品ごとの基準を使わない)。
                float lW = p.achromaLightWeight;
                nL = nL * (1f - lW) + nLAchroma * lW;
            }

            // 暗部脱彩 (旧 shadowDesaturation の OkLab 等価): 暗い画素の chroma を最大 50% 抑制。
            if (shadowDesaturation > 0f && oV < shadowDesaturation) {
                float shadowIntensity = Mathf.Clamp01((shadowDesaturation - oV) / shadowDesaturation);
                float fac = 1f - 0.5f * shadowIntensity;
                na *= fac;
                nb *= fac;
            }

            // sRGB の範囲を超えた色は、明度と色相を保ったまま彩度だけ縮める(OklabToRgbInGamut のコメント参照)。
            OkLab.OklabToRgbInGamut(nL, na, nb, out float outR, out float outG, out float outB);
            Color result = new Color(Mathf.Clamp01(outR), Mathf.Clamp01(outG), Mathf.Clamp01(outB), 1f);

            // ハイライト合成 (P5 軸射影版・residual なし / 2026-05-26):
            // ピクセルを「sample → (1,1,1) 白直線」に射影して進行度 w を取り、
            // 同じ w を使って target 軸上の対応点を求める。residual (軸からのずれ) は
            // 捨てる — これにより青の色相歪みが target 側 (赤) にコピーされてピンク化する
            // P5 純正版の副作用を構造的に除去する。
            //
            //   proj_t = target + w * (white - target)
            //   result = Lerp(result(OkLab 再着色), proj_t, valRise)
            //
            // 直感: pixel が sample から白に向けて 0.99 進んでいるなら (ハイライト中心)、
            //       target からも白に向けて 0.99 進んだ点が出力色。中心は完全な白では
            //       なく「target に向けて 1% 染まった白」(= わずかに赤い白)。
            //       周辺 (w=0.5) は target と white の中間 (例: 赤と白で薄い赤)。
            //
            // 境界 (oV ≤ sV) は valRise=0 で OkLab 再着色の結果に切り戻すため不連続なし。
            // wash 用サンプル(washR/G/B/V): 既定は match と同じ sample。俯瞰スポイト補正では
            // パーツ地色(同色相・低V)が渡され、ハイライト合成 (oV>washV) がドーム全体に効く。
            // match/base は sample のままなので再着色範囲は不変(新規 FP なし)。
            //
            // applyHighlightWash ゲート (2026-06-04): この白寄せ射影は既定 OFF のオプトイン。
            // OFF のときは OkLab 再着色のみで明部の明度・彩度構造を温存する。
            if (applyHighlightWash && sS > 0.01f && oV > washV) {
                float dR = 1f - washR;
                float dG = 1f - washG;
                float dB = 1f - washB;
                float dirSq = dR * dR + dG * dG + dB * dB;
                if (dirSq > 1e-6f) {
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
                    OkLab.RgbToOklab(axR, axG, axB, out float axL, out float axA, out float axB2);
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

                    // 境界連続性のため valRise でフェード (oV=washV で valRise=0、OkLab 再着色の結果に戻る)
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

        // ───────── 無彩パスの再着色メモ ─────────
        // 成分別 L マップ(BuildComponentMedianLMap)を使うゾーンでは、再着色色が「色」と「その画素の
        // 成分の地色基準 L」の組で決まるので、色ごとの表を 1 枚では作れない。代わりに、スレッドごとの
        // 小さな直接写像メモで、同じ(色番号, 地色基準 L)の結果を使い回す。テクスチャは成分ごとに
        // 色の種類が少ないので、ほとんどの画素はメモに当たる。
        // 鍵には呼び出し(ゾーン)ごとの世代番号を含める。スレッドプールのスレッドは別の呼び出しの
        // 行も処理するので、世代が違う項目は外れとして扱う(色番号は呼び出しごとに振り直されるため)。
        // 当たった値は同じ入力で RecolorPixel を呼んだ結果そのものなので、出力はビット単位で同じ。
        private const int RecolorMemoSize = 8192;   // 2 のべき
        // スレッドごとのメモ。[ThreadStatic] の読み出しは遅いので、呼び出し側が行の初めに 1 回だけ RecolorMemoOfThread で
        // 取り出して、画素ごとには渡すだけにする。
        private sealed class RecolorMemo {
            internal readonly int[] Epoch = new int[RecolorMemoSize];
            internal readonly int[] Color = new int[RecolorMemoSize];
            internal readonly float[] Mid = new float[RecolorMemoSize];
            internal readonly float[] Up = new float[RecolorMemoSize];
            internal readonly Color32[] Out = new Color32[RecolorMemoSize];
        }
        [ThreadStatic] private static RecolorMemo t_recolorMemo;
        private static int s_recolorMemoEpoch;

        private static RecolorMemo RecolorMemoOfThread() => t_recolorMemo ?? (t_recolorMemo = new RecolorMemo());

        private static int NextRecolorMemoEpoch() {
            int e = Interlocked.Increment(ref s_recolorMemoEpoch);
            // 0 は「メモを使わない」の印なので飛ばす(一周するのは 2^32 回後)。
            return e != 0 ? e : Interlocked.Increment(ref s_recolorMemoEpoch);
        }

        private static Color32 RecolorPixelMemo(RecolorMemo m, int epoch, int colorId, float regLmid, float regUp,
            Color32 op, float oV, float alpha, in RecolorParams p) {
            int slot = (int)(((uint)colorId * 2654435761u) ^ (uint)(int)(regLmid * 1048576f) ^ ((uint)(int)(regUp * 1048576f) * 40503u))
                       & (RecolorMemoSize - 1);
            if (m.Epoch[slot] == epoch && m.Color[slot] == colorId && m.Mid[slot] == regLmid && m.Up[slot] == regUp) {
                return m.Out[slot];
            }
            Color32 r = RecolorPixel(op.r, op.g, op.b, oV, alpha, in p, regLmid, regUp);
            m.Epoch[slot] = epoch;
            m.Color[slot] = colorId;
            m.Mid[slot] = regLmid;
            m.Up[slot] = regUp;
            m.Out[slot] = r;
            return r;
        }
    }
}
