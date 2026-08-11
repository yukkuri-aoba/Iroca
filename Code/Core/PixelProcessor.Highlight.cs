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
    // PixelProcessor: ハイライト処理(コアからの空間伝播・sample→白 軸の帯成長)。
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

                    // 左上から右下へのパス (候補のみ・ラスタ順)
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

                            // 右と下も覗き見る (現在の状態で)
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

                    // 右下から左上へのパス (候補のみ・逆ラスタ順)
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

                            // 左と上も覗き見る
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

            // core から候補領域へ 4 連結 BFS（候補セルのみ拡張）
            while (qHead < qTail)
            {
                int packed = queue[qHead++];
                int x = packed & 0xFFFF, y = packed >> 16;
                int idx = y * w + x;
                if (x > 0)     TryVisit(idx - 1, (y << 16) | (x - 1));
                if (x < w - 1) TryVisit(idx + 1, (y << 16) | (x + 1));
                if (y > 0)     TryVisit(idx - w, ((y - 1) << 16) | x);
                if (y < h - 1) TryVisit(idx + w, ((y + 1) << 16) | x);
                // 逐次 BFS なので定期的にキャンセルを見る(数値ロジックは不変)。
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
    }
}
