// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// バックグラウンド処理に渡すためのマスク一式のイミュータブルスナップショット。
    /// 配列は deep clone 済み（呼び出し側の書き換えと競合しない）。
    /// </summary>
    internal class MaskSnapshot
    {
        public bool[] common;
        public int width;
        public int height;
        public Dictionary<string, bool[]> zones;
    }

    /// <summary>
    /// テクスチャの再着色アルゴリズム本体。
    /// UnityEditor / EditorWindow / AssetDatabase に依存しない純粋ロジックだけを保持する。
    /// バックグラウンドスレッドからの呼び出しを前提にしている。
    /// </summary>
    internal static class PixelProcessor
    {
        // Parallel.For で使用する、CPU コア数制限。
        // Unity Editor は多数のスレッドを使用するため、全コア並列で
        // スレッドプールを圧迫するのを防ぐため 2 コア分をあけておく。
        private static readonly int s_maxParallelism =
            Math.Max(1, Environment.ProcessorCount - 2);

        // スタティック計算メソッド — バックグラウンドスレッドで実行可能
        // Texture2Dなし、UnityEngine.Object APIなし、Mathfとカラー計算のみ（いずれもスレッドセーフ）
        // originX/Y: フル解像度テクスチャでのクロップオフセット（0で全テクスチャ処理）
        // fullW/H: フル解像度テクスチャの寸法（0 = w/hと同じ、つまりクロップなし）
        // useDecontamination: AA境界でα分解＋再合成を行い halo を除去
        public static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int holeFillPasses = 5, int holeFillMinNeighbors = 4,
            float relaxedSatMin = 0.02f, float relaxedSatRamp = 0.08f,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0,
            bool useDecontamination = true, int decontaminationRadius = 4,
            float decontaminationInteriorThreshold = 0.97f,
            IDebugCapture debug = null)
        {
            ProcessPixelsArray(pixels, w, h, masks, sortedZones, edgeFeather, antiAliasCleanup,
                holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp,
                originX, originY, fullW, fullH, CancellationToken.None,
                useDecontamination, decontaminationRadius, decontaminationInteriorThreshold,
                debug);
        }

        // キャンセルトークン対応バージョン — バックグラウンドプレビューから使用
        public static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int holeFillPasses, int holeFillMinNeighbors,
            float relaxedSatMin, float relaxedSatRamp,
            int originX, int originY, int fullW, int fullH,
            CancellationToken cancellationToken,
            bool useDecontamination = true, int decontaminationRadius = 4,
            float decontaminationInteriorThreshold = 0.97f,
            IDebugCapture debug = null)
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
            // null 初期化してから try 内で Rent することで、
            // 2番目以降の Rent が例外を投げた場合に先行の配列をリークしない。
            float[]? pixH = null, pixS = null, pixV = null;
            try
            {
            pixH = ArrayPool<float>.Shared.Rent(len);
            pixS = ArrayPool<float>.Shared.Rent(len);
            pixV = ArrayPool<float>.Shared.Rent(len);

            // po を HSV 計算 + foreach 内の全 Parallel.For で共用。
            // MaxDegreeOfParallelism で Unity Editor のスレッドプール圧迫を防ぐ。
            var po = new ParallelOptions
            {
                CancellationToken      = cancellationToken,
                MaxDegreeOfParallelism = s_maxParallelism,
            };
            Parallel.For(0, len, po, i =>
            {
                Color.RGBToHSV((Color)originalPixels[i], out pixH[i], out pixS[i], out pixV[i]);
            });

            debug?.BeginCapture(w, h);

            foreach (var zone in sortedZones)
            {
                // キャンセルチェック: 新しいプレビューリクエストが来た場合は即座に中断
                cancellationToken.ThrowIfCancellationRequested();

                // このゾーンに紐付くゾーン別マスクを取得（存在しなければ null）
                bool[] zoneMask = null;
                if (masks != null && masks.zones != null && !string.IsNullOrEmpty(zone.id))
                    masks.zones.TryGetValue(zone.id, out zoneMask);

                // po は foreach 外で定義済みなので再宣言しない。
                // Parallel.For に入る前にキャッシュを確定させてホットループ内の条件分岐を排除
                zone.UpdateCacheIfNeeded();

                // ArrayPool 借用は per-zone の try/finally で必ず返却する。
                // Parallel.For は po.CancellationToken でキャンセル時に OperationCanceledException
                // を投げ、FillSmallHoles 等の内部 Parallel.For 利用も例外を伝播し得るため、
                // 例外経路でもプールが汚染されないよう finally でガードする。
                float[] strength = null;
                float[] highlightPot = null;
                try
                {
                    // 1. 元のピクセルカラーを使用した強度マップを構築
                    strength = ArrayPool<float>.Shared.Rent(len);
                    Array.Clear(strength, 0, len);
                    if (zone.highlightRecovery)
                    {
                        highlightPot = ArrayPool<float>.Shared.Rent(len);
                        Array.Clear(highlightPot, 0, len);
                    }

                    var strengthLocal = strength;
                    var highlightPotLocal = highlightPot;
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
                            strengthLocal[i] = s;
                            if (highlightPotLocal != null) highlightPotLocal[i] = hPot;
                        }
                    });
                    debug?.RecordStage(zone.id, DebugStages.Match, strength, w, h);

                    // 1.a 空間伝播によるハイライト領域の回収 (モルフォロジー拡張)
                    if (highlightPot != null)
                    {
                        PropagateHighlights(strength, highlightPot, w, h);
                        ArrayPool<float>.Shared.Return(highlightPot);
                        highlightPot = null;
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    // 1.a.1 ハイライト帯成長: matched core から「sample→白 軸上の同色相の明部」へ
                    //       strength を空間連結で伸ばし、薄いハイライトのベタ塗り化・取りこぼしを防ぐ。
                    if (zone.highlightBandExpand && zone.highlightRecovery)
                    {
                        GrowHighlightBand(strength, originalPixels, pixH, pixS, pixV, zone, w, h);
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    // 1.a.2 Flood Fill: シード点から連続する領域のみに強度を絞り込む
                    if (VACCConsts.ExperimentalFeatures.EnableFloodFill
                        && zone.mode == SelectionMode.ColorPick
                        && zone.useFloodFill
                        && zone.seedUV.x >= 0f)
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
                        debug?.RecordStage(zone.id, DebugStages.FloodFill, strength, w, h);
                    }

                    // 1b. 孤立した穴を埋める：アンチエイリアス処理された端のピクセルは低彩度を持つことが多く
                    //     satConfidenceで見落とされて、元のカラーの孤立したドットを残す
                    //     ゼロ強度ピクセルが主にマッチしたピクセルに囲まれている場合は埋める
                    FillSmallHoles(strength, w, h, holeFillPasses, holeFillMinNeighbors);
                    debug?.RecordStage(zone.id, DebugStages.HoleFill, strength, w, h);

                    // 1c. 境界復元：マッチしたピクセルに隣接するマッチしないピクセルを再評価
                    //     古い固定低彩度閾値を使用して、正しい段階的な強度を与える
                    if (antiAliasCleanup > 0)
                    {
                        RecoverBoundaryEdges(strength, w, h, pixH, pixS, pixV,
                            zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                            zone.satDistWeight, relaxedSatMin, relaxedSatRamp, zone.shadowForgivenessSatMin, antiAliasCleanup);
                        debug?.RecordStage(zone.id, DebugStages.BoundaryRecover, strength, w, h);
                    }

                    // 2. スムーズな端の遷移のためのガウシアンブラー（端に限定）
                    if (edgeFeather > 0.01f)
                    {
                        // ガウシアンブラー用の一時バッファ。GaussianBlur 内部の Parallel.For
                        // でキャンセルが入っても preBlur/blurOut が漏れないよう try/finally で囲む。
                        float[] preBlur = null;
                        float[] blurOut = null;
                        try
                        {
                            preBlur = ArrayPool<float>.Shared.Rent(len);
                            Array.Copy(strength, preBlur, len);
                            blurOut = ArrayPool<float>.Shared.Rent(len);
                            if (GaussianBlur(strength, blurOut, w, h, edgeFeather))
                            {
                                // strength の所有権を blurOut に移し、もとの strength は返却
                                ArrayPool<float>.Shared.Return(strength);
                                strength = blurOut;
                                blurOut = null; // 二重返却防止
                                ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * 2.5f));
                            }
                        }
                        finally
                        {
                            if (blurOut != null) ArrayPool<float>.Shared.Return(blurOut);
                            if (preBlur != null) ArrayPool<float>.Shared.Return(preBlur);
                        }
                        debug?.RecordStage(zone.id, DebugStages.Blur, strength, w, h);
                    }

                    // 3. 除外マスクを再適用：ブラーが除外ピクセルにはみ出す可能性がある
                    if (commonMask != null || zoneMask != null)
                    {
                        var strengthForReapply = strength;
                        Parallel.For(0, h, po, y =>
                        {
                            int yf = y + originY;
                            int rowOff = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                int xf = x + originX;
                                int i = rowOff + x;
                                if (IsExcludedCombined(xf, yf, fullW, fullH, commonMask, zoneMask, maskW, maskH)) strengthForReapply[i] = 0f;
                            }
                        });
                        debug?.RecordStage(zone.id, DebugStages.MaskReapply, strength, w, h);
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
                        debug?.RecordDecontamination(zone.id, aaMask, w, h);
                    }

                    // 4. 強度でブレンドした再色付けを適用
                    // target/sample は zone 内で定数なので HSV 変換をループ外で事前計算
                    float zTH, zTS, zTV;
                    float zSH, zSS, zSV;
                    Color.RGBToHSV(zone.targetColor, out zTH, out zTS, out zTV);
                    Color.RGBToHSV(zone.sampleColor, out zSH, out zSS, out zSV);
                    // P5' (2026-05-26): ハイライト白方向射影に必要な sample / target RGB を事前取得
                    float zSR = zone.sampleColor.r;
                    float zSG = zone.sampleColor.g;
                    float zSB = zone.sampleColor.b;
                    float zTR = zone.targetColor.r;
                    float zTG = zone.targetColor.g;
                    float zTB = zone.targetColor.b;
                    float zValueBlend = zone.valueBlend;
                    float zOutputSat = zone.outputSaturation;
                    // 俯瞰スポイト補正: ハイライト合成(wash)に使う実効サンプル。テクスチャの地色
                    // (同色相・低V)を自動導出し、明るい所をスポイトしても wash がドーム全体に効く
                    // ようにする。autoHighlightSample=false / 低彩度 / 地色不足のときは sample のまま。
                    Color zWash = HighlightSampleCorrector.ComputeWashSample(
                        originalPixels, pixH, pixS, pixV, w, h, zone);
                    float zWR = zWash.r, zWG = zWash.g, zWB = zWash.b;
                    Color.RGBToHSV(zWash, out _, out _, out float zWV);
                    var strengthForRecolor = strength;
                    var aaMaskLocal = aaMask;
                    var decontaminatedLocal = decontaminatedPixels;
                    Parallel.For(0, h, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = rowOff + x;
                            float s = strengthForRecolor[i];
                            if (s <= 0.001f) continue;
                            if (aaMaskLocal != null && aaMaskLocal[i])
                            {
                                // AA pixel: use decontaminated value (overrides standard mix)
                                pixels[i] = decontaminatedLocal[i];
                                continue;
                            }
                            Color32 op = originalPixels[i];
                            float alpha = op.a / 255f;
                            float oR = op.r / 255f;
                            float oG = op.g / 255f;
                            float oB = op.b / 255f;
                            Color32 recolored = RecolorPixel(
                                oR, oG, oB,
                                pixH[i], pixS[i], pixV[i], alpha,
                                tH: zTH, tS: zTS, tV: zTV,
                                sH: zSH, sS: zSS, sV: zSV,
                                sR: zSR, sG: zSG, sB: zSB,
                                tR: zTR, tG: zTG, tB: zTB,
                                valueBlend: zValueBlend,
                                shadowDesaturation: zone.shadowDesaturation,
                                washR: zWR, washG: zWG, washB: zWB, washV: zWV,
                                outputSaturation: zOutputSat);
                            pixels[i] = s >= 0.999f ? recolored : Color32.Lerp(pixels[i], recolored, s);
                        }
                    });
                    debug?.RecordStage(zone.id, DebugStages.Recolor, strength, w, h);

                    // Recolor 段で各ピクセルに適用されたサブブランチを記録する。
                    // hot loop には分岐を増やさず、debug 有効時だけ追加の Parallel.For で
                    // 上の RecolorPixel 内の条件式を再評価する。
                    // 優先度: Decontaminate > Shadow > Highlight > Base。
                    // shadow と highlight は条件上ほぼ排他（oV<thr と oV>sV）だが念のため shadow を優先。
                    if (debug != null)
                    {
                        byte[] branchMap = new byte[len];
                        float zoneShadowDesat = zone.shadowDesaturation;
                        float zoneSV = zSV;
                        float zoneSS = zSS;
                        var aaMaskForBranch = aaMask;
                        Parallel.For(0, len, po, i =>
                        {
                            if (strengthForRecolor[i] <= 0.001f)
                            {
                                branchMap[i] = (byte)DebugBranch.None;
                                return;
                            }
                            if (aaMaskForBranch != null && aaMaskForBranch[i])
                            {
                                branchMap[i] = (byte)DebugBranch.Decontaminate;
                                return;
                            }
                            float oV = pixV[i];
                            if (zoneShadowDesat > 0f && oV < zoneShadowDesat)
                            {
                                branchMap[i] = (byte)DebugBranch.Shadow;
                                return;
                            }
                            if (zoneSS > 0.01f && oV > zoneSV)
                            {
                                branchMap[i] = (byte)DebugBranch.Highlight;
                                return;
                            }
                            branchMap[i] = (byte)DebugBranch.Base;
                        });
                        debug.RecordRecolorBranches(zone.id, branchMap, w, h);
                    }
                }
                finally
                {
                    if (highlightPot != null) ArrayPool<float>.Shared.Return(highlightPot);
                    if (strength != null) ArrayPool<float>.Shared.Return(strength);
                }
            } // foreach zone

            } // end try (pixH/S/V)
            finally
            {
                if (pixV != null) ArrayPool<float>.Shared.Return(pixV);
                if (pixS != null) ArrayPool<float>.Shared.Return(pixS);
                if (pixH != null) ArrayPool<float>.Shared.Return(pixH);
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
            // null 初期化してから try 内で Rent することでリークを防ぐ。
            float[]? wR = null, wG = null, wB = null, wD = null;
            float[]? bgRSum = null, bgGSum = null, bgBSum = null, bgDensity = null;
            try
            {
            wR = ArrayPool<float>.Shared.Rent(len);
            wG = ArrayPool<float>.Shared.Rent(len);
            wB = ArrayPool<float>.Shared.Rent(len);
            wD = ArrayPool<float>.Shared.Rent(len);
            bgRSum = ArrayPool<float>.Shared.Rent(len);
            bgGSum = ArrayPool<float>.Shared.Rent(len);
            bgBSum = ArrayPool<float>.Shared.Rent(len);
            bgDensity = ArrayPool<float>.Shared.Rent(len);
            // Rent はゼロ初期化を保証しないので strength>0 のピクセルを明示的にゼロ化
            Array.Clear(wR, 0, len);
            Array.Clear(wG, 0, len);
            Array.Clear(wB, 0, len);
            Array.Clear(wD, 0, len);
            var decontamPo = new ParallelOptions { MaxDegreeOfParallelism = s_maxParallelism };
            Parallel.For(0, len, decontamPo, i =>
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

            Parallel.For(0, len, decontamPo, i =>
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
                if (bgDensity != null) ArrayPool<float>.Shared.Return(bgDensity);
                if (bgBSum   != null) ArrayPool<float>.Shared.Return(bgBSum);
                if (bgGSum   != null) ArrayPool<float>.Shared.Return(bgGSum);
                if (bgRSum   != null) ArrayPool<float>.Shared.Return(bgRSum);
                if (wD != null) ArrayPool<float>.Shared.Return(wD);
                if (wB != null) ArrayPool<float>.Shared.Return(wB);
                if (wG != null) ArrayPool<float>.Shared.Return(wG);
                if (wR != null) ArrayPool<float>.Shared.Return(wR);
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
            var filterPo = new ParallelOptions { MaxDegreeOfParallelism = s_maxParallelism };
            try
            {
                // 水平パス
                Parallel.For(0, h, filterPo, y =>
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
                Parallel.For(0, w, filterPo, x =>
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

        // ハイライト帯成長で使用する定数。
        // dev_safe/vacc_python/algorithm.py の HL_BAND_* と同期。
        private const float HlBandCoreThreshold = 0.90f;  // 信頼コア（本体）とみなす strength 下限
        private const float HlBandAxisEps       = 0.10f;  // sample→白 軸からの許容残差（RGB ユークリッド）
        private const float HlBandMinSampleSat  = 0.20f;  // 源色がこれ未満（灰色寄り）なら無効
        private const float HlBandMinSatFrac    = 0.15f;  // 帯候補の彩度下限（源色相対）。白素材への流入を防ぐ

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
        /// 採用画素は strength=1 にし、後段の P5 白寄せで階調を保ったまま再着色する。
        ///
        /// dev_safe/vacc_python/algorithm.py::grow_highlight_band と等価。
        /// </summary>
        private static void GrowHighlightBand(
            float[] strength, Color32[] originalPixels,
            float[] pixH, float[] pixS, float[] pixV, ColorZone zone, int w, int h)
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
            bool[] candidate = new bool[len];
            bool[] visited = new bool[len];
            var queue = new Queue<int>();

            // 候補判定 + core をシードとして収集（1 パス）
            for (int i = 0; i < len; i++)
            {
                float pV = pixV[i];
                if (pV > sV)
                {
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

                if (strength[i] >= HlBandCoreThreshold)
                {
                    visited[i] = true;
                    queue.Enqueue(i);
                }
            }

            if (queue.Count == 0) return;

            // core から候補領域へ 4 連結 BFS（候補セルのみ拡張）
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w;
                int y = idx / w;
                TryVisit(idx - 1, x > 0);
                TryVisit(idx + 1, x < w - 1);
                TryVisit(idx - w, y > 0);
                TryVisit(idx + w, y < h - 1);
            }

            void TryVisit(int ni, bool inBounds)
            {
                if (!inBounds || visited[ni] || !candidate[ni]) return;
                visited[ni] = true;
                if (strength[ni] < 1f) strength[ni] = 1f;
                queue.Enqueue(ni);
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
            var gaussPo = new ParallelOptions { MaxDegreeOfParallelism = s_maxParallelism };
            try
            {
                // 水平パス
                Parallel.For(0, h, gaussPo, y =>
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
                Parallel.For(0, h, gaussPo, y =>
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
            float[]? mask = null, neighborSum = null;
            try
            {
                mask = ArrayPool<float>.Shared.Rent(len);
                neighborSum = ArrayPool<float>.Shared.Rent(len);
                for (int i = 0; i < len; i++)
                    mask[i] = original[i] > 0f ? 1f : 0f;

                BoxFilterSum(mask, neighborSum, w, h, radius);

                Parallel.For(0, len, new ParallelOptions { MaxDegreeOfParallelism = s_maxParallelism }, i =>
                {
                    if (original[i] > 0f) return; // already matched
                    if (neighborSum[i] <= 0f)
                        blurred[i] = 0f;
                });
            }
            finally
            {
                if (neighborSum != null) ArrayPool<float>.Shared.Return(neighborSum);
                if (mask        != null) ArrayPool<float>.Shared.Return(mask);
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
            var fillPo = new ParallelOptions { MaxDegreeOfParallelism = s_maxParallelism };
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(0, h, fillPo, y =>
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
            float relaxedSatMin, float relaxedSatRamp, float shadowForgivenessSatMin, int passes)
        {
            if (passes <= 0) return;

            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            int len = w * h;
            float[] buffer = ArrayPool<float>.Shared.Rent(len);
            var recoverPo = new ParallelOptions { MaxDegreeOfParallelism = s_maxParallelism };
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(0, h, recoverPo, y =>
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
                            satDistWeight, relaxedSatMin, relaxedSatRamp, shadowForgivenessSatMin);
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
            float satDistWeight, float relaxedSatMin, float relaxedSatRamp, float shadowForgivenessSatMin)
        {
            // ColorZone.GetColorMatchScoresと同じ動的頃値：暗いサンプルほどグレースケールモードの範囲を広げる
            float effectiveChromaThreshold = Mathf.Lerp(0.30f, 0.05f, Mathf.Clamp01(sV / 0.20f));

            // 純白装飾はそのまま残す: relaxedSatMin 未満は弾く（ハードゲート）
            // ただしサンプル自体が高彩度の場合のみ適用（暗サンプルの低彩度ピクセルは通過させる）
            if (pS < relaxedSatMin && sS > effectiveChromaThreshold) return 0f;

            // サンプルが暗い型（指定出来ない彩度）の場合: 動的頃値を使って判定
            if (sS <= effectiveChromaThreshold)
            {
                float darknessFactor = Mathf.Clamp01((0.3f - sV) / 0.3f);
                float vDistGray = Mathf.Abs(pV - sV);
                float effectiveDist = Mathf.Lerp(vDistGray, pS, darknessFactor);
                if (effectiveDist >= tolerance) return 0f;
                float sr = tolerance * edgeSoftness;
                float hr = tolerance - sr;
                if (sr < 0.0001f || effectiveDist <= hr) return 1f;
                return 1f - (effectiveDist - hr) / sr;
            }

            float hDist = Mathf.Abs(pH - sH);
            if (hDist > 0.5f) hDist = 1f - hDist;

            // 境界ピクセルはAAブレンディングから低下した彩度を持つと予想される
            float sDist = Mathf.Abs(pS - sS);
            float vDist = Mathf.Abs(pV - sV);
            float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
            float dist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            // シャドウ（暗い色）の境界許容:
            // パキッとした影やMultiplyで暗くなった境界部分は、ベース色と同じ色相でも明度や彩度が大きく落ち、
            // 距離ペナルティがToleranceを超えて取り残されることがあるため、色相が近い暗部は距離を減免する。
            if (pV < sV * 0.75f && hDist < 0.15f && pS >= shadowForgivenessSatMin)
            {
                float darkForgiveness = Mathf.Clamp01((sV * 0.75f - pV) / (sV * 0.6f));
                dist *= Mathf.Lerp(1f, 0.2f, darkForgiveness); // 暗いほど距離を最大70%免除
            }

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
            float oR, float oG, float oB,
            float oH, float oS, float oV, float alpha,
            float tH, float tS, float tV,
            float sH, float sS, float sV,
            float sR, float sG, float sB,
            float tR, float tG, float tB,
            float valueBlend, float shadowDesaturation,
            float washR, float washG, float washB, float washV,
            float outputSaturation = 1f)
        {
            // 彩度比を保持：アンチエイリアス境界ピクセルは相対的な彩度を保つ
            float newS = (sS > 0.001f) ? Mathf.Clamp01(oS * tS / sS) : tS;

            // 出力彩度スケール: 純色 target で潰れる明度グラデーションを、比例的な脱彩で取り戻す。
            // 1.0 では従来と完全一致。1.0未満で全体の彩度を比例的に下げ立体感を出す。
            if (outputSaturation < 0.999f) newS *= outputSaturation;

            // 相対明度保持（オフセット式）: サンプル明度 sV を基準点に、元画素の
            // 陰影・段差成分 (oV - sV) はそのまま温存し、全体トーンだけを valueBlend で
            // ターゲット側へ寄せる。これにより d(newV)/d(oV)=1 となり、手描きの
            // 多段シェーディング（明るい段）が valueBlend<1 でも潰れない。
            // valueBlend=1.0 では baseV=sV, newV=sV+(oV-sV)=oV ＝従来 Lerp(tV,oV,1) と完全一致。
            float shadingOffset = oV - sV;
            float baseV = Mathf.Lerp(tV, sV, valueBlend);
            float newV = Mathf.Clamp01(baseV + shadingOffset);

            // シャドウ（暗い部分）の彩度の保護ロジック:
            // 黒や極端に暗いピクセルはHSV変換でおかしな色になりやすいため、暗さに応じて彩度を0に近づける
            if (shadowDesaturation > 0f && oV < shadowDesaturation)
            {
                // 0.3以下から一気に暗い色として扱う
                float shadowIntensity = Mathf.Clamp01((shadowDesaturation - oV) / shadowDesaturation);
                newS = Mathf.Lerp(newS, Mathf.Min(oS, newS * 0.5f), shadowIntensity); // 暗い部分の彩度をさらに抑える
            }
            Color result = Color.HSVToRGB(tH, newS, newV);

            // ハイライト合成 (P5 軸射影版・residual なし / 2026-05-26):
            // ピクセルを「sample → (1,1,1) 白直線」に射影して進行度 w を取り、
            // 同じ w を使って target 軸上の対応点を求める。residual (軸からのずれ) は
            // 捨てる — これにより青の色相歪みが target 側 (赤) にコピーされてピンク化する
            // P5 純正版の副作用を構造的に除去する。
            //
            //   proj_t = target + w * (white - target)
            //   result = Lerp(hsv_result, proj_t, valRise)
            //
            // 直感: pixel が sample から白に向けて 0.99 進んでいるなら (ハイライト中心)、
            //       target からも白に向けて 0.99 進んだ点が出力色。中心は完全な白では
            //       なく「target に向けて 1% 染まった白」(= わずかに赤い白)。
            //       周辺 (w=0.5) は target と white の中間 (例: 赤と白で薄い赤)。
            //
            // 境界 (oV ≤ sV) は valRise=0 で hsv_result に切り戻すため不連続なし。
            // wash 用サンプル(washR/G/B/V): 既定は match と同じ sample。俯瞰スポイト補正では
            // パーツ地色(同色相・低V)が渡され、ハイライト合成 (oV>washV) がドーム全体に効く。
            // match/base は sample のままなので再着色範囲は不変(新規 FP なし)。
            if (sS > 0.01f && oV > washV)
            {
                float dR = 1f - washR;
                float dG = 1f - washG;
                float dB = 1f - washB;
                float dirSq = dR * dR + dG * dG + dB * dB;
                if (dirSq > 1e-6f)
                {
                    float pR = oR - washR;
                    float pG = oG - washG;
                    float pB = oB - washB;
                    float w = Mathf.Clamp01((pR * dR + pG * dG + pB * dB) / dirSq);

                    // target 軸上の対応点 (residual は捨てる)
                    float projTR = tR + w * (1f - tR);
                    float projTG = tG + w * (1f - tG);
                    float projTB = tB + w * (1f - tB);

                    // 境界連続性のため valRise でフェード (oV=washV で valRise=0、hsv_result に戻る)
                    float valRise = Mathf.Clamp01((oV - washV) / Mathf.Max(0.05f, 1f - washV));
                    result.r = Mathf.Lerp(result.r, projTR, valRise);
                    result.g = Mathf.Lerp(result.g, projTG, valRise);
                    result.b = Mathf.Lerp(result.b, projTB, valRise);
                }
            }

            result.a = alpha;
            return result;
        }

        /// <summary>
        /// プレビュー表示用にダウンサンプルした Color32 配列を生成する。
        /// 各 dst 画素は対応する src ブロック内のピクセル平均色になる（box フィルタ）。
        /// </summary>
        public static Color32[] BoxDownsample(Color32[] src, int srcW, int srcH,
            int dstW, int dstH, float scale)
        {
            Color32[] dst = new Color32[dstW * dstH];
            // 合計値が int の範囲を超えないように long を使用。
            // 例: 8192x8192 の画像を 512x512 に縮小すると 1 ピクセル当たり 256 サンプル以上、
            // 合計が byte(255) * 256 = 65280 を超え、バッチサイズ次第では int でも桁数が
            // 増えるため安全側に倒す。
            for (int y = 0; y < dstH; y++)
            {
                int sy0 = Mathf.FloorToInt(y / scale);
                int sy1 = Mathf.Min(Mathf.CeilToInt((y + 1f) / scale) - 1, srcH - 1);
                for (int x = 0; x < dstW; x++)
                {
                    int sx0 = Mathf.FloorToInt(x / scale);
                    int sx1 = Mathf.Min(Mathf.CeilToInt((x + 1f) / scale) - 1, srcW - 1);
                    long r = 0, g = 0, b = 0, a = 0;
                    int count = 0;
                    for (int ky = sy0; ky <= sy1; ky++)
                        for (int kx = sx0; kx <= sx1; kx++)
                        {
                            var p = src[ky * srcW + kx];
                            r += p.r; g += p.g; b += p.b; a += p.a;
                            count++;
                        }
                    if (count <= 0) count = 1; // ゼロ除算ガード（理論上は到達しないが念のため）
                    dst[y * dstW + x] = new Color32(
                        (byte)(r / count), (byte)(g / count),
                        (byte)(b / count), (byte)(a / count));
                }
            }
            return dst;
        }
    }
}
