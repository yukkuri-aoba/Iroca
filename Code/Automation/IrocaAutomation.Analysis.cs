// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    // iroca_inspect の情報のうち、環境と今の色替えの内容・分析。
    public static partial class IrocaAutomation {
        /// <summary>1 回で聞ける場所の数の上限。</summary>
        internal const int MaxInspectPoints = 32;

        // 各ゾーンの割合を数える画素の数のおよその上限(全画素を読むと画素の大きさの配列が要る)。
        private const int CoverageSamples = 65536;

        // これより小さいゾーンを「ほとんど塗らない」として知らせる割合(標本の 13 画素分くらい。標本が粗いので、0 ちょうどでは見ない)。
        private const float NoCoverage = 0.0002f;

        private static void FillEnvironment(InspectResult r) {
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

            // 終わって結果を待っているだけの依頼は、次の依頼を断らないので数えない。
            r.recolorRunning = s_job != null && s_job.finalJson == null;
            foreach (var folder in new[] { PresetStore.ProjectPresetFolder, PresetStore.UserPresetFolder }) {
                foreach (var path in PresetStore.ListJson(folder)) {
                    r.presets.Add(System.IO.Path.GetFileNameWithoutExtension(path));
                }
            }
        }

        // 見せる編集状態: 窓で開いていれば窓の内容、レシピを指定されていればそれ、このテクスチャのレシピが 1 つならそれ。
        private static IrocaSessionState LoadInspectState(InspectResult r, TextureInfo info, Texture2D tex, IrocaRecipe recipe) {
            var source = MeshUvLocator.EditableSourceOf(tex) ?? tex;
            if (EditingWindows.Current != null && EditingWindows.Current.IsShowing(source, out var shown)
                && (recipe == null || shown == recipe)) {
                var editing = EditingWindows.Current.EditingStateOf(source);
                if (editing != null) {
                    info.zonesFrom = "window";
                    return Normalized(editing);
                }
            }
            if (recipe == null) {
                // このテクスチャのレシピは NewTextureInfo が探して info.recipes に入れてある(同じ元テクスチャで探した
                // 同じ並び)。プロジェクトのレシピ探しをもう 1 回しない。
                var recipes = info.recipes.Select(AssetDatabase.LoadAssetAtPath<IrocaRecipe>).Where(x => x != null).ToList();
                if (recipes.Count > 1) {
                    r.warnings.Add("this texture has several recipes, so 'zones' is empty. Pass a recipe path from 'recipes' as target to see its zones.");
                    return null;
                }
                if (recipes.Count == 1) {
                    recipe = recipes[0];
                }
            }
            if (recipe == null) {
                return null;
            }
            if (RegistrationService.IsUnreadable(recipe)) {
                r.warnings.Add($"the recipe cannot be read (made by a newer Iroca?): {AssetDatabase.GetAssetPath(recipe)}");
                return null;
            }
            var loaded = RecipeStore.Load(recipe);
            if (loaded == null) {
                return null;
            }
            info.zonesFrom = AssetDatabase.GetAssetPath(recipe);
            return Normalized(loaded);
        }

        private static IrocaSessionState Normalized(IrocaSessionState state) {
            state.zones ??= new List<ColorZone>();
            foreach (var z in state.zones) {
                z?.EnsureId();
            }
            return state;
        }

        // 見つけたメッシュを返す(聞かれた場所の島を引くときに使い回す。探索は全 Renderer の UV の読み出しなので 1 回だけ)。
        private static MeshUvLocator.Result AddMeshes(InspectResult r, TextureInfo info, Texture2D tex) {
            var found = MeshUvLocator.FindForTexture(MeshUvLocator.EditableSourceOf(tex) ?? tex);
            if (found == null) {
                return null;
            }
            foreach (var f in found.found) {
                if (!info.meshes.Contains(f.source.name)) {
                    info.meshes.Add(f.source.name);
                }
            }
            if (found.found.Count == 0 && found.unreadable > 0) {
                r.warnings.Add($"{found.unreadable} mesh part(s) use this texture but cannot be read, so by:'island' is not available "
                    + "(enable Read/Write on the model).");
            }
            return found;
        }

        // 今の色替えを 1 回走らせて、各ゾーンの選択を読み、割合と聞かれた場所の様子を結果へ書く。
        // 出力は色替えと同じ(選択の控えを取るだけ。プレビューも同じ控えで作っている)。
        private static unsafe void AnalyzeCurrentRecolor(InspectResult r, TextureInfo info, Texture2D tex, NativePixels work,
            IrocaSessionState state, float[] pointsUv, MeshUvLocator.Result found) {
            int w = work.Width, h = work.Height;
            int pointCount = pointsUv != null ? pointsUv.Length / 2 : 0;
            var input = state != null ? RecolorInput.FromSaved(state) : null;

            // 色替えが無くても、聞かれた場所の元の色とメッシュは返す。
            NativePixels after = null;
            SelectionCache selection = null;
            MaskWorkspace masks = null;
            UvChartMap chart = null;
            NativeMemoryLifecycle.WorkStarted();
            try {
                // 読む画素: 割合を数える粗い格子(先頭の coverageCount 個)と、聞かれた場所(その後ろに 1 つずつ)。
                // 選択は、この画素の値だけを控える(SelectionCache.ForSamples)。全画素を控えると、ゾーンの数 × 4K で 64MB・
                // 8K で 256MB を持つ(8K・6 ゾーンで約 1.5GB だった)。控える値は計算した強さそのものなので、結果は同じ。
                int[] samples = null;
                int coverageCount = 0;
                float[][] strengths = null;
                if (input != null) {
                    int[] coverage = CoverageIndices(work, w, h);
                    coverageCount = coverage.Length;
                    samples = new int[coverageCount + pointCount];
                    System.Array.Copy(coverage, samples, coverageCount);
                    for (int i = 0; i < pointCount; i++) {
                        PreviewCoords.UvToPixel(pointsUv[i * 2], pointsUv[i * 2 + 1], w, h, out int sx, out int sy);
                        samples[coverageCount + i] = sy * w + sx;
                    }
                    after = Duplicate(work);
                    selection = SelectionCache.ForSamples(samples);
                    PixelProcessor.ProcessPixelsNative(after.Ptr, w, h, input.Masks, input.Zones, input.Settings,
                        selectionCache: selection, sourcePixels: work.Ptr);
                    masks = new MaskWorkspace();
                    if (state.maskState != null) {
                        masks.DecodeFrom(state.maskState);
                    }
                    strengths = new float[input.Zones.Count][];
                    for (int k = 0; k < input.Zones.Count; k++) {
                        strengths[k] = ReadStrength(input.Zones[k], selection, masks, samples, w, h);
                    }
                }

                if (input != null) {
                    FillCoverage(r, info, input.Zones, strengths, coverageCount);
                }

                if (pointCount > 0) {
                    // メッシュは AddMeshes が探したもの(同じ元テクスチャで探した同じ結果)を使う。
                    if (found != null && found.found.Count > 0) {
                        chart = UvChartMap.BuildBaked(w, h, found.Sources());
                    }
                    for (int i = 0; i < pointCount; i++) {
                        var p = new PointInfo { uv = new[] { pointsUv[i * 2], pointsUv[i * 2 + 1] } };
                        PreviewCoords.UvToPixel(p.uv[0], p.uv[1], w, h, out int x, out int y);
                        int index = y * w + x;
                        var before = work.Ptr[index];
                        p.hex = "#" + ColorUtility.ToHtmlStringRGB(before);
                        p.alpha = before.a;
                        p.afterHex = p.hex;
                        if (after != null) {
                            var now = after.Ptr[index];
                            p.afterHex = "#" + ColorUtility.ToHtmlStringRGB(now);
                            p.changed = now.r != before.r || now.g != before.g || now.b != before.b;
                        }
                        if (chart != null) {
                            int c = chart.ChartAt(x, y, chart.DefaultPaddingRadius);
                            if (c >= 0) {
                                p.island = c;
                                p.mesh = found.found[chart.ChartSource[c]].source.name;
                            }
                        }
                        if (input != null) {
                            FillPointZones(p, input.Zones, strengths, coverageCount + i, masks, x, y, w, h);
                        }
                        info.points.Add(p);
                    }
                }
            } finally {
                input?.ReleaseMasks();
                chart?.Dispose();
                masks?.Clear();
                selection?.Dispose();
                after?.Release();
                NativeMemoryLifecycle.WorkEnded();
                ReleaseMemoryIfNoWindow();
            }
        }

        // 割合を数える粗い格子の画素(不透明なものだけ。約 CoverageSamples 個)。
        private static unsafe int[] CoverageIndices(NativePixels work, int w, int h) {
            int step = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)((double)w * h / CoverageSamples))));
            var indices = new List<int>(CoverageSamples + 1024);
            for (int y = step / 2; y < h; y += step) {
                for (int x = step / 2; x < w; x += step) {
                    if (work.Ptr[y * w + x].a >= PixelProcessor.OpaqueAlphaMin) {
                        indices.Add(y * w + x);
                    }
                }
            }
            return indices.ToArray();
        }

        // 粗い格子の画素を、合成と同じく上のゾーンから順に取り合って、各ゾーンの取り分を数える(MaskRegionEdit.PaintingZoneAt と同じ考え方)。
        // strengths[k] はゾーン k の、読む画素の強さ(先頭の count 個が粗い格子)。
        private static void FillCoverage(InspectResult r, TextureInfo info, List<ColorZone> zones, float[][] strengths, int count) {
            if (count == 0) {
                return;
            }

            var share = new double[zones.Count];
            for (int i = 0; i < count; i++) {
                float room = 1f;
                for (int k = 0; k < zones.Count && room > 0f; k++) {
                    if (strengths[k] == null) {
                        continue;
                    }
                    float take = Mathf.Min(Mathf.Max(strengths[k][i], 0f), room);
                    share[k] += take;
                    room -= take;
                }
            }
            for (int k = 0; k < zones.Count; k++) {
                float coverage = (float)(share[k] / count);
                // 名前でなく id で突き合わせる。同じ名前のゾーンが 2 つあると、名前では先頭の欄に後のゾーンの値が上書きで入り、
                // 後の欄は測っていない(-1)ままになる(同じ名前は窓・プリセットから入る)。
                string id = zones[k].id;
                var zoneInfo = string.IsNullOrEmpty(id)
                    ? info.zones.Find(z => z.name == zones[k].name)
                    : info.zones.Find(z => z.zoneId == id);
                if (zoneInfo != null) {
                    zoneInfo.approxCoverage = coverage;
                }
                if (coverage < NoCoverage) {
                    r.warnings.Add($"zone '{zones[k].name}' paints almost nothing: another zone earlier in the list may take its pixels, "
                        + "or its masks exclude them. Check with 'points'.");
                }
            }
        }

        // all[k] はゾーン k の、読む画素の強さ(無ければ null)。この場所は、その at 番目。
        private static void FillPointZones(PointInfo p, List<ColorZone> zones, float[][] all, int at, MaskWorkspace masks,
            int x, int y, int w, int h) {
            var strengths = new float[zones.Count][];
            p.commonExcluded = MaskBitAt(masks?.Common, x, y, w, h);
            for (int k = 0; k < zones.Count; k++) {
                var z = zones[k];
                strengths[k] = all[k] != null ? new[] { all[k][at] } : null;
                p.zones.Add(new PointZoneInfo {
                    name = z.name,
                    strength = strengths[k] != null ? strengths[k][0] : 0f,
                    excluded = MaskBitAt(masks?.Layer(false, z.id), x, y, w, h),
                    included = MaskBitAt(masks?.Layer(true, z.id), x, y, w, h),
                });
            }
            int painting = MaskRegionEdit.PaintingZoneAt(strengths, 1);
            if (painting >= 0) {
                p.paintedBy = zones[painting].name;
            }
        }

        // 選択の控えは含めるを掛ける前(除外は掛けて返す)なので、合成と同じく、除外されていない含めるの画素を全強度にする。
        // selection は indices だけを控える作り(SelectionCache.ForSamples(indices))で、返す並びも indices と同じ。
        private static float[] ReadStrength(ColorZone zone, SelectionCache selection, MaskWorkspace masks, int[] indices, int w, int h) {
            var s = new float[indices.Length];
            if (!selection.TryReadSamples(zone.id, w, h, s)) {
                return null;
            }
            var include = masks?.Layer(true, zone.id);
            if (include == null) {
                return s;
            }
            var exclude = masks.Layer(false, zone.id);
            var common = masks.Common;
            for (int i = 0; i < indices.Length; i++) {
                int x = indices[i] % w, y = indices[i] / w;
                if (MaskBitAt(include, x, y, w, h) && !MaskBitAt(exclude, x, y, w, h) && !MaskBitAt(common, x, y, w, h)) {
                    s[i] = 1f;
                }
            }
            return s;
        }

        // 作業座標 (x, y) のマスクの値(座標の対応は PixelProcessor.BuildMaskIndexMap と同じ)。無ければ false。
        private static bool MaskBitAt(NativeBitMask mask, int x, int y, int w, int h) {
            if (mask == null) {
                return false;
            }
            int mx = Mathf.Clamp(x * mask.Width / w, 0, mask.Width - 1);
            int my = Mathf.Clamp(y * mask.Height / h, 0, mask.Height - 1);
            return mask.Get(my * mask.Width + mx);
        }
    }
}
