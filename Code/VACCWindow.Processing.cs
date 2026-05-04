using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // エクスポート処理で使用されるインスタンスラッパー（メインスレッド、インスタンスフィールドに直接アクセス可能）
        private void ProcessPixels(Texture2D tex)
        {
            var sorted = zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();
            if (sorted.Count == 0) return;
            Color32[] pixels = tex.GetPixels32();
            ProcessPixelsArray(pixels, tex.width, tex.height,
                BuildMaskSnapshot(), sorted, edgeFeather, antiAliasCleanup,
                holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp,
                useDecontamination: useDecontamination,
                decontaminationRadius: decontaminationRadius);
            tex.SetPixels32(pixels);
        }

        // スタティック計算メソッド — バックグラウンドスレッドで実行可能
        // Texture2Dなし、UnityEngine.Object APIなし、Mathfとカラー計算のみ（いずれもスレッドセーフ）
        // originX/Y: フル解像度テクスチャでのクロップオフセット（0で全テクスチャ処理）
        // fullW/H: フル解像度テクスチャの寸法（0 = w/hと同じ、つまりクロップなし）
        // useDecontamination: AA境界でα分解＋再合成を行い halo を除去
        private static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int holeFillPasses = 5, int holeFillMinNeighbors = 4,
            float relaxedSatMin = 0.02f, float relaxedSatRamp = 0.08f,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0,
            bool useDecontamination = true, int decontaminationRadius = 4,
            float decontaminationInteriorThreshold = 0.97f)
        {
            ProcessPixelsArray(pixels, w, h, masks, sortedZones, edgeFeather, antiAliasCleanup,
                holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp,
                originX, originY, fullW, fullH, CancellationToken.None,
                useDecontamination, decontaminationRadius, decontaminationInteriorThreshold);
        }

        // キャンセルトークン対応バージョン — バックグラウンドプレビューから使用
        private static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int holeFillPasses, int holeFillMinNeighbors,
            float relaxedSatMin, float relaxedSatRamp,
            int originX, int originY, int fullW, int fullH,
            CancellationToken cancellationToken,
            bool useDecontamination = true, int decontaminationRadius = 4,
            float decontaminationInteriorThreshold = 0.97f)
        {
            if (fullW <= 0) fullW = w;
            if (fullH <= 0) fullH = h;

            // マスクスナップショットからローカル変数に展開
            bool[] commonMask = masks?.common;
            int maskW = masks?.width ?? 0;
            int maskH = masks?.height ?? 0;

            int len = w * h;
            Color32[] originalPixels = new Color32[len];
            System.Array.Copy(pixels, originalPixels, len);

            // 全ピクセルの HSV を zone ループに入る前に一括計算（zone 数に関わらず1回）
            float[] pixH = ArrayPool<float>.Shared.Rent(len);
            float[] pixS = ArrayPool<float>.Shared.Rent(len);
            float[] pixV = ArrayPool<float>.Shared.Rent(len);
            try
            {
            Parallel.For(0, len, i =>
            {
                Color.RGBToHSV((Color)originalPixels[i], out pixH[i], out pixS[i], out pixV[i]);
            });

            foreach (var zone in sortedZones)
            {
                // キャンセルチェック: 新しいプレビューリクエストが来た場合は即座に中断
                cancellationToken.ThrowIfCancellationRequested();

                // このゾーンに紐付くゾーン別マスクを取得（存在しなければ null）
                bool[] zoneMask = null;
                if (masks != null && masks.zones != null && !string.IsNullOrEmpty(zone.id))
                    masks.zones.TryGetValue(zone.id, out zoneMask);

                var po = new ParallelOptions { CancellationToken = cancellationToken };

                // Parallel.For に入る前にキャッシュを確定させてホットループ内の条件分岐を排除
                zone.UpdateCacheIfNeeded();

                // 1. 元のピクセルカラーを使用した強度マップを構築
                float[] strength = ArrayPool<float>.Shared.Rent(len);
                Array.Clear(strength, 0, len);
                float[] highlightPot = null;
                if (zone.highlightRecovery)
                {
                    highlightPot = ArrayPool<float>.Shared.Rent(len);
                    Array.Clear(highlightPot, 0, len);
                }

                Parallel.For(0, h, po, y =>
                {
                    int yf = y + originY;
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int xf = x + originX;
                        int i = rowOff + x;
                        if (IsExcludedCombined(xf, yf, fullW, fullH, commonMask, zoneMask, maskW, maskH)) continue;

                        float s, hPot;
                        zone.GetMatchScoresPrecomputedHSV(pixH[i], pixS[i], pixV[i], (Color)originalPixels[i], xf, yf, fullW, fullH, out s, out hPot);
                        strength[i] = s;
                        if (highlightPot != null) highlightPot[i] = hPot;
                    }
                });

                // 1.a 空間伝播によるハイライト領域の回収 (モルフォロジー拡張)
                if (highlightPot != null)
                {
                    PropagateHighlights(strength, highlightPot, w, h);
                    ArrayPool<float>.Shared.Return(highlightPot);
                    highlightPot = null;
                }

                // 1.a.2 Flood Fill: シード点から連続する領域のみに強度を絞り込む
                if (zone.mode == SelectionMode.ColorPick && zone.useFloodFill && zone.seedUV.x >= 0f)
                {
                    int efW = fullW > 0 ? fullW : w;
                    int efH = fullH > 0 ? fullH : h;
                    int seedXFull = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.x * (efW - 1)), 0, efW - 1);
                    int seedYFull = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.y * (efH - 1)), 0, efH - 1);
                    int seedX = seedXFull - originX;
                    int seedY = seedYFull - originY;
                    if (seedX >= 0 && seedX < w && seedY >= 0 && seedY < h)
                        ApplyFloodFillMask(strength, pixS, pixV, w, h, seedX, seedY, zone.edgeStopThreshold);
                    else
                        Array.Clear(strength, 0, len);
                }

                // 1b. 孤立した穴を埋める：アンチエイリアス処理された端のピクセルは低彩度を持つことが多く
                //     satConfidenceで見落とされて、元のカラーの孤立したドットを残す
                //     ゼロ強度ピクセルが主にマッチしたピクセルに囲まれている場合は埋める
                FillSmallHoles(strength, w, h, holeFillPasses, holeFillMinNeighbors);

                // 1c. 境界復元：マッチしたピクセルに隣接するマッチしないピクセルを再評価
                //     古い固定低彩度閾値を使用して、正しい段階的な強度を与える
                if (antiAliasCleanup > 0)
                    RecoverBoundaryEdges(strength, w, h, pixH, pixS, pixV,
                        zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                        zone.satDistWeight, relaxedSatMin, relaxedSatRamp, antiAliasCleanup);

                // 2. スムーズな端の遷移のためのガウシアンブラー（端に限定）
                if (edgeFeather > 0.01f)
                {
                    float[] preBlur = ArrayPool<float>.Shared.Rent(len);
                    Array.Copy(strength, preBlur, len);
                    float[] blurOut = ArrayPool<float>.Shared.Rent(len);
                    if (GaussianBlur(strength, blurOut, w, h, edgeFeather))
                    {
                        ArrayPool<float>.Shared.Return(strength);
                        strength = blurOut;
                        ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * 2.5f));
                    }
                    else
                    {
                        ArrayPool<float>.Shared.Return(blurOut);
                    }
                    ArrayPool<float>.Shared.Return(preBlur);
                }

                // 3. 除外マスクを再適用：ブラーが除外ピクセルにはみ出す可能性がある
                if (commonMask != null || zoneMask != null)
                {
                    Parallel.For(0, h, po, y =>
                    {
                        int yf = y + originY;
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int xf = x + originX;
                            int i = rowOff + x;
                            if (IsExcludedCombined(xf, yf, fullW, fullH, commonMask, zoneMask, maskW, maskH)) strength[i] = 0f;
                        }
                    });
                }

                // 3b. AA 境界の α 分解（オプション）：strength が 0 < s < interiorThreshold の
                //     ピクセルを「α×FG + (1-α)×BG」と見て元テクスチャの合成を逆算し、
                //     新色で再合成する。halo（薄汚れた中間色）を構造的に除去する。
                //     詳細は dev_safe/docs/edge_decontamination.md を参照。
                bool[] aaMask = null;
                Color32[] decontaminatedPixels = null;
                if (useDecontamination)
                {
                    DecontaminateAaBoundary(originalPixels, strength, w, h,
                        zone.sampleColor, zone.targetColor,
                        decontaminationRadius, decontaminationInteriorThreshold,
                        out aaMask, out decontaminatedPixels);
                }

                // 4. 強度でブレンドした再色付けを適用
                // target/sample は zone 内で定数なので HSV 変換をループ外で事前計算
                float zTH, zTS, zTV;
                float zSH, zSS, zSV;
                Color.RGBToHSV(zone.targetColor, out zTH, out zTS, out zTV);
                Color.RGBToHSV(zone.sampleColor, out zSH, out zSS, out zSV);
                float zValueBlend = zone.valueBlend;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        float s = strength[i];
                        if (s <= 0.001f) continue;
                        if (aaMask != null && aaMask[i])
                        {
                            // AA pixel: use decontaminated value (overrides standard mix)
                            pixels[i] = decontaminatedPixels[i];
                            continue;
                        }
                        float alpha = originalPixels[i].a / 255f;
                        Color32 recolored = RecolorPixel(pixH[i], pixS[i], pixV[i], alpha, zTH, zTS, zTV, zSH, zSS, zSV, zValueBlend);
                        pixels[i] = s >= 0.999f ? recolored : Color32.Lerp(pixels[i], recolored, s);
                    }
                });

                ArrayPool<float>.Shared.Return(strength);
            } // foreach zone

            } // end try (pixH/S/V)
            finally
            {
                ArrayPool<float>.Shared.Return(pixH);
                ArrayPool<float>.Shared.Return(pixS);
                ArrayPool<float>.Shared.Return(pixV);
            }
        }

        /// <summary>
        /// AA 境界での α 分解 + 再合成（color decontamination / alpha matting）。
        /// 元テクスチャは「pixel = α × FG + (1-α) × BG」で合成されているため、
        /// HSV transfer を直接適用すると AA ピクセル（混色）が薄汚れた中間色になる（halo）。
        /// このメソッドは BG を局所近傍の strength=0 ピクセルから推定し、
        /// α を RGB 空間の射影で計算して、新色 target で再合成する。
        ///
        /// 出力:
        ///   aaMask[i] = true なら pixels[i] を decontaminatedPixels[i] で上書きすべき
        ///   それ以外は通常の HSV transfer にフォールバック
        /// </summary>
        private static void DecontaminateAaBoundary(
            Color32[] originalPixels, float[] strength, int w, int h,
            Color sampleColor, Color targetColor,
            int radius, float interiorThreshold,
            out bool[] aaMask, out Color32[] decontaminatedPixels)
        {
            int len = w * h;
            bool[] localAaMask = new bool[len];
            Color32[] localDecontaminatedPixels = new Color32[len];
            aaMask = localAaMask;
            decontaminatedPixels = localDecontaminatedPixels;

            // 局所 BG 推定: strength=0 のピクセルだけを使った近傍和とその密度
            // 0..255 のスケールで計算（後で divide で平均化）
            float[] wR = ArrayPool<float>.Shared.Rent(len);
            float[] wG = ArrayPool<float>.Shared.Rent(len);
            float[] wB = ArrayPool<float>.Shared.Rent(len);
            float[] wD = ArrayPool<float>.Shared.Rent(len);
            float[] bgRSum = ArrayPool<float>.Shared.Rent(len);
            float[] bgGSum = ArrayPool<float>.Shared.Rent(len);
            float[] bgBSum = ArrayPool<float>.Shared.Rent(len);
            float[] bgDensity = ArrayPool<float>.Shared.Rent(len);
            try
            {
            // Rent はゼロ初期化を保証しないので strength>0 のピクセルを明示的にゼロ化
            Array.Clear(wR, 0, len);
            Array.Clear(wG, 0, len);
            Array.Clear(wB, 0, len);
            Array.Clear(wD, 0, len);
            Parallel.For(0, len, i =>
            {
                // アルファが0のピクセルはRGBがゴミデータ(黒など)の可能性が高いためBG推定から除外
                if (strength[i] <= 0f && originalPixels[i].a > 0)
                {
                    wR[i] = originalPixels[i].r;
                    wG[i] = originalPixels[i].g;
                    wB[i] = originalPixels[i].b;
                    wD[i] = 1f;
                }
            });
            BoxFilterSum(wR, bgRSum, w, h, radius);
            BoxFilterSum(wG, bgGSum, w, h, radius);
            BoxFilterSum(wB, bgBSum, w, h, radius);
            BoxFilterSum(wD, bgDensity, w, h, radius);

            // sample / target を 0..255 スケールに揃える
            float sR = sampleColor.r * 255f;
            float sG = sampleColor.g * 255f;
            float sB = sampleColor.b * 255f;
            float tR = targetColor.r * 255f;
            float tG = targetColor.g * 255f;
            float tB = targetColor.b * 255f;
            const float DegenEps = 1f; // ‖sample - BG‖² 下限（≈1 階調）

            Parallel.For(0, len, i =>
            {
                float s = strength[i];
                if (s <= 0f || s >= interiorThreshold) return;
                float density = bgDensity[i];
                if (density < 1f) return; // 近傍に BG ピクセルなし → fallback

                float bR = bgRSum[i] / density;
                float bG = bgGSum[i] / density;
                float bB = bgBSum[i] / density;

                float dirR = sR - bR;
                float dirG = sG - bG;
                float dirB = sB - bB;
                float dirSq = dirR * dirR + dirG * dirG + dirB * dirB;
                if (dirSq < DegenEps) return; // sample ≈ BG → α が定義できない

                float pR = originalPixels[i].r;
                float pG = originalPixels[i].g;
                float pB = originalPixels[i].b;

                float dot = (pR - bR) * dirR + (pG - bG) * dirG + (pB - bB) * dirB;
                float alpha = dot / dirSq;
                if (alpha < 0f) alpha = 0f;
                else if (alpha > 1f) alpha = 1f;

                // BGとSampleで合成される線分からの距離の2乗を確認。
                // 大きく外れている場合は全く別の色（陰影や別パーツ等）であり、α分解の前提が崩れるためスキップ
                float projR = bR + alpha * dirR;
                float projG = bG + alpha * dirG;
                float projB = bB + alpha * dirB;
                float distSq = (pR - projR) * (pR - projR) + (pG - projG) * (pG - projG) + (pB - projB) * (pB - projB);
                if (distSq > 3000f) return; // 許容誤差。各チャンネル約31のズレまで許容

                float oneMinusAlpha = 1f - alpha;
                float resR = alpha * tR + oneMinusAlpha * bR;
                float resG = alpha * tG + oneMinusAlpha * bG;
                float resB = alpha * tB + oneMinusAlpha * bB;

                localAaMask[i] = true;
                localDecontaminatedPixels[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(resR), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(resG), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(resB), 0, 255),
                    originalPixels[i].a);
            });
            } // end try
            finally
            {
                ArrayPool<float>.Shared.Return(wR);
                ArrayPool<float>.Shared.Return(wG);
                ArrayPool<float>.Shared.Return(wB);
                ArrayPool<float>.Shared.Return(wD);
                ArrayPool<float>.Shared.Return(bgRSum);
                ArrayPool<float>.Shared.Return(bgGSum);
                ArrayPool<float>.Shared.Return(bgBSum);
                ArrayPool<float>.Shared.Return(bgDensity);
            }
        }

        /// <summary>
        /// 分離型ボックス和フィルタ。各ピクセル位置で (2r+1)×(2r+1) 窓内の合計を dst に書き込む
        /// （境界はゼロ拡張：画像外の寄与を 0 として無視）。スライディングウィンドウで O(N) で計算。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        /// </summary>
        private static void BoxFilterSum(float[] src, float[] dst, int w, int h, int r)
        {
            int len = w * h;
            float[] temp = ArrayPool<float>.Shared.Rent(len);
            try
            {
                // 水平パス
                Parallel.For(0, h, y =>
                {
                    int rowOff = y * w;
                    float sum = 0f;
                    int initEnd = Mathf.Min(r, w - 1);
                    for (int k = 0; k <= initEnd; k++) sum += src[rowOff + k];
                    temp[rowOff] = sum;
                    for (int x = 1; x < w; x++)
                    {
                        int subIdx = x - 1 - r;
                        int addIdx = x + r;
                        if (subIdx >= 0) sum -= src[rowOff + subIdx];
                        if (addIdx < w) sum += src[rowOff + addIdx];
                        temp[rowOff + x] = sum;
                    }
                });

                // 垂直パス
                Parallel.For(0, w, x =>
                {
                    float sum = 0f;
                    int initEnd = Mathf.Min(r, h - 1);
                    for (int k = 0; k <= initEnd; k++) sum += temp[k * w + x];
                    dst[x] = sum;
                    for (int y = 1; y < h; y++)
                    {
                        int subIdx = y - 1 - r;
                        int addIdx = y + r;
                        if (subIdx >= 0) sum -= temp[subIdx * w + x];
                        if (addIdx < h) sum += temp[addIdx * w + x];
                        dst[y * w + x] = sum;
                    }
                });
            }
            finally
            {
                ArrayPool<float>.Shared.Return(temp);
            }
        }

        /// <summary>
        /// Flood Fill: シード点から strength &gt; 0 の連続領域のみ残し、残りをゼロ化する。
        /// edgeStopThreshold &gt; 0 のとき、隣接ピクセル間の輝度差・彩度差が閾値を超えると
        /// そこで拡張を止める（エッジストッパー）。
        /// </summary>
        private static void ApplyFloodFillMask(
            float[] strength, float[] pixS, float[] pixV, int w, int h,
            int seedX, int seedY, float edgeStopThreshold)
        {
            int seedIdx = seedY * w + seedX;
            if (strength[seedIdx] <= 0f) return;

            bool[] reachable = new bool[w * h];
            var queue = new Queue<int>();
            reachable[seedIdx] = true;
            queue.Enqueue(seedIdx);

            bool useEdgeStop = edgeStopThreshold > 0f;

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w;
                int y = idx / w;

                float curS = useEdgeStop ? pixS[idx] : 0f;
                float curV = useEdgeStop ? pixV[idx] : 0f;

                // 隣接4方向を試みる
                TryEnqueue(idx - 1, x > 0);
                TryEnqueue(idx + 1, x < w - 1);
                TryEnqueue(idx - w, y > 0);
                TryEnqueue(idx + w, y < h - 1);

                void TryEnqueue(int ni, bool inBounds)
                {
                    if (!inBounds || reachable[ni] || strength[ni] <= 0f) return;

                    if (useEdgeStop)
                    {
                        if (Mathf.Abs(curV - pixV[ni]) > edgeStopThreshold ||
                            Mathf.Abs(curS - pixS[ni]) > edgeStopThreshold * 0.5f)
                            return;
                    }

                    reachable[ni] = true;
                    queue.Enqueue(ni);
                }
            }

            for (int i = 0; i < w * h; i++)
                if (!reachable[i]) strength[i] = 0f;
        }

        /// <summary>
        /// ハイライト候補領域をコア領域から空間伝播させてマスク化する。
        /// strengthの強いピクセル(コア)から、ハイライト候補スコア(highlightPot)を持つ隣接ピクセルへ
        /// strengthを徐々に伝播させ、孤立した白いシャツなどを染めないようにする。
        /// </summary>
        private static void PropagateHighlights(float[] strength, float[] highlightPot, int w, int h)
        {
            int passes = 3;
            for (int p = 0; p < passes; p++)
            {
                bool changed = false;

                // 左上から右下へのパス
                for (int y = 0; y < h; y++)
                {
                    int rowBase = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowBase + x;
                        float pot = highlightPot[i];
                        if (pot > 0f && strength[i] < pot)
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
                }

                // 右下から左上へのパス
                for (int y = h - 1; y >= 0; y--)
                {
                    int rowBase = y * w;
                    for (int x = w - 1; x >= 0; x--)
                    {
                        int i = rowBase + x;
                        float pot = highlightPot[i];
                        if (pot > 0f && strength[i] < pot)
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
                }

                if (!changed) break;
            }
        }

        // 共通マスクとゾーン別マスクを OR 結合した除外判定。
        // どちらか片方でも true ならそのピクセルはこのゾーン処理から除外される。
        private static bool IsExcludedCombined(int x, int y, int texW, int texH,
            bool[] commonMask, bool[] zoneMask, int maskW, int maskH)
        {
            if (commonMask == null && zoneMask == null) return false;
            if (maskW <= 0 || maskH <= 0) return false;
            int mx = Mathf.Clamp(x * maskW / texW, 0, maskW - 1);
            int my = Mathf.Clamp(y * maskH / texH, 0, maskH - 1);
            int idx = my * maskW + mx;
            if (commonMask != null && idx < commonMask.Length && commonMask[idx]) return true;
            if (zoneMask != null && idx < zoneMask.Length && zoneMask[idx]) return true;
            return false;
        }

        /// <summary>
        /// ガウシアンブラーを src に適用して dst に書き込む。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        /// radius &lt; 1 のとき何もしない（dst は未定義のまま）。
        /// 戻り値: ブラー処理を行った場合 true、スキップした場合 false。
        /// </summary>
        private static bool GaussianBlur(float[] src, float[] dst, int w, int h, float sigma)
        {
            int radius = Mathf.CeilToInt(sigma * 2.5f);
            if (radius < 1) return false;

            // 1D カーネルを構築
            float[] kernel = new float[radius * 2 + 1];
            float kernelSum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                kernelSum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= kernelSum;

            int len = w * h;
            float[] temp = ArrayPool<float>.Shared.Rent(len);
            try
            {
                // 水平パス
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int nx = Mathf.Clamp(x + k, 0, w - 1);
                            val += src[y * w + nx] * kernel[k + radius];
                        }
                        temp[y * w + x] = val;
                    }
                });

                // 垂直パス
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int ny = Mathf.Clamp(y + k, 0, h - 1);
                            val += temp[ny * w + x] * kernel[k + radius];
                        }
                        dst[y * w + x] = val;
                    }
                });
            }
            finally
            {
                ArrayPool<float>.Shared.Return(temp);
            }
            return true;
        }

        private static void ConstrainBlur(float[] blurred, float[] original, int w, int h, int radius)
        {
            // マッチがなかった領域へのブラーの流出を防止。
            // original > 0 を float マスクに変換して BoxFilterSum に流すことで
            // 近傍チェックを O(N·r²) から O(N) に削減。
            int len = w * h;
            float[] mask = ArrayPool<float>.Shared.Rent(len);
            float[] neighborSum = ArrayPool<float>.Shared.Rent(len);
            try
            {
                for (int i = 0; i < len; i++)
                    mask[i] = original[i] > 0f ? 1f : 0f;

                BoxFilterSum(mask, neighborSum, w, h, radius);

                Parallel.For(0, len, i =>
                {
                    if (original[i] > 0f) return; // already matched
                    if (neighborSum[i] <= 0f)
                        blurred[i] = 0f;
                });
            }
            finally
            {
                ArrayPool<float>.Shared.Return(mask);
                ArrayPool<float>.Shared.Return(neighborSum);
            }
        }

        /// <summary>
        /// 形態学的フィル：ゼロ強度のピクセルがマッチした隣接ピクセルの多数派に囲まれていれば
        /// 最小隣接強度で埋める。
        /// これにより、satConfidenceゲートを通過するに低い彩度を持つアンチエイリアス処理された
        /// 端のピクセルが原因の孤立した1-2pxドットアーティファクトを除去します。
        /// パス数と最小隣接数はアドバンスモードで調整可能。
        /// </summary>
        /// <remarks>
        /// ダブルバッファリング方式: パスごとの配列クローンを避け、事前確保した
        /// バッファを読み書きで swap することで大テクスチャでのメモリコピーを削減。
        /// </remarks>
        private static void FillSmallHoles(float[] strength, int w, int h,
            int passes = 3, int minNeighbors = 4)
        {
            if (passes <= 0) return;

            int len = w * h;
            float[] buffer = ArrayPool<float>.Shared.Rent(len);
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (read[idx] > 0f) continue;

                        int matched = 0;
                        int total = 0;
                        float minNeighbour = 1f;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                total++;
                                float ns = read[ny * w + nx];
                                if (ns > 0f)
                                {
                                    matched++;
                                    if (ns < minNeighbour) minNeighbour = ns;
                                }
                            }
                        }

                        // 最小隣接数以上のマッチした隣接ピクセルがあれば埋める
                        if (matched >= minNeighbors && total >= minNeighbors)
                            write[idx] = minNeighbour;
                    }
                });

                // read/write を入れ替え
                var tmp = read;
                read = write;
                write = tmp;
            }

            // 最新結果が呼び出し元の strength 配列に入るように調整
            if (!ReferenceEquals(read, strength))
                System.Array.Copy(read, strength, len);
            } // end try
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// 境界復元：少なくとも1つのマッチしたピクセルに隣接するマッチしないピクセルについて
        /// 元の固定低彩度閾値（satMin=0.02, satRamp=0.08）を使用してカラーマッチを再評価します。
        /// これにより、厳格な動的satMinが拒否したアンチエイリアス端のピクセルに対して
        /// 正しい段階的な強度値が得られます。各パスはマッチした境界からさらに1ピクセル
        /// 外側への復元を拡張します。隣接要件により、内部領域でのAO/影のにじみを防ぎます。
        /// </summary>
        private static void RecoverBoundaryEdges(
            float[] strength, int w, int h,
            float[] pixH, float[] pixS, float[] pixV,
            Color sampleColor, float tolerance,
            float edgeSoftness, float valueWeight, float satDistWeight,
            float relaxedSatMin, float relaxedSatRamp, int passes)
        {
            if (passes <= 0) return;

            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            int len = w * h;
            float[] buffer = ArrayPool<float>.Shared.Rent(len);
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (read[idx] > 0f) continue;

                        // Check if adjacent to at least one matched pixel
                        bool hasMatchedNeighbor = false;
                        for (int dy = -1; dy <= 1 && !hasMatchedNeighbor; dy++)
                            for (int dx = -1; dx <= 1 && !hasMatchedNeighbor; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                if (read[ny * w + nx] > 0f) hasMatchedNeighbor = true;
                            }

                        if (!hasMatchedNeighbor) continue;

                        // Re-evaluate this pixel with the relaxed fixed saturation threshold
                        float relaxed = GetRelaxedMatchStrength(
                            pixH[idx], pixS[idx], pixV[idx],
                            sH, sS, sV, tolerance, edgeSoftness, valueWeight,
                            satDistWeight, relaxedSatMin, relaxedSatRamp);
                        if (relaxed > 0f)
                            write[idx] = relaxed;
                    }
                });

                var tmp = read;
                read = write;
                write = tmp;
            }

            if (!ReferenceEquals(read, strength))
                System.Array.Copy(read, strength, len);
            } // end try
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// 緩和された彩度閾値を使用したカラーマッチ。
        /// すでにマッチした領域に隣接する境界ピクセルにのみ使用されます。
        /// アドバンスモードでrelaxedSatMin/relaxedSatRamp/satDistWeightを調整可能。
        /// </summary>
        /// <remarks>
        /// FIX: satConfidence による強度ダンピングを廃止。境界復元はすでにマッチ済みピクセルに
        /// 隣接するピクセルのみが対象なので、「微妙にマッチさせる」より「マッチさせるなら全強度で」
        /// の方が見た目が綺麗。低 satConfidence (例: 0.3) を掛けると AA 縁が部分的に元色を残し、
        /// 白装飾の周囲などにピンク/赤の残留ピクセルが見える原因になっていた。
        /// 純白装飾はそのまま残すため、relaxedSatMin による「最低彩度ゲート」だけは保持する。
        /// relaxedSatRamp は引数互換のため残置（未使用）。
        /// </remarks>
        private static float GetRelaxedMatchStrength(
            float pH, float pS, float pV,
            float sH, float sS, float sV,
            float tolerance, float edgeSoftness, float valueWeight,
            float satDistWeight, float relaxedSatMin, float relaxedSatRamp)
        {
            // 純白装飾はそのまま残す: relaxedSatMin 未満は弾く（ハードゲート）
            if (pS < relaxedSatMin) return 0f;

            float hDist = Mathf.Abs(pH - sH);
            if (hDist > 0.5f) hDist = 1f - hDist;

            // 境界ピクセルはAAブレンディングから低下した彩度を持つと予想される
            float sDist = Mathf.Abs(pS - sS);
            float vDist = Mathf.Abs(pV - sV);
            float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
            float dist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            if (dist >= tolerance) return 0f;

            float softRange = tolerance * edgeSoftness;
            float hardRange = tolerance - softRange;

            float strength;
            if (softRange < 0.0001f)
                strength = 1f;
            else if (dist <= hardRange)
                strength = 1f;
            else
                strength = 1f - (dist - hardRange) / softRange;

            // satConfidence ダンピング廃止 → AA 縁を全強度で再色化
            return strength;
        }

        private static Color32 RecolorPixel(
            float oH, float oS, float oV, float alpha,
            float tH, float tS, float tV,
            float sH, float sS, float sV,
            float valueBlend)
        {
            // 彩度比を保持：アンチエイリアス境界ピクセルは相対的な彩度を保つ
            float newS = (sS > 0.001f) ? Mathf.Clamp01(oS * tS / sS) : tS;

            float newV = Mathf.Lerp(tV, oV, valueBlend);

            // ハイライト合成ロジック: 元のピクセルが白に近く飛んでいるほど、
            // ターゲットカラーの彩度を急激に落とし、明度を引き上げて「オーバーレイ/スクリーン」的な光沢感を維持する。
            if (oV > 0.85f && oS < 0.15f)
            {
                // どれくらい「白飛び」の特性に近いか (0.0 ～ 1.0)
                float hlIntensity = Mathf.Clamp01((oV - 0.85f) / 0.15f) * Mathf.Clamp01(1f - oS / 0.15f);
                newS = Mathf.Lerp(newS, oS, hlIntensity); // 彩度は元の白っぽい状態に逃がす
                newV = Mathf.Lerp(newV, oV, hlIntensity); // 明度はターゲット色等より優先して元の輝度を残す
            }

            Color result = Color.HSVToRGB(tH, newS, newV);
            result.a = alpha;
            return result;
        }
    }
}
