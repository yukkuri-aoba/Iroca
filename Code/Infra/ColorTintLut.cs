// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マテリアルのメインカラーの掛け算(テクスチャ × 色)を 8 bit の画素に当てる表。
    /// シェーダーはプロジェクトが Linear なら線形の値で掛ける(テクスチャは sRGB として読まれて線形へ、
    /// 色のプロパティも線形へ直されて渡り、結果は sRGB へ戻して表示される)ので、表もそのとおりに作る。
    /// Gamma のプロジェクトでは保存された値どうしをそのまま掛ける。
    /// チャンネルごとに独立なので、256 段の表を 3 本作れば全画素に当てられる(α はそのまま)。
    /// </summary>
    internal static class ColorTintLut
    {
        /// <summary>
        /// 1 チャンネルぶんの表。tint はマテリアルに保存されている色の値(HDR なら 1 を超える)。
        /// 結果は 0〜1 に収める(8 bit の表示先で飽和するのと同じ)。
        /// </summary>
        public static byte[] Build(float tint, bool linearSpace)
        {
            var lut = new byte[256];
            float t = linearSpace ? SrgbToLinear(Math.Max(0f, tint)) : Math.Max(0f, tint);
            for (int v = 0; v < 256; v++)
            {
                float x = v / 255f;
                float y = linearSpace ? LinearToSrgb(SrgbToLinear(x) * t) : x * t;
                lut[v] = (byte)Math.Round(Math.Min(1f, Math.Max(0f, y)) * 255f);
            }
            return lut;
        }

        /// <summary>表が何もしない(どの値もそのまま返す)か。白の掛け算を素通しするのに使う。</summary>
        public static bool IsIdentity(byte[] lut)
        {
            for (int v = 0; v < 256; v++)
                if (lut[v] != v) return false;
            return true;
        }

        /// <summary>src の各画素の RGB を表で置き換えて dst へ書く(src と dst は同じでもよい)。</summary>
        public static unsafe void Apply(Color32* src, Color32* dst, int count, byte[] r, byte[] g, byte[] b)
        {
            fixed (byte* lr = r)
            fixed (byte* lg = g)
            fixed (byte* lb = b)
                for (int i = 0; i < count; i++)
                {
                    var c = src[i];
                    dst[i] = new Color32(lr[c.r], lg[c.g], lb[c.b], c.a);
                }
        }

        /// <summary>配列版(テスト用)。</summary>
        public static unsafe void Apply(Color32[] src, Color32[] dst, byte[] r, byte[] g, byte[] b)
        {
            if (src.Length != dst.Length) throw new ArgumentException("src and dst differ in length");
            fixed (Color32* s = src)
            fixed (Color32* d = dst)
                Apply(s, d, src.Length, r, g, b);
        }

        // sRGB の伝達関数(IEC 61966-2-1)。Unity の Mathf.GammaToLinearSpace / LinearToGammaSpace と同じ式。
        // Mathf の方はネイティブ実装で、Unity を起動しないテスト(scripts/unit-run)からは呼べないので自前で持つ。
        public static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSrgb(float c)
            => c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1f / 2.4f) - 0.055f;
    }
}
