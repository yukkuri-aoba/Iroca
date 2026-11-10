// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 製品の実行環境（Unity 2022.3 の Mono）で再着色の速さを測る。品質は検査しない。
    /// headless ハーネス（net8）は JIT の段階化や最適化が Mono と違うので、高速化の採否は
    /// ここで測った数字で決める（dev_safe/docs/perf_plan_2026-10-01.md の P0）。
    ///
    /// 入力は IROCA_PERF_CASES が指す JSON（dev_safe/scripts/perf_bench.py unity-cases が作る）:
    ///   { "repeats": 5, "threads": [0, 4],
    ///     "cases": [ { "name": "...", "png": "<絶対パス>", "zones": "<zones JSON の絶対パス>" } ] }
    /// 製品経路（<see cref="IrocaAutomation.RecolorWithZones"/>）を同じドメインで repeats 回呼び、
    /// <see cref="DebugCaptureHooks.OnPerfReport"/> で ProcessPixelsArray の時間と段内訳を受け取る。
    /// 1 回目は JIT と初回確保を含み、2 回目以降が暖機後。PNG の読み書きは時間に含まれない。
    /// 結果は IROCA_PERF_REPORT が指すファイルへ 1 呼び出し 1 行の JSON で書く。
    ///
    /// 普段の EditMode テストでは IROCA_PERF_CASES が無いので Ignore になる。実行は
    /// <c>scripts/Run-EditorTests.ps1 -PerfCases &lt;json&gt;</c>。
    /// </summary>
    [Category("Perf")]
    public class PerfBenchTests {
        [Serializable] private class CaseDto { public string name = ""; public string png = ""; public string zones = ""; }
        // session = true: 製品経路(自動化)の代わりに、プレビューの確定段と同じ呼び方(原本の控え + 選択キャッシュを
        // 持ち越す)で、色だけを変えた 1 手を repeats 回測る。1 回目は選択キャッシュが空(外れ)、2 回目以降は当たり。
        // 名前に "+session" を付けて書く。
        [Serializable] private class CasesDto { public int repeats = 5; public int[] threads = { 0 }; public bool session = false; public List<CaseDto> cases = new List<CaseDto>(); }

        private TestAssets _assets;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();
        [TearDown]
        public void TearDown() {
            DebugCaptureHooks.ParallelismOverride = 0;
            _assets.Dispose();
        }

        [Test]
        public void RecolorTimings() {
            string casesPath = Environment.GetEnvironmentVariable("IROCA_PERF_CASES");
            string reportPath = Environment.GetEnvironmentVariable("IROCA_PERF_REPORT");
            if (string.IsNullOrEmpty(casesPath) || !File.Exists(casesPath)) {
                Assert.Ignore("IROCA_PERF_CASES が未設定です（性能計測は Run-EditorTests.ps1 -PerfCases で明示実行）");
            }
            Assert.IsFalse(string.IsNullOrEmpty(reportPath), "IROCA_PERF_REPORT が未設定です");

            var cfg = JsonUtility.FromJson<CasesDto>(File.ReadAllText(casesPath));
            Assert.IsNotNull(cfg, "IROCA_PERF_CASES の JSON を読めません");
            Assert.IsTrue(cfg.cases.Count > 0, "計測するケースがありません");

            PerfReport last = null;
            void OnReport(PerfReport r) => last = r;
            DebugCaptureHooks.OnPerfReport += OnReport;
            var sb = new StringBuilder();
            try {
                if (cfg.session) {
                    foreach (var c in cfg.cases) {
                        RunSessionCase(c, cfg, () => last, () => last = null, sb);
                    }
                    return;
                }
                foreach (var c in cfg.cases) {
                    string src = _assets.WriteBytes(c.name + ".png", File.ReadAllBytes(c.png));
                    string zones = File.ReadAllText(c.zones);
                    foreach (int threads in cfg.threads) {
                        DebugCaptureHooks.ParallelismOverride = threads;
                        for (int k = 0; k < Math.Max(2, cfg.repeats); k++) {
                            // 出力は呼び出しごとに別名にする。同じファイルへ続けて書くと、直前の書き出しの
                            // 取り込みがファイルを掴んでいて置き換え(File.Replace)が IOException になる。
                            string outPath = $"{_assets.Folder}/{c.name}_t{threads}_{k}_out.png";
                            last = null;
                            string json = IrocaAutomation.RecolorWithZones(src, zones, outPath);
                            var result = JsonUtility.FromJson<IrocaAutomation.RecolorResult>(json);
                            Assert.IsTrue(result.ok, $"{c.name}: 製品経路が失敗しました: {result.error}");
                            Assert.IsNotNull(last, $"{c.name}: PerfReport が届きませんでした");
                            sb.Append(ToJsonLine(c.name, threads, k, last)).Append('\n');
                        }
                    }
                }
            } finally {
                DebugCaptureHooks.OnPerfReport -= OnReport;
                File.WriteAllText(reportPath, sb.ToString());
            }
        }

        // プレビューの確定段(PreviewPipeline.ScheduleFullPreview)と同じ呼び方: 原本は読むだけの控え(sourcePixels)、
        // 出力は毎回原本の写しから、選択キャッシュは持ち越す。1 手ごとに全ゾーンの目標色の色相を少しずらす
        // (目標色は選択の鍵に入らないので、2 回目以降は選択キャッシュに当たる)。
        private static unsafe void RunSessionCase(CaseDto c, CasesDto cfg, Func<PerfReport> last, Action reset, StringBuilder sb) {
            var warnings = new List<string>();
            var state = IrocaAutomation.StateFromZonesJson(File.ReadAllText(c.zones), warnings, out string error);
            Assert.IsNotNull(state, $"{c.name}: {error}");
            var input = RecolorInput.FromSaved(state);
            Assert.IsNotNull(input, $"{c.name}: 有効なゾーンがありません");
            Assert.IsTrue(ExportPipeline.HasPixels(ExportService.ReadSource(c.png, null, out NativePixels src)),
                $"{c.name}: PNG を読めません: {c.png}");
            NativePixels work = null;
            try {
                work = NativePixels.Allocate(src.Width, src.Height);
                var baseTargets = input.Zones.Select(z => z.targetColor).ToList();
                foreach (int threads in cfg.threads) {
                    DebugCaptureHooks.ParallelismOverride = threads;
                    using (var selection = new SelectionCache()) {
                        for (int k = 0; k < Math.Max(2, cfg.repeats); k++) {
                            for (int zi = 0; zi < input.Zones.Count; zi++) {
                                Color.RGBToHSV(baseTargets[zi], out float hh, out float ss, out float vv);
                                var t = Color.HSVToRGB(Mathf.Repeat(hh + 0.05f * k, 1f), ss, vv);
                                t.a = baseTargets[zi].a;
                                input.Zones[zi].targetColor = t;
                                input.Zones[zi].UpdateCacheIfNeeded();
                            }
                            work.CopyFrom(src);
                            reset();
                            PixelProcessor.ProcessPixelsNative(work.Ptr, src.Width, src.Height, input.Masks, input.Zones,
                                input.Settings, default, debug: null, selectionCache: selection, sourcePixels: src.Ptr);
                            Assert.IsNotNull(last(), $"{c.name}: PerfReport が届きませんでした");
                            sb.Append(ToJsonLine(c.name + "+session", threads, k, last())).Append('\n');
                        }
                    }
                }
            } finally {
                work?.Release();
                src.Release();
                input.ReleaseMasks();
            }
        }

        private static string ToJsonLine(string name, int threads, int iter, PerfReport r) {
            string Entries(PhasePerfEntry[] es) => es == null ? "{}" :
                "{" + string.Join(",", es.Select(e => $"\"{e.Name}\":{e.TotalMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}")) + "}";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"{{\"name\":\"{name}\",\"threads\":{threads},\"iter\":{iter},\"w\":{r.Width},\"h\":{r.Height}," +
                   $"\"total\":{r.TotalMs.ToString("F2", inv)},\"processors\":{Environment.ProcessorCount}," +
                   $"\"phases\":{Entries(r.Phases)},\"sub\":{Entries(r.SubPhases)}}}";
        }
    }
}
