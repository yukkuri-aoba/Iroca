// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEngine;

namespace Iroca
{
    // ColorZone: 【実験】OKLab ベースのマッチング距離(選択エンジンの隔離実装)。
    // ─────────────────────────────────────────────────────────────────────────
    // 既存の HSV/RGB ハイブリッド距離(ColorZone.Match.cs の MatchOneSample)と並行する
    // 隔離経路。既定は必ず従来 HSV(この経路は DebugCaptureHooks.MatchDistanceOklab が
    // true のときだけ PixelProcessor が呼ぶ)。設計・仮説・評価方針の正は
    // docs/oklab_matching_distance_experiment_plan.md / oklab_matching_implementation_plan.md。
    //
    // 仮説の核心: HSV の Hue は低彩度で特異(彩度 0 で角度が定義不能)なため、グレー/有彩の
    // ハードな分岐と chromaConfidence ブレンド等のパッチが積み重なっている。OKLab の chroma
    // C=sqrt(a²+b²) は彩度が下がると a,b ごと滑らかに 0 へ縮む(特異点なし)ので、単一の連続
    // 距離式に統合できる、というのを実測で確認する。距離式は HSV 版 hsvDist と同型・同スケール
    // (turns [0,0.5] の色相・[0,1] の chroma/L)に構成し、tolerance / softRange / hardRange /
    // CalculateEdgeStrength を無変更で流用する。彩度ガード・ハイライト回復など Hue/Sat 依存の
    // 周辺機能は当面 HSV 版のまま残す(ハイブリッド構成、実験計画書 §5 が許容)。
    public partial class ColorZone
    {
        // sRGB 色域内で到達可能な OKLab chroma の最大値(≈0.323)の逆数。pCn=|chroma|*この係数で
        // 正規化 chroma を [0,1] へ写し、HSV の S [0,1] とスケールを揃える(satDistWeight/satRampScale
        // 等の HSV 較正済み係数をそのまま流用するため)。
        internal const float InvOklabChromaNorm = 3.0960f; // 1 / 0.323

        // atan2 の戻り(ラジアン, [-π,π])を turns([-0.5,0.5])へ写す係数。1/(2π)。
        // HSV の Hue が [0,1) turns なのに合わせ、色相環状距離を同スケール([0,0.5])にする。
        private const float InvTwoPi = 0.15915494f;

        // FF コア判定用の固定半径(OKLab 距離スケール版)。HSV 版 CoreMatchDistance(0.14)と同型
        // ・同スケールに構成したので同値から開始する。採用時の再導出は再較正フェーズ(スコープ外)。
        private const float CoreMatchDistanceOklab = 0.14f;

        /// <summary>
        /// 【実験】OKLab マッチング距離版。<see cref="GetMatchScoresPrecomputedHSV"/> と同型で、
        /// L / a / b が事前計算済みの場合に使う。HSV(pH/pS/pV)も受けるのは温存ヒューリスティック
        /// (彩度ガード・ハイライト回復)用。ColorPick モード専用。キャッシュは呼び出し前に
        /// UpdateCacheIfNeeded() で更新しておくこと。
        /// </summary>
        public void GetMatchScoresPrecomputedOklab(
            float pH, float pS, float pV, float pL, float pA, float pB, Color pixelColor,
            int x, int y, int texWidth, int texHeight,
            out float strength, out float highlightPot, out float matchConf)
        {
            strength = 0f;
            highlightPot = 0f;
            matchConf = 0f;
            if (!enabled) return;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    GetColorMatchScoresOklab(pixelColor, pH, pS, pV, pL, pA, pB,
                        out strength, out highlightPot, out matchConf);
                    break;
                case SelectionMode.Rect:
                    if (IsInRect(x, y, texWidth, texHeight))
                    {
                        strength = 1f;
                        matchConf = 1f; // 矩形選択は確定領域=完全確信
                    }
                    break;
            }
        }

        // マルチサンプルの和集合マッチング(OKLab 版)。全サンプル(主＋追加スポイト)に対して
        // 1 サンプル分のマッチを計算し最大強度を採る。GetColorMatchScores(HSV 版)と同型。
        // ピクセル側の正規化 chroma pCn と色相角 pHueOk は先頭で 1 回だけ計算し全サンプルへ渡す
        // (マルチサンプル時の Sqrt/Atan2 重複を避ける)。
        private void GetColorMatchScoresOklab(
            Color pixelColor, float pH, float pS, float pV, float pL, float pA, float pB,
            out float strength, out float highlightPotential, out float matchConf)
        {
            strength = 0f;
            highlightPotential = 0f;
            matchConf = 0f;

            var caches = _sampleCaches;
            if (caches == null || caches.Length == 0)
            {
                UpdateCacheIfNeeded();
                caches = _sampleCaches;
            }

            float pCn = Mathf.Sqrt(pA * pA + pB * pB) * InvOklabChromaNorm;
            float pHueOk = Mathf.Atan2(pB, pA) * InvTwoPi; // turns [-0.5, 0.5]

            for (int si = 0; si < caches.Length; si++)
            {
                MatchOneSampleOklab(in caches[si], pixelColor, pH, pS, pV, pL, pCn, pHueOk,
                    out float s, out float hPot, out float mc);
                if (s > strength) strength = s;
                if (hPot > highlightPotential) highlightPotential = hPot;
                if (mc > matchConf) matchConf = mc;
            }
        }

