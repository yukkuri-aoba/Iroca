// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
// --oneshot(窓のワンショット)と、--autotune が通る自動調整の段取り(AutoTuneFlow)の口。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using UnityEngine;

namespace Iroca {
    internal static partial class Harness {
        // 提案の来ない源(--autotune: 証拠は先に渡し、漏れの確かめはしない)。
        private sealed class NoProposals : IProposalSource {
            public static readonly NoProposals Instance = new NoProposals();
            public AutoTuneProposal Propose(AutoTuneAsk ask, CancellationToken ct) => AutoTuneProposal.None;
        }

        /// <summary>
        /// --autotune の 1 ゾーン分: 渡された証拠(無ければ証拠なし)で、製品と同じ段取り(AutoTuneFlow)の解析を回して結果を返す。
        /// 漏れの確かめはしない(この口は前から確かめない)。解析の例外はそのまま投げる(前の口と同じ)。
        /// </summary>
        private static ZoneAutoTuner.TuneResult AutoTuneGiven(NativePixels source, ColorZone zone,
            bool[] evidence, int evW, int evH, bool[] excluded, int maskW, int maskH, SettingsCfg st, MaskSnapshot masks) {
            var exN = excluded != null ? NativeBools.CopyOf(excluded, maskW, maskH) : null;
            using var flow = new AutoTuneFlow(zone.Clone(), source, exN, maskW, maskH, st.ToRecolorSettings(), null,
                checkLeaks: false);
            var ev = evidence != null
                ? new AutoTuneProposal(NativeBools.CopyOf(evidence, evW, evH), evW, evH, false)
                : AutoTuneProposal.None;
            AutoTuneStep step;
            try {
                step = AutoTuneFlow.Drive(flow, flow.StartGiven(ev), NoProposals.Instance, CancellationToken.None);
            } finally {
                ev.Mask?.Release();
            }
            if (step != AutoTuneStep.Done) {
                if (flow.Error != null) {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(flow.Error).Throw();
                }
                throw new InvalidOperationException("自動調整が終わらなかった: " + flow.Failure);
            }
            return flow.Result;
        }

        // 自動調整に渡す除外: 共通(mask.raw) ∪ そのゾーンの除外。全部 0・全部 1 なら渡さない(--autotune と同じ。製品の解析も
        // 「マスクなし」とみなす)。
        private static bool[] AutoTuneExclusion(bool[] common, MaskSnapshot masks, ColorZone z) {
            bool[] excluded = common;
            if (masks.zones != null && masks.zones.TryGetValue(z.id, out var zoneExclude) && zoneExclude != null) {
                excluded = (bool[])common.Clone();
                for (int k = 0; k < excluded.Length; k++) {
                    if (zoneExclude.Get(k)) {
                        excluded[k] = true;
                    }
                }
            }
            int exCount = 0;
            for (int k = 0; k < excluded.Length; k++) {
                if (excluded[k]) {
                    exCount++;
                }
            }
            return exCount > 0 && exCount < excluded.Length ? excluded : null;
        }

        /// <summary>
        /// 自動調整の提案の源: 製品と同じ段取り(SamProposalFlow)と推論(onnxruntime)で 1 件ずつ作る。
        /// mirror(切り分け専用): 推論の側だけ画像と UV を上下反転して渡し、返った範囲を反転し直す。dev_safe の Python の写しが
        /// 自動調整・塗りを numpy の配列のまま(上下が鏡写しの世界で)回し、SAM だけを正しい向きで回していたのを再現する。
        /// そのとき UV は Python と同じく小数 9 桁に丸めてから渡す(Python は double で計算して 9 桁の文字列で渡していた)。
        /// </summary>
        private sealed class SamProposalSource : IProposalSource {
            private readonly OrtSamInference.Models _models;
            private readonly NativePixels _sam;
            private readonly bool _mirror;
            private readonly string _cache;
            private readonly float[] _givenFull;
            public readonly List<(AutoTuneAsk ask, SamProposalDiag diag, int count)> Asked = new List<(AutoTuneAsk, SamProposalDiag, int)>();
            public string FullEmbeddingOrigin;

            public SamProposalSource(OrtSamInference.Models models, NativePixels source, bool mirror, string cache, float[] givenFull) {
                _models = models;
                _mirror = mirror;
                _cache = cache;
                _givenFull = givenFull;
                _sam = mirror ? FlipRows(source) : source.AddRef();
            }

