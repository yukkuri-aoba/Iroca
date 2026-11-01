// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // iroca_inspect の情報のうち、環境と今の色替えの内容・分析。
    public static partial class IrocaAutomation
    {
        /// <summary>1 回で聞ける場所の数の上限。</summary>
        internal const int MaxInspectPoints = 32;

        // 各ゾーンの割合を数える画素の数のおよその上限(全画素を読むと画素の大きさの配列が要る)。
        private const int CoverageSamples = 65536;

        // これより小さいゾーンを「ほとんど塗らない」として知らせる割合(標本の 13 画素分くらい。標本が粗いので、0 ちょうどでは見ない)。
        private const float NoCoverage = 0.0002f;

        private static void FillEnvironment(InspectResult r)
        {
            var ndmf = NdmfStateForApply;
            r.ndmf = ndmf == NdmfSupportState.Supported ? "supported"
                : ndmf == NdmfSupportState.TooOld ? "too_old"
                : ndmf == NdmfSupportState.TooNew ? "too_new"
                : "missing";

            var svc = MaskSuggestBridge.Service;
            r.ai = svc == null ? "no_sentis"
                : svc.Phase == MaskSuggestPhase.NoModel ? "no_model"
                : svc.Phase == MaskSuggestPhase.Error ? "error"
                : svc.Phase == MaskSuggestPhase.NotLoaded ? "not_loaded"
                : "ready";

            r.recolorRunning = s_job != null;
            foreach (var folder in new[] { PresetStore.ProjectPresetFolder, PresetStore.UserPresetFolder })
                foreach (var path in PresetStore.ListJson(folder))
                    r.presets.Add(System.IO.Path.GetFileNameWithoutExtension(path));
        }

        // 見せる編集状態: 窓で開いていれば窓の内容、レシピを指定されていればそれ、このテクスチャのレシピが 1 つならそれ。
        private static IrocaSessionState LoadInspectState(InspectResult r, TextureInfo info, Texture2D tex, IrocaRecipe recipe)
        {
            var source = MeshUvLocator.EditableSourceOf(tex) ?? tex;
            if (EditingWindows.Current != null && EditingWindows.Current.IsShowing(source, out var shown)
                && (recipe == null || shown == recipe))
            {
                var editing = EditingWindows.Current.EditingStateOf(source);
                if (editing != null)
                {
                    info.zonesFrom = "window";
                    return Normalized(editing);
                }
            }
            if (recipe == null)
            {
                var recipes = RecipeStore.FindForTexture(source).ToList();
                if (recipes.Count > 1)
                {
                    r.warnings.Add("this texture has several recipes, so 'zones' is empty. Pass a recipe path from 'recipes' as target to see its zones.");
                    return null;
                }
                if (recipes.Count == 1) recipe = recipes[0];
            }
            if (recipe == null) return null;
            if (RegistrationService.IsUnreadable(recipe))
            {
                r.warnings.Add($"the recipe cannot be read (made by a newer Iroca?): {AssetDatabase.GetAssetPath(recipe)}");
                return null;
            }
            var loaded = RecipeStore.Load(recipe);
            if (loaded == null) return null;
            info.zonesFrom = AssetDatabase.GetAssetPath(recipe);
            return Normalized(loaded);
        }

        private static IrocaSessionState Normalized(IrocaSessionState state)
        {
            state.zones ??= new List<ColorZone>();
            foreach (var z in state.zones) if (z != null) { z.EnsureId(); z.UpdateCacheIfNeeded(); }
            return state;
        }

        private static void AddMeshes(InspectResult r, TextureInfo info, Texture2D tex)
        {
            var found = MeshUvLocator.FindForTexture(MeshUvLocator.EditableSourceOf(tex) ?? tex);
            if (found == null) return;
            foreach (var f in found.found)
                if (!info.meshes.Contains(f.source.name)) info.meshes.Add(f.source.name);
            if (found.found.Count == 0 && found.unreadable > 0)
                r.warnings.Add($"{found.unreadable} mesh part(s) use this texture but cannot be read, so by:'island' is not available "
                    + "(enable Read/Write on the model).");
        }

        // 今の色替えを 1 回走らせて、各ゾーンの選択を読み、割合と聞かれた場所の様子を結果へ書く。
        // 出力は色替えと同じ(選択の控えを取るだけ。プレビューも同じ控えで作っている)。
        private static unsafe void AnalyzeCurrentRecolor(InspectResult r, TextureInfo info, Texture2D tex, NativePixels work,
            IrocaSessionState state, float[] pointsUv)
        {
            int w = work.Width, h = work.Height;
            int pointCount = pointsUv != null ? pointsUv.Length / 2 : 0;
            var input = state != null ? RecolorInput.FromSaved(state) : null;

            // 色替えが無くても、聞かれた場所の元の色とメッシュは返す。
            NativePixels after = null;
            SelectionCache selection = null;
            MaskWorkspace masks = null;
            UvChartMap chart = null;
            NativeMemoryLifecycle.WorkStarted();
            try
            {
                if (input != null)
                {
                    after = Duplicate(work);
                    selection = new SelectionCache();
                    PixelProcessor.ProcessPixelsNative(after.Ptr, w, h, input.Masks, input.Zones, input.Settings,
                        selectionCache: selection, sourcePixels: work.Ptr);
                    masks = new MaskWorkspace();
                    if (state.maskState != null) masks.DecodeFrom(state.maskState);
                }

                if (input != null)
                    FillCoverage(r, info, input.Zones, selection, masks, work, w, h);

                if (pointCount > 0)
                {
                    var found = MeshUvLocator.FindForTexture(MeshUvLocator.EditableSourceOf(tex) ?? tex);
                    if (found != null && found.found.Count > 0)
                    {
                        chart = UvChartMap.Build(w, h, found.Sources());
                        chart.AssignPaddingBaked(chart.DefaultPaddingRadius);
                    }
                    for (int i = 0; i < pointCount; i++)
                    {
                        var p = new PointInfo { uv = new[] { pointsUv[i * 2], pointsUv[i * 2 + 1] } };
                        PreviewCoords.UvToPixel(p.uv[0], p.uv[1], w, h, out int x, out int y);
                        int index = y * w + x;
                        var before = work.Ptr[index];
                        p.hex = "#" + ColorUtility.ToHtmlStringRGB(before);
                        p.alpha = before.a;
                        p.afterHex = p.hex;
                        if (after != null)
                        {
                            var now = after.Ptr[index];
                            p.afterHex = "#" + ColorUtility.ToHtmlStringRGB(now);
                            p.changed = now.r != before.r || now.g != before.g || now.b != before.b;
                        }
                        if (chart != null)
                        {
                            int c = chart.ChartAt(x, y, chart.DefaultPaddingRadius);
                            if (c >= 0)
                            {
                                p.island = c;
                                p.mesh = found.found[chart.ChartSource[c]].source.name;
                            }
                        }
                        if (input != null) FillPointZones(p, input.Zones, selection, masks, x, y, w, h);
                        info.points.Add(p);
                    }
                }
            }
            finally
            {
                input?.ReleaseMasks();
                chart?.Dispose();
                masks?.Clear();
                selection?.Dispose();
                after?.Release();
                NativeMemoryLifecycle.WorkEnded();
                ReleaseMemoryIfNoWindow();
            }
        }

        // 粗い格子の画素を、合成と同じく上のゾーンから順に取り合って、各ゾーンの取り分を数える(MaskRegionEdit.PaintingZoneAt と同じ考え方)。
        private static unsafe void FillCoverage(InspectResult r, TextureInfo info, List<ColorZone> zones, SelectionCache selection,
            MaskWorkspace masks, NativePixels work, int w, int h)
        {
            int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)((double)w * h / CoverageSamples))));
            var indices = new List<int>(CoverageSamples + 1024);
            for (int y = step / 2; y < h; y += step)
                for (int x = step / 2; x < w; x += step)
                    if (work.Ptr[y * w + x].a >= PixelProcessor.OpaqueAlphaMin) indices.Add(y * w + x);
            if (indices.Count == 0) return;

            var idx = indices.ToArray();
            var strengths = new float[zones.Count][];
            for (int k = 0; k < zones.Count; k++)
                strengths[k] = ReadStrength(zones[k], selection, masks, idx, w, h);

            var share = new double[zones.Count];
            for (int i = 0; i < idx.Length; i++)
            {
                float room = 1f;
                for (int k = 0; k < zones.Count && room > 0f; k++)
                {
                    if (strengths[k] == null) continue;
                    float take = Mathf.Min(Mathf.Max(strengths[k][i], 0f), room);
                    share[k] += take;
                    room -= take;
                }
            }
            for (int k = 0; k < zones.Count; k++)
            {
                float coverage = (float)(share[k] / idx.Length);
                var zoneInfo = info.zones.Find(z => z.name == zones[k].name);
                if (zoneInfo != null) zoneInfo.approxCoverage = coverage;
                if (coverage < NoCoverage)
                    r.warnings.Add($"zone '{zones[k].name}' paints almost nothing: another zone earlier in the list may take its pixels, "
                        + "or its masks exclude them. Check with 'points'.");
            }
        }

        private static void FillPointZones(PointInfo p, List<ColorZone> zones, SelectionCache selection, MaskWorkspace masks,
            int x, int y, int w, int h)
        {
            int index = y * w + x;
            var idx = new[] { index };
            var strengths = new float[zones.Count][];
            p.commonExcluded = MaskBitAt(masks?.Common, x, y, w, h);
            for (int k = 0; k < zones.Count; k++)
            {
                var z = zones[k];
                strengths[k] = ReadStrength(z, selection, masks, idx, w, h);
                p.zones.Add(new PointZoneInfo
                {
                    name = z.name,
                    strength = strengths[k] != null ? strengths[k][0] : 0f,
                    excluded = MaskBitAt(masks?.Layer(false, z.id), x, y, w, h),
                    included = MaskBitAt(masks?.Layer(true, z.id), x, y, w, h),
                });
            }
            int painting = MaskRegionEdit.PaintingZoneAt(strengths, 1);
            if (painting >= 0) p.paintedBy = zones[painting].name;
        }

        // 選択の控えは含めるを掛ける前(除外は掛けて返す)なので、合成と同じく、除外されていない含めるの画素を全強度にする。
        private static float[] ReadStrength(ColorZone zone, SelectionCache selection, MaskWorkspace masks, int[] indices, int w, int h)
        {
            var s = new float[indices.Length];
            if (!selection.TryReadLatest(zone.id, w, h, indices, s)) return null;
            var include = masks?.Layer(true, zone.id);
            if (include == null) return s;
            var exclude = masks.Layer(false, zone.id);
            var common = masks.Common;
            for (int i = 0; i < indices.Length; i++)
            {
                int x = indices[i] % w, y = indices[i] / w;
                if (MaskBitAt(include, x, y, w, h) && !MaskBitAt(exclude, x, y, w, h) && !MaskBitAt(common, x, y, w, h))
                    s[i] = 1f;
            }
            return s;
        }

        // 作業座標 (x, y) のマスクの値(座標の対応は PixelProcessor.BuildMaskIndexMap と同じ)。無ければ false。
        private static bool MaskBitAt(NativeBitMask mask, int x, int y, int w, int h)
        {
            if (mask == null) return false;
            int mx = Mathf.Clamp(x * mask.Width / w, 0, mask.Width - 1);
            int my = Mathf.Clamp(y * mask.Height / h, 0, mask.Height - 1);
            return mask.Get(my * mask.Width + mx);
        }
    }
}
