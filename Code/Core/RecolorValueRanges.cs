// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 再着色の値の範囲(全体設定とゾーン)と、範囲の外・NaN の値を丸める規則(点検 A-8)。
    ///
    /// zones JSON(静的 API・CLI・MCP)、手で書いたプリセット・レシピ・セッション、ハーネスは、UI のスライダーの範囲の
    /// 外の値もそのまま渡せる。範囲の外では前提が崩れる所がある(デコンタミの半径 &gt; 12 で窓和を float で正確に持つ前提を
    /// 超える・outputSaturation &lt; 0 で補色に反転・valueBlend &gt; 1 で黒潰れ・holeFillPasses &lt; 0 で後段の範囲が選択より
    /// 狭くなる・NaN の edgeFeather で余白が int.MinValue になる など)。そこで Core の入口で 1 回だけ丸める:
    /// 全体設定は <see cref="RecolorSettings"/> のコンストラクタ(セッション・プリセット・zones JSON・ハーネスの全部が通る)、
    /// ゾーンは PixelProcessor の処理の入口(ProcessPixelsArrayCore の先頭、選択の鍵を作る前)。
    /// 範囲の中の値は変えないので、UI と自動調整で作った値の出力はビット単位で同じ。
    ///
    /// NaN は既定値へ戻す(全体設定は RecolorSettings.Default*、ゾーンは新しい <see cref="ColorZone"/> の値)。
    /// UI のスライダーの範囲と、自動化が丸めた値を warnings で知らせる判定も、ここの定数と関数を使う
    /// (<see cref="ClampedSettingNames"/>・<see cref="ClampZone"/> の clamped)。
    /// </summary>
    internal static class RecolorValueRanges {
        // ─── 全体設定 ───
        // 境界ぼかし(ガウスの σ)。UI のスライダーと同じ 0..5。
        public const float EdgeFeatherMin = 0f;
        public const float EdgeFeatherMax = 5f;
        // 境界クリーンアップ(境界回復の回数)。UI と同じ 0..5(自動調整が入れる最大も 5)。
        public const int AntiAliasCleanupMin = 0;
        public const int AntiAliasCleanupMax = 5;
        // 穴埋めの回数。UI と同じ 0..10。
        public const int HoleFillPassesMin = 0;
        public const int HoleFillPassesMax = 10;
        // 穴埋めの近傍数(8 近傍のうち選ばれた数)。UI と同じ 1..8。
        public const int HoleFillMinNeighborsMin = 1;
        public const int HoleFillMinNeighborsMax = 8;
        // 緩和マッチの彩度の下限。彩度は 0..1(UI は 0.2 までしか出さない)。
        public const float RelaxedSatMinMin = 0f;
        public const float RelaxedSatMinMax = 1f;
        // 緩和マッチの彩度のランプ。照合では使っていない(選択キャッシュの鍵と保存形式の互換のために残る値)ので、
        // 有限の値にするだけ。
        public const float RelaxedSatRampMin = 0f;
        public const float RelaxedSatRampMax = 1f;
        // デコンタミの半径。UI と同じ 1..12。窓和を float で正確に持てる(2·12+1)²·255 &lt; 2^24 の前提
        // (PixelProcessor.Decontam.cs)はこの上限から。
        public const int DecontaminationRadiusMin = 1;
        public const int DecontaminationRadiusMax = 12;

        // ─── ゾーン ───
        // 割合・彩度・明度・色相の距離・色の成分はどれも 0..1。partHueBand も 0..1(UI のスライダーは 0.5 までだが、
        // 証拠つきの自動調整が 0.5 を超える値を書くことがある。0.5 で切ると出力が変わる)。satRampScale も 0..1。
        public const float ZoneValueMin = 0f;
        public const float ZoneValueMax = 1f;

        private static readonly ColorZone s_zoneDefaults = new ColorZone();

        // ─── 全体設定を丸める(RecolorSettings のコンストラクタが使う) ───
        public static float ClampEdgeFeather(float v) =>
            Clamp(v, EdgeFeatherMin, EdgeFeatherMax, RecolorSettings.DefaultEdgeFeather);
        public static int ClampAntiAliasCleanup(int v) => Clamp(v, AntiAliasCleanupMin, AntiAliasCleanupMax);
        public static int ClampHoleFillPasses(int v) => Clamp(v, HoleFillPassesMin, HoleFillPassesMax);
        public static int ClampHoleFillMinNeighbors(int v) => Clamp(v, HoleFillMinNeighborsMin, HoleFillMinNeighborsMax);
        public static float ClampRelaxedSatMin(float v) =>
            Clamp(v, RelaxedSatMinMin, RelaxedSatMinMax, RecolorSettings.DefaultRelaxedSatMin);
        public static float ClampRelaxedSatRamp(float v) =>
            Clamp(v, RelaxedSatRampMin, RelaxedSatRampMax, RecolorSettings.DefaultRelaxedSatRamp);
        public static int ClampDecontaminationRadius(int v) =>
            Clamp(v, DecontaminationRadiusMin, DecontaminationRadiusMax);

        /// <summary>
        /// 丸めると値が変わる全体設定の名前(フィールド名)を返す。自動化が「範囲外を丸めた」と warnings で知らせるため。
        /// RecolorSettings はコンストラクタで丸めるので、丸める前の値(DTO など)を渡す。
        /// </summary>
        public static List<string> ClampedSettingNames(float edgeFeather, int antiAliasCleanup,
                                                       int holeFillPasses, int holeFillMinNeighbors,
                                                       float relaxedSatMin, float relaxedSatRamp,
                                                       int decontaminationRadius) {
            var names = new List<string>();
            if (!SameFloat(ClampEdgeFeather(edgeFeather), edgeFeather)) {
                names.Add(nameof(RecolorSettings.edgeFeather));
            }
            if (ClampAntiAliasCleanup(antiAliasCleanup) != antiAliasCleanup) {
                names.Add(nameof(RecolorSettings.antiAliasCleanup));
            }
            if (ClampHoleFillPasses(holeFillPasses) != holeFillPasses) {
                names.Add(nameof(RecolorSettings.holeFillPasses));
            }
            if (ClampHoleFillMinNeighbors(holeFillMinNeighbors) != holeFillMinNeighbors) {
                names.Add(nameof(RecolorSettings.holeFillMinNeighbors));
            }
            if (!SameFloat(ClampRelaxedSatMin(relaxedSatMin), relaxedSatMin)) {
                names.Add(nameof(RecolorSettings.relaxedSatMin));
            }
            if (!SameFloat(ClampRelaxedSatRamp(relaxedSatRamp), relaxedSatRamp)) {
                names.Add(nameof(RecolorSettings.relaxedSatRamp));
            }
            if (ClampDecontaminationRadius(decontaminationRadius) != decontaminationRadius) {
                names.Add(nameof(RecolorSettings.decontaminationRadius));
            }
            return names;
        }

        /// <summary>
        /// ゾーンの値を範囲へ丸めて書き戻す(範囲の中の値は触らない)。何か変えたら true。clamped を渡すと、変えた
        /// フィールド名を足す(自動化の warnings 用。同じ名前は 1 回だけ)。PixelProcessor の処理の入口が、処理に渡された
        /// ゾーン(製品では RecolorInput の写し、ハーネスでは自分で組んだゾーン)に対して呼ぶ。
        /// sampleUV・seedUV は NaN のときだけ「未設定」(-1, -1) にする(範囲の外は使う所で端へ丸めている)。
        /// </summary>
        public static bool ClampZone(ColorZone z, List<string> clamped = null) {
            var d = s_zoneDefaults;
            bool any = false;
            any |= Fraction(ref z.tolerance, d.tolerance, nameof(ColorZone.tolerance), clamped);
            any |= Fraction(ref z.valueBlend, d.valueBlend, nameof(ColorZone.valueBlend), clamped);
            any |= Fraction(ref z.outputSaturation, d.outputSaturation, nameof(ColorZone.outputSaturation), clamped);
            any |= Fraction(ref z.edgeSoftness, d.edgeSoftness, nameof(ColorZone.edgeSoftness), clamped);
            any |= Fraction(ref z.saturationStrictness, d.saturationStrictness, nameof(ColorZone.saturationStrictness), clamped);
            any |= Fraction(ref z.valueWeight, d.valueWeight, nameof(ColorZone.valueWeight), clamped);
            any |= Fraction(ref z.satDistWeight, d.satDistWeight, nameof(ColorZone.satDistWeight), clamped);
            any |= Fraction(ref z.satRampScale, d.satRampScale, nameof(ColorZone.satRampScale), clamped);
            any |= Fraction(ref z.shadowDesaturation, d.shadowDesaturation, nameof(ColorZone.shadowDesaturation), clamped);
            any |= Fraction(ref z.shadowForgivenessSatMin, d.shadowForgivenessSatMin, nameof(ColorZone.shadowForgivenessSatMin), clamped);
            any |= Fraction(ref z.shadowValueFloor, d.shadowValueFloor, nameof(ColorZone.shadowValueFloor), clamped);
            any |= Fraction(ref z.partSatCeiling, d.partSatCeiling, nameof(ColorZone.partSatCeiling), clamped);
            any |= Fraction(ref z.partHueBand, d.partHueBand, nameof(ColorZone.partHueBand), clamped);
            any |= Fraction(ref z.chromaCeiling, d.chromaCeiling, nameof(ColorZone.chromaCeiling), clamped);
            any |= Fraction(ref z.holeFillMargin, d.holeFillMargin, nameof(ColorZone.holeFillMargin), clamped);
            any |= Fraction(ref z.chromaThreshold, d.chromaThreshold, nameof(ColorZone.chromaThreshold), clamped);
            any |= Fraction(ref z.saturationGuard, d.saturationGuard, nameof(ColorZone.saturationGuard), clamped);
            any |= ColorComponents(ref z.sampleColor, d.sampleColor, nameof(ColorZone.sampleColor), clamped);
            any |= ColorComponents(ref z.targetColor, d.targetColor, nameof(ColorZone.targetColor), clamped);
            var extra = z.extraSamples;
            if (extra != null) {
                for (int i = 0; i < extra.Count; i++) {
                    Color c = extra[i];
                    if (ColorComponents(ref c, d.sampleColor, nameof(ColorZone.extraSamples), clamped)) {
                        extra[i] = c;
                        any = true;
                    }
                }
            }
            any |= UvNotNaN(ref z.sampleUV, nameof(ColorZone.sampleUV), clamped);
            any |= UvNotNaN(ref z.seedUV, nameof(ColorZone.seedUV), clamped);
            return any;
        }

        private static float Clamp(float v, float min, float max, float nanFallback) {
            if (float.IsNaN(v)) {
                return nanFallback;
            }
            if (v < min) {
                return min;
            }
            if (v > max) {
                return max;
            }
            return v;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        // 丸めで値が変わらなかったか。NaN は丸めで必ず変わる。-0 と +0 は同じ値として扱う(範囲の中なので触らない)。
        private static bool SameFloat(float clamped, float v) => clamped == v;

        private static bool Fraction(ref float field, float nanFallback, string name, List<string> clamped) {
            float c = Clamp(field, ZoneValueMin, ZoneValueMax, nanFallback);
            if (SameFloat(c, field)) {
                return false;
            }
            field = c;
            AddName(clamped, name);
            return true;
        }

        private static bool ColorComponents(ref Color color, Color nanFallback, string name, List<string> clamped) {
            Color c = new Color(
                Clamp(color.r, ZoneValueMin, ZoneValueMax, nanFallback.r),
                Clamp(color.g, ZoneValueMin, ZoneValueMax, nanFallback.g),
                Clamp(color.b, ZoneValueMin, ZoneValueMax, nanFallback.b),
                Clamp(color.a, ZoneValueMin, ZoneValueMax, nanFallback.a));
            if (SameFloat(c.r, color.r) && SameFloat(c.g, color.g) && SameFloat(c.b, color.b) && SameFloat(c.a, color.a)) {
                return false;
            }
            color = c;
            AddName(clamped, name);
            return true;
        }

        private static bool UvNotNaN(ref Vector2 uv, string name, List<string> clamped) {
            if (!float.IsNaN(uv.x) && !float.IsNaN(uv.y)) {
                return false;
            }
            uv = new Vector2(-1f, -1f);
            AddName(clamped, name);
            return true;
        }

        private static void AddName(List<string> names, string name) {
            if (names != null && !names.Contains(name)) {
                names.Add(name);
            }
        }
    }
}
