// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Runtime.CompilerServices;

namespace Iroca {
    internal static unsafe partial class PixelProcessor {
        /// <summary>
        /// 画素ごとの HSV(<c>Color.RGBToHSV</c> の結果)。色の表(<see cref="ColorPalette"/>)があれば
        /// 「画素 → 番号」(<see cref="Index"/>)と「番号 → HSV」(<see cref="H"/>/<see cref="S"/>/<see cref="V"/>)の
        /// 2 段で引き、フル解像度の float 3 本(4K で 192MB)を持たない。表が無い(色の種類が多すぎる)ときは
        /// <see cref="Index"/> が null で、H/S/V が画素ごとの作業配列。どちらでも値はビット単位で同じ。
        /// 読む側は <see cref="HsvSlot"/> で添字を求めてから H/S/V を引く(ラムダへはローカルへ取り出して渡す)。
        /// どれも Mono のヒープの外(色の表の持ち物か、作業配列のプール)を指す。指す先が生きているのは、
        /// 色の表の参照か作業配列を ProcessPixelsArrayCore が持っている間だけ。
        /// </summary>
        internal readonly struct PixelHsv {
            internal readonly float* H, S, V;
            internal readonly int* Index;   // null = H/S/V が画素ごと
            /// <summary>
            /// 元画素(この HSV を求めた画素)の α がどれも OpaqueAlphaMin 以上と分かっているか。真なら、選択の段は
            /// `α &gt;= OpaqueAlphaMin`(いつも真)・`α &lt; OpaqueAlphaMin`(いつも偽)の判定で α を読まなくてよい。
            /// false は「分からない」(今までどおり α を読む)。真を入れるのは、同じ元画素から作った色の表の
            /// <see cref="ColorPalette.MinAlpha"/> で確かめた所だけ。
            /// </summary>
            internal readonly bool AllOpaque;

            internal PixelHsv(float* h, float* s, float* v, int* index, bool allOpaque = false) {
                H = h; S = s; V = v; Index = index; AllOpaque = allOpaque;
            }

        }

        /// <summary><see cref="PixelHsv"/> の H/S/V を引く添字(表があれば画素の番号、無ければ画素そのもの)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int HsvSlot(int* index, int i) => index == null ? i : index[i];
    }
}
