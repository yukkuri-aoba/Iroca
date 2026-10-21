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

        /// <summary>いま確保しているバイト数。</summary>
        public static long LiveBytes => Interlocked.Read(ref s_liveBytes);

        public static IntPtr Alloc(long bytes)
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            IntPtr p = Marshal.AllocHGlobal(new IntPtr(bytes));
            Interlocked.Add(ref s_liveBytes, bytes);
            return p;
        }

        public static void Free(IntPtr p, long bytes)
        {
            if (p == IntPtr.Zero) return;
            Marshal.FreeHGlobal(p);
            Interlocked.Add(ref s_liveBytes, -bytes);
        }

        /// <summary>
        /// このスレッドが中断されている最中か(Mono のドメインのアンロードは、残っているスレッドを中断する)。
        /// 並列で写している途中に呼び出し元のスレッドだけが中断されると、ほかのスレッドがまだその領域を読み書きしている。
        /// そのときは解放せず、アンロードの後で走るファイナライザに任せる(.NET 8 では常に false)。
        /// </summary>
        public static bool CurrentThreadAborting =>
            (Thread.CurrentThread.ThreadState & ThreadState.AbortRequested) != 0;
    }

    /// <summary>
    /// <see cref="IrocaNativeMemory"/> から確保した 1 つの塊の持ち主。確保した直後に包んで、本当の持ち主
    /// (色の表など)に渡すまでの守りに使う。Dispose で解放し、Dispose を忘れても GC が回収するときに解放する。
    /// 中断中(<see cref="IrocaNativeMemory.CurrentThreadAborting"/>)の Dispose は解放しない。並列で書いている途中の
    /// スレッドが残っていることがあるからで、塊はドメインのアンロードまで静的に持っておく(ほかの持ち主の守りと
    /// 同じ考え方。手放すと、アンロードより前に GC がファイナライザで解放してしまうことがある。アンロードは
    /// スレッドを止めてからファイナライザを走らせるので、そこで解放される)。
    /// </summary>
    internal sealed class NativeBlock : IDisposable
    {
        private static readonly List<NativeBlock> s_keptUntilUnload = new List<NativeBlock>();

        private IntPtr _ptr;
        public long Bytes { get; }
        public IntPtr Ptr => _ptr;

        public NativeBlock(long bytes)
        {
            Bytes = bytes;
            _ptr = IrocaNativeMemory.Alloc(bytes);
        }

        ~NativeBlock() => Free();

        public void Dispose()
        {
            if (IrocaNativeMemory.CurrentThreadAborting)
            {
                lock (s_keptUntilUnload) s_keptUntilUnload.Add(this);
                return;
            }
            Free();
            GC.SuppressFinalize(this);
        }

        private void Free()
        {
            IntPtr p = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
            if (p != IntPtr.Zero) IrocaNativeMemory.Free(p, Bytes);
        }
    }

    /// <summary>
    /// ネイティブメモリに置いた float の並び。中身の出し入れは配列・ポインタとの一括コピーだけ。
    /// 1 つの持ち主が使い、ほかのスレッドから同時に Dispose しないこと(持ち主がロックで守る)。
    /// Dispose を忘れても、GC がこのオブジェクトを回収するときに解放する(ただしいつになるかは分からない)。
    /// </summary>
    internal sealed unsafe class NativeFloatBuffer : IDisposable
    {
        private IntPtr _ptr;
        public int Length { get; }

        // 大きい一括コピーは帯に分けて並列にする(4K の 64MB を 1 本で写すと数十 ms かかる)。
        private const int CopyChunk = 1 << 18;

        public NativeFloatBuffer(int length)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            Length = length;
            _ptr = IrocaNativeMemory.Alloc((long)length * sizeof(float));
        }

        ~NativeFloatBuffer() => Release();

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
            if (_ptr == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeFloatBuffer));
            if (dst.Length < indices.Length) throw new ArgumentOutOfRangeException(nameof(dst));
            float* mine = (float*)_ptr;
            for (int i = 0; i < indices.Length; i++)
            {
                int k = indices[i];
                if ((uint)k >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(indices));
                dst[i] = mine[k];
            }
        }

        private void Copy(float* other, int count, bool toNative)
        {
            if (_ptr == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeFloatBuffer));
            if (count < 0 || count > Length) throw new ArgumentOutOfRangeException(nameof(count));
            float* mine = (float*)_ptr;
            int chunks = (count + CopyChunk - 1) / CopyChunk;
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism() }, c =>
            {
                int s0 = c * CopyChunk;
                long bytes = (long)Math.Min(CopyChunk, count - s0) * sizeof(float);
                if (toNative) Buffer.MemoryCopy(other + s0, mine + s0, bytes, bytes);
                else Buffer.MemoryCopy(mine + s0, other + s0, bytes, bytes);
            });
        }

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            IntPtr p = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
            if (p != IntPtr.Zero) IrocaNativeMemory.Free(p, (long)Length * sizeof(float));
        }
    }
}
