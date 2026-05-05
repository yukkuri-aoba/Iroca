using System;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public enum SelectionMode
    {
        ColorPick,
        Rect
    }

    [Serializable]
    public class ColorZone
    {
        // ハイライト補助マッチで対象ピクセルとみなす「鏡面反射/ハイライトらしさ」の閾値。
        private const float HighlightValueMin = 0.80f;
        private const float HighlightSaturationMax = 0.20f;
        private const float HighlightRelaxedSatMin = 0.02f;
        private const float HighlightRelaxedSatRamp = 0.08f;

        // 低彩度サンプル時の RGB ↔ HSV ハイブリッド距離フェード閾値。
        private const float ChromaConfidenceLo = 0.05f;
        private const float ChromaConfidenceHi = 0.15f;

        public string name = "Zone";
        public bool enabled = true;
        public SelectionMode mode = SelectionMode.ColorPick;

        // カラーピックモード
        public Color sampleColor = Color.white;
        public float tolerance = 0f;

        // 矩形モード（UV座標0-1）
        public Rect uvRect = new Rect(0, 0, 1, 1);

        // Flood Fill（連続領域モード）: ColorPick モードで有効
        public bool useFloodFill = false;
        // シード点のUV座標（0-1）。負値 = 未設定
        public Vector2 seedUV = new Vector2(-1f, -1f);

        [Range(0f, 0.5f)]
        public float edgeStopThreshold = 0.15f;

        // 変更先
        public Color targetColor = Color.white;

        [Range(0f, 1f)]
        public float valueBlend = 1f;

        [Range(0f, 1f)]
        public float edgeSoftness = 0f;

        [Range(0f, 1f)]
        public float saturationStrictness = 0.50f;

        [Range(0f, 1f)]
        public float valueWeight = 1.0f;

        [Range(0f, 1f)]
        public float satDistWeight = 0.15f;

        [Range(0.01f, 0.5f)]
        public float satRampScale = 0.10f;

        [Range(0f, 1f)]
        public float shadowDesaturation = 0.35f;

        [Range(0f, 1f)]
        public float shadowForgivenessSatMin = 0.05f;

        public bool highlightRecovery = true;
        public int layerIndex = 0;
        public string id = "";

        // === 事前計算キャッシュ ===
        [NonSerialized] private bool _cacheInitiated = false;
        [NonSerialized] private Color _cSampleColor;
        [NonSerialized] private float _cTolerance, _cSatStrictness, _cSatRampScale, _cEdgeSoftness;
        
        // キャッシュされた値
        [NonSerialized] private float sH, sS, sV; // サンプル色のHSV
        [NonSerialized] private float satMin, satRamp;
        [NonSerialized] private float chromaConfidence;
        [NonSerialized] private float softRange, hardRange;
        [NonSerialized] private float hlHueCap;
        [NonSerialized] private float hlSoftRange, hlHardRange;

        public void EnsureId()
        {
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString();
        }

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
                _cEdgeSoftness == edgeSoftness)
            {
                return;
            }

            _cSampleColor = sampleColor;
            _cTolerance = tolerance;
            _cSatStrictness = saturationStrictness;
            _cSatRampScale = satRampScale;
            _cEdgeSoftness = edgeSoftness;
            _cacheInitiated = true;

            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            satMin = Mathf.Max(0.02f, sS * saturationStrictness);
            satRamp = Mathf.Max(0.08f, sS * satRampScale);

            float baseChromaConf = Mathf.Clamp01((sS - ChromaConfidenceLo) / (ChromaConfidenceHi - ChromaConfidenceLo));
            // 暗すぎる色（黒）は彩度データが高くても色相（Hue）の計算がノイズで暴れるため信用しない
            float valueConf = Mathf.Clamp01((sV - 0.05f) / 0.15f); // Vが0.05(非常に暗い)〜0.20の範囲で減衰
            chromaConfidence = Mathf.Min(baseChromaConf, valueConf);

            softRange = tolerance * edgeSoftness;
            hardRange = tolerance - softRange;

            hlHueCap = Mathf.Max(0.05f, tolerance * 0.3f);
            hlSoftRange = tolerance * edgeSoftness;
            hlHardRange = tolerance - hlSoftRange;
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
                    GetColorMatchScores(pixelColor, pH, pS, pV, out strength, out highlightPot);
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
            out float strength, out float highlightPot)
        {
            strength = 0f;
            highlightPot = 0f;
            if (!enabled) return;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    GetColorMatchScores(pixelColor, pH, pS, pV, out strength, out highlightPot);
                    break;
                case SelectionMode.Rect:
                    if (IsInRect(x, y, texWidth, texHeight))
                        strength = 1f;
                    break;
            }
        }

        public bool ContainsPixel(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            return GetMatchStrength(pixelColor, x, y, texWidth, texHeight) > 0f;
        }

        private void GetColorMatchScores(Color pixelColor, float pH, float pS, float pV, out float strength, out float highlightPotential)
        {
            strength = 0f;
            highlightPotential = 0f;

            // 基礎パラメータの計算
            float satConfidence = Mathf.Clamp01((pS - satMin) / satRamp);
            float hDist = CalculateHueDistance(pH, sH);
            float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
            // 同系色・暗部のシャドウ許容（暗い影の部分は彩度や明度が落ちるが、同じ色として拾う）
            if (pV < sV * 0.75f && hDist < 0.15f)
            {
                float darkForgiveness = Mathf.Clamp01((sV * 0.75f - pV) / (sV * 0.6f));
                
                // 1. 色相(Hue)が離れているほど免除を弱くする（ノイズによる無関係な色の巻き込み防止）
                float hueFactor = 1f - (hDist / 0.15f);
                darkForgiveness *= hueFactor;

                // 2. サンプルが有彩色(S > 0.05)の場合、対象の彩度が低すぎる(グレー/黒に近い)と免除を減衰
                if (sS > 0.05f)
                {
                    float satFactor = Mathf.Clamp01(pS / Mathf.Max(0.01f, shadowForgivenessSatMin));
                    darkForgiveness *= satFactor;
                }
                
                // 暗いほど、本来の彩度ゲート（satMin）を無視して拾いやすくする
                satConfidence = Mathf.Max(satConfidence, darkForgiveness);
            }
            // 各距離の計算
            float dist = CalculateHybridDistance(pixelColor, pS, pV, hDist, sRatio);
            float gate = Mathf.Lerp(1f, satConfidence, chromaConfidence);

            // 通常マッチ強度
            strength = CalculateEdgeStrength(dist, hardRange, softRange) * gate;

            // ハイライト復元マッチ
            if (highlightRecovery)
            {
                highlightPotential = CalculateHighlightRecovery(pH, pS, pV, hDist, sRatio);
            }
        }

        private float CalculateHueDistance(float pixelH, float sampleH)
        {
            float hDist = Mathf.Abs(pixelH - sampleH);
            return hDist > 0.5f ? 1f - hDist : hDist;
        }

        private float CalculateHybridDistance(Color pixelColor, float pS, float pV, float hDist, float sRatio)
        {
            float sDist = Mathf.Abs(pS - sS);
            float vDist = Mathf.Abs(pV - sV);
            float hsvDist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            float dr = pixelColor.r - _cSampleColor.r;
            float dg = pixelColor.g - _cSampleColor.g;
            float db = pixelColor.b - _cSampleColor.b;
            
            // 距離の近似として平方根を残すが、共通して使うことで計算量を抑制できる
            float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f;

            float finalDist = Mathf.Lerp(rgbDist, hsvDist, chromaConfidence);

            // シャドウ（暗い色）の距離許容:
            if (pV < sV * 0.75f && hDist < 0.15f)
            {
                float darkForgiveness = Mathf.Clamp01((sV * 0.75f - pV) / (sV * 0.6f));
                
                // 1. 色相(Hue)が離れているほど免除を弱くする（ノイズによる無関係な色の巻き込み防止）
                float hueFactor = 1f - (hDist / 0.15f);
                darkForgiveness *= hueFactor;

                // 2. サンプルが有彩色(S > 0.05)の場合、対象の彩度が低すぎる(グレー/黒に近い)と免除を減衰
                if (sS > 0.05f)
                {
                    float satFactor = Mathf.Clamp01(pS / Mathf.Max(0.01f, shadowForgivenessSatMin));
                    darkForgiveness *= satFactor;
                }

                // 免除が強すぎると他のテクスチャで許容範囲が広がりすぎるため、0.3f (最大70%免除) に抑える
                finalDist *= Mathf.Lerp(1f, 0.3f, darkForgiveness);
            }

            return finalDist;
        }

        private float CalculateHighlightRecovery(float pH, float pS, float pV, float hDist, float sRatio)
        {
            if (pV <= HighlightValueMin || pS >= HighlightSaturationMax || hDist > hlHueCap) 
                return 0f;

            float relaxedSatConf = Mathf.Clamp01((pS - HighlightRelaxedSatMin) / HighlightRelaxedSatRamp);
            if (relaxedSatConf <= 0f) 
                return 0f;

            float vDist = Mathf.Abs(pV - sV);
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
