// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// Mono のヒープの外(<see cref="IrocaNativeMemory"/>)に置いた画素(Color32。並びは GetPixels32 と同じで行 0 = 画像下端)。
    /// プレビューの原本と確定段の出力(4K で 64MB、8K で 256MB ずつ)に使う。Unity の Mono のヒープは一度広がると
    /// OS へ返らないので、管理配列だとウィンドウを閉じた後もその分がエディタのメモリに残っていた。
    ///
    /// 持ち主ごとに参照を 1 つ持ち、最後の <see cref="Release"/> で解放する(作った側が 1 つ目を持つ)。
    /// 別スレッドのジョブが読むときは、予約するスレッドで <see cref="AddRef"/> し、ジョブの後始末で Release する
    /// (取り消されて読まなかったときも返す)。参照を持たないまま <see cref="Ptr"/> を読み続けないこと。
    /// 取りこぼしても、GC がこのオブジェクトを回収するときにファイナライザが解放する(いつになるかは分からない)。
    /// </summary>
    internal sealed unsafe class NativePixels
    {
        public readonly int Width, Height;
        private readonly long _bytes;
        private IntPtr _mem;
        private int _refs = 1;

        private NativePixels(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"invalid size: {width}x{height}");
            if ((long)width * height > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), $"size too large: {width}x{height}");
            Width = width;
            Height = height;
            _bytes = (long)width * height * sizeof(Color32);
            _mem = IrocaNativeMemory.Alloc(_bytes, NativeUsage.Pixels);
        }

        ~NativePixels() => Free();

        /// <summary>中身を初期化しない領域を確保する(呼び出し側が全画素を書くこと)。参照 1 で返る。</summary>
        public static NativePixels Allocate(int width, int height) => new NativePixels(width, height);

        /// <summary>管理配列の先頭 width*height 画素を写した領域を作る。参照 1 で返る。</summary>
        public static NativePixels CopyOf(Color32[] src, int width, int height)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Length < (long)width * height)
                throw new ArgumentException($"src.Length({src.Length}) < {width}x{height}", nameof(src));
            var p = new NativePixels(width, height);
            fixed (Color32* s = src) Buffer.MemoryCopy(s, (void*)p._mem, p._bytes, p._bytes);
            return p;
        }

        /// <summary>画素数(Width * Height)。</summary>
        public int Length => Width * Height;

        /// <summary>先頭の画素。解放後は null。</summary>
        public Color32* Ptr => (Color32*)_mem;

        /// <summary>いまの参照の数(テストと、使い回しのプールが「自分だけが持っているか」を見るため)。</summary>
        public int RefCount => Volatile.Read(ref _refs);

        /// <summary>参照を 1 つ足す。自分が参照を持っている間に呼ぶこと(解放済みなら投げる)。</summary>
        public NativePixels AddRef()
        {
            if (Interlocked.Increment(ref _refs) <= 1)
            {
                Interlocked.Decrement(ref _refs);
                throw new ObjectDisposedException(nameof(NativePixels), "解放済みの画素に参照を足そうとしました");
            }
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照ならすぐ解放する(どのスレッドからでもよい)。</summary>
        public void Release()
        {
            int left = Interlocked.Decrement(ref _refs);
            if (left > 0) return;
            if (left < 0)
            {
                Interlocked.Increment(ref _refs);
                throw new InvalidOperationException("NativePixels の参照を返しすぎました");
            }
            // 中断中(ドメインのアンロード)は、並列で読んでいる途中のスレッドが残っていることがあるので
            // 解放しない(アンロードの後で走るファイナライザに任せる)。
            if (IrocaNativeMemory.CurrentThreadAborting) return;
            Free();
            GC.SuppressFinalize(this);
        }

        /// <summary>寸法が同じ別の領域の中身をそのまま写す。</summary>
        public void CopyFrom(NativePixels src)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Width != Width || src.Height != Height)
                throw new ArgumentException($"size mismatch: {src.Width}x{src.Height} -> {Width}x{Height}", nameof(src));
            Buffer.MemoryCopy(src.Ptr, Ptr, _bytes, _bytes);
        }

        /// <summary>中身を管理配列へ写す(小さい表示用や、デバッグの書き出し用)。</summary>
        public Color32[] ToArray()
        {
            var a = new Color32[Length];
            fixed (Color32* d = a) Buffer.MemoryCopy(Ptr, d, _bytes, _bytes);
            return a;
        }

        /// <summary>1 画素を読む(範囲外は投げる)。スポイトなど、メインスレッドで少し読むとき用。</summary>
        public Color32 Get(int index)
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));
            return Ptr[index];
        }

        private void Free()
        {
            IntPtr p = Interlocked.Exchange(ref _mem, IntPtr.Zero);
            if (p != IntPtr.Zero) IrocaNativeMemory.Free(p, _bytes, NativeUsage.Pixels);
        }
    }
}
