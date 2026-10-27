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
    /// 参照の数・返しすぎ・中断中・返し忘れの扱いは、中身を持つ <see cref="NativeBuffer"/> の決まりのとおり。
    /// </summary>
    internal sealed unsafe class NativePixels
    {
        public readonly int Width, Height;
        private readonly long _bytes;
        private readonly NativeBuffer _mem;

        private NativePixels(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"invalid size: {width}x{height}");
            if ((long)width * height > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), $"size too large: {width}x{height}");
            Width = width;
            Height = height;
            _bytes = (long)width * height * sizeof(Color32);
            _mem = new NativeBuffer(_bytes, NativeUsage.Pixels);
        }

        /// <summary>中身を初期化しない領域を確保する(呼び出し側が全画素を書くこと)。参照 1 で返る。</summary>
        public static NativePixels Allocate(int width, int height) => new NativePixels(width, height);

        /// <summary>管理配列の先頭 width*height 画素を写した領域を作る。参照 1 で返る。</summary>
        public static NativePixels CopyOf(Color32[] src, int width, int height)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Length < (long)width * height)
                throw new ArgumentException($"src.Length({src.Length}) < {width}x{height}", nameof(src));
            var p = new NativePixels(width, height);
            fixed (Color32* s = src) Buffer.MemoryCopy(s, p.Ptr, p._bytes, p._bytes);
            return p;
        }

        /// <summary>画素数(Width * Height)。</summary>
        public int Length => Width * Height;

        /// <summary>先頭の画素。解放後は null。</summary>
        public Color32* Ptr => (Color32*)_mem.Ptr;

        /// <summary>いまの参照の数(テストと、使い回しのプールが「自分だけが持っているか」を見るため)。</summary>
        public int RefCount => _mem.RefCount;

        /// <summary>参照を 1 つ足す。自分が参照を持っている間に呼ぶこと(解放済みなら投げる)。</summary>
        public NativePixels AddRef()
        {
            _mem.AddRef();
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照ならすぐ解放する(どのスレッドからでもよい)。返しすぎは投げる。</summary>
        public void Release() => _mem.Release();

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
    }
}
