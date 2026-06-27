// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// テクスチャと ColorZone の (sampleColor, targetColor) から、
    /// 許容範囲・彩度制限などのパラメータを自動的に算出する純粋ロジック層。
    /// UI からは IrocaWindow.RunAutoTune() 経由で呼ばれ、結果は TuneResult として返す。
    /// 純粋計算のため、ピクセル配列さえあればバックグラウンドスレッドからも呼べる。
    /// </summary>
    internal static class ZoneAutoTuner
    {
        // ── デフォルト値（ColorZone.cs / IrocaSessionState.cs と同期） ──
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

            // 自動トーン抽出で生成した内部サンプル（暗部/中間/明部の代表色）。
            // ユーザーのスポイト1点から算出され、選択（マッチング）の和集合に使う。
            // 空のとき＝単一サンプル挙動。zone.extraSamples へ適用される。
            public List<Color> autoSamples;
        }

        /// <summary>
        /// 既存呼び出し互換 API。Texture2D を受け取り、内部でメインスレッド前提の
        /// GetPixels32 を呼んでからピクセル受け取り版へ委譲する。
        /// バックグラウンドスレッドから呼ぶ場合は、メインスレッドで取得した
        /// Color32[] を渡せる <see cref="Analyze(Color32[], int, int, ColorZone, IrocaSessionState, bool[], int, int)"/>
        /// オーバーロードを使用すること。
        /// </summary>
        public static TuneResult Analyze(Texture2D tex, ColorZone zone, IrocaSessionState session,
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
            ColorZone zone, IrocaSessionState session,
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

                // tolerance は常に「サンプル近傍クラスタの実マッチ距離分布」から導出する。
                // 無彩(グレーモード=純 RGB 距離)と有彩(HSV マッチ)で距離式が違うため経路を分けるが、
                // いずれも MergeAnalyzed の hSpread/vSpread 由来ヒューリスティック(実距離と切り離され
                // 過大選択を招く)を実距離分布へ置き換える。
                //
                // マスクがある場合は含有(非除外)領域にクラスタを限定する(色が同じ別パーツを除外)が、
                // tolerance 自体はクラスタの色のまとまりから決める。旧「マスク認識経路(含有領域全画素の
                // P99.9, 上限0.40)」は、ゆるい/残存マスクや明暗の広いパーツ(例: 明るいサンプルの髪)で
                // 上限 0.40 に張り付いていた(ユーザー報告)ため廃止。パーツ内の暗部・薄い装飾は本番の
                // シャドウ免除/ハイライト復元が tolerance とは独立に拾うので、tolerance を膨らませない。
                bool useMask = HasUsableMask(excluded, maskW, maskH);
                bool[] clusterMask = useMask ? excluded : null;
                Color.RGBToHSV(zone.sampleColor, out _, out float sampleS, out _);

                if (sampleS < AchromaSampleSatMax)
                {
                    // 無彩色サンプル: グレーモードの純 RGB 距離分布から(V 広がりの過大評価を回避)。
                    // 自動トーン抽出は無彩では背景の白/黒と色で分離できず危険なので行わない（単一経路）。
                    if (TryDeriveAchromaticTolerance(pixels, width, height, zone,
                            clusterMask, maskW, maskH, out float achTol))
                    {
                        result.tolerance = achTol;
                        // 無彩色サンプルではハイライト復元を切る。グレーモードのハイライト経路は
                        // 「明度だけ」で判定し色相/彩度を見ないため、明るい有彩画素(別素材)まで
                        // 巻き込んでしまう。グレー本体のハイライトは RGB 距離 tolerance で拾える。
                        result.highlightRecovery = false;
                    }
                }
                else
                {
                    // ── 有彩サンプル: 自動トーン抽出（内部マルチサンプル）─────────────────
                    // スポイト1点から、同色相のパーツ全体のトーン分布を内部で走査し、暗部・中間・
                    // 明部の代表色を自動生成する（ユーザーの追加スポイト操作は不要）。これらを和集合の
                    // 内部サンプルとして、各画素の最近サンプルまでの距離 P95 から tolerance を導出する。
                    // スポイト位置が明部でも暗部でも、トーン全域を覆うので取りこぼし/はみ出しを抑えられる。
                    var autoSamples = DeriveAutoTonalSamples(pixels, width, height, zone,
                        clusterMask, maskW, maskH);
                    bool derivedMulti = false;
                    if (autoSamples.Count > 0)
                    {
                        var samples = BuildSampleHSVs(zone.sampleColor, autoSamples);
                        if (TryDeriveChromaticToleranceMulti(pixels, width, height, zone, samples,
                                clusterMask, maskW, maskH, out float chromTolM))
                        {
                            result.autoSamples = autoSamples;
                            result.tolerance = chromTolM;
                            derivedMulti = true;
                        }
                    }
                    if (!derivedMulti && TryDeriveChromaticTolerance(pixels, width, height, zone,
                            clusterMask, maskW, maskH, out float chromTol))
                    {
                        // 単一サンプルへフォールバック(トーン抽出が不発/クラスタ過少)。
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
                autoSamples             = new List<Color>(),
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

        // ─────────────────── クラスタ距離の共通定数 / マスク判定 ───────────────────

        private const int DistBins = 600;          // 距離 [0,1.5] を 600 分割（分解能 0.0025）
        private const float DistMax = 1.5f;

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

        // ピクセル (x,y) がマスクで除外されているか。excluded==null なら常に false(マスク無し)。
        private static bool IsMaskExcluded(bool[] excluded, int maskW, int maskH, int x, int y, int w, int h)
        {
            if (excluded == null) return false;
            int mx = Mathf.Clamp(x * maskW / w, 0, maskW - 1);
            int my = Mathf.Clamp(y * maskH / h, 0, maskH - 1);
            return excluded[my * maskW + mx];
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
            bool[] excluded, int maskW, int maskH, out float tolerance)
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
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue; // マスク除外領域は対象外
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

        // ── foreign-hue 隣接パーツ検出(adjacent-part bleed の打ち切り) ──
        // near 窓(hd<NearHueDist=0.10)内に「彩度/明度は近いが hue が自パーツの自然な広がりを超えて
        // 外れる」画素が多数あるとき、それは色相が近い別パーツ(例: 青の隣に水色)。tolerance がその
        // 距離を越えると巻き込む。自パーツ core の hue 広がり(P90)から許容 hue ゲートを導出し、それを
        // 超える foreign 画素の量(core 比)が閾値を超えたら、foreign の最小距離(P10)の直下で tolerance を
        // 打ち切る。通常の床(ChromaTolMin=0.08)より低い ForeignLowFloor まで下げてよい(隣接別パーツの
        // 存在という積極的根拠があるため)。foreign が無いテクスチャでは一切発火せず現行挙動と同一。
        // 特定の色相・キャラ・ピクセルに依存せず、テクスチャ統計(core hue spread / foreign 比)のみから決まる。
        private const int   ForeignHueBins     = 200;    // [0,NearHueDist] の hue ヒストグラム分解能
        private const float CoreHueWindow      = 0.03f;  // core 抽出: サンプルからの hue 窓
        private const float CoreSatWindow      = 0.15f;  // core 抽出: 彩度窓
        private const float CoreValWindow      = 0.20f;  // core 抽出: 明度窓
        private const float ForeignGateK       = 2.5f;   // 許容 hue ゲート = K*coreSpread + floor
        private const float ForeignGateFloor   = 0.015f;
        private const float ForeignGateMin     = 0.03f;  // ゲート下限(締まった core でもこの幅は同パーツ扱い)
        private const float ForeignRatioThresh = 0.25f;  // foreign/core 比がこれ超で隣接別パーツと判定
        private const int   ForeignMinCount    = 30;     // foreign 画素数の下限(ノイズ無視)
        private const float ForeignLowFloor    = 0.04f;  // 打ち切り時に許す tolerance 下限(通常床 0.08 より低い)
        private const float ForeignCapEps      = 0.005f; // foreign 最小距離(P10)からのマージン

        private static bool TryDeriveChromaticTolerance(Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, out float tolerance)
        {
            tolerance = 0f;
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            float satDistW = zone.satDistWeight;
            float valueW = zone.valueWeight;

            int stride = (w <= 2048) ? 1 : 2;

            // ── 事前パス: 自パーツ core の hue 広がり(P90)から foreign 判定の hue ゲートを導出 ──
            // core = サンプルにごく近い(hue/彩度/明度の窓内)有彩画素。その hue 広がりの数倍までを
            // 「同パーツの色相」とみなし、それを超える画素を foreign(別パーツ)候補にする。
            var coreHueBins = new int[ForeignHueBins];
            int coreHueCount = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    Color.RGBToHSV(new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f),
                        out float pH, out float pS, out float pV);
                    if (pS < sS * ChromaClusterSatFrac) continue;
                    float hdc = Mathf.Abs(pH - sH); if (hdc > 0.5f) hdc = 1f - hdc;
                    if (hdc >= CoreHueWindow) continue;
                    if (Mathf.Abs(pS - sS) >= CoreSatWindow) continue;
                    if (Mathf.Abs(pV - sV) >= CoreValWindow) continue;
                    int cb = Mathf.Clamp((int)(hdc / NearHueDist * ForeignHueBins), 0, ForeignHueBins - 1);
                    coreHueBins[cb]++;
                    coreHueCount++;
                }
            }
            float coreSpread = 0.02f;
            if (coreHueCount >= MinNearSampleCount)
            {
                int ctgt = Mathf.CeilToInt(coreHueCount * 0.90f), ccum = 0;
                for (int i = 0; i < ForeignHueBins; i++)
                {
                    ccum += coreHueBins[i];
                    if (ccum >= ctgt) { coreSpread = (i + 1) / (float)ForeignHueBins * NearHueDist; break; }
                }
            }
            float effHueGate = Mathf.Clamp(ForeignGateK * coreSpread + ForeignGateFloor,
                                           ForeignGateMin, NearHueDist);

            // ── 主パス: 既存の near-sample クラスタ距離 P95 + foreign 距離分布/個数を同時に集計 ──
            var bins = new int[DistBins];
            int count = 0;
            var fgnBins = new int[DistBins];      // foreign 画素(別 hue)の距離分布
            int fgnCount = 0, coreCount = 0;       // 同パーツ core 個数(foreign 比の分母)
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue; // マスク除外領域は対象外
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
                    // core / foreign の振り分け(hue ゲートで分割)
                    if (hd < effHueGate) coreCount++;
                    else { fgnBins[bi]++; fgnCount++; }
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

            // ── foreign 打ち切り: 色相の近い隣接別パーツを検出したら、その手前で tolerance を止める ──
            // foreign 画素が core に対して十分多い(=隣に別パーツがある)ときのみ発火。foreign の最小側
            // 距離(P10)の直下まで下げ、別パーツの巻き込みを防ぐ。暗部の取りこぼしは本番のシャドウ免除が拾う。
            if (coreCount > 0 && fgnCount >= ForeignMinCount
                && fgnCount > coreCount * ForeignRatioThresh)
            {
                int ftgt = Mathf.CeilToInt(fgnCount * 0.10f), fcum = 0;
                float fgnP10 = DistMax;
                for (int i = 0; i < DistBins; i++)
                {
                    fcum += fgnBins[i];
                    if (fcum >= ftgt) { fgnP10 = (i + 1) / (float)DistBins * DistMax; break; }
                }
                tolerance = Mathf.Clamp(Mathf.Min(tolerance, fgnP10 - ForeignCapEps),
                                        ForeignLowFloor, ChromaTolMax);
            }
            return true;
        }

        // ─────────────────── マルチサンプル tolerance 導出 ───────────────────
        // 複数スポイト（主サンプル + extraSamples）から、各画素の「最も近いサンプルまでの距離」
        // の高パーセンタイルで tolerance を決める。和集合マッチング（ColorZone 側で全サンプルの
        // max 強度）と整合する。単一サンプルのとき（extraSamples 空）は呼ばれず、既存の単一経路が
        // そのまま走るので後方互換は保たれる。

        private struct SampleHSV
        {
            public float r, g, b;
            public float h, s, v;
            public float effHueGate; // foreign 判定の hue ゲート（このサンプルの core hue 広がりから導出）
        }

        private static SampleHSV[] BuildSampleHSVs(Color primary, List<Color> extras)
        {
            int extraN = extras?.Count ?? 0;
            var arr = new SampleHSV[1 + extraN];
            arr[0] = MakeSampleHSV(primary);
            for (int i = 0; i < extraN; i++) arr[i + 1] = MakeSampleHSV(extras[i]);
            return arr;
        }

        // ─────────────────── 自動トーン抽出（内部マルチサンプル） ───────────────────
        // スポイト1点(zone.sampleColor)から、同色相のパーツ全体のトーン分布を走査し、暗部・中間・
        // 明部の代表色を内部サンプルとして自動生成する。ユーザーの追加スポイト操作は一切不要。
        // 目的: スポイト位置（明るい所/暗い所/中間）に依存せず、トーン全域を和集合で覆い、
        // 「明部クリックで暗部を取りこぼす（または逆）」位置依存を解消する。
        //
        // 仕組み: まず主サンプル近傍クラスタ(near-window)の彩度分布から「パーツの彩度バンド」を
        // 推定し、その下限(satFloor)を求める。次に、同色相かつ satFloor 以上の画素を明度(V)で
        // ビン分けし、低/中/高パーセンタイルの代表色(そのVバンドの平均RGB)を取る。サンプル色や
        // 既存代表に近すぎる代表は重複として除く。無彩サンプルでは呼ばない(背景の白/黒と分離不能)。
        //
        // ★彩度バンドで限定する理由★: 同色相でも「彩度が著しく低い別マテリアル」(例: HAOLAN_Sneakers
        // の暗い紺ベロ navy, S≈0.4 / パーツ本体 S≈0.85+)が暗部に大量にあると、単純な明度パーセンタイルの
        // 暗部代表がその別パーツの色になって巻き込む。パーツの彩度バンド内に絞れば、percentile 前に
        // 別マテリアルを除外でき、暗部代表がパーツ本来の暗い影になる。色だけでパーツ分離できない領域での
        // 過検出を防ぐ安全弁(汎化のため特定色・座標に依存せず、テクスチャ統計のみから決める)。
        private const float AutoToneHueBand   = 0.06f;  // 同パーツとみなす hue 近傍
        private const float AutoToneSatFrac   = 0.35f;  // satFloor の下限 = sS*frac
        private const float AutoTonePartSatRelax = 0.85f; // satFloor = max(sS*frac, nearClusterSatP10*relax)
        private const float AutoToneDarkPct   = 0.12f;  // 暗部代表のパーセンタイル
        private const float AutoToneMidPct    = 0.50f;  // 中間代表のパーセンタイル
        private const float AutoToneLightPct  = 0.85f;  // 明部代表のパーセンタイル
        private const float AutoToneMinSep    = 0.10f;  // サンプル/既存代表とこの距離未満は重複として除外
        private const int   AutoToneMinPixels = 200;    // 同色相画素がこれ未満なら抽出しない
        private const int   AutoToneValueBins = 64;
        private const int   AutoToneSatBins   = 64;

        private static List<Color> DeriveAutoTonalSamples(Color32[] pixels, int w, int h,
            ColorZone zone, bool[] excluded, int maskW, int maskH)
        {
            var samples = new List<Color>();
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            if (sS < AchromaSampleSatMax) return samples; // 無彩は対象外

            int stride = (w <= 2048) ? 1 : 2;

            // ── パス1: near-window(主サンプルに似た=パーツ本体相当の画素)の彩度 P10 を求める ──
            // |dS|<NearSatDist かつ |dV|<NearValDist の窓に入る同色相画素の彩度分布。低彩度の別
            // マテリアルはこの窓(サンプル彩度の近傍)に入らないので、P10 はパーツ本体の彩度下限を表す。
            var satBins = new int[AutoToneSatBins];
            int nearCount = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    Color.RGBToHSV(new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f),
                        out float pH, out float pS, out float pV);
                    float hd0 = Mathf.Abs(pH - sH); if (hd0 > 0.5f) hd0 = 1f - hd0;
                    if (hd0 >= NearHueDist) continue;
                    if (Mathf.Abs(pS - sS) >= NearSatDist) continue;
                    if (Mathf.Abs(pV - sV) >= NearValDist) continue;
                    int sb = Mathf.Clamp((int)(pS * AutoToneSatBins), 0, AutoToneSatBins - 1);
                    satBins[sb]++; nearCount++;
                }
            }
            float nearSatP10 = 0f;
            if (nearCount >= MinNearSampleCount)
            {
                int tgt = Mathf.CeilToInt(nearCount * 0.10f), cum0 = 0;
                for (int i = 0; i < AutoToneSatBins; i++)
                {
                    cum0 += satBins[i];
                    if (cum0 >= tgt) { nearSatP10 = i / (float)AutoToneSatBins; break; }
                }
            }
            // パーツの彩度バンド下限: sS*frac と「near-cluster の彩度 P10*relax」の大きい方。
            // 高彩度均一パーツ(sneakers)では P10≈0.85 が効いて低彩度 navy を弾く。脱彩する素材では
            // P10 が低く出るので下限も下がり、自パーツの中程度の影は残る。
            float satFloor = Mathf.Max(sS * AutoToneSatFrac, nearSatP10 * AutoTonePartSatRelax);

            // ── パス2: 同色相 かつ 彩度バンド内 の画素を V でビン分け ──
            int VB = AutoToneValueBins;
            var cnt = new int[VB];
            var sumR = new float[VB];
            var sumG = new float[VB];
            var sumB = new float[VB];
            int total = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    Color.RGBToHSV(new Color(r, g, b, 1f), out float pH, out float pS, out float pV);
                    if (pS < satFloor) continue;                     // パーツの彩度バンド外(別マテリアル/脱彩)を除外
                    float hd = Mathf.Abs(pH - sH); if (hd > 0.5f) hd = 1f - hd;
                    if (hd >= AutoToneHueBand) continue;              // 別色相パーツを除外
                    int vb = Mathf.Clamp((int)(pV * VB), 0, VB - 1);
                    cnt[vb]++; sumR[vb] += r; sumG[vb] += g; sumB[vb] += b; total++;
                }
            }
            if (total < AutoToneMinPixels) return samples;

            // 指定パーセンタイルの V バンドの平均色を代表色として取る。
            Color RepAtPct(float pct)
            {
                int targetCount = Mathf.Clamp(Mathf.RoundToInt(pct * total), 1, total);
                int cum = 0, bin = VB - 1;
                for (int i = 0; i < VB; i++) { cum += cnt[i]; if (cum >= targetCount) { bin = i; break; } }
                // RepAtPct は累積が閾値を越えた bin を返すので cnt[bin] > 0 が保証される。
                float inv = 1f / cnt[bin];
                return new Color(sumR[bin] * inv, sumG[bin] * inv, sumB[bin] * inv, 1f);
            }
            void TryAdd(float pct)
            {
                Color rep = RepAtPct(pct);
                if (ColorDist(rep, zone.sampleColor) < AutoToneMinSep) return; // クリック色と重複
                foreach (var s in samples) if (ColorDist(rep, s) < AutoToneMinSep) return; // 既存代表と重複
                samples.Add(rep);
            }
            TryAdd(AutoToneDarkPct);
            TryAdd(AutoToneMidPct);
            TryAdd(AutoToneLightPct);
            return samples;
        }

        private static float ColorDist(Color a, Color b)
        {
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f;
        }

        private static SampleHSV MakeSampleHSV(Color c)
        {
            SampleHSV s;
            s.r = c.r; s.g = c.g; s.b = c.b;
            Color.RGBToHSV(c, out s.h, out s.s, out s.v);
            s.effHueGate = ForeignGateMin;
            return s;
        }

        // 有彩マルチサンプル: 各画素を「いずれかのサンプルの near 窓に入る画素」に限定し、
        // その画素から全サンプルへの最小マッチ距離を距離分布に積む。P95+margin を tolerance とする。
        // foreign 打ち切り: どのサンプルの core hue ゲートにも入らない near 画素（=隣接別パーツ）が
        // core に対し多いとき、その最小距離手前で tolerance を止める（単一経路と同じ思想を和集合化）。
        private static bool TryDeriveChromaticToleranceMulti(Color32[] pixels, int w, int h,
            ColorZone zone, SampleHSV[] samples, bool[] excluded, int maskW, int maskH, out float tolerance)
        {
            tolerance = 0f;
            float satDistW = zone.satDistWeight;
            float valueW = zone.valueWeight;
            int stride = (w <= 2048) ? 1 : 2;

            // ── 事前パス: 各サンプルの core hue 広がり(P90)→ effHueGate を導出 ──
            for (int si = 0; si < samples.Length; si++)
            {
                var sm = samples[si];
                if (sm.s < AchromaSampleSatMax) { samples[si].effHueGate = ForeignGateMin; continue; }
                var coreHueBins = new int[ForeignHueBins];
                int coreHueCount = 0;
                for (int y = 0; y < h; y += stride)
                {
                    int rowStart = y * w;
                    for (int x = 0; x < w; x += stride)
                    {
                        Color32 c = pixels[rowStart + x];
                        if (c.a < 128) continue;
                        if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                        Color.RGBToHSV(new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f),
                            out float pH, out float pS, out float pV);
                        if (pS < sm.s * ChromaClusterSatFrac) continue;
                        float hdc = Mathf.Abs(pH - sm.h); if (hdc > 0.5f) hdc = 1f - hdc;
                        if (hdc >= CoreHueWindow) continue;
                        if (Mathf.Abs(pS - sm.s) >= CoreSatWindow) continue;
                        if (Mathf.Abs(pV - sm.v) >= CoreValWindow) continue;
                        int cb = Mathf.Clamp((int)(hdc / NearHueDist * ForeignHueBins), 0, ForeignHueBins - 1);
                        coreHueBins[cb]++;
                        coreHueCount++;
                    }
                }
                float coreSpread = 0.02f;
                if (coreHueCount >= MinNearSampleCount)
                {
                    int ctgt = Mathf.CeilToInt(coreHueCount * 0.90f), ccum = 0;
                    for (int i = 0; i < ForeignHueBins; i++)
                    {
                        ccum += coreHueBins[i];
                        if (ccum >= ctgt) { coreSpread = (i + 1) / (float)ForeignHueBins * NearHueDist; break; }
                    }
                }
                samples[si].effHueGate = Mathf.Clamp(ForeignGateK * coreSpread + ForeignGateFloor,
                                                     ForeignGateMin, NearHueDist);
            }

            // ── 主パス: 最小距離分布 + foreign 集計 ──
            var bins = new int[DistBins];
            int count = 0;
            var fgnBins = new int[DistBins];
            int fgnCount = 0, coreCount = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    Color.RGBToHSV(new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f),
                        out float pH, out float pS, out float pV);

                    float minDist = float.MaxValue;
                    bool inAnyNear = false, isCore = false;
                    for (int si = 0; si < samples.Length; si++)
                    {
                        var sm = samples[si];
                        float hd = Mathf.Abs(pH - sm.h); if (hd > 0.5f) hd = 1f - hd;
                        if (hd >= NearHueDist) continue;
                        if (Mathf.Abs(pS - sm.s) >= NearSatDist) continue;
                        if (Mathf.Abs(pV - sm.v) >= NearValDist) continue;
                        if (pS < sm.s * ChromaClusterSatFrac) continue; // 無彩寄り画素を除外
                        inAnyNear = true;
                        float sd = Mathf.Abs(pS - sm.s);
                        float vd = Mathf.Abs(pV - sm.v);
                        float sRatio = (sm.s > 0.01f) ? Mathf.Clamp01(pS / sm.s) : 1f;
                        float d = hd + sd * satDistW + vd * valueW * (1f - sRatio);
                        if (d < minDist) minDist = d;
                        if (hd < sm.effHueGate) isCore = true; // どれかのサンプル core hue 内なら core
                    }
                    if (!inAnyNear) continue;
                    int bi = Mathf.Clamp((int)(minDist / DistMax * DistBins), 0, DistBins - 1);
                    bins[bi]++;
                    count++;
                    if (isCore) coreCount++;
                    else { fgnBins[bi]++; fgnCount++; }
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

            // foreign 打ち切り（単一経路と同形）
            if (coreCount > 0 && fgnCount >= ForeignMinCount
                && fgnCount > coreCount * ForeignRatioThresh)
            {
                int ftgt = Mathf.CeilToInt(fgnCount * 0.10f), fcum = 0;
                float fgnP10 = DistMax;
                for (int i = 0; i < DistBins; i++)
                {
                    fcum += fgnBins[i];
                    if (fcum >= ftgt) { fgnP10 = (i + 1) / (float)DistBins * DistMax; break; }
                }
                tolerance = Mathf.Clamp(Mathf.Min(tolerance, fgnP10 - ForeignCapEps),
                                        ForeignLowFloor, ChromaTolMax);
            }
            return true;
        }

        // 無彩マルチサンプル: 各画素から全サンプルへの最小 RGB 距離（グレーモード距離式）の P95。
        private static TuneResult MergeAnalyzed(TuneResult heuristic, AnalysisStats s)
        {
            float hSpread = HueSpreadFromHistogram(s.hBins, s.sH, s.nearSampleCount);
            float sP10 = PercentileBin(s.sBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP10 = PercentileBin(s.vBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP90 = PercentileBin(s.vBins, s.nearSampleCount, 0.90f) / (float)HistogramBins;
            float vSpread = Mathf.Max(0f, vP90 - vP10);

            // tolerance（フォールバック）: 色相の広がり + マージン。低彩度時は V の広がりで近似。
            // 通常は Analyze で TryDeriveChromaticTolerance/TryDeriveAchromaticTolerance(実距離分布)に
            // 上書きされる。ここはクラスタが過少(<MinNearSampleCount)で実距離導出が失敗したときの保険。
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

            // shadowForgivenessSatMin: シャドウ免除(暗部の同系色画素を救済する仕組み)の下限彩度。
            // 免除は「自パーツの暗部彩度まで」に限定する = darkestNearSampleS(自パーツの暗部の
            // 最小彩度)のすぐ下に下限を置く。darkestNearSampleS は定義上「自パーツ暗部の最小彩度」
            // なので、その 0.95 倍を下限にすれば自パーツの暗部は全て免除され(recall 不変)、それより
            // 彩度の低い別パーツの暗部だけが免除から外れる(暗部の巻き込み=はみ出しを抑える)。
            // 旧式(darkestNearSampleS*0.5, 上限0.20)は、暗部が高彩度を保つパーツ(例: スニーカー青,
            // 暗部彩度≈0.85)でも下限が 0.20 に張り付き、彩度 0.2〜0.85 の別パーツ暗部を巻き込んでいた
            // (実測: 下限を 0.20→0.80 にすると precision 0.980→0.994 / IoU 0.951→0.964 で recall 不変)。
            // 暗部が脱彩する素材は darkestNearSampleS が低く出るので下限も自動的に下がり、免除が効いて
            // recall を保つ。暗部画素が無いパーツ(darkSeen=false)は既定 0.05 のまま。
            float shadowForgivenessSatMin = DefaultShadowForgivenessSatMin;
            if (s.darkSeen && s.darkestNearSampleS > 0.10f)
            {
                shadowForgivenessSatMin = Mathf.Clamp(s.darkestNearSampleS * 0.95f, 0.05f, 0.90f);
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
                autoSamples             = new List<Color>(),
            };
        }

        // ─────────────────── Globals 判定 ───────────────────

        private static void DecideGlobals(int texWidth, int texHeight, IrocaSessionState session, ref TuneResult result)
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

        private static void CollectOverwrittenLabels(ColorZone zone, IrocaSessionState session, ref TuneResult result)
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
