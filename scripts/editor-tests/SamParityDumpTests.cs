// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// 製品の AI 推論(Sentis)と、テストのハーネスの AI 推論(onnxruntime)の突き合わせの、Unity 側の書き出し
    /// (dev_safe の比べる台本が、同じ入力をハーネスで回して比べる。ここは比べない)。カテゴリ SamParity。
    /// 環境変数 IROCA_SAM_PARITY_OUT(書き出す置き場)が無ければ Ignore(普段の EditMode では回らない。
    /// <c>scripts/Run-EditorTests.ps1 -SamParity</c> で回す)。本物の推論が要るので、Sentis とモデルのあるホスト
    /// (Iroca_U2022)で -Graphics を付けて回す。GPU と CPU のバックエンドを 1 本ずつ(GPU を使えない環境では GPU の方は Ignore)。
    /// <para>
    /// 入力: IROCA_SAM_PARITY_CASES(任意)の JSON = テクスチャ(下原点の RGBA の raw と、元の PNG)とクリックの一覧。
    /// 公開リポのテストなので被写体の素材は持たない(JSON は dev_safe の台本が作る)。合成の絵(円・小さい円・形)は常に回し、
    /// その raw も書き出す(ハーネスが同じ画素で回せるように)。
    /// </para>
    /// <para>
    /// 書き出すもの(バックエンドごとのフォルダ。行は results.jsonl):
    /// (1) 全体の埋め込み(サービスが計算したもの)と、エンコーダの入力(製品の前処理。テクスチャごとに 1 つ)、
    /// (2) その埋め込みでクリックの点をデコードした logits とスコア(サービスのデコーダを直に呼ぶ。後処理なし)、
    /// (3) クリック 1 件の最終の提案(範囲・背景まで広がった印・ズームの有無・切り出しの矩形・切り出しの埋め込みと logits)、
    /// (4) 自動調整(AutoTuneService + 本物の推論。窓の 1 クリック目と同じく、スポイト → 塗り先 → 自動調整 → 外す範囲を
    ///     「ここは塗らない」へ → 塗る。ハーネスの --script の eyedropper・target・autotune と同じ部品)の調整後のゾーンの値・
    ///     漏れの確かめ・外した範囲・塗った出力。
    /// サービスの内部(埋め込み・デコーダ・ズームの印・切り出しの控え・GPU の失敗の数)は AI 統合アセンブリの中なので名前で読む
    /// (名前を変えたら、このテストも直す)。
    /// </para>
    /// </summary>
    [Category("SamParity")]
    public class SamParityDumpTests {
        const string OutEnv = "IROCA_SAM_PARITY_OUT";
        const string CasesEnv = "IROCA_SAM_PARITY_CASES";
        const double StepTimeoutSeconds = 900;
        const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags Statics = BindingFlags.NonPublic | BindingFlags.Static;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ───────── 入力の JSON(dev_safe の台本が書く。JsonUtility で読む) ─────────
        [Serializable]
        class CasesDto {
            public TextureDto[] textures;
            public ClickDto[] clicks;
            public bool synthetic = true;
        }

        [Serializable]
        class TextureDto {
            public string id;
            public string raw;   // [int32 w][int32 h][RGBA](行 0 = 下端。ハーネスの in.raw と同じ)
            public string png;   // 元の PNG(製品の読み込み PngFileDecoder と raw の画素を比べる。無ければ空)
        }

        [Serializable]
        class ClickDto {
            public string id;
            public string texture;
            public float u, v;       // 下原点の UV
            public string gran = "auto";
            public string use = "evidence";
            public bool proposal = true;   // 提案 1 件を書き出すか
            public bool autotune;          // 自動調整を回すか
            public float[] target;         // 自動調整の塗り先(0..1)
        }

        [UnityTest, Timeout(14400000)]
        public IEnumerator Dump_Cpu() => Dump("cpu");

        [UnityTest, Timeout(14400000)]
        public IEnumerator Dump_Gpu() => Dump("gpu");

        static IEnumerator Dump(string backend) {
            string outRoot = Environment.GetEnvironmentVariable(OutEnv);
            if (string.IsNullOrEmpty(outRoot)) {
                Assert.Ignore($"{OutEnv} が未設定です(突き合わせは Run-EditorTests.ps1 -SamParity で回す)");
            }
            var svc = MaskSuggestBridge.Service;
            if (svc == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            if (!MaskSuggestBridge.ModelFilesPresent) {
                Assert.Ignore("AI モデルが未配置");
            }
            bool cpu = backend == "cpu";
            if (!cpu && !SystemInfo.supportsComputeShaders) {
                Assert.Ignore("この環境はコンピュートシェーダを使えない(-nographics など。GPU の推論は無い)");
            }
            var cases = LoadCases(outRoot);
            string dir = Path.Combine(outRoot, backend);
            Directory.CreateDirectory(dir);
            string results = Path.Combine(dir, "results.jsonl");
            File.WriteAllText(results, "");
            string runId = Guid.NewGuid().ToString("N").Substring(0, 12);

            // -nographics・GPU の失敗の切り替えで推論エンジンが LogError を出しても落とさない(ほかの AI のテストと同じ)。
            LogAssert.ignoreFailingMessages = true;
            var warnings = new List<string>();
            Application.LogCallback onLog = (msg, stack, type) => {
                if (type == LogType.Warning || type == LogType.Error || type == LogType.Exception) {
                    lock (warnings) {
                        warnings.Add(msg);
                    }
                }
            };
            Application.logMessageReceived += onLog;
            bool savedKeep = AutoTuneService.KeepEvidenceForRepro;
            try {
                Assert.IsTrue(ForceBackend(svc, cpu, out string why), why);
                if (!cpu && svc.UsesCpuBackend) {
                    Assert.Ignore("この環境は GPU で推論していない(GPU のワーカーを作れず CPU に切り替えた)");
                }
                Assert.AreEqual(cpu, svc.UsesCpuBackend, "バックエンドを切り替えられていない");
                // 自動調整の証拠を控えさせる(調整に渡った証拠の範囲を書き出す。処理には効かない)。
                AutoTuneService.KeepEvidenceForRepro = true;
                foreach (var tex in cases.textures) {
                    yield return RunTexture(svc, backend, dir, results, runId, tex, cases.clicks, warnings);
                }
                Append(results, new Dictionary<string, object> { ["kind"] = "done", ["backend"] = backend, ["usesCpu"] = svc.UsesCpuBackend });
            } finally {
                AutoTuneService.KeepEvidenceForRepro = savedKeep;
                Application.logMessageReceived -= onLog;
                ForceBackend(svc, false, out _, recreate: false);
                LogAssert.ignoreFailingMessages = false;
            }
        }

        // ───────── テクスチャ 1 枚分 ─────────
        static IEnumerator RunTexture(IMaskSuggestService svc, string backend, string dir, string results, string runId,
                                      TextureDto tex, ClickDto[] clicks, List<string> warnings) {
            NativePixels px = ReadRaw(tex.raw);
            try {
                int w = px.Width, h = px.Height;
                var line = new Dictionary<string, object> {
                    ["kind"] = "texture", ["backend"] = backend, ["texture"] = tex.id, ["w"] = w, ["h"] = h,
                };
                // 製品の前処理(エンコーダの入力)はバックエンドに依らないので、テクスチャごとに 1 つだけ書く。
                string encInput = Path.Combine(Path.GetDirectoryName(dir), "enc_input_" + tex.id + ".f32");
                if (!File.Exists(encInput)) {
                    var chw = SamNativeOps.BuildEncoderInput(px);
                    try {
                        WriteBuffer(encInput, chw);
                    } finally {
                        chw.Release();
                    }
                }
                line["encInput"] = Path.GetFileName(encInput);
                if (!string.IsNullOrEmpty(tex.png) && File.Exists(tex.png)) {
                    line["png"] = ComparePng(tex.png, px);
                }

                string key = $"sam-parity|{runId}|{backend}|{tex.id}";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                svc.SetSource(key, px);
                yield return WaitFor(svc, () => svc.Phase == MaskSuggestPhase.Idle || svc.Phase == MaskSuggestPhase.Error,
                    "画像のエンコードと暖機");
                Assert.AreEqual(MaskSuggestPhase.Idle, svc.Phase, svc.ErrorMessage);
                line["encodeMs"] = sw.Elapsed.TotalMilliseconds;
                line["usesCpu"] = svc.UsesCpuBackend;
                var emb = (NativeBuffer)Field(svc, "_embedding").GetValue(svc);
                Assert.IsNotNull(emb, "埋め込みが無い");
                string embFile = $"{tex.id}_full_embedding.f32";
                WriteBuffer(Path.Combine(dir, embFile), emb);
                line["embedding"] = embFile;
                Append(results, line);

                for (int i = 0; i < clicks.Length; i++) {
                    var c = clicks[i];
                    if (c.texture != tex.id || !c.proposal) {
                        continue;
                    }
                    yield return RunProposal(svc, backend, dir, results, tex, c, i, w, h, warnings);
                }
                for (int i = 0; i < clicks.Length; i++) {
                    var c = clicks[i];
                    if (c.texture != tex.id || !c.autotune) {
                        continue;
                    }
                    yield return RunAutoTune(svc, backend, dir, results, key, tex, c, i, px, warnings);
                }
            } finally {
                px.Release();
            }
        }

        // ───────── (2)(3) クリック 1 件の推論と提案 ─────────
        static IEnumerator RunProposal(IMaskSuggestService svc, string backend, string dir, string results,
                                       TextureDto tex, ClickDto c, int index, int w, int h, List<string> warnings) {
            string stem = $"c{index:000}";
            var line = new Dictionary<string, object> {
                ["kind"] = "proposal", ["backend"] = backend, ["click"] = c.id, ["index"] = index, ["texture"] = tex.id,
                ["u"] = c.u, ["v"] = c.v, ["gran"] = c.gran, ["use"] = c.use,
            };
            // (2) 全体の埋め込みのまま、サービスのデコーダを直に 1 回(後処理なし。点は製品と同じ座標の写し方)。
            var start = svc.GetType().GetMethod("StartDecoder", Instance);
            Assert.IsNotNull(start, "サービスのデコーダの入口(StartDecoder)が見つからない(名前を変えたらこのテストも直す)");
            bool decoded = false;
            float[] logits = null, scores = null;
            Exception decodeError = null;
            Action<float[], float[]> onDone = (l, s) => {
                logits = (float[])l.Clone();
                scores = (float[])s.Clone();
                decoded = true;
            };
            Action<Exception> onError = e => {
                decodeError = e;
                decoded = true;
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            start.Invoke(svc, new object[] { c.u, c.v, null, onDone, onError });
            yield return WaitFor(svc, () => decoded, "全体のデコード");
            Assert.IsNull(decodeError, decodeError?.Message);
            line["fullDecodeMs"] = sw.Elapsed.TotalMilliseconds;
            WriteFloats(Path.Combine(dir, stem + "_full_logits.f32"), logits);
            WriteFloats(Path.Combine(dir, stem + "_full_scores.f32"), scores);

            // (3) 製品の 1 クリック(後処理・ズーム込み)。
            int warn0;
            lock (warnings) {
                warn0 = warnings.Count;
            }
            sw.Restart();
            Assert.IsTrue(svc.RequestProposal(c.u, c.v, ParseGran(c.gran), ParseUse(c.use)), "提案を受け付けない");
            yield return WaitFor(svc, () => svc.Phase == MaskSuggestPhase.ProposalReady || svc.Phase == MaskSuggestPhase.Error,
                "クリックの推論");
            Assert.AreEqual(MaskSuggestPhase.ProposalReady, svc.Phase, svc.ErrorMessage);
            Assert.IsTrue(svc.TryTakeProposal(out var p));
            line["proposalMs"] = sw.Elapsed.TotalMilliseconds;
            try {
                Assert.AreEqual((w, h), (p.width, p.height));
                WriteMaskGz(Path.Combine(dir, stem + "_mask.raw.gz"), p.mask);
                line["count"] = p.mask.CountTrue();
                line["score"] = p.score;
                line["areaFrac"] = p.areaFrac;
                line["flood"] = p.floodWarning;
            } finally {
                p.Release();
            }
            line["usesCpu"] = svc.UsesCpuBackend;
            bool zoomPlanned = (bool)Field(svc, "_clickZoomRan").GetValue(svc);
            line["zoomPlanned"] = zoomPlanned;
            bool fallback = false;
            lock (warnings) {
                for (int k = warn0; k < warnings.Count; k++) {
                    if (warnings[k].Contains("ズームイン再推論に失敗")) {
                        fallback = true;
                    }
                }
            }
            line["fallback"] = fallback;
            line["zoomed"] = zoomPlanned && !fallback;
            if (zoomPlanned) {
                // 切り出しの埋め込みの控え(鍵 = 元の鍵|x0,y0,side。このクリックの分が使った順の最後)。
                var lru = Field(svc, "_cropEmbeddingCache").GetValue(svc);
                var order = (List<string>)lru.GetType().GetField("_order", Instance).GetValue(lru);
                var items = (Dictionary<string, NativeBuffer>)lru.GetType().GetField("_items", Instance).GetValue(lru);
                string last = order.Count > 0 ? order[order.Count - 1] : null;
                int bar = last != null ? last.LastIndexOf('|') : -1;
                if (bar >= 0) {
                    var parts = last.Substring(bar + 1).Split(',');
                    line["crop"] = new[] { int.Parse(parts[0], Inv), int.Parse(parts[1], Inv), int.Parse(parts[2], Inv) };
                    if (items.TryGetValue(last, out var cropEmb)) {
                        WriteBuffer(Path.Combine(dir, stem + "_crop_embedding.f32"), cropEmb);
                        line["cropEmbedding"] = stem + "_crop_embedding.f32";
                    }
                }
                if (!fallback) {
                    // ズームしたクリックの最後のデコードは切り出しのデコード(使い回しの並び。次のデコードまで残る)。
                    var lastLogits = (float[])Field(svc, "_logits").GetValue(svc);
                    var lastScores = (float[])Field(svc, "_scores").GetValue(svc);
                    WriteFloats(Path.Combine(dir, stem + "_crop_logits.f32"), lastLogits);
                    WriteFloats(Path.Combine(dir, stem + "_crop_scores.f32"), lastScores);
                }
            } else {
                line["crop"] = null;
            }
            Append(results, line);
        }

        // ───────── (4) 自動調整(本物の推論 + AutoTuneService) ─────────
        static IEnumerator RunAutoTune(IMaskSuggestService svc, string backend, string dir, string results, string key,
                                       TextureDto tex, ClickDto c, int index, NativePixels px, List<string> warnings) {
            string stem = $"c{index:000}";
            int w = px.Width, h = px.Height;
            var line = new Dictionary<string, object> {
                ["kind"] = "autotune", ["backend"] = backend, ["click"] = c.id, ["index"] = index, ["texture"] = tex.id,
                ["u"] = c.u, ["v"] = c.v,
            };
            // 窓の 1 クリック目と同じ部品(ハーネスの --script の eyedropper → target → autotune と同じ)。
            var zone = ZoneEdits.NewZone("Zone 1");
            var uv = new Vector2(c.u, c.v);
            ZoneEdits.SetNewSample(zone, ZoneEdits.SampleColorAt(px, c.u, c.v), uv);
            var target = c.target != null && c.target.Length == 3 ? new Color(c.target[0], c.target[1], c.target[2], 1f) : Color.white;
            ZoneEdits.SetTarget(zone, target);
            var zones = new List<ColorZone> { zone };
            var masks = new MaskWorkspace();
            masks.Reset(w, h);
            var settings = RecolorSettings.Default;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var run = AutoTuneService.Start(zone, px, key, masks.CombinedExclusion(zone.id), masks.Width, masks.Height,
                in settings, masks.ToSnapshot());
            try {
                double end = EditorApplication.timeSinceStartup + StepTimeoutSeconds;
                while (run.Status != AutoTuneService.Status.Done && run.Status != AutoTuneService.Status.Failed) {
                    if (EditorApplication.timeSinceStartup > end) {
                        run.Cancel();
                        Assert.Fail($"自動調整が {StepTimeoutSeconds} 秒で終わらない({c.id}, {run.Status})");
                    }
                    run.Advance();
                    yield return null;
                }
                line["ms"] = sw.Elapsed.TotalMilliseconds;
                line["usesCpu"] = svc.UsesCpuBackend;
                line["status"] = run.Status.ToString();
                line["failure"] = run.Status == AutoTuneService.Status.Failed
                    ? run.Failure + (run.FailureDetail != null ? ": " + run.FailureDetail : "") : null;
                line["method"] = run.Method;
                line["provenance"] = run.Provenance;
                line["conventionalReason"] = run.ConventionalReason;
                var leak = new Dictionary<string, object> {
                    ["checked"] = run.LeakChecked, ["blobs"] = run.LeakBlobCount, ["excludedParts"] = run.LeakExcludedParts,
                    ["excludedPixels"] = run.LeakExcludedPixels, ["skip"] = run.LeakSkipReason, ["applied"] = false,
                };
                line["hadEvidence"] = run.HadEvidence;
                if (run.Status == AutoTuneService.Status.Done) {
                    line["usedEvidence"] = run.HadEvidence && run.UsedEvidence;
                    line["evidenceDiag"] = run.Result.evidenceDiag;
                    var input = run.ToInput();
                    if (input.evidence != null) {
                        WriteBitsGz(Path.Combine(dir, stem + "_evidence.raw.gz"), input.evidence, input.evW, input.evH);
                        line["evidence"] = stem + "_evidence.raw.gz";
                    }
                    run.ApplyTo(zone);
                    ApplyLeak(run, masks, zone, leak);
                }
                line["leak"] = leak;
                line["zone"] = ZoneDocument(zone);
                var exclude = masks.Layer(false, zone.id);
                if (exclude != null) {
                    WriteBitMaskGz(Path.Combine(dir, stem + "_exclude.raw.gz"), exclude);
                    line["exclude"] = stem + "_exclude.raw.gz";
                }
                WriteRgbaGz(Path.Combine(dir, stem + "_out.raw.gz"), Paint(px, masks, zones, settings), w, h);
                line["out"] = stem + "_out.raw.gz";
            } finally {
                run.ReleaseLeakExclusion();
                masks.Clear();
            }
            Append(results, line);
        }

        // 外す範囲を、窓と同じ手順で「ここは塗らない」へ書く(AutoTuneService.Run.LeakRegionFor → MaskWorkspace.ApplyRegion。
        // ハーネスの EditSession.ApplyLeakRegion と同じ)。
        static unsafe void ApplyLeak(AutoTuneService.Run run, MaskWorkspace masks, ColorZone zone, Dictionary<string, object> leak) {
            var region = run.LeakRegionFor(masks, null);
            if (region == null) {
                return;
            }
            try {
                leak["applied"] = masks.ApplyRegion(region.Ptr, zone.id, MaskRegionOp.DontPaintHere, null, out var res);
                leak["changed"] = res.changed;
            } finally {
                region.Release();
            }
        }

        // 塗る(窓の出力と同じ: 今のマスクのスナップショットで全ゾーン。原本の写しへ)。
        static unsafe Color32[] Paint(NativePixels px, MaskWorkspace masks, List<ColorZone> zones, RecolorSettings settings) {
            int w = px.Width, h = px.Height;
            var outPx = new Color32[w * h];
            fixed (Color32* d = outPx) {
                Buffer.MemoryCopy(px.Ptr, d, (long)w * h * 4, (long)w * h * 4);
            }
            var snap = masks.ToSnapshot();
            try {
                PixelProcessor.ProcessPixelsArray(outPx, w, h, snap, zones, in settings);
            } finally {
                snap.Release();
            }
            return outPx;
        }

        // 調整後のゾーン(ハーネスの ZoneDocument と同じ名前。比べる台本が項目ごとに比べる)。
        static Dictionary<string, object> ZoneDocument(ColorZone z) {
            var samples = new List<object>();
            foreach (var s in z.extraSamples) {
                samples.Add(new[] { s.r, s.g, s.b });
            }
            return new Dictionary<string, object> {
                ["name"] = z.name,
                ["sample"] = new[] { z.sampleColor.r, z.sampleColor.g, z.sampleColor.b },
                ["samples"] = samples,
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
        }

        // ───────── バックエンドの切り替え(GPU の失敗の数を上限にすると CPU だけを使う。0 に戻すと GPU を試す) ─────────
        static bool ForceBackend(IMaskSuggestService svc, bool cpu, out string why, bool recreate = true) {
            why = null;
            var t = svc.GetType();
            var failures = t.GetField("_gpuFailures", Instance);
            var max = t.GetField("MaxGpuFailures", Statics);
            if (failures == null || max == null) {
                why = "サービスに GPU の失敗の数(_gpuFailures・MaxGpuFailures)が無い(名前を変えたらこのテストも直す)";
                return false;
            }
            int limit = (int)(max.IsLiteral ? max.GetRawConstantValue() : max.GetValue(null));
            failures.SetValue(svc, cpu ? limit : 0);
            svc.ReleaseGpuResources();   // 次に作るワーカーは、今の失敗の数で決まるバックエンド
            if (!recreate) {
                return true;
            }
            if (!svc.TryEnsureModels()) {
                why = $"モデルのロード失敗: {svc.ErrorMessage}";
                return false;
            }
            return true;
        }

        static FieldInfo Field(object o, string name) {
            var f = o.GetType().GetField(name, Instance);
            Assert.IsNotNull(f, $"サービスの {name} が見つからない(名前を変えたらこのテストも直す)");
            return f;
        }

        static IEnumerator WaitFor(IMaskSuggestService svc, Func<bool> done, string what) {
            double end = EditorApplication.timeSinceStartup + StepTimeoutSeconds;
            while (!done()) {
                if (EditorApplication.timeSinceStartup > end) {
                    Assert.Fail($"{what} が {StepTimeoutSeconds} 秒で終わりませんでした（Phase={svc.Phase}, {svc.ErrorMessage}）");
                }
                yield return null;
            }
        }

        static MaskSuggestGranularity ParseGran(string g) {
            switch (g) {
                case "fine": return MaskSuggestGranularity.Fine;
                case "coarse": return MaskSuggestGranularity.Coarse;
                default: return MaskSuggestGranularity.Auto;
            }
        }

        static MaskSuggestUse ParseUse(string u) {
            switch (u) {
                case "region": return MaskSuggestUse.Region;
                default: return MaskSuggestUse.Evidence;
            }
        }

        // ───────── 入力(JSON と合成の絵) ─────────
        static CasesDto LoadCases(string outRoot) {
            var textures = new List<TextureDto>();
            var clicks = new List<ClickDto>();
            bool synthetic = true;
            string path = Environment.GetEnvironmentVariable(CasesEnv);
            if (!string.IsNullOrEmpty(path)) {
                Assert.IsTrue(File.Exists(path), $"{CasesEnv} のファイルが無い: {path}");
                var doc = JsonUtility.FromJson<CasesDto>(File.ReadAllText(path));
                if (doc.textures != null) {
                    textures.AddRange(doc.textures);
                }
                if (doc.clicks != null) {
                    clicks.AddRange(doc.clicks);
                }
                synthetic = doc.synthetic;
            }
            if (synthetic) {
                AddSynthetic(outRoot, textures, clicks);
            }
            var all = new CasesDto { textures = textures.ToArray(), clicks = clicks.ToArray(), synthetic = synthetic };
            // 回した一覧(合成の分を含む)。比べる台本はこれを読む。
            File.WriteAllText(Path.Combine(outRoot, "cases_used.json"), JsonUtility.ToJson(all, true));
            return all;
        }

        // 合成の絵(下原点)。円(ズームしない)・小さい円(ズームする)・形(陰影のある長方形・離れた同じ色の小さな円・青い円。
        // 自動調整の漏れの確かめが塊を探す)。
        static void AddSynthetic(string outRoot, List<TextureDto> textures, List<ClickDto> clicks) {
            string texDir = Path.Combine(outRoot, "textures");
            Directory.CreateDirectory(texDir);
            void AddTexture(string id, Color32[] px, int size) {
                string raw = Path.Combine(texDir, id + ".raw");
                WriteRgbaRaw(raw, px, size, size);
                textures.Add(new TextureDto { id = id, raw = raw, png = "" });
            }
            void AddClick(string id, string tex, float u, float v, string gran, string use, bool proposal, bool autotune) {
                clicks.Add(new ClickDto {
                    id = id, texture = tex, u = u, v = v, gran = gran, use = use, proposal = proposal, autotune = autotune,
                    target = new[] { 0.20f, 0.45f, 0.85f },
                });
            }
            var gray = new Color32(150, 150, 150, 255);
            var red = new Color32(200, 30, 30, 255);

            var disc = new Color32[256 * 256];
            for (int y = 0; y < 256; y++) {
                for (int x = 0; x < 256; x++) {
                    float d2 = (x - 127.5f) * (x - 127.5f) + (y - 127.5f) * (y - 127.5f);
                    disc[y * 256 + x] = d2 <= 70f * 70f ? red : gray;
                }
            }
            AddTexture("syn_disc256", disc, 256);
            AddClick("syn:disc256:region", "syn_disc256", 0.5f, 0.5f, "auto", "region", true, false);
            AddClick("syn:disc256:evidence", "syn_disc256", 0.5f, 0.5f, "auto", "evidence", true, true);

            var small = new Color32[1024 * 1024];
            for (int y = 0; y < 1024; y++) {
                for (int x = 0; x < 1024; x++) {
                    small[y * 1024 + x] = (x - 300) * (x - 300) + (y - 700) * (y - 700) <= 24 * 24 ? red : gray;
                }
            }
            AddTexture("syn_smalldisc1024", small, 1024);
            float su = 300.5f / 1024f, sv = 700.5f / 1024f;
            AddClick("syn:smalldisc:region", "syn_smalldisc1024", su, sv, "auto", "region", true, false);
            AddClick("syn:smalldisc:region-fine", "syn_smalldisc1024", su, sv, "fine", "region", true, false);
            AddClick("syn:smalldisc:evidence", "syn_smalldisc1024", su, sv, "auto", "evidence", true, true);

            var shapes = new Color32[1024 * 1024];
            for (int y = 0; y < 1024; y++) {
                for (int x = 0; x < 1024; x++) {
                    byte g = (byte)(120 + (x + y) * 60 / 2048);
                    var c = new Color32(g, g, (byte)(g + 10), 255);
                    if (x >= 200 && x < 620 && y >= 300 && y < 640) {
                        // 陰影のある赤(下側ほど暗い。セル影の段を 1 つ)
                        byte r = (byte)(y < 420 ? 150 : 205 - (x - 200) % 37);
                        c = new Color32(r, (byte)(r / 7), (byte)(r / 6), 255);
                    } else if ((x - 820) * (x - 820) + (y - 200) * (y - 200) <= 40 * 40) {
                        c = new Color32(200, 28, 32, 255);   // 離れた同じ色の小さな円
                    } else if ((x - 780) * (x - 780) + (y - 780) * (y - 780) <= 120 * 120) {
                        c = new Color32(40, 60, 190, 255);
                    }
                    shapes[y * 1024 + x] = c;
                }
            }
            AddTexture("syn_shapes1024", shapes, 1024);
            AddClick("syn:shapes:evidence", "syn_shapes1024", 400.5f / 1024f, 500.5f / 1024f, "auto", "evidence", true, true);
        }

        // ───────── 製品の PNG の読み込み(PngFileDecoder)と raw の画素を比べる(D-15) ─────────
        static unsafe Dictionary<string, object> ComparePng(string png, NativePixels raw) {
            var doc = new Dictionary<string, object>();
            NativePixels dec = null;
            try {
                dec = PngFileDecoder.TryDecode(png);
            } catch (Exception e) {
                doc["error"] = e.Message;
                return doc;
            }
            if (dec == null) {
                doc["decoded"] = false;
                return doc;
            }
            try {
                doc["decoded"] = true;
                doc["w"] = dec.Width;
                doc["h"] = dec.Height;
                if (dec.Width != raw.Width || dec.Height != raw.Height) {
                    doc["sizeMismatch"] = true;
                    return doc;
                }
                int diff = 0, maxDiff = 0;
                Color32* a = dec.Ptr, b = raw.Ptr;
                for (int i = 0; i < raw.Length; i++) {
                    int d = Math.Max(Math.Max(Math.Abs(a[i].r - b[i].r), Math.Abs(a[i].g - b[i].g)),
                        Math.Max(Math.Abs(a[i].b - b[i].b), Math.Abs(a[i].a - b[i].a)));
                    if (d != 0) {
                        diff++;
                        maxDiff = Math.Max(maxDiff, d);
                    }
                }
                doc["diffPixels"] = diff;
                doc["maxDiff"] = maxDiff;
            } finally {
                dec.Release();
            }
            return doc;
        }

        // ───────── 読み書き ─────────
        static unsafe NativePixels ReadRaw(string path) {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) {
                var head = new byte[8];
                ReadExactly(fs, head, 8);
                int w = BitConverter.ToInt32(head, 0), h = BitConverter.ToInt32(head, 4);
                var px = NativePixels.Allocate(w, h);
                try {
                    long total = (long)w * h * 4, off = 0;
                    var buf = new byte[1 << 20];
                    byte* dst = (byte*)px.Ptr;
                    while (off < total) {
                        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, total - off));
                        if (n <= 0) {
                            throw new EndOfStreamException($"raw の画素が足りない: {path}");
                        }
                        fixed (byte* s = buf) {
                            Buffer.MemoryCopy(s, dst + off, n, n);
                        }
                        off += n;
                    }
                    return px;
                } catch {
                    px.Release();
                    throw;
                }
            }
        }

        static void ReadExactly(Stream s, byte[] buf, int n) {
            int read = 0;
            while (read < n) {
                int k = s.Read(buf, read, n - read);
                if (k <= 0) {
                    throw new EndOfStreamException();
                }
                read += k;
            }
        }

        static unsafe void WriteBuffer(string path, NativeBuffer b) {
            using (var src = new UnmanagedMemoryStream((byte*)b.Ptr, b.Bytes))
            using (var fs = File.Create(path)) {
                src.CopyTo(fs, 1 << 20);
            }
        }

        static void WriteFloats(string path, float[] a) {
            var bytes = new byte[(long)a.Length * sizeof(float)];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }

        static Stream CreateGz(string path)
            => new GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Fastest);

        static void WriteHeader(Stream s, int w, int h) {
            s.Write(BitConverter.GetBytes(w), 0, 4);
            s.Write(BitConverter.GetBytes(h), 0, 4);
        }

        // マスクの raw(ハーネスと同じ形: [int32 w][int32 h][1 画素 1 バイト。非 0 = true]。行 0 = 下端)を gzip で。
        static unsafe void WriteMaskGz(string path, NativeBools m) {
            using (var gz = CreateGz(path)) {
                WriteHeader(gz, m.Width, m.Height);
                using (var src = new UnmanagedMemoryStream((byte*)m.Ptr, m.Length)) {
                    src.CopyTo(gz, 1 << 20);
                }
            }
        }

        static void WriteBitMaskGz(string path, NativeBitMask m) {
            var bytes = new byte[m.Length];
            for (int i = 0; i < bytes.Length; i++) {
                bytes[i] = m.Get(i) ? (byte)1 : (byte)0;
            }
            using (var gz = CreateGz(path)) {
                WriteHeader(gz, m.Width, m.Height);
                gz.Write(bytes, 0, bytes.Length);
            }
        }

        // MaskSnapshot.Pack の形(1 画素 1 ビット。語の下位ビットから)を 1 画素 1 バイトにして書く。
        static void WriteBitsGz(string path, ulong[] bits, int w, int h) {
            var bytes = new byte[w * h];
            for (int i = 0; i < bytes.Length; i++) {
                bytes[i] = (bits[i >> 6] & (1UL << (i & 63))) != 0 ? (byte)1 : (byte)0;
            }
            using (var gz = CreateGz(path)) {
                WriteHeader(gz, w, h);
                gz.Write(bytes, 0, bytes.Length);
            }
        }

        static unsafe void WriteRgbaGz(string path, Color32[] px, int w, int h) {
            var bytes = new byte[(long)w * h * 4];
            fixed (Color32* s = px) {
                fixed (byte* d = bytes) {
                    Buffer.MemoryCopy(s, d, bytes.Length, bytes.Length);
                }
            }
            using (var gz = CreateGz(path)) {
                WriteHeader(gz, w, h);
                gz.Write(bytes, 0, bytes.Length);
            }
        }

        static unsafe void WriteRgbaRaw(string path, Color32[] px, int w, int h) {
            var bytes = new byte[(long)w * h * 4];
            fixed (Color32* s = px) {
                fixed (byte* d = bytes) {
                    Buffer.MemoryCopy(s, d, bytes.Length, bytes.Length);
                }
            }
            using (var fs = File.Create(path)) {
                WriteHeader(fs, w, h);
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        // ───────── 結果の行(JSON。1 行 1 件) ─────────
        static void Append(string path, Dictionary<string, object> doc) {
            var sb = new StringBuilder();
            AppendJson(sb, doc);
            sb.Append('\n');
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static void AppendJson(StringBuilder sb, object v) {
            switch (v) {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    AppendString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) {
                        sb.Append("null");
                    } else {
                        sb.Append(f.ToString("R", Inv));
                    }
                    break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) {
                        sb.Append("null");
                    } else {
                        sb.Append(d.ToString("R", Inv));
                    }
                    break;
                case int i:
                    sb.Append(i.ToString(Inv));
                    break;
                case long l:
                    sb.Append(l.ToString(Inv));
                    break;
                case IDictionary<string, object> dict: {
                        sb.Append('{');
                        bool first = true;
                        foreach (var kv in dict) {
                            if (!first) {
                                sb.Append(',');
                            }
                            first = false;
                            AppendString(sb, kv.Key);
                            sb.Append(':');
                            AppendJson(sb, kv.Value);
                        }
                        sb.Append('}');
                        break;
                    }
                case IEnumerable seq: {
                        sb.Append('[');
                        bool first = true;
                        foreach (var item in seq) {
                            if (!first) {
                                sb.Append(',');
                            }
                            first = false;
                            AppendJson(sb, item);
                        }
                        sb.Append(']');
                        break;
                    }
                default:
                    AppendString(sb, Convert.ToString(v, Inv));
                    break;
            }
        }

        static void AppendString(StringBuilder sb, string s) {
            sb.Append('"');
            foreach (char ch in s) {
                switch (ch) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", Inv));
                        } else {
                            sb.Append(ch);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
