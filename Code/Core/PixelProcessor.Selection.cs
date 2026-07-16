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
    // PixelProcessor: 選択リファイン後段(ボックス和/ブラー/bbox/穴埋め/境界回復/緩和マッチ)。
    internal static partial class PixelProcessor
    {
        /// <summary>
        /// 分離型ボックス和フィルタ。各ピクセル位置で (2r+1)×(2r+1) 窓内の合計を dst に書き込む
        /// （境界はゼロ拡張：画像外の寄与を 0 として無視）。スライディングウィンドウで O(N) で計算。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        /// </summary>
        private static void BoxFilterSum(float[] src, float[] dst, int w, int h, int r, CancellationToken ct = default)
        {
            int len = w * h;
            float[] temp = s_floatPool.Rent(len);
            var filterPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
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
                s_floatPool.Return(temp);
            }
        }

        /// <summary>
        /// ガウシアンブラーを src に適用して dst に書き込む。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        /// radius &lt; 1 のとき何もしない（dst は未定義のまま）。
        /// 戻り値: ブラー処理を行った場合 true、スキップした場合 false。
        /// </summary>
        private static bool GaussianBlur(float[] src, float[] dst, int w, int h, float sigma,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            CancellationToken ct = default)
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
            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけブラーする(P2-7)。矩形は
            // 呼び出し側が「src の非0 が矩形より radius 以上内側」を保証するため、矩形外の真のブラー出力は
            // 常に 0。dst を全クリアしておけば矩形内だけ計算しても全画素ブラーと出力ビット一致する。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            float[] temp = s_floatPool.Rent(len);
            var gaussPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
                Array.Clear(dst, 0, len);   // 矩形外は 0(全画素ブラーの src=0 領域と一致)
                // 垂直パスが読む行は [boxMinY-radius, boxMaxY+radius] なので、水平パスはその行範囲ぶん
                // 上下に広げて temp を用意する(列は矩形のまま。垂直は矩形列しか読まない)。
                int hMinY = Mathf.Max(0, boxMinY - radius);
                int hMaxY = Mathf.Min(h - 1, boxMaxY + radius);
                // 水平パス
                Parallel.For(hMinY, hMaxY + 1, gaussPo, y =>
                {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int nx = Mathf.Clamp(x + k, 0, w - 1);
                            val += src[rb + nx] * kernel[k + radius];
                        }
                        temp[rb + x] = val;
                    }
                });

                // 垂直パス
                Parallel.For(boxMinY, boxMaxY + 1, gaussPo, y =>
                {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int ny = Mathf.Clamp(y + k, 0, h - 1);
                            val += temp[ny * w + x] * kernel[k + radius];
                        }
                        dst[rb + x] = val;
                    }
                });
            }
            finally
            {
                s_floatPool.Return(temp);
            }
            return true;
        }

        private static void ConstrainBlur(float[] blurred, float[] original, int w, int h, int radius, CancellationToken ct = default)
        {
            // マッチがなかった領域へのブラーの流出を防止。
            // original > 0 を float マスクに変換して BoxFilterSum に流すことで
            // 近傍チェックを O(N·r²) から O(N) に削減。
            int len = w * h;
            float[]? mask = null, neighborSum = null;
            try
            {
                mask = s_floatPool.Rent(len);
                neighborSum = s_floatPool.Rent(len);
                for (int i = 0; i < len; i++)
                    mask[i] = original[i] > 0f ? 1f : 0f;

                BoxFilterSum(mask, neighborSum, w, h, radius, ct);

                Parallel.For(0, len, new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct }, i =>
                {
                    if (original[i] > 0f) return; // already matched
                    if (neighborSum[i] <= 0f)
                        blurred[i] = 0f;
                });
            }
            finally
            {
                if (neighborSum != null) s_floatPool.Return(neighborSum);
                if (mask        != null) s_floatPool.Return(mask);
            }
        }

        /// <summary>
        /// strength &gt; thr の画素を囲むバウンディングボックス(min/max)を求める。
        /// 後段パス(穴埋め/境界回復/ブラー)を実マッチ範囲＋余白に限定し、全画素走査を避けるために使う。
        /// </summary>
        /// <returns>マッチ画素が 1 つ以上あれば true(false のとき bbox は空で、後段パスは no-op)。</returns>
        private static bool TryComputeStrengthBBox(float[] strength, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = w; minY = h; maxX = -1; maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rb = y * w;
                int rowMinX = -1, rowMaxX = -1;
                for (int x = 0; x < w; x++)
                {
                    if (strength[rb + x] > thr)
                    {
                        if (rowMinX < 0) rowMinX = x;
                        rowMaxX = x;
                    }
                }
                if (rowMaxX >= 0)
                {
                    if (rowMinX < minX) minX = rowMinX;
                    if (rowMaxX > maxX) maxX = rowMaxX;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX >= 0;
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
            int passes = 3, int minNeighbors = 4, bool[] allowedMask = null,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            CancellationToken ct = default)
        {
            if (passes <= 0) return;

            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけ近傍走査する(P2-7)。
            // 矩形外は呼び出し側が「処理前後とも 0」を保証するので走査を省いても出力ビット不変。
            // Array.Copy(全画素)は安価なので残し、近傍を読む重いループだけを矩形に絞る。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            int len = w * h;
            float[] buffer = s_floatPool.Rent(len);
            var fillPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(boxMinY, boxMaxY + 1, fillPo, y =>
                {
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        int idx = y * w + x;
                        if (read[idx] > 0f) continue;
                        // relaxed ゲート: 画素自身が relaxed マッチを通る色でなければ埋めない。
                        // マッチ領域に囲まれただけの背景グレー/白を full strength に塗らないことで、
                        // 薄いロゴ周辺のフリンジ(白/灰ノイズ)を構造的に防ぐ。
                        if (allowedMask != null && !allowedMask[idx]) continue;

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
                s_floatPool.Return(buffer);
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
            float relaxedSatMin, float relaxedSatRamp, float shadowForgivenessSatMin, int passes,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            Color32[] originalPixels = null, float chromaConfidence = 1f, float chromaThreshold = 0.05f,
            CancellationToken ct = default)
        {
            if (passes <= 0) return;

            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけ走査する(P2-7、出力ビット不変)。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);
            // WS-M: relaxed ゲートの RGB ブレンド用 sample RGB(0..1)。
            float rcSampR = sampleColor.r, rcSampG = sampleColor.g, rcSampB = sampleColor.b;

            int len = w * h;
            float[] buffer = s_floatPool.Rent(len);
            var recoverPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(boxMinY, boxMaxY + 1, recoverPo, y =>
                {
                    for (int x = boxMinX; x <= boxMaxX; x++)
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
                        float rpR = 0f, rpG = 0f, rpB = 0f;
                        if (originalPixels != null)
                        {
                            Color32 rop = originalPixels[idx];
                            rpR = rop.r / 255f; rpG = rop.g / 255f; rpB = rop.b / 255f;
                        }
                        float relaxed = GetRelaxedMatchStrength(
                            pixH[idx], pixS[idx], pixV[idx],
                            sH, sS, sV, tolerance, edgeSoftness, valueWeight,
                            satDistWeight, relaxedSatMin, relaxedSatRamp, shadowForgivenessSatMin,
                            rpR, rpG, rpB, rcSampR, rcSampG, rcSampB, chromaConfidence, chromaThreshold);
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
                s_floatPool.Return(buffer);
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
            float satDistWeight, float relaxedSatMin, float relaxedSatRamp, float shadowForgivenessSatMin,
            float pR = 0f, float pG = 0f, float pB = 0f,
            float sR = 0f, float sG = 0f, float sB = 0f, float chromaConfidence = 1f,
            float chromaThreshold = 0.05f)
        {
            // ColorZone.MatchOneSample と同じ動的しきい値：暗いサンプルほどグレースケールモードの範囲を広げる。
            // 上端は zone.chromaThreshold(ユーザー可変)を使う。以前は既定値 0.05 を焼き込んでいたため、
            // ユーザーが chromaThreshold を変えると主経路と穴埋め/境界回復でグレーモード判定が食い違っていた。
            float effectiveChromaThreshold = Mathf.Lerp(ColorZone.GrayModeBaseChromaThreshold, chromaThreshold, Mathf.Clamp01(sV / ColorZone.GrayModeChromaConfidenceRamp));

            // 純白装飾はそのまま残す: relaxedSatMin 未満は弾く（ハードゲート）
            // ただしサンプル自体が高彩度の場合のみ適用（暗サンプルの低彩度ピクセルは通過させる）
            if (pS < relaxedSatMin && sS > effectiveChromaThreshold) return 0f;

            // サンプルが暗い型（指定出来ない彩度）の場合: 動的頃値を使って判定
            if (sS <= effectiveChromaThreshold)
            {
                // 主経路(GetColorMatchScores グレーモード)と同じ RGB 距離で判定する。
                // 旧実装は値距離 |pV-sV| のみで、明るいサンプルでは色に関係なく「明るい」だけで
                // 一致したため、珊瑚やバンダナ縁(salmon→白)が境界回復/穴埋めで黒く滲み、三角の
                // 元領域を超えて黒がはみ出していた。RGB 距離なら主経路と同じく珊瑚(距離>tol)を拒否し、
                // 三角自身の AA 縁(cream 寄り)だけを回復する。暗サンプルでは Lerp で pS へ収束=従来同等。
                float dr = pR - sR, dg = pG - sG, db = pB - sB;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f;
                float darknessFactor = Mathf.Clamp01((ColorZone.GrayModeDarkSampleValue - sV) / ColorZone.GrayModeDarkSampleValue);
                float effectiveDist = Mathf.Lerp(rgbDist, pS, darknessFactor);
                // 輝度盲対策(A-6): 純黒サンプルで純白まで距離0マッチするのを防ぐ。ヘッドルーム超えの
                // 明るさに輝度超過ペナルティを加える。ColorZone.MatchOneSample のグレーモードと同期。
                float lumExcess = Mathf.Max(0f, (pV - sV) - ColorZone.GrayHighlightHeadroom);
                effectiveDist += lumExcess * ColorZone.GrayLumExcessWeight;
                // 彩度整合ゲート(GetColorMatchScores のグレーモードと同じ)。サンプルが微小な tint を
                // 持つとき、それより著しく中性寄りの候補(純白 UV 背景等)を距離加算でソフト排除する。
                // 明るいクリームサンプルでは値距離だと純白(pV≈sV)が一致するため、ここでも必要。
                // sS≈0(真の無彩サンプル)では作動しない=従来挙動を維持。明部限定(gateWeight)で
                // 暗いサンプル(中性が正常)では矛盾を避けフェードさせる。ColorZone.cs と同期。
                if (sS > ColorZone.ChromaGateActivateSat)
                {
                    float gateWeight = Mathf.Clamp01(sV / ColorZone.GrayModeDarkSampleValue);
                    float satFloor = sS * ColorZone.ChromaGateFloorFrac;
                    float shortfall = Mathf.Clamp01((satFloor - pS) / Mathf.Max(satFloor, 1e-4f));
                    effectiveDist += shortfall * ColorZone.ChromaGatePenalty * tolerance * gateWeight;
                }
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

            // WS-M (2026-06-14): 低彩度サンプル(白/灰)では HSV 距離が hue 支配になり、同色相の
            // 高彩度色(off-white→赤バンダナ等)を弾けず境界回復が別色を周囲へ大量スピルさせる。
            // プライマリ(CalculateHybridDistance)と同じ RGB 距離ブレンドで整合させる:
            // dist = lerp(rgbDist, hsvDist, chromaConfidence)。有彩サンプルは cc≈1 で従来式と一致。
            if (chromaConfidence < 0.999f)
            {
                float dr = pR - sR, dg = pG - sG, db = pB - sB;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f;
                dist = rgbDist * (1f - chromaConfidence) + dist * chromaConfidence;
            }

            // シャドウ（暗い色）の境界距離許容は廃止（プライマリ CalculateHybridDistance と同期）。
            // 緩和マッチ(穴埋め/境界回復)で near-black の別マテリアルを tolerance 内へ逆送し
            // 巻き込みを広げる経路だったため除去する。

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
    }
}
