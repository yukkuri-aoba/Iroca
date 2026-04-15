using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // Instance wrapper used by the export path (main thread, can access instance fields directly).
        private void ProcessPixels(Texture2D tex)
        {
            var sorted = zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();
            if (sorted.Count == 0) return;
            Color32[] pixels = tex.GetPixels32();
            ProcessPixelsArray(pixels, tex.width, tex.height,
                exclusionMask, maskWidth, maskHeight, sorted, edgeFeather, antiAliasCleanup);
            tex.SetPixels32(pixels);
        }

        // Static pure-computation method — safe to run on a background thread.
        // No Texture2D, no UnityEngine.Object API, only Mathf and Color math (both thread-safe).
        // originX/Y: crop offset in the full-resolution texture (0 for whole-texture processing).
        // fullW/H: full-resolution texture dimensions (0 = same as w/h, i.e. no crop).
        private static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            bool[] mask, int maskW, int maskH,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0)
        {
            if (fullW <= 0) fullW = w;
            if (fullH <= 0) fullH = h;

            int len = w * h;
            Color32[] originalPixels = new Color32[len];
            System.Array.Copy(pixels, originalPixels, len);

            foreach (var zone in sortedZones)
            {
                // 1. Build strength map using original pixel colors
                float[] strength = new float[len];
                for (int i = 0; i < len; i++)
                {
                    int x = i % w, y = i / w;
                    int xf = x + originX, yf = y + originY;
                    if (IsExcludedStatic(xf, yf, fullW, fullH, mask, maskW, maskH)) continue;
                    strength[i] = zone.GetMatchStrength((Color)originalPixels[i], xf, yf, fullW, fullH);
                }

                // 1b. Fill isolated holes: anti-aliased edge pixels often have low saturation
                //     and get missed by satConfidence, leaving isolated dots of the original color.
                //     If a zero-strength pixel is mostly surrounded by matched pixels, fill it in.
                FillSmallHoles(strength, w, h);

                // 1c. Boundary recovery: re-evaluate unmatched pixels adjacent to matched ones
                //     using the old fixed low-saturation threshold, giving correct gradual strength.
                if (antiAliasCleanup > 0)
                    RecoverBoundaryEdges(strength, w, h, originalPixels,
                        zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight, antiAliasCleanup);

                // 2. Gaussian blur for smooth edge transitions (constrained to edges)
                if (edgeFeather > 0.01f)
                {
                    float[] preBlur = (float[])strength.Clone();
                    strength = GaussianBlur(strength, w, h, edgeFeather);
                    ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * 2.5f));
                }

                // 3. Re-apply exclusion mask: blur can spread strength INTO excluded pixels
                if (mask != null)
                {
                    for (int i = 0; i < len; i++)
                    {
                        int x = i % w, y = i / w;
                        int xf = x + originX, yf = y + originY;
                        if (IsExcludedStatic(xf, yf, fullW, fullH, mask, maskW, maskH)) strength[i] = 0f;
                    }
                }

                // 4. Apply recoloring blended by strength
                for (int i = 0; i < len; i++)
                {
                    float s = strength[i];
                    if (s <= 0.001f) continue;
                    Color original = originalPixels[i];
                    Color32 recolored = RecolorPixel(original, zone.targetColor, zone.sampleColor, zone.valueBlend);
                    pixels[i] = s >= 0.999f ? recolored : Color32.Lerp(pixels[i], recolored, s);
                }
            }
        }

        private static bool IsExcludedStatic(int x, int y, int texW, int texH,
            bool[] mask, int maskW, int maskH)
        {
            if (mask == null) return false;
            int mx = Mathf.Clamp(x * maskW / texW, 0, maskW - 1);
            int my = Mathf.Clamp(y * maskH / texH, 0, maskH - 1);
            return mask[my * maskW + mx];
        }

        private static float[] GaussianBlur(float[] map, int w, int h, float sigma)
        {
            int radius = Mathf.CeilToInt(sigma * 2.5f);
            if (radius < 1) return map;

            // Build 1D kernel
            float[] kernel = new float[radius * 2 + 1];
            float sum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                sum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= sum;

            // Horizontal pass
            float[] temp = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float val = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int nx = Mathf.Clamp(x + k, 0, w - 1);
                        val += map[y * w + nx] * kernel[k + radius];
                    }
                    temp[y * w + x] = val;
                }
            }

            // Vertical pass
            float[] result = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float val = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int ny = Mathf.Clamp(y + k, 0, h - 1);
                        val += temp[ny * w + x] * kernel[k + radius];
                    }
                    result[y * w + x] = val;
                }
            }

            return result;
        }

        private static void ConstrainBlur(float[] blurred, float[] original, int w, int h, int radius)
        {
            // Prevent blur from bleeding into areas that had no nearby match.
            // If neither this pixel nor any neighbor within blur radius had
            // original strength > 0, zero out the blurred value.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (original[idx] > 0f) continue; // was already matched

                    bool hasNearby = false;
                    int yMin = Mathf.Max(0, y - radius);
                    int yMax = Mathf.Min(h - 1, y + radius);
                    int xMin = Mathf.Max(0, x - radius);
                    int xMax = Mathf.Min(w - 1, x + radius);

                    for (int ny = yMin; ny <= yMax && !hasNearby; ny++)
                    {
                        for (int nx = xMin; nx <= xMax && !hasNearby; nx++)
                        {
                            if (original[ny * w + nx] > 0f)
                                hasNearby = true;
                        }
                    }

                    if (!hasNearby)
                        blurred[idx] = 0f;
                }
            }
        }

        /// <summary>
        /// Morphological fill: zero-strength pixels surrounded by a majority of matched
        /// neighbours get filled in with the minimum neighbour strength.
        /// This removes isolated 1-2px dot artifacts caused by anti-aliased edge pixels
        /// whose saturation is too low to pass the satConfidence gate.
        /// Three passes allow filling up to 3px-wide gaps at anti-alias boundaries.
        /// </summary>
        private static void FillSmallHoles(float[] strength, int w, int h)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                float[] filled = (float[])strength.Clone();

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (strength[idx] > 0f) continue;

                        int matched = 0;
                        int total = 0;
                        float minNeighbour = 1f;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                total++;
                                float ns = strength[ny * w + nx];
                                if (ns > 0f)
                                {
                                    matched++;
                                    if (ns < minNeighbour) minNeighbour = ns;
                                }
                            }
                        }

                        // Fill if at least 4 of the (up to 8) neighbours are matched
                        if (matched >= 4 && total >= 4)
                            filled[idx] = minNeighbour;
                    }
                }

                System.Array.Copy(filled, strength, strength.Length);
            }
        }

        /// <summary>
        /// Boundary recovery: for unmatched pixels adjacent to at least one matched pixel,
        /// re-evaluate the colour match using the original fixed low-saturation threshold
        /// (satMin=0.02, satRamp=0.08). This gives correct gradual strength values for
        /// anti-alias edge pixels that the strict dynamic satMin rejected.
        /// Each pass extends recovery by one more pixel outward from the matched boundary.
        /// The adjacency requirement prevents AO/shadow bleed in interior regions.
        /// </summary>
        private static void RecoverBoundaryEdges(
            float[] strength, int w, int h,
            Color32[] pixels, Color sampleColor, float tolerance,
            float edgeSoftness, float valueWeight, int passes)
        {
            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            for (int pass = 0; pass < passes; pass++)
            {
                float[] updated = (float[])strength.Clone();

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (strength[idx] > 0f) continue;

                        // Check if adjacent to at least one matched pixel
                        bool hasMatchedNeighbor = false;
                        for (int dy = -1; dy <= 1 && !hasMatchedNeighbor; dy++)
                            for (int dx = -1; dx <= 1 && !hasMatchedNeighbor; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                if (strength[ny * w + nx] > 0f) hasMatchedNeighbor = true;
                            }

                        if (!hasMatchedNeighbor) continue;

                        // Re-evaluate this pixel with the relaxed fixed saturation threshold
                        float relaxed = GetRelaxedMatchStrength(
                            (Color)pixels[idx], sH, sS, sV, tolerance, edgeSoftness, valueWeight);
                        if (relaxed > 0f)
                            updated[idx] = relaxed;
                    }
                }

                System.Array.Copy(updated, strength, strength.Length);
            }
        }

        /// <summary>
        /// Colour match using the original fixed low saturation threshold (satMin=0.02/satRamp=0.08).
        /// Used only for boundary pixels adjacent to already-matched regions.
        /// </summary>
        private static float GetRelaxedMatchStrength(
            Color pixelColor, float sH, float sS, float sV,
            float tolerance, float edgeSoftness, float valueWeight)
        {
            float pH, pS, pV;
            Color.RGBToHSV(pixelColor, out pH, out pS, out pV);

            // Fixed low saturation threshold — matches original algorithm before dynamic satMin
            float satConfidence = Mathf.Clamp01((pS - 0.02f) / 0.08f);

            float hDist = Mathf.Abs(pH - sH);
            if (hDist > 0.5f) hDist = 1f - hDist;

            // Boundary pixels are expected to have reduced saturation from AA blending.
            // Use a lower sDist weight (0.15 vs 0.15 in main match) to allow recovery
            // of edge pixels whose saturation dropped due to alpha compositing.
            float sDist = Mathf.Abs(pS - sS);
            float vDist = Mathf.Abs(pV - sV);
            float dist = hDist + sDist * 0.15f + vDist * valueWeight;

            if (dist >= tolerance) return 0f;

            float softRange = tolerance * edgeSoftness;
            float hardRange = tolerance - softRange;

            float strength;
            if (softRange < 0.0001f)
                strength = 1f;
            else if (dist <= hardRange)
                strength = 1f;
            else
                strength = 1f - (dist - hardRange) / softRange;

            return strength * satConfidence;
        }

        private static Color32 RecolorPixel(Color original, Color target, Color sample, float valueBlend)
        {
            float oH, oS, oV;
            float tH, tS, tV;
            float sH, sS, sV;
            Color.RGBToHSV(original, out oH, out oS, out oV);
            Color.RGBToHSV(target,   out tH, out tS, out tV);
            Color.RGBToHSV(sample,   out sH, out sS, out sV);

            // Preserve saturation ratio: antialias boundary pixels keep their relative saturation
            float newS = (sS > 0.001f) ? Mathf.Clamp01(oS * tS / sS) : tS;

            float newV = Mathf.Lerp(tV, oV, valueBlend);

            Color result = Color.HSVToRGB(tH, newS, newV);
            result.a = original.a;
            return result;
        }
    }
}
