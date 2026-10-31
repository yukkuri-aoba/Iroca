// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
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

            // 不透明な選択にもスポイト位置にもつながらない半透明は塗らない(別の部位の半透明の重ね)。
            int keepX = -1, keepY = -1;
            if (zone.HasSampleUV) PreviewCoords.UvToPixel(zone.sampleUV.x, zone.sampleUV.y, w, h, out keepX, out keepY);
            DropDetachedTranslucent(strength, run.Original, w, h, keepX, keepY, run.Ct);

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
        /// 半透明の選択画素(strength &gt; 0 かつ α &lt; OpaqueAlphaMin)のうち、残った不透明な選択にも
        /// 指定の位置(シード、無ければスポイト位置)にも 4 近傍で地続きにつながらないものを選択から外す。
        /// 連結成分は不透明な画素だけで作る(半透明の余白で島どうしがつながらないように)ので、半透明の画素は
        /// 上のどのゲートも通らずに選ばれたまま残り、別の部位の半透明の重ね(瞳の上の重ね・肌の重ね)を
        /// 丸ごと塗っていた(2026-10-20、ワンショット p50: 過検出 922 万のうち 49.5 万、肌の過検出の 58%・瞳の 77%)。
        /// 部位の縁の AA や、残した部位に重なる半透明の層は地続きなので残る。
        /// </summary>
        private static void DropDetachedTranslucent(float* strength, Color32* px, int w, int h,
            int keepX, int keepY, CancellationToken ct)
        {
            int len = w * h;
            int nT = 0;
            for (int i = 0; i < len; i++)
                if (strength[i] > 0f && px[i].a < OpaqueAlphaMin) nT++;
            if (nT == 0) return;
            bool* reached = s_boolPool.Rent(len);
            int* stack = s_intPool.Rent(nT);
            try
            {
                for (int i = 0; i < len; i++) reached[i] = false;
                int sp = 0;
                bool IsT(int i) => strength[i] > 0f && px[i].a < OpaqueAlphaMin;
                void Push(int i) { if (!reached[i]) { reached[i] = true; stack[sp++] = i; } }
                // 種: 残った不透明な選択に 4 近傍で接する半透明の選択画素と、指定位置の半透明の選択画素。
                for (int y = 0; y < h; y++)
                {
                    if ((y & 255) == 0) ct.ThrowIfCancellationRequested();
                    int rb = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rb + x;
                        if (!IsT(i)) continue;
                        bool touch =
                               (x > 0 && strength[i - 1] > 0f && px[i - 1].a >= OpaqueAlphaMin)
                            || (x + 1 < w && strength[i + 1] > 0f && px[i + 1].a >= OpaqueAlphaMin)
                            || (y > 0 && strength[i - w] > 0f && px[i - w].a >= OpaqueAlphaMin)
                            || (y + 1 < h && strength[i + w] > 0f && px[i + w].a >= OpaqueAlphaMin);
                        if (touch) Push(i);
                    }
                }
                if (keepX >= 0 && keepX < w && keepY >= 0 && keepY < h && IsT(keepY * w + keepX))
                    Push(keepY * w + keepX);
                while (sp > 0)
                {
                    int i = stack[--sp];
                    int x = i % w, y = i / w;
                    if (x > 0 && IsT(i - 1)) Push(i - 1);
                    if (x + 1 < w && IsT(i + 1)) Push(i + 1);
                    if (y > 0 && IsT(i - w)) Push(i - w);
                    if (y + 1 < h && IsT(i + w)) Push(i + w);
                }
                for (int i = 0; i < len; i++)
                    if (!reached[i] && strength[i] > 0f && px[i].a < OpaqueAlphaMin) strength[i] = 0f;
            }
            finally
            {
                s_intPool.Return(stack);
                s_boolPool.Return(reached);
            }
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
