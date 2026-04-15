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
        public string name = "Zone";
        public bool enabled = true;
        public SelectionMode mode = SelectionMode.ColorPick;

        // ColorPick mode
        public Color sampleColor = Color.white;
        public float tolerance = 0f;

        // Rect mode (UV coordinates 0-1)
        public Rect uvRect = new Rect(0, 0, 1, 1);

        // Target
        public Color targetColor = Color.white;

        // 0 = use target V completely (flat/vivid), 1 = keep original V completely (preserve pattern)
        [Range(0f, 1f)]
        public float valueBlend = 1f;

        // 0 = hard edge, 1 = very soft edge (anti-alias friendly)
        [Range(0f, 1f)]
        public float edgeSoftness = 0f;

        // Saturation strictness: controls how aggressively low-saturation pixels (AO/shadow)
        // are excluded from colour matching. Higher = fewer false positives (less bleed) but
        // may leave dot artifacts at anti-alias edges. Lower = more inclusive matching.
        // 0 = fixed low threshold (0.02), 0.50 = default, 1.0 = maximum strictness.
        [Range(0f, 1f)]
        public float saturationStrictness = 0.20f;

        // Layer index: zones in higher layers override lower layers (0 = base layer)
        public int layerIndex = 0;

        /// <summary>
        /// Returns a match strength in [0, 1] for soft selection.
        /// 0 = no match, 1 = full match, intermediate = partial (edge/transition).
        /// </summary>
        public float GetMatchStrength(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            if (!enabled) return 0f;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    return GetColorMatchStrength(pixelColor);
                case SelectionMode.Rect:
                    return IsInRect(x, y, texWidth, texHeight) ? 1f : 0f;
                default:
                    return 0f;
            }
        }

        public bool ContainsPixel(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            return GetMatchStrength(pixelColor, x, y, texWidth, texHeight) > 0f;
        }

        private float GetColorMatchStrength(Color pixelColor)
        {
            float pH, pS, pV, sH, sS, sV;
            Color.RGBToHSV(pixelColor, out pH, out pS, out pV);
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            // Dynamic saturation threshold: when the sample is highly saturated (e.g. vivid blue),
            // require pixels to have a proportional minimum saturation to match.
            // This prevents AO/shadow background layers that share the same hue
            // (but are much less saturated) from being recoloured.
            // Trade-off: high multiplier blocks AO/shadow background; FillSmallHolesHueAware
            // spatially recovers AA-edge pixels near matched boundaries.
            // For low-saturation samples the threshold collapses to the fixed 0.02/0.08 ramp.
            float satMin  = Mathf.Max(0.02f, sS * saturationStrictness);
            float satRamp = Mathf.Max(0.08f, sS * 0.10f);
            float satConfidence = Mathf.Clamp01((pS - satMin) / satRamp);

            // Hue distance (circular)
            float hDist = Mathf.Abs(pH - sH);
            if (hDist > 0.5f) hDist = 1f - hDist;

            // Saturation distance (light weight): excludes truly neutral pixels
            // but allows shadow/highlight variations of the same material.
            float sDist = Mathf.Abs(pS - sS);

            // Value distance intentionally omitted: brightness varies across
            // shadows and highlights of the same hue, and should all be recoloured.
            float dist = hDist + sDist * 0.15f;

            if (dist >= tolerance) return 0f;

            // Soft edge: gradual falloff in the outer portion of the tolerance range
            float softRange = tolerance * edgeSoftness;
            float hardRange = tolerance - softRange;

            float strength;
            if (softRange < 0.0001f)
                strength = 1f; // hard edge mode
            else if (dist <= hardRange)
                strength = 1f;
            else
                strength = 1f - (dist - hardRange) / softRange;

            return strength * satConfidence;
        }

        private bool IsInRect(int x, int y, int texWidth, int texHeight)
        {
            float u = (float)x / texWidth;
            float v = (float)y / texHeight;
            return uvRect.Contains(new Vector2(u, v));
        }
    }
}
