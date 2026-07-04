// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    // ColorZone: マッチングエンジン(サンプル派生キャッシュとピクセル一致スコア計算)。
    public partial class ColorZone
    {
        /// <summary>
        /// セッション開始時などに明示的にキャッシュを更新する場合に呼び出します。
        /// 呼ばれない場合は各ピクセルの評価時に暗黙的に更新されます。
        /// </summary>
        public void UpdateCacheIfNeeded()
        {
            if (_cacheInitiated &&
                _cSampleColor == sampleColor &&
                _cTolerance == tolerance &&
                _cSatStrictness == saturationStrictness &&
                _cSatRampScale == satRampScale &&
                _cEdgeSoftness == edgeSoftness &&
                _cSaturationGuard == saturationGuard &&
                !ExtraSamplesChanged())
            {
                return;
            }

            _cSampleColor = sampleColor;
            _cTolerance = tolerance;
            _cSatStrictness = saturationStrictness;
            _cSatRampScale = satRampScale;
            _cEdgeSoftness = edgeSoftness;
            _cSaturationGuard = saturationGuard;
            _cacheInitiated = true;

            // サンプルごとの派生値を構築（主サンプル + 追加スポイト）。
            int extraN = extraSamples?.Count ?? 0;
            _sampleCaches = new SampleCache[1 + extraN];
            _sampleCaches[0] = BuildSampleCache(sampleColor);
            for (int i = 0; i < extraN; i++)
                _sampleCaches[i + 1] = BuildSampleCache(extraSamples[i]);
            // 変更検知用にスナップショットを取る。
            if (_cExtraSamples == null || _cExtraSamples.Length != extraN)
                _cExtraSamples = new Color[extraN];
            for (int i = 0; i < extraN; i++) _cExtraSamples[i] = extraSamples[i];

            softRange = tolerance * edgeSoftness;
            hardRange = tolerance - softRange;

            hlHueCap = Mathf.Max(0.05f, tolerance * 0.3f);
            hlSoftRange = tolerance * edgeSoftness;
            hlHardRange = tolerance - hlSoftRange;
        }

        // extraSamples の内容が前回キャッシュ時と変わったか（個数・各色）。
        private bool ExtraSamplesChanged()
        {
            int n = extraSamples?.Count ?? 0;
            int cn = _cExtraSamples?.Length ?? 0;
            if (n != cn) return true;
            for (int i = 0; i < n; i++)
                if (_cExtraSamples[i] != extraSamples[i]) return true;
            return false;
        }

        // 1 サンプル分の派生キャッシュを計算する。ゾーン共通の倍率
        // （saturationStrictness/satRampScale/chromaThreshold/saturationGuard）を使うので、
        // 主サンプルに対しては従来の単一サンプル計算と完全に一致する（＝後方互換）。
        private SampleCache BuildSampleCache(Color c)
        {
            SampleCache sc;
            sc.color = c;
            Color.RGBToHSV(c, out sc.sH, out sc.sS, out sc.sV);

            // 彩度ガード床: 源色が高彩度なときだけ正値になる。
            // 既定 saturationGuard=0 では常に 0（=機能無効）で従来動作と完全互換。
            sc.saturationGuardFloor = (saturationGuard > 0f && sc.sS >= SaturationGuardActiveSourceSat)
                ? sc.sS * SaturationGuardFractionScale * saturationGuard
                : 0f;

            sc.satMin = Mathf.Max(0.02f, sc.sS * saturationStrictness);
            sc.satRamp = Mathf.Max(0.08f, sc.sS * satRampScale);

            float currentChromaHi = chromaThreshold + 0.10f;
            float baseChromaConf = Mathf.Clamp01((sc.sS - chromaThreshold) / ((currentChromaHi) - chromaThreshold));
            // 暗すぎる色（黒）は彩度データが高くても色相（Hue）の計算がノイズで暴れるため信用しない
            float valueConf = Mathf.Clamp01((sc.sV - 0.05f) / 0.15f); // Vが0.05(非常に暗い)〜0.20の範囲で減衰
            sc.chromaConfidence = Mathf.Min(baseChromaConf, valueConf);
            return sc;
        }

        public float GetMatchStrength(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            GetMatchScores(pixelColor, x, y, texWidth, texHeight, out float strength, out float highlightPot);
            return Mathf.Max(strength, highlightPot);
        }

        public void GetMatchScores(Color pixelColor, int x, int y, int texWidth, int texHeight, out float strength, out float highlightPot)
        {
            strength = 0f;
            highlightPot = 0f;
            if (!enabled) return;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    UpdateCacheIfNeeded();
                    Color.RGBToHSV(pixelColor, out float pH, out float pS, out float pV);
                    GetColorMatchScores(pixelColor, pH, pS, pV, out strength, out highlightPot, out _);
                    break;
                case SelectionMode.Rect:
                    if (IsInRect(x, y, texWidth, texHeight))
                        strength = 1f;
                    break;
            }
        }

        /// <summary>
        /// HSV が事前計算済みの場合に使うバリアント。ColorPick モード専用。
        /// キャッシュは呼び出し前に UpdateCacheIfNeeded() で更新しておくこと。
        /// </summary>
        public void GetMatchScoresPrecomputedHSV(
            float pH, float pS, float pV, Color pixelColor,
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
                    GetColorMatchScores(pixelColor, pH, pS, pV, out strength, out highlightPot, out matchConf);
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

        public bool ContainsPixel(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            return GetMatchStrength(pixelColor, x, y, texWidth, texHeight) > 0f;
        }

        // マルチサンプルの和集合マッチング。全サンプル（主＋追加スポイト）に対して
        // 1 サンプル分のマッチを計算し、最大強度を採る。サンプルが 1 個なら従来の
        // 単一サンプル計算と完全に一致する（追加スポイトが無い限り出力はビット不変）。
        private void GetColorMatchScores(Color pixelColor, float pH, float pS, float pV, out float strength, out float highlightPotential, out float matchConf)
        {
            strength = 0f;
            highlightPotential = 0f;
            matchConf = 0f;

            var caches = _sampleCaches;
            if (caches == null || caches.Length == 0)
            {
                // UpdateCacheIfNeeded 未実行時の安全網（通常は到達しない）。
                UpdateCacheIfNeeded();
                caches = _sampleCaches;
            }

            for (int si = 0; si < caches.Length; si++)
            {
                MatchOneSample(in caches[si], pixelColor, pH, pS, pV, out float s, out float hPot, out float mc);
                if (s > strength) strength = s;
                if (hPot > highlightPotential) highlightPotential = hPot;
                if (mc > matchConf) matchConf = mc;
            }
        }

        // 1 サンプル分のマッチ強度／ハイライト候補を計算する。サンプル依存の値は sc から、
        // ゾーン共通の値（tolerance・各 range・重み・閾値）はインスタンスフィールドから読む。
        private void MatchOneSample(in SampleCache sc, Color pixelColor, float pH, float pS, float pV, out float strength, out float highlightPotential, out float matchConf)
        {
            strength = 0f;
            highlightPotential = 0f;
            // matchConf: 連結成分アンカリング(flood fill)のコア判定専用の「色一致の確信度」。
            // strength は edgeSoftness=0 だと tolerance 内で二値(=色距離を反映しない)・有彩では
            // 彩度ゲートのみを反映するため、コア判定に使うと「色は遠いが彩度が高い別素材」を
            // コア扱いしてしまう。matchConf は固定半径 CoreMatchDistance で正規化した連続距離
            // (1=サンプル色に一致, 0=半径以遠)で、tolerance に依存せず「色がどれだけ近いか」を表す。
            // 選択/出力には一切使わない(FF OFF ではビット不変)。
            matchConf = 0f;

            // 彩度ガード: 源色が高彩度なときだけ作動し、白/黒/灰色など無彩色寄りの
            // 画素を hard reject する。源色 S が低い（グレー/黒）の場合は
            // saturationGuardFloor が 0 になり、このゲートは作動しない（自動無効）。
            if (sc.saturationGuardFloor > 0f && pS < sc.saturationGuardFloor)
            {
                return;
            }

            // サンプル色の彩度がしきい値以下の場合は、自動的に無彩色(グレー/黒)抽出モードとして扱う
            // 暗いサンプルはHSV色相・彩度が不安定なため、黒るいほどグレースケールモードの適用範囲を動的に広げる。
            // sV = 0 で 0.30、sV >= 0.20 で chromaThreshold に収束する。
            float effectiveChromaThreshold = Mathf.Lerp(GrayModeBaseChromaThreshold, chromaThreshold, Mathf.Clamp01(sc.sV / GrayModeChromaConfidenceRamp));
            if (sc.sS <= effectiveChromaThreshold)
            {
                // グレー抽出モード：HueやSatを完全に無視し、純粋なRGBの近さのみで判定する
                float dr = pixelColor.r - sc.color.r;
                float dg = pixelColor.g - sc.color.g;
                float db = pixelColor.b - sc.color.b;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * InvSqrt3;

                // 暗いサンプル（黒〜暗グレー）の明るい側許容:
                // 黒いファブリックは表面の凹凸・照明により中間グレーのハイライトを持つが同じマテリアル。
                // サンプルが暗いほど、無彩色ピクセルの彩度（≒中立からの逸脱度）を距離指標として使い、
                // 輝度差があっても無彩色なら「同素材」とみなせるようにする。
                float effectiveDist = rgbDist;
                if (sc.sV < GrayModeDarkSampleValue)
                {
                    float darknessFactor = Mathf.Clamp01((GrayModeDarkSampleValue - sc.sV) / GrayModeDarkSampleValue);
                    effectiveDist = Mathf.Lerp(rgbDist, pS, darknessFactor);
                }

                // 彩度整合ゲート: サンプルが微小な tint を持つ(sS>ActivateSat)ときのみ作動。
                // サンプル彩度の相対床 sS*FloorFrac を下回る中性画素(純白背景等)に距離を加算し、
                // pS=0 では確実に tolerance 超え→strength 0 に落とす。AA縁(tint一部残存)は連続的な
                // 部分ペナルティで崖を作らない。sS≈0(真の無彩サンプル)では作動しない。
                // 明部限定(gateWeight=clamp(sV/0.3)): 暗いサンプルは上の分岐で pS を距離指標に使い
                // 「中性=同素材」とみなす(暗布は中性が正常)ため、中性を罰するこのゲートと矛盾する。
                // 暗いサンプルではフェードさせ、明るい tint 素材(クリーム等)でのみ全効果にする。
                if (sc.sS > ChromaGateActivateSat)
                {
                    float gateWeight = Mathf.Clamp01(sc.sV / GrayModeDarkSampleValue);
                    float satFloor = sc.sS * ChromaGateFloorFrac;
                    float shortfall = Mathf.Clamp01((satFloor - pS) / Mathf.Max(satFloor, 1e-4f));
                    effectiveDist += shortfall * ChromaGatePenalty * _cTolerance * gateWeight;
                }

                // AA 縁の忠実復元: 外側の混色帯に soft ramp を与え partial strength にして、
                // 後段デコンタミ(α 再合成)が元の滑らかな AA を復元できるようにする(脚色でなく
                // 元の AA カバレッジの復元)。地色コアは hardRange 未満で full のまま=陰影は不変。
                // ユーザーが edgeSoftness を上げている場合はそちらを尊重(floor として作用)。
                float aaSoftRange = Mathf.Max(softRange, _cTolerance * AchromaEdgeSoftness);
                float aaHardRange = _cTolerance - aaSoftRange;
                strength = CalculateEdgeStrength(effectiveDist, aaHardRange, aaSoftRange);
                // FF コア判定用: グレーモードの色一致確信度(中性ペナルティ込み effectiveDist を使う)。
                if (strength > 0f) matchConf = Mathf.Clamp01(1f - effectiveDist / CoreMatchDistance);
                // ハイライト復元は輝度のみでざっくり判定
                if (highlightRecovery && pV > HighlightValueMin)
                {
                    float vDist = Mathf.Abs(pV - sc.sV);
                    highlightPotential = CalculateEdgeStrength(vDist, hlHardRange, hlSoftRange);
                }
                return;
            }

            // 基礎パラメータの計算
            float satConfidence = Mathf.Clamp01((pS - sc.satMin) / sc.satRamp);
            float hDist = CalculateHueDistance(pH, sc.sH);
            float sRatio = (sc.sS > 0.01f) ? Mathf.Clamp01(pS / sc.sS) : 1f;

            // 無彩色領域でのHueのバタつきを緩和する
            float maxSat = Mathf.Max(pS, sc.sS);
            float hueRelevance = Mathf.Clamp01(maxSat / Mathf.Max(0.01f, chromaThreshold));
            float effectiveHDist = hDist * hueRelevance;

            // 同系色・暗部のシャドウ許容（暗い影の部分は彩度や明度が落ちるが、同じ色として拾う）
            if (pV < sc.sV * ShadowValueThresholdFrac && effectiveHDist < ForgivenessHueGate)
            {
                float darkForgiveness = Mathf.Clamp01((sc.sV * ShadowValueThresholdFrac - pV) / (sc.sV * ForgivenessRangeFrac));

                // 1. 色相(Hue)が離れているほど免除を弱くする（ノイズによる無関係な色の巻き込み防止）
                float hueFactor = 1f - (effectiveHDist / ForgivenessHueGate);
                darkForgiveness *= hueFactor;

                // 2. サンプルが有彩色の場合、対象の彩度が低すぎる(グレー/黒に近い)と免除を減衰
                if (sc.sS > chromaThreshold)
                {
                    float satFactor = Mathf.Clamp01(pS / Mathf.Max(0.01f, shadowForgivenessSatMin));
                    darkForgiveness *= satFactor;
                }

                // 暗いほど、本来の彩度ゲート（satMin）を無視して拾いやすくする
                satConfidence = Mathf.Max(satConfidence, darkForgiveness);
            }
            // 同系色・明部のハイライト許容（上のシャドウ許容の対称形）。
            // 光が強く当たった部分は同じマテリアルでも明度が上がり彩度が抜けて
            // 白っぽくなる（手描きハイライトの芯）。サンプルより明るく同色相なら
            // 彩度ゲートを免除して同素材として拾う。FP は色相ゲートで抑える。
            // シャドウ側の satFactor 減衰は付けない（ハイライトは低彩度化が正常で
            // 暗部のグレー/黒混入とは性質が逆のため）。
            if (!simDisableBrightForgiveness && pV > sc.sV && effectiveHDist < ForgivenessHueGate)
            {
                // 明度の伸び量を上方ヘッドルーム (1 - sV) で正規化。
                // 閾値 sV + (1-sV)*0.25 は暗側 sV*0.75（25% デッドマージン）の鏡像。
                float brightThreshold = sc.sV + (1f - sc.sV) * HighlightValueHeadroomFrac;
                float brightForgiveness = Mathf.Clamp01(
                    (pV - brightThreshold) / Mathf.Max(0.01f, (1f - sc.sV) * ForgivenessRangeFrac));

                // 色相が離れているほど免除を弱くする（暗側と同形・無関係色の巻き込み防止）
                float hueFactor = 1f - (effectiveHDist / ForgivenessHueGate);
                brightForgiveness *= hueFactor;

                satConfidence = Mathf.Max(satConfidence, brightForgiveness);
            }
            // 各距離の計算
            float dist = CalculateHybridDistance(in sc, pixelColor, pS, pV, effectiveHDist, sRatio);
            float gate = Mathf.Lerp(1f, satConfidence, sc.chromaConfidence);

            // 通常マッチ強度
            strength = CalculateEdgeStrength(dist, hardRange, softRange) * gate;
            // FF コア判定用: 有彩モードの色一致確信度。strength は彩度ゲートを掛けるため色の近さを
            // 表さない。dist(実マッチ距離)を「確信できる地色」の固定半径 CoreMatchDistance で正規化。
            if (strength > 0f) matchConf = Mathf.Clamp01(1f - dist / CoreMatchDistance);

            // ハイライト復元マッチ
            if (highlightRecovery)
            {
                highlightPotential = CalculateHighlightRecovery(in sc, pH, pS, pV, effectiveHDist, sRatio);
            }
        }

        private float CalculateHueDistance(float pixelH, float sampleH)
        {
            float hDist = Mathf.Abs(pixelH - sampleH);
            return hDist > 0.5f ? 1f - hDist : hDist;
        }

        private float CalculateHybridDistance(in SampleCache sc, Color pixelColor, float pS, float pV, float hDist, float sRatio)
        {
            float sDist = Mathf.Abs(pS - sc.sS);
            float vDist = Mathf.Abs(pV - sc.sV);
            float hsvDist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            float dr = pixelColor.r - sc.color.r;
            float dg = pixelColor.g - sc.color.g;
            float db = pixelColor.b - sc.color.b;

            // 距離の近似として平方根を残すが、共通して使うことで計算量を抑制できる
            float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * InvSqrt3;

            float finalDist = Mathf.Lerp(rgbDist, hsvDist, sc.chromaConfidence);

            // シャドウ（暗い色）の距離許容は廃止。距離短縮(dist*=Lerp(1,0.3,df))は、同色相だが彩度の
            // 低い near-black の別マテリアル(例: 暗い紺色 S≈0.40/V≈0.09 のパーツ)を tolerance 内へ逆送し
            // 巨大な巻き込みを生む主因。全 subject で recall 非寄与・precision が大幅改善(GT recall 不変)と
            // 実測。暗部の取りこぼし救済は彩度ゲート緩和(GetColorMatchScores の satConfidence 底上げ)で
            // 代替する(in-tolerance 画素にしか効かず安全)。明部(ハイライト)免除はベタ塗り対策で性質が逆の
            // ため温存=非対称は意図的。

            // ハイライト（明部）の距離許容: 上のシャドウ許容の対称形。
            // サンプルより明るく同色相なら、低彩度化したハイライト芯でも同素材として
            // 距離を免除する。免除上限はシャドウ側と同じ 0.3f（最大70%）で対称。
            if (!simDisableBrightForgiveness && pV > sc.sV && hDist < ForgivenessHueGate)
            {
                float brightThreshold = sc.sV + (1f - sc.sV) * HighlightValueHeadroomFrac;
                float brightForgiveness = Mathf.Clamp01(
                    (pV - brightThreshold) / Mathf.Max(0.01f, (1f - sc.sV) * ForgivenessRangeFrac));

                float hueFactor = 1f - (hDist / ForgivenessHueGate);
                brightForgiveness *= hueFactor;

                finalDist *= Mathf.Lerp(1f, BrightDistanceForgivenessMin, brightForgiveness);
            }

            return finalDist;
        }

        private float CalculateHighlightRecovery(in SampleCache sc, float pH, float pS, float pV, float hDist, float sRatio)
        {
            if (pV <= HighlightValueMin || pS >= HighlightSaturationMax || hDist > hlHueCap)
                return 0f;

            float relaxedSatConf = Mathf.Clamp01((pS - HighlightRelaxedSatMin) / HighlightRelaxedSatRamp);
            if (relaxedSatConf <= 0f)
                return 0f;

            float vDist = Mathf.Abs(pV - sc.sV);
            float highlightDist = hDist + vDist * valueWeight * (1f - sRatio);

            if (highlightDist >= _cTolerance)
                return 0f;

            float hlStrength = CalculateEdgeStrength(highlightDist, hlHardRange, hlSoftRange);
            return hlStrength * relaxedSatConf;
        }

        private float CalculateEdgeStrength(float distance, float hRange, float sRange)
        {
            if (distance >= _cTolerance) return 0f;
            if (sRange < 0.0001f || distance <= hRange) return 1f;
            return 1f - (distance - hRange) / sRange;
        }

        private bool IsInRect(int x, int y, int texWidth, int texHeight)
        {
            float u = (float)x / texWidth;
            float v = (float)y / texHeight;
            return uvRect.Contains(new Vector2(u, v));
        }
    }
}
