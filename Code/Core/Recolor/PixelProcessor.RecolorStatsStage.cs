// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 再着色の段の 3 つ目「再着色の統計」(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から中身を書き換えずに
        /// 切り出した)。ハイライト合成の実効サンプル・OkLab のゾーン定数・地色アンカー・無彩パスの領域 L を求め、
        /// ゾーン不変の再着色パラメータ(<see cref="ZoneState.Params"/>)と領域 L(<see cref="ZoneState.RegLmid"/>・
        /// 成分ごとの地図)を書く。
        /// 地図はゾーンの作業配列(<see cref="ZoneBuffers"/>)の持ち物。
        /// </summary>
        private static class RecolorStatsStage
        {
            internal static void Run(RunContext run, ZoneState z)
            {
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
                // (明部の地色)へ置換する。スポイトを陰影のどの明るさで取ってもパーツの明部が
                // target 色に一致する。マッチング(strength)・wash・デコンタミはスポイト色の
                // まま＝再着色範囲は不変。フォールバック時(false)は従来挙動。
                float zEffShadowDesat = zone.shadowDesaturation;
                if (zone.autoRecolorAnchor && !zOkGray &&
                    TryComputeRecolorAnchor(originalPixels, strength, w,
                        ppMinX, ppMinY, ppMaxX, ppMaxY, out float anchorL, out float anchorC,
                        zSL, zSa, zSb, sampS > ColorZone.ChromaGateActivateSat,
                        includedPx, cancellationToken, palette))
                {
                    float zSC0 = zSC;
                    zSL = anchorL;
                    zSC = anchorC;
                    // 暗部脱彩の領域相対化(アンカー採用時のみ): 絶対 V 閾値のままだと暗い
                    // パーツは全体が閾値未満になり一律最大50%脱彩される(=入力明度で出力彩度
                    // が変わる)。閾値に地色アンカーの V(明部の代表明度)を乗じ「パーツ内の
                    // 相対的な暗部」だけを脱彩する。アンカー非採用時は従来の絶対閾値で完全互換。
                    if (zEffShadowDesat > 0f)
                    {
                        OkLab.OklabToRgb(zSL,
                            zSa / Mathf.Max(zSC0, 1e-9f) * zSC,
                            zSb / Mathf.Max(zSC0, 1e-9f) * zSC,
                            out float aRr, out float aGg, out float aBb);
                        Color.RGBToHSV(new Color(aRr, aGg, aBb), out _, out _, out float repV);
                        zEffShadowDesat *= repV;
                    }
                }
                _sub.Mark(SpAnchor);
                float zOkMagScale = 0f, zOkGa = 0f, zOkGb = 0f;
                if (zOkGray)
                {
                    zOkGa = zTa * zOsat;
                    zOkGb = zTb * zOsat;
                }
                else
                {
                    // na = |chroma| * (osat/sC) * zTa, nb = 同 zTb。oC=sC(sample) で (zTa,zTb)=target に一致。
                    zOkMagScale = zOsat / zSC;
                }
                // 彩度の写し方(PixelProcessor.Recolor.cs の「彩度の写し方」参照)。塗り先が元より鮮やか(osat·tC > sC)なときだけ、
                // 彩度を陰影・混色で予想できる分と残りに分けて写す。ゾーン不変なのでここで 1 回だけ決める。サンプルが無彩
                // (okGray)・塗り先が無彩(tC≈0)なら全体の比例のまま。
                bool zOkSplitChroma = false;
                float zOkInvTC = 0f;
                if (!zOkGray)
                {
                    float zTC = Mathf.Sqrt(zTa * zTa + zTb * zTb);
                    if (zTC > 1e-4f && zTC * zOsat > zSC)
                    {
                        zOkSplitChroma = true;
                        zOkInvTC = 1f / zTC;
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
                if (zAchromaWeight > AchromaWeightActiveMin)
                {
                    zHasRegL = TryComputeRegionLRange(originalPixels, strength, w,
                        ppMinX, ppMinY, ppMaxX, ppMaxY,
                        out zRegLmid, includedPx, cancellationToken, palette);
                        _sub.Mark(SpRegionLRange);
                    if (zHasRegL)
                    {
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
                    zSL, zTL, zSC, zOkSplitChroma, zOkInvTC, zValueBlend, zEffShadowDesat,
                    sampS, zTR, zTG, zTB, zWR, zWG, zWB, zWV,
                    zApplyWash, zAchromaWeight, zOsat, zHasRegL);
                z.Params = rcParams;
                z.RegLmid = zRegLmid;
                z.RegMidMap = zRegMidMap;
                z.RegUpMap = zRegUpMap;
                run.Tp = _tp;
            }
        }
    }
}
