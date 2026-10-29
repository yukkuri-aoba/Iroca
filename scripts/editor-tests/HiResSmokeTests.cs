// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 大きなテクスチャ(8K・16K)の通し確認。取り込み → 原本の読み込み → マスクの保存形式 → 色替え(書き出し・NDMF と
    /// 同じ入口) → PNG 書き出しと取り込み → 書いた画素の読み戻し一致 → シーンへのプレビュー(GPU があるときだけ)
    /// → NDMF の差し替えテクスチャ(初回とキャッシュ) → MCP/コマンドラインの入口、を製品の実行環境(Unity の Mono)で
    /// 1 回ずつ通し、各段の時間とメモリを記録する。品質は見ない(落ちない・壊れないことと、所要時間・メモリ)。
    ///
    /// 入力は IROCA_HIRES_CASES が指す JSON:
    ///   { "cases": [ { "name": "...", "png": "<絶対パス>", "zones": "<zones JSON の絶対パス>", "importMaxSize": 8192 } ] }
    /// importMaxSize は取り込み設定の Max Size(マスクはこの寸法で持つ。原本の読み込みと色替えは PNG の解像度のまま)。
    /// 結果は IROCA_HIRES_REPORT へ 1 行 1 段の JSON(途中で落ちてもそこまでの段は残る)。
    /// 普段の EditMode テストでは IROCA_HIRES_CASES が無いので Ignore になる。実行は
    /// <c>scripts/Run-EditorTests.ps1 -HiResCases &lt;json&gt;</c>(GPU の段も見るなら -Graphics)。
    /// 16K はメモリを 20GB 前後使うので、1 回の起動に 1 ケースにすること。
    /// </summary>
    [Category("HiRes")]
    public class HiResSmokeTests
    {
        [Serializable] private class CaseDto { public string name = ""; public string png = ""; public string zones = ""; public int importMaxSize = 2048; }
        [Serializable] private class CasesDto { public List<CaseDto> cases = new List<CaseDto>(); }

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

        private StringBuilder _sb;
        private string _report;

        private void Step(string name, string phase, double ms, string info = "")
        {
            GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _sb.Append($"{{\"name\":\"{name}\",\"phase\":\"{phase}\",\"ms\":{ms.ToString("F1", inv)}," +
                       $"\"monoHeap\":{Profiler.GetMonoHeapSizeLong() / MB},\"monoUsed\":{Profiler.GetMonoUsedSizeLong() / MB}," +
                       $"\"private\":{(long)c.PagefileUsage.ToUInt64() / MB},\"peakPrivate\":{(long)c.PeakPagefileUsage.ToUInt64() / MB}," +
                       $"\"info\":\"{info.Replace("\\", "/").Replace("\"", "'")}\"}}\n");
            File.WriteAllText(_report, _sb.ToString());   // 途中で落ちても段ごとの記録を残す
        }

        private static List<ColorZone> LoadZones(string json, out IrocaSessionState state)
        {
            var req = JsonUtility.FromJson<IrocaAutomation.ZonesRequest>(json);
            var build = BenchSeams.BuildZone;   // 基準の版でも回るよう名前で探す
            var zones = new List<ColorZone>();
            state = new IrocaSessionState();
            foreach (var z in req.zones)
            {
                var zone = (ColorZone)build.Invoke(null, new object[] { z });
                if (zone == null || !zone.enabled) continue;
                zone.EnsureId();
                zones.Add(zone);
                state.zones.Add(zone);
            }
            return zones;
        }

        [Test]
        public void Pipeline()
        {
            string casesPath = Environment.GetEnvironmentVariable("IROCA_HIRES_CASES");
            _report = Environment.GetEnvironmentVariable("IROCA_HIRES_REPORT");
            if (string.IsNullOrEmpty(casesPath) || !File.Exists(casesPath))
                Assert.Ignore("IROCA_HIRES_CASES が未設定です（Run-EditorTests.ps1 -HiResCases で明示実行）");
            Assert.IsFalse(string.IsNullOrEmpty(_report), "IROCA_HIRES_REPORT が未設定です");
            var cfg = JsonUtility.FromJson<CasesDto>(File.ReadAllText(casesPath));
            _sb = new StringBuilder();
            var failures = new List<string>();
            foreach (var c in cfg.cases)
            {
                try { RunCase(c); }
                catch (Exception e)
                {
                    Step(c.name, "EXCEPTION", 0, e.GetType().Name + ": " + e.Message);
                    failures.Add(c.name + ": " + e);
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
        }

        private void RunCase(CaseDto c)
        {
            using (var assets = TestAssets.Create())
            {
                Step(c.name, "start", 0);
                var sw = Stopwatch.StartNew();
                string path = assets.Folder + "/src.png";
                File.Copy(c.png, TestAssets.Abs(path));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                if (imp.maxTextureSize != c.importMaxSize) { imp.maxTextureSize = c.importMaxSize; imp.SaveAndReimport(); }
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                Step(c.name, "import", sw.Elapsed.TotalMilliseconds, $"{tex.width}x{tex.height} {tex.format} mips={tex.mipmapCount}");

                sw.Restart();
                var kind = ExportPipeline.ReadSourcePixels(path, tex, out var src, out int w, out int h);
                Step(c.name, "readSource", sw.Elapsed.TotalMilliseconds, $"{kind} {w}x{h}");
                Assert.AreEqual(ExportPipeline.SourceKind.File, kind);

                // マスクはマスク編集と同じく取り込み後の寸法で持つ。中央の縦帯を除外にする。
                var zones = LoadZones(File.ReadAllText(c.zones), out var state);
                int mw = tex.width, mh = tex.height;
                var common = new bool[mw * mh];
                for (int y = 0; y < mh; y++)
                    for (int x = mw * 45 / 100; x < mw * 55 / 100; x++)
                        common[y * mw + x] = true;
                sw.Restart();
                MaskStateCodec.Encode(state.maskState, mw, mh, common, null, null);
                Step(c.name, "maskEncode", sw.Elapsed.TotalMilliseconds, $"{mw}x{mh} base64={state.maskState.commonMaskBase64.Length}");
                common = null;

                var outPx = (Color32[])src.Clone();
                sw.Restart();
                Assert.IsTrue(SessionRecolor.Apply(outPx, w, h, state));
                double recolorMs = sw.Elapsed.TotalMilliseconds;
                long changed = 0;
                for (int i = 0; i < outPx.Length; i++)
                    if (outPx[i].r != src[i].r || outPx[i].g != src[i].g || outPx[i].b != src[i].b) changed++;
                Step(c.name, "recolor", recolorMs, $"changed={changed} ({100.0 * changed / outPx.Length:F2}%)");
                Assert.Greater(changed, 0, "色替えされていない");
                src = null;

                sw.Restart();
                byte[] png = ExportPipeline.EncodePng(outPx, w, h);
                Step(c.name, "encodePng", sw.Elapsed.TotalMilliseconds, $"{png.Length / MB}MB");
                sw.Restart();
                string outRel;
                using (var native = new Unity.Collections.NativeArray<byte>(png, Unity.Collections.Allocator.Persistent))
                    outRel = ExportService.WriteAndImport(TestAssets.Abs(assets.Folder + "/out.png"), native, path, true);
                png = null;
                var outTex = AssetDatabase.LoadAssetAtPath<Texture2D>(outRel);
                Step(c.name, "writeImport", sw.Elapsed.TotalMilliseconds, $"{outTex.width}x{outTex.height} {outTex.format} max={((TextureImporter)AssetImporter.GetAtPath(outRel)).maxTextureSize}");

                sw.Restart();
                ExportPipeline.ReadSourcePixels(outRel, outTex, out var back, out int bw, out int bh);
                Assert.AreEqual((w, h), (bw, bh));
                int diff = 0;
                for (int i = 0; i < back.Length; i++)
                {
                    Color32 a = back[i], b = outPx[i];
                    if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) diff++;
                }
                Step(c.name, "readBack", sw.Elapsed.TotalMilliseconds, $"diff={diff}");
                Assert.AreEqual(0, diff, "書き出した PNG が色替えの画素と違う");
                back = null;

                // シーンへのプレビュー(GPU)。-nographics では GPU が無いので飛ばす。
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var saved = LivePreview.Current;
                    LivePreview.SetTarget(tex, null);
                    try
                    {
                        sw.Restart();
                        var rt = (RenderTexture)LivePreview.Acquire(tex);
                        double acquireMs = sw.Elapsed.TotalMilliseconds;
                        sw.Restart();
                        LivePreview.Push(tex, outPx, w, h);
                        double pushMs = sw.Elapsed.TotalMilliseconds;
                        // 1 画素読み戻して、GPU に色替え後の絵が載ったかを見る(縮小されているので中央付近の平均色)。
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                        probe.ReadPixels(new Rect(rt.width / 2, rt.height / 2, 1, 1), 0, 0);
                        probe.Apply();
                        RenderTexture.active = prev;
                        Color32 p = probe.GetPixel(0, 0);
                        UnityEngine.Object.DestroyImmediate(probe);
                        Step(c.name, "livePreview", pushMs, $"acquire={acquireMs:F0}ms rt={rt.width}x{rt.height} {SystemInfo.graphicsDeviceType} center={p}");
                        LivePreview.Release(tex);
                    }
                    finally { LivePreview.SetTarget(saved?.source, saved?.boundRecipe); }
                }
                outPx = null;
                GC.Collect();

                // NDMF の差し替えテクスチャ(原本を読んで色替え → 取り込み設定へそろえる → キャッシュ)。
                var recipe = RecipeStore.Create(tex, state, assets.Folder);
                sw.Restart();
                var built = RecipeTextureBuilder.Build(recipe, out var failure);
                Step(c.name, "ndmfBuild", sw.Elapsed.TotalMilliseconds,
                    built != null ? $"{built.width}x{built.height} {built.format}" : "null " + failure);
                Assert.IsNotNull(built, failure.ToString());
                UnityEngine.Object.DestroyImmediate(built);
                sw.Restart();
                built = RecipeTextureBuilder.Build(recipe, out failure);
                Step(c.name, "ndmfBuildCached", sw.Elapsed.TotalMilliseconds,
                    built != null ? $"{built.width}x{built.height} {built.format}" : "null " + failure);
                if (built != null) UnityEngine.Object.DestroyImmediate(built);
                built = null;
                GC.Collect();

                // MCP/コマンドラインの入口(同期。原本を読んで色替えし PNG を書く)。
                sw.Restart();
                string res = IrocaAutomation.RecolorWithZones(path, File.ReadAllText(c.zones), assets.Folder + "/auto.png");
                Step(c.name, "automation", sw.Elapsed.TotalMilliseconds, res.Length > 300 ? res.Substring(0, 300) : res);
                StringAssert.Contains("\"ok\":true", res.Replace(" ", ""));
            }
        }
    }
}
