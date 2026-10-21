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
    ///   { "steps": 12, "releaseWaitSeconds": 60, "gcEachStep": false, "reuseOutput": false,
    ///     "hashOutput": true,
    ///     "cases": [ { "name": "...", "png": "<絶対パス>", "zones": "<zones JSON の絶対パス>", "importMaxSize": 0, "exportAfterStep": -1 } ] }
    /// importMaxSize が 0 より大きいと、取り込み設定の Max Size がその値のときの作業解像度(WorkingResolution)へ
    /// プレビューと同じ読み方で縮めてから処理する(0 は元ファイルの解像度＝「元の解像度で編集」と同じ)。
    /// 1 手ごとに、フル段のプレビュー(PreviewView.ScheduleFullPreview)と同じく原本を複製して
    /// <see cref="PixelProcessor.ProcessPixelsArray"/> を選択キャッシュ付きで呼び、出力を 1 枚だけ保持する。
    /// 偶数手は許容度を変える(選択キャッシュのミス)、奇数手はターゲット色だけ変える(ヒット)。
    /// reuseOutput なら出力バッファを 0.3.0 より前のフル段と同じく 1 本だけ使い回す(false は毎回複製)。
    /// gcEachStep なら各手の後に GC.Collect をかけ、その所要時間を gcMs に記録する(ヒープの膨らみを抑えられるかの比較)。
    /// exportAfterStep が 0 以上なら、その手の後に「書き出し」と同じ処理(元ファイルの解像度・入力の複製なし)を 1 回はさむ
    /// (作業解像度と違う解像度の作業配列がその後も残るかを見る)。cancelOverlapRounds が 1 以上なら、続けてその回数だけ
    /// フル段を PreviewJob で投げ、途中で投げ直す(取り消した計算と次の計算が重なったときに、プールが 2 組目を持つかを見る)。
    /// 最後に作業配列のプールと選択キャッシュを手放して GC し、ヒープとプロセスのメモリが OS へ返るかを見る。
    /// window が true のケースは、上の代わりに製品のウィンドウ(IrocaWindow)でプレビューを steps 回作り直してから
    /// ウィンドウを閉じ、プレビューの原本・出力(0.3.0 から Mono のヒープの外)が閉じた後に OS へ返るかを見る
    /// (png を一時アセットとして取り込み、Max Size は importMaxSize、0 なら 16384)。
    /// 結果は IROCA_MEM_REPORT が指すファイルへ 1 行 1 計測の JSON で書く。Mono ヒープ・Unity のネイティブ(nativeAlloc/Reserved)・
    /// いろかが自分で持つネイティブ(irocaNative、無い版は -1)・プロセスの Private と WS。hashOutput なら出力の指紋(hash)も書くので、
    /// 基準と変更後で Mono の出力が同じかを 8K でも比べられる(同じ JSON・同じ手順なら同じ値になる)。
    ///
    /// 普段の EditMode テストでは IROCA_MEM_CASES が無いので Ignore になる。実行は
    /// <c>scripts/Run-EditorTests.ps1 -MemCases &lt;json&gt;</c>。ケースごとに Unity を起動し直すこと
    /// (ヒープの最大値は同じプロセスの前のケースを引きずる)。
    /// </summary>
    [Category("Memory")]
    public class MemoryBenchTests
    {
        [Serializable] private class CaseDto { public string name = ""; public string png = ""; public string zones = ""; public int importMaxSize; public int exportAfterStep = -1; public int cancelOverlapRounds; public int refineRounds; public bool window; }
        [Serializable] private class CasesDto { public int steps = 12; public int releaseWaitSeconds = 60; public bool gcEachStep; public bool reuseOutput; public bool hashOutput = true; public List<CaseDto> cases = new List<CaseDto>(); }

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

        // いろかが Marshal.AllocHGlobal で持っているバイト数(0.3.0 で足す予定の数え上げ)。Unity の Profiler には
        // 出ないので、あれば反射で読む。基準側のコード(まだ無い)でも同じテストを回せるよう、型は名前で引く。
        private static readonly PropertyInfo s_irocaNativeBytes =
            typeof(PixelProcessor).Assembly.GetType("Iroca.IrocaNativeMemory")?.GetProperty("LiveBytes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        // 直前の ProcessPixelsArray の段ごとの時間(このスレッドで出たレポート)。20ms 以上の段だけを "名前=ms,…" で返す。
        // 2 手目(選択キャッシュに当たる手)の遅れがどの段で出ているかを、基準と変更後で比べるため。
        private static string TakeSubPhases()
        {
            var r = DebugCaptureHooks.TakeThreadPerfReport();
            if (r == null) return null;
            var sb = new StringBuilder();
            void Add(PhasePerfEntry[] es, string prefix)
            {
                if (es == null) return;
                foreach (var e in es)
                {
                    if (e.TotalMs < 20) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(prefix).Append(e.Name).Append('=').Append(e.TotalMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            Add(r.Phases, "");
            Add(r.SubPhases, "s.");
            return sb.ToString();
        }

        private static string Sample(string name, string phase, int step, double ms, double gcMs = 0, string hash = null, string sub = null)
        {
            GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            long irocaNative = s_irocaNativeBytes != null ? Convert.ToInt64(s_irocaNativeBytes.GetValue(null)) / MB : -1;
            return $"{{\"name\":\"{name}\",\"phase\":\"{phase}\",\"step\":{step},\"ms\":{ms.ToString("F1", inv)},\"gcMs\":{gcMs.ToString("F1", inv)}," +
                   $"\"monoHeap\":{Profiler.GetMonoHeapSizeLong() / MB},\"monoUsed\":{Profiler.GetMonoUsedSizeLong() / MB}," +
                   $"\"nativeAlloc\":{Profiler.GetTotalAllocatedMemoryLong() / MB},\"nativeReserved\":{Profiler.GetTotalReservedMemoryLong() / MB}," +
                   $"\"irocaNative\":{irocaNative},\"gcCount\":{GC.CollectionCount(0)}," +
                   $"\"private\":{(long)c.PagefileUsage.ToUInt64() / MB},\"peakPrivate\":{(long)c.PeakPagefileUsage.ToUInt64() / MB}," +
                   $"\"ws\":{(long)c.WorkingSetSize.ToUInt64() / MB},\"peakWs\":{(long)c.PeakWorkingSetSize.ToUInt64() / MB}" +
                   (hash != null ? $",\"hash\":\"{hash}\"" : "") +
                   (sub != null ? $",\"sub\":\"{sub}\"" : "") + "}";
        }

        // 出力の指紋(FNV-1a 64 を画素単位で)。基準と変更後で Mono の出力が同じかを、8K でも確かめるため。
        private static string HashPixels(Color32[] px, int len)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < len; i++)
            {
                Color32 c = px[i];
                h = (h ^ ((uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24))) * 1099511628211UL;
            }
            return h.ToString("x16");
        }

        // 元ファイルの解像度の画素(書き出しと同じ読み方)。
        private static Color32[] LoadSourcePixels(string png, out int w, out int h)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(png)), $"PNG を読めません: {png}");
                w = tex.width;
                h = tex.height;
                return tex.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
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

        // 作業配列のプールを手放す。製品に解放の口(PixelProcessor.ReleaseWorkBuffers、0.3.0 で足す予定)があれば
        // それを呼び、無い版(基準側)では PixelProcessor の s_*Pool を空の同設定のものへ差し替える。
        // 差し替えはプールが Rent/Return のたびにフィールドを読むので、処理中でなければ安全。
        private static void ReleasePools()
        {
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic;
            var release = typeof(PixelProcessor).GetMethod("ReleaseWorkBuffers", F | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (release != null) { release.Invoke(null, null); return; }
            foreach (var f in typeof(PixelProcessor).GetFields(F))
            {
                if (!f.Name.EndsWith("Pool", StringComparison.Ordinal) || !f.FieldType.IsGenericType) continue;
                var create = f.FieldType.GetMethod("Create", new[] { typeof(int), typeof(int) });
                if (create == null) continue;
                // バケットの上限本数は製品と揃えなくてよい(この後は処理しない)。
                f.SetValue(null, create.Invoke(null, new object[] { 1 << 24, 4 }));
            }
        }

        // ウィンドウの場面(CaseDto.window)。製品の PreviewView を反射で動かす(基準の版でも同じテストが回るように)。
        private static void RunWindowCase(CaseDto c, CasesDto cfg, StringBuilder sb)
        {
            const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            const string dir = "Assets/IrocaMemBench";
            string assetPath = $"{dir}/{c.name}.png";
            Directory.CreateDirectory(dir);
            File.Copy(c.png, assetPath, true);
            UnityEditor.AssetDatabase.ImportAsset(assetPath, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
            var imp = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(assetPath);
            imp.maxTextureSize = c.importMaxSize > 0 ? c.importMaxSize : 16384;
            imp.textureCompression = UnityEditor.TextureImporterCompression.Uncompressed;
            imp.SaveAndReimport();
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            Assert.IsNotNull(tex, $"{c.name}: 取り込めません");
            var (zones, _) = LoadZones(File.ReadAllText(c.zones));
            var baseTol = new float[zones.Count];
            for (int i = 0; i < zones.Count; i++) baseTol[i] = zones[i].tolerance;

            IrocaWindow win = null;
            try
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                win = ScriptableObject.CreateInstance<IrocaWindow>();
                win.SourceTexture = tex;
                win.Session.zones.Clear();
                foreach (var z in zones) win.Session.zones.Add(z);
                var pv = win.Preview;
                var gen = typeof(PreviewView).GetMethod("GeneratePreviewAsync", I);
                var proxyJob = typeof(PreviewView).GetField("_proxyJob", I).GetValue(pv);
                var fullJob = typeof(PreviewView).GetField("_previewJob", I).GetValue(pv);
                var fullOutput = typeof(PreviewView).GetField("_fullOutput", I);
                Assert.IsNotNull(gen, "PreviewView.GeneratePreviewAsync が見つかりません");
                bool Running(object job) => (bool)job.GetType().GetProperty("IsRunning").GetValue(job);
                for (int k = 0; k < cfg.steps; k++)
                {
                    for (int i = 0; i < zones.Count; i++)
                    {
                        zones[i].tolerance = baseTol[i] + 0.002f * (k + 1);
                        zones[i].UpdateCacheIfNeeded();
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    gen.Invoke(pv, new object[] { false });
                    // 概要段 → 確定段(概要段の apply が確定段を予約する)が両方終わるまで、メインスレッドの戻りを捌く。
                    int idle = 0;
                    while (idle < 3)
                    {
                        if (sw.ElapsedMilliseconds > 600000) Assert.Fail("プレビューが終わらない");
                        PreviewJobMainThread.Drain();
                        idle = Running(proxyJob) || Running(fullJob) ? 0 : idle + 1;
                        Thread.Sleep(2);
                    }
                    sb.Append(Sample(c.name, "wstep", k, sw.Elapsed.TotalMilliseconds, 0,
                        cfg.hashOutput ? HashAny(fullOutput.GetValue(pv)) : null)).Append('\n');
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveWindowOpen", cfg.steps, 0)).Append('\n');
            }
            finally
            {
                // 閉じる(OnDisable → プレビューの Suspend)。その後は製品と同じく作業配列も手放す。
                if (win != null) UnityEngine.Object.DestroyImmediate(win);
                ReleasePools();
                var wait = System.Diagnostics.Stopwatch.StartNew();
                for (int s = 0; s <= cfg.releaseWaitSeconds; s += 5)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    sb.Append(Sample(c.name, "windowClosed", s, wait.Elapsed.TotalMilliseconds)).Append('\n');
                    if (s < cfg.releaseWaitSeconds) Thread.Sleep(5000);
                }
                UnityEditor.AssetDatabase.DeleteAsset(assetPath);
                UnityEditor.AssetDatabase.DeleteAsset(dir);
            }
        }

        // 確定段の出力の指紋。0.3.0 より前は Color32[]、0.3.0 からは NativePixels(管理配列へ写さずに読む)。
        private static unsafe string HashAny(object output)
        {
            if (output == null) return "none";
            if (output is Color32[] a) return HashPixels(a, a.Length);
            var t = output.GetType();
            int len = (int)t.GetProperty("Length").GetValue(output);
            // Mono の反射はポインタ型のプロパティを GetValue で読めない(型引数にできないと言って投げる)ので、確保した領域の IntPtr を読む。
            var ptr = (Color32*)(IntPtr)t.GetField("_mem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(output);
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < len; i++)
            {
                Color32 c = ptr[i];
                h = (h ^ ((uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24))) * 1099511628211UL;
            }
            return h.ToString("x16");
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
                    if (c.window)
                    {
                        RunWindowCase(c, cfg, sb);
                        continue;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(c.png)), $"{c.name}: PNG を読めません");
                    int w = tex.width, h = tex.height;
                    Color32[] src;
                    if (c.importMaxSize > 0)
                    {
                        // 取り込みの Max Size は長辺をその値へ縮める(縦横比を保つ)。
                        int longest = Math.Max(w, h);
                        int iw = longest > c.importMaxSize ? Math.Max(1, (int)((long)w * c.importMaxSize / longest)) : w;
                        int ih = longest > c.importMaxSize ? Math.Max(1, (int)((long)h * c.importMaxSize / longest)) : h;
                        WorkingResolution.Size(w, h, iw, ih, false, out int ww, out int wh);
                        Assert.IsTrue(WorkingResolution.TryLayout(tex.format, out var layout), $"{c.name}: {tex.format}");
                        src = ww == w && wh == h ? tex.GetPixels32()
                            : WorkingResolution.ResizeArea(tex.GetPixelData<byte>(0), layout, w, h, ww, wh);
                        w = ww;
                        h = wh;
                    }
                    else src = tex.GetPixels32();
                    UnityEngine.Object.DestroyImmediate(tex);

                    var (zones, settings) = LoadZones(File.ReadAllText(c.zones));
                    Assert.IsTrue(zones.Count > 0, $"{c.name}: 有効なゾーンがありません");
                    var baseTol = new float[zones.Count];
                    var baseTarget = new Color[zones.Count];
                    for (int i = 0; i < zones.Count; i++) { baseTol[i] = zones[i].tolerance; baseTarget[i] = zones[i].targetColor; }

                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');

                    var selectionCache = new SelectionCache();
                    // 出力の使い回し(0.3.0 より前の PreviewOutputBuffers と同じく、置き換えた前の出力を 1 本だけ置く)。
                    // 製品の PreviewOutputBuffers は 0.3.0 で形が変わったので、基準の版でもコンパイルできるようここで持つ。
                    Color32[] retiredOutput = null;
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
                        Color32[] pixels;
                        if (cfg.reuseOutput)
                        {
                            pixels = retiredOutput != null && retiredOutput.Length == src.Length
                                ? retiredOutput : new Color32[src.Length];
                            retiredOutput = null;
                            Array.Copy(src, pixels, src.Length);
                        }
                        else pixels = (Color32[])src.Clone();
                        PixelProcessor.ProcessPixelsArray(pixels, w, h, null, zones, settings,
                            CancellationToken.None, selectionCache: selectionCache);
                        string sub = TakeSubPhases();
                        if (cfg.reuseOutput && heldOutput != null) retiredOutput = heldOutput;
                        heldOutput = pixels;
                        double ms = sw.Elapsed.TotalMilliseconds, gcMs = 0;
                        if (cfg.gcEachStep)
                        {
                            var gw = System.Diagnostics.Stopwatch.StartNew();
                            GC.Collect();
                            gcMs = gw.Elapsed.TotalMilliseconds;
                        }
                        sb.Append(Sample(c.name, "step", k, ms, gcMs, cfg.hashOutput ? HashPixels(pixels, src.Length) : null, sub)).Append('\n');

                        if (k == c.exportAfterStep)
                        {
                            // 「テクスチャとして書き出す」相当: 元ファイルの解像度で読み直し、入力の複製を渡さずに
                            // (ExportView と同じく sourcePixels なし・選択キャッシュなし)処理する。作業解像度より大きいと、
                            // その解像度の作業配列がプールに 1 組増える。書き出した画素はこの手の中で手放す。
                            var ew = System.Diagnostics.Stopwatch.StartNew();
                            var exportPixels = LoadSourcePixels(c.png, out int sw0, out int sh0);
                            PixelProcessor.ProcessPixelsArray(exportPixels, sw0, sh0, null, zones, settings, CancellationToken.None);
                            double ems = ew.Elapsed.TotalMilliseconds;
                            sb.Append(Sample(c.name, "export", k, ems, 0, cfg.hashOutput ? HashPixels(exportPixels, exportPixels.Length) : null)).Append('\n');
                            exportPixels = null;
                            // 製品の書き出し(ExportView)は終わったら作業配列の空きを手放す(0.3.0〜)。口がある版では同じく呼ぶ。
                            typeof(PixelProcessor).GetMethod("ReleaseWorkBuffers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                                null, Type.EmptyTypes, null)?.Invoke(null, null);
                            // 書き出しの解像度の作業配列がプールに残ったか(GC の後の生きている量)。
                            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                            sb.Append(Sample(c.name, "liveAfterExport", k, 0)).Append('\n');
                        }
                    }

                    // GC の後の生きている量(プール・キャッシュ・保持している出力)。重なりの場面の前後で比べると、
                    // プールが 2 組目を持ったか(生きている量が 1 組分増える)が、ゴミに埋もれずに見える。
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    sb.Append(Sample(c.name, "liveAfterSteps", cfg.steps, 0)).Append('\n');

                    if (c.cancelOverlapRounds > 0)
                    {
                        // 操作を続けたときのフル段の取り消し: プレビューと同じ PreviewJob で 1 本目を投げ、
                        // 1 手の半分ほどの時間で 2 本目を投げ直す(1 本目はキャンセル)。選択キャッシュは使わない
                        // (どちらも最初から計算する)。PreviewJob が前の計算の終わりを待たない版では 2 本が重なり、
                        // プールにもう 1 組が残る。
                        double sumMs = 0; int nMs = 0;
                        foreach (var line in sb.ToString().Split('\n'))
                        {
                            if (!line.Contains("\"phase\":\"step\"")) continue;
                            int i0 = line.IndexOf("\"ms\":", StringComparison.Ordinal) + 5;
                            int i1 = line.IndexOf(',', i0);
                            sumMs += double.Parse(line.Substring(i0, i1 - i0), System.Globalization.CultureInfo.InvariantCulture);
                            nMs++;
                        }
                        int delayMs = Math.Max(10, (int)(sumMs / Math.Max(1, nMs) * 0.5));
                        using (var job = new PreviewJob<Color32[]>())
                        {
                            for (int r = 0; r < c.cancelOverlapRounds; r++)
                            {
                                var ow = System.Diagnostics.Stopwatch.StartNew();
                                Color32[] applied = null;
                                for (int k = 0; k < 2; k++)
                                {
                                    job.Schedule(tok =>
                                    {
                                        var px = (Color32[])src.Clone();
                                        PixelProcessor.ProcessPixelsArray(px, w, h, null, zones, settings, tok, sourcePixels: src);
                                        return px;
                                    }, px => applied = px);
                                    if (k == 0) Thread.Sleep(delayMs);
                                }
                                while (applied == null)
                                {
                                    if (ow.ElapsedMilliseconds > 600000) Assert.Fail("重なりの計測が終わらない");
                                    PreviewJobMainThread.Drain();
                                    Thread.Sleep(2);
                                }
                                heldOutput = applied;
                                sb.Append(Sample(c.name, "overlap", r, ow.Elapsed.TotalMilliseconds, 0,
                                    cfg.hashOutput ? HashPixels(applied, src.Length) : null)).Append('\n');
                            }
                        }
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        sb.Append(Sample(c.name, "liveAfterOverlap", c.cancelOverlapRounds, 0)).Append('\n');
                    }

                    if (c.refineRounds > 0)
                    {
                        // AI 提案の精密化(右クリックの「ここは塗らない」などのたびに走る)を繰り返したときのヒープと GC。
                        // 提案の代わりに、最初のゾーンのサンプル色に近い画素のマスクを使う(部位ほどの大きさの塊になる)。
                        var sample = (Color32)zones[0].sampleColor;
                        var proposal = new bool[src.Length];
                        for (int i = 0; i < src.Length; i++)
                        {
                            int dr = src[i].r - sample.r, dg = src[i].g - sample.g, db = src[i].b - sample.b;
                            proposal[i] = dr * dr + dg * dg + db * db < 40 * 40;
                        }
                        for (int r = 0; r < c.refineRounds; r++)
                        {
                            var rw = System.Diagnostics.Stopwatch.StartNew();
                            var mask = (bool[])proposal.Clone();
                            SamMaskRefine.SnapBoundary(mask, src, w, h);
                            SamMaskRefine.ExtendFringe(mask, src, w, h);
                            SamMaskRefine.IncludeAaTransition(mask, src, w, h);
                            SamMaskRefine.PeelThinSpill(mask, src, w, h);
                            int on = 0;
                            for (int i = 0; i < mask.Length; i++) if (mask[i]) on++;
                            sb.Append(Sample(c.name, "refine", r, rw.Elapsed.TotalMilliseconds, 0, on.ToString())).Append('\n');
                        }
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        sb.Append(Sample(c.name, "liveAfterRefine", c.refineRounds, 0)).Append('\n');
                    }

                    // 手放す: 出力・選択キャッシュ・プール。その後 GC をかけながら待ち、OS へ返るかを見る。
                    heldOutput = null;
                    retiredOutput = null;
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
