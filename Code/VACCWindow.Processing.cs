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

            foreach (var zone in sortedZones)
            {
                // キャンセルチェック: 新しいプレビューリクエストが来た場合は即座に中断
                cancellationToken.ThrowIfCancellationRequested();

                // このゾーンに紐付くゾーン別マスクを取得（存在しなければ null）
                bool[] zoneMask = null;
                if (masks != null && masks.zones != null && !string.IsNullOrEmpty(zone.id))
                    masks.zones.TryGetValue(zone.id, out zoneMask);

                var po = new ParallelOptions { CancellationToken = cancellationToken };

                // 1. 元のピクセルカラーを使用した強度マップを構築
                float[] strength = new float[len];
                float[] highlightPot = zone.highlightRecovery ? new float[len] : null;

                Parallel.For(0, len, po, i =>
                {
                    int x = i % w, y = i / w;
                    int xf = x + originX, yf = y + originY;
                    if (IsExcludedCombined(xf, yf, fullW, fullH, commonMask, zoneMask, maskW, maskH)) return;
                    
                    float s, hPot;
                    zone.GetMatchScores((Color)originalPixels[i], xf, yf, fullW, fullH, out s, out hPot);
                    strength[i] = s;
                    if (highlightPot != null) highlightPot[i] = hPot;
                });

                // 1.a 空間伝播によるハイライト領域の回収 (モルフォロジー拡張)
                if (highlightPot != null)
                {
                    PropagateHighlights(strength, highlightPot, w, h);
                }

                // 1b. 孤立した穴を埋める：アンチエイリアス処理された端のピクセルは低彩度を持つことが多く
                //     satConfidenceで見落とされて、元のカラーの孤立したドットを残す
                //     ゼロ強度ピクセルが主にマッチしたピクセルに囲まれている場合は埋める
                FillSmallHoles(strength, w, h, holeFillPasses, holeFillMinNeighbors);

                // 1c. 境界復元：マッチしたピクセルに隣接するマッチしないピクセルを再評価
                //     古い固定低彩度閾値を使用して、正しい段階的な強度を与える
                if (antiAliasCleanup > 0)
                    RecoverBoundaryEdges(strength, w, h, originalPixels,
                        zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                        zone.satDistWeight, relaxedSatMin, relaxedSatRamp, antiAliasCleanup);

                // 2. スムーズな端の遷移のためのガウシアンブラー（端に限定）
                if (edgeFeather > 0.01f)
                {
                    float[] preBlur = (float[])strength.Clone();
                    strength = GaussianBlur(strength, w, h, edgeFeather);
                    ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * 2.5f));
                }

                // 3. 除外マスクを再適用：ブラーが除外ピクセルにはみ出す可能性がある
                if (commonMask != null || zoneMask != null)
                {
                    Parallel.For(0, len, po, i =>
                    {
                        int x = i % w, y = i / w;
                        int xf = x + originX, yf = y + originY;
                        if (IsExcludedCombined(xf, yf, fullW, fullH, commonMask, zoneMask, maskW, maskH)) strength[i] = 0f;
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
                Parallel.For(0, len, po, i =>
                {
                    float s = strength[i];
                    if (s <= 0.001f) return;
                    if (aaMask != null && aaMask[i])
                    {
                        // AA pixel: use decontaminated value (overrides standard mix)
                        pixels[i] = decontaminatedPixels[i];
                        return;
                    }
                    Color original = originalPixels[i];
                    Color32 recolored = RecolorPixel(original, zone.targetColor, zone.sampleColor, zone.valueBlend);
                    pixels[i] = s >= 0.999f ? recolored : Color32.Lerp(pixels[i], recolored, s);
                });
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
            float[] wR = new float[len];
            float[] wG = new float[len];
            float[] wB = new float[len];
            float[] wD = new float[len];
            Parallel.For(0, len, i =>
            {
                if (strength[i] <= 0f)
                {
                    wR[i] = originalPixels[i].r;
                    wG[i] = originalPixels[i].g;
                    wB[i] = originalPixels[i].b;
                    wD[i] = 1f;
                }
            });
            float[] bgRSum = BoxFilterSum(wR, w, h, radius);
            float[] bgGSum = BoxFilterSum(wG, w, h, radius);
            float[] bgBSum = BoxFilterSum(wB, w, h, radius);
            float[] bgDensity = BoxFilterSum(wD, w, h, radius);

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
        }

        /// <summary>
        /// 分離型ボックス和フィルタ。各ピクセル位置で (2r+1)×(2r+1) 窓内の合計を返す
        /// （境界はゼロ拡張：画像外の寄与を 0 として無視）。スライディングウィンドウで O(N) で計算。
        /// </summary>
        private static float[] BoxFilterSum(float[] src, int w, int h, int r)
        {
            // 水平パス
            float[] temp = new float[w * h];
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
            float[] result = new float[w * h];
            Parallel.For(0, w, x =>
            {
                float sum = 0f;
                int initEnd = Mathf.Min(r, h - 1);
                for (int k = 0; k <= initEnd; k++) sum += temp[k * w + x];
                result[x] = sum;
                for (int y = 1; y < h; y++)
                {
                    int subIdx = y - 1 - r;
                    int addIdx = y + r;
                    if (subIdx >= 0) sum -= temp[subIdx * w + x];
                    if (addIdx < h) sum += temp[addIdx * w + x];
                    result[y * w + x] = sum;
                }
            });
            return result;
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

        private static float[] GaussianBlur(float[] map, int w, int h, float sigma)
        {
            int radius = Mathf.CeilToInt(sigma * 2.5f);
            if (radius < 1) return map;

            // 1D カーネルを構築
            float[] kernel = new float[radius * 2 + 1];
            float sum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                sum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= sum;

            // 水平パス
            float[] temp = new float[w * h];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float val = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int nx = Mathf.Clamp(x + k, 0, w - 1);
                        val += map[y * w + nx] * kernel[k + radius];
                    }
                    temp[y * w + x] = val;
                }
            });

            // 垂直パス
            float[] result = new float[w * h];
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
                    result[y * w + x] = val;
                }
            });

            return result;
        }

        private static void ConstrainBlur(float[] blurred, float[] original, int w, int h, int radius)
        {
            // マッチがなかった領域へのブラーの流出を防止
            // このピクセルもブラー半径内の隣接ピクセルも、元の強度 > 0 がなければ
            // ブラー値をゼロに
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (original[idx] > 0f) continue; // was already matched

                    bool hasNearby = false;
                    int yMin = Mathf.Max(0, y - radius);
                    int yMax = Mathf.Min(h - 1, y + radius);
                    int xMin = Mathf.Max(0, x - radius);
                    int xMax = Mathf.Min(w - 1, x + radius);

                    for (int ny = yMin; ny <= yMax && !hasNearby; ny++)
                    {
                        for (int nx = xMin; nx <= xMax && !hasNearby; nx++)
                        {
                            if (original[ny * w + nx] > 0f)
                                hasNearby = true;
                        }
                    }

                    if (!hasNearby)
                        blurred[idx] = 0f;
                }
            });
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

            // 事前に 1 つだけ作業バッファを確保し、パスごとに入れ替えて利用する
            float[] buffer = new float[strength.Length];
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                // 既存の値を write にコピーしておき、変更対象のピクセルのみを上書きする
                System.Array.Copy(read, write, read.Length);

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
                System.Array.Copy(read, strength, strength.Length);
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
            Color32[] pixels, Color sampleColor, float tolerance,
            float edgeSoftness, float valueWeight, float satDistWeight,
            float relaxedSatMin, float relaxedSatRamp, int passes)
        {
            if (passes <= 0) return;

            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            // ダブルバッファリング: パスごとのクローンを避ける
            float[] buffer = new float[strength.Length];
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, read.Length);

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
                            (Color)pixels[idx], sH, sS, sV, tolerance, edgeSoftness, valueWeight,
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
                System.Array.Copy(read, strength, strength.Length);
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
            Color pixelColor, float sH, float sS, float sV,
            float tolerance, float edgeSoftness, float valueWeight,
            float satDistWeight, float relaxedSatMin, float relaxedSatRamp)
        {
            float pH, pS, pV;
            Color.RGBToHSV(pixelColor, out pH, out pS, out pV);

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

        private static Color32 RecolorPixel(Color original, Color target, Color sample, float valueBlend)
        {
            float oH, oS, oV;
            float tH, tS, tV;
            float sH, sS, sV;
            Color.RGBToHSV(original, out oH, out oS, out oV);
            Color.RGBToHSV(target,   out tH, out tS, out tV);
            Color.RGBToHSV(sample,   out sH, out sS, out sV);

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
            result.a = original.a;
            return result;
        }
    }
}
