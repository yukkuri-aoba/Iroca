// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Runtime.CompilerServices;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 画素ごとの HSV(<c>Color.RGBToHSV</c> の結果)。色の表(<see cref="ColorPalette"/>)があれば
        /// 「画素 → 番号」(<see cref="Index"/>)と「番号 → HSV」(<see cref="H"/>/<see cref="S"/>/<see cref="V"/>)の
        /// 2 段で引き、フル解像度の float 3 本(4K で 192MB)を持たない。表が無い(色の種類が多すぎる)ときは
        /// <see cref="Index"/> が null で、H/S/V が画素ごとの配列。どちらでも値はビット単位で同じ。
        /// 読む側は <see cref="HsvSlot"/> で添字を求めてから H/S/V を引く(ラムダへは配列をローカルへ取り出して渡す)。
        /// </summary>
        internal readonly struct PixelHsv
        {
            internal readonly float[] H, S, V;
            internal readonly int[] Index;   // null = H/S/V が画素ごと

            internal PixelHsv(float[] h, float[] s, float[] v, int[] index)
            {
                H = h; S = s; V = v; Index = index;
            }

        }

        /// <summary><see cref="PixelHsv"/> の H/S/V を引く添字(表があれば画素の番号、無ければ画素そのもの)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int HsvSlot(int[] index, int i) => index == null ? i : index[i];
    }
}
