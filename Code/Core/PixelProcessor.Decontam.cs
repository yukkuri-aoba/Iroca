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
    // PixelProcessor: AA 境界の α 分解 + 再合成(color decontamination)と無彩フチ消し。
    internal static partial class PixelProcessor
    {
        // 無彩フチ消し(CleanAchromaFringe)の定数。マッチ境界の外側に残る「地色の残った」混色画素
        // (背景より地色寄り=明るく、暗い再着色色に対し明るいフチに見える)を α 分解で背景へ寄せて消す。
        private const int AchromaFringeMatchRadius = 2;    // マッチ境界からこの px 以内の外側を対象
        private const float AchromaFringeMinAlpha = 0.05f; // これ未満=地色の残りがほぼ無い→触らない
        private const float AchromaFringeMaxAlpha = 0.70f; // これ超=地色寄り→除外(白拒否を維持)

        // 弱AA画素を「選択領域の内部」とみなして strength=1 に固める背景密度のしきい(窓面積比)。
        // 旧実装は「窓内に背景ドナーが1画素でもあれば α 分解」だったため、淡 tint 布地の内部に
        // 点在するごく少数の未選択画素(彩度整合ゲートの残余等、窓内密度 ~2%)が偽の背景ドナーに
        // なり、布の内部を「背景との AA 混色」として α 再合成→明るい斑点ノイズになっていた。
        // 真の AA 境界は片側が背景に接するため窓内密度が高い(細い1px背景スリットでも
        // ≈1/(2r+1)=14% @r=3)。この比率未満は背景不在=内部と判定して完全再着色に固める。
        // テクスチャ非依存の幾何比率であり特定素材への較正ではない。
        private const float DecontamInteriorBgFrac = 0.08f;

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
            bool[] aaMask, Color32[] decontaminatedPixels, bool hasMatch = true,
            CancellationToken ct = default)
        {
            int len = w * h;
            // 呼び出し側がゾーン間で再利用するバッファを渡す。aaMask は全画素で読まれるため
            // 前ゾーンの結果をクリアしてから書き込む。decontaminatedPixels は aaMask=true の
            // 位置だけ下で上書きされ、その位置だけ参照されるためクリア不要。
            Array.Clear(aaMask, 0, len);
            // マッチ皆無(strength>0 が無い)なら AA 境界画素も存在しないので、4ch BG 推定
            // (BoxFilterSum)を丸ごと省く。aaMask は上でクリア済み=出力ビット不変。
            if (!hasMatch) return;
            bool[] localAaMask = aaMask;
            Color32[] localDecontaminatedPixels = decontaminatedPixels;

            // 局所 BG 推定: strength=0 のピクセルだけを使った近傍和とその密度
            // 0..255 のスケールで計算（後で divide で平均化）
            // null 初期化してから try 内で Rent することでリークを防ぐ。
            float[]? wR = null, wG = null, wB = null, wD = null;
            float[]? bgRSum = null, bgGSum = null, bgBSum = null, bgDensity = null;
            try
            {
            wR = s_floatPool.Rent(len);
            wG = s_floatPool.Rent(len);
            wB = s_floatPool.Rent(len);
            wD = s_floatPool.Rent(len);
            bgRSum = s_floatPool.Rent(len);
            bgGSum = s_floatPool.Rent(len);
            bgBSum = s_floatPool.Rent(len);
            bgDensity = s_floatPool.Rent(len);
            // Rent はゼロ初期化を保証しないので strength>0 のピクセルを明示的にゼロ化
            Array.Clear(wR, 0, len);
            Array.Clear(wG, 0, len);
            Array.Clear(wB, 0, len);
            Array.Clear(wD, 0, len);
            var decontamPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
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
            // BG 推定(R/G/B/density)。各 ch を順に処理する(融合版は temp ストリームが 4 本同時に
            // なりメモリ帯域律速のこの処理ではキャッシュスラッシングで遅くなったため単一版に戻した)。
            BoxFilterSum(wR, bgRSum, w, h, radius, ct);
            BoxFilterSum(wG, bgGSum, w, h, radius, ct);
            BoxFilterSum(wB, bgBSum, w, h, radius, ct);
            BoxFilterSum(wD, bgDensity, w, h, radius, ct);

            // sample / target を 0..255 スケールに揃える
            float sR = sampleColor.r * 255f;
            float sG = sampleColor.g * 255f;
            float sB = sampleColor.b * 255f;
            float tR = targetColor.r * 255f;
            float tG = targetColor.g * 255f;
            float tB = targetColor.b * 255f;
            const float DegenEps = 1f; // ‖sample - BG‖² 下限（≈1 階調）

            int winSide = 2 * radius + 1;
            float interiorBgDensityMin = Mathf.Max(1f, winSide * winSide * DecontamInteriorBgFrac);
            Parallel.For(0, len, decontamPo, i =>
            {
                float s = strength[i];
                if (s <= 0f || s >= interiorThreshold) return;
                float density = bgDensity[i];
                if (density < interiorBgDensityMin)
                {
                    // 近傍 radius 内に背景(非選択)画素が実質無い = この弱AA画素は選択領域の「内部」。
                    // 背景が無いので α 分解で再合成できないが、内部なら元色を残すべきでない。weak strength
                    // のままだと再着色が部分的になり元色(例: 白文字×青地の縁の薄青)が残留する。full に
                    // 固めて完全再着色する(SolidifyAchromaInterior の有彩ターゲット版・内部限定)。
                    // 「実質無い」= 窓面積比 DecontamInteriorBgFrac 未満。点在ピンホール(偽ドナー)は
                    // 内部扱いで固め、境界(背景に面し密度が高い縁)は従来どおり α 分解されるので
                    // AA ソフトさは不変。
                    strength[i] = 1f;
                    return;
                }

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
                if (bgDensity != null) s_floatPool.Return(bgDensity);
                if (bgBSum   != null) s_floatPool.Return(bgBSum);
                if (bgGSum   != null) s_floatPool.Return(bgGSum);
                if (bgRSum   != null) s_floatPool.Return(bgRSum);
                if (wD != null) s_floatPool.Return(wD);
                if (wB != null) s_floatPool.Return(wB);
                if (wG != null) s_floatPool.Return(wG);
                if (wR != null) s_floatPool.Return(wR);
            }
        }

        /// <summary>
        /// 無彩(白↔黒)再着色のエッジに残る「地色の残り」フチ消し(無彩ゾーンのみ呼ばれる)。
        /// 二値マッチ+デコンタミは選択 tolerance ちょうどで止まるため、その外側 1〜2px に
        /// 「地色↔背景の混色で、背景より地色寄り(=明るい)」画素が残り、暗い再着色色に対して明るい
        /// フチに見える。ここをマッチ境界の外側 AchromaFringeMatchRadius px に限り α 分解
        /// (出力 = α·target + (1-α)·背景)で背景側へ寄せてフチを消す。背景優勢(α 小)の画素だけ
        /// 対象にし、白寄り(α≈1)の画素は除外して白拒否を維持する。脚色でなく元の混色の打ち消し。
        /// </summary>
        private static void CleanAchromaFringe(
            Color32[] pixels, Color32[] originalPixels, float[] strength, float[] claimed,
            int w, int h, Color sampleColor, Color targetColor,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY, CancellationToken ct = default)
        {
            int len = w * h;
            float[] wR = null, wG = null, wB = null, wD = null;
            float[] bgRSum = null, bgGSum = null, bgBSum = null, bgD = null;
            float[] wM = null, mNear = null;
            try
            {
                wR = s_floatPool.Rent(len); wG = s_floatPool.Rent(len);
                wB = s_floatPool.Rent(len); wD = s_floatPool.Rent(len);
                bgRSum = s_floatPool.Rent(len); bgGSum = s_floatPool.Rent(len);
                bgBSum = s_floatPool.Rent(len); bgD = s_floatPool.Rent(len);
                wM = s_floatPool.Rent(len); mNear = s_floatPool.Rent(len);
                Array.Clear(wR, 0, len); Array.Clear(wG, 0, len);
                Array.Clear(wB, 0, len); Array.Clear(wD, 0, len); Array.Clear(wM, 0, len);
                var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
                // 背景候補(非マッチ かつ α>0)と、マッチ指標を準備
                Parallel.For(0, len, po, i =>
                {
                    float s = strength[i];
                    if (s <= 0f && originalPixels[i].a > 0)
                    {
                        wR[i] = originalPixels[i].r; wG[i] = originalPixels[i].g;
                        wB[i] = originalPixels[i].b; wD[i] = 1f;
                    }
                    if (s > 0.05f) wM[i] = 1f;
                });
                BoxFilterSum(wR, bgRSum, w, h, 4, ct);
                BoxFilterSum(wG, bgGSum, w, h, 4, ct);
                BoxFilterSum(wB, bgBSum, w, h, 4, ct);
                BoxFilterSum(wD, bgD, w, h, 4, ct);
                BoxFilterSum(wM, mNear, w, h, AchromaFringeMatchRadius, ct);

                float sR = sampleColor.r * 255f, sG = sampleColor.g * 255f, sB = sampleColor.b * 255f;
                float tR = targetColor.r * 255f, tG = targetColor.g * 255f, tB = targetColor.b * 255f;
                int x0 = Mathf.Max(0, bbMinX - AchromaFringeMatchRadius);
                int x1 = Mathf.Min(w - 1, bbMaxX + AchromaFringeMatchRadius);
                int y0 = Mathf.Max(0, bbMinY - AchromaFringeMatchRadius);
                int y1 = Mathf.Min(h - 1, bbMaxY + AchromaFringeMatchRadius);
                Parallel.For(y0, y1 + 1, po, y =>
                {
                    int row = y * w;
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = row + x;
                        if (strength[i] > 1e-4f) continue;            // マッチ済みは既存処理が担当
                        if (claimed != null && claimed[i] > 0.001f) continue; // 上位ゾーン占有は不可侵
                        if (mNear[i] < 1f) continue;                  // マッチ境界の近傍のみ
                        float density = bgD[i];
                        if (density < 1f) continue;
                        float bR = bgRSum[i] / density, bG = bgGSum[i] / density, bB = bgBSum[i] / density;
                        float dirR = sR - bR, dirG = sG - bG, dirB = sB - bB;
                        float dirSq = dirR * dirR + dirG * dirG + dirB * dirB;
                        if (dirSq < 1f) continue;                     // sample≈BG → α 未定義
                        float pR = originalPixels[i].r, pG = originalPixels[i].g, pB = originalPixels[i].b;
                        float alpha = ((pR - bR) * dirR + (pG - bG) * dirG + (pB - bB) * dirB) / dirSq;
                        // 地色の残りがある背景優勢画素のみ。地色寄り(α≈1)は除外=白拒否を維持。
                        if (alpha < AchromaFringeMinAlpha || alpha > AchromaFringeMaxAlpha) continue;
                        float projR = bR + alpha * dirR, projG = bG + alpha * dirG, projB = bB + alpha * dirB;
                        float distSq = (pR - projR) * (pR - projR) + (pG - projG) * (pG - projG) + (pB - projB) * (pB - projB);
                        if (distSq > 3000f) continue;                 // 線から外れる=別色 → 触らない
                        float om = 1f - alpha;
                        pixels[i] = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tR + om * bR), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tG + om * bG), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tB + om * bB), 0, 255),
                            originalPixels[i].a);
                        if (claimed != null) claimed[i] = 1f;
                    }
                });
            }
            finally
            {
                if (mNear != null) s_floatPool.Return(mNear);
                if (wM != null) s_floatPool.Return(wM);
                if (bgD != null) s_floatPool.Return(bgD);
                if (bgBSum != null) s_floatPool.Return(bgBSum);
                if (bgGSum != null) s_floatPool.Return(bgGSum);
                if (bgRSum != null) s_floatPool.Return(bgRSum);
                if (wD != null) s_floatPool.Return(wD);
                if (wB != null) s_floatPool.Return(wB);
                if (wG != null) s_floatPool.Return(wG);
                if (wR != null) s_floatPool.Return(wR);
            }
        }
    }
}