            public AutoTuneProposal Propose(AutoTuneAsk ask, CancellationToken ct) {
                float u = ask.U, v = ask.V;
                if (_mirror) {
                    u = Round9(u);
                    v = Round9(1.0 - v);
                }
                var ai = new OrtSamInference(_models, _sam, _cache, _givenFull);
                // 用途はどれも自動調整の証拠と同じ使い方(製品の AutoTuneService と同じ)。
                var p = SamProposalFlow.RunSynchronously(ai, _sam, "oneshot", u, v,
                    ask.Fine ? MaskSuggestGranularity.Fine : MaskSuggestGranularity.Auto, MaskSuggestUse.Evidence, ct, out var diag);
                FullEmbeddingOrigin ??= ai.FullEmbeddingOrigin;
                var mask = p.mask;
                p.mask = null;   // 範囲の参照は受け取った側へ渡す
                if (_mirror) {
                    var flipped = FlipMask(mask);
                    mask.Release();
                    mask = flipped;
                }
                Asked.Add((ask, diag, mask.CountTrue()));
                return new AutoTuneProposal(mask, p.width, p.height, p.floodWarning);
            }

            public void Release() => _sam.Release();

            static float Round9(double x) => float.Parse(x.ToString("F9", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            static float Round9(float x) => Round9((double)x);

            static unsafe NativePixels FlipRows(NativePixels src) {
                var dst = NativePixels.Allocate(src.Width, src.Height);
                int w = src.Width, h = src.Height;
                for (int y = 0; y < h; y++) {
                    Buffer.MemoryCopy(src.Ptr + (long)y * w, dst.Ptr + (long)(h - 1 - y) * w, (long)w * 4, (long)w * 4);
                }
                return dst;
            }

            static unsafe NativeBools FlipMask(NativeBools src) {
                var dst = NativeBools.AllocateUninitialized(src.Width, src.Height);
                int w = src.Width, h = src.Height;
                for (int y = 0; y < h; y++) {
                    Buffer.MemoryCopy(src.Ptr + (long)y * w, dst.Ptr + (long)(h - 1 - y) * w, w, w);
                }
                return dst;
            }
        }

        // ─── --oneshot ───
        // --oneshot <in.raw(下原点 RGBA)> <mask.raw(1=共通の除外)> <zones.json> <out.raw> --zone i
        //   [--sam-models dir] [--sam-cache dir] [--sam-threads n] [--sam-embedding full.f32] [--emit-dir dir] [--sam-mirror]
        // ゾーン i(スポイトの色 sample・スポイト位置 sampleUV・塗り先 target。ほかの項目は zones JSON に書いた値。窓の新しいゾーンの
        // 既定は呼ぶ側が書く)を、窓のワンショットと同じ段取りで自動調整する: AutoTuneFlow(証拠の提案 → 解析 → 漏れの確かめ。
        // 提案は SamProposalFlow + onnxruntime)→ TuneResult.ApplyTo → 外す範囲から「含める」を外して(DropIncluded)ゾーンの除外へ
        // 足す(右クリックの「ここは塗らない」と同じ) → 全ゾーンを塗る。漏れの確かめの選択は、渡したマスク(mask.raw と zones JSON の
        // ゾーン別マスク)と zones JSON の全体設定で回す(窓の今のマスク・設定と同じ扱い)。証拠の提案が背景まで広がったら証拠なしで
        // 解析し、sampleUV は残したまま塗る(製品の動き)。自動調整が失敗したら(証拠が来ないなど)ゾーンは変えずに塗る。
        // 出力: out.raw と、標準出力の 1 行 ONESHOT {json}(調整後のゾーンの全項目を zones JSON の名前で・method・provenance・
        // leak{checked, blobs, excludedParts, excludedPixels, skip, applied}・evidence{zoomed, crop, flood, used}・failure)。
        // --emit-dir: 証拠(evidence.raw)・漏れの塊ごとの提案(leak_<k>_<auto|fine>.raw)・外す範囲(exclusion.raw。含めるを
        // 外した後)・調整後の zones JSON(zones.json。そのまま --zones に渡せる。外す範囲は zone<i>_exclude.raw)・提案の診断(asks.json)。
        private static int RunOneshot(string[] args) {
            var inv = CultureInfo.InvariantCulture;
            var pos = new List<string>();
            int zi = 0;
            string models = OrtSamInference.DefaultModelsDirectory, cache = null, emit = null, embeddingPath = null;
            int threads = 0;
            bool mirror = false;
            for (int i = 1; i < args.Length; i++) {
                string a = args[i];
                string Next() {
                    if (i + 1 >= args.Length) {
                        throw new ArgumentException($"{a} の値がありません");
                    }
                    return args[++i];
                }
                switch (a) {
                    case "--zone": zi = int.Parse(Next(), inv); break;
                    case "--sam-models": models = Next(); break;
                    case "--sam-cache": cache = Next(); break;
                    case "--sam-threads": threads = int.Parse(Next(), inv); break;
                    case "--sam-embedding": embeddingPath = Next(); break;
                    case "--emit-dir": emit = Next(); break;
                    case "--sam-mirror": mirror = true; break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) {
                            Console.Error.WriteLine($"--oneshot: 知らない引数 {a}");
                            return 2;
                        }
                        pos.Add(a);
                        break;
                }
            }
            if (pos.Count != 4) {
                Console.Error.WriteLine("usage: --oneshot <in.raw> <mask.raw> <zones.json> <out.raw> --zone i [--sam-*] [--emit-dir dir] [--sam-mirror]");
                return 2;
            }
            if (emit != null) {
                Directory.CreateDirectory(emit);
            }
            OrtSamInference.Models m;
            try {
                m = OrtSamInference.Models.Load(models, threads);
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine(e.Message);
                return 3;
            }
            float[] givenFull = null;
            if (embeddingPath != null) {
                byte[] bytes = File.ReadAllBytes(embeddingPath);
                if (bytes.Length != SamProposalFlow.EmbeddingFloatCount * sizeof(float)) {
                    Console.Error.WriteLine($"--sam-embedding の大きさが違う: {bytes.Length}");
                    return 2;
                }
                givenFull = new float[SamProposalFlow.EmbeddingFloatCount];
                Buffer.BlockCopy(bytes, 0, givenFull, 0, bytes.Length);
            }

            var (w, h, pixels) = ReadRgbaRaw(pos[0]);
            var (mw, mh, common) = ReadMaskRaw(pos[1]);
            var masks = new MaskSnapshot { common = NativeBitMask.FromBools(common, mw, mh), width = mw, height = mh, zones = null };
            var (zoneList, st, zoneMasks, _) = LoadZones(pos[2]);
            AttachZoneMasks(masks, zoneMasks);
            if (zi < 0 || zi >= zoneList.Count) {
                Console.Error.WriteLine($"--zone {zi} がありません(ゾーン {zoneList.Count} 個)");
                return 2;
            }
            var z = zoneList[zi];
            bool[] excluded = AutoTuneExclusion(common, masks, z);
            var src = NativePixels.CopyOf(pixels, w, h);
            var sam = new SamProposalSource(m, src, mirror, cache, givenFull);
            var flow = new AutoTuneFlow(z.Clone(), src, excluded != null ? NativeBools.CopyOf(excluded, mw, mh) : null,
                excluded != null ? mw : 0, excluded != null ? mh : 0, st.ToRecolorSettings(), masks.AddRef());
            var leak = new Dictionary<string, object>();
            var evidence = new Dictionary<string, object>();
            string failure = null;
            try {
                var source = emit == null ? (IProposalSource)sam : new RecordingSource(sam, emit, w, h);
                var step = AutoTuneFlow.RunSynchronously(flow, source, CancellationToken.None);
                if (sam.Asked.Count > 0) {
                    var d = sam.Asked[0].diag;
                    evidence["zoomed"] = d.zoomed;
                    evidence["crop"] = d.zoomPlanned ? new[] { d.cropX0, d.cropY0, d.cropSide } : null;
                    evidence["flood"] = d.floodWarning;
                    evidence["count"] = sam.Asked[0].count;
                }
                evidence["had"] = flow.HadEvidence;
                evidence["used"] = step == AutoTuneStep.Done && flow.HadEvidence && flow.UsedEvidence;
                leak["checked"] = flow.LeakChecked;
                leak["blobs"] = flow.LeakBlobCount;
                leak["excludedParts"] = flow.LeakExcludedParts;
                leak["excludedPixels"] = flow.LeakExcludedPixels;
                leak["skip"] = flow.LeakSkipReason;
                leak["applied"] = false;
                if (step == AutoTuneStep.Done) {
                    flow.Result.ApplyTo(z);
                    var excl = flow.TakeLeakExclusion();
                    if (excl != null) {
                        try {
                            if (excl.Width != mw || excl.Height != mh) {
                                // 寸法の違うマスクへの写し(ProposalMaskRegion.ToMaskSize)は Unity 側にある(C7 で Core へ)。
                                leak["applied"] = "mask-size-mismatch";
                            } else {
                                NativeBitMask include = null;
                                masks.zoneIncludes?.TryGetValue(z.id, out include);
                                if (AutoTuneFlow.DropIncluded(excl, include)) {
                                    ApplyDontPaintHere(masks, z.id, excl, include);
                                    leak["applied"] = true;
                                    if (emit != null) {
                                        WriteMaskRaw(Path.Combine(emit, "exclusion.raw"), w, h, excl.ToArray());
                                    }
                                }
                            }
                        } finally {
                            excl.Release();
                        }
                    }
                } else {
                    failure = flow.Failure + (flow.Error != null ? ": " + flow.Error.Message : "");
                }
            } finally {
                flow.Dispose();
                sam.Release();
                src.Release();
            }

            ProcessZones(pixels, w, h, masks, zoneList, st);
            WriteRawRgba(pos[3], w, h, pixels);

            var zoneDoc = ZoneDocument(z);
            if (emit != null) {
                WriteTunedZones(pos[2], zi, z, masks, emit, w, h);
                File.WriteAllText(Path.Combine(emit, "asks.json"), JsonSerializer.Serialize(sam.Asked.ConvertAll(a => new Dictionary<string, object> {
                    ["kind"] = a.ask.Kind.ToString(),
                    ["blob"] = a.ask.Blob,
                    ["u"] = a.ask.U,
                    ["v"] = a.ask.V,
                    ["zoomed"] = a.diag.zoomed,
                    ["crop"] = a.diag.zoomPlanned ? new[] { a.diag.cropX0, a.diag.cropY0, a.diag.cropSide } : null,
                    ["flood"] = a.diag.floodWarning,
                    ["count"] = a.count,
                })));
            }
            var doc = new Dictionary<string, object> {
                ["zone"] = zoneDoc,
                ["method"] = flow.Method,
                ["provenance"] = flow.Provenance,
                ["conventionalReason"] = flow.ConventionalReason,
                ["leak"] = leak,
                ["evidence"] = evidence,
                ["failure"] = failure,
                ["fullEmbedding"] = sam.FullEmbeddingOrigin,
                ["mirror"] = mirror,
            };
            Console.WriteLine("ONESHOT " + JsonSerializer.Serialize(doc));
            return 0;
        }

