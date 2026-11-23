// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
// --oneshot(窓のワンショット)・--script(窓の操作の列)と、--autotune が通る自動調整の段取り(AutoTuneFlow)の口。
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

        /// <summary>
        /// AI 提案の源: 製品と同じ段取り(SamProposalFlow)と推論(onnxruntime)で 1 件ずつ作る。自動調整の提案の源(用途 Evidence)にも、
        /// 右クリックの提案(用途 Region)にも使う。
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
            // 自動調整の提案が届いたとき(--emit-dir が書く。範囲は読むだけ)。
            public Action<AutoTuneAsk, NativeBools> OnAutoTuneProposal;

            public SamProposalSource(OrtSamInference.Models models, NativePixels source, bool mirror, string cache, float[] givenFull) {
                _models = models;
                _mirror = mirror;
                _cache = cache;
                _givenFull = givenFull;
                _sam = mirror ? FlipRows(source) : source.AddRef();
            }

            /// <summary>1 件(範囲の参照ごと渡す。受け取った側が Release)。u, v は下原点(mirror なら鏡写しの世界の UV)。</summary>
            public MaskSuggestProposal Propose(float u, float v, MaskSuggestGranularity g, MaskSuggestUse use,
                                               CancellationToken ct, out SamProposalDiag diag) {
                if (_mirror) {
                    u = Round9(u);
                    v = Round9(1.0 - v);
                }
                var ai = new OrtSamInference(_models, _sam, _cache, _givenFull);
                var p = SamProposalFlow.RunSynchronously(ai, _sam, "harness", u, v, g, use, ct, out diag);
                FullEmbeddingOrigin ??= ai.FullEmbeddingOrigin;
                if (_mirror) {
                    var flipped = FlipMask(p.mask);
                    p.mask.Release();
                    p.mask = flipped;
                }
                return p;
            }

            public AutoTuneProposal Propose(AutoTuneAsk ask, CancellationToken ct) {
                // 用途はどれも自動調整の証拠と同じ使い方(製品の AutoTuneService と同じ)。
                var p = Propose(ask.U, ask.V, ask.Fine ? MaskSuggestGranularity.Fine : MaskSuggestGranularity.Auto,
                    MaskSuggestUse.Evidence, ct, out var diag);
                var mask = p.mask;
                p.mask = null;   // 範囲の参照は受け取った側へ渡す
                Asked.Add((ask, diag, mask.CountTrue()));
                OnAutoTuneProposal?.Invoke(ask, mask);
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

        // 推論の設定(--sam-*。--oneshot と --script で共通)。
        private sealed class SamOptions {
            public string Models = OrtSamInference.DefaultModelsDirectory, Cache, EmbeddingPath, MaskPixelsPath;
            public int Threads;
            public bool Mirror;

            // 推論の引数なら読んで true(i は値の分だけ進める)。
            public bool TryParse(string[] args, ref int i) {
                var inv = CultureInfo.InvariantCulture;
                string a = args[i];
                if (a == "--sam-mirror") {
                    Mirror = true;
                    return true;
                }
                if (a != "--sam-models" && a != "--sam-cache" && a != "--sam-threads" && a != "--sam-embedding" && a != "--mask-pixels") {
                    return false;
                }
                if (i + 1 >= args.Length) {
                    throw new ArgumentException($"{a} の値がありません");
                }
                string value = args[++i];
                switch (a) {
                    case "--sam-models": Models = value; break;
                    case "--sam-cache": Cache = value; break;
                    case "--sam-threads": Threads = int.Parse(value, inv); break;
                    case "--sam-embedding": EmbeddingPath = value; break;
                    default: MaskPixelsPath = value; break;
                }
                return true;
            }

            public float[] ReadEmbedding() {
                if (EmbeddingPath == null) {
                    return null;
                }
                byte[] bytes = File.ReadAllBytes(EmbeddingPath);
                if (bytes.Length != SamProposalFlow.EmbeddingFloatCount * sizeof(float)) {
                    throw new InvalidDataException($"--sam-embedding の大きさが違う: {bytes.Length}");
                }
                var full = new float[SamProposalFlow.EmbeddingFloatCount];
                Buffer.BlockCopy(bytes, 0, full, 0, bytes.Length);
                return full;
            }
        }

        /// <summary>
        /// 窓の編集の状態(原本・ゾーン・全体設定・編集中のマスク)と、窓と同じ部品で回す操作(スポイト・塗り先・自動調整・右クリックの
        /// 範囲の操作・許容範囲・塗る)。--oneshot と --script が使う。
        /// </summary>
        private sealed unsafe class EditSession : IDisposable {
            public readonly int W, H;
            public readonly Color32[] Original;
            public readonly NativePixels Source;
            public readonly List<ColorZone> Zones;
            public readonly SettingsCfg Settings;
            public readonly MaskWorkspace Masks = new MaskWorkspace();
            // マスクの寸法の画素(寸法が原本と違うとき、提案を写した後に AA 包含をやり直すのに読む。製品の窓の SourceTexture)。
            public NativePixels MaskPixels;
            public SamProposalSource Sam;

            public EditSession(Color32[] pixels, int w, int h, List<ColorZone> zones, SettingsCfg settings,
                               bool[] common, int mw, int mh, List<(string zoneId, string path, bool include)> zoneMasks) {
                W = w;
                H = h;
                Original = pixels;
                Source = NativePixels.CopyOf(pixels, w, h);
                Zones = zones;
                Settings = settings;
                Masks.Reset(mw, mh);
                // 共通の除外は、立っている画素があるときだけ置く(窓は塗るまで共通の層を持たない)。
                if (common != null && Array.IndexOf(common, true) >= 0) {
                    Masks.SetCommon(NativeBitMask.FromBools(common, mw, mh));
                }
                if (zoneMasks != null) {
                    foreach (var (zoneId, path, include) in zoneMasks) {
                        var (iw, ih, ibytes) = ReadRaw(path, 1);
                        if (iw != mw || ih != mh) {
                            throw new InvalidOperationException($"ゾーン別マスク寸法 {iw}x{ih} が mask.raw 寸法 {mw}x{mh} と不一致: {path}");
                        }
                        Masks.SetLayer(include, zoneId, NativeBitMask.FromBools(ToBoolMask(ibytes, iw * ih), iw, ih));
                    }
                }
            }

            /// <summary>
            /// 自動調整(窓の「自動調整」と同じ: AutoTuneService と同じ入力 — 除外は共通 ∪ そのゾーン、全体設定、今のマスクの
            /// スナップショット — で AutoTuneFlow を回し、結果をゾーンへ当て、外す範囲を窓の LeakRegionFor と同じ手順で
            /// 「ここは塗らない」として書く)。返すのは診断(ONESHOT・SCRIPT の行)。
            /// </summary>
            public Dictionary<string, object> AutoTune(ColorZone z) {
                var leak = new Dictionary<string, object>();
                var evidence = new Dictionary<string, object>();
                int asked0 = Sam.Asked.Count;
                var excluded = Masks.CombinedExclusion(z.id);
                var flow = new AutoTuneFlow(z.Clone(), Source, excluded, Masks.Width, Masks.Height,
                    Settings.ToRecolorSettings(), Masks.ToSnapshot());
                string failure = null;
                string evidenceDiag = null;
                try {
                    var step = AutoTuneFlow.RunSynchronously(flow, Sam, CancellationToken.None);
                    if (step == AutoTuneStep.Done) {
                        // 導出の診断(製品の TuneResult.evidenceDiag そのまま。証拠を使えず従来の導出へ戻ったら "fallback …"、
                        // 証拠なしの解析なら空)。provenance の文字列にも入っているが、切り出さずに読めるよう単独で出す。
                        evidenceDiag = flow.Result.evidenceDiag;
                    }
                    if (Sam.Asked.Count > asked0) {
                        var d = Sam.Asked[asked0].diag;
                        evidence["zoomed"] = d.zoomed;
                        evidence["crop"] = d.zoomPlanned ? new[] { d.cropX0, d.cropY0, d.cropSide } : null;
                        evidence["flood"] = d.floodWarning;
                        evidence["count"] = Sam.Asked[asked0].count;
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
                                leak["applied"] = ApplyLeakRegion(z, excl, out int changed);
                                leak["changed"] = changed;
                            } finally {
                                excl.Release();
                            }
                        }
                    } else {
                        failure = flow.Failure + (flow.Error != null ? ": " + flow.Error.Message : "");
                    }
                } finally {
                    flow.Dispose();
                }
                return new Dictionary<string, object> {
                    ["zone"] = ZoneDocument(z),
                    ["method"] = flow.Method,
                    ["provenance"] = flow.Provenance,
                    ["conventionalReason"] = flow.ConventionalReason,
                    ["leak"] = leak,
                    ["evidence"] = evidence,
                    ["failure"] = failure,
                    ["evidenceDiag"] = evidenceDiag,
                };
            }

            // 窓の AutoTuneService.Run.LeakRegionFor + ApplyRegion(DontPaintHere): マスクの寸法へ写し(寸法が違えば被覆を保って写し、
            // マスクの寸法の画素があれば AA 包含をやり直す)、そのゾーンの「含める」の画素を外し、残れば「ここは塗らない」で書く。
            private bool ApplyLeakRegion(ColorZone z, NativeBools excl, out int changed) {
                changed = 0;
                var region = ToMaskSize(excl, out bool owned);
                if (!owned) {
                    region = region.Clone();   // 自分の範囲は書き換えない
                }
                try {
                    if (!AutoTuneFlow.DropIncluded(region, Masks.Layer(true, z.id))) {
                        return false;
                    }
                    bool ok = Masks.ApplyRegion(region.Ptr, z.id, MaskRegionOp.DontPaintHere, null, out var result);
                    changed = result.changed;
                    return ok;
                } finally {
                    region.Release();
                }
            }

            // 提案をマスクの寸法の範囲に(窓の ProposalMaskRegion と同じ規則。画素はマスクの寸法の画素 = --mask-pixels)。
            private NativeBools ToMaskSize(NativeBools src, out bool owned)
                => ProposalMaskTransfer.ToMaskSize(src, Masks.Width, Masks.Height,
                    MaskPixels != null ? MaskPixels.Width : 0, MaskPixels != null ? MaskPixels.Height : 0,
                    MaskPixels != null ? () => MaskPixels.AddRef() : null, out owned);

            /// <summary>
            /// 右クリックの範囲の操作(窓の MaskSuggestController.CommitProposalToMask と同じ): 用途 Region の AI 提案を取り、
            /// 「ここも塗る」(粒度 自動)なら押した所の色に近い画素へ絞るかを決めて絞り(PaintHereNarrowing。窓はプレビューの選択の
            /// 控えから、ここは今のマスクでゾーンの選択を回して強さを読む)、マスクの寸法の範囲にし、空なら当てない。
            /// zoneId = null は全ゾーン共通(「塗らない」「消す」だけ)。
            /// </summary>
            public Dictionary<string, object> Region(string zoneId, float u, float v, MaskSuggestGranularity g, MaskRegionOp op) {
                var p = Sam.Propose(u, v, g, MaskSuggestUse.Region, CancellationToken.None, out var diag);
                NativeBools proposal = null;
                bool ownProposal = false;
                try {
                    return RegionApply(p, diag, zoneId, u, v, g, op, ref proposal, ref ownProposal);
                } finally {
                    if (ownProposal) {
                        proposal.Release();
                    }
                    p.Release();
                }
            }

            // Region の続き(提案 p を絞って当てる)。絞った範囲は proposal に置く(ownProposal なら呼び出し側が放す)。
            private Dictionary<string, object> RegionApply(MaskSuggestProposal p, SamProposalDiag diag, string zoneId,
                float u, float v, MaskSuggestGranularity g, MaskRegionOp op, ref NativeBools proposal, ref bool ownProposal) {
                var zone = zoneId == null ? null : Zones.Find(x => x != null && x.id == zoneId);
                int proposalBeforeColor = p.mask.CountTrue();
                PaintHereNarrowing.Outcome narrowing;
                var snap = Masks.ToSnapshot();
                try {
                    proposal = PaintHereNarrowing.NarrowBySelection(p.mask, Source, u, v, op, g == MaskSuggestGranularity.Auto,
                        zone, Settings.ToRecolorSettings(), snap, out ownProposal, out narrowing);
                } finally {
                    snap.Release();
                }
                int proposalCount = ownProposal ? proposal.CountTrue() : proposalBeforeColor;
                var doc = new Dictionary<string, object> {
                    // 押した所(5×5)がもうそのゾーンで塗れていたか(「ここも塗る」・粒度 自動のときだけ見る)
                    ["clickPainted"] = narrowing.clickPainted,
                    ["zoomed"] = diag.zoomed,
                    ["crop"] = diag.zoomPlanned ? new[] { diag.cropX0, diag.cropY0, diag.cropSide } : null,
                    ["flood"] = p.floodWarning,
                    ["proposalCount"] = proposalCount,
                    // 色で絞る前後の画素数(絞らない操作・粒度では同じ)と、絞って外した画素数
                    ["proposalBeforeColor"] = proposalBeforeColor,
                    ["colorDropped"] = narrowing.dropped,
                    // 絞った理由(painted = 押した所が塗れていた・minority = 押した色が足す画素の少数派。絞らなければ null)
                    ["narrowed"] = narrowing.reason == PaintHereNarrowing.Reason.ClickPainted ? "painted"
                        : narrowing.reason == PaintHereNarrowing.Reason.Minority ? "minority" : null,
                    // 範囲のうちまだ塗れていない画素・そのうち押した色に近い画素・その割合(診断。強さを読めなければ null)
                    ["unpaintedPixels"] = narrowing.measured ? narrowing.unpainted : (int?)null,
                    ["unpaintedNearPixels"] = narrowing.measured ? narrowing.unpaintedNear : (int?)null,
                    ["nearFraction"] = narrowing.measured ? narrowing.NearFraction : (float?)null,
                };
                var region = ToMaskSize(proposal, out bool owned);
                try {
                    int regionCount = region.CountTrue();
                    bool any = regionCount > 0;
                    doc["empty"] = !any;
                    // マスクの寸法にした範囲の画素数(当てる範囲。寸法が原本と同じなら proposalCount と同じ)。
                    doc["regionCount"] = regionCount;
                    if (!any) {
                        // 空の提案で「この部分だけ」を当てると全体が除外になるので、当てない(窓も同じ)
                        doc["applied"] = false;
                        doc["skip"] = "empty";
                        return doc;
                    }
                    bool ok = Masks.ApplyRegion(region.Ptr, zoneId, op, null, out var result);
                    doc["applied"] = ok;
                    doc["changed"] = result.changed;
                    doc["commonOverlap"] = result.commonOverlap;
                    // 当てなかった・何も変わらなかった理由(窓の知らせ MaskPaintView.DescribeRegionEdit と同じ分け方)。
                    // 背景まで広がった提案(flood)も窓は当てる(知らせるだけ)ので、理由にはしない。
                    doc["skip"] = !ok ? "rejected" : result.changed == 0 ? "no-change" : null;
                    return doc;
                } finally {
                    if (owned) {
                        region.Release();
                    }
                }
            }

            /// <summary>全ゾーンを今のマスクで塗った出力(原本の写しへ塗る)。</summary>
            public Color32[] Paint() {
                var px = (Color32[])Original.Clone();
                var snap = Masks.ToSnapshot();
                try {
                    ProcessZones(px, W, H, snap, Zones, Settings);
                } finally {
                    snap.Release();
                }
                return px;
            }

            /// <summary>
            /// 今の状態を、そのまま --zones・mask.raw に渡せる形で書く(prefix + "zones.json"・"mask.raw"・ゾーンごとの
            /// "zone{i}_exclude.raw"/"zone{i}_include.raw")。
            /// </summary>
            public void WriteState(string dir, string prefix) {
                var cfg = new ZonesConfig { settings = Settings };
                WriteLayer(Path.Combine(dir, prefix + "mask.raw"), Masks.Common);
                for (int i = 0; i < Zones.Count; i++) {
                    var z = Zones[i];
                    var c = ZoneToCfg(z);
                    var ex = Masks.Layer(false, z.id);
                    if (ex != null) {
                        c.excludeMask = Path.GetFullPath(Path.Combine(dir, $"{prefix}zone{i}_exclude.raw"));
                        WriteLayer(c.excludeMask, ex);
                    }
                    var inc = Masks.Layer(true, z.id);
                    if (inc != null) {
                        c.includeMask = Path.GetFullPath(Path.Combine(dir, $"{prefix}zone{i}_include.raw"));
                        WriteLayer(c.includeMask, inc);
                    }
                    cfg.zones.Add(c);
                }
                File.WriteAllText(Path.Combine(dir, prefix + "zones.json"), JsonSerializer.Serialize(cfg));
            }

            // 層を mask.raw の形で書く(null は全部 0)。
            private void WriteLayer(string path, NativeBitMask layer) {
                var bools = new bool[Masks.Width * Masks.Height];
                if (layer != null) {
                    for (int i = 0; i < bools.Length; i++) {
                        bools[i] = layer.Get(i);
                    }
                }
                WriteMaskRaw(path, Masks.Width, Masks.Height, bools);
            }

            public void Dispose() {
                Sam?.Release();
                Source.Release();
                MaskPixels?.Release();
                Masks.Clear();
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

        // ゾーンを zones JSON の項目へ(BuildZone の逆。マスクのパスは呼ぶ側が足す)。
        private static ZoneCfg ZoneToCfg(ColorZone z) => new ZoneCfg {
            name = z.name,
            sample = new[] { z.sampleColor.r, z.sampleColor.g, z.sampleColor.b },
            samples = z.extraSamples.Count > 0 ? z.extraSamples.ConvertAll(s => new[] { s.r, s.g, s.b }).ToArray() : null,
            target = new[] { z.targetColor.r, z.targetColor.g, z.targetColor.b },
            tolerance = z.tolerance,
            valueBlend = z.valueBlend,
            edgeSoftness = z.edgeSoftness,
            saturationStrictness = z.saturationStrictness,
            saturationGuard = z.saturationGuard,
            chromaThreshold = z.chromaThreshold,
            shadowDesaturation = z.shadowDesaturation,
            shadowForgivenessSatMin = z.shadowForgivenessSatMin,
            shadowValueFloor = z.shadowValueFloor,
            partSatCeiling = z.partSatCeiling,
            partHueBand = z.partHueBand,
            chromaCeiling = z.chromaCeiling,
            holeFillMargin = z.holeFillMargin,
            matchCutoff = z.matchCutoff,
            outputSaturation = z.outputSaturation,
            highlightRecovery = z.highlightRecovery,
            highlightBandExpand = z.highlightBandExpand,
            applyHighlightWash = z.applyHighlightWash,
            autoHighlightSample = z.autoHighlightSample,
            autoRecolorAnchor = z.autoRecolorAnchor,
            layerIndex = z.layerIndex,
            useFloodFill = z.useFloodFill,
            seedUV = z.seedUV.x >= 0f ? new[] { z.seedUV.x, z.seedUV.y } : null,
            sampleUV = z.HasSampleUV ? new[] { z.sampleUV.x, z.sampleUV.y } : null,
        };

        private static OrtSamInference.Models LoadModels(SamOptions o) => OrtSamInference.Models.Load(o.Models, o.Threads);

        // ─── --oneshot ───
        // --oneshot <in.raw(下原点 RGBA)> <mask.raw(1=共通の除外)> <zones.json> <out.raw> --zone i
        //   [--sam-models dir] [--sam-cache dir] [--sam-threads n] [--sam-embedding full.f32] [--mask-pixels raw] [--emit-dir dir] [--sam-mirror]
        // ゾーン i(スポイトの色 sample・スポイト位置 sampleUV・塗り先 target。ほかの項目は zones JSON に書いた値。窓の新しいゾーンの
        // 既定は呼ぶ側が書く)を、窓のワンショットと同じ段取りで自動調整する(EditSession.AutoTune: AutoTuneFlow → TuneResult.ApplyTo →
        // 外す範囲を窓の LeakRegionFor と同じ手順で「ここは塗らない」へ) → 全ゾーンを塗る。マスクは編集中のマスク(MaskWorkspace)として
        // 持ち、自動調整・塗りへは窓と同じスナップショットで渡す。マスクの寸法が原本と違えば、提案は被覆を保って写す(--mask-pixels で
        // マスクの寸法の画素を渡すと AA 包含もやり直す。窓の SourceTexture)。自動調整が失敗したら(証拠が来ないなど)ゾーンは変えずに塗る。
        // 出力: out.raw と、標準出力の 1 行 ONESHOT {json}(調整後のゾーンの全項目を zones JSON の名前で・method・provenance・
        // leak{checked, blobs, excludedParts, excludedPixels, skip, applied, changed}・evidence{zoomed, crop, flood, used}・failure・
        // evidenceDiag(製品の TuneResult.evidenceDiag そのまま。証拠なしの解析なら空、終わらなければ null))。
        // ゾーンにスポイト位置(sampleUV)が無ければ、製品どおり証拠なしの自動調整になる(AI に頼まない)。
        // --emit-dir: 証拠(evidence.raw)・漏れの塊ごとの提案(leak_<k>_<auto|fine>.raw)・調整後の状態(zones.json・mask.raw・
        // zone<i>_exclude.raw ほか。そのまま --zones に渡せる)・提案の診断(asks.json)。
        private static int RunOneshot(string[] args) {
            var inv = CultureInfo.InvariantCulture;
            var pos = new List<string>();
            int zi = 0;
            string emit = null;
            var o = new SamOptions();
            for (int i = 1; i < args.Length; i++) {
                if (o.TryParse(args, ref i)) {
                    continue;
                }
                string a = args[i];
                if (a == "--zone" && i + 1 < args.Length) {
                    zi = int.Parse(args[++i], inv);
                } else if (a == "--emit-dir" && i + 1 < args.Length) {
                    emit = args[++i];
                } else if (a.StartsWith("--", StringComparison.Ordinal)) {
                    Console.Error.WriteLine($"--oneshot: 知らない引数 {a}");
                    return 2;
                } else {
                    pos.Add(a);
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
                m = LoadModels(o);
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine(e.Message);
                return 3;
            }
            var (w, h, pixels) = ReadRgbaRaw(pos[0]);
            var (mw, mh, common) = ReadMaskRaw(pos[1]);
            var (zoneList, st, zoneMasks, _) = LoadZones(pos[2]);
            if (zi < 0 || zi >= zoneList.Count) {
                Console.Error.WriteLine($"--zone {zi} がありません(ゾーン {zoneList.Count} 個)");
                return 2;
            }
            using var s = new EditSession(pixels, w, h, zoneList, st, common, mw, mh, zoneMasks);
            if (o.MaskPixelsPath != null) {
                var (pw, ph, mp) = ReadRgbaRaw(o.MaskPixelsPath);
                s.MaskPixels = NativePixels.CopyOf(mp, pw, ph);
            }
            s.Sam = new SamProposalSource(m, s.Source, o.Mirror, o.Cache, o.ReadEmbedding());
            if (emit != null) {
                s.Sam.OnAutoTuneProposal = (ask, mask) => {
                    string name = ask.Kind == AutoTuneAskKind.Evidence ? "evidence.raw"
                        : $"leak_{ask.Blob}_{(ask.Fine ? "fine" : "auto")}.raw";
                    WriteMaskRaw(Path.Combine(emit, name), mask.Width, mask.Height, mask.ToArray());
                };
            }
            var doc = s.AutoTune(zoneList[zi]);
            WriteRawRgba(pos[3], w, h, s.Paint());
            if (emit != null) {
                s.WriteState(emit, "");
                File.WriteAllText(Path.Combine(emit, "asks.json"), JsonSerializer.Serialize(AsksDocument(s.Sam)));
            }
            doc["fullEmbedding"] = s.Sam.FullEmbeddingOrigin;
            doc["mirror"] = o.Mirror;
            Console.WriteLine("ONESHOT " + JsonSerializer.Serialize(doc));
            return 0;
        }

        private static List<Dictionary<string, object>> AsksDocument(SamProposalSource sam) => sam.Asked.ConvertAll(a => new Dictionary<string, object> {
            ["kind"] = a.ask.Kind.ToString(),
            ["blob"] = a.ask.Blob,
            ["u"] = a.ask.U,
            ["v"] = a.ask.V,
            ["zoomed"] = a.diag.zoomed,
            ["crop"] = a.diag.zoomPlanned ? new[] { a.diag.cropX0, a.diag.cropY0, a.diag.cropSide } : null,
            ["flood"] = a.diag.floodWarning,
            ["count"] = a.count,
        });

        // ─── --script ───
        // --script <scenario.json> <in.raw(下原点 RGBA)> <out-dir> [--sam-models dir] [--sam-cache dir] [--sam-threads n]
        //   [--sam-embedding full.f32] [--mask-pixels raw] [--sam-mirror]
        // 窓の操作の列を、窓と同じ部品(ZoneEdits・AutoTuneFlow・SamProposalFlow・ProposalMaskTransfer・MaskWorkspace・
        // ColorZone.SetToleranceManually)で再生する。scenario.json:
        //   { "zones": [zones JSON の項目…](省略可。始めのゾーン), "settings": {…}(省略可), "mask": "<mask.raw>"(省略可。共通の除外),
        //     "emit": "all"|"last"|"none"(既定 all), "steps": [手…] }
        // 手(UV は下原点。--sam-mirror のときは鏡写しの世界の UV):
        //   {"op":"eyedropper","uv":[u,v]}                 新しいゾーン(窓のスポイトと同じ: クリックした 1 画素の色・位置。既定は ZoneEdits.NewZone)
        //   {"op":"eyedropper","uv":[u,v],"new":false}     今のゾーンの色を取り直す(EditingSession.ApplySample と同じ。色が変われば島の値を戻す)
        //   {"op":"target","rgb":[r,g,b]}                  塗り先(0..1)
        //   {"op":"autotune"}                              自動調整(--oneshot と同じ)
        //   {"op":"region","uv":[u,v],"gran":"auto|fine|coarse","action":"PaintHere|DontPaintHere|OnlyThisPart|ClearHere","common":false}
        //                                                  右クリックの範囲の操作(用途 Region の AI 提案)。common=true は全ゾーン共通(塗らない・消す)
        //   {"op":"tolerance","value":t}                   許容範囲のスライダー(ColorZone.SetToleranceManually)
        //   {"op":"zone","index":i}                        操作するゾーンを選ぶ
        //   {"op":"seed","uv":[u,v]}                       今のゾーンに連続領域のシードを置く(窓の「指定」+クリック・Shift+クリック。ZoneEdits)
        //   {"op":"seed","clear":true}                     シードを外す(窓の「クリア」)。どちらも連続領域モードのゾーンだけ(でなければエラー)
        //   {"op":"set","zone":{項目:値…},"settings":{項目:値…}}
        //                                                  今のゾーンの項目・処理の設定を窓の欄と同じ経路で書く(項目名は zones JSON・
        //                                                  settings JSON。sample は色の欄 = スポイト位置が外れる、tolerance は手の許容範囲、
        //                                                  target は塗り先の欄)。書く順は JSON の順。窓に欄の無い項目・知らない項目・
        //                                                  範囲の外・窓で押せない状態はエラー
        // 自動調整は、ゾーンにスポイト位置(sampleUV)が無ければ製品どおり証拠なし(AI に頼まない。理由 no-sample-position)で回る。
        // 位置の無いゾーンは、zones に sampleUV を書かずに渡すか、set の sample(色の欄)で作る。
        // 出力: 手ごとに標準出力の 1 行 SCRIPT {"step":k,"op":…,…}、emit が all なら手ごとに out-dir/step<k>_out.raw と
        // step<k>_zones.json(・step<k>_mask.raw ほか。そのまま --zones に渡せる)、last なら最後の手だけ。
        // 行の項目: autotune は ONESHOT と同じ(evidenceDiag も)。region は zoomed・crop・flood・proposalCount・
        // clickPainted(押した所がもうそのゾーンで塗れていたか)・proposalBeforeColor・colorDropped(「ここも塗る」(粒度 自動)で
        // 右クリックした所の色に近い画素へ絞る前の画素数と外した画素数)・narrowed(絞った理由: painted = 押した所が塗れていた・
        // minority = 押した色が足す画素の少数派・null = 絞らない)・unpaintedPixels・unpaintedNearPixels・nearFraction(範囲のうち
        // まだ塗れていない画素・そのうち押した色に近い画素・その割合。「ここも塗る」(粒度 自動)のときだけ。PaintHereNarrowing)・
        // empty・regionCount(マスクの寸法にした範囲の画素数)・applied・changed・commonOverlap・skip(当てなかった・変わらなかった理由:
        // empty・rejected・no-change。当てて変わったら null)。seed は changed・seedUV・zone。set は keys・zone。
        private static int RunScript(string[] args) {
            var inv = CultureInfo.InvariantCulture;
            var pos = new List<string>();
            var o = new SamOptions();
            for (int i = 1; i < args.Length; i++) {
                if (o.TryParse(args, ref i)) {
                    continue;
                }
                if (args[i].StartsWith("--", StringComparison.Ordinal)) {
                    Console.Error.WriteLine($"--script: 知らない引数 {args[i]}");
                    return 2;
                }
                pos.Add(args[i]);
            }
            if (pos.Count != 3) {
                Console.Error.WriteLine("usage: --script <scenario.json> <in.raw> <out-dir> [--sam-*] [--mask-pixels raw] [--sam-mirror]");
                return 2;
            }
            string outDir = pos[2];
            Directory.CreateDirectory(outDir);
            using var docJson = JsonDocument.Parse(File.ReadAllText(pos[0]));
            var root = docJson.RootElement;
            var (w, h, pixels) = ReadRgbaRaw(pos[1]);
            bool[] common = null;
            int mw = w, mh = h;
            if (root.TryGetProperty("mask", out var maskEl) && maskEl.ValueKind == JsonValueKind.String) {
                (mw, mh, common) = ReadMaskRaw(maskEl.GetString());
            }
            var cfg = new ZonesConfig();
            if (root.TryGetProperty("zones", out var zonesEl)) {
                cfg.zones = JsonSerializer.Deserialize<List<ZoneCfg>>(zonesEl.GetRawText(), CaseInsensitiveJson);
            }
            if (root.TryGetProperty("settings", out var settingsEl)) {
                cfg.settings = JsonSerializer.Deserialize<SettingsCfg>(settingsEl.GetRawText(), CaseInsensitiveJson);
            }
            var (zoneList, st, zoneMasks, _) = LoadZones(cfg);
            string emit = root.TryGetProperty("emit", out var emitEl) ? emitEl.GetString() : "all";
            var steps = new List<JsonElement>();
            if (root.TryGetProperty("steps", out var stepsEl)) {
                foreach (var e in stepsEl.EnumerateArray()) {
                    steps.Add(e);
                }
            }
            OrtSamInference.Models m;
            try {
                m = LoadModels(o);
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine(e.Message);
                return 3;
            }
            using var s = new EditSession(pixels, w, h, zoneList, st, common, mw, mh, zoneMasks);
            if (o.MaskPixelsPath != null) {
                var (pw, ph, mp) = ReadRgbaRaw(o.MaskPixelsPath);
                s.MaskPixels = NativePixels.CopyOf(mp, pw, ph);
            }
            s.Sam = new SamProposalSource(m, s.Source, o.Mirror, o.Cache, o.ReadEmbedding());
            int current = zoneList.Count - 1;
            for (int k = 0; k < steps.Count; k++) {
                var e = steps[k];
                string op = e.GetProperty("op").GetString();
                var line = new Dictionary<string, object> { ["step"] = k, ["op"] = op };
                ColorZone z = current >= 0 && current < zoneList.Count ? zoneList[current] : null;
                switch (op) {
                    case "eyedropper": {
                            var uv = ReadUv(e.GetProperty("uv"));
                            var picked = ZoneEdits.SampleColorAt(s.Source, uv.x, uv.y);
                            bool isNew = !e.TryGetProperty("new", out var nEl) || nEl.GetBoolean();
                            if (isNew || z == null) {
                                // 窓のスポイトで新しいゾーンを作る(名前は窓の番号の規則の英語の形。名前は出力に関わらない)。
                                z = ZoneEdits.NewZone(NextZoneName(zoneList));
                                ZoneEdits.SetNewSample(z, picked, uv);
                                zoneList.Add(z);
                                current = zoneList.Count - 1;
                                line["created"] = true;
                            } else {
                                bool changed = ZoneEdits.SampleColorChanges(z, picked);
                                ZoneEdits.ApplySample(z, picked, uv, changed);
                                line["colorChanged"] = changed;
                            }
                            line["sample"] = new[] { picked.r, picked.g, picked.b };
                            line["zone"] = current;
                            break;
                        }
                    case "target": {
                            RequireZone(z, op);
                            var rgb = e.GetProperty("rgb");
                            ZoneEdits.SetTarget(z, new Color(rgb[0].GetSingle(), rgb[1].GetSingle(), rgb[2].GetSingle(), 1f));
                            break;
                        }
                    case "autotune": {
                            RequireZone(z, op);
                            foreach (var kv in s.AutoTune(z)) {
                                line[kv.Key] = kv.Value;
                            }
                            break;
                        }
                    case "region": {
                            var uv = ReadUv(e.GetProperty("uv"));
                            var g = ParseGranularity(e.TryGetProperty("gran", out var gEl) ? gEl.GetString() : "auto");
                            var action = (MaskRegionOp)Enum.Parse(typeof(MaskRegionOp), e.GetProperty("action").GetString());
                            bool toCommon = e.TryGetProperty("common", out var cEl) && cEl.GetBoolean();
                            if (!toCommon) {
                                RequireZone(z, op);
                            }
                            foreach (var kv in s.Region(toCommon ? null : z.id, uv.x, uv.y, g, action)) {
                                line[kv.Key] = kv.Value;
                            }
                            break;
                        }
                    case "tolerance":
                        RequireZone(z, op);
                        z.SetToleranceManually(e.GetProperty("value").GetSingle());
                        break;
                    case "seed": {
                            RequireZone(z, op);
                            // 窓のシードの入力(詳細設定の「指定」で武装したクリック・Shift+クリック)と「クリア」は、連続領域モードの
                            // ゾーンにしか出ない。今のゾーンが受けなければ止める(窓の Shift+クリックは、そのとき別のゾーンへ回すが、
                            // 台本は宛先を名指しするので回さない)。
                            if (!ZoneEdits.AcceptsFloodFillSeed(z)) {
                                throw new ArgumentException($"step {k}: seed: ゾーン {current} は連続領域モードでない(窓ではシードを置けない)");
                            }
                            bool clear = e.TryGetProperty("clear", out var clearEl) && clearEl.GetBoolean();
                            bool changed;
                            if (clear) {
                                // 「クリア」はシードがあるときだけ押せる(押せなければ何も変わらない)。
                                changed = ZoneEdits.HasFloodFillSeed(z);
                                if (changed) {
                                    ZoneEdits.ClearFloodFillSeed(z);
                                }
                            } else {
                                var uv = ReadUv(e.GetProperty("uv"));
                                changed = ZoneEdits.FloodFillSeedChanges(z, uv);   // 同じ位置なら窓は記録も知らせもしない
                                if (changed) {
                                    ZoneEdits.SetFloodFillSeed(z, uv);
                                }
                            }
                            line["changed"] = changed;
                            line["seedUV"] = ZoneEdits.HasFloodFillSeed(z) ? new[] { z.seedUV.x, z.seedUV.y } : null;
                            line["zone"] = current;
                            break;
                        }
                    case "set": {
                            var keys = new List<string>();
                            bool any = false;
                            foreach (var prop in e.EnumerateObject()) {
                                switch (prop.Name) {
                                    case "op":
                                        break;
                                    case "zone":
                                        RequireZone(z, op);
                                        foreach (var f in prop.Value.EnumerateObject()) {
                                            SetZoneField(z, f.Name, f.Value, s.Settings, k);
                                            keys.Add("zone." + f.Name);
                                        }
                                        any = true;
                                        break;
                                    case "settings":
                                        foreach (var f in prop.Value.EnumerateObject()) {
                                            SetSettingsField(s.Settings, f.Name, f.Value, k);
                                            keys.Add("settings." + f.Name);
                                        }
                                        any = true;
                                        break;
                                    default:
                                        throw new ArgumentException($"step {k}: set: 知らない項目 {prop.Name}(zone か settings)");
                                }
                            }
                            if (!any) {
                                throw new ArgumentException($"step {k}: set: zone も settings もない");
                            }
                            line["keys"] = keys;
                            if (keys.Exists(key => key.StartsWith("zone.", StringComparison.Ordinal))) {
                                line["zone"] = current;
                            }
                            break;
                        }
                    case "zone":
                        current = e.GetProperty("index").GetInt32();
                        if (current < 0 || current >= zoneList.Count) {
                            throw new ArgumentException($"step {k}: ゾーン {current} がありません");
                        }
                        break;
                    default:
                        throw new ArgumentException($"step {k}: 知らない手 {op}");
                }
                bool write = emit == "all" || (emit == "last" && k == steps.Count - 1);
                if (write) {
                    WriteRawRgba(Path.Combine(outDir, $"step{k}_out.raw"), w, h, s.Paint());
                    s.WriteState(outDir, $"step{k}_");
                }
                Console.WriteLine("SCRIPT " + JsonSerializer.Serialize(line));
            }
            Console.WriteLine("SCRIPT_DONE " + JsonSerializer.Serialize(new Dictionary<string, object> {
                ["steps"] = steps.Count,
                ["zones"] = zoneList.Count,
                ["fullEmbedding"] = s.Sam.FullEmbeddingOrigin,
                ["mirror"] = o.Mirror,
            }));
            return 0;
        }

        private static void RequireZone(ColorZone z, string op) {
            if (z == null) {
                throw new ArgumentException($"{op}: 操作するゾーンがありません(先に eyedropper か zone)");
            }
        }

        private static Vector2 ReadUv(JsonElement e) => new Vector2(e[0].GetSingle(), e[1].GetSingle());

        // ─── set の手 ───
        // 窓のゾーンのカード(IrocaWindow.ZoneList)と処理の設定(IrocaWindow.Layout の DrawProcessingSettings)が値を書くのと同じ経路で書く。
        // 項目名は zones JSON・settings JSON と同じ。窓に欄の無い項目・知らない項目・欄の範囲の外の値・窓で押せない状態の欄はエラーで止める
        // (黙って無視したり丸めたりしない)。Undo の記録・編集の知らせは窓だけのもの。

        // スライダーの値(窓の欄の範囲。範囲の外はエラー)。
        private static float SliderValue(string key, JsonElement v, float min, float max, int step) {
            float x = v.GetSingle();
            if (!(x >= min && x <= max)) {
                throw new ArgumentException($"step {step}: set: {key}={x.ToString(CultureInfo.InvariantCulture)} は窓の欄の範囲 [{min.ToString(CultureInfo.InvariantCulture)}, {max.ToString(CultureInfo.InvariantCulture)}] の外");
            }
            return x;
        }

        private static int IntSliderValue(string key, JsonElement v, int min, int max, int step) {
            int x = v.GetInt32();
            if (x < min || x > max) {
                throw new ArgumentException($"step {step}: set: {key}={x} は窓の欄の範囲 [{min}, {max}] の外");
            }
            return x;
        }

        private static Color ReadRgb(string key, JsonElement v, int step) {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3) {
                throw new ArgumentException($"step {step}: set: {key} は [r,g,b](0..1)");
            }
            return new Color(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle(), 1f);
        }

        private static void RequireEditable(bool editable, string key, string why, int step) {
            if (!editable) {
                throw new ArgumentException($"step {step}: set: {key} は今の状態では窓で押せない({why})");
            }
        }

        // ゾーンの項目を 1 つ書く(窓のゾーンのカード・詳細設定の欄と同じ)。
        private static void SetZoneField(ColorZone z, string key, JsonElement v, SettingsCfg st, int step) {
            switch (key) {
                case "name": z.name = v.GetString(); break;
                // 色の欄(スポイトではない): 変われば指定済みの印・スポイト位置を外す・島の値を戻す(窓と同じ ZoneEdits)。
                case "sample": ZoneEdits.SetSampleFromField(z, ReadRgb(key, v, step)); break;
                case "target": ZoneEdits.SetTargetFromField(z, ReadRgb(key, v, step)); break;
                // 許容範囲を手で変えたら、自動調整が入れた照合の上限距離を外す(窓のスライダーと同じ)。
                case "tolerance": z.SetToleranceManually(SliderValue(key, v, 0f, 1f, step)); break;
                case "valueBlend": z.valueBlend = SliderValue(key, v, 0f, 1f, step); break;
                case "outputSaturation": z.outputSaturation = SliderValue(key, v, 0f, 1f, step); break;
                case "useFloodFill": z.useFloodFill = v.GetBoolean(); break;
                case "edgeSoftness": z.edgeSoftness = SliderValue(key, v, 0f, 1f, step); break;
                case "saturationStrictness": z.saturationStrictness = SliderValue(key, v, 0f, 1f, step); break;
                case "saturationGuard": z.saturationGuard = SliderValue(key, v, 0f, 1f, step); break;
                case "holeFillMargin":
                    // 縁を混色の割合で塗るゾーンでは穴埋めが走らないので、窓は押せなくする(規則は Core の ZoneEdits)。
                    RequireEditable(ZoneEdits.HoleFillMarginApplies(z, st.useDecontamination, st.edgeFeather), key,
                        "縁を混色の割合で塗るゾーン", step);
                    z.holeFillMargin = SliderValue(key, v, 0f, 1f, step);
                    break;
                case "highlightRecovery": z.highlightRecovery = v.GetBoolean(); break;
                case "highlightBandExpand":
                    RequireEditable(z.highlightRecovery, key, "highlightRecovery が OFF", step);   // 窓の DisabledScope と同じ条件
                    z.highlightBandExpand = v.GetBoolean();
                    break;
                // 白寄せ合成とその自動補正は、窓では開発者向けの設定を出したとき(か既定から変わっているとき)だけ見える。
                case "applyHighlightWash": z.applyHighlightWash = v.GetBoolean(); break;
                case "autoHighlightSample":
                    RequireEditable(z.applyHighlightWash, key, "applyHighlightWash が OFF", step);   // 窓の DisabledScope と同じ条件
                    z.autoHighlightSample = v.GetBoolean();
                    break;
                case "shadowDesaturation": z.shadowDesaturation = SliderValue(key, v, 0f, 1f, step); break;
                case "shadowForgivenessSatMin": z.shadowForgivenessSatMin = SliderValue(key, v, 0f, 1f, step); break;
                case "shadowValueFloor": z.shadowValueFloor = SliderValue(key, v, 0f, 1f, step); break;
                case "partSatCeiling": z.partSatCeiling = SliderValue(key, v, 0f, 1f, step); break;
                case "partHueBand": z.partHueBand = SliderValue(key, v, 0f, 0.5f, step); break;
                case "chromaThreshold": z.chromaThreshold = SliderValue(key, v, 0f, 1f, step); break;
                case "chromaCeiling": z.chromaCeiling = SliderValue(key, v, 0f, 1f, step); break;
                case "autoRecolorAnchor": z.autoRecolorAnchor = v.GetBoolean(); break;
                case "seedUV":
                    throw new ArgumentException($"step {step}: set: seedUV は手 seed で置く・外す");
                case "samples":
                case "matchCutoff":
                case "layerIndex":
                case "sampleUV":
                case "includeMask":
                case "excludeMask":
                case "evidenceMask":
                    throw new ArgumentException($"step {step}: set: {key} は窓に欄が無い(自動調整・スポイト・マスクの操作が書く)");
                default:
                    throw new ArgumentException($"step {step}: set: 知らないゾーンの項目 {key}");
            }
        }

        // 処理の設定を 1 つ書く(窓の「処理」の欄と同じ。範囲は Core の RecolorValueRanges)。
        private static void SetSettingsField(SettingsCfg st, string key, JsonElement v, int step) {
            switch (key) {
                case "edgeFeather":
                    st.edgeFeather = SliderValue(key, v, RecolorValueRanges.EdgeFeatherMin, RecolorValueRanges.EdgeFeatherMax, step);
                    break;
                case "antiAliasCleanup":
                    st.antiAliasCleanup = IntSliderValue(key, v, RecolorValueRanges.AntiAliasCleanupMin, RecolorValueRanges.AntiAliasCleanupMax, step);
                    break;
                case "useDecontamination": st.useDecontamination = v.GetBoolean(); break;
                case "holeFillPasses":
                    st.holeFillPasses = IntSliderValue(key, v, RecolorValueRanges.HoleFillPassesMin, RecolorValueRanges.HoleFillPassesMax, step);
                    break;
                case "holeFillMinNeighbors":
                    st.holeFillMinNeighbors = IntSliderValue(key, v, RecolorValueRanges.HoleFillMinNeighborsMin,
                        RecolorValueRanges.HoleFillMinNeighborsMax, step);
                    break;
                case "relaxedSatMin": st.relaxedSatMin = SliderValue(key, v, 0f, 0.2f, step); break;
                case "decontaminationRadius":
                    RequireEditable(st.useDecontamination, key, "useDecontamination が OFF", step);   // 窓の DisabledScope と同じ条件
                    st.decontaminationRadius = IntSliderValue(key, v, RecolorValueRanges.DecontaminationRadiusMin,
                        RecolorValueRanges.DecontaminationRadiusMax, step);
                    break;
                case "relaxedSatRamp":
                    throw new ArgumentException($"step {step}: set: relaxedSatRamp は窓に欄が無い(処理が読まない値)");
                default:
                    throw new ArgumentException($"step {step}: set: 知らない設定の項目 {key}");
            }
        }

        private static MaskSuggestGranularity ParseGranularity(string g) => g switch {
            "auto" => MaskSuggestGranularity.Auto,
            "fine" => MaskSuggestGranularity.Fine,
            "coarse" => MaskSuggestGranularity.Coarse,
            _ => throw new ArgumentException($"gran は auto|fine|coarse: {g}"),
        };

        // 窓の新しいゾーンの名前の規則(EditingSession.NextZoneName)の英語の形。名前は出力に関わらない。
        private static string NextZoneName(List<ColorZone> zones) {
            var used = new HashSet<string>();
            foreach (var z in zones) {
                if (z != null && !string.IsNullOrEmpty(z.name)) {
                    used.Add(z.name);
                }
            }
            for (int n = 1; ; n++) {
                string candidate = "Zone " + n.ToString(CultureInfo.InvariantCulture);
                if (!used.Contains(candidate)) {
                    return candidate;
                }
            }
        }
    }
}