        // 1 サンプル分のマッチ強度／ハイライト候補を OKLab 距離で計算する。サンプル依存の派生値は
        // sc から(sL/sCn/sHueOk/satMinOk/satRampOk/chromaConfidenceOk を BuildSampleCache が充填)、
        // ゾーン共通の値(tolerance・各 range・重み・閾値)はインスタンスフィールドから読む。
        private void MatchOneSampleOklab(
            in SampleCache sc, Color pixelColor, float pH, float pS, float pV,
            float pL, float pCn, float pHueOk,
            out float strength, out float highlightPotential, out float matchConf)
        {
            strength = 0f;
            highlightPotential = 0f;
            matchConf = 0f;

            // 彩度ガード(HSV のまま流用): 源色が高彩度なときだけ作動し、白/黒/灰など無彩色寄りの
            // 画素を hard reject する。源色 S が低い(グレー/黒)なら saturationGuardFloor=0 で自動無効。
            if (sc.saturationGuardFloor > 0f && pS < sc.saturationGuardFloor)
                return;

            // 色相(a,b 平面)環状距離 [0,0.5](HSV の hDist と同スケール=turns)。
            float hueDistOk = Mathf.Abs(pHueOk - sc.sHueOk);
            if (hueDistOk > 0.5f) hueDistOk = 1f - hueDistOk;
            // 無彩領域での色相角のバタつきを抑える関連度(既存 hueRelevance と同形。max(pCn,sCn)基準)。
            float hueRelevanceOk = Mathf.Clamp01(Mathf.Max(pCn, sc.sCn) / Mathf.Max(0.01f, chromaThreshold));
            float effHueDist = hueDistOk * hueRelevanceOk;

            // L 距離の chroma-ratio 減衰(HSV の sRatio と同型: 有彩どうしでは明度差=陰影の寄与を抑える)。
            float cRatio = (sc.sCn > 0.01f) ? Mathf.Clamp01(pCn / sc.sCn) : 1f;
            // L 減衰の重み。有彩サンプルでは (1-cRatio) で同 chroma の陰影を許容する(HSV と同型)。
            // ただし **減衰を chromaConfidenceOk でゲートする**: 無彩サンプル(sCn≈0)は cRatio→1 で L 項が
            // 消えるが、無彩素材は L(明度)こそが識別軸なので減衰させてはいけない。ゲート無しの literal な
            // (1-cRatio) だと無彩サンプルで黒〜白の低 chroma 画素を全て拾う致命的な過選択になる(計測で
            // 4096² クリーム→青が 5.6k→715k 画素に膨張=126倍を確認)。chromaConfidenceOk→0 で lWeight→1 と
            // し、実験計画書の意図「低 chroma では |ΔCn|+|ΔL| 距離へ連続退化」(§3.2/実装計画 §核心)を満たす。
            float lWeight = 1f - cRatio * sc.chromaConfidenceOk;

            // 単一連続距離(グレー/有彩のハード分岐を撤廃=仮説の核心)。低 chroma では hueRelevanceOk→0 で
            // 色相項が落ち、lWeight→1 で |ΔCn|+|ΔL| 距離へ連続退化する。HSV hsvDist と同型・同スケールなので
            // tolerance を流用。注: RGB 距離との chromaConfidence Lerp(HSV の :343)は載せない(Hue 特異点パッチ)。
            float distOklab = effHueDist
                + Mathf.Abs(pCn - sc.sCn) * satDistWeight
                + Mathf.Abs(pL - sc.sL) * valueWeight * lWeight;

            // 彩度ゲート satConfidence(C 基準)。有彩サンプルでのみ効かせる(下の gate で chromaConfidenceOk Lerp)。
            float cConf = Mathf.Clamp01((pCn - sc.satMinOk) / sc.satRampOk);

            // シャドウ免除(L 基準・同型翻訳): 同色相で暗い画素(影)の彩度ゲートを緩めて同素材として拾う。
            if (pL < sc.sL * ShadowValueThresholdFrac && effHueDist < ForgivenessHueGate)
            {
                float darkForgiveness = Mathf.Clamp01((sc.sL * ShadowValueThresholdFrac - pL) / (sc.sL * ForgivenessRangeFrac));
                darkForgiveness *= 1f - (effHueDist / ForgivenessHueGate);
                // サンプルが有彩の場合、対象の chroma が低すぎる(グレー/黒に近い)と免除を減衰。
                if (sc.sCn > chromaThreshold)
                {
                    float satFactor = Mathf.Clamp01(pCn / Mathf.Max(0.01f, shadowForgivenessSatMin));
                    darkForgiveness *= satFactor;
                }
                cConf = Mathf.Max(cConf, darkForgiveness);
            }
            // 明部免除(L 基準・シャドウの対称形): 同色相で明るい画素(ハイライト芯)を同素材とみなす。
            // 彩度ゲート免除と距離短縮の両方に効かせる。暗部側の距離短縮は廃止済み=非対称は意図的に踏襲。
            // (HSV の :293-306 と :355-365 が同一式なのでここでは 1 回計算して両方へ適用する。)
            if (!simDisableBrightForgiveness && pL > sc.sL && effHueDist < ForgivenessHueGate)
            {
                float brightThreshold = sc.sL + (1f - sc.sL) * HighlightValueHeadroomFrac;
                float brightForgiveness = Mathf.Clamp01(
                    (pL - brightThreshold) / Mathf.Max(0.01f, (1f - sc.sL) * ForgivenessRangeFrac));
                brightForgiveness *= 1f - (effHueDist / ForgivenessHueGate);
                cConf = Mathf.Max(cConf, brightForgiveness);
                distOklab *= Mathf.Lerp(1f, BrightDistanceForgivenessMin, brightForgiveness);
            }

            // ChromaGate(C 基準・連続化): サンプルが微小な chroma を持つとき、それより著しく中性
            // (無彩)寄りの候補(純白 UV 背景など)を距離加算でソフト排除する。(1 - chromaConfidenceOk) を
            // 乗じ「低 chroma サンプル限定」を連続再現(有彩サンプルでは chromaConfidenceOk≈1 で無効)。
            // 明部限定 gateWeight(HSV の :234)は v1 では省略=採否は再較正フェーズの課題。
            if (sc.sCn > ChromaGateActivateSat)
            {
                float satFloor = sc.sCn * ChromaGateFloorFrac;
                float shortfall = Mathf.Clamp01((satFloor - pCn) / Mathf.Max(satFloor, 1e-4f));
                distOklab += shortfall * ChromaGatePenalty * _cTolerance * (1f - sc.chromaConfidenceOk);
            }

            // AA 縁の忠実復元(連続化): 低 chroma サンプルほど softRange を広げ、外側の混色帯を partial
            // strength にして後段デコンタミ(α 再合成)が滑らかな AA を復元できるようにする。有彩サンプル
            // (chromaConfidenceOk≈1)では aaSoftRange=softRange=従来。edgeSoftness 指定はそれを尊重(floor)。
            float aaSoftRange = Mathf.Max(softRange, _cTolerance * AchromaEdgeSoftness * (1f - sc.chromaConfidenceOk));
            float aaHardRange = _cTolerance - aaSoftRange;

            float gate = Mathf.Lerp(1f, cConf, sc.chromaConfidenceOk);
            strength = CalculateEdgeStrength(distOklab, aaHardRange, aaSoftRange) * gate;
            // FF コア判定用: 色一致確信度。dist(実マッチ距離)を固定半径 CoreMatchDistanceOklab で正規化。
            if (strength > 0f) matchConf = Mathf.Clamp01(1f - distOklab / CoreMatchDistanceOklab);

            // ハイライト回復(HSV のまま流用): highlightPot は HSV ベースの空間パス(PropagateHighlights /
            // GrowHighlightBand)に繋がる別チャンネルのため、ここは HSV の effectiveHDist で既存メソッドを
            // 呼ぶ。注: HSV 主経路はグレー分岐で別式(vDist 直接)を使うが、OKLab 経路は分岐撤廃のため
            // 常に有彩版 CalculateHighlightRecovery を使う(highlightPot は補助チャンネルなので v1 で許容)。
            if (highlightRecovery)
            {
                float hDistHsv = CalculateHueDistance(pH, sc.sH);
                float hueRelevanceHsv = Mathf.Clamp01(Mathf.Max(pS, sc.sS) / Mathf.Max(0.01f, chromaThreshold));
                float effHDistHsv = hDistHsv * hueRelevanceHsv;
                float sRatioHsv = (sc.sS > 0.01f) ? Mathf.Clamp01(pS / sc.sS) : 1f;
                highlightPotential = CalculateHighlightRecovery(in sc, pH, pS, pV, effHDistHsv, sRatioHsv);
            }
        }

        // 代替案(実装せず記録のみ): 色相環状距離×関連度が低 chroma でまだ暴れる場合の差し替え候補として、
        // CIE 型の ΔH_ab = sqrt(max(0, Δa²+Δb² - ΔC²)) がある(角度計算不要・chroma で自動減衰)。
        // hueDistOk×hueRelevanceOk が計測で不安定なら effHueDist をこれに差し替えて再計測する。
    }
}
