// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>1 回の処理の中で使い回す、領域と素材の地図(テクスチャの色だけで決まる)。</summary>
        private sealed class MaterialScene : IDisposable
        {
            public readonly RegionMap Regions;
            public readonly MaterialMap Materials;

            public MaterialScene(Color32* pixels, int w, int h)
            {
                Regions = RegionMap.Build(pixels, w, h, RegionMapSettings.Default);
                Materials = MaterialMap.Build(Regions);
            }

            public void Dispose() => Regions.Dispose();
        }

        /// <summary>
        /// 新しい選択(T5 の L3)。ゾーンの素材の領域を塗り、縁の混色の画素は素材の側の被覆率にする。
        /// マスクを掛ける前の強度と後段の範囲を返す(<see cref="SelectionResult"/>。旧の選択と同じ形)。
        /// </summary>
        private static SelectionResult RunMaterialSelection(RunContext run, ColorZone zone, ZoneBuffers zb, ulong[] zoneMask)
        {
            int w = run.W, h = run.H, len = run.Len;
            var scene = run.Materials ??= new MaterialScene(run.Original, w, h);
            RegionMap regions = scene.Regions;
            bool[] selected = scene.Materials.SelectedRegions(zone, w, h);

            float* strength = zb.Strength = s_floatPool.Rent(len);
            var strengthLocal = strength;
            int* labels = regions.Labels;
            ulong[] commonMask = run.CommonMask;
            int[] maskColOf = run.MaskColOf, maskRowBase = run.MaskRowBase;
            Parallel.For(0, h, run.Po, y =>
            {
                int rowOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOff + x;
                    strengthLocal[i] = selected[labels[i]] && !IsExcludedAt(maskColOf, maskRowBase, x, y, commonMask, zoneMask) ? 1f : 0f;
                }
            });

            // 2 つの領域の混色の画素: 片方だけが選ばれていれば、その側の被覆率。
            byte* mix = regions.Mix;
            int* index = regions.MixtureIndex;
            int* partner = regions.MixturePartner;
            for (int k = 0; k < regions.MixturePixels; k++)
            {
                int i = index[k];
                bool own = selected[labels[i]], other = selected[partner[k]];
                if (own == other) continue;
                if (IsExcludedAt(maskColOf, maskRowBase, i % w, i / w, commonMask, zoneMask)) continue;
                float cover = mix[i] / 255f;
                strength[i] = own ? cover : 1f - cover;
            }

            int blurRadius = run.EdgeFeather > EdgeFeatherBlurMin ? Mathf.CeilToInt(run.EdgeFeather * GaussianRadiusPerSigma) : 0;
            int margin = run.HoleFillPasses + Mathf.Max(0, run.AntiAliasCleanup) + blurRadius + 2;
            bool has = TryComputeStrengthBBox(strength, w, h, 0f, out int minX, out int minY, out int maxX, out int maxY, run.Ct);
            return new SelectionResult
            {
                Strength = strength,
                Box = new SelectionCache.PostBox
                {
                    Has = has,
                    MinX = has ? Mathf.Max(0, minX - margin) : 0,
                    MinY = has ? Mathf.Max(0, minY - margin) : 0,
                    MaxX = has ? Mathf.Min(w - 1, maxX + margin) : 0,
                    MaxY = has ? Mathf.Min(h - 1, maxY + margin) : 0,
                },
            };
        }

        /// <summary>
        /// 自動調整の新しい形(T5 の P2 の先取り): クリックの素材を決め、スポイト色をその素材の地色にする。
        /// AI 提案(無ければ null)は、クリックが端の色や縁に当たったときに素材を選び直す投票に使う。
        /// </summary>
        internal static void TuneZoneByMaterial(Color32* pixels, int w, int h, ColorZone zone, bool[] evidence, int evidenceW, int evidenceH)
        {
            using var scene = new MaterialScene(pixels, w, h);
            scene.Materials.TuneZone(zone, w, h, evidence, evidenceW, evidenceH);
        }
    }
}
