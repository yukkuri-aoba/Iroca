// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 俯瞰スポイト補正: ハイライト合成(wash)に使う実効サンプル色を、テクスチャ全体から自動導出する。
    ///
    /// RecolorPixel のハイライト合成(P5 白寄せ)は「サンプルより明るい画素(oV &gt; sampleV)」にのみ効く。
    /// ユーザーが光沢の明るい所をスポイトすると sampleV が高くなり、wash が芯付近の極狭い明部にしか
    /// 効かず、ドーム全体は base(色相回転のみ)になって純色 target では陰影が潰れ「ベタ塗り」に見える。
    ///
    /// 本クラスはパーツ(同色相・有彩の地色)の代表 V をテクスチャから求め、スポイト色と同色相・同 S で
    /// V を地色まで下げた wash 用サンプルを返す。これで wash がドーム全体に効き、明るい所をスポイトしても
    /// 鏡面グラデが潰れない。**match / base は元の sampleColor のまま**なので再着色範囲は不変
    /// (= 新規の誤検出は増えない)。
    /// </summary>
    internal static class HighlightSampleCorrector
    {
        private const float MinSampleSat   = 0.20f;  // 源色がこれ未満(灰色寄り)なら補正しない(色相が不安定)
        private const float BodySatFrac    = 0.50f;  // 地色とみなす彩度下限(源色相対)。脱彩した wash 済み明部を除外
        private const float HueBand        = 0.15f;  // 同色相とみなす色相帯(許容値非依存の緩め)
        private const float HighlightGatePct = 0.75f; // スポイトVが地色Vのこのpercentileより上のときだけ補正
        private const float TargetPct        = 0.60f; // 補正時の washV = 地色Vのこのpercentile(上位~40%だけ白寄せ)

        /// <summary>
        /// wash 用サンプル色を返す。autoHighlightSample=false / 低彩度源色 / 地色画素が少ない場合は
        /// zone.sampleColor をそのまま返す(= 従来挙動)。
        /// </summary>
        public static Color ComputeWashSample(
            Color32[] px, float[] pixH, float[] pixS, float[] pixV, int w, int h, ColorZone zone)
        {
            Color sample = zone.sampleColor;
            // 自動導出は「白寄せ合成 ON」かつ「自動補正 ON」の両方が必要。
            // applyHighlightWash が OFF なら射影自体が走らないので sample のままで十分。
            // Python 参照 algorithm.py の wash_rgb 条件 (apply_highlight_wash && auto_highlight_sample) と一致。
            if (!zone.applyHighlightWash || !zone.autoHighlightSample) return sample;

            Color.RGBToHSV(sample, out float sH, out float sS, out float sV);
            if (sS < MinSampleSat) return sample;

            float satFloor = sS * BodySatFrac;
            int len = w * h;

            // 同色相・有彩の地色画素の V ヒストグラムを作る
            var vHist = new int[256];
            int body = 0;
            for (int i = 0; i < len; i++)
            {
                if (px[i].a < 128) continue;
                if (pixS[i] < satFloor) continue;
                float hd = Mathf.Abs(pixH[i] - sH);
                if (hd > 0.5f) hd = 1f - hd;
                if (hd >= HueBand) continue;
                body++;
                vHist[Mathf.Clamp((int)(pixV[i] * 255f), 0, 255)]++;
            }
            if (body < 100) return sample;

            // スポイトが地色帯の高 percentile より上＝明らかにハイライトを取った時だけ補正する。
            // 地色帯内のスポイト(ハイライトでない)は補正しない＝房の多いテクスチャ等での過剰白寄せを防ぐ。
            float gateV = PercentileFromHist(vHist, body, HighlightGatePct);
            if (sV <= gateV) return sample;

            // 補正時の wash しきい(washV)= 地色帯上部(TargetPct)。中央値まで下げない(=上位~40%だけ白寄せ)。
            float targetV = PercentileFromHist(vHist, body, TargetPct);
            float effV = Mathf.Min(sV, targetV);
            float scale = effV / Mathf.Max(sV, 1e-6f);
            return new Color(sample.r * scale, sample.g * scale, sample.b * scale, 1f);
        }

        /// <summary>0..255 V ヒストグラムの percentile(0..1) を 0..1 の V で返す。</summary>
        private static float PercentileFromHist(int[] hist, int total, float pct)
        {
            int target = Mathf.Clamp(Mathf.CeilToInt(total * pct), 1, total);
            int cum = 0;
            for (int b = 0; b < 256; b++)
            {
                cum += hist[b];
                if (cum >= target) return b / 255f;
            }
            return 1f;
        }
    }
}
