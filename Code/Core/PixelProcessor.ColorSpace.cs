// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static partial class PixelProcessor
    {
        // ───────────── OkLab 知覚色空間ヘルパー (リング除去の中核) ─────────────
        // リング(ドーナツ)の根本原因は「HSV の V/S は知覚的でないため、S/V を保持して
        // 色相だけ変えると元の単調な輝度 falloff が変換先の色の輝度応答で非単調化する」こと。
        // OkLab で L(知覚明度)を保持し彩度(a,b)を sample→target の線形写像で移すことで、
        // 明度構造を完全保存しリング・白部サイズ変化・ベタ塗りを構造的に排除する。
        //
        // パフォーマンス(P1-3): 再着色ホットループは画素あたり ~9 回の Mathf.Pow を呼んでいた
        // (Pow は乗算の数十倍コスト)。Pow を以下で置換する:
        //  - SrgbToLinear: 入力が byte/255 の 256 通りしかない per-pixel 経路は 256 エントリ LUT で
        //    厳密置換(s_srgbToLinearLut)。任意 float 入力(ゾーン定数)は従来どおり Pow。
        //  - Cbrt: Mathf.Pow(x,1/3) は cbrt の近似。MathF.Cbrt へ置換すると精度が上がり、
        //    かつ Pow を除去できる。
        //  - LinearToSrgb: 出力は byte に量子化されるため LUT+線形補間で視覚的に無損失に置換。
        private const int LinToSrgbLutSize = 4096;
        private static readonly float[] s_srgbToLinearLut = BuildSrgbToLinearLut();
        private static readonly float[] s_linearToSrgbLut = BuildLinearToSrgbLut();

        private static float[] BuildSrgbToLinearLut()
        {
            var lut = new float[256];
            for (int i = 0; i < 256; i++) lut[i] = SrgbToLinear(i / 255f);
            return lut;
        }

        private static float[] BuildLinearToSrgbLut()
        {
            // インデックス k は線形値 k/LinToSrgbLutSize に対応。線形補間用に末尾 +1 エントリ。
            var lut = new float[LinToSrgbLutSize + 1];
            for (int k = 0; k <= LinToSrgbLutSize; k++)
                lut[k] = LinearToSrgbExact((float)k / LinToSrgbLutSize);
            return lut;
        }

        private static float SrgbToLinear(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        private static float LinearToSrgbExact(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private static float LinearToSrgb(float c)
        {
            if (c <= 0f) return 0f;
            if (c >= 1f) return 1f;
            float f = c * LinToSrgbLutSize;
            int k = (int)f;                 // c<1 なので 0..LinToSrgbLutSize-1
            float frac = f - k;
            float a = s_linearToSrgbLut[k];
            return a + (s_linearToSrgbLut[k + 1] - a) * frac;
        }

        private static float Cbrt(float x)
        {
            // MathF.Cbrt は負値も正しく扱い、Mathf.Pow(x,1/3) より高精度。
            return MathF.Cbrt(x);
        }

        private static void OklabFromLinear(float lr, float lg, float lb,
            out float L, out float a, out float bb)
        {
            float l = 0.4122214708f * lr + 0.5363325363f * lg + 0.0514459929f * lb;
            float m = 0.2119034982f * lr + 0.6806995451f * lg + 0.1073969566f * lb;
            float s = 0.0883024619f * lr + 0.2817188376f * lg + 0.6299787005f * lb;
            float l_ = Cbrt(l), m_ = Cbrt(m), s_ = Cbrt(s);
            L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
            a = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
            bb = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;
        }

        // 任意 float 入力版(ゾーン定数: sample/target 色)。
        private static void RgbToOklab(float r, float g, float b,
            out float L, out float a, out float bb)
        {
            OklabFromLinear(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b), out L, out a, out bb);
        }

        // byte 入力版(per-pixel 経路)。SrgbToLinear を 256 エントリ LUT で厳密に表引きする。
        private static void RgbToOklab(byte r, byte g, byte b,
            out float L, out float a, out float bb)
        {
            OklabFromLinear(s_srgbToLinearLut[r], s_srgbToLinearLut[g], s_srgbToLinearLut[b],
                out L, out a, out bb);
        }

        private static void OklabToRgb(float L, float a, float b,
            out float r, out float g, out float bb)
        {
            float l_ = L + 0.3963377774f * a + 0.2158037573f * b;
            float m_ = L - 0.1055613458f * a - 0.0638541728f * b;
            float s_ = L - 0.0894841775f * a - 1.2914855480f * b;
            float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
            float lr = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
            float lg = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
            float lb = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
            r = LinearToSrgb(lr);
            g = LinearToSrgb(lg);
            bb = LinearToSrgb(lb);
        }
    }
}
