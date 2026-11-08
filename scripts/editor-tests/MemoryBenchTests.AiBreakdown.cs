// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Iroca.EditorTests
{
    /// <summary>
    /// AI 提案を使った後に残るプロセスのメモリ(Private)の内訳を調べる診断(aiSuggest のケースを使う)。
    /// 区切りごとに、Unity と Mono の数え方に加えて、プロセスのアドレス空間を VirtualQuery でなめて、
    /// 確保の単位(AllocationBase)ごとのコミット量を取る。始めと比べて増えた確保を種類(NT ヒープ・セグメントヒープ・
    /// スレッドのスタック・それ以外)つきで書き出す。
    /// 実行: 環境変数 IROCA_AI_BREAKDOWN_REPORT を立てて
    /// <c>scripts/Run-EditorTests.ps1 -MemCases &lt;aiSuggest の json&gt; -TestFilter AiMemoryBreakdown -Graphics</c>。
    /// IROCA_AI_BREAKDOWN_CLICKS でクリック数(既定 30。0 ならモデルの読み込みだけ)、IROCA_AI_BREAKDOWN_DISPOSEALL=1 で
    /// 最後にサービスの DisposeAll(モデルも手放す)まで進める。
    /// </summary>
    public partial class MemoryBenchTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Mbi
        {
            public ulong BaseAddress, AllocationBase;
            public uint AllocationProtect, Pad1;
            public ulong RegionSize;
            public uint State, Protect, Type, Pad2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HeapSummaryT
        {
            public uint cb;
            public UIntPtr cbAllocated, cbCommitted, cbReserved, cbMaxReserve;
        }

        [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr addr, out Mbi info, UIntPtr len);
        [DllImport("kernel32.dll")] private static extern IntPtr GetProcessHeap();
        [DllImport("kernel32.dll")] private static extern uint GetProcessHeaps(uint n, IntPtr[] heaps);
        [DllImport("kernel32.dll")] private static extern bool HeapSummary(IntPtr heap, uint flags, ref HeapSummaryT s);

        private const uint MemCommit = 0x1000, MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;
        private const uint PageGuard = 0x100, PageNoAccess = 0x01;

        private sealed class Alloc
        {
            public ulong Base, Committed, Reserved;
            public uint Protect;        // 最初のコミット済み領域の保護
            public bool Guard;          // ガードページを持つ(スレッドのスタック)
            public string Kind = "";
        }

        private sealed class Snap
        {
            public string Label;
            public Dictionary<ulong, Alloc> Private = new Dictionary<ulong, Alloc>();
            public Dictionary<string, long> Totals = new Dictionary<string, long>();
            public string Heaps = "";
        }

        private static readonly uint[] s_readable = { 0x02, 0x04, 0x20, 0x40 };

        private static unsafe Snap TakeSnap(string label)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var snap = new Snap { Label = label };
            var heaps = new IntPtr[4096];
            uint nh = GetProcessHeaps((uint)heaps.Length, heaps);
            var heapSet = new HashSet<ulong>();
            var perHeap = new StringBuilder();
            ulong defaultHeap = (ulong)GetProcessHeap().ToInt64();
            long heapCommitted = 0, heapAllocated = 0;
            for (int i = 0; i < nh && i < heaps.Length; i++)
            {
                heapSet.Add((ulong)heaps[i].ToInt64());
                var s = new HeapSummaryT { cb = (uint)Marshal.SizeOf<HeapSummaryT>() };
                if (HeapSummary(heaps[i], 0, ref s))
                {
                    heapCommitted += (long)s.cbCommitted.ToUInt64();
                    heapAllocated += (long)s.cbAllocated.ToUInt64();
                    perHeap.Append($"{((ulong)heaps[i].ToInt64() == defaultHeap ? "*" : "")}{(ulong)heaps[i].ToInt64():X}=" +
                        $"{s.cbCommitted.ToUInt64() / (double)MB:F1}/{s.cbAllocated.ToUInt64() / (double)MB:F1} ");
                }
            }

            long privCommit = 0, mappedCommit = 0, imageCommit = 0, imageWritable = 0, wcCommit = 0, mappedWc = 0;
            ulong addr = 0x10000;
            int mbiSize = Marshal.SizeOf<Mbi>();
            while (addr < 0x7FFFFFFF0000UL)
            {
                if (VirtualQuery(new IntPtr((long)addr), out var m, new UIntPtr((uint)mbiSize)) == UIntPtr.Zero) break;
                if (m.RegionSize == 0) break;
                if (m.State == MemCommit)
                {
                    if (m.Type == MemPrivate)
                    {
                        privCommit += (long)m.RegionSize;
                        if ((m.Protect & 0x400) != 0) wcCommit += (long)m.RegionSize;   // PAGE_WRITECOMBINE(GPU と共有するメモリ)
                        if (!snap.Private.TryGetValue(m.AllocationBase, out var a))
                            snap.Private[m.AllocationBase] = a = new Alloc { Base = m.AllocationBase, Protect = m.Protect };
                        a.Committed += m.RegionSize;
                        if ((m.Protect & PageGuard) != 0) a.Guard = true;
                    }
                    else if (m.Type == MemMapped)
                    {
                        mappedCommit += (long)m.RegionSize;
                        if ((m.Protect & 0x400) != 0) mappedWc += (long)m.RegionSize;
                    }
                    else if (m.Type == MemImage)
                    {
                        imageCommit += (long)m.RegionSize;
                        uint p = m.Protect & 0xFF;
                        if (p == 0x04 || p == 0x08 || p == 0x40 || p == 0x80) imageWritable += (long)m.RegionSize;
                    }
                }
                if (m.Type == MemPrivate && m.State != 0x10000 /* MEM_FREE */)
                {
                    if (!snap.Private.TryGetValue(m.AllocationBase, out var a))
                        snap.Private[m.AllocationBase] = a = new Alloc { Base = m.AllocationBase };
                    a.Reserved += m.RegionSize;
                    if (m.State == MemCommit && (m.Protect & PageGuard) != 0) a.Guard = true;
                }
                addr = m.BaseAddress + m.RegionSize;
            }

            // 確保の種類: ヒープのハンドル・ヒープのセグメントの印・ガードページ(スタック)。
            long kHeap = 0, kStack = 0, kOther = 0, kOtherBig = 0;
            foreach (var a in snap.Private.Values)
            {
                if (a.Committed == 0) { a.Kind = "reserveOnly"; continue; }
                if (heapSet.Contains(a.Base)) a.Kind = "heapHandle";
                else if (a.Guard) a.Kind = "stack";
                else
                {
                    a.Kind = "other";
                    if (VirtualQuery(new IntPtr((long)a.Base), out var m0, new UIntPtr((uint)mbiSize)) != UIntPtr.Zero
                        && m0.State == MemCommit && (m0.Protect & PageGuard) == 0 && s_readable.Contains(m0.Protect & 0xFF)
                        && m0.RegionSize >= 0x20)
                    {
                        uint sig = *(uint*)(a.Base + 0x10);
                        if (sig == 0xffeeffee) a.Kind = "ntHeapSeg";
                        else if (sig == 0xddeeddee) a.Kind = "segHeap";
                    }
                }
                switch (a.Kind)
                {
                    case "heapHandle": case "ntHeapSeg": case "segHeap": kHeap += (long)a.Committed; break;
                    case "stack": kStack += (long)a.Committed; break;
                    default:
                        kOther += (long)a.Committed;
                        if (a.Committed >= 1 << 20) kOtherBig += (long)a.Committed;
                        break;
                }
            }

            GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
            var t = snap.Totals;
            t["private"] = (long)c.PagefileUsage.ToUInt64();
            t["ws"] = (long)c.WorkingSetSize.ToUInt64();
            t["vqPrivateCommit"] = privCommit;
            t["vqMappedCommit"] = mappedCommit;
            t["privateWriteCombine"] = wcCommit;
            t["mappedWriteCombine"] = mappedWc;
            t["vqImageCommit"] = imageCommit;
            t["vqImageWritable"] = imageWritable;
            t["heapSummaryCommitted"] = heapCommitted;
            t["heapSummaryAllocated"] = heapAllocated;
            t["heapCount"] = nh;
            t["kindHeap"] = kHeap;
            t["kindStack"] = kStack;
            t["kindOther"] = kOther;
            t["kindOtherBig1MB"] = kOtherBig;
            t["threads"] = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
            t["monoHeap"] = Profiler.GetMonoHeapSizeLong();
            t["monoUsed"] = Profiler.GetMonoUsedSizeLong();
            t["unityReserved"] = Profiler.GetTotalReservedMemoryLong();
            t["unityAllocated"] = Profiler.GetTotalAllocatedMemoryLong();
            t["unityUnused"] = Profiler.GetTotalUnusedReservedMemoryLong();
            t["gfxDriver"] = Profiler.GetAllocatedMemoryForGraphicsDriver();
            t["irocaNative"] = s_irocaNativeBytes != null ? Convert.ToInt64(s_irocaNativeBytes.GetValue(null)) : -1;
            snap.Heaps = perHeap.ToString();
            return snap;
        }

        private static string SnapLine(Snap s)
        {
            var sb = new StringBuilder();
            sb.Append("{\"phase\":\"").Append(s.Label).Append('"');
            foreach (var kv in s.Totals)
            {
                long v = kv.Key == "threads" || kv.Key == "heapCount" ? kv.Value : kv.Value / MB;
                sb.Append(",\"").Append(kv.Key).Append("\":").Append(v);
            }
            sb.Append(",\"heaps\":\"").Append(s.Heaps).Append('"');
            return sb.Append('}').ToString();
        }

        private static void AppendDiff(StringBuilder sb, Snap a, Snap b, int top)
        {
            sb.Append($"## diff {a.Label} -> {b.Label}\n");
            var byKind = new Dictionary<string, long>();
            var rows = new List<(long delta, Alloc alloc, ulong before)>();
            foreach (var kv in b.Private)
            {
                a.Private.TryGetValue(kv.Key, out var old);
                long d = (long)kv.Value.Committed - (long)(old?.Committed ?? 0);
                if (d != 0) rows.Add((d, kv.Value, old?.Committed ?? 0));
                byKind.TryGetValue(kv.Value.Kind, out long k);
                byKind[kv.Value.Kind] = k + d;
            }
            foreach (var kv in a.Private)
            {
                if (b.Private.ContainsKey(kv.Key)) continue;
                rows.Add((-(long)kv.Value.Committed, kv.Value, kv.Value.Committed));
                string kind = "gone:" + kv.Value.Kind;
                byKind.TryGetValue(kind, out long k);
                byKind[kind] = k - (long)kv.Value.Committed;
            }
            sb.Append("byKind: ").Append(string.Join(", ", byKind.OrderByDescending(x => Math.Abs(x.Value))
                .Select(x => $"{x.Key}={x.Value / (double)MB:F1}MB"))).Append('\n');
            // 大きさの分布(増えた確保の 1 件あたりのコミット量)
            var buckets = new SortedDictionary<long, (int n, long sum)>();
            foreach (var r in rows.Where(r => r.delta > 0 && r.before == 0))
            {
                long sz = (long)r.alloc.Committed, bucket = 4096;
                while (bucket < sz) bucket <<= 1;
                buckets.TryGetValue(bucket, out var e);
                buckets[bucket] = (e.n + 1, e.sum + sz);
            }
            sb.Append("newAllocsBySize: ").Append(string.Join(", ", buckets.Select(x =>
                $"<= {x.Key / 1024}KB n={x.Value.n} sum={x.Value.sum / (double)MB:F1}MB"))).Append('\n');
            foreach (var r in rows.OrderByDescending(r => Math.Abs(r.delta)).Take(top))
                sb.Append($"  {r.delta / (double)MB,9:F2}MB base=0x{r.alloc.Base:X12} commit={r.alloc.Committed / (double)MB:F2}MB " +
                          $"reserve={r.alloc.Reserved / (double)MB:F2}MB prot=0x{r.alloc.Protect:X} kind={r.alloc.Kind} before={r.before / (double)MB:F2}MB\n");
        }

        private static IEnumerator Frames(double seconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds) yield return null;
        }

        [UnityTest, Timeout(3600000)]
        public IEnumerator AiMemoryBreakdown()
        {
            string casesPath = Environment.GetEnvironmentVariable("IROCA_MEM_CASES");
            string reportPath = Environment.GetEnvironmentVariable("IROCA_AI_BREAKDOWN_REPORT");
            if (string.IsNullOrEmpty(casesPath) || !File.Exists(casesPath) || string.IsNullOrEmpty(reportPath))
                Assert.Ignore("IROCA_MEM_CASES と IROCA_AI_BREAKDOWN_REPORT が要ります");
            int clicks = int.TryParse(Environment.GetEnvironmentVariable("IROCA_AI_BREAKDOWN_CLICKS"), out int n) ? n : 30;
            bool disposeAll = Environment.GetEnvironmentVariable("IROCA_AI_BREAKDOWN_DISPOSEALL") == "1";
            int cycles = int.TryParse(Environment.GetEnvironmentVariable("IROCA_AI_BREAKDOWN_CYCLES"), out int cy) ? Math.Max(1, cy) : 1;

            var cfg = JsonUtility.FromJson<CasesDto>(File.ReadAllText(casesPath));
            var c = cfg.cases.First(x => x.scenario == AiScenario);
            Assert.IsNotNull(MaskSuggestBridge.Service, "AI 統合アセンブリが無いホストです");
            Assert.IsTrue(MaskSuggestBridge.ModelFilesPresent, "AI モデルが未配置です");
            LogAssert.ignoreFailingMessages = true;

            var snaps = new List<Snap>();
            var sb = new StringBuilder();
            sb.Append($"# case={c.name} clicks={clicks} cycles={cycles} disposeAll={disposeAll} gfx={SystemInfo.graphicsDeviceType} " +
                      $"gpu={SystemInfo.graphicsDeviceName} compute={SystemInfo.supportsComputeShaders}\n");
            void Mark(string label)
            {
                var s = TakeSnap(label);
                snaps.Add(s);
                sb.Append(SnapLine(s)).Append('\n');
                File.WriteAllText(reportPath, sb.ToString());   // 途中で落ちても残す
            }

            var tex = ImportBenchTexture(c, out string assetPath);
            var svc = MaskSuggestBridge.Service;
            yield return Frames(3);
            Mark("start");
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                string tag = cycles > 1 ? $"c{cycle}:" : "";
                var owner = new object();
                NativePixels pixels = null;
                try
                {
                    var kind = ExportPipeline.ReadWorkingPixelsNative(Path.GetFullPath(assetPath), tex, out pixels);
                    Assert.AreNotEqual(ExportPipeline.SourceKind.Unavailable, kind);
                    Mark(tag + "pixels");
                    Assert.IsTrue(svc.TryEnsureModels(), $"モデルのロード失敗: {svc.ErrorMessage}");
                    Mark(tag + "models");

                    string key = "membench:" + c.name;
                    for (int k = 0; k < clicks; k++)
                    {
                        float u = Mathf.Repeat(0.13f + 0.37f * k, 1f), v = Mathf.Repeat(0.21f + 0.53f * k, 1f);
                        var sw = Stopwatch.StartNew();
                        bool done = false;
                        while (true)
                        {
                            var r = AiSuggestBroker.RequestWhenIdle(owner, key, pixels, u, v, p => done = true);
                            if (r == AiSuggestBroker.Request.Started) break;
                            Assert.AreNotEqual(AiSuggestBroker.Request.Unavailable, r, $"AI が使えません: {svc.ErrorMessage}");
                            Assert.Less(sw.Elapsed.TotalSeconds, AiTimeoutSeconds);
                            yield return null;
                        }
                        while (!done)
                        {
                            Assert.Less(sw.Elapsed.TotalSeconds, AiTimeoutSeconds, $"提案が届きません（手 {k}）");
                            yield return null;
                        }
                        if (k == 0) Mark(tag + "click1");
                        if (k == 4) Mark(tag + "click5");
                    }
                    if (clicks > 0) Mark($"{tag}click{clicks}");
                }
                finally
                {
                    AiSuggestBroker.Cancel(owner);
                    pixels?.Release();
                    AiSuggestBroker.ReleaseGpuIfUnused();
                }
                Mark(tag + "gpuReleased");
                yield return Frames(10);
                Mark(tag + "gpuReleased+frames10s");
                ReleasePools();
                yield return Frames(20);
                Mark(tag + "poolsReleased+frames20s");
            }
            if (disposeAll)
            {
                var m = svc.GetType().GetMethod("DisposeAll", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.IsNotNull(m, "DisposeAll が見つかりません");
                m.Invoke(svc, null);
                yield return Frames(5);
                Mark("disposeAll");
                UnityEditor.EditorUtility.UnloadUnusedAssetsImmediate();
                yield return Frames(5);
                Mark("unloadUnused");
            }
            AssetDatabase_DeleteBench();

            sb.Append('\n');
            for (int i = 1; i < snaps.Count; i++) AppendDiff(sb, snaps[i - 1], snaps[i], 25);
            AppendDiff(sb, snaps[0], snaps[snaps.Count - 1], 40);
            File.WriteAllText(reportPath, sb.ToString());
            LogAssert.ignoreFailingMessages = false;
        }

        private static void AssetDatabase_DeleteBench() => UnityEditor.AssetDatabase.DeleteAsset(BenchDir);
    }
}