        // 外す範囲をゾーンの除外へ足す(右クリックの「ここは塗らない」= MaskRegionOp.DontPaintHere と同じ: 範囲を除外へ足し、
        // 同じ範囲の「含める」を消す)。範囲は DropIncluded の後なので、含めるとは重ならない。
        private static unsafe void ApplyDontPaintHere(MaskSnapshot masks, string zoneId, NativeBools region, NativeBitMask include) {
            masks.zones ??= new Dictionary<string, NativeBitMask>();
            if (!masks.zones.TryGetValue(zoneId, out var ex) || ex == null) {
                ex = NativeBitMask.Allocate(masks.width, masks.height);
            } else {
                ex = ex.Writable();
            }
            masks.zones[zoneId] = ex;
            bool* r = region.Ptr;
            for (int i = 0; i < region.Length; i++) {
                if (r[i]) {
                    ex.Set(i, true);
                }
            }
            if (include != null) {
                var inc = include.Writable();
                masks.zoneIncludes[zoneId] = inc;
                for (int i = 0; i < region.Length; i++) {
                    if (r[i]) {
                        inc.Set(i, false);
                    }
                }
            }
        }

        // 調整後のゾーンを zones JSON の名前で(BuildZone の逆。マスクのパスは持たない)。
        private static Dictionary<string, object> ZoneDocument(ColorZone z) => new Dictionary<string, object> {
            ["name"] = z.name,
            ["sample"] = new[] { z.sampleColor.r, z.sampleColor.g, z.sampleColor.b },
            ["samples"] = z.extraSamples.ConvertAll(c => new[] { c.r, c.g, c.b }),
            ["target"] = new[] { z.targetColor.r, z.targetColor.g, z.targetColor.b },
            ["tolerance"] = z.tolerance,
            ["valueBlend"] = z.valueBlend,
            ["edgeSoftness"] = z.edgeSoftness,
            ["saturationStrictness"] = z.saturationStrictness,
            ["saturationGuard"] = z.saturationGuard,
            ["chromaThreshold"] = z.chromaThreshold,
            ["shadowDesaturation"] = z.shadowDesaturation,
            ["shadowForgivenessSatMin"] = z.shadowForgivenessSatMin,
            ["shadowValueFloor"] = z.shadowValueFloor,
            ["partSatCeiling"] = z.partSatCeiling,
            ["partHueBand"] = z.partHueBand,
            ["chromaCeiling"] = z.chromaCeiling,
            ["holeFillMargin"] = z.holeFillMargin,
            ["matchCutoff"] = z.matchCutoff,
            ["outputSaturation"] = z.outputSaturation,
            ["highlightRecovery"] = z.highlightRecovery,
            ["highlightBandExpand"] = z.highlightBandExpand,
            ["applyHighlightWash"] = z.applyHighlightWash,
            ["autoHighlightSample"] = z.autoHighlightSample,
            ["autoRecolorAnchor"] = z.autoRecolorAnchor,
            ["layerIndex"] = z.layerIndex,
            ["useFloodFill"] = z.useFloodFill,
            ["seedUV"] = z.seedUV.x >= 0f ? new[] { z.seedUV.x, z.seedUV.y } : null,
            ["sampleUV"] = z.HasSampleUV ? new[] { z.sampleUV.x, z.sampleUV.y } : null,
        };

