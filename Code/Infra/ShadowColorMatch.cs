// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マテリアルの影の色を塗り先の色に合わせる計算(Unity のオブジェクトに触らない部分)。
    /// 影の色はテクスチャに掛かるので、元の布に合わせた青い影のまま布を緑に塗ると、白に近い所が青紫に見える。
    /// 明るさはそのままで(影の濃さを変えない)、色味の向きを塗り先の色へ回す。
    /// </summary>
    internal static class ShadowColorMatch
    {
        // OkLab の色味(a, b)の差がこれ未満なら、合わせても見分けられない(案内を出さない)。
        private const float NoticeableChromaDelta = 0.02f;

        /// <summary>
        /// 影の色 <paramref name="shadow"/>(不透明度はそのまま)を、<paramref name="sample"/> から
        /// <paramref name="target"/> への色替えに合わせた色を返す。色味の強さは塗り先の方が淡いときだけ同じ割合で弱める
        /// (塗り先が無彩色なら影も無彩色になる)。元の影に色味が無ければ変わらない。
        /// </summary>
        public static Color Compute(Color shadow, Color sample, Color target)
        {
            OkLab.RgbToOklab(shadow.r, shadow.g, shadow.b, out float L, out float a, out float b);
            float chroma = Mathf.Sqrt(a * a + b * b);
            OkLab.RgbToOklab(sample.r, sample.g, sample.b, out _, out float sa, out float sb);
            OkLab.RgbToOklab(target.r, target.g, target.b, out _, out float ta, out float tb);
            float sampleChroma = Mathf.Sqrt(sa * sa + sb * sb);
            float targetChroma = Mathf.Sqrt(ta * ta + tb * tb);

            float na = 0f, nb = 0f;
            if (targetChroma > 1e-4f)
            {
                float ratio = sampleChroma > targetChroma ? targetChroma / sampleChroma : 1f;
                float scale = chroma * ratio / targetChroma;
                na = ta * scale;
                nb = tb * scale;
            }
            OkLab.OklabToRgbInGamut(L, na, nb, out float r, out float g, out float bb);
            return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(bb), shadow.a);
        }

        /// <summary>2 つの影の色の色味の違いが、見て分かる大きさか。</summary>
        public static bool Differs(Color x, Color y)
        {
            OkLab.RgbToOklab(x.r, x.g, x.b, out _, out float xa, out float xb);
            OkLab.RgbToOklab(y.r, y.g, y.b, out _, out float ya, out float yb);
            float da = xa - ya, db = xb - yb;
            return da * da + db * db >= NoticeableChromaDelta * NoticeableChromaDelta;
        }
    }
}
