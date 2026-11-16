// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// マテリアルの影の色を塗り先の色に合わせる計算(Unity のオブジェクトに触らない部分)。
    /// 影の色はテクスチャに掛かるので、元の布に合わせた青い影のまま布を緑に塗ると、白に近い所が青紫に見える。
    /// <para>
    /// 合わせるのは、影の色がそのパーツの元の色(サンプル色)に合わせて作られているときだけ。影の色味の向きがサンプル色の
    /// 色味の向きに近いほど、パーツの色相が変わった分だけ影の色相も回す(影とパーツの色のずれはそのまま残す)。
    /// 向きが離れた影(どの色の上でも紫・灰にする画風の影)と、元の色に向きの無いパーツ(白・黒・灰)の影は変えない。
    /// 配布アバター 415 マテリアルの実測では、影の色の多くはパーツの色と関係の無い画風の色だった
    /// (dev_safe/docs/shadow_color_survey_2026-11-16.md)。明るさは変えない(影の濃さを変えない)。
    /// </para>
    /// </summary>
    internal static class ShadowColorMatch {
        // OkLab の色味(a, b)の差・大きさがこれ未満なら、見分けられない(案内を出さない・向きが無いとみなす)。
        private const float NoticeableChromaDelta = 0.02f;
        // 影がパーツの元の色に合わせてあるとみなす、色味の向きの差の上限(度)。これ以上離れていれば画風の影として残す。
        // 色の名前(赤・橙・黄・緑・青・紫・桃)が変わるくらいの差で、紫の影と青い布(実測で多い組)は別とみなす。
        // 0 度で全部、上限で 0 になるよう、その間はなめらかに減らす(cos)。
        private const float SameHueFamilyDegrees = 45f;

        /// <summary>
        /// 影の色 <paramref name="shadow"/>(不透明度はそのまま)を、<paramref name="sample"/> から
        /// <paramref name="target"/> への色替えに合わせた色を返す。影がサンプル色に合わせてある度合い(<see cref="Affinity"/>)だけ、
        /// 色相を塗り先の方へ回し、色味の強さは塗り先の方が淡いときだけ同じ割合で弱める(塗り先が無彩色なら影も無彩色へ近づく)。
        /// 合わせてある度合いが 0 なら(画風の影・白黒灰のパーツ・色味の無い影)変わらない。
        /// </summary>
        public static Color Compute(Color shadow, Color sample, Color target) {
            OkLab.RgbToOklab(shadow.r, shadow.g, shadow.b, out float L, out float a, out float b);
            OkLab.RgbToOklab(sample.r, sample.g, sample.b, out _, out float sa, out float sb);
            OkLab.RgbToOklab(target.r, target.g, target.b, out _, out float ta, out float tb);
            float weight = Affinity(a, b, sa, sb);
            if (weight <= 0f) {
                return shadow;
            }
            float chroma = Mathf.Sqrt(a * a + b * b);
            float sampleChroma = Mathf.Sqrt(sa * sa + sb * sb);
            float targetChroma = Mathf.Sqrt(ta * ta + tb * tb);

            // 色相: パーツの色相の変化(塗り先 − サンプル)を、合わせてある度合いだけ足す。塗り先が無彩色なら向きは変えず、
            // 下の色味の強さだけで無彩色へ近づける。
            float hue = Mathf.Atan2(b, a);
            if (targetChroma > 1e-4f) {
                hue += weight * Mathf.DeltaAngle(Mathf.Atan2(sb, sa) * Mathf.Rad2Deg, Mathf.Atan2(tb, ta) * Mathf.Rad2Deg)
                       * Mathf.Deg2Rad;
            }
            // 色味の強さ: 塗り先の方が淡いときだけ、同じ割合で弱める(合わせてある度合いだけ)。
            float ratio = sampleChroma > targetChroma ? targetChroma / sampleChroma : 1f;
            float newChroma = chroma * (1f + weight * (ratio - 1f));
            OkLab.OklabToRgbInGamut(L, newChroma * Mathf.Cos(hue), newChroma * Mathf.Sin(hue), out float r, out float g, out float bb);
            return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(bb), shadow.a);
        }

        /// <summary>
        /// 影の色がパーツの元の色(サンプル色)に合わせて作られている度合い(0〜1)。色味の向きが同じなら 1、
        /// <see cref="SameHueFamilyDegrees"/> 以上離れていれば 0。どちらかに見分けられるほどの色味が無ければ 0
        /// (白・黒・灰のパーツの影は、どのパーツのものか分からないので残す)。
        /// </summary>
        public static float Affinity(Color shadow, Color sample) {
            OkLab.RgbToOklab(shadow.r, shadow.g, shadow.b, out _, out float a, out float b);
            OkLab.RgbToOklab(sample.r, sample.g, sample.b, out _, out float sa, out float sb);
            return Affinity(a, b, sa, sb);
        }

        private static float Affinity(float a, float b, float sa, float sb) {
            if (a * a + b * b < NoticeableChromaDelta * NoticeableChromaDelta
                || sa * sa + sb * sb < NoticeableChromaDelta * NoticeableChromaDelta) {
                return 0f;
            }
            float degrees = Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(b, a) * Mathf.Rad2Deg, Mathf.Atan2(sb, sa) * Mathf.Rad2Deg));
            if (degrees >= SameHueFamilyDegrees) {
                return 0f;
            }
            return Mathf.Cos(degrees / SameHueFamilyDegrees * 90f * Mathf.Deg2Rad);
        }

        /// <summary>2 つの影の色の色味の違いが、見て分かる大きさか。</summary>
        public static bool Differs(Color x, Color y) {
            OkLab.RgbToOklab(x.r, x.g, x.b, out _, out float xa, out float xb);
            OkLab.RgbToOklab(y.r, y.g, y.b, out _, out float ya, out float yb);
            float da = xa - ya, db = xb - yb;
            return da * da + db * db >= NoticeableChromaDelta * NoticeableChromaDelta;
        }
    }
}
