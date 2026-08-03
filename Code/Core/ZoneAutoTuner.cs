// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// テクスチャと ColorZone の (sampleColor, targetColor) から、
    /// 許容範囲・彩度制限などのパラメータを自動的に算出する純粋ロジック層。
    /// UI からは IrocaWindow.RunAutoTune() 経由で呼ばれ、結果は TuneResult として返す。
    /// 純粋計算のため、ピクセル配列さえあればバックグラウンドスレッドからも呼べる。
    /// </summary>
    internal static partial class ZoneAutoTuner
    {
        // ── デフォルト値（単一ソース: ColorZone / IrocaSessionState のフィールド初期値） ──
        // 旧実装は const をここへ複製し「同期は手作業」だったが、片側だけ変えると
        // IsZoneBasicsAtDefault / CollectOverwrittenLabels / Globals 適用可否の判定が静かに壊れる
        // (2026-07/08 監査で指摘)。既定値インスタンスから読むことで構造的に同期する。
        // 注意: 読み取り専用。変異させないこと(BG スレッドからも参照される)。
        private static readonly ColorZone s_zoneDefaults = new ColorZone();
        private static readonly IrocaSessionState s_sessionDefaults = new IrocaSessionState();
        private static float DefaultTolerance               => s_zoneDefaults.tolerance;
        private static float DefaultSaturationStrictness    => s_zoneDefaults.saturationStrictness;
        private static float DefaultSaturationGuard         => s_zoneDefaults.saturationGuard;
        private static float DefaultChromaThreshold         => s_zoneDefaults.chromaThreshold;
        private static bool  DefaultHighlightRecovery       => s_zoneDefaults.highlightRecovery;
        private static float DefaultValueBlend              => s_zoneDefaults.valueBlend;
        private static float DefaultEdgeSoftness            => s_zoneDefaults.edgeSoftness;
        private static float DefaultShadowDesaturation      => s_zoneDefaults.shadowDesaturation;
        private static float DefaultShadowForgivenessSatMin => s_zoneDefaults.shadowForgivenessSatMin;
        private static int   DefaultAntiAliasCleanup        => s_sessionDefaults.antiAliasCleanup;
        private static bool  DefaultUseDecontamination      => s_sessionDefaults.useDecontamination;

        // 彩度ガード自動導出パラメータ
        //
        // 感度解析の結論:
        //   - 源色がほぼ全域で高彩度の素材(sS≈1.0): guard ON で過検出が大幅に減り、選択精度が改善。
        //   - 影で彩度が落ちる素材(暗部=低彩度が正常): guard ON で悪化。低彩度画素を弾くと
        //     正常なシェーディングまで削ってしまうため。
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
            bool[] excluded = null, int maskW = 0, int maskH = 0, CancellationToken ct = default)
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
            return Analyze(pixels, w, h, zone, session, excluded, maskW, maskH, ct);
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
            bool[] excluded = null, int maskW = 0, int maskH = 0, CancellationToken ct = default)
        {
            var result = BuildHeuristicDefault(zone);

            bool canAnalyze = pixels != null
                && width >= MinTextureDim && height >= MinTextureDim
                && pixels.Length >= width * height;
            if (canAnalyze)
            {
                // 解析は最大 15 回前後の全画面走査で数百 ms かかる。各ステップ間でキャンセルを確認し、
                // 新しい自動調整が来たら旧解析をゾンビ実行させない(CPU 2 倍/進捗バー飛びを防ぐ)。
                // 各ステップは O(len) なので最悪でも 1 ステップ分で停止する。
                ct.ThrowIfCancellationRequested();
                // マスクがある場合は含有(非除外)領域にクラスタを限定する(色が同じ別パーツを除外)。
                // ヒストグラム解析(TryAnalyzePixels)も含め全経路で同じ含有領域だけを見る。マスクで
                // 除外したパーツの画素が shadowForgivenessSatMin / saturationStrictness /
                // highlightRecovery 初期値へ混入するのを防ぐ(tolerance 導出側は元から尊重していた)。
                bool useMask = HasUsableMask(excluded, maskW, maskH);
                bool[] clusterMask = useMask ? excluded : null;

                if (TryAnalyzePixels(pixels, width, height, zone, clusterMask, maskW, maskH, out var analyzed))
                    result = MergeAnalyzed(result, analyzed);

                // tolerance は常に「サンプル近傍クラスタの実マッチ距離分布」から導出する。
                // 無彩(グレーモード=純 RGB 距離)と有彩(HSV マッチ)で距離式が違うため経路を分けるが、
                // いずれも MergeAnalyzed の hSpread/vSpread 由来ヒューリスティック(実距離と切り離され
                // 過大選択を招く)を実距離分布へ置き換える。
                //
                // 旧「マスク認識経路(含有領域全画素の P99.9, 上限0.40)」は、ゆるい/残存マスクや
                // 明暗の広いパーツ(例: 明るいサンプルの髪)で上限 0.40 に張り付いていた(ユーザー報告)
                // ため廃止。パーツ内の暗部・薄い装飾は本番のシャドウ免除/ハイライト復元が tolerance
                // とは独立に拾うので、tolerance を膨らませない。
                Color.RGBToHSV(zone.sampleColor, out _, out float sampleS, out _);

                ct.ThrowIfCancellationRequested();
                // foreign 打ち切り(隣接同色相パーツの検出)が効いた場合は覚えておき、
                // 明部ツヤ救済(VerifyBrightSheenRecall)の拡張を封印する(打ち切りと拡張が相殺し
                // 隣接パーツを再び巻き込むのを防ぐ)。
                bool foreignCapped = false;
                // トーン連結域の V 上端 bin(明部ツヤ救済の上限に使う)。-1=未確定。
                int vConnHiBin = -1;
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
                        clusterMask, maskW, maskH, out _, out vConnHiBin);
                    bool derivedMulti = false;
                    if (autoSamples.Count > 0)
                    {
                        var samples = BuildSampleHSVs(zone.sampleColor, autoSamples);
                        if (TryDeriveChromaticToleranceMulti(pixels, width, height, zone, samples,
                                clusterMask, maskW, maskH, out float chromTolM, out bool fCapM))
                        {
                            result.autoSamples = autoSamples;
                            result.tolerance = chromTolM;
                            foreignCapped = fCapM;
                            derivedMulti = true;
                        }
                    }
                    if (!derivedMulti && TryDeriveChromaticTolerance(pixels, width, height, zone,
                            clusterMask, maskW, maskH, out float chromTol, out bool fCap))
                    {
                        // 単一サンプルへフォールバック(トーン抽出が不発/クラスタ過少)。
                        result.tolerance = chromTol;
                        foreignCapped = fCap;
                    }
                }

                // ── 閉ループ検証: 導出パラメータを実マッチャーに通し、有害な設定を安全側へ倒す ──
                ct.ThrowIfCancellationRequested();
                bool hlRecBeforeVerify = result.highlightRecovery;
                if (result.highlightRecovery)
                    VerifyHighlightRecoveryGrowth(pixels, width, height, zone,
                        excluded, maskW, maskH, ref result);
                // 成長テストが highlightRecovery を落とした=「明るい同色相の別素材」が既に検出された
                // 状況なので、同じ方向へ広げる明部ツヤ救済も封印する。
                bool hlRecVetoed = hlRecBeforeVerify && !result.highlightRecovery;
                ct.ThrowIfCancellationRequested();
                if (sampleS >= AchromaSampleSatMax)
                {
                    float tolBeforeOvershoot = result.tolerance;
                    VerifyBrightForgivenessOvershoot(pixels, width, height, zone,
                        excluded, maskW, maskH, ref result);
                    // 免除過剰で tolerance を縮めた直後に拡張するのは矛盾するのでスキップする。
                    bool overshootShrunk = result.tolerance < tolBeforeOvershoot;

                    ct.ThrowIfCancellationRequested();
                    // vConnHiBin < 0(トーン構造を確定できなかった)ときは拡張しない(構造未知のまま
                    // 広げるのは危険。素直なパーツならヒストグラムは常に作れる)。
                    if (!foreignCapped && !overshootShrunk && !hlRecVetoed && vConnHiBin >= 0)
                        VerifyBrightSheenRecall(pixels, width, height, zone,
                            excluded, maskW, maskH, vConnHiBin, ref result);
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

        private static bool TryAnalyzePixels(Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, out AnalysisStats stats)
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
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue; // マスク除外領域は対象外

                    float r = c32.r / 255f;
                    float g = c32.g / 255f;
                    float b = c32.b / 255f;
                    Color.RGBToHSV(new Color(r, g, b, 1f), out float pH, out float pS, out float pV);

                    float hDist = HueDistance(pH, stats.sH);

                    // ハイライト復元候補（サンプル色と同系のハイライト領域）。
                    // 条件は ColorZone のハイライト判定・Verify.cs:GrowHighlightBand ミラーと同じ定数を共有する。
                    if (pV > ColorZone.HighlightValueMin && pS < ColorZone.HighlightSaturationMax
                        && hDist < ColorZone.ForgivenessHueGate)
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
            // 旧式(darkestNearSampleS*0.5, 上限0.20)は、暗部でも彩度がほとんど落ちないパーツ(陰影が
            // ほぼ V だけで表現され、暗部でも彩度が 0.85 前後に留まる濃色の布地など)でも下限が 0.20 に
            // 張り付き、彩度 0.2〜0.85 の別パーツ暗部を巻き込んでいた(実測例: この種のパーツで下限を
            // 0.20→0.80 にすると precision 0.980→0.994、recall は不変)。
            // 暗部が脱彩する素材は darkestNearSampleS が低く出るので下限も自動的に下がり、免除が効いて
            // recall を保つ。暗部画素が無いパーツ(darkSeen=false)は既定 0.05 のまま。
            float shadowForgivenessSatMin = DefaultShadowForgivenessSatMin;
            if (s.darkSeen && s.darkestNearSampleS > 0.10f)
            {
                shadowForgivenessSatMin = Mathf.Clamp(s.darkestNearSampleS * 0.95f, 0.05f, 0.90f);
            }

            // saturationGuard: 源色 S が極端に高い (>=0.95) ときだけ自動提案。
            // sS=0.95 → Min(0.5)、sS=1.0 → Max(0.8) で線形補間。
            // sS<0.95 では 0 のまま（感度解析で、影で彩度が落ちる素材は
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
