// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
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
    public class PerfBenchTests
    {
        [Serializable] private class CaseDto { public string name = ""; public string png = ""; public string zones = ""; }
        [Serializable] private class CasesDto { public int repeats = 5; public int[] threads = { 0 }; public List<CaseDto> cases = new List<CaseDto>(); }

        private TestAssets _assets;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();
        [TearDown] public void TearDown()
        {
            DebugCaptureHooks.ParallelismOverride = 0;
            _assets.Dispose();
        }

        [Test]
        public void RecolorTimings()
        {
            string casesPath = Environment.GetEnvironmentVariable("IROCA_PERF_CASES");
            string reportPath = Environment.GetEnvironmentVariable("IROCA_PERF_REPORT");
            if (string.IsNullOrEmpty(casesPath) || !File.Exists(casesPath))
                Assert.Ignore("IROCA_PERF_CASES が未設定です（性能計測は Run-EditorTests.ps1 -PerfCases で明示実行）");
            Assert.IsFalse(string.IsNullOrEmpty(reportPath), "IROCA_PERF_REPORT が未設定です");

            var cfg = JsonUtility.FromJson<CasesDto>(File.ReadAllText(casesPath));
            Assert.IsNotNull(cfg, "IROCA_PERF_CASES の JSON を読めません");
            Assert.IsTrue(cfg.cases.Count > 0, "計測するケースがありません");

            PerfReport last = null;
            void OnReport(PerfReport r) => last = r;
            DebugCaptureHooks.OnPerfReport += OnReport;
            var sb = new StringBuilder();
            try
            {
                foreach (var c in cfg.cases)
                {
                    string src = _assets.WriteBytes(c.name + ".png", File.ReadAllBytes(c.png));
                    string zones = File.ReadAllText(c.zones);
                    string outPath = _assets.Folder + "/" + c.name + "_out.png";
                    foreach (int threads in cfg.threads)
                    {
                        DebugCaptureHooks.ParallelismOverride = threads;
                        for (int k = 0; k < Math.Max(2, cfg.repeats); k++)
                        {
                            last = null;
                            string json = IrocaAutomation.RecolorWithZones(src, zones, outPath);
                            var result = JsonUtility.FromJson<IrocaAutomation.RecolorResult>(json);
                            Assert.IsTrue(result.ok, $"{c.name}: 製品経路が失敗しました: {result.error}");
                            Assert.IsNotNull(last, $"{c.name}: PerfReport が届きませんでした");
                            sb.Append(ToJsonLine(c.name, threads, k, last)).Append('\n');
                        }
                    }
                }
            }
            finally
            {
                DebugCaptureHooks.OnPerfReport -= OnReport;
                File.WriteAllText(reportPath, sb.ToString());
            }
        }

        private static string ToJsonLine(string name, int threads, int iter, PerfReport r)
        {
            string Entries(PhasePerfEntry[] es) => es == null ? "{}" :
                "{" + string.Join(",", es.Select(e => $"\"{e.Name}\":{e.TotalMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}")) + "}";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"{{\"name\":\"{name}\",\"threads\":{threads},\"iter\":{iter},\"w\":{r.Width},\"h\":{r.Height}," +
                   $"\"total\":{r.TotalMs.ToString("F2", inv)},\"processors\":{Environment.ProcessorCount}," +
                   $"\"phases\":{Entries(r.Phases)},\"sub\":{Entries(r.SubPhases)}}}";
        }
    }
}
