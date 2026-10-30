// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca
{
    /// <summary>
    /// Mono のヒープの外(<see cref="IrocaNativeMemory"/>)に置いた、1 画素 1 バイトの真偽の並び(並びは GetPixels32 と同じで
    /// 行 0 = 画像下端)。AI 提案の領域(元の解像度。4K で 16MB)と、その後処理・精密化の作業に使う(0.4.0 の計画の B4)。
    /// 1 画素ずつ読み書きを繰り返す後処理(塗りつぶし・距離変換)が速く書けるよう、ビットに詰めない
    /// (編集中のマスクのように長く持つものは <see cref="NativeBitMask"/>)。
    ///
    /// 持ち方は <see cref="NativePixels"/> と同じ(参照の数。作った側が 1 つ目を持ち、最後の <see cref="Release"/> で解放する。
    /// 別スレッドのジョブが読むときは、予約するスレッドで <see cref="AddRef"/> し、ジョブの後始末で Release する)。
    /// </summary>
    internal sealed unsafe class NativeBools
    {
        public readonly int Width, Height;
        private readonly NativeBuffer _mem;

        private NativeBools(int width, int height, bool clear)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"invalid size: {width}x{height}");
            if ((long)width * height > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), $"size too large: {width}x{height}");
            Width = width;
            Height = height;
            _mem = new NativeBuffer((long)width * height, NativeUsage.Mask);
            if (clear) new Span<bool>(Ptr, Length).Clear();
        }

        /// <summary>すべて false の領域を確保する。参照 1 で返る。</summary>
        public static NativeBools Allocate(int width, int height) => new NativeBools(width, height, clear: true);

        /// <summary>中身を初期化しない領域を確保する(呼び出し側が全画素を書くこと)。参照 1 で返る。</summary>
        public static NativeBools AllocateUninitialized(int width, int height) => new NativeBools(width, height, clear: false);

        /// <summary>管理配列の先頭 width*height 個を写した領域を作る。参照 1 で返る。</summary>
        public static NativeBools CopyOf(bool[] src, int width, int height)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Length < (long)width * height)
                throw new ArgumentException($"src.Length({src.Length}) < {width}x{height}", nameof(src));
            var m = new NativeBools(width, height, clear: false);
            fixed (bool* s = src) Buffer.MemoryCopy(s, m.Ptr, m.Length, m.Length);
            return m;
        }

        /// <summary>画素数(Width * Height)。</summary>
        public int Length => Width * Height;

        /// <summary>先頭。解放後は null。</summary>
        public bool* Ptr => (bool*)_mem.Ptr;

        /// <summary>いまの参照の数(テスト用)。</summary>
        public int RefCount => _mem.RefCount;

        /// <summary>参照を 1 つ足す。自分が参照を持っている間に呼ぶこと(解放済みなら投げる)。</summary>
        public NativeBools AddRef()
        {
            _mem.AddRef();
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照ならすぐ解放する(どのスレッドからでもよい)。返しすぎは投げる。</summary>
        public void Release() => _mem.Release();

        /// <summary>true の数。</summary>
        public int CountTrue()
        {
            bool* p = Ptr;
            int n = 0, len = Length;
            for (int i = 0; i < len; i++)
                if (p[i]) n++;
            return n;
        }

        /// <summary>中身を管理配列へ写す(旧の自動調整・テスト・ハーネスとの境目用)。</summary>
        public bool[] ToArray()
        {
            var a = new bool[Length];
            fixed (bool* d = a) Buffer.MemoryCopy(Ptr, d, Length, Length);
            return a;
        }
    }
}
