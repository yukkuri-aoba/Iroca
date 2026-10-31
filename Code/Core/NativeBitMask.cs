// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca
{
    /// <summary>
    /// 1 画素 1 ビットのマスク。Mono のヒープの外(<see cref="NativeBuffer"/>。用途 Mask)に置く。並びは
    /// <see cref="MaskSnapshot"/> の詰めた形と同じ(画素 i は語 i &gt;&gt; 6 の i &amp; 63 ビット目。画素数より後ろのビットは 0)。
    /// bool[] の 1/8 の大きさで、8K でも 1 枚 8MB。
    ///
    /// 持ち方は参照の数(<see cref="NativeBuffer"/> の決まり)。読み手は <see cref="AddRef"/> して共有し、書き手は書く前に
    /// <see cref="Writable"/> で自分だけのものにする(ほかに持ち主がいるときだけ写す)。保存形式(RLE)との変換は
    /// <see cref="MaskRle.EncodeBits"/> / <see cref="MaskRle.DecodeBits"/> が bool[] を経由せずに行う。
    /// </summary>
    internal sealed unsafe class NativeBitMask
    {
        public readonly int Width, Height;
        private readonly NativeBuffer _mem;

        private NativeBitMask(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"invalid size: {width}x{height}");
            if ((long)width * height > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), $"size too large: {width}x{height}");
            Width = width;
            Height = height;
            _mem = new NativeBuffer((long)Words * sizeof(ulong), NativeUsage.Mask);
        }

        /// <summary>全部 0 のマスクを作る。参照 1 で返る。</summary>
        public static NativeBitMask Allocate(int width, int height)
        {
            var m = new NativeBitMask(width, height);
            new Span<ulong>(m.Bits, m.Words).Clear();
            return m;
        }

        /// <summary>画素数。</summary>
        public int Length => Width * Height;

        /// <summary>語(64 画素)の数。</summary>
        public int Words => (Length + 63) >> 6;

        /// <summary>先頭の語。解放後は null。</summary>
        public ulong* Bits => (ulong*)_mem.Ptr;

        /// <summary>いまの参照の数。</summary>
        public int RefCount => _mem.RefCount;

        /// <summary>参照を 1 つ足す(解放済みなら投げる)。</summary>
        public NativeBitMask AddRef()
        {
            _mem.AddRef();
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照なら解放する。返しすぎは投げる。</summary>
        public void Release() => _mem.Release();

        /// <summary>同じ中身の別のマスク(参照 1)。</summary>
        public NativeBitMask Clone()
        {
            var c = new NativeBitMask(Width, Height);
            long bytes = (long)Words * sizeof(ulong);
            Buffer.MemoryCopy(Bits, c.Bits, bytes, bytes);
            return c;
        }

        /// <summary>
        /// 書く前に呼ぶ。ほかに持ち主がいなければ自分を返し、いれば写しを作って、自分の持っていた参照を返す
        /// (呼び出し側は戻り値の参照を持つ)。<c>mask = mask.Writable();</c>
        /// </summary>
        public NativeBitMask Writable()
        {
            if (RefCount == 1) return this;
            var c = Clone();
            Release();
            return c;
        }

        /// <summary>画素 i が立っているか(範囲外は投げる)。</summary>
        public bool Get(int i)
        {
            if ((uint)i >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(i));
            return (Bits[i >> 6] & (1UL << (i & 63))) != 0UL;
        }

        /// <summary>画素 i を書く(範囲外は投げる)。ほかに持ち主がいるなら先に <see cref="Writable"/> すること。</summary>
        public void Set(int i, bool value)
        {
            if ((uint)i >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(i));
            if (value) Bits[i >> 6] |= 1UL << (i & 63);
            else Bits[i >> 6] &= ~(1UL << (i & 63));
        }

        /// <summary>立っている画素が 1 つでもあるか。</summary>
        public bool Any()
        {
            ulong* b = Bits;
            int n = Words;
            for (int k = 0; k < n; k++) if (b[k] != 0UL) return true;
            return false;
        }

        /// <summary>先頭の語。マスクが無ければ null。</summary>
        public static ulong* BitsOf(NativeBitMask mask) => mask != null ? mask.Bits : null;

        /// <summary>語の写し(管理配列)。テストと確認の道具用で、製品の経路では使わない。</summary>
        public ulong[] ToWords()
        {
            var words = new ulong[Words];
            fixed (ulong* d = words) Buffer.MemoryCopy(Bits, d, (long)words.Length * sizeof(ulong), (long)words.Length * sizeof(ulong));
            return words;
        }

        /// <summary>bool[](先頭の Length 画素)から作る(参照 1)。作業用の bool[] から移す間だけ使う。</summary>
        public static NativeBitMask FromBools(bool[] mask, int width, int height)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (mask.Length < (long)width * height)
                throw new ArgumentException($"mask.Length({mask.Length}) < {width}x{height}", nameof(mask));
            var m = Allocate(width, height);
            int n = m.Length;
            ulong* b = m.Bits;
            for (int i = 0; i < n; i++)
                if (mask[i]) b[i >> 6] |= 1UL << (i & 63);
            return m;
        }
    }
}
