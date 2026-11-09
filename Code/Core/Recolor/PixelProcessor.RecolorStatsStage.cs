// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using UnityEngine;

namespace Iroca {
    internal static unsafe partial class PixelProcessor {
        /// <summary>
        /// 再着色の段の 3 つ目「再着色の統計」(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から中身を書き換えずに
        /// 切り出した)。ハイライト合成の実効サンプル・OkLab のゾーン定数・地色アンカー・無彩パスの領域 L を求め、
        /// ゾーン不変の再着色パラメータ(<see cref="ZoneState.Params"/>)と領域 L(<see cref="ZoneState.RegLmid"/>・
        /// 成分ごとの地図)を書く。
        /// 地図はゾーンの作業配列(<see cref="ZoneBuffers"/>)の持ち物。
        /// </summary>
        private static class RecolorStatsStage {
            internal static void Run(RunContext run, ZoneState z) {
                // ProcessPixelsArrayCore と同じ名前のローカルへ展開する(切り出した本体をそのまま読めるように)。
                int w = run.W, h = run.H;
                Color32* originalPixels = run.Original;
                var cancellationToken = run.Ct;
                var palette = run.Palette;
                var hsv = run.Hsv;
                var _phaseTicks = run.PhaseTicks;
                var _sub = run.Sub;
                long _tp = run.Tp;
                var zone = z.Zone;
                var zb = z.Buffers;
                float sampS = z.SampS;
                bool* includedPx = z.IncludedPx;
                float* strength = z.Strength;
                int ppMinX = z.Box.MinX, ppMinY = z.Box.MinY, ppMaxX = z.Box.MaxX, ppMaxY = z.Box.MaxY;
                float zAchromaWeight = z.AchromaWeight;

                // 4. 強度でブレンドした再色付けを適用
                // (サンプルの S/V は sampS/sampV としてゾーン先頭で算出済み)
                // ハイライト白方向射影(wash)・OkLab リカラーに必要な sample / target RGB を事前取得
                float zSR = zone.sampleColor.r;
                float zSG = zone.sampleColor.g;
                float zSB = zone.sampleColor.b;
                float zTR = zone.targetColor.r;
                float zTG = zone.targetColor.g;
                float zTB = zone.targetColor.b;
                float zValueBlend = zone.valueBlend;
                float zOutputSat = zone.outputSaturation;
                bool zApplyWash = zone.applyHighlightWash;

                // 俯瞰スポイト補正: ハイライト合成(wash)に使う実効サンプル。テクスチャの地色
                // (同色相・低V)を自動導出し、明るい所をスポイトしても wash がドーム全体に効く
                // ようにする。autoHighlightSample=false / 低彩度 / 地色不足のときは sample のまま。
                float zWR, zWG, zWB, zWV;
                Color zWash = HighlightSampleCorrector.ComputeWashSample(
                    originalPixels, hsv, w, h, zone);
                zWR = zWash.r; zWG = zWash.g; zWB = zWash.b;
                Color.RGBToHSV(zWash, out _, out _, out zWV);

                _sub.Mark(SpWashSample);
                // OkLab 明度保持リカラーのゾーン定数を事前計算 (per-pixel コスト削減)。
                // sample/target を OkLab に変換。彩度(a,b)は「大きさを |chroma|/sC で正規化し、
                // 向きは target 色相(zTa,zTb)に均一化」する。旧版は source の色相を回転保持していたが、
                // 単色ロゴなどでは AA 縁の混色が元と違う色相へ転写され、輪郭だけが別色に転ぶ色相ノイズを
                // 生んだ。向きを target に揃えることで L(リング除去)を保ったまま色相を均一化する。outputSaturation は
                // 大きさスケールに畳み込む。sample が無彩(zSC≈0)なら target chroma を一律付与する。
                OkLab.RgbToOklab(zSR, zSG, zSB, out float zSL, out float zSa, out float zSb);
                OkLab.RgbToOklab(zTR, zTG, zTB, out float zTL, out float zTa, out float zTb);
                float zSC = Mathf.Sqrt(zSa * zSa + zSb * zSb);
                float zOsat = zOutputSat < 0.999f ? zOutputSat : 1f;
                bool zOkGray = zSC <= 1e-4f;
                // サンプル自動補正: アンカー (zSL, zSC) をスポイト画素からマッチ領域の代表色
                // (地色。明部より暗い所に地色の山があればその山、無ければ明部)へ置換する。スポイトを陰影のどの明るさで取ってもパーツの地色が
                // target 色に一致する。マッチング(strength)・wash・デコンタミはスポイト色の
                // まま＝再着色範囲は不変。フォールバック時(false)は従来挙動。
                float zEffShadowDesat = zone.shadowDesaturation;
                float zGateC = zSC;  // 明度の彩度ゲートの基準(倍率の基準を上げても動かさない。下の RecolorParams の okGateC)
                bool zAnchored = false;
                float zClickL = zSL;    // クリックした画素の明度(下の「塗り先に合わせる所」)
                float zSC0Click = zSC;  // クリックした画素の彩度(下の zChromaSampleW と「塗り先に合わせる所」)
                float zDeepC = 0f;      // 素材の濃い側の彩度(下の zChromaSampleW)
                if (zone.autoRecolorAnchor && !zOkGray &&
                    TryComputeRecolorAnchor(originalPixels, strength, w,
                        ppMinX, ppMinY, ppMaxX, ppMaxY, out float anchorL, out float anchorC,
                        zSL, zSa, zSb, sampS > ColorZone.ChromaGateActivateSat,
                        includedPx, run.Buf.Claimed, cancellationToken, palette)) {
                    float zSC0 = zSC;
                    zAnchored = true;
                    zDeepC = ComputeCoreChromaPercentile(originalPixels, strength, w,
                        ppMinX, ppMinY, ppMaxX, ppMaxY, includedPx, run.Buf.Claimed, cancellationToken, palette,
                        AchromaDeepChromaPercentile);
                    zSL = anchorL;
                    zGateC = anchorC;
                    // 彩度の倍率の基準は、塗る範囲の彩度の中央値より下げない。白へ抜ける布で少数の淡い面が基準に選ばれると、
                    // その彩度を塗り先まで上げる倍率が布全体に掛かり、元の淡い所まで色が乗って濃淡の比が崩れる。淡い面が
                    // 大半を占める素材(明るい髪など)では中央値が基準以下なので、これまでどおり面が塗り先の彩度に届く。
                    // クリックの位置では決めない(濃い筋をクリックした髪が地味になり、結果がクリックに左右される)。
                    zSC = Mathf.Max(anchorC, ComputeCoreChromaMedian(originalPixels, strength, w,
                        ppMinX, ppMinY, ppMaxX, ppMaxY, includedPx, run.Buf.Claimed, cancellationToken, palette));
                    // 暗部脱彩の領域相対化(アンカー採用時のみ): 絶対 V 閾値のままだと暗い
                    // パーツは全体が閾値未満になり一律最大50%脱彩される(=入力明度で出力彩度
                    // が変わる)。閾値に地色アンカーの V(地色の代表明度)を乗じ「パーツ内の
                    // 相対的な暗部」だけを脱彩する。アンカー非採用時は従来の絶対閾値で完全互換。
                    if (zEffShadowDesat > 0f) {
                        OkLab.OklabToRgb(zSL,
                            zSa / Mathf.Max(zSC0, 1e-9f) * zGateC,
                            zSb / Mathf.Max(zSC0, 1e-9f) * zGateC,
                            out float aRr, out float aGg, out float aBb);
                        Color.RGBToHSV(new Color(aRr, aGg, aBb), out _, out _, out float repV);
                        zEffShadowDesat *= repV;
                    }
                }
                _sub.Mark(SpAnchor);
                // 彩度を塗り先へ一律にする重み。この重みは、彩度の倍率の分母が小さいと揺れが増幅される(oC/sC)のを止めるための
                // もの。基準色の自動補正を採ったら、クリックした画素の彩度と素材の代表の彩度(分母 zSC)の大きい方で決め、さらに
                // 素材の濃い側の彩度 zDeepC で上限を掛ける(AchromaDeepChromaPercentile のコメント)。白へ抜ける色の布は淡い所を
                // クリックしても濃い側に色があるので重みが 0 になり、濃淡がクリックの場所に依らない(旧: クリックした画素の彩度
                // だけで決め、淡い所をクリックすると濃淡が一律に寄った)。濃い側まで淡い白っぽい布と、濃い側が別の色の所
                // (AchromaDeepSameMaterialRatio)の布は上限を掛けない。
                // 無彩パスの明度の重みのサンプル側は、クリックした画素の彩度のまま(下げると 2 区間の明度リマップが効き、
                // 白っぽい布の明部の布目が広がって元に無い線が増えた)。採らなければ従来どおり。
                // クリックが代表の色より暗く、しかも彩度も低い(影の側)ときも従来どおり。白へ抜ける布では暗い所ほど彩度が
                // 高いので、この側のクリックは起きない。灰色の部品をクリックして範囲に明るいクリーム色が入ると、灰色が
                // クリーム色の影の線に乗り、代表の彩度で重みが下がって、クリックした灰色が塗り先に届かなくなった。
                bool zShadowClick = zClickL < zSL && zSC0Click < zGateC;
                float zChromaSampleW = ComputeAchromaSampleWeight(zone.sampleColor);
                if (zAnchored && !zShadowClick) {
                    float deepCap = Mathf.Clamp01((AchromaSampleC - zDeepC) / (AchromaSampleC * (1f - AchromaDeepChromaFloorFrac)));
                    // 濃い側が代表の彩度の AchromaDeepSameMaterialRatio 倍を超えるなら、濃い側は同じ素材の濃い所でなく範囲に
                    // 入った別の色の所(白い布の色の飾りなど)。上限を弱め、その倍の比で外す(外さないと白い布の重みが 0 になり、
                    // 小さな揺れが増幅されて元に無い線が増えた)。
                    float otherMaterial = Mathf.Clamp01(
                        (zDeepC / Mathf.Max(zSC, 1e-4f) - AchromaDeepSameMaterialRatio) / AchromaDeepSameMaterialRatio);
                    deepCap = deepCap + (1f - deepCap) * otherMaterial;
                    zChromaSampleW = Mathf.Min(ComputeAchromaSampleWeight(Mathf.Max(zSC0Click, zSC)), deepCap);
                }
                float zOkMagScale = 0f, zOkGa = 0f, zOkGb = 0f;
                if (zOkGray) {
                    zOkGa = zTa * zOsat;
                    zOkGb = zTb * zOsat;
                } else {
                    // na = |chroma| * (osat/sC) * zTa, nb = 同 zTb。oC=sC(sample) で (zTa,zTb)=target に一致。
                    zOkMagScale = zOsat / zSC;
                }
                // 彩度の写し方(PixelProcessor.Recolor.cs の「彩度の写し方」参照)。塗り先が元より鮮やか(osat·tC > sC)なときだけ、
                // 彩度を陰影・混色で予想できる分と残りに分けて写す。ゾーン不変なのでここで 1 回だけ決める。サンプルが無彩
                // (okGray)・塗り先が無彩(tC≈0)なら全体の比例のまま。
                bool zOkSplitChroma = false;
                float zOkInvTC = 0f;
                if (!zOkGray) {
                    float zTC = Mathf.Sqrt(zTa * zTa + zTb * zTb);
                    if (zTC > 1e-4f && zTC * zOsat > zSC) {
                        zOkSplitChroma = true;
                        zOkInvTC = 1f / zTC;
                    }
                }

                // 残りの揺れの頭打ち(ResidualSoftLimitFrac)と、クリックした色を塗り先に合わせる倍率(AnchorIntentQLo、
                // PixelProcessor.Recolor.cs のコメント参照)。どちらも基準色の自動補正を採ったときだけ(採らなければクリックした
                // 色がそのまま倍率の基準なので要らない)。
                float zResidRho = 0f;
                float zClickGain = 1f;
                if (zAnchored && !zOkGray) {
                    if (zOkSplitChroma) {
                        // 幅は無彩の境 AchromaSampleC より狭くしない(白に近い素材では素材の彩度の 2 倍が布目の揺れより
                        // 小さく、揺れまで抑えて塗り先の色そのものの画素が増え、プレビューと確定の差も出た)。
                        zResidRho = Mathf.Max(ResidualSoftLimitFrac * zSC, AchromaSampleC);
                    }
                    // クリックした色の彩度を、アンカーの素材をクリックの明るさまで黒(暗い側)・白(明るい側)と混ぜたときの
                    // 彩度と比べる。
                    float predC = zClickL <= zSL
                        ? zGateC * zClickL / Mathf.Max(zSL, 1e-4f)
                        : zGateC * (1f - zClickL) / Mathf.Max(1f - zSL, 1e-4f);
                    float q = zSC0Click / Mathf.Max(predC, 1e-4f);
                    float intent = Mathf.Clamp01((AnchorIntentQHi - q) / (AnchorIntentQHi - AnchorIntentQLo));
                    if (intent > 0f) {
                        // クリックした画素の写し先の彩度(RecolorPixel の mag と同じ式。倍率を掛ける前)
                        float clickMag;
                        if (zOkSplitChroma) {
                            float cHatC = zClickL <= zSL
                                ? zSC * zClickL / Mathf.Max(zSL, 1e-4f)
                                : zSC * (1f - zClickL) / Mathf.Max(1f - zSL, 1e-4f);
                            clickMag = Mathf.Max(0f, cHatC * zOkMagScale + SoftResidual(zSC0Click - cHatC, zResidRho) * zOkInvTC);
                        } else {
                            clickMag = zSC0Click * zOkMagScale;
                        }
                        if (zChromaSampleW > AchromaWeightActiveMin) {
                            clickMag = clickMag * (1f - zChromaSampleW) + zOsat * zChromaSampleW;
                        }
                        float xClick = clickMag / Mathf.Max(zOsat, 1e-4f);
                        if (xClick > 1e-4f && xClick < 1f) {
                            zClickGain = Mathf.Pow(1f / xClick, intent);
                        }
                    }
                }

                // 無彩パスの領域 L 中央値を事前計算(zAchromaWeight は上で算出済み)。
                float zRegLmid = 0.5f;
                bool zHasRegL = false;
                // 形維持リマップの center 基準(中央値)を連結成分ごとに局所化する per-pixel マップ。
                // ゆるいマスクで明るい背景を巻き込んでも、各成分が自分の地色基準で再着色されるので、
                // 背景よりわずかに暗いだけの明るい対象がベタ黒へ潰れない。null のときは
                // zRegLmid(全体中央値)へフォールバック。
                float* zRegMidMap = null;
                float* zRegUpMap = null;
                if (zAchromaWeight > AchromaWeightActiveMin) {
                    zHasRegL = TryComputeRegionLRange(originalPixels, strength, w,
                        ppMinX, ppMinY, ppMaxX, ppMaxY,
                        out zRegLmid, includedPx, cancellationToken, palette);
                    _sub.Mark(SpRegionLRange);
                    if (zHasRegL) {
                        // 地図はプールから借りる(ゾーンの終わりに返す)。
                        zRegMidMap = BuildComponentMedianLMap(originalPixels, strength, w, h, ColorZone.MatchStrengthFloor, out zRegUpMap, cancellationToken,
                            palette);
                        zb.RegMidMap = zRegMidMap; zb.RegUpMap = zRegUpMap;
                    }
                }

                _sub.Mark(SpMedianLMap);
                _phaseTicks[PhRegionStats] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                // ゾーン不変の再着色パラメータをループ前に 1 回だけ構築(in 渡しで per-pixel コピー回避)。
                var rcParams = new RecolorParams(
                    zOkMagScale, zTa, zTb, zOkGray, zOkGa, zOkGb,
                    zSL, zTL, zSC, zGateC, zOkSplitChroma, zOkInvTC, zValueBlend, zEffShadowDesat,
                    sampS, zTR, zTG, zTB, zWR, zWG, zWB, zWV,
                    zApplyWash, zAchromaWeight, zChromaSampleW,
                    ComputeAchromaLightWeight(ComputeAchromaSampleWeight(zone.sampleColor), zone.targetColor), zOsat, zHasRegL,
                    zClickGain, zResidRho);
                z.Params = rcParams;
                z.RegLmid = zRegLmid;
                z.RegMidMap = zRegMidMap;
                z.RegUpMap = zRegUpMap;
                run.Tp = _tp;
            }
        }
    }
}
