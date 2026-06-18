// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// テクスチャと ColorZone の (sampleColor, targetColor) から、
    /// 許容範囲・彩度制限などのパラメータを自動的に算出する純粋ロジック層。
    /// UI からは VACCWindow.RunAutoTune() 経由で呼ばれ、結果は TuneResult として返す。
    /// 純粋計算のため、ピクセル配列さえあればバックグラウンドスレッドからも呼べる。
    /// </summary>
    internal static class ZoneAutoTuner
    {
        // ── デフォルト値（ColorZone.cs / VACCSessionState.cs と同期） ──
        // 元ファイルへの変更を避けるためここに定数で持ち、同期は手作業で行う。
        private const float DefaultTolerance               = 0f;
        private const float DefaultSaturationStrictness    = 0.50f;
        private const float DefaultSaturationGuard         = 0f;
        private const float DefaultChromaThreshold         = 0.05f;
        private const bool  DefaultHighlightRecovery       = true;
        private const float DefaultValueBlend              = 1f;
        private const float DefaultEdgeSoftness            = 0f;
        private const float DefaultShadowDesaturation      = 0.35f;
        private const float DefaultShadowForgivenessSatMin = 0.05f;
        private const int   DefaultAntiAliasCleanup        = 3;
        private const bool  DefaultUseDecontamination      = true;

        // 彩度ガード自動導出パラメータ
        //
        // 感度解析(dev_safe/scripts/_audit_saturation_guard.py, 2026-05-20)の結論:
        //   - sneaker(sS=1.0) / costume(sS=1.0): guard ON で大幅改善
        //       sneaker tol=0.40 IoU 0.655→0.931 (FP 2.46M→325k)
        //       costume tol=0.15 IoU 0.93996→0.93435
        //   - hair(sS=0.963) / bandana(sS=0.502): guard ON で悪化
        //       これらは「暗部で S が落ちる素材(影=低彩度が正常)」のため、
        //       低彩度画素を弾くと正常なシェーディングまで削ってしまう。
        //
        // ⇒ 自動提案は「源色 S が極端に高い (>=0.95)」のときに限定する。
        //    境界の sS=0.95〜1.0 では guard を 0.5〜0.8 で線形補間。
        //    UI スライダーは独立に常時露出されているので、中彩度サンプルでも
        //    ユーザーが手動で guard を有効化することは可能。
        private const float SaturationGuardActivateSS    = 0.95f;
        private const float SaturationGuardMinAtActivate = 0.5f;
        private const float SaturationGuardMaxAtHighSS   = 0.8f;

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
            public float saturationGuard;
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
        /// 既存呼び出し互換 API。Texture2D を受け取り、内部でメインスレッド前提の
        /// GetPixels32 を呼んでからピクセル受け取り版へ委譲する。
        /// バックグラウンドスレッドから呼ぶ場合は、メインスレッドで取得した
        /// Color32[] を渡せる <see cref="Analyze(Color32[], int, int, ColorZone, VACCSessionState, bool[], int, int)"/>
        /// オーバーロードを使用すること。
        /// </summary>
        public static TuneResult Analyze(Texture2D tex, ColorZone zone, VACCSessionState session,
            bool[] excluded = null, int maskW = 0, int maskH = 0)
        {
            Color32[] pixels = null;
            int w = 0, h = 0;
            if (tex != null)
            {
                w = tex.width;
                h = tex.height;
                try { pixels = tex.GetPixels32(); }
                catch (UnityEngine.UnityException) { pixels = null; }
            }
            return Analyze(pixels, w, h, zone, session, excluded, maskW, maskH);
        }

        /// <summary>
        /// pixels, zone, session から推奨値と上書き対象ラベルを返す。
        /// 副作用なし。失敗時もデフォルト相当の TuneResult を返す。
        /// pixels が null / 寸法が極端に小さい場合はヒューリスティック既定のみで返す。
        /// </summary>
        /// <param name="excluded">
        /// 除外マスク(共通∪ゾーン別の OR 結合, true=除外)。サイズ maskW*maskH。
        /// null または全 false の場合はマスク無しパス。マスクがある場合は
        /// 「含有(非除外)領域全体をパーツとみなし、その距離分布から tolerance を導出」する。
        /// </param>
        public static TuneResult Analyze(Color32[] pixels, int width, int height,
            ColorZone zone, VACCSessionState session,
            bool[] excluded = null, int maskW = 0, int maskH = 0)
        {
            var result = BuildHeuristicDefault(zone);

            bool canAnalyze = pixels != null
                && width >= MinTextureDim && height >= MinTextureDim
                && pixels.Length >= width * height;
            if (canAnalyze)
            {
                if (TryAnalyzePixels(pixels, width, height, zone, out var analyzed))
                    result = MergeAnalyzed(result, analyzed);

                // マスク運用前提（はみ出しは手動マスク担当）: 含有領域全体をパーツとみなし、
                // パーツ内の薄い装飾（白プリント等）まで均一に match できるよう tolerance を
                // 含有領域の距離分布 P99.9 から導出する。マスクは色からは推論不可能な
                // 「パーツ分離」をユーザーが与えたものなので、それを最大限尊重する。
                if (HasUsableMask(excluded, maskW, maskH))
                {
                    if (TryDeriveMaskAwareTolerance(pixels, width, height, zone, excluded, maskW, maskH,
                            out float maskTol))
                    {
                        result.tolerance = maskTol;
                        // パーツが分離済みなら白プリント等を薄く色づけるため復元を有効化。
                        result.highlightRecovery = true;
                    }
                }
                else
                {
                    // マスク無し(かんたんモード相当)。tolerance は「サンプル近傍クラスタの実マッチ距離
                    // 分布」から取り直す。無彩(グレーモード=純 RGB 距離)と有彩(HSV マッチ)で距離式が
                    // 違うため経路を分けるが、いずれも MergeAnalyzed の hSpread/vSpread 由来ヒューリスティック
                    // (実距離と切り離され過大選択を招く)を実距離分布へ置き換える。
                    Color.RGBToHSV(zone.sampleColor, out _, out float sampleS, out _);
                    if (sampleS < AchromaSampleSatMax)
                    {
                        // 無彩色サンプル: グレーモードの純 RGB 距離分布から(V 広がりの過大評価を回避)。
                        if (TryDeriveAchromaticTolerance(pixels, width, height, zone, out float achTol))
                        {
                            result.tolerance = achTol;
                            // 無彩色サンプルではハイライト復元を切る。グレーモードのハイライト経路は
                            // 「明度だけ」で判定し色相/彩度を見ないため、明るい有彩画素(別素材)まで
                            // 巻き込んでしまう。グレー本体のハイライトは RGB 距離 tolerance で拾える。
                            result.highlightRecovery = false;
                        }
                    }
                    else if (TryDeriveChromaticTolerance(pixels, width, height, zone, out float chromTol))
                    {
                        // 有彩サンプル: 本番 HSV マッチ距離分布から(hSpread+0.10 の過大選択を解消)。
                        result.tolerance = chromTol;
                    }
                }
            }

            DecideGlobals(width, height, session, ref result);
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
                saturationGuard         = DefaultSaturationGuard,
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

        private static bool TryAnalyzePixels(Color32[] pixels, int w, int h, ColorZone zone, out AnalysisStats stats)
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

        // ─────────────────── マスク認識型 tolerance ───────────────────

        private const int DistBins = 600;          // 距離 [0,1.5] を 600 分割（分解能 0.0025）
        private const float DistMax = 1.5f;
        private const float MaskAwarePercentile = 0.999f;
        private const float MaskAwareMargin = 0.03f;
        private const float MaskAwareTolMin = 0.12f;
        private const float MaskAwareTolMax = 0.40f;

        private static bool HasUsableMask(bool[] excluded, int maskW, int maskH)
        {
            if (excluded == null || maskW <= 0 || maskH <= 0) return false;
            if (excluded.Length < maskW * maskH) return false;
            // 何も除外していない / 全部除外 のマスクは「パーツ分離」として使えない。
            int total = maskW * maskH;
            int ex = 0;
            for (int i = 0; i < total; i++) if (excluded[i]) ex++;
            return ex > 0 && ex < total;
        }

        /// <summary>
        /// 含有(非除外)opaque ピクセルの「サンプルからの match 距離」分布の高パーセンタイル
        /// を tolerance とする。マスクが定義したパーツ全体（薄い装飾含む）を均一に
        /// match させるため。距離式は本番アルゴリズムと同一:
        ///   d = hueDist + |dS|*satDistWeight + |dV|*valueWeight*(1 - sRatio)
        /// </summary>
        private static bool TryDeriveMaskAwareTolerance(Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, out float tolerance)
        {
            tolerance = 0f;

            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            float satDistW = zone.satDistWeight;
            float valueW = zone.valueWeight;

            int stride = (w <= 2048) ? 1 : 2;
            var bins = new int[DistBins];
            int count = 0;

            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                int my = Mathf.Clamp(y * maskH / h, 0, maskH - 1);
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c32 = pixels[rowStart + x];
                    if (c32.a < 128) continue;
                    int mx = Mathf.Clamp(x * maskW / w, 0, maskW - 1);
                    if (excluded[my * maskW + mx]) continue; // 除外パーツ外

                    Color.RGBToHSV(new Color(c32.r / 255f, c32.g / 255f, c32.b / 255f, 1f),
                        out float pH, out float pS, out float pV);

                    float hd = Mathf.Abs(pH - sH);
                    if (hd > 0.5f) hd = 1f - hd;
                    float sd = Mathf.Abs(pS - sS);
                    float vd = Mathf.Abs(pV - sV);
                    float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
                    float d = hd + sd * satDistW + vd * valueW * (1f - sRatio);

                    int bi = Mathf.Clamp((int)(d / DistMax * DistBins), 0, DistBins - 1);
                    bins[bi]++;
                    count++;
                }
            }

            if (count < MinNearSampleCount) return false;

            int target = Mathf.CeilToInt(count * MaskAwarePercentile);
            int cum = 0;
            float pctDist = DistMax;
            for (int i = 0; i < DistBins; i++)
            {
                cum += bins[i];
                if (cum >= target)
                {
                    pctDist = (i + 1) / (float)DistBins * DistMax;
                    break;
                }
            }
            tolerance = Mathf.Clamp(pctDist + MaskAwareMargin, MaskAwareTolMin, MaskAwareTolMax);
            return true;
        }

        // ─────────────────── 無彩色(低彩度サンプル)の tolerance ───────────────────
        // 無彩色サンプル(白/黒/グレー)はマッチングがグレーモード=純 RGB 距離になる。
        // 旧来の「near-box の V 広がり(vSpread)+0.10、上限 0.50」は、(1)サンプルからの距離
        // ではなくパート全体の V 幅(両側)を使うため約 2 倍に過大評価し、(2)上限 0.50 が
        // 黒や(RGB が近ければ)有彩色まで巻き込む。代わりにマスク認識型と同じく「サンプルからの
        // 実 RGB 距離分布の高パーセンタイル」で導出する。クラスタは『無彩寄り(低彩度)かつ
        // サンプルと明度が近い』画素に限定し、別パート(黒/白)や有彩を距離分布から排除する。
        private const float AchromaSampleSatMax = 0.15f; // このサンプル彩度未満で無彩 tolerance を使う
        private const float AchromaClusterSatMax = 0.20f; // クラスタに入れる画素の彩度上限(有彩を除外)
        private const float AchromaVWindow = 0.30f;       // サンプル明度からの V 窓(想定シェーディング幅)
        private const float AchromaPercentile = 0.95f;
        private const float AchromaMargin = 0.04f;
        private const float AchromaTolMin = 0.12f;
        private const float AchromaTolMax = 0.40f;

        private static bool TryDeriveAchromaticTolerance(Color32[] pixels, int w, int h, ColorZone zone,
            out float tolerance)
        {
            tolerance = 0f;
            Color.RGBToHSV(zone.sampleColor, out _, out _, out float sV);
            float sr = zone.sampleColor.r, sg = zone.sampleColor.g, sb = zone.sampleColor.b;

            int stride = (w <= 2048) ? 1 : 2;
            var bins = new int[DistBins];
            int count = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    Color.RGBToHSV(new Color(r, g, b, 1f), out _, out float pS, out float pV);
                    if (pS > AchromaClusterSatMax) continue;        // 有彩は別素材として距離分布に入れない
                    if (Mathf.Abs(pV - sV) > AchromaVWindow) continue; // 明度が遠い(黒/白の別パート)は除外
                    float dr = r - sr, dg = g - sg, db = b - sb;
                    float d = Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f; // グレーモードの距離式と一致
                    int bi = Mathf.Clamp((int)(d / DistMax * DistBins), 0, DistBins - 1);
                    bins[bi]++;
                    count++;
                }
            }
            if (count < MinNearSampleCount) return false;

            int target = Mathf.CeilToInt(count * AchromaPercentile);
            int cum = 0;
            float pctDist = DistMax;
            for (int i = 0; i < DistBins; i++)
            {
                cum += bins[i];
                if (cum >= target) { pctDist = (i + 1) / (float)DistBins * DistMax; break; }
            }
            tolerance = Mathf.Clamp(pctDist + AchromaMargin, AchromaTolMin, AchromaTolMax);
            return true;
        }

        // ─────────────────── 有彩(中〜高彩度サンプル)の tolerance ───────────────────
        // 有彩サンプルの no-mask tolerance は従来 MergeAnalyzed で hSpread(色相広がり P90)+0.10 と
        // 導出していたが、(1) 実マッチ距離(彩度・明度項を含む)と切り離され、(2) 平坦な +0.10 マージンが
        // 暗い高彩度パーツ(例: 黒寄りスニーカー青)で過大選択を招いていた(実測: 導出 0.13 / 最適 ~0.08、
        // tol を下げると IoU 0.85→0.95・precision 0.85→0.99)。無彩経路(TryDeriveAchromaticTolerance,
        // commit 43e2a0a)と同じく「サンプル近傍クラスタの実距離分布の高パーセンタイル」から導出する。
        // 距離式は本番(ColorZone)と同一:
        //   d = hd + |dS|*satDistWeight + |dV|*valueWeight*(1 - clamp(pS/sS))
        // クラスタは near-sample(色相<0.10, |dS|<0.20, |dV|<0.30 = サンプルに似た同パーツ相当の画素)に
        // 限定し、別パーツを距離分布から除外する。床は無彩/マスク認識(0.12)より低い 0.08 とし、暗い高彩度
        // パーツのタイトな分布に追従できるようにする(素直なパーツでは P95+margin がこの床に収まり、内部変動
        // が大きいパーツでは P95 が上がるので自然に広がる ⇒ 取りこぼしと過選択のバランスが取れる)。
        // クラスタが過少(<MinNearSampleCount)なら false を返し、MergeAnalyzed の hSpread tolerance を温存。
        //
        // 【無彩画素の混入対策】Unity の Color.RGBToHSV は無彩(R=G=B)画素の hue を 0 に丸める。
        // そのため暖色(hue≈0)かつ低彩度(sS<NearSatDist=0.20)のサンプルでは、背景や陰影の
        // グレー画素(pS≈0, hue=0)が near-sample クラスタに紛れ込み、その大きな value 項距離で
        // P95 が跳ね上がって tolerance が上限に張り付く(実測: 暖色 sS≈0.17 で 0.35〜0.40)。
        // クラスタは「色のついた同パーツ画素」を表すべきなので、サンプル彩度の一定割合に満たない
        // 無彩寄り画素を距離分布から除外する(無彩経路 TryDeriveAchromaticTolerance が彩度上限で
        // 有彩を除外するのと対称)。パーツ自身の中程度の陰影(pS ≳ sS*frac)は残り、彩度が大きく
        // 落ちる深い陰影は本番のシャドウ免除が拾うので tolerance で覆う必要はない。
        // 上限は HSV 距離で色相 0.22 ぶん(≈79°)までに抑える(0.40 は色相 144° 相当で広すぎた)。
        private const float ChromaPercentile = 0.95f;
        private const float ChromaMargin = 0.03f;
        private const float ChromaTolMin = 0.08f;
        private const float ChromaTolMax = 0.22f;
        private const float ChromaClusterSatFrac = 0.35f; // pS < sS*frac の無彩寄り画素はクラスタから除外

        private static bool TryDeriveChromaticTolerance(Color32[] pixels, int w, int h, ColorZone zone,
            out float tolerance)
        {
            tolerance = 0f;
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            float satDistW = zone.satDistWeight;
            float valueW = zone.valueWeight;

            int stride = (w <= 2048) ? 1 : 2;
            var bins = new int[DistBins];
            int count = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    Color.RGBToHSV(new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f),
                        out float pH, out float pS, out float pV);
                    float hd = Mathf.Abs(pH - sH);
                    if (hd > 0.5f) hd = 1f - hd;
                    // near-sample クラスタ(サンプルに似た画素=同パーツ相当)に限定
                    if (hd >= NearHueDist) continue;
                    if (Mathf.Abs(pS - sS) >= NearSatDist) continue;
                    if (Mathf.Abs(pV - sV) >= NearValDist) continue;
                    // 無彩寄り画素(背景/陰影のグレー, hue=0 で暖色サンプルに誤マッチ)を除外
                    if (pS < sS * ChromaClusterSatFrac) continue;
                    float sd = Mathf.Abs(pS - sS);
                    float vd = Mathf.Abs(pV - sV);
                    float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
                    float d = hd + sd * satDistW + vd * valueW * (1f - sRatio);
                    int bi = Mathf.Clamp((int)(d / DistMax * DistBins), 0, DistBins - 1);
                    bins[bi]++;
                    count++;
                }
            }
            if (count < MinNearSampleCount) return false;

            int target = Mathf.CeilToInt(count * ChromaPercentile);
            int cum = 0;
            float pctDist = DistMax;
            for (int i = 0; i < DistBins; i++)
            {
                cum += bins[i];
                if (cum >= target) { pctDist = (i + 1) / (float)DistBins * DistMax; break; }
            }
            tolerance = Mathf.Clamp(pctDist + ChromaMargin, ChromaTolMin, ChromaTolMax);
            return true;
        }

        private static TuneResult MergeAnalyzed(TuneResult heuristic, AnalysisStats s)
        {
            float hSpread = HueSpreadFromHistogram(s.hBins, s.sH, s.nearSampleCount);
            float sP10 = PercentileBin(s.sBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP10 = PercentileBin(s.vBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP90 = PercentileBin(s.vBins, s.nearSampleCount, 0.90f) / (float)HistogramBins;
            float vSpread = Mathf.Max(0f, vP90 - vP10);

            // tolerance（マスク無しパス）: 色相の広がり + マージン。低彩度時は V の広がりで近似。
            // マスク運用前提では別途 mask-aware パスで上書きされる（DeriveMaskAwareTolerance）。
            float tolerance;
            if (s.sS < 0.10f)
            {
                tolerance = Mathf.Clamp(vSpread + 0.10f, 0.12f, 0.50f);
            }
            else
            {
                tolerance = Mathf.Clamp(hSpread + 0.10f, 0.12f, 0.50f);
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

            // saturationGuard: 源色 S が極端に高い (>=0.95) ときだけ自動提案。
            // sS=0.95 → Min(0.5)、sS=1.0 → Max(0.8) で線形補間。
            // sS<0.95 では 0 のまま（感度解析で hair(sS=0.96)/bandana(sS=0.50) が
            // 控えめ guard でも悪化することを確認 — シェーディングが S を落とす素材を
            // 巻き込まないよう厳しめのゲート）。
            //
            // 数値はキャラ・テクスチャ依存ではなく「源色の彩度」という距離式入力から
            // 導出しているため、特定テクスチャへのハードコードにはならない。
            float saturationGuard;
            if (s.sS < SaturationGuardActivateSS)
            {
                saturationGuard = 0f;
            }
            else
            {
                float t = Mathf.Clamp01((s.sS - SaturationGuardActivateSS)
                                        / Mathf.Max(0.001f, 1f - SaturationGuardActivateSS));
                saturationGuard = Mathf.Lerp(SaturationGuardMinAtActivate,
                                             SaturationGuardMaxAtHighSS, t);
            }

            return new TuneResult
            {
                tolerance               = tolerance,
                saturationStrictness    = saturationStrictness,
                saturationGuard         = saturationGuard,
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

        private static void DecideGlobals(int texWidth, int texHeight, VACCSessionState session, ref TuneResult result)
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
            result.antiAliasCleanup = AntiAliasCleanupForResolution(texWidth, texHeight);
            result.useDecontamination = DefaultUseDecontamination;
        }

        private static int AntiAliasCleanupForResolution(int width, int height)
        {
            if (width <= 0 || height <= 0) return DefaultAntiAliasCleanup;
            int dim = Mathf.Max(width, height);
            if (dim >= 2048) return 5;
            if (dim >= 1024) return 4;
            return DefaultAntiAliasCleanup; // 3 = 推奨下限
        }

        // ─────────────────── 上書き対象ラベル収集 ───────────────────

        /// <summary>
        /// Analyze を呼ぶ前に、現在の zone 値が default と異なるか（＝自動調整で上書きされ
        /// うるか）を判定して labels を返す。globals 関連ラベルは applyGlobals=true 時のみ
        /// 追加されるが、その条件下では globals は default 値であるため実質追加されない
        /// （CollectOverwrittenLabels と整合）。
        ///
        /// 非同期化のために事前確認をジョブ開始前へ移動する用途で使う。
        /// </summary>
        public static List<string> PreviewOverwrittenLabels(ColorZone zone)
        {
            var labels = new List<string>();
            if (zone == null) return labels;

            if (!Mathf.Approximately(zone.tolerance, DefaultTolerance))
                labels.Add(Localization.Tolerance);
            if (!Mathf.Approximately(zone.saturationStrictness, DefaultSaturationStrictness))
                labels.Add(Localization.SaturationStrictness);
            if (!Mathf.Approximately(zone.saturationGuard, DefaultSaturationGuard))
                labels.Add(Localization.SaturationGuard);
            if (!Mathf.Approximately(zone.chromaThreshold, DefaultChromaThreshold))
                labels.Add(Localization.ChromaThreshold);
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
            return labels;
        }

        private static void CollectOverwrittenLabels(ColorZone zone, VACCSessionState session, ref TuneResult result)
        {
            var labels = result.overwrittenLabels;

            if (!Mathf.Approximately(zone.tolerance, DefaultTolerance))
                labels.Add(Localization.Tolerance);
            if (!Mathf.Approximately(zone.saturationStrictness, DefaultSaturationStrictness))
                labels.Add(Localization.SaturationStrictness);
            if (!Mathf.Approximately(zone.saturationGuard, DefaultSaturationGuard))
                labels.Add(Localization.SaturationGuard);
            if (!Mathf.Approximately(zone.chromaThreshold, DefaultChromaThreshold))
                labels.Add(Localization.ChromaThreshold);
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
                && Mathf.Approximately(z.saturationGuard, DefaultSaturationGuard)
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
