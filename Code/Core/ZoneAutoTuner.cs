using System.Collections.Generic;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// テクスチャと ColorZone の (sampleColor, targetColor) から、
    /// 許容範囲・彩度制限などのパラメータを自動的に算出する純粋ロジック層。
    /// UI からは VACCWindow.RunAutoTune() 経由で呼ばれ、結果は TuneResult として返す。
    /// このクラス自体は Texture2D.GetPixels32() 以外の Unity Editor API に依存しない。
    /// </summary>
    internal static class ZoneAutoTuner
    {
        // ── デフォルト値（ColorZone.cs / VACCSessionState.cs と同期） ──
        // 元ファイルへの変更を避けるためここに定数で持ち、同期は手作業で行う。
        private const float DefaultTolerance               = 0f;
        private const float DefaultSaturationStrictness    = 0.50f;
        private const float DefaultChromaThreshold         = 0.05f;
        private const bool  DefaultHighlightRecovery       = true;
        private const float DefaultValueBlend              = 1f;
        private const float DefaultEdgeSoftness            = 0f;
        private const float DefaultShadowDesaturation      = 0.35f;
        private const float DefaultShadowForgivenessSatMin = 0.05f;
        private const int   DefaultAntiAliasCleanup        = 3;
        private const bool  DefaultUseDecontamination      = true;

        // ── 解析パラメータ ──
        private const int HistogramBins = 32;
        private const float NearHueDist  = 0.10f;
        private const float NearSatDist  = 0.20f;
        private const float NearValDist  = 0.30f;
        private const int MinTextureDim  = 32;
        private const int MinNearSampleCount = 64;

        public struct TuneResult
        {
            // Per-zone（常に有効）
            public float tolerance;
            public float saturationStrictness;
            public float chromaThreshold;
            public bool  highlightRecovery;
            public float valueBlend;
            public float edgeSoftness;
            public float shadowDesaturation;
            public float shadowForgivenessSatMin;

            // Globals（applyGlobals が true のときのみ適用）
            // edgeFeather は「ごまかし」なので自動調整では一切扱わない（既定の 0 を維持）。
            public bool  applyGlobals;
            public int   antiAliasCleanup;
            public bool  useDecontamination;

            // 現在値がデフォルトと異なるフィールドのローカライズ済みラベル
            public List<string> overwrittenLabels;
        }

        /// <summary>
        /// texture, zone, session の現在値を読み取り、推奨値と上書き対象ラベルを返す。
        /// 副作用なし。失敗時もデフォルト相当の TuneResult を返す。
        /// </summary>
        public static TuneResult Analyze(Texture2D tex, ColorZone zone, VACCSessionState session)
        {
            var result = BuildHeuristicDefault(zone);

            bool canAnalyze = tex != null && tex.width >= MinTextureDim && tex.height >= MinTextureDim;
            if (canAnalyze)
            {
                if (TryAnalyzePixels(tex, zone, out var analyzed))
                    result = MergeAnalyzed(result, analyzed);
            }

            DecideGlobals(tex, session, ref result);
            CollectOverwrittenLabels(zone, session, ref result);
            return result;
        }

        // ─────────────────── 既定値ベース ───────────────────

        private static TuneResult BuildHeuristicDefault(ColorZone zone)
        {
            return new TuneResult
            {
                tolerance               = 0.25f,
                saturationStrictness    = DefaultSaturationStrictness,
                chromaThreshold         = DefaultChromaThreshold,
                highlightRecovery       = DefaultHighlightRecovery,
                valueBlend              = DefaultValueBlend,
                edgeSoftness            = 0.15f,
                shadowDesaturation      = DefaultShadowDesaturation,
                shadowForgivenessSatMin = DefaultShadowForgivenessSatMin,
                applyGlobals            = false,
                antiAliasCleanup        = DefaultAntiAliasCleanup,
                useDecontamination      = DefaultUseDecontamination,
                overwrittenLabels       = new List<string>(),
            };
        }

        // ─────────────────── ピクセル解析 ───────────────────

        private struct AnalysisStats
        {
            public int nearSampleCount;
            public int[] hBins;
            public int[] sBins;
            public int[] vBins;
            public int highlightCandidates;
            public float darkestNearSampleS;
            public bool darkSeen;
            public float sH, sS, sV;
            public float tV;  // target color V（明度差で valueBlend 判定に使う）
        }

        private static bool TryAnalyzePixels(Texture2D tex, ColorZone zone, out AnalysisStats stats)
        {
            stats = new AnalysisStats
            {
                hBins = new int[HistogramBins],
                sBins = new int[HistogramBins],
                vBins = new int[HistogramBins],
                darkestNearSampleS = 1f,
            };

            Color.RGBToHSV(zone.sampleColor, out stats.sH, out stats.sS, out stats.sV);
            Color.RGBToHSV(zone.targetColor, out _, out _, out stats.tV);

            Color32[] pixels;
            try
            {
                pixels = tex.GetPixels32();
            }
            catch (UnityEngine.UnityException)
            {
                // Read/Write 無効。呼び出し側でガードしている想定だが念のため。
                return false;
            }

            int w = tex.width;
            int h = tex.height;
            int stride = (w <= 2048) ? 1 : 2;

            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c32 = pixels[rowStart + x];
                    if (c32.a < 128) continue;

                    float r = c32.r / 255f;
                    float g = c32.g / 255f;
                    float b = c32.b / 255f;
                    Color.RGBToHSV(new Color(r, g, b, 1f), out float pH, out float pS, out float pV);

                    float hDist = HueDistance(pH, stats.sH);

                    // ハイライト復元候補（サンプル色と同系のハイライト領域）
                    if (pV > 0.80f && pS < 0.20f && hDist < 0.15f)
                        stats.highlightCandidates++;

                    if (hDist < NearHueDist &&
                        Mathf.Abs(pS - stats.sS) < NearSatDist &&
                        Mathf.Abs(pV - stats.sV) < NearValDist)
                    {
                        stats.nearSampleCount++;
                        stats.hBins[Mathf.Clamp((int)(pH * HistogramBins), 0, HistogramBins - 1)]++;
                        stats.sBins[Mathf.Clamp((int)(pS * HistogramBins), 0, HistogramBins - 1)]++;
                        stats.vBins[Mathf.Clamp((int)(pV * HistogramBins), 0, HistogramBins - 1)]++;

                        if (pV < 0.4f && pS < stats.darkestNearSampleS)
                        {
                            stats.darkestNearSampleS = pS;
                            stats.darkSeen = true;
                        }
                    }
                }
            }

            return stats.nearSampleCount >= MinNearSampleCount;
        }

        private static TuneResult MergeAnalyzed(TuneResult heuristic, AnalysisStats s)
        {
            float hSpread = HueSpreadFromHistogram(s.hBins, s.sH, s.nearSampleCount);
            float sP10 = PercentileBin(s.sBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP10 = PercentileBin(s.vBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP90 = PercentileBin(s.vBins, s.nearSampleCount, 0.90f) / (float)HistogramBins;
            float vSpread = Mathf.Max(0f, vP90 - vP10);

            // tolerance: 色相の広がり + マージン。低彩度時は V の広がりで近似。
            float tolerance;
            if (s.sS < 0.10f)
            {
                tolerance = Mathf.Clamp(vSpread + 0.05f, 0.10f, 0.50f);
            }
            else
            {
                tolerance = Mathf.Clamp(hSpread + 0.05f, 0.10f, 0.50f);
            }

            // saturationStrictness: 低彩度サンプル → 緩める。低 S テールが目立つ → 厳しく。
            float saturationStrictness;
            if (s.sS < 0.10f)
            {
                saturationStrictness = 0.30f;
            }
            else if (sP10 < s.sS * 0.3f)
            {
                saturationStrictness = 0.65f;
            }
            else
            {
                saturationStrictness = 0.50f;
            }

            // chromaThreshold: サンプル彩度がしきい値以下のときグレースケールモードに入る仕様（ColorZone.GetColorMatchScores 参照）。
            // 低彩度サンプル(sS < 0.15)はしきい値を sS + 0.05 に引き上げて確実にグレーモード化、
            // 有彩色サンプルは既定値 0.05 を維持して通常の HSV マッチに任せる。
            float chromaThreshold = (s.sS < 0.15f)
                ? Mathf.Min(0.20f, s.sS + 0.05f)
                : DefaultChromaThreshold;

            // highlightRecovery: 同系ハイライトが十分にあれば有効化
            int highlightThreshold = Mathf.Max(50, Mathf.RoundToInt(s.nearSampleCount * 0.02f));
            bool highlightRecovery = s.highlightCandidates >= highlightThreshold;

            // valueBlend: 原則 1.0（模様完全保持）を維持。
            // サンプルとターゲットの明度差が極端（例: 明るい色 → 黒）な場合のみ下げる。
            // それ以外では模様を残すことを最優先する方針。
            float vDelta = Mathf.Abs(s.sV - s.tV);
            float valueBlend;
            if (vDelta > 0.55f) valueBlend = 0.7f;        // 例: 白系 → 黒系
            else if (vDelta > 0.35f) valueBlend = 0.9f;   // 中程度の明度差
            else valueBlend = 1.0f;                       // 通常は模様完全保持

            // edgeSoftness: 色相広がりが大きいほど柔らかく
            float edgeSoftness;
            if (hSpread > 0.15f) edgeSoftness = 0.4f;
            else if (hSpread < 0.05f) edgeSoftness = 0.0f;
            else edgeSoftness = 0.15f;

            // shadowForgivenessSatMin: 暗部の最小彩度を見て巻き込みを抑制
            float shadowForgivenessSatMin = DefaultShadowForgivenessSatMin;
            if (s.darkSeen && s.darkestNearSampleS > 0.10f)
            {
                shadowForgivenessSatMin = Mathf.Clamp(s.darkestNearSampleS * 0.5f, 0.05f, 0.20f);
            }

            return new TuneResult
            {
                tolerance               = tolerance,
                saturationStrictness    = saturationStrictness,
                chromaThreshold         = chromaThreshold,
                highlightRecovery       = highlightRecovery,
                valueBlend              = valueBlend,
                edgeSoftness            = edgeSoftness,
                shadowDesaturation      = heuristic.shadowDesaturation,
                shadowForgivenessSatMin = shadowForgivenessSatMin,
                applyGlobals            = false,
                antiAliasCleanup        = heuristic.antiAliasCleanup,
                useDecontamination      = heuristic.useDecontamination,
                overwrittenLabels       = new List<string>(),
            };
        }

        // ─────────────────── Globals 判定 ───────────────────

        private static void DecideGlobals(Texture2D tex, VACCSessionState session, ref TuneResult result)
        {
            // edgeFeather は自動調整では一切触らない（"ごまかし" を増やさない方針）。
            // 自動調整が扱う Global は antiAliasCleanup と useDecontamination のみ。
            // 1) これらが既定 かつ 2) 他ゾーンの基本パラメータも全て既定 のときだけ提案する。
            // ユーザーが既に手で動かしている場合は触らない（"後勝ち事故" 防止）。
            bool globalsAtDefault =
                session.antiAliasCleanup == DefaultAntiAliasCleanup &&
                session.useDecontamination == DefaultUseDecontamination;

            bool allZonesAtDefault = true;
            if (session.zones != null)
            {
                foreach (var z in session.zones)
                {
                    if (!IsZoneBasicsAtDefault(z))
                    {
                        allZonesAtDefault = false;
                        break;
                    }
                }
            }

            if (!globalsAtDefault || !allZonesAtDefault)
            {
                result.applyGlobals = false;
                return;
            }

            result.applyGlobals = true;
            // AA 境界クリーンアップ: 高解像度ほど AA フリンジが太く、回収パスを増やす方が
            // 境界品質が上がる。テクスチャ寸法のみから導出（キャラ・色に依存しない）。
            result.antiAliasCleanup = AntiAliasCleanupForResolution(tex);
            result.useDecontamination = DefaultUseDecontamination;
        }

        private static int AntiAliasCleanupForResolution(Texture2D tex)
        {
            if (tex == null) return DefaultAntiAliasCleanup;
            int dim = Mathf.Max(tex.width, tex.height);
            if (dim >= 2048) return 5;
            if (dim >= 1024) return 4;
            return DefaultAntiAliasCleanup; // 3 = 推奨下限
        }

        // ─────────────────── 上書き対象ラベル収集 ───────────────────

        private static void CollectOverwrittenLabels(ColorZone zone, VACCSessionState session, ref TuneResult result)
        {
            var labels = result.overwrittenLabels;

            if (!Mathf.Approximately(zone.tolerance, DefaultTolerance))
                labels.Add(Localization.Tolerance);
            if (!Mathf.Approximately(zone.saturationStrictness, DefaultSaturationStrictness))
                labels.Add(Localization.SaturationStrictness);
            if (!Mathf.Approximately(zone.chromaThreshold, DefaultChromaThreshold))
                labels.Add(Localization.IsJapanese ? "自動しきい値(無彩色判定)" : "Auto Grayscale Threshold");
            if (zone.highlightRecovery != DefaultHighlightRecovery)
                labels.Add(Localization.HighlightRecovery);
            if (!Mathf.Approximately(zone.valueBlend, DefaultValueBlend))
                labels.Add(Localization.PatternPreserve);
            if (!Mathf.Approximately(zone.edgeSoftness, DefaultEdgeSoftness))
                labels.Add(Localization.EdgeSoftness);
            if (!Mathf.Approximately(zone.shadowDesaturation, DefaultShadowDesaturation))
                labels.Add(Localization.ShadowDesaturation);
            if (!Mathf.Approximately(zone.shadowForgivenessSatMin, DefaultShadowForgivenessSatMin))
                labels.Add(Localization.ShadowForgivenessSatMin);

            if (result.applyGlobals)
            {
                if (session.antiAliasCleanup != DefaultAntiAliasCleanup)
                    labels.Add(Localization.AntiAliasCleanup);
                if (session.useDecontamination != DefaultUseDecontamination)
                    labels.Add(Localization.UseDecontamination);
            }
        }

        // ─────────────────── ユーティリティ ───────────────────

        private static bool IsZoneBasicsAtDefault(ColorZone z)
        {
            if (z == null) return true;
            return Mathf.Approximately(z.tolerance, DefaultTolerance)
                && Mathf.Approximately(z.saturationStrictness, DefaultSaturationStrictness)
                && Mathf.Approximately(z.chromaThreshold, DefaultChromaThreshold)
                && z.highlightRecovery == DefaultHighlightRecovery
                && Mathf.Approximately(z.valueBlend, DefaultValueBlend)
                && Mathf.Approximately(z.edgeSoftness, DefaultEdgeSoftness)
                && Mathf.Approximately(z.shadowDesaturation, DefaultShadowDesaturation)
                && Mathf.Approximately(z.shadowForgivenessSatMin, DefaultShadowForgivenessSatMin);
        }

        private static float HueDistance(float a, float b)
        {
            float d = Mathf.Abs(a - b);
            return d > 0.5f ? 1f - d : d;
        }

        /// <summary>
        /// ヒストグラムから P10/P90 を求め、色相は環状なのでサンプル色相 sH を中心として
        /// 「中心からの最大距離」相当を返す（P90 of |hue - sH|_wrap）。
        /// </summary>
        private static float HueSpreadFromHistogram(int[] hBins, float sH, int total)
        {
            if (total <= 0) return 0f;
            // 各 bin の中心色相 → サンプル色相からの環状距離をキーに、上位 10% を切る。
            // bin 数 = 32 と小さいので、距離ソートをしてから累積で 90 パーセンタイル位置を取る。
            int n = hBins.Length;
            float[] dists = new float[n];
            int[] counts = new int[n];
            for (int i = 0; i < n; i++)
            {
                float center = (i + 0.5f) / n;
                dists[i] = HueDistance(center, sH);
                counts[i] = hBins[i];
            }
            // 簡易ソート（n=32 なので選択ソートでも十分高速）
            for (int i = 0; i < n - 1; i++)
            {
                int min = i;
                for (int j = i + 1; j < n; j++)
                    if (dists[j] < dists[min]) min = j;
                if (min != i)
                {
                    float tmpD = dists[i]; dists[i] = dists[min]; dists[min] = tmpD;
                    int tmpC = counts[i]; counts[i] = counts[min]; counts[min] = tmpC;
                }
            }

            int target = Mathf.CeilToInt(total * 0.90f);
            int cum = 0;
            for (int i = 0; i < n; i++)
            {
                cum += counts[i];
                if (cum >= target) return dists[i];
            }
            return 0.5f;
        }

        /// <summary>
        /// ヒストグラムから指定パーセンタイルに対応する bin index を返す（小数）。
        /// </summary>
        private static float PercentileBin(int[] bins, int total, float p)
        {
            if (total <= 0) return 0f;
            int target = Mathf.CeilToInt(total * p);
            int cum = 0;
            for (int i = 0; i < bins.Length; i++)
            {
                cum += bins[i];
                if (cum >= target) return i;
            }
            return bins.Length - 1;
        }
    }
}
