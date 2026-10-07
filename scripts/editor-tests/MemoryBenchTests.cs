// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 製品の実行環境（Unity 2022.3 の Mono）で、プレビュー相当の処理を繰り返したときのメモリを測る。品質は検査しない。
    /// Unity の Mono ヒープは一度広がると縮まないので、見た目のメモリ(プロセスの Private)を決めるのは
    /// ヒープの最大値(=処理中のピーク＋断片化)。headless ハーネス(net8)は GC が別物なのでこれを測れない。
    ///
    /// 入力は IROCA_MEM_CASES が指す JSON:
    ///   { "steps": 12, "releaseWaitSeconds": 60, "gcEachStep": false,
    ///     "cases": [ { "name": "...", "png": "<絶対パス>", "zones": "<zones JSON の絶対パス>" } ] }
    /// 1 手ごとに、フル段のプレビュー(PreviewView.ScheduleFullPreview)と同じく原本を複製して
    /// <see cref="PixelProcessor.ProcessPixelsArray"/> を選択キャッシュ付きで呼び、出力を 1 枚だけ保持する。
    /// 偶数手は許容度を変える(選択キャッシュのミス)、奇数手はターゲット色だけ変える(ヒット)。
    /// gcEachStep なら各手の後に GC.Collect をかけ、その所要時間を gcMs に記録する(ヒープの膨らみを抑えられるかの比較)。
    /// 最後に作業配列のプールと選択キャッシュを手放して GC し、ヒープとプロセスのメモリが OS へ返るかを見る。
    /// 結果は IROCA_MEM_REPORT が指すファイルへ 1 行 1 計測の JSON で書く。
    ///
    /// 普段の EditMode テストでは IROCA_MEM_CASES が無いので Ignore になる。実行は
    /// <c>scripts/Run-EditorTests.ps1 -MemCases &lt;json&gt;</c>。ケースごとに Unity を起動し直すこと
    /// (ヒープの最大値は同じプロセスの前のケースを引きずる)。
    /// </summary>
    [Category("Memory")]
    public class MemoryBenchTests
    {
        [Serializable] private class CaseDto { public string name = ""; public string png = ""; public string zones = ""; }
        [Serializable] private class CasesDto { public int steps = 12; public int releaseWaitSeconds = 60; public bool gcEachStep; public List<CaseDto> cases = new List<CaseDto>(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr p, out ProcessMemoryCounters c, uint cb);

        private const long MB = 1024 * 1024;

        private static string Sample(string name, string phase, int step, double ms, double gcMs = 0)
        {
            GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"{{\"name\":\"{name}\",\"phase\":\"{phase}\",\"step\":{step},\"ms\":{ms.ToString("F1", inv)},\"gcMs\":{gcMs.ToString("F1", inv)}," +
                   $"\"monoHeap\":{Profiler.GetMonoHeapSizeLong() / MB},\"monoUsed\":{Profiler.GetMonoUsedSizeLong() / MB}," +
                   $"\"private\":{(long)c.PagefileUsage.ToUInt64() / MB},\"peakPrivate\":{(long)c.PeakPagefileUsage.ToUInt64() / MB}," +
                   $"\"ws\":{(long)c.WorkingSetSize.ToUInt64() / MB},\"peakWs\":{(long)c.PeakWorkingSetSize.ToUInt64() / MB}}}";
        }

        // zones JSON の読み込みは製品(IrocaAutomation.RecolorWithZones)と同じ変換を通す。
        private static (List<ColorZone> zones, RecolorSettings settings) LoadZones(string json)
        {
            var t = typeof(IrocaAutomation);
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic;
            var req = JsonUtility.FromJson<IrocaAutomation.ZonesRequest>(json);
            var build = t.GetMethod("BuildZone", F);
            var settingsFrom = t.GetMethod("SettingsFromDto", F);
            Assert.IsNotNull(build, "IrocaAutomation.BuildZone が見つかりません");
            Assert.IsNotNull(settingsFrom, "IrocaAutomation.SettingsFromDto が見つかりません");
            var zones = new List<ColorZone>();
            foreach (var z in req.zones)
            {
                var zone = (ColorZone)build.Invoke(null, new object[] { z });
                if (zone == null || !zone.enabled) continue;
                zone.EnsureId(); zone.UpdateCacheIfNeeded();
                zones.Add(zone);
            }
            var settingsDto = req.settings ?? Activator.CreateInstance(settingsFrom.GetParameters()[0].ParameterType);
            var settings = (RecolorSettings)settingsFrom.Invoke(null, new[] { settingsDto });
            return (zones, settings);
        }

        // 作業配列のプール(PixelProcessor の s_*Pool)を空の同設定のものへ差し替える。製品に解放の口は無いので
        // テストから行う。プールは Rent/Return のたびにフィールドを読むので、処理中でなければ安全。
        private static void ReleasePools()
        {
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic;
            foreach (var f in typeof(PixelProcessor).GetFields(F))
            {
                if (!f.Name.EndsWith("Pool", StringComparison.Ordinal) || !f.FieldType.IsGenericType) continue;
                var create = f.FieldType.GetMethod("Create", new[] { typeof(int), typeof(int) });
                if (create == null) continue;
                // バケットの上限本数は製品と揃えなくてよい(この後は処理しない)。
                f.SetValue(null, create.Invoke(null, new object[] { 1 << 24, 4 }));
            }
        }

        [Test]
        public void PreviewMemory()
        {
            string casesPath = Environment.GetEnvironmentVariable("IROCA_MEM_CASES");
            string reportPath = Environment.GetEnvironmentVariable("IROCA_MEM_REPORT");
            if (string.IsNullOrEmpty(casesPath) || !File.Exists(casesPath))
                Assert.Ignore("IROCA_MEM_CASES が未設定です（メモリ計測は Run-EditorTests.ps1 -MemCases で明示実行）");
            Assert.IsFalse(string.IsNullOrEmpty(reportPath), "IROCA_MEM_REPORT が未設定です");

            var cfg = JsonUtility.FromJson<CasesDto>(File.ReadAllText(casesPath));
            Assert.IsTrue(cfg != null && cfg.cases.Count > 0, "計測するケースがありません");

            var sb = new StringBuilder();
            try
            {
                foreach (var c in cfg.cases)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(c.png)), $"{c.name}: PNG を読めません");
                    int w = tex.width, h = tex.height;
                    Color32[] src = tex.GetPixels32();
                    UnityEngine.Object.DestroyImmediate(tex);

                    var (zones, settings) = LoadZones(File.ReadAllText(c.zones));
                    Assert.IsTrue(zones.Count > 0, $"{c.name}: 有効なゾーンがありません");
                    var baseTol = new float[zones.Count];
                    var baseTarget = new Color[zones.Count];
                    for (int i = 0; i < zones.Count; i++) { baseTol[i] = zones[i].tolerance; baseTarget[i] = zones[i].targetColor; }

                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');

                    var selectionCache = new SelectionCache();
                    Color32[] heldOutput = null;   // PreviewView._fullOutput 相当(最新の 1 枚だけ保持)
                    for (int k = 0; k < cfg.steps; k++)
                    {
                        for (int i = 0; i < zones.Count; i++)
                        {
                            if (k % 2 == 0) zones[i].tolerance = baseTol[i] + 0.002f * (k / 2 + 1);
                            else
                            {
                                var t = baseTarget[i];
                                zones[i].targetColor = new Color(Mathf.Repeat(t.r + 0.05f * k, 1f), t.g, t.b, t.a);
                            }
                            zones[i].UpdateCacheIfNeeded();
                        }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var pixels = (Color32[])src.Clone();
                        PixelProcessor.ProcessPixelsArray(pixels, w, h, null, zones, settings,
                            CancellationToken.None, selectionCache: selectionCache);
                        heldOutput = pixels;
                        double ms = sw.Elapsed.TotalMilliseconds, gcMs = 0;
                        if (cfg.gcEachStep)
                        {
                            var gw = System.Diagnostics.Stopwatch.StartNew();
                            GC.Collect();
                            gcMs = gw.Elapsed.TotalMilliseconds;
                        }
                        sb.Append(Sample(c.name, "step", k, ms, gcMs)).Append('\n');
                    }

                    // 手放す: 出力・選択キャッシュ・プール。その後 GC をかけながら待ち、OS へ返るかを見る。
                    heldOutput = null;
                    selectionCache.Clear();
                    ReleasePools();
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    for (int s = 0; s <= cfg.releaseWaitSeconds; s += 5)
                    {
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        sb.Append(Sample(c.name, "released", s, wait.Elapsed.TotalMilliseconds)).Append('\n');
                        if (s < cfg.releaseWaitSeconds) Thread.Sleep(5000);
                    }
                    GC.KeepAlive(heldOutput);
                }
            }
            finally
            {
                File.WriteAllText(reportPath, sb.ToString());
            }
        }
    }
}