        // 調整後の zones JSON(そのまま --zones に渡せる形)。元の JSON を読み直し、ゾーン i を調整後の値にする。外す範囲を足した
        // ゾーンの除外は zone<i>_exclude.raw に書いて指す。証拠のパスは外す(--autotune なしでは読まない)。
        private static void WriteTunedZones(string zonesPath, int zi, ColorZone z, MaskSnapshot masks, string dir, int w, int h) {
            var cfg = JsonSerializer.Deserialize<ZonesConfig>(File.ReadAllText(zonesPath), CaseInsensitiveJson);
            var c = cfg.zones[zi];
            c.sample = new[] { z.sampleColor.r, z.sampleColor.g, z.sampleColor.b };
            c.samples = z.extraSamples.Count > 0 ? z.extraSamples.ConvertAll(s => new[] { s.r, s.g, s.b }).ToArray() : null;
            c.tolerance = z.tolerance;
            c.valueBlend = z.valueBlend;
            c.edgeSoftness = z.edgeSoftness;
            c.saturationStrictness = z.saturationStrictness;
            c.saturationGuard = z.saturationGuard;
            c.chromaThreshold = z.chromaThreshold;
            c.shadowDesaturation = z.shadowDesaturation;
            c.shadowForgivenessSatMin = z.shadowForgivenessSatMin;
            c.shadowValueFloor = z.shadowValueFloor;
            c.partSatCeiling = z.partSatCeiling;
            c.partHueBand = z.partHueBand;
            c.chromaCeiling = z.chromaCeiling;
            c.holeFillMargin = z.holeFillMargin;
            c.matchCutoff = z.matchCutoff;
            c.outputSaturation = z.outputSaturation;
            c.highlightRecovery = z.highlightRecovery;
            c.highlightBandExpand = z.highlightBandExpand;
            c.applyHighlightWash = z.applyHighlightWash;
            c.autoHighlightSample = z.autoHighlightSample;
            c.autoRecolorAnchor = z.autoRecolorAnchor;
            c.evidenceMask = null;
            if (masks.zones != null && masks.zones.TryGetValue(z.id, out var ex) && ex != null) {
                string exPath = Path.Combine(dir, $"zone{zi}_exclude.raw");
                var bools = new bool[ex.Length];
                for (int i = 0; i < bools.Length; i++) {
                    bools[i] = ex.Get(i);
                }
                WriteMaskRaw(exPath, masks.width, masks.height, bools);
                c.excludeMask = Path.GetFullPath(exPath);
            }
            File.WriteAllText(Path.Combine(dir, "zones.json"), JsonSerializer.Serialize(cfg));
        }

        // 提案の源を包んで、届いた提案を --emit-dir へ書く(証拠は evidence.raw、漏れは leak_<k>_<auto|fine>.raw)。
        private sealed class RecordingSource : IProposalSource {
            private readonly IProposalSource _inner;
            private readonly string _dir;
            private readonly int _w, _h;

            public RecordingSource(IProposalSource inner, string dir, int w, int h) {
                _inner = inner;
                _dir = dir;
                _w = w;
                _h = h;
            }

            public AutoTuneProposal Propose(AutoTuneAsk ask, CancellationToken ct) {
                var p = _inner.Propose(ask, ct);
                if (p.Mask != null) {
                    string name = ask.Kind == AutoTuneAskKind.Evidence ? "evidence.raw"
                        : $"leak_{ask.Blob}_{(ask.Fine ? "fine" : "auto")}.raw";
                    WriteMaskRaw(Path.Combine(_dir, name), p.Mask.Width, p.Mask.Height, p.Mask.ToArray());
                }
                return p;
            }
        }
    }
}
