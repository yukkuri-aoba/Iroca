// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Runtime.CompilerServices;

namespace Iroca
{
    /// <summary>
    /// 1 画素 1 bit のビット列の下回り(0.4.0 の計画の 3-5 で、混色帯と選択キャッシュの鍵が別々に持っていた
    /// 実装を 1 つにした)。
    /// </summary>
    internal static class Bits
    {
        /// <summary>立っているビットの数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int PopCount(ulong v)
        {
#if NETCOREAPP3_0_OR_GREATER
            return System.Numerics.BitOperations.PopCount(v);
#else
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
#endif
        }
    }
}
