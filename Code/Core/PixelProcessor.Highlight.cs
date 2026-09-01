// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static partial class PixelProcessor
    {
        /// <summary>
        /// ハイライト候補領域をコア領域から空間伝播させてマスク化する。
        /// strengthの強いピクセル(コア)から、ハイライト候補スコア(highlightPot)を持つ隣接ピクセルへ
        /// strengthを徐々に伝播させ、孤立した白いシャツなどを染めないようにする。
        /// </summary>
        private static void PropagateHighlights(float[] strength, float[] highlightPot, int w, int h,
            CancellationToken ct = default)
        {
            // bbox 制限(P1-5): strength を更新し得るのは下のガード `pot > 0f` を満たす画素のみ。
            // highlightPot>0 の bbox だけ走査すれば、bbox 外画素は元々 skip(何もしない)、bbox 端の
            // 近傍読み(i±1 / i±w)が指す bbox 外画素は pot=0 で本関数では不変のため値が一致し、
            // スイープ順序も bbox 内の pot>0 画素の相対順は全面走査と同一。よって出力はビット不変。
            // ハイライト候補はテクスチャの一部に偏在するため実効コストを大きく削減できる。
            // bbox 走査は行ごとに独立(min/max のマージ)なので並列化しても逐次と同一結果。
            if (!TryComputeStrengthBBox(highlightPot, w, h, 0f,
                    out int minX, out int minY, out int maxX, out int maxY, ct))
                return;   // pot>0 の画素が無い → 伝播対象なし

            // 候補画素(pot>0)だけをラスタ順のパック座標列に畳む(出力ビット不変)。
            // 旧実装は bbox の全画素を最大 6 回(3 パス × 前方/後方)走査していたが、
            // 外側ガード `pot > 0f` を満たさない画素は訪問しても読み書きが一切ない。
            // 同じ画素集合を同じ順序(前方=ラスタ順・後方=その逆順)で辿るので、近傍読みが
            // 見る途中経過まで含めて逐次スイープと完全に一致する。ハイライト候補は
            // テクスチャ中で疎なため、bbox 内でもさらに大きく実効走査量が減る。
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            int bh = maxY - minY + 1;
            var rowCount = new int[bh];
            Parallel.For(0, bh, po, ly =>
            {
                int rb = (ly + minY) * w;
                int n = 0;
                for (int x = minX; x <= maxX; x++) if (highlightPot[rb + x] > 0f) n++;
                rowCount[ly] = n;
            });
            var rowOff = new int[bh + 1];
            for (int ly = 0; ly < bh; ly++) rowOff[ly + 1] = rowOff[ly] + rowCount[ly];
            int nCand = rowOff[bh];
            if (nCand == 0) return;

            // パック座標 (y<<16)|x で保持する(index からの %w・/w を避ける。w/h は Unity の
            // 最大テクスチャ 16384 でも 16bit に収まる)。
            int[] cand = s_intPool.Rent(nCand);
            try
            {
                Parallel.For(0, bh, po, ly =>
                {
                    int y = ly + minY;
                    int rb = y * w;
                    int k = rowOff[ly];
                    for (int x = minX; x <= maxX; x++)
                        if (highlightPot[rb + x] > 0f) cand[k++] = (y << 16) | x;
                });

                int passes = 3;
                for (int p = 0; p < passes; p++)
                {
                    bool changed = false;

                    for (int k = 0; k < nCand; k++)
                    {
                        // 伝播スイープは逐次(順序依存)なので、一定間隔でキャンセルだけ見る。
                        if ((k & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                        int packed = cand[k];
                        int x = packed & 0xFFFF, y = packed >> 16;
                        int i = y * w + x;
                        float pot = highlightPot[i];
                        if (strength[i] < pot)
                        {
                            float maxNeighbor = 0f;
                            if (x > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - 1]);
                            if (y > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - w]);

                            if (x < w - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + 1]);
                            if (y < h - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + w]);

                            if (maxNeighbor > 0.1f)
                            {
                                float newS = Mathf.Min(pot, maxNeighbor * 0.95f);
                                if (newS > strength[i])
                                {
                                    strength[i] = newS;
                                    changed = true;
                                }
                            }
                        }
                    }

                    for (int k = nCand - 1; k >= 0; k--)
                    {
                        if ((k & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                        int packed = cand[k];
                        int x = packed & 0xFFFF, y = packed >> 16;
                        int i = y * w + x;
                        float pot = highlightPot[i];
                        if (strength[i] < pot)
                        {
                            float maxNeighbor = 0f;
                            if (x < w - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + 1]);
                            if (y < h - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + w]);

                            if (x > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - 1]);
                            if (y > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - w]);

                            if (maxNeighbor > 0.1f)
                            {
                                float newS = Mathf.Min(pot, maxNeighbor * 0.95f);
                                if (newS > strength[i])
                                {
                                    strength[i] = newS;
                                    changed = true;
                                }
                            }
                        }
                    }

                    if (!changed) break;
                }
            }
            finally
            {
                s_intPool.Return(cand);
            }
        }

        // ハイライト帯成長で使用する定数。
        // internal: ZoneAutoTuner の閉ループ検証が「帯候補になり得る画素」を同一条件で
        // シミュレートするために参照する（手動同期による定数ドリフトを避ける）。
        internal const float HlBandCoreThreshold = 0.90f;  // 信頼コア（本体）とみなす strength 下限
        internal const float HlBandAxisEps       = 0.10f;  // sample→白 軸からの許容残差（RGB ユークリッド）
        internal const float HlBandMinSampleSat  = 0.20f;  // 源色がこれ未満（灰色寄り）なら無効
        internal const float HlBandMinSatFrac    = 0.15f;  // 帯候補の彩度下限（源色相対）。白素材への流入を防ぐ

        /// <summary>
        /// ハイライト帯成長: matched core（本体）から「sample→白 直線上に乗った同色相の
        /// 明部画素」へ strength を空間連結で伸ばす。グローバル tolerance を上げずに
        /// 描き込みハイライトの薄い帯を full strength で拾い、ベタ塗り化・取りこぼしを防ぐ。
        ///
        /// 判定（元テクスチャと比較して「本体色が白く飛んだ画素」か）:
        ///   候補 = pV&gt;sV ∧ 同色相(hd&lt;hueCap) ∧ sample→白 軸からの残差&lt;eps
        ///          ∧ sS×MinSatFrac ≤ pS &lt; sS
        /// 安全ゲート（俯瞰: 周囲の構造を見る）:
        ///   候補のうち core(strength≥THR) に 4 連結で到達できる画素のみ採用。
        ///   孤立した同系色の島（別パーツ・白素材）は core に触れないので入らない。
        /// 採用画素は strength=1 にし、後段のハイライト白寄せ合成で階調を保ったまま再着色する。
        /// </summary>
        private static void GrowHighlightBand(
            float[] strength, Color32[] originalPixels,
            float[] pixH, float[] pixS, float[] pixV, ColorZone zone, int w, int h,
            CancellationToken ct = default)
        {
            float sH, sS, sV;
            Color.RGBToHSV(zone.sampleColor, out sH, out sS, out sV);
            if (sS < HlBandMinSampleSat) return;

            float sR = zone.sampleColor.r, sG = zone.sampleColor.g, sB = zone.sampleColor.b;
            float dR = 1f - sR, dG = 1f - sG, dB = 1f - sB;   // sample → 白 方向
            float dsq = dR * dR + dG * dG + dB * dB;
            if (dsq < 1e-6f) return;

            float hueCap = Mathf.Max(0.05f, zone.tolerance * 0.3f);
            float satFloor = sS * HlBandMinSatFrac;

            int len = w * h;
            // 従来はゾーンごとに bool[len]×2(4K で 16.7MB×2)を GC 確保していた。プールから
            // Rent(Create プールはゼロ初期化しないので Array.Clear で全 false に戻す)し、
            // 早期 return / キャンセル例外を含む全経路で finally から Return する。
            bool[] candidate = s_boolPool.Rent(len);
            bool[] visited = s_boolPool.Rent(len);
            // 探索キューは (y<<16)|x のパック座標を持つ int[]。旧実装は Queue<int> に画素 index を
            // 積み、デキューごとに idx%w と idx/w を計算していた(コアが数百万画素あるので除算だけで
            // 数千万回)。座標を持ち回れば除算はゼロになる(w/h は Unity の最大テクスチャ 16384 でも
            // 16bit に収まる)。各画素は visited を立ててから 1 回だけ積むので容量は len で足りる。
            // 到達集合は探索順に依存しないので出力は不変。
            int[] queue = s_intPool.Rent(len);
            int qHead = 0, qTail = 0;
            try
            {
            Array.Clear(candidate, 0, len);
            Array.Clear(visited, 0, len);

            // 候補判定: 各画素は独立(他画素を参照しない)なので並列化する。candidate[] は
            // 走査順に依存せず、書き込みは distinct index のため出力は逐次版とビット不変。
            // 行並列(per-index デリゲートの 1670 万回呼び出しを避ける)。
            var hlbPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            Parallel.For(0, h, hlbPo, y =>
            {
                int rowOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOff + x;
                    float pV = pixV[i];
                    if (pV <= sV) continue;
                    float pS = pixS[i];
                    if (pS < sS && pS >= satFloor)
                    {
                        float hd = Mathf.Abs(pixH[i] - sH);
                        if (hd > 0.5f) hd = 1f - hd;
                        if (hd < hueCap)
                        {
                            Color32 op = originalPixels[i];
                            float pr = op.r / 255f, pg = op.g / 255f, pb = op.b / 255f;
                            float ox = pr - sR, oy = pg - sG, oz = pb - sB;
                            float wv = (ox * dR + oy * dG + oz * dB) / dsq;
                            if (wv < 0f) wv = 0f; else if (wv > 1f) wv = 1f;
                            float rr = pr - (sR + wv * dR);
                            float rg = pg - (sG + wv * dG);
                            float rb = pb - (sB + wv * dB);
                            if (rr * rr + rg * rg + rb * rb < HlBandAxisEps * HlBandAxisEps)
                                candidate[i] = true;
                        }
                    }
                }
            });

            // core をシードとして収集する。行ごとの本数を数えてから行並列で詰めることで、
            // 全画素の逐次走査(4K で 1670 万回)を排除する。書き込み位置は行オフセットで
            // 決まるので enqueue 順は従来と同一の i 昇順のまま、visited も同じ集合に立つ。
            // BFS 到達集合はもともと探索順に依存しないので出力は不変。
            var seedRowCount = new int[h];
            Parallel.For(0, h, hlbPo, y =>
            {
                int rb = y * w;
                int n = 0;
                for (int x = 0; x < w; x++) if (strength[rb + x] >= HlBandCoreThreshold) n++;
                seedRowCount[y] = n;
            });
            var seedRowOff = new int[h + 1];
            for (int y = 0; y < h; y++) seedRowOff[y + 1] = seedRowOff[y] + seedRowCount[y];
            qTail = seedRowOff[h];
            if (qTail == 0) return;
            Parallel.For(0, h, hlbPo, y =>
            {
                int rb = y * w;
                int k = seedRowOff[y];
                for (int x = 0; x < w; x++)
                {
                    int i = rb + x;
                    if (strength[i] >= HlBandCoreThreshold)
                    {
                        visited[i] = true;
                        queue[k++] = (y << 16) | x;
                    }
                }
            });

            while (qHead < qTail)
            {
                int packed = queue[qHead++];
                int x = packed & 0xFFFF, y = packed >> 16;
                int idx = y * w + x;
                if (x > 0)     TryVisit(idx - 1, (y << 16) | (x - 1));
                if (x < w - 1) TryVisit(idx + 1, (y << 16) | (x + 1));
                if (y > 0)     TryVisit(idx - w, ((y - 1) << 16) | x);
                if (y < h - 1) TryVisit(idx + w, ((y + 1) << 16) | x);
                if ((qHead & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            }

            void TryVisit(int ni, int npacked)
            {
                if (visited[ni] || !candidate[ni]) return;
                visited[ni] = true;
                if (strength[ni] < 1f) strength[ni] = 1f;
                queue[qTail++] = npacked;
            }
            }
            finally
            {
                if (queue != null) s_intPool.Return(queue);
                s_boolPool.Return(candidate);
                s_boolPool.Return(visited);
            }
        }

        // ─────────────────── 閉領域ハイライト復帰(有彩サンプル専用) ───────────────────
        //
        // 【解決する問題】描き込みのハイライト芯は「地色より明るく・脱彩し・色相が回る」(実測: 青紫の
        // スニーカー(色相 245°)の芯が水色 215°、S 0.45、V 0.97)。既存のハイライト経路は色相帯を
        // hlHueCap(= max(0.05, tol×0.3) ≈ 18°)に絞り、帯成長は sample→白 軸からの残差で判定する
        // ため、色相が 30〜40° 回った芯は原理的に届かず、赤く塗った靴の真ん中に青い楕円が残る。
        // 色相帯を広げるだけでは「サンプルより明るい同色相の隣接素材」(肌など)を巻き込む
        // (ZoneAutoTuner.Verify の成長テストが守っている失敗)。
        //
        // 【アプローチ】色だけでは決まらないので空間構造で決める。ハイライト芯は必ず本体の
        // 内側にある: 選択に囲まれた閉領域(画像端から未選択画素を辿って届かない)で、しかも
        // **その周囲(グロー)が本体の典型明度より明るい** ときだけ、色相帯を ForgivenessHueGate
        // (同色相とみなす免除ゲート)まで広げて芯を戻す。RecoverEnclosedNeutral(グレーモードの中性
        // ツヤ復帰)と同じ「閉領域」の空間条件に、明部の文脈条件を足したもの。
        // GT スクリーニング(2026-09-01、14 被写体 × 3 クリック = 42 ケース、dev_safe の p25/p50/p95
        // 出力): 正解回収 3.2k〜5.5k px/位置(sneakers の芯 3 島すべて、skirt の星雲光)、誤回収は
        // 流出済み選択(precision 0.37)の内側の 148 px のみ。
        //
        // 【落とすもの(実測で確認した偽陽性)】
        //   ・平坦な地色に囲まれた淡色の模様(赤帯の中の淡黄ダイヤ): 周囲が本体の典型明度そのもの
        //     でグローが無い → リング明度ゲートで落ちる。色だけ(明るい・脱彩・色相差 30°)では
        //     本物の芯と区別できない。
        //   ・囲まれた白/灰の装飾(ロゴ・目の白目・金具): 中央値 S が彩度床未満 → 色相が信用できない
        //     ので対象外(白ツヤは既存の highlightRecovery / 穴埋めの担当)。
        //   ・微小成分: 穴埋め(holeFillPasses)の守備範囲かつ中央値統計が不安定 → 最小画素数で除外。
        //   ・大きな閉領域(囲まれた別パネル): 選択量比の上限で除外(ハイライト芯は本体の数%)。
        // 統計はすべて成分の中央値と「成分から 2〜4px 外側の選択画素」の平均で取る。1px 隣接帯は
        // AA 混色で成分自身の色に引っ張られる(実測: 淡黄ダイヤの隣接リング V 0.95 に対し 2〜4px
        // 外側は 0.87=地色)ため使わない。判定は色量の比と連結性のみで、特定色・座標に依存しない。
        //
        // 連結性は大域演算なので **フル画像経路でのみ**呼ぶ(部分クロップにはビット集合で転写する)。
        internal const int   EnclosedHlMinPixels  = 64;    // 成分の最小不透明画素数(8×8 相当。これ未満は穴埋めの守備範囲)
        internal const float EnclosedHlMaxSelFrac = 0.05f; // 成分の最大画素数(選択画素数比)。芯は本体の数%
        internal const float EnclosedHlSatFrac    = 0.60f; // 芯の中央値 S < min(サンプル S, リング S) × これ(脱彩の定義。ZoneAutoTuner.EvSheenSatFrac と同値)
        internal const int   EnclosedHlRingRadius = 4;     // リング = 成分境界から Chebyshev 距離 2..この値 の選択画素

        /// <summary>
        /// 選択に囲まれた「色相の回った有彩ハイライト芯」を空間条件で復帰させる(有彩サンプル専用)。
        /// 復帰した画素は strength=1。matchConf は触らない: 連結成分アンカリングは「コア(matchConf>0)を
        /// 含む成分」を残すので、復帰画素をコアにすると芯を囲む選択が流出成分だった場合にそれごと
        /// 錨止めしてしまう。芯の採否は囲んでいる成分の採否に従わせる(同じ連結成分なので一緒に残る/落ちる)。
        /// recoveredBits が非 null なら復帰画素のビットを立てる(詳細プレビューへの転写用)。戻り値は復帰画素数。
        /// </summary>
        private static int RecoverEnclosedHighlight(
            float[] strength, Color32[] pixels,
            float[] pixH, float[] pixS, float[] pixV, ColorZone zone, int w, int h,
            ulong[] recoveredBits, CancellationToken ct = default)
        {
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out _);
            if (sS < HlBandMinSampleSat) return 0;   // 灰色寄りのサンプルでは色相が信用できない(帯成長と同じ床)
            // 彩度床: 色相が完全に信用できる彩度(chromaConfidence が 1 に達する chromaThreshold+0.10)と
            // 帯成長の相対床の大きい方。上限はサンプル比の脱彩。窓が空なら対象外。
            float satFloor = Mathf.Max(sS * HlBandMinSatFrac, zone.chromaThreshold + 0.10f);
            float satCap = sS * EnclosedHlSatFrac;
            if (satFloor >= satCap) return 0;

            const float matchThr = ColorZone.MatchStrengthFloor;
            const float hueGate = ColorZone.ForgivenessHueGate;
            int len = w * h;
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };

            // 1) 前提チェック: 候補色(未選択・不透明・同色相帯・脱彩した有彩)の画素が無ければ何もしない。
            //    同時に選択画素(不透明)の V ヒストグラムを取り、本体の典型明度(中央値)を得る。
            var vHist = new int[256];
            int selCount = 0;
            bool anyCand = false;
            var mergeGate = new object();
            Parallel.For(0, h, po,
                () => (hist: new int[256], sel: 0, cand: false),
                (y, _, local) =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        if (pixels[i].a < 128) continue;
                        if (strength[i] > matchThr)
                        {
                            local.hist[Mathf.Clamp((int)(pixV[i] * 256f), 0, 255)]++;
                            local.sel++;
                            continue;
                        }
                        if (local.cand) continue;
                        float pS = pixS[i];
                        if (pS < satFloor || pS >= satCap) continue;
                        float hd = Mathf.Abs(pixH[i] - sH); if (hd > 0.5f) hd = 1f - hd;
                        if (hd < hueGate) local.cand = true;
                    }
                    return local;
                },
                local =>
                {
                    lock (mergeGate)
                    {
                        for (int b = 0; b < 256; b++) vHist[b] += local.hist[b];
                        selCount += local.sel;
                        anyCand |= local.cand;
                    }
                });
            if (!anyCand || selCount == 0) return 0;
            int maxPixels = (int)(selCount * EnclosedHlMaxSelFrac);
            if (maxPixels < EnclosedHlMinPixels) return 0;
            // グロー閾値: 本体の典型明度(選択の中央値 V)から、明部免除の立ち上がり(ヘッドルーム比)だけ明るい。
            float medSelV = HistogramMedian(vHist, selCount, 256f);
            float glowThr = medSelV + (1f - medSelV) * ColorZone.HighlightValueHeadroomFrac;
            if (glowThr >= 1f) return 0;

            bool[] free = s_boolPool.Rent(len);   // 未選択(閉領域探索の通路)
            bool[] open = s_boolPool.Rent(len);   // 画像端から到達できた未選択画素。成分走査後は「訪問済み」を兼ねる
            int[] stamp = s_intPool.Rent(len);    // +id=現在の成分, -id=その成分のリング判定済み。成分 id は単調増加なのでクリア不要
            int[] queue = s_intPool.Rent(len);    // 成分 flood のキュー(パック座標 (y<<16)|x)
            int recovered = 0;
            try
            {
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        free[i] = strength[i] <= matchThr;
                        open[i] = false;
                        stamp[i] = 0;
                    }
                });
                MarkOpenFromBorder(free, open, w, h, ct);

                var hV = new int[256];
                var hS = new int[256];
                var hHd = new int[256];   // 色相距離 0..0.5 を 256 bin
                int compId = 0;
                for (int y0 = 0; y0 < h; y0++)
                {
                    int row0 = y0 * w;
                    for (int x0 = 0; x0 < w; x0++)
                    {
                        int i0 = row0 + x0;
                        if (!free[i0] || open[i0]) continue;
                        ct.ThrowIfCancellationRequested();

                        // 2) 閉領域成分を 4 近傍 flood で集める(free ∧ !open)。open を訪問済みに流用。
                        compId++;
                        int qHead = 0, qTail = 0;
                        open[i0] = true; stamp[i0] = compId; queue[qTail++] = (y0 << 16) | x0;
                        int nOpaque = 0;
                        Array.Clear(hV, 0, 256); Array.Clear(hS, 0, 256); Array.Clear(hHd, 0, 256);
                        while (qHead < qTail)
                        {
                            int packed = queue[qHead++];
                            int x = packed & 0xFFFF, y = packed >> 16;
                            int i = y * w + x;
                            if (pixels[i].a >= 128)
                            {
                                nOpaque++;
                                hV[Mathf.Clamp((int)(pixV[i] * 256f), 0, 255)]++;
                                hS[Mathf.Clamp((int)(pixS[i] * 256f), 0, 255)]++;
                                float hd = Mathf.Abs(pixH[i] - sH); if (hd > 0.5f) hd = 1f - hd;
                                hHd[Mathf.Clamp((int)(hd * 512f), 0, 255)]++;
                            }
                            if (x > 0 && free[i - 1] && !open[i - 1])
                            { open[i - 1] = true; stamp[i - 1] = compId; queue[qTail++] = (y << 16) | (x - 1); }
                            if (x < w - 1 && free[i + 1] && !open[i + 1])
                            { open[i + 1] = true; stamp[i + 1] = compId; queue[qTail++] = (y << 16) | (x + 1); }
                            if (y > 0 && free[i - w] && !open[i - w])
                            { open[i - w] = true; stamp[i - w] = compId; queue[qTail++] = ((y - 1) << 16) | x; }
                            if (y < h - 1 && free[i + w] && !open[i + w])
                            { open[i + w] = true; stamp[i + w] = compId; queue[qTail++] = ((y + 1) << 16) | x; }
                        }
                        if (nOpaque < EnclosedHlMinPixels || nOpaque > maxPixels) continue;

                        // 3) 成分の色ゲート(中央値): 同色相帯・脱彩した有彩。
                        float medHd = HistogramMedian(hHd, nOpaque, 512f);
                        if (medHd >= hueGate) continue;
                        float medS = HistogramMedian(hS, nOpaque, 256f);
                        if (medS < satFloor || medS >= satCap) continue;

                        // 4) リング(成分境界から Chebyshev 距離 2..R の選択画素)の平均 V/S。
                        //    成分に 8 隣接する画素(距離 1 = AA 混色帯)は除く。
                        double sumV = 0, sumS = 0; int ringN = 0;
                        const int R = EnclosedHlRingRadius;
                        for (int k = 0; k < qTail; k++)
                        {
                            int packed = queue[k];
                            int x = packed & 0xFFFF, y = packed >> 16;
                            int i = y * w + x;
                            bool boundary = x == 0 || y == 0 || x == w - 1 || y == h - 1
                                || stamp[i - 1] != compId || stamp[i + 1] != compId
                                || stamp[i - w] != compId || stamp[i + w] != compId;
                            if (!boundary) continue;
                            int yLo = Mathf.Max(0, y - R), yHi = Mathf.Min(h - 1, y + R);
                            int xLo = Mathf.Max(0, x - R), xHi = Mathf.Min(w - 1, x + R);
                            for (int yy = yLo; yy <= yHi; yy++)
                            {
                                int rowJ = yy * w;
                                for (int xx = xLo; xx <= xHi; xx++)
                                {
                                    int j = rowJ + xx;
                                    int sj = stamp[j];
                                    if (sj == compId || sj == -compId) continue;
                                    stamp[j] = -compId;   // この成分については判定済み
                                    if (strength[j] <= matchThr || pixels[j].a < 128) continue;
                                    if (IsAdjacent8ToStamp(stamp, compId, xx, yy, w, h)) continue;
                                    sumV += pixV[j]; sumS += pixS[j]; ringN++;
                                }
                            }
                        }
                        if (ringN == 0) continue;
                        float ringV = (float)(sumV / ringN), ringS = (float)(sumS / ringN);
                        // 5) 文脈ゲート: 周囲がグロー(本体の典型明度より明るい)で、芯はその周囲よりも脱彩している。
                        if (ringV <= glowThr) continue;
                        if (medS >= Mathf.Min(sS, ringS) * EnclosedHlSatFrac) continue;

                        // 6) 復帰: 成分の不透明画素を full strength へ。
                        for (int k = 0; k < qTail; k++)
                        {
                            int packed = queue[k];
                            int i = (packed >> 16) * w + (packed & 0xFFFF);
                            if (pixels[i].a < 128) continue;
                            strength[i] = 1f;
                            if (recoveredBits != null) recoveredBits[i >> 6] |= 1UL << (i & 63);
                            recovered++;
                        }
                    }
                }
            }
            finally
            {
                s_intPool.Return(queue);
                s_intPool.Return(stamp);
                s_boolPool.Return(open);
                s_boolPool.Return(free);
            }
            return recovered;
        }

        // 画素 (x,y) の 8 近傍に stamp==id の画素があるか(成分に隣接する AA 混色帯の除外用)。
        private static bool IsAdjacent8ToStamp(int[] stamp, int id, int x, int y, int w, int h)
        {
            int yLo = Mathf.Max(0, y - 1), yHi = Mathf.Min(h - 1, y + 1);
            int xLo = Mathf.Max(0, x - 1), xHi = Mathf.Min(w - 1, x + 1);
            for (int yy = yLo; yy <= yHi; yy++)
            {
                int row = yy * w;
                for (int xx = xLo; xx <= xHi; xx++)
                    if (stamp[row + xx] == id) return true;
            }
            return false;
        }

        // ヒストグラム(bin 幅 1/scale)の中央値(bin 中心)。n は総度数。
        private static float HistogramMedian(int[] hist, int n, float scale)
        {
            int half = (n + 1) / 2, acc = 0;
            for (int b = 0; b < hist.Length; b++)
            {
                acc += hist[b];
                if (acc >= half) return (b + 0.5f) / scale;
            }
            return (hist.Length - 0.5f) / scale;
        }
    }
}
