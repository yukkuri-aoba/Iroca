// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Iroca
{
    /// <summary>
    /// いろかが Mono のヒープの外(Marshal.AllocHGlobal)に持つメモリの確保・解放と、その量の数え上げ。
    /// Unity の Mono のヒープ(Boehm)は一度広がると OS へ返らないので、大きくて長く持つ配列はここに置き、
    /// 要らなくなったらすぐ解放する(Windows では大きい塊は解放と同時に OS へ戻る)。
    /// headless ハーネス(.NET 8)でも同じように動く(NativeArray・UnsafeUtility は Unity の中でしか動かない)。
    /// Unity の Profiler には出ないので、量は <see cref="LiveBytes"/> で見る(デバッグ表示と MemoryBench)。
    /// </summary>
    internal static class IrocaNativeMemory
    {
        private static long s_liveBytes;
        // 用途(NativeUsage)ごとのいまの量・最大値・ファイナライザで解放した数(取りこぼし)。
        private const int UsageCount = (int)NativeUsage.Other + 1;
        private static readonly long[] s_liveByUsage = new long[UsageCount];
        private static readonly long[] s_peakByUsage = new long[UsageCount];
        private static readonly int[] s_finalizedByUsage = new int[UsageCount];

        /// <summary>いま確保しているバイト数。</summary>
        public static long LiveBytes => Interlocked.Read(ref s_liveBytes);

        /// <summary>用途ごとの、いま確保しているバイト数。</summary>
        public static long LiveBytesOf(NativeUsage usage) => Interlocked.Read(ref s_liveByUsage[(int)usage]);

        /// <summary>用途ごとの、これまでの最大のバイト数(<see cref="ResetPeaks"/> から)。</summary>
        public static long PeakBytesOf(NativeUsage usage) => Interlocked.Read(ref s_peakByUsage[(int)usage]);

        /// <summary>
        /// 用途ごとの、持ち主が返さずにファイナライザが解放した塊の数(<see cref="NativeBuffer"/>)。取りこぼしなので
        /// 0 のはず(テストで確かめる)。
        /// </summary>
        public static int FinalizedCount(NativeUsage usage) => Volatile.Read(ref s_finalizedByUsage[(int)usage]);

        /// <summary>最大値をいまの量に戻す(計測の区切り)。</summary>
        public static void ResetPeaks()
        {
            for (int u = 0; u < UsageCount; u++)
                Interlocked.Exchange(ref s_peakByUsage[u], Interlocked.Read(ref s_liveByUsage[u]));
        }

        internal static void NoteFinalized(NativeUsage usage) => Interlocked.Increment(ref s_finalizedByUsage[(int)usage]);

        /// <summary>
        /// 検証用: IROCA_NATIVE_GUARD=1 なら、確保する領域の末尾をページの境目にそろえ、直後のページを読み書きできない
        /// ページ(ガードページ)にする(Windows だけ。ほかの OS では普段どおり確保する)。寸法の計算違いで塊の後ろへ
        /// 1 バイトでもはみ出すと、その場でアクセス違反になる(普段は隣の塊やヒープを黙って壊し、後で別の所で落ちる)。
        /// 解放した塊は OS へ返すので、解放後の読み書きもアクセス違反になる。作業配列のプール(NativeWorkPool)の
        /// 切り上げと使い回しもやめる。確保のたびに OS から 1 ページ余分に取るので遅く、中身は 0 で始まる
        /// (普段は不定。書く前に読む所は IROCA_POOL_POISON で探す)。製品では使わない。使い方は docs/testing-architecture.md。
        /// </summary>
        public static readonly bool GuardPages =
            Environment.GetEnvironmentVariable("IROCA_NATIVE_GUARD") == "1"
            && Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>
        /// bytes バイトを確保する(中身は不定)。ふつうは <see cref="NativeBuffer"/> を通す(持ち主と後始末の決まりが
        /// そこにある)。usage は数え上げの区分で、<see cref="Free"/> にも同じ値を渡す。
        /// </summary>
        public static IntPtr Alloc(long bytes, NativeUsage usage)
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            IntPtr p = GuardPages ? GuardedAlloc(bytes) : Marshal.AllocHGlobal(new IntPtr(bytes));
            Interlocked.Add(ref s_liveBytes, bytes);
            long now = Interlocked.Add(ref s_liveByUsage[(int)usage], bytes);
            long peak;
            while (now > (peak = Interlocked.Read(ref s_peakByUsage[(int)usage]))
                   && Interlocked.CompareExchange(ref s_peakByUsage[(int)usage], now, peak) != peak) { }
            return p;
        }

        public static void Free(IntPtr p, long bytes, NativeUsage usage)
        {
            if (p == IntPtr.Zero) return;
            if (GuardPages) GuardedFree(p);
            else Marshal.FreeHGlobal(p);
            Interlocked.Add(ref s_liveBytes, -bytes);
            Interlocked.Add(ref s_liveByUsage[(int)usage], -bytes);
        }

        // ガードページつきで取った領域の先頭 → OS から取った塊の先頭(解放に使う)。
        private static readonly Dictionary<IntPtr, IntPtr> s_guardedBases = new Dictionary<IntPtr, IntPtr>();

        private static IntPtr GuardedAlloc(long bytes)
        {
            long page = Environment.SystemPageSize;
            long dataBytes = (bytes + page - 1) / page * page;
            IntPtr block = VirtualAlloc(IntPtr.Zero, new UIntPtr((ulong)(dataBytes + page)), MemCommit | MemReserve, PageReadWrite);
            if (block == IntPtr.Zero)
                throw new OutOfMemoryException($"VirtualAlloc に失敗しました（{bytes} バイト、エラー {Marshal.GetLastWin32Error()}）");
            var guard = new IntPtr(block.ToInt64() + dataBytes);
            if (!VirtualProtect(guard, new UIntPtr((ulong)page), PageNoAccess, out _))
            {
                int error = Marshal.GetLastWin32Error();
                VirtualFree(block, UIntPtr.Zero, MemRelease);
                throw new InvalidOperationException($"ガードページを作れませんでした（エラー {error}）");
            }
            // 要求長ちょうどで、末尾がガードページの直前に来る位置を返す(寸法が 4 の倍数なら 4 バイト境界にそろう)。
            var p = new IntPtr(guard.ToInt64() - bytes);
            lock (s_guardedBases) s_guardedBases.Add(p, block);
            return p;
        }

        private static void GuardedFree(IntPtr p)
        {
            IntPtr block;
            lock (s_guardedBases)
            {
                if (!s_guardedBases.TryGetValue(p, out block))
                    throw new InvalidOperationException("IrocaNativeMemory.Alloc が返した領域ではありません（二重の解放か、ずれた番地）");
                s_guardedBases.Remove(p);
            }
            VirtualFree(block, UIntPtr.Zero, MemRelease);
        }

        private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000;
        private const uint PageNoAccess = 0x01, PageReadWrite = 0x04;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

        /// <summary>
        /// このスレッドが中断されている最中か(Mono のドメインのアンロードは、残っているスレッドを中断する)。
        /// 並列で写している途中に呼び出し元のスレッドだけが中断されると、ほかのスレッドがまだその領域を読み書きしている。
        /// そのときは解放せず、アンロードの後で走るファイナライザに任せる(.NET 8 では常に false)。
        /// </summary>
        public static bool CurrentThreadAborting =>
            (Thread.CurrentThread.ThreadState & ThreadState.AbortRequested) != 0;
    }

    /// <summary>
    /// ネイティブメモリに置いた float の並び(選択キャッシュの強さ)。中身の出し入れは配列・ポインタとの一括コピーだけ。
    /// 持ち方は <see cref="NativeBuffer"/> と同じ参照の数: 作った側が 1 つ目を持ち、写している間の読み手は
    /// <see cref="AddRef"/> して持ち、<see cref="Release"/> で返す。返し忘れは NativeBuffer のファイナライザが解放する。
    /// </summary>
    internal sealed unsafe class NativeFloatBuffer : IDisposable
    {
        private readonly NativeBuffer _mem;
        public int Length { get; }

        // 大きい一括コピーは帯に分けて並列にする(4K の 64MB を 1 本で写すと数十 ms かかる)。
        private const int CopyChunk = 1 << 18;

        public NativeFloatBuffer(int length)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            Length = length;
            _mem = new NativeBuffer((long)length * sizeof(float), NativeUsage.Selection);
        }

        /// <summary>いまの参照の数(持ち主が「自分だけが持っているか」を見るため)。</summary>
        public int RefCount => _mem.RefCount;

        /// <summary>参照を 1 つ足す(解放済みなら投げる)。</summary>
        public NativeFloatBuffer AddRef()
        {
            _mem.AddRef();
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照なら解放する。返しすぎは投げる。</summary>
        public void Release() => _mem.Release();

        /// <summary>持ち主が 1 つのときの後始末(<see cref="Release"/> と同じ)。</summary>
        public void Dispose() => Release();

        public void CopyFrom(float[] src, int count)
        {
            if (count > src.Length) throw new ArgumentOutOfRangeException(nameof(count));
            fixed (float* p = src) Copy(p, count, toNative: true);
        }

        public void CopyTo(float[] dst, int count)
        {
            if (count > dst.Length) throw new ArgumentOutOfRangeException(nameof(count));
            fixed (float* p = dst) Copy(p, count, toNative: false);
        }

        // 長さ count 以上の領域との出し入れ(作業配列のプールが貸すポインタ)。
        public void CopyFrom(float* src, int count) => Copy(src, count, toNative: true);

        public void CopyTo(float* dst, int count) => Copy(dst, count, toNative: false);

        // 少数の画素だけを読む(右クリックの宛先を決める 5×5 画素など)。範囲外は例外。
        public void ReadAt(int[] indices, float[] dst)
        {
            float* mine = (float*)_mem.Ptr;
            if (mine == null) throw new ObjectDisposedException(nameof(NativeFloatBuffer));
            if (dst.Length < indices.Length) throw new ArgumentOutOfRangeException(nameof(dst));
            for (int i = 0; i < indices.Length; i++)
            {
                int k = indices[i];
                if ((uint)k >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(indices));
                dst[i] = mine[k];
            }
        }

        private void Copy(float* other, int count, bool toNative)
        {
            float* mine = (float*)_mem.Ptr;
            if (mine == null) throw new ObjectDisposedException(nameof(NativeFloatBuffer));
            if (count < 0 || count > Length) throw new ArgumentOutOfRangeException(nameof(count));
            int chunks = (count + CopyChunk - 1) / CopyChunk;
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism() }, c =>
            {
                int s0 = c * CopyChunk;
                long bytes = (long)Math.Min(CopyChunk, count - s0) * sizeof(float);
                if (toNative) Buffer.MemoryCopy(other + s0, mine + s0, bytes, bytes);
                else Buffer.MemoryCopy(mine + s0, other + s0, bytes, bytes);
            });
        }

    }
}
