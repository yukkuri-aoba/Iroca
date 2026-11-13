// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 証拠つきの自動調整で、利用者の除外マスクの寸法が証拠(AI 提案のセグメント)と違っても、除外した所の色が
    /// 地色の母集団に入らないこと。以前は、寸法が同じときだけ除外を母集団から外していた(辺が 8192 を超えるテクスチャや、
    /// 保存したマスクが今のテクスチャと違う解像度のときに、除外が無視された)。
    /// </summary>
    public class AutoTuneEvidenceMaskTests {
        private const int N = 256;
        // セグメント(証拠)は x・y とも [16, 240)。その中を左から: 地色(明度 0.50。幅の 8 割)→ 明るい側への勾配 → 明るい帯
        // (明度 0.92。幅の 7%)。色相と彩度はどこも同じ。クリックは明るい帯。
        private const int Seg0 = 16, Seg1 = 240, BaseEnd = 198, RampEnd = 224;
        private const float BaseV = 0.50f, BrightV = 0.92f, Sat = 0.80f;

        private static float ValueAt(int x) =>
            x < BaseEnd ? BaseV : x >= RampEnd ? BrightV : Mathf.Lerp(BaseV, BrightV, (x - BaseEnd + 1) / (float)(RampEnd - BaseEnd + 1));

        private static Color32 Red(float v) => new Color32((byte)Mathf.RoundToInt(v * 255f),
            (byte)Mathf.RoundToInt(v * (1f - Sat) * 255f), (byte)Mathf.RoundToInt(v * (1f - Sat) * 255f), 255);

        // 白い地に、上の帯の並びのセグメント。除外しなければ、地色は面積の大きい明度 0.50 の面へ寄る(クリックは明るい端)。
        // 地色の面を除外すれば、母集団は勾配と明るい帯だけになり、いちばん多いのは明るい帯なので寄らない。
        private static Color32[] Scene() {
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    px[y * N + x] = InSegment(x, y) ? Red(ValueAt(x)) : new Color32(255, 255, 255, 255);
                }
            }
            return px;
        }

        // size×size の真偽の地図(テクスチャの座標 (x, y) を N/size 画素おきに引く)。
        private static bool[] Mask(int size, System.Func<int, int, bool> at) {
            var m = new bool[size * size];
            int step = N / size;
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    m[y * size + x] = at(x * step, y * step);
                }
            }
            return m;
        }

        private static bool InSegment(int x, int y) => x >= Seg0 && x < Seg1 && y >= Seg0 && y < Seg1;
        private static bool IsBase(int x, int y) => InSegment(x, y) && x < BaseEnd;

        private static ZoneAutoTuner.TuneResult Tune(int evidenceSize, bool[] excluded, int excludedSize) {
            var zone = new ColorZone {
                name = "red",
                sampleColor = Red(BrightV),
                sampleColorSet = true,
                sampleUV = new Vector2(232.5f / N, 128.5f / N),
                targetColor = new Color(0.1f, 0.6f, 0.9f),
                targetColorSet = true,
            };
            zone.EnsureId();
            return ZoneAutoTuner.AnalyzeWithEvidence(Scene(), N, N, zone, default,
                Mask(evidenceSize, InSegment), evidenceSize, evidenceSize,
                excluded, excludedSize, excludedSize);
        }

        private static string Normalized(ZoneAutoTuner.TuneResult r) =>
            r.hasNormalizedSample ? ((Color32)r.normalizedSample).ToString() : "(not moved)";

        [Test]
        public void ExclusionOfADifferentSize_IsKeptOutOfTheBaseColorPopulation() {
            var excludeBase = Mask(N, IsBase);

            var noExclusion = Tune(N / 2, null, 0);
            var sameSize = Tune(N, excludeBase, N);              // 証拠と除外が同じ寸法(前から効いていた道)
            var otherSize = Tune(N / 2, excludeBase, N);         // 証拠が半分の寸法(以前は除外が無視された)

            Assert.IsNotNull(sameSize.evidenceDiag, "証拠つきの導出を通っていない(テストの場面が証拠として使えない)");
            Assert.IsNotNull(otherSize.evidenceDiag, "証拠つきの導出を通っていない(テストの場面が証拠として使えない)");
            Assert.AreNotEqual(Normalized(noExclusion), Normalized(sameSize),
                "テストの前提: 除外の有無で地色の正規化が変わる場面であること");
            Assert.AreEqual(Normalized(sameSize), Normalized(otherSize),
                "除外マスクの寸法が証拠と違うと、除外した所の色が地色の母集団に入った");
        }
    }
}
