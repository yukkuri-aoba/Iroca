// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 作業配列を Mono のヒープの外(Marshal.AllocHGlobal)へ移すときの書き方を決めるための速さ比べ(0.3.0)。
    /// Core の代表的なループの形(画素ごとの照合・行方向の窓和・列方向の窓和・3 本の合成)を、
    /// 管理配列(今の書き方)・ポインタ・ポインタを包んだ構造体の添字・行ごとの Span の 4 通りで回す。
    /// headless ハーネス(.NET 8 の RyuJIT)は参考にならないので Unity の Mono で測る。Debug の最適化モード
    /// (Run-EditorTests の -DebugCodeOptimization)ではインライン展開が止まるので、Release と両方で回す。
    /// 普段は IROCA_KERNEL_REPORT が無いので Ignore。実行は <c>Run-EditorTests.ps1 -KernelBench</c>。
    /// </summary>
    [Category("Kernel")]
    public unsafe class KernelBenchTests
    {
        private const int W = 4096, H = 4096, N = W * H;
        private const int Reps = 9, Warmup = 2;

        // ポインタを包んだ構造体。添字は ref を返す(配列と同じ書き方で読み書きできる)。
        private readonly struct NBuf
        {
            public readonly float* P;
            public NBuf(float* p) { P = p; }
            public ref float this[int i]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref P[i];
            }
        }

        // 同じものの総称型版(float・bool・int・byte・Color32 を 1 つの型で扱う形。Mono が値型ごとに展開するか)。
        private readonly struct GBuf<T> where T : unmanaged
        {
            public readonly T* P;
            public GBuf(T* p) { P = p; }
            public ref T this[int i]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref P[i];
            }
        }

        private static void MatchGBuf(Color32[] px, GBuf<float> dst) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            for (int x = 0; x < W; x++) dst[o + x] = MatchOne(px[o + x]);
        });

        private static void RowGBuf(GBuf<float> src, GBuf<float> dst) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            float sum = 0f;
            for (int k = 0; k <= R; k++) sum += src[o + k];
            dst[o] = sum;
            for (int x = 1; x < W; x++)
            {
                int sub = x - 1 - R, add = x + R;
                if (sub >= 0) sum -= src[o + sub];
                if (add < W) sum += src[o + add];
                dst[o + x] = sum;
            }
        });

        private static void BlendGBuf(GBuf<float> a, GBuf<float> b, GBuf<float> c, GBuf<float> d) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            for (int x = 0; x < W; x++) d[o + x] = a[o + x] * b[o + x] + c[o + x];
        });

        private static ParallelOptions Po => new ParallelOptions { MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism() };

        // ───────── 照合(画素の色 → 強さ) ─────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float MatchOne(Color32 c)
        {
            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
            float mx = Mathf.Max(r, Mathf.Max(g, b)), mn = Mathf.Min(r, Mathf.Min(g, b));
            float s = mx > 0f ? (mx - mn) / mx : 0f;
            float d = (r - 0.3f) * (r - 0.3f) + (g - 0.5f) * (g - 0.5f) + (b - 0.7f) * (b - 0.7f);
            return mx > 0.1f ? Mathf.Clamp01(1f - Mathf.Sqrt(d) * s) : 0f;
        }

        private static void MatchArr(Color32[] px, float[] dst) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            for (int x = 0; x < W; x++) dst[o + x] = MatchOne(px[o + x]);
        });

        private static void MatchPtr(Color32[] px, float* dst) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            float* row = dst + o;
            for (int x = 0; x < W; x++) row[x] = MatchOne(px[o + x]);
        });

        private static void MatchBuf(Color32[] px, NBuf dst) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            for (int x = 0; x < W; x++) dst[o + x] = MatchOne(px[o + x]);
        });

        private static void MatchSpan(Color32[] px, IntPtr dstP) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            var row = new Span<float>((float*)dstP + o, W);
            for (int x = 0; x < W; x++) row[x] = MatchOne(px[o + x]);
        });

        // ───────── 行方向の窓和(BoxFilterSum の水平パスと同じ形) ─────────
        private const int R = 4;

        private static void RowArr(float[] src, float[] dst) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            float sum = 0f;
            for (int k = 0; k <= R; k++) sum += src[o + k];
            dst[o] = sum;
            for (int x = 1; x < W; x++)
            {
                int sub = x - 1 - R, add = x + R;
                if (sub >= 0) sum -= src[o + sub];
                if (add < W) sum += src[o + add];
                dst[o + x] = sum;
            }
        });

        private static void RowPtr(float* src, float* dst) => Parallel.For(0, H, Po, y =>
        {
            float* s = src + y * W, d = dst + y * W;
            float sum = 0f;
            for (int k = 0; k <= R; k++) sum += s[k];
            d[0] = sum;
            for (int x = 1; x < W; x++)
            {
                int sub = x - 1 - R, add = x + R;
                if (sub >= 0) sum -= s[sub];
                if (add < W) sum += s[add];
                d[x] = sum;
            }
        });

        private static void RowBuf(NBuf src, NBuf dst) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            float sum = 0f;
            for (int k = 0; k <= R; k++) sum += src[o + k];
            dst[o] = sum;
            for (int x = 1; x < W; x++)
            {
                int sub = x - 1 - R, add = x + R;
                if (sub >= 0) sum -= src[o + sub];
                if (add < W) sum += src[o + add];
                dst[o + x] = sum;
            }
        });

        private static void RowSpan(IntPtr srcP, IntPtr dstP) => Parallel.For(0, H, Po, y =>
        {
            var s = new ReadOnlySpan<float>((float*)srcP + y * W, W);
            var d = new Span<float>((float*)dstP + y * W, W);
            float sum = 0f;
            for (int k = 0; k <= R; k++) sum += s[k];
            d[0] = sum;
            for (int x = 1; x < W; x++)
            {
                int sub = x - 1 - R, add = x + R;
                if (sub >= 0) sum -= s[sub];
                if (add < W) sum += s[add];
                d[x] = sum;
            }
        });

        // ───────── 列方向の窓和(16 列ずつ行方向に進む。BoxFilterSum の垂直パスと同じ形) ─────────
        private const int VBlock = 16;

        private static void ColArr(float[] src, float[] dst) => Parallel.For(0, W / VBlock, Po, bi =>
        {
            int xs = bi * VBlock;
            var sums = new float[VBlock];
            for (int k = 0; k <= R; k++) for (int j = 0; j < VBlock; j++) sums[j] += src[k * W + xs + j];
            for (int j = 0; j < VBlock; j++) dst[xs + j] = sums[j];
            for (int y = 1; y < H; y++)
            {
                int sub = y - 1 - R, add = y + R;
                if (sub >= 0) { int o = sub * W + xs; for (int j = 0; j < VBlock; j++) sums[j] -= src[o + j]; }
                if (add < H) { int o = add * W + xs; for (int j = 0; j < VBlock; j++) sums[j] += src[o + j]; }
                int d = y * W + xs;
                for (int j = 0; j < VBlock; j++) dst[d + j] = sums[j];
            }
        });

        private static void ColPtr(float* src, float* dst) => Parallel.For(0, W / VBlock, Po, bi =>
        {
            int xs = bi * VBlock;
            var sums = new float[VBlock];
            for (int k = 0; k <= R; k++) { float* s = src + k * W + xs; for (int j = 0; j < VBlock; j++) sums[j] += s[j]; }
            for (int j = 0; j < VBlock; j++) dst[xs + j] = sums[j];
            for (int y = 1; y < H; y++)
            {
                int sub = y - 1 - R, add = y + R;
                if (sub >= 0) { float* s = src + sub * W + xs; for (int j = 0; j < VBlock; j++) sums[j] -= s[j]; }
                if (add < H) { float* s = src + add * W + xs; for (int j = 0; j < VBlock; j++) sums[j] += s[j]; }
                float* d = dst + y * W + xs;
                for (int j = 0; j < VBlock; j++) d[j] = sums[j];
            }
        });

        private static void ColBuf(NBuf src, NBuf dst) => Parallel.For(0, W / VBlock, Po, bi =>
        {
            int xs = bi * VBlock;
            var sums = new float[VBlock];
            for (int k = 0; k <= R; k++) for (int j = 0; j < VBlock; j++) sums[j] += src[k * W + xs + j];
            for (int j = 0; j < VBlock; j++) dst[xs + j] = sums[j];
            for (int y = 1; y < H; y++)
            {
                int sub = y - 1 - R, add = y + R;
                if (sub >= 0) { int o = sub * W + xs; for (int j = 0; j < VBlock; j++) sums[j] -= src[o + j]; }
                if (add < H) { int o = add * W + xs; for (int j = 0; j < VBlock; j++) sums[j] += src[o + j]; }
                int d = y * W + xs;
                for (int j = 0; j < VBlock; j++) dst[d + j] = sums[j];
            }
        });

        // ───────── 3 本の合成(d = a*b + c) ─────────
        private static void BlendArr(float[] a, float[] b, float[] c, float[] d) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            for (int x = 0; x < W; x++) d[o + x] = a[o + x] * b[o + x] + c[o + x];
        });

        private static void BlendPtr(float* a, float* b, float* c, float* d) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            float* pa = a + o, pb = b + o, pc = c + o, pd = d + o;
            for (int x = 0; x < W; x++) pd[x] = pa[x] * pb[x] + pc[x];
        });

        private static void BlendBuf(NBuf a, NBuf b, NBuf c, NBuf d) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            for (int x = 0; x < W; x++) d[o + x] = a[o + x] * b[o + x] + c[o + x];
        });

        private static void BlendSpan(IntPtr a, IntPtr b, IntPtr c, IntPtr d) => Parallel.For(0, H, Po, y =>
        {
            int o = y * W;
            var sa = new ReadOnlySpan<float>((float*)a + o, W);
            var sb = new ReadOnlySpan<float>((float*)b + o, W);
            var sc = new ReadOnlySpan<float>((float*)c + o, W);
            var sd = new Span<float>((float*)d + o, W);
            for (int x = 0; x < W; x++) sd[x] = sa[x] * sb[x] + sc[x];
        });

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        private static (double med, double min) Time(Action a)
        {
            for (int i = 0; i < Warmup; i++) a();
            var t = new List<double>();
            for (int i = 0; i < Reps; i++)
            {
                var sw = Stopwatch.StartNew();
                a();
                t.Add(sw.Elapsed.TotalMilliseconds);
            }
            double min = double.MaxValue;
            foreach (var x in t) min = Math.Min(min, x);
            return (Median(t), min);
        }

        private static ulong Sum(float* p)
        {
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < N; i++) h = (h ^ (uint)BitConverter.SingleToInt32Bits(p[i])) * 1099511628211UL;
            return h;
        }

        private static ulong Sum(float[] a) { fixed (float* p = a) return Sum(p); }

        [Test]
        public void CompareLoopStyles()
        {
            string report = Environment.GetEnvironmentVariable("IROCA_KERNEL_REPORT");
            if (string.IsNullOrEmpty(report)) Assert.Ignore("IROCA_KERNEL_REPORT が未設定です（Run-EditorTests.ps1 -KernelBench で明示実行）");

            var rng = new System.Random(1);
            var px = new Color32[N];
            for (int i = 0; i < N; i++) px[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
            float[] a = new float[N], b = new float[N], c = new float[N], d = new float[N];
            for (int i = 0; i < N; i++) { a[i] = (float)rng.NextDouble(); b[i] = (float)rng.NextDouble(); c[i] = (float)rng.NextDouble(); }

            long bytes = (long)N * sizeof(float);
            IntPtr na = Marshal.AllocHGlobal(new IntPtr(bytes)), nb = Marshal.AllocHGlobal(new IntPtr(bytes)),
                   nc = Marshal.AllocHGlobal(new IntPtr(bytes)), nd = Marshal.AllocHGlobal(new IntPtr(bytes));
            var sb = new StringBuilder();
            try
            {
                Marshal.Copy(a, 0, na, N); Marshal.Copy(b, 0, nb, N); Marshal.Copy(c, 0, nc, N);
                float* pa = (float*)na, pb = (float*)nb, pc = (float*)nc, pd = (float*)nd;
                var ba = new NBuf(pa); var bb = new NBuf(pb); var bc = new NBuf(pc); var bd = new NBuf(pd);
                var ga = new GBuf<float>(pa); var gb = new GBuf<float>(pb); var gc = new GBuf<float>(pc); var gd = new GBuf<float>(pd);
                bool debugMode = UnityEditor.Compilation.CompilationPipeline.codeOptimization == UnityEditor.Compilation.CodeOptimization.Debug;

                void Run(string kernel, string variant, Action act, Func<ulong> check)
                {
                    var (med, min) = Time(act);
                    ulong h = check();
                    sb.Append($"{{\"kernel\":\"{kernel}\",\"variant\":\"{variant}\",\"debug\":{(debugMode ? "true" : "false")}," +
                              $"\"median\":{med.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}," +
                              $"\"min\":{min.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\"check\":\"{h:x16}\"}}\n");
                }

                Run("match", "arr", () => MatchArr(px, d), () => Sum(d));
                Run("match", "ptr", () => MatchPtr(px, pd), () => Sum(pd));
                Run("match", "buf", () => MatchBuf(px, bd), () => Sum(pd));
                Run("match", "span", () => MatchSpan(px, nd), () => Sum(pd));
                Run("match", "gbuf", () => MatchGBuf(px, gd), () => Sum(pd));
                Run("row", "arr", () => RowArr(a, d), () => Sum(d));
                Run("row", "ptr", () => RowPtr(pa, pd), () => Sum(pd));
                Run("row", "buf", () => RowBuf(ba, bd), () => Sum(pd));
                Run("row", "span", () => RowSpan(na, nd), () => Sum(pd));
                Run("row", "gbuf", () => RowGBuf(ga, gd), () => Sum(pd));
                Run("col", "arr", () => ColArr(a, d), () => Sum(d));
                Run("col", "ptr", () => ColPtr(pa, pd), () => Sum(pd));
                Run("col", "buf", () => ColBuf(ba, bd), () => Sum(pd));
                Run("blend", "arr", () => BlendArr(a, b, c, d), () => Sum(d));
                Run("blend", "ptr", () => BlendPtr(pa, pb, pc, pd), () => Sum(pd));
                Run("blend", "buf", () => BlendBuf(ba, bb, bc, bd), () => Sum(pd));
                Run("blend", "span", () => BlendSpan(na, nb, nc, nd), () => Sum(pd));
                Run("blend", "gbuf", () => BlendGBuf(ga, gb, gc, gd), () => Sum(pd));
            }
            finally
            {
                Marshal.FreeHGlobal(na); Marshal.FreeHGlobal(nb); Marshal.FreeHGlobal(nc); Marshal.FreeHGlobal(nd);
                File.WriteAllText(report, sb.ToString());
            }
        }
    }
}
