// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    // ZoneAutoTuner: 証拠マスク(AI マスク提案のセグメント)を教師にした導出。
    //
    // 【解決する問題】従来の導出は「クリック 1 texel + 対称な近傍窓(|ΔS|<0.20, |ΔV|<0.30)」を
    // 証拠にするため、V が上がり S が下がる非対称な逸脱であるハイライト/ツヤは統計に入る前に
    // 除外され、原理的に届かない(ZoneAutoTuner.Verify.cs の明部ツヤ救済コメント参照)。実測でも
    // 45 ケース中 27 で「どの tolerance でも 適合率 0.98 ∧ 再現率 0.95 ∧ 残存島 0.5% を同時に
    // 満たせない」(dev_safe operating_curve 2026-08-21)。
    //
    // 【アプローチ】AI マスク提案のセグメントは「クリックした島をツヤ込みで完全に取る」
    // (製品 E2E 実測: precision 中央値 0.99)。従来導出が「クリック色の近傍窓」という当て推量で
    // 作っていた母集団を、このセグメントに置き換える。証拠が変えるのは次の 3 つだけで、
    // tolerance・彩度厳密度などの他パラメータは従来と同じ全画面統計から導く:
    //   1. 地色 = スポイト位置の正規化を「母集団 = セグメント」で行う
    //      (近白のツヤを踏んだクリックでも地色に落ちる。実測: tops の明部クリックで IoU 0.15→0.82)
    //   2. 従来のトーン代表色でセグメント芯を覆えないとき、未被覆部の代表色を足す
    //      (明度レンジの広い素材の明端まで届く。実測: gold の残存島 2.4〜4.0% → 0)
    //   3. ハイライト補助(highlightRecovery)の発火 = 従来判定 OR セグメント内のツヤ署名の実在
    // 事前実測(dev_safe/_spike/segment_color_coverage.json, 2026-08-25): 従来導出の取りこぼし
    // (見逃し+残存島)の加重 86.5% がセグメント内またはセグメント実在色の中にある。
    //
    // 【試して捨てたもの(dev_safe/_spike/evidence_autotune_*.json)】
    //   - ツヤ色をサンプル化: 淡い色は汎用的で別の明色素材を色だけで巻き込む(precision 0.99→0.86)。
    //     回収は空間錨つきの本番経路(highlightRecovery / GrowHighlightBand)に任せる。
    //   - 「セグメント芯を実マッチャーで覆う tolerance 下限」: 彩度ゲート等で弾かれる画素まで
    //     tolerance で覆おうとして従来の導出値を押し上げ、別素材へ溢れた(skirt IoU −0.27、eye −0.07)。
    //     勝ちに寄与したケースは無かった。
    //   - 副パラメータ(彩度厳密度・シャドウ免除下限等)やトーン代表色そのものを証拠限定の統計から
    //     導く: 従来の値との僅かな差が非線形なゲート群を通って別素材の巻き込みになる被写体があった
    //     (eye IoU −0.07)。従来の導出を土台にし、証拠は「補う」だけにする。
    //
    // 【セグメント内でも残す従来のフィルタ】セグメントは「同じ物体」を保証するが「同じ素材の
    // 同じ陰影ランプ」までは保証しない(実測: ゴーグル本体+金属フレーム、黒コート+囲まれた
    // 白ビスチェ、白衣装+金トリム、暗いスカートの低彩度暗部)。代表色をそこから取ると
    // グローバルに別素材を巻き込む(実測 IoU −0.14〜−0.36)。従来導出がシェーディングの物理として
    // 持っているフィルタ — V 連結域(明度の空の谷の向こうは別素材)・相対彩度床(低彩度の暗部は
    // 全素材共通の色なのでシャドウ免除の担当)・色相帯 — は証拠内でも同じ理由で適用する。
    // それでも芯の 2 割以上がフィルタを通らないセグメントは複数素材と判断し、従来導出へ戻す。
    //
    // 証拠が使えないとき(null/空/過少/汚染)は従来の Analyze へフォールバックするので、
    // AI モデル未ダウンロード環境の挙動は変わらない。
    internal static partial class ZoneAutoTuner
    {
        // ── 証拠の前処理 ──
        // セグメント境界の AA/にじみ画素が統計を汚さないよう、統計は収縮した芯で取る。
        // 収縮量は残存島計測(dev_safe measure_residual ERODE_R)と同じ「境界 2px は視覚的に無害」の線。
        private const int EvErodeR = 2;
        // 収縮後にこれ未満しか残らない証拠は芯が細すぎる(細いストラップ等)ので、収縮なしで使う。
        private const int EvMinErodedPixels = 256;
        // セグメント芯のうち「地色と同じ素材」(ドメイン ∪ ツヤ署名)の割合がこれ未満なら、
        // セグメントは複数素材にまたがっており単一ゾーンの教師にできない → 従来導出へ。
        // 実測: 金属フレームを巻き込んだゴーグルのセグメントで 0.63、他の 13 被写体は 0.91 以上。
        private const float EvMinMaterialFrac = 0.80f;

        // ── 証拠による補完(未被覆トーンの追加) ──
        // 導出パラメータで証拠ドメインの被覆率がこれ未満なら、未被覆部の代表色をサンプルへ足す。
        // 足した色はグローバルに効くので、明らかな取りこぼし(2% 以上)があるときだけ発火させる
        // (実測: 0.2% の取り残しを埋めるために足した色が別素材と衝突し skirt precision 0.70→0.63。
        // 一方 gold トリムの明端は 8% 未被覆で、足せば残存島 2.4% → 0)。
        private const float EvCoverTarget = 0.98f;
        // 1 色足しても被覆がこれしか増えないなら、残りは tolerance/ゲートで届かない画素なので止める。
        private const float EvCoverGainMin = 0.002f;
        private const int   EvMaxExtraSamples = 3;
        // 未被覆代表の V 帯に最低限必要な画素数(ドメイン比)。絶対数 64 では小さなトリム島
        // (芯 1.3 万点)の 8% の取り残しが 64 bin に散って拾えなかった。
        private const float EvUncoveredBinMinFrac = 0.002f;
        private const int   EvUncoveredBinMinCount = 8;
        // 追加サンプルの溢れ判定。追加で増える全画面の選択量は、クリック島で新たに覆えた量 ×
        // パーツ多重度(全画面の基礎選択 / クリック島の基礎選択 = 同素材の島が幾つ分あるか)で
        // 説明できるはず。それを K 倍超えるなら別素材へ溢れている(実測: gold トリムは多重度 ≈4 で
        // 比 2〜5、暗いスカートの星柄色は多重度 ≈1 で比 ≈50 → precision 0.70→0.63)。
        private const float EvBleedK = 3f;
        // 追加候補が既存サンプルと実質同色(RGB 距離)なら足さない。マッチャーは彩度差を見るので
        // 従来の AutoToneMinSep(0.10)より細かく取る(実測: 脱彩した明端が既存明部と 0.08 で棄却され、
        // gold の残存島が残った)。
        private const float EvDupSep = 0.03f;

        // ── 脱彩ツヤの署名 ──
        // ツヤ = 地色より彩度が明確に落ち(S < 地色 S × frac)、明度が上がった(V > 地色 V + margin)
        // 画素。セグメント芯にこれが一定量あれば highlightRecovery(空間錨つきの回収経路)を
        // 発火させる。代表色の統計からは外す。
        private const float EvSheenSatFrac   = 0.60f;
        private const float EvSheenValMargin = 0.05f;
        private const int   EvSheenMinCount  = 50;
        // セグメント芯に対する最小面積比。芯は収縮済み(境界 AA を含まない)なので、
        // 全画面統計の highlightRecovery 閾値(近傍数の 2%)のような大きな相対床は不要。
        // 1 島あたりのツヤは芯の 0.1〜数% しかない(実測: sneakers 1 島でツヤが芯の 0.5% 未満)。
        private const float EvSheenMinFrac   = 0.001f;

        /// <summary>
        /// 証拠マスク(true=このゾーンの素材そのもの。AI マスク提案のセグメント)を教師にして
        /// パラメータを導出する。証拠が使えないときは従来の <see cref="Analyze(Color32[],int,int,ColorZone,IrocaSessionState,bool[],int,int,CancellationToken,System.Action{float})"/>
        /// へフォールバックする。evidence の寸法はテクスチャと同一想定(異寸は最近傍で対応)。
        /// </summary>
        public static TuneResult AnalyzeWithEvidence(Color32[] pixels, int width, int height,
            ColorZone zone, IrocaSessionState session,
            bool[] evidence, int evW, int evH,
            bool[] excluded = null, int maskW = 0, int maskH = 0, CancellationToken ct = default,
            System.Action<float> report = null)
        {
            bool canAnalyze = pixels != null
                && width >= MinTextureDim && height >= MinTextureDim
                && pixels.Length >= width * height
                && evidence != null && evW > 0 && evH > 0 && evidence.Length >= evW * evH;
            if (!canAnalyze)
                return Analyze(pixels, width, height, zone, session, excluded, maskW, maskH, ct, report);

            void Progress(float v) { report?.Invoke(v); }

            var result = BuildHeuristicDefault(zone);
            var hsv = BuildHsvGrid(pixels, width, height, ct);
            Progress(0.25f);

            // ── 証拠芯(収縮後)の格子リスト ──
            ct.ThrowIfCancellationRequested();
            var core = ErodeMask(evidence, evW, evH, EvErodeR, ct);
            var coreList = CollectGridIndices(pixels, width, height, hsv, core, evW, evH,
                excluded, maskW, maskH);
            if (coreList.Count < EvMinErodedPixels)
            {
                // 芯が細すぎる(細ベルト等) → 収縮なしの生セグメントで再試行
                coreList = CollectGridIndices(pixels, width, height, hsv, evidence, evW, evH,
                    excluded, maskW, maskH);
            }
            if (coreList.Count < MinNearSampleCount)
                return Analyze(pixels, width, height, zone, session, excluded, maskW, maskH, ct, report);
            Progress(0.40f);

            // 「証拠の中だけ」を見るための除外マスク(非証拠画素 ∪ 実際の除外)。
            var notEvidence = new bool[evW * evH];
            for (int i = 0; i < notEvidence.Length; i++) notEvidence[i] = !evidence[i];
            if (excluded != null && maskW == evW && maskH == evH)
                for (int i = 0; i < notEvidence.Length; i++) notEvidence[i] |= excluded[i];

            // ── 1. 地色 = スポイト位置の正規化を「母集団 = セグメント」で行う ──
            // 面積最頻の色をそのまま地色にすると、面積最大の暗部やベタ塗りに落ちる(実測: ゴーグル
            // 56→30、虹彩 (50,53,129)→(32,35,71))。暗い地色は「地色より明るい同色相」への明部免除の
            // 範囲を広げ、グローバルに別素材を巻き込む(実測 IoU −0.06〜−0.11)。従来の正規化は
            // この罠(ベタ塗り端ゲート・クリックが地色帯なら動かさない)を実測で塞いであるので、
            // 母集団をセグメントに限定してそのまま使う。正規化しない(=既に地色)ならクリック色。
            ct.ThrowIfCancellationRequested();
            //
            // 外れ値ゲート(クリックの (S,V) 帯が最頻帯の 1/10 以上あれば動かさない)は、クリックが
            // トーン連結域の端に居るときだけ外す(edgeBypass。判定と実測は Normalize.cs の
            // NormEdgeFrac を参照)。母集団がセグメント(=クリックしたパーツそのもの)なら最頻 (S,V)
            // bin はそのパーツの地色で、端を踏んだクリックを寄せる相手として信頼できる。
            Color rep = zone.sampleColor;
            if (TryNormalizeSample(pixels, width, height, zone, notEvidence, evW, evH, hsv,
                    out Color normalized, out float clickT, edgeBypass: true))
                rep = normalized;
            Color.RGBToHSV(rep, out float repH, out float repS, out float repV);
            var aZone = zone.Clone();
            aZone.sampleColor = rep;

            // ── 証拠ドメイン = 芯のうち地色と同じ陰影ランプ(汚染判定と後段の被覆確認に使う) ──
            var domain = BuildEvidenceDomain(hsv, coreList, repH, repS, repV,
                out int sheenCount, out int hlCandidates);
            float materialFrac = (domain.Count + sheenCount) / (float)coreList.Count;
            if (materialFrac < EvMinMaterialFrac)
            {
                // 汚染セグメント(複数素材)は教師にしない。従来導出へ(証拠は使わない)。
                var fallback = Analyze(pixels, width, height, zone, session, excluded, maskW, maskH,
                    ct, report);
                fallback.evidenceDiag = $"fallback contaminated materialFrac={materialFrac:F3}"
                    + $" core={coreList.Count} domain={domain.Count} sheen={sheenCount}";
                return fallback;
            }
            Progress(0.50f);

            // ── 以下、Analyze と同一の導出(母集団はクリック近傍窓)。違いは地色(aZone)だけ ──
            bool useMask = HasUsableMask(excluded, maskW, maskH);
            bool[] clusterMask = useMask ? excluded : null;
            if (TryAnalyzePixels(pixels, width, height, aZone, clusterMask, maskW, maskH, hsv, out var analyzed))
                result = MergeAnalyzed(result, analyzed);

            // ── 3. highlightRecovery = 従来判定 OR 証拠署名 ──
            // MergeAnalyzed の閾値(近傍数の 2%)は「境界 AA をスペキュラと誤認しない」ための
            // 全画面統計向けの床で、1 島のツヤ(芯の 0.1〜数%)がグローバルには埋もれて OFF に
            // なることがある。芯の中にハイライト署名(明部・低彩度・同色相)が実在するなら、それは
            // 空間的に確認された本物の証拠なので ON にする。従来の ON は OFF にしない(証拠は
            // 見落としを補うためのもの)。有害な場合は下の成長テストが従来どおり落とす。
            int hlFloor = Mathf.Max(EvSheenMinCount,
                Mathf.RoundToInt(coreList.Count * EvSheenMinFrac));
            if (repS >= AchromaSampleSatMax && (sheenCount >= hlFloor || hlCandidates >= hlFloor))
                result.highlightRecovery = true;
            Progress(0.58f);

            // ── tolerance とトーン代表色: Analyze と同一の経路 ──
            ct.ThrowIfCancellationRequested();
            bool foreignCapped = false;
            int vConnHiBin = -1;
            bool chromatic = repS >= AchromaSampleSatMax;
            if (!chromatic)
            {
                if (TryDeriveAchromaticTolerance(pixels, width, height, aZone,
                        clusterMask, maskW, maskH, hsv, out float achTol))
                {
                    result.tolerance = achTol;
                    result.highlightRecovery = false; // 無彩: グレーモードのハイライト経路は危険
                }
            }
            else
            {
                var tones = DeriveAutoTonalSamples(pixels, width, height, aZone,
                    clusterMask, maskW, maskH, hsv, out _, out vConnHiBin);
                bool derivedMulti = false;
                if (tones.Count > 0)
                {
                    var samples = BuildSampleHSVs(aZone.sampleColor, tones);
                    if (TryDeriveChromaticToleranceMulti(pixels, width, height, aZone, samples,
                            clusterMask, maskW, maskH, hsv, out float tolM, out bool fCapM))
                    {
                        result.autoSamples = tones;
                        result.tolerance = tolM;
                        foreignCapped = fCapM;
                        derivedMulti = true;
                    }
                }
                if (!derivedMulti && TryDeriveChromaticTolerance(pixels, width, height, aZone,
                        clusterMask, maskW, maskH, hsv, out float tol1, out bool fCap1))
                {
                    result.tolerance = tol1;
                    foreignCapped = fCap1;
                }
            }
            Progress(0.68f);

            // ── 2. 証拠による補完: 導出パラメータでセグメント芯(ドメイン)を覆えないなら、
            //       未被覆部の代表色をサンプルへ足す(tolerance は上げない) ──
            // 明度レンジの広い素材(実測: gold トリム)はクリック近傍窓のトーン抽出が明端の脱彩した
            // 段を取りこぼし、tolerance でも届かない(残存島 2.4〜4.0%)。セグメント芯の実色を
            // 足せば tolerance を上げずに覆える(実測: 島 0%)。tolerance の方を上げて覆う案は
            // 隣接素材へ溢れたので採らない(skirt IoU −0.27)。
            // 無彩地色では行わない(従来のトーン抽出と同じ理由: 背景の白/黒と色で分離できない。
            // 実測: クリームの上衣で純白が足され、白パディングを塗って IoU 0.83→0.34)。
            int added = 0;
            float cov = chromatic
                ? DomainCoverage(aZone, result, hsv, pixels, width, height, domain) : 1f;
            int selGlobal = cov < EvCoverTarget
                ? GlobalSelected(aZone, result, hsv, pixels, width, height, excluded, maskW, maskH) : 0;
            while (cov < EvCoverTarget && added < EvMaxExtraSamples)
            {
                ct.ThrowIfCancellationRequested();
                var sim = BuildSimZone(aZone, result);
                sim.highlightRecovery = false;
                var existing = result.autoSamples ?? new List<Color>();
                if (!TryUncoveredRepresentative(sim, hsv, pixels, width, height, domain,
                        rep, existing, out Color extra))
                    break;
                var trial = new List<Color>(existing) { extra };
                float tolPrev = result.tolerance;
                var samplesPrev = result.autoSamples;
                bool fCapPrev = foreignCapped;
                result.autoSamples = trial;
                if (TryDeriveChromaticToleranceMulti(pixels, width, height, aZone,
                        BuildSampleHSVs(aZone.sampleColor, trial), clusterMask, maskW, maskH, hsv,
                        out float tolT, out bool fCapT))
                {
                    result.tolerance = tolT;
                    foreignCapped = fCapT;
                }
                float covN = DomainCoverage(aZone, result, hsv, pixels, width, height, domain);
                int selGlobalN = GlobalSelected(aZone, result, hsv, pixels, width, height,
                    excluded, maskW, maskH);
                // 溢れ判定: 全画面で増えた選択量 vs クリック島で新たに覆えた量 × パーツ多重度
                float gainDomain = (covN - cov) * domain.Count;
                float multiplicity = Mathf.Max(1f, selGlobal / Mathf.Max(1f, cov * domain.Count));
                bool bleeds = (selGlobalN - selGlobal) > EvBleedK * multiplicity * gainDomain;
                if (covN - cov < EvCoverGainMin || bleeds)
                {
                    // 足しても覆えない(ゲート棄却/異物)か、別素材へ溢れる → 元に戻して打ち切り
                    result.autoSamples = samplesPrev;
                    result.tolerance = tolPrev;
                    foreignCapped = fCapPrev;
                    break;
                }
                cov = covN;
                selGlobal = selGlobalN;
                added++;
            }
            Progress(0.78f);

            // ── 閉ループ検証(従来と同じ全画面視点・同じ順序・同じ封印条件) ──
            // 明部ツヤ救済(VerifyBrightSheenRecall)も従来どおり残す: 「やや脱彩・やや明るい」ツヤ
            // (実測: 虹彩の反射 S≈0.3、V≈0.75)はハイライト補助の条件(V>0.80・彩度上限)にも
            // 証拠ドメイン(ツヤ署名は除外)にも入らず、この救済だけが拾う。
            ct.ThrowIfCancellationRequested();
            bool hlRecBeforeVerify = result.highlightRecovery;
            if (result.highlightRecovery)
                VerifyHighlightRecoveryGrowth(pixels, width, height, aZone,
                    excluded, maskW, maskH, hsv, ref result);
            bool hlRecVetoed = hlRecBeforeVerify && !result.highlightRecovery;
            Progress(0.88f);
            ct.ThrowIfCancellationRequested();
            if (repS >= AchromaSampleSatMax)
            {
                float tolBeforeOvershoot = result.tolerance;
                VerifyBrightForgivenessOvershoot(pixels, width, height, aZone,
                    excluded, maskW, maskH, hsv, ref result);
                bool overshootShrunk = result.tolerance < tolBeforeOvershoot;
                ct.ThrowIfCancellationRequested();
                if (!foreignCapped && !overshootShrunk && !hlRecVetoed && vConnHiBin >= 0)
                    VerifyBrightSheenRecall(pixels, width, height, aZone,
                        excluded, maskW, maskH, hsv, vConnHiBin, ref result);
            }

            result.normalizedSample = rep;
            result.hasNormalizedSample = ColorDist(rep, zone.sampleColor) >= NormMinShift;
            result.evidenceDiag =
                $"core={coreList.Count} domain={domain.Count} sheen={sheenCount} hlCand={hlCandidates}"
                + $" clickT={clickT:F2}"
                + $" hlRec={result.highlightRecovery} samples={result.autoSamples?.Count ?? 0}"
                + $" added={added} cov={cov:F4} tol={result.tolerance:F4}";
            Progress(0.98f);

            DecideGlobals(width, height, session, ref result);
            CollectOverwrittenLabels(zone, session, ref result);
            return result;
        }

        // ─────────────────── 証拠画素の収集/ドメイン/代表色 ───────────────────

        // HSV 格子(stride 済み)上の証拠画素 index。gi = HsvGrid index。alpha < 128 と除外マスクは対象外。
        private struct GridPt { public int x, y, gi; }

        private static List<GridPt> CollectGridIndices(Color32[] pixels, int w, int h, HsvGrid hsv,
            bool[] mask, int mW, int mH, bool[] excluded, int maskW, int maskH)
        {
            var list = new List<GridPt>();
            int stride = hsv.stride;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    if (pixels[rowStart + x].a < 128) continue;
                    int mx = Mathf.Clamp(x * mW / w, 0, mW - 1);
                    int my = Mathf.Clamp(y * mH / h, 0, mH - 1);
                    if (!mask[my * mW + mx]) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    list.Add(new GridPt { x = x, y = y, gi = grow + gx });
                }
            }
            return list;
        }

        // 証拠ドメイン = 芯のうち「地色と同じ素材の同じ陰影ランプ」とみなせる画素。
        // 代表色(トーンサンプル)はこのドメインから取る。落とすもの:
        //   - ツヤ署名(脱彩+増明): highlightRecovery の担当(sheenCount で発火判定)
        //   - 相対彩度床未満(有彩地色)/彩度上限超(無彩地色): 従来のクラスタ定義と同じ。
        //     低彩度の暗部は全素材が共有する色なので代表色にすると別素材へ滲む(実測: 暗い
        //     スカートの暗紺代表が黒コートを巻き込み precision 0.83→0.47)。深い影はシャドウ免除が拾う
        //   - 色相帯の外(有彩地色): 同物体でも別素材(実測: 白衣装セグメント内の金トリム)
        //   - V 連結域の外: 明度の空の谷の向こうは同物体でも別素材(実測: ゴーグル本体に対する
        //     金属フレーム、黒コートに囲まれた白ビスチェ)。従来のトーン抽出と同じ判定
        // hlCandidates は TryAnalyzePixels のハイライト候補と同条件(発火判定用)。
        private static List<GridPt> BuildEvidenceDomain(HsvGrid hsv, List<GridPt> core,
            float repH, float repS, float repV, out int sheenCount, out int hlCandidates)
        {
            sheenCount = 0;
            hlCandidates = 0;
            bool chromatic = repS >= AchromaSampleSatMax;
            float sheenSatCap = repS * EvSheenSatFrac;
            float sheenValMin = repV + EvSheenValMargin;
            float satFloor = repS * AutoToneSatFrac;
            const int VB = AutoToneValueBins;
            var vHist = new int[VB];
            var pass = new List<GridPt>(core.Count);
            foreach (var p in core)
            {
                float pH = hsv.h[p.gi], pS = hsv.s[p.gi], pV = hsv.v[p.gi];
                if (pV > ColorZone.HighlightValueMin
                    && pS < ColorZone.HighlightSaturationCeiling(repS)
                    && HueDistance(pH, repH) < ColorZone.ForgivenessHueGate)
                    hlCandidates++;
                if (chromatic)
                {
                    if (pS < sheenSatCap && pV > sheenValMin) { sheenCount++; continue; }
                    if (pS < satFloor) continue;
                    if (HueDistance(pH, repH) >= AutoToneHueBand) continue;
                }
                else
                {
                    if (pS > AchromaClusterSatMax) continue;
                }
                vHist[Mathf.Clamp((int)(pV * VB), 0, VB - 1)]++;
                pass.Add(p);
            }
            if (pass.Count == 0) return pass;
            if (!TryConnectedValueRange(vHist, pass.Count, repV, out int loBin, out int hiBin))
                return pass;
            if (loBin == 0 && hiBin == VB - 1) return pass;
            var domain = new List<GridPt>(pass.Count);
            foreach (var p in pass)
            {
                int vb = Mathf.Clamp((int)(hsv.v[p.gi] * VB), 0, VB - 1);
                if (vb >= loBin && vb <= hiBin) domain.Add(p);
            }
            return domain;
        }

        // 導出パラメータ(result)を実マッチャーに通し、証拠ドメインのうち選択される(strength > 0)
        // 割合を返す。基底マッチのみ(ハイライト系は成長テストの管轄)。
        private static float DomainCoverage(ColorZone aZone, in TuneResult result, HsvGrid hsv,
            Color32[] pixels, int w, int h, List<GridPt> domain)
        {
            if (domain.Count == 0) return 1f;
            var sim = BuildSimZone(aZone, result);
            sim.highlightRecovery = false;
            sim.UpdateCacheIfNeeded();
            int sel = 0;
            foreach (var p in domain)
            {
                Color32 c = pixels[p.y * w + p.x];
                var col = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                sim.GetMatchScoresPrecomputedHSV(hsv.h[p.gi], hsv.s[p.gi], hsv.v[p.gi], col,
                    p.x, p.y, w, h, out float strength, out _, out _);
                if (strength > 0f) sel++;
            }
            return sel / (float)domain.Count;
        }

        // sim で覆えていない証拠ドメイン画素のうち、最も多い V 帯の平均色を新サンプルとして返す。
        // 既存サンプルと重複(AutoToneMinSep 未満)するなら false(=これ以上足しても増えない)。
        private static bool TryUncoveredRepresentative(ColorZone sim, HsvGrid hsv, Color32[] pixels,
            int w, int h, List<GridPt> domain, Color primary, List<Color> existing, out Color rep)
        {
            const int VB = AutoToneValueBins;
            var cnt = new int[VB];
            var sumR = new float[VB];
            var sumG = new float[VB];
            var sumB = new float[VB];
            sim.UpdateCacheIfNeeded();
            foreach (var p in domain)
            {
                Color32 c = pixels[p.y * w + p.x];
                var col = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                sim.GetMatchScoresPrecomputedHSV(hsv.h[p.gi], hsv.s[p.gi], hsv.v[p.gi], col,
                    p.x, p.y, w, h, out float strength, out _, out _);
                if (strength > 0f) continue;
                int vb = Mathf.Clamp((int)(hsv.v[p.gi] * VB), 0, VB - 1);
                cnt[vb]++; sumR[vb] += col.r; sumG[vb] += col.g; sumB[vb] += col.b;
            }
            int best = -1, bestCnt = 0;
            for (int i = 0; i < VB; i++) if (cnt[i] > bestCnt) { bestCnt = cnt[i]; best = i; }
            rep = primary;
            int minCnt = Mathf.Max(EvUncoveredBinMinCount,
                Mathf.RoundToInt(domain.Count * EvUncoveredBinMinFrac));
            if (best < 0 || bestCnt < minCnt) return false;
            float inv = 1f / bestCnt;
            rep = new Color(sumR[best] * inv, sumG[best] * inv, sumB[best] * inv, 1f);
            if (ColorDist(rep, primary) < EvDupSep) return false;
            foreach (var s in existing) if (ColorDist(rep, s) < EvDupSep) return false;
            return true;
        }

        // 導出パラメータ(result)を実マッチャーに通し、全画面(不透明・非除外)で選択される画素数を
        // 返す(HSV 格子上の数)。追加サンプルの溢れ判定に使う。基底マッチのみ。
        private static int GlobalSelected(ColorZone aZone, in TuneResult result, HsvGrid hsv,
            Color32[] pixels, int w, int h, bool[] excluded, int maskW, int maskH)
        {
            var sim = BuildSimZone(aZone, result);
            sim.highlightRecovery = false;
            sim.UpdateCacheIfNeeded();
            int stride = hsv.stride;
            int sel = 0;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    int gi = grow + gx;
                    var col = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                    sim.GetMatchScoresPrecomputedHSV(hsv.h[gi], hsv.s[gi], hsv.v[gi], col,
                        x, y, w, h, out float strength, out _, out _);
                    if (strength > 0f) sel++;
                }
            }
            return sel;
        }

        // ─────────────────── bool マスクの収縮(4 近傍) ───────────────────

        private static bool[] ErodeMask(bool[] src, int w, int h, int r, CancellationToken ct)
        {
            var a = (bool[])src.Clone();
            var b = new bool[w * h];
            var po = new ParallelOptions { MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism() };
            for (int pass = 0; pass < r; pass++)
            {
                ct.ThrowIfCancellationRequested();
                var s = a; var d = b;
                Parallel.For(0, h, po, y =>
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        bool c = s[row + x];
                        bool l = x > 0 ? s[row + x - 1] : c;
                        bool rr = x < w - 1 ? s[row + x + 1] : c;
                        bool u = y > 0 ? s[row - w + x] : c;
                        bool dn = y < h - 1 ? s[row + w + x] : c;
                        d[row + x] = c && l && rr && u && dn;
                    }
                });
                (a, b) = (b, a);
            }
            return a;
        }
    }
}
