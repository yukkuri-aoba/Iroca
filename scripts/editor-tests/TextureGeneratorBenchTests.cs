// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Profiling;

namespace Iroca.EditorTests {
    /// <summary>
    /// 非破壊ビルドの差し替えテクスチャを作る 2 つの方法を、同じ原本の画素で比べる(点検 D-8 段 2 の判断の材料。2026-11-11)。
    /// <list type="bullet">
    /// <item>以前の作り(RecipeTextureBuilder の CreateMatchingByApply): 取り込み後の寸法へ面積平均で縮め、Apply の単純なフィルタでミップを作り、
    ///   CompressTexture で取り込みと同じ形式へ。</item>
    /// <item>製品の作り(CreateMatching): 取り込み設定があれば TextureGenerator.GenerateTexture で、取り込みと同じ処理(縮小・ミップの
    ///   フィルタ・Alpha・圧縮)に元の取り込み設定と今のビルド対象のプラットフォーム設定を渡す(2026-11-11 から)。</item>
    /// </list>
    /// 1 回ごとに、時間・呼び出しの間のプロセスの Private の山(別スレッドで 1ms ごとに読む)・終わった後の残り・Mono のヒープ・
    /// Unity のネイティブの量と、出来たテクスチャの生データが取り込み済みのもの(元テクスチャ)と同じかを書く。
    /// 普段は IROCA_TEXGEN_BENCH_CASES が無いので Ignore。ケースの JSON は dev_safe に置く(公開リポに素材の名前を書かない)。
    /// </summary>
    [Category("TextureGeneratorBench")]
    public class TextureGeneratorBenchTests {
        [Serializable] private class CaseDto { public string name = ""; public string png = ""; public int maxSize = 4096; public string format = "BC7"; public string mipFilter = "Kaiser"; public bool crunch; public string platform = "Standalone"; public bool overrideFormat = true; }
        [Serializable] private class CasesDto { public int repeats = 2; public List<CaseDto> cases = new List<CaseDto>(); }

        private const string Folder = "Assets/IrocaTexGenBench";
        private const long MB = 1024 * 1024;

        [StructLayout(LayoutKind.Sequential, Size = 72)]
        private struct ProcessMemoryCounters {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr p, out ProcessMemoryCounters c, uint cb);

        private static long PrivateBytes() {
            GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
            return (long)c.PagefileUsage.ToUInt64();
        }

        [TearDown]
        public void TearDown() {
            if (AssetDatabase.IsValidFolder(Folder)) {
                AssetDatabase.DeleteAsset(Folder);
            }
        }

