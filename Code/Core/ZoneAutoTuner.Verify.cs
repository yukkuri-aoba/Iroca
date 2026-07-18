// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    // ZoneAutoTuner: 閉ループ検証(実マッチャーで導出パラメータの有害設定を検出し安全側へ倒す)。
    internal static partial class ZoneAutoTuner
    {
        // ─────────────────── 閉ループ検証（実マッチャーによる選択シミュレーション） ───────────────────
        // 導出パラメータを ColorZone.GetMatchScoresPrecomputedHSV（出荷されるマッチャーそのもの）に
        // 通して選択を模擬し、統計で有害な設定を検出して安全側へ倒す。距離分布ベースの導出
        // (TryDerive*) は本番マッチの免除経路(ハイライト距離免除・ハイライト復元)を再現しておらず、
        // 「導出 tolerance では拾わないはずの領域」が本番で選択される乖離があるため、最終判定は実マッチャーで
        // 検証する(導出側が持つ簡略モデルではなく、出荷される実装そのものを唯一の正とするための措置)。

        // highlightRecovery 成長テスト:
        // 自動 ON 判定(MergeAnalyzed のハイライト候補数)は「パーツ境界の AA 画素(背景との混色)」を
        // スペキュラと区別できないため、白背景テクスチャではほぼ常に ON になる。ON が有害なのは
        // ハイライト系経路が「サンプルより明るい同色相の別素材(肌など)」を丸ごと拾うときで、
        // その害は選択画素の増加率として直接測れる:
        //   growth = (ハイライト系経路のみで加わり得る画素数) / (通常マッチの画素数)
        // 増加側の経路は 2 つあり、両方を数える:
        //   1. ハイライト復元マッチ(highlightPotential>0) — 彩度を見ない緩い距離
        //   2. ハイライト帯拡張(GrowHighlightBand の候補条件) — sample→白 軸近傍の明るい画素へ
        //      コア連結 BFS で strength=1 を伝播する。肌のような「明るい同色相の隣接素材」は
        //      軸残差が小さく候補化し、境界 AA を橋にして全面が塗られる(実測でこちらが主経路)。
        // 本物のスペキュラはパーツ面積の数%(実測例: 手描きの光沢で +1.0%、合成スペキュラで +2〜3%)、
        // 別素材の巻き込みは数十%以上(実測例: 服の隣の肌を丸ごと拾って +80%)なので明確に分離できる。
        // 面積比というテクスチャ統計のみで決まり、特定色・キャラへの依存は無い。
        private const float HighlightGrowthMaxFrac = 0.15f;
        private const int   VerifyMinBaseSelected  = 64;

        private static void VerifyHighlightRecoveryGrowth(Color32[] pixels, int w, int h,
            ColorZone zone, bool[] excluded, int maskW, int maskH, ref TuneResult result)
        {
            var sim = BuildSimZone(zone, result);

            // 帯拡張(GrowHighlightBand)の候補条件を同一定数でミラーする。
            // 連結性(コア連結 BFS)は模擬しない=候補は全て加算される上界。境界 AA が常に
            // 橋になるため実運用でもほぼ全候補へ到達し、上界と実際の差は小さい。
            Color.RGBToHSV(zone.sampleColor, out float bSH, out float bSS, out float bSV);
            bool bandActive = zone.highlightBandExpand && bSS >= PixelProcessor.HlBandMinSampleSat;
            float sR = zone.sampleColor.r, sG = zone.sampleColor.g, sB = zone.sampleColor.b;
            float dR = 1f - sR, dG = 1f - sG, dB = 1f - sB;   // sample → 白 方向
            float dsq = dR * dR + dG * dG + dB * dB;
            if (dsq < 1e-6f) bandActive = false;
            float hueCap = Mathf.Max(0.05f, result.tolerance * 0.3f);
            float bandSatFloor = bSS * PixelProcessor.HlBandMinSatFrac;
            float axisEpsSq = PixelProcessor.HlBandAxisEps * PixelProcessor.HlBandAxisEps;

            int stride = (w <= 2048) ? 1 : 2;
            int baseSel = 0, hlOnly = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowStart = y * w;
                for (int x = 0; x < w; x += stride)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    var col = new Color(r, g, b, 1f);
                    Color.RGBToHSV(col, out float pH, out float pS, out float pV);
                    sim.GetMatchScoresPrecomputedHSV(pH, pS, pV, col, x, y, w, h,
                        out float strength, out float hlPot, out _);
                    if (strength > 0f) { baseSel++; continue; }
                    if (hlPot > 0f) { hlOnly++; continue; }
                    // 帯拡張の候補条件(GrowHighlightBand と同一)
                    if (bandActive && pV > bSV && pS < bSS && pS >= bandSatFloor)
                    {
                        float hd = Mathf.Abs(pH - bSH); if (hd > 0.5f) hd = 1f - hd;
                        if (hd < hueCap)
                        {
                            float ox = r - sR, oy = g - sG, oz = b - sB;
                            float wv = (ox * dR + oy * dG + oz * dB) / dsq;
                            if (wv < 0f) wv = 0f; else if (wv > 1f) wv = 1f;
                            float rr = r - (sR + wv * dR);
                            float rg = g - (sG + wv * dG);
                            float rb = b - (sB + wv * dB);
                            if (rr * rr + rg * rg + rb * rb < axisEpsSq) hlOnly++;
                        }
                    }
                }
            }
            if (baseSel >= VerifyMinBaseSelected && hlOnly > baseSel * HighlightGrowthMaxFrac)
                result.highlightRecovery = false;
        }

        // 明部距離免除(bright forgiveness)の過剰検出:
        // 本番マッチは「サンプルより明るく同色相」の画素の距離を最大 70% 免除する
        // (ColorZone.CalculateHybridDistance)。本物のハイライト芯を救う仕組みだが、
        // 「サンプルより明るい同色相の別素材」(服の隣の肌が典型)も同じ色信号を持つため、
        // 距離分布から導出した tolerance では拾わないはずの領域が本番で丸ごと選択される。
        // トーン免除は zone パラメータではないので直接は切れない。そこで
        //   forgivenOnly = (免除ありで選択) − (免除なしで選択)
        // を実マッチャーで数え、免除だけで加わる画素が基礎選択に対して大きすぎるときは、
        // tolerance を段階的に下げて免除経由の巻き込みを打ち切る。本物のハイライト芯は
        // パーツ面積の数%なので発火しない。下げ幅は「サンプル近傍クラスタ(証拠)の被覆率」を
        // ガードし、パーツ本体の取りこぼしが出る手前で止める(拒否系優先・recall ガード付き)。
        private const float BrightForgiveMaxFrac = 0.15f;  // forgivenOnly/baseSel がこれ超で過剰と判定
        private const float EvidenceKeepFrac     = 0.97f;  // tol 縮小時に維持すべき証拠被覆率(導出時比)
        private static readonly float[] TolShrinkSteps = { 0.8f, 0.65f, 0.5f };

        private static void VerifyBrightForgivenessOvershoot(Color32[] pixels, int w, int h,
            ColorZone zone, bool[] excluded, int maskW, int maskH, ref TuneResult result)
        {
            var sim = BuildSimZone(zone, result);
            sim.highlightRecovery = false;  // ハイライト復元は成長テスト側で扱う(基底マッチのみ測る)

            // (selWith, selWithout, evidenceCovered) を tolerance ごとに実マッチャーで数える。
            void Eval(float tol, out int selWith, out int selWithout, out int evCov)
            {
                sim.tolerance = tol;
                sim.UpdateCacheIfNeeded();
                int sw = 0, swo = 0, ec = 0;
                var caches = SampleWindows(sim);
                int stride = (w <= 2048) ? 1 : 2;
                for (int y = 0; y < h; y += stride)
                {
                    int rowStart = y * w;
                    for (int x = 0; x < w; x += stride)
                    {
                        Color32 c = pixels[rowStart + x];
                        if (c.a < 128) continue;
                        if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                        float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                        var col = new Color(r, g, b, 1f);
                        Color.RGBToHSV(col, out float pH, out float pS, out float pV);

                        sim.simDisableBrightForgiveness = false;
                        sim.GetMatchScoresPrecomputedHSV(pH, pS, pV, col, x, y, w, h,
                            out float sWith, out _, out _);
                        bool selectedWith = sWith > 0f;
                        if (selectedWith)
                        {
                            sw++;
                            sim.simDisableBrightForgiveness = true;
                            sim.GetMatchScoresPrecomputedHSV(pH, pS, pV, col, x, y, w, h,
                                out float sWithout, out _, out _);
                            if (sWithout > 0f) swo++;
                        }
                        // 証拠 = いずれかのサンプルの near 窓に入る有彩画素(クラスタと同一条件)。
                        // 被覆率は「証拠のうち選択された画素」で recall の代理を測る。
                        if (selectedWith && InAnyNearWindow(caches, pH, pS, pV)) ec++;
                    }
                }
                sim.simDisableBrightForgiveness = false;
                selWith = sw; selWithout = swo; evCov = ec;
            }

            float derived = result.tolerance;
            Eval(derived, out int selW0, out int selWo0, out int evCov0);
            int forgivenOnly = selW0 - selWo0;
            if (selWo0 < VerifyMinBaseSelected) return;                    // 基礎選択が小さすぎる
            if (forgivenOnly <= selWo0 * BrightForgiveMaxFrac) return;     // 免除は正常範囲

            // 免除経由の巻き込みが過剰 → tolerance を段階的に下げ、証拠被覆率を守れる範囲で
            // 免除超過が閾値内に収まる最初の(=最大の) tolerance を採る。
            foreach (float frac in TolShrinkSteps)
            {
                float t = Mathf.Max(derived * frac, ForeignLowFloor);
                Eval(t, out int selW, out int selWo, out int evCov);
                if (evCov < evCov0 * EvidenceKeepFrac) break;              // これ以上はパーツを削る
                if (selWo >= VerifyMinBaseSelected
                    && (selW - selWo) <= selWo * BrightForgiveMaxFrac)
                {
                    result.tolerance = t;
                    return;
                }
                if (t <= ForeignLowFloor) break;
            }
        }

        // 明部同色相テール(塗りツヤ/スペキュラ)の取りこぼし救済:
        // スタイライズド塗りのツヤは「色相を僅かにずらし軽く脱彩した明部」(実測例: hd≈0.06,
        // |dS|≈0.22, |dV|≈0.35)として near 窓のすぐ外に分布するため、距離分布 P95 に一切現れず、
        // 導出 tolerance は原理的に届かない(明部距離免除を効かせても僅かに tolerance 超過で全滅する)。
        // そこで実マッチャーで tolerance を段階拡張し、「追加で選択される画素」が
        //   (1) 基礎選択に対して小面積(本物のツヤはパーツ面積の数%。明るい同色相の別素材=肌などは
        //       数十%に達するため面積比で分離できる)
        //   (2) 圧倒的に「サンプルより明るい・同色相・無彩寄りでない・トーン連結域の延長上」
        //       (ツヤのシグネチャ。隣接色ブリードは同色相でなく、白背景の巻き込みは相対彩度床
        //       sS*ChromaClusterSatFrac を割るため棄却。同色相・軽脱彩でもツヤと区別できない
        //       「V の空谷の向こうの明るい別素材」(暗い革地に対する明るいクリーム生地の色双子)は、
        //       V 連結領域ゲートの上端 vConnHiBin を僅かに超えるだけの画素に限る条件で棄却する。
        //       シェーディングが V 連続というトーン抽出と同じ仮定で、ツヤは連結域の明端に接するが
        //       別素材は谷の向こうに孤立する。副作用として V が段状に量子化されたベタ塗りパーツの
        //       明段は拡張対象から外れるが、これは色統計上「別パーツの明段」と同型で原理的に区別
        //       できないため、安全側(拡張しない)に倒す)
        // の両方を満たす最大ステップだけを受け入れる。シグネチャ不合格のステップは採用しない(打ち切りは
        // しない: 累積判定なので、より広いステップで追加集合全体が合格すればそれを採る)。面積超過は
        // 単調増加なので、そこで打ち切る。判定はすべてテクスチャ統計(面積比・サンプル相対の色信号)のみで、
        // 特定キャラ・色・座標には依存しない。
        // 発動しない条件(呼び出し側でスキップ): 無彩サンプル / foreign 打ち切り発火(隣接同色相パーツを
        // 検出済み) / 免除過剰で tolerance 縮小済み / hlRec 成長テストで別素材巻き込みを検出済み /
        // V 連結範囲が確定できなかった。
        private const float SheenGrowMaxAddedFrac = 0.05f;  // 追加画素/基礎選択(累積)の上限
        private const float SheenSigMinFrac       = 0.90f;  // 追加画素中のツヤシグネチャ率の下限
        private const int   SheenMinAddedCount    = 24;     // これ未満の追加は証拠不足として採用しない
        private const int   SheenVConnMarginBins  = 2;      // 連結域上端から許す V 余白(連結歩行の橋渡し幅と同じ)
        private static readonly float[] SheenTolGrowSteps = { 1.15f, 1.3f, 1.5f };

        private static void VerifyBrightSheenRecall(Color32[] pixels, int w, int h,
            ColorZone zone, bool[] excluded, int maskW, int maskH, int vConnHiBin, ref TuneResult result)
        {
            float derived = result.tolerance;
            if (derived >= ChromaTolMax) return; // 既に上限=伸ばす余地なし

            var sim = BuildSimZone(zone, result);
            sim.highlightRecovery = false; // 基底マッチのみ測る(ハイライト系は成長テスト側の管轄)

            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            float satSigFloor = sS * ChromaClusterSatFrac;
            // ツヤとして許す V 上限 = トーン連結域の上端 + 余白。連結域が明端まで届いている
            // パーツ(素直な連続シェーディング)では 1.0 超になり実質無制限。
            float sheenVMax = (vConnHiBin + 1 + SheenVConnMarginBins) / (float)AutoToneValueBins;

            int stride = (w <= 2048) ? 1 : 2;
            int gw = (w + stride - 1) / stride;
            int gh = (h + stride - 1) / stride;

            // ── 基礎選択(導出 tolerance)のビットマスク ──
            var baseSel = new bool[gw * gh];
            int baseCount = 0;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    var col = new Color(r, g, b, 1f);
                    Color.RGBToHSV(col, out float pH, out float pS, out float pV);
                    sim.GetMatchScoresPrecomputedHSV(pH, pS, pV, col, x, y, w, h,
                        out float s0, out _, out _);
                    if (s0 > 0f) { baseSel[gy * gw + gx] = true; baseCount++; }
                }
            }
            if (baseCount < VerifyMinBaseSelected) return;

            // ── 段階拡張: 追加画素の面積とシグネチャを実マッチャーで判定 ──
            float accepted = derived;
            foreach (float mult in SheenTolGrowSteps)
            {
                float t = Mathf.Min(derived * mult, ChromaTolMax);
                if (t <= accepted) break; // 上限到達でこれ以上伸ばせない
                sim.tolerance = t;
                sim.UpdateCacheIfNeeded();
                int added = 0, sig = 0;
                for (int y = 0, gy = 0; y < h; y += stride, gy++)
                {
                    int rowStart = y * w;
                    for (int x = 0, gx = 0; x < w; x += stride, gx++)
                    {
                        if (baseSel[gy * gw + gx]) continue;
                        Color32 c = pixels[rowStart + x];
                        if (c.a < 128) continue;
                        if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                        float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                        var col = new Color(r, g, b, 1f);
                        Color.RGBToHSV(col, out float pH, out float pS, out float pV);
                        sim.GetMatchScoresPrecomputedHSV(pH, pS, pV, col, x, y, w, h,
                            out float st, out _, out _);
                        if (st <= 0f) continue;
                        added++;
                        float hd = Mathf.Abs(pH - sH); if (hd > 0.5f) hd = 1f - hd;
                        if (pV > sV && pV < sheenVMax
                            && hd < ColorZone.ForgivenessHueGate && pS >= satSigFloor)
                            sig++;
                    }
                }
                if (added > baseCount * SheenGrowMaxAddedFrac) break; // 面積超過(単調)→打ち切り
                if (added >= SheenMinAddedCount && sig >= added * SheenSigMinFrac)
                    accepted = t; // このステップの累積追加集合はツヤとして合格
                if (t >= ChromaTolMax) break; // 上限で評価済み=以降のステップは同値
            }
            if (accepted > derived) result.tolerance = accepted;
        }

        // 証拠判定用: 各サンプルの HSV と near 窓下限彩度を前計算する。
        private static SampleHSV[] SampleWindows(ColorZone sim)
        {
            var extras = sim.extraSamples ?? new List<Color>();
            return BuildSampleHSVs(sim.sampleColor, extras);
        }

        private static bool InAnyNearWindow(SampleHSV[] samples, float pH, float pS, float pV)
        {
            for (int si = 0; si < samples.Length; si++)
            {
                var sm = samples[si];
                float hd = Mathf.Abs(pH - sm.h); if (hd > 0.5f) hd = 1f - hd;
                if (hd >= NearHueDist) continue;
                if (Mathf.Abs(pS - sm.s) >= NearSatDist) continue;
                if (Mathf.Abs(pV - sm.v) >= NearValDist) continue;
                if (pS < sm.s * ChromaClusterSatFrac) continue;
                return true;
            }
            return false;
        }

        // 導出パラメータを適用したシミュレーション用ゾーンを作る（zone 本体は変更しない）。
        // highlightRecovery は常に ON にし、呼び出し側が strength / highlightPotential を
        // 分けて集計できるようにする。
        private static ColorZone BuildSimZone(ColorZone zone, in TuneResult result)
        {
            var sim = zone.Clone();
            sim.enabled                 = true;
            sim.mode                    = SelectionMode.ColorPick;
            sim.tolerance               = result.tolerance;
            sim.saturationStrictness    = result.saturationStrictness;
            sim.saturationGuard         = result.saturationGuard;
            sim.chromaThreshold         = result.chromaThreshold;
            sim.highlightRecovery       = true;
            sim.edgeSoftness            = result.edgeSoftness;
            sim.shadowForgivenessSatMin = result.shadowForgivenessSatMin;
            sim.extraSamples = result.autoSamples ?? new List<Color>();
            sim.UpdateCacheIfNeeded();
            return sim;
        }
    }
}
