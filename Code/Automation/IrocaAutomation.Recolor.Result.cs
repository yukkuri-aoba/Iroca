// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    // iroca_recolor のうち、結果(比較画像と指標・ゾーンの一覧・返す JSON)。
    // 入口(Recolor・RecolorStatus)と依頼の寿命は IrocaAutomation.Recolor.cs。
    public static partial class IrocaAutomation {
        /// <summary>比較画像の材料(作業解像度。登録・プレビューで見えるものと同じ)。</summary>
        private sealed class Comparison {
            public RecolorPreview.ChangeMetrics metrics;
            public byte[] panelPng;    // 作れなければ null(理由は panelError)
            public string panelError;
        }

        // 作業解像度で色替えして、変化の指標と比較画像(PNG)を作る。Unity の API に触らないので背景から呼んでよい。
        // 色替えした画素はヒープの外に作る。keepAfter なら after に返す(呼ぶ側が Release する)。
        private static unsafe Comparison Compare(NativePixels work, IrocaSessionState state, CancellationToken ct,
            bool keepAfter, out NativePixels after) {
            after = null;
            int w = work.Width, h = work.Height;
            var recolored = Duplicate(work);
            try {
                SessionRecolor.Apply(recolored.Ptr, w, h, state, ct);
                ct.ThrowIfCancellationRequested();
                var c = BuildComparison(work, recolored);
                if (keepAfter) {
                    after = recolored;
                    recolored = null;
                }
                return c;
            } finally { recolored?.Release(); }
        }

        // 色替えの前と後の画素(同じ寸法)から、変化の指標と比較画像(PNG)を作る。Unity の API に触らないので背景から呼んでよい。
        // MCP の色替えと、静的 API・コマンドラインの色替えが使う(以前は静的 API の側が同じことを別に書いていた)。
        private static unsafe Comparison BuildComparison(NativePixels before, NativePixels after) {
            int w = before.Width, h = before.Height;
            var c = new Comparison { metrics = RecolorPreview.ComputeMetrics(before.Ptr, after.Ptr, w, h) };
            try {
                var panel = RecolorPreview.BuildComparisonPanel(before.Ptr, after.Ptr, w, h,
                    RecolorPreview.DefaultMaxTile, out int pw, out int ph);
                c.panelPng = ExportPipeline.EncodePng(panel, pw, ph);
            } catch (Exception ex) {
                c.panelError = $"{ex.GetType().Name}: {ex.Message}";
            }
            return c;
        }

        // 同じ寸法の写し(ヒープの外。参照 1 で返す)。
        internal static NativePixels Duplicate(NativePixels src) {
            var copy = NativePixels.Allocate(src.Width, src.Height);
            copy.CopyFrom(src);
            return copy;
        }

        // 比較の結果(指標・比較画像)とゾーンの一覧を結果へ書く(メインスレッド)。
        private static void FillResult(RecolorJob job, Comparison c) {
            var r = job.r;
            r.changedFraction = c.metrics.changedFraction;
            r.largestComponentFraction = c.metrics.largestComponentFraction;
            r.changeComponentCount = c.metrics.componentCount;
            r.preview = WriteRecolorPanel(c, r.texture, r.warnings);
            AddZoneInfos(r.zones, job.state, z => {
                if (job.tune != null && job.tune.How.TryGetValue(z.id ?? "", out var h)) {
                    return h;
                }
                return job.changedZoneIds.Contains(z.id) && !job.req.autoTune ? "skipped: autoTune=false" : "";
            }, out _);
        }

        // 編集状態のゾーンの一覧を結果の形で足す(並びは優先度の順。先頭が最も高い)。commonExcludedFraction は全ゾーン共通の除外の割合。
        private static void AddZoneInfos(List<ZoneInfo> dst, IrocaSessionState state, Func<ColorZone, string> autoTune,
            out float commonExcludedFraction) {
            commonExcludedFraction = 0f;
            var masks = new MaskWorkspace();
            try {
                if (state.maskState != null) {
                    masks.DecodeFrom(state.maskState);
                }
                commonExcludedFraction = FractionOf(masks.Common);
                foreach (var z in state.zones) {
                    if (z == null) {
                        continue;
                    }
                    dst.Add(new ZoneInfo {
                        zoneId = z.id,
                        name = z.name,
                        from = "#" + ColorUtility.ToHtmlStringRGB(z.sampleColor),
                        to = "#" + ColorUtility.ToHtmlStringRGB(z.targetColor),
                        at = z.sampleUV.x >= 0f ? new[] { z.sampleUV.x, z.sampleUV.y } : new float[0],
                        tolerance = z.tolerance,
                        enabled = z.enabled,
                        autoTune = autoTune != null ? autoTune(z) : "",
                        excludedFraction = FractionOf(masks.Layer(false, z.id)),
                        includedFraction = FractionOf(masks.Layer(true, z.id)),
                    });
                }
            } finally { masks.Clear(); }
        }

        // 立っている画素の割合(無ければ 0)。
        private static unsafe float FractionOf(NativeBitMask mask) {
            if (mask == null || mask.Width <= 0 || mask.Height <= 0) {
                return 0f;
            }
            long on = 0;
            ulong* bits = mask.Bits;
            for (int i = 0; i < mask.Words; i++) {
                on += Bits.PopCount(bits[i]);
            }
            return (float)on / mask.Length;
        }

        private static string WriteRecolorPanel(Comparison c, string texturePath, List<string> warnings) {
            if (c.panelPng == null) {
                warnings.Add($"preview failed: {c.panelError}");
                return "";
            }
            try {
                string file = AutomationOutputRules.PreviewPanelPath(texturePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                AtomicFile.WriteAllBytes(file, c.panelPng);
                return file;
            } catch (Exception ex) {
                warnings.Add($"preview failed: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        private static string FinishRecolor(RecolorTextureResult r, string error) {
            r.ok = string.IsNullOrEmpty(error);
            r.error = error ?? "";
            if (r.ok) {
                r.note = "Open 'preview' to check the result: left=before, middle=after, right=changed pixels (magenta). "
                    + "Magenta outside the intended part means over-selection; missing magenta inside it means it was missed. "
                    + "To retry a part, send a change with the same 'name' (it replaces that zone). "
                    + "To keep a same-colored part unpainted or paint only one part, use 'masks'. "
                    + "To see which zone paints a spot and why, call iroca_inspect with 'points'.";
                if (r.apply == ApplyAvatar) {
                    r.note += " The recolor is registered non-destructively and shows in the Scene view (NDMF preview); "
                        + "the user can undo it with Ctrl+Z, or fine-tune it in the Iroca window (iroca_open).";
                } else if (r.apply == ApplyPreview) {
                    r.note += " Nothing was saved (apply='preview').";
                }
            }
            return JsonUtility.ToJson(r);
        }
    }
}