        [Test]
        public unsafe void CurrentBuilder_vs_TextureGenerator() {
            string casesPath = Environment.GetEnvironmentVariable("IROCA_TEXGEN_BENCH_CASES");
            string report = Environment.GetEnvironmentVariable("IROCA_TEXGEN_BENCH_REPORT");
            if (string.IsNullOrEmpty(casesPath) || string.IsNullOrEmpty(report)) {
                Assert.Ignore("IROCA_TEXGEN_BENCH_CASES と IROCA_TEXGEN_BENCH_REPORT が要ります(D-8 段 2 の計測)");
            }
            var cfg = JsonUtility.FromJson<CasesDto>(File.ReadAllText(casesPath));
            var byApply = typeof(RecipeTextureBuilder).GetMethod("CreateMatchingByApply", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(NativePixels), typeof(Texture2D) }, null);
            Assert.IsNotNull(byApply, "RecipeTextureBuilder.CreateMatchingByApply(NativePixels, Texture2D)");
            // 製品の作り(取り込み設定があれば TextureGenerator。プラットフォーム設定は今のビルド対象から選ぶ)
            var builder = typeof(RecipeTextureBuilder).GetMethod("CreateMatching", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(NativePixels), typeof(Texture2D), typeof(bool) }, null);
            Assert.IsNotNull(builder, "RecipeTextureBuilder.CreateMatching(NativePixels, Texture2D, bool)");
            if (!AssetDatabase.IsValidFolder(Folder)) {
                AssetDatabase.CreateFolder("Assets", "IrocaTexGenBench");
            }
            var sb = new StringBuilder();
            foreach (var c in cfg.cases) {
                string assetPath = $"{Folder}/{c.name}.png";
                File.Copy(c.png, Path.GetFullPath(assetPath), true);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                importer.isReadable = true;   // 取り込み済みの生データと比べるため(山と残りは前後の差で見るので、元テクスチャの分は差に入らない)
                importer.mipmapEnabled = true;
                importer.mipmapFilter = c.mipFilter == "Box" ? TextureImporterMipFilter.BoxFilter : TextureImporterMipFilter.KaiserFilter;
                importer.maxTextureSize = c.maxSize;
                importer.alphaIsTransparency = false;
                // overrideFormat なら c.platform の設定を上書きする。しなければ既定(Automatic)のまま取り込む。
                if (c.overrideFormat) {
                    var ps = importer.GetPlatformTextureSettings(c.platform);
                    ps.overridden = true;
                    ps.maxTextureSize = c.maxSize;
                    ps.format = (TextureImporterFormat)Enum.Parse(typeof(TextureImporterFormat), c.format);
                    ps.crunchedCompression = c.crunch;
                    ps.compressionQuality = 50;
                    importer.SetPlatformTextureSettings(ps);
                }
                importer.SaveAndReimport();
                var like = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                byte[] likeRaw = like.GetRawTextureData();
                string likeHash = Hash(likeRaw);
                likeRaw = null;

                var pixels = PngFileDecoder.TryDecode(c.png);
                Assert.IsNotNull(pixels, c.png);
                try {
                    for (int rep = 0; rep < cfg.repeats; rep++) {
                        foreach (string way in new[] { "byApply", "builder" }) {
                            sb.Append(Measure(c, way, rep, likeHash, like, pixels, way == "byApply" ? byApply : builder)).Append('\n');
                            File.WriteAllText(report, sb.ToString());
                        }
                    }
                } finally {
                    pixels.Release();
                }
                AssetDatabase.DeleteAsset(assetPath);
            }
            File.WriteAllText(report, sb.ToString());
        }

        private static unsafe string Measure(CaseDto c, string way, int rep, string likeHash, Texture2D like, NativePixels pixels,
            MethodInfo make) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long monoBefore = Profiler.GetMonoHeapSizeLong(), nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
            long privBefore = PrivateBytes();
            long peak = privBefore;
            bool stop = false;
            var sampler = new Thread(() => {
                while (!Volatile.Read(ref stop)) {
                    long p = PrivateBytes();
                    if (p > Interlocked.Read(ref peak)) {
                        Interlocked.Exchange(ref peak, p);
                    }
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            sampler.Start();
            var sw = Stopwatch.StartNew();
            Texture2D tex = null;
            try {
                // 3 つ目の引数は「原本ファイルを読んだ画素か」(製品の作りだけが受ける。計測は PNG を読んで渡す)。
                tex = (Texture2D)make.Invoke(null, make.GetParameters().Length == 3
                    ? new object[] { pixels, like, true } : new object[] { pixels, like });
            } finally {
                sw.Stop();
                Volatile.Write(ref stop, true);
                sampler.Join();
            }
            long privPeak = Interlocked.Read(ref peak);
            string result = "null", sameAsImport = "-";
            int w = 0, h = 0, mips = 0;
            string fmt = "";
            if (tex != null) {
                byte[] raw = tex.GetRawTextureData();
                result = Hash(raw);
                sameAsImport = (result == likeHash).ToString().ToLowerInvariant();
                w = tex.width; h = tex.height; mips = tex.mipmapCount; fmt = tex.format.ToString();
            }
            long privWithOutput = PrivateBytes();
            if (tex != null) {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long privAfter = PrivateBytes();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return "{" +
                   $"\"case\":\"{c.name}\",\"way\":\"{way}\",\"rep\":{rep},\"ms\":{sw.Elapsed.TotalMilliseconds.ToString("F0", inv)}," +
                   $"\"src\":\"{pixels.Width}x{pixels.Height}\",\"out\":\"{w}x{h}\",\"mips\":{mips},\"format\":\"{fmt}\"," +
                   $"\"privPeakDeltaMB\":{(privPeak - privBefore) / MB},\"privWithOutputDeltaMB\":{(privWithOutput - privBefore) / MB}," +
                   $"\"privAfterDeltaMB\":{(privAfter - privBefore) / MB}," +
                   $"\"monoHeapDeltaMB\":{(Profiler.GetMonoHeapSizeLong() - monoBefore) / MB}," +
                   $"\"nativeAllocDeltaMB\":{(Profiler.GetTotalAllocatedMemoryLong() - nativeBefore) / MB}," +
                   $"\"platform\":\"{EditorUserBuildSettings.activeBuildTarget}\",\"sameAsImport\":{sameAsImport},\"hash\":\"{result}\"" + "}";
        }

        private static string Hash(byte[] data) {
            using (var sha = System.Security.Cryptography.SHA256.Create()) {
                return BitConverter.ToString(sha.ComputeHash(data), 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
