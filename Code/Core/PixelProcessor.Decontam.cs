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
        // 無彩フチ消し(CleanAchromaFringe)の定数。マッチ境界の外側に残る「地色の残った」混色画素
        // (背景より地色寄り=明るく、暗い再着色色に対し明るいフチに見える)を α 分解で背景へ寄せて消す。
        private const int AchromaFringeMatchRadius = 2;    // マッチ境界からこの px 以内の外側を対象
        private const float AchromaFringeMinAlpha = 0.05f; // これ未満=地色の残りがほぼ無い→触らない
        private const float AchromaFringeMaxAlpha = 0.70f; // これ超=地色寄り→除外(白拒否を維持)
        private const int AchromaFringeBgRadius = 4;       // BG 推定の窓半径
        // CleanAchromaFringe が maskExcluded を読む最大マージン(bbox からの距離)。
        // 書き込み側は bbox±MatchRadius、BG ドナー収集はさらに ±BgRadius まで読む。
        // 呼び出し側はこのマージン分だけ除外フラグを埋めれば足りる(単一ソース化して
        // 「呼び出し側が狭く埋めて未初期化を読む」事故を防ぐ)。
        internal const int AchromaFringeExclusionMargin =
            AchromaFringeMatchRadius + AchromaFringeBgRadius;

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
        /// 通常の再着色(RecolorPixel)を直接適用すると AA ピクセル（混色）が薄汚れた中間色になる（halo）。
        /// このメソッドは BG を局所近傍の strength=0 ピクセルから推定し、
        /// α を RGB 空間の射影で計算して、新色 target で再合成する。
        ///
        /// 出力:
        ///   aaMask[i] = true なら pixels[i] を decontaminatedPixels[i] で上書きすべき
        ///   それ以外は通常の再着色にフォールバック
        /// </summary>
        private static void DecontaminateAaBoundary(
            Color32[] originalPixels, float[] strength, int w, int h,
            Color sampleColor, Color targetColor,
            int radius, float interiorThreshold,
            bool[] aaMask, Color32[] decontaminatedPixels, bool hasMatch = true,
            CancellationToken ct = default, bool[] maskExcluded = null,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            bool solidifyOnly = false)
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

            // 処理矩形(P2-7 の後段 bbox)。α 分解が触るのは 0<strength<interiorThreshold の画素だけ=
            // 定義上 strength>0 の bbox 内なので、矩形外は前後で常に無変更=出力ビット不変。
            // 未指定(boxMaxX<0)なら従来どおり全画素。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }
            // BG ドナー(w*)は出力矩形の各画素から窓 ±radius だけ読まれるので、その分広げた範囲を
            // 用意すれば足りる。ここまで含めて矩形限定になるので、小 match のゾーン(ロゴ等)でも
            // 全画素分の Array.Clear ×4(4K で各 67MB)と窓和 ×4 を払わなくなる。
            int donorX0 = Mathf.Max(0, boxMinX - radius);
            int donorX1 = Mathf.Min(w - 1, boxMaxX + radius);
            int donorY0 = Mathf.Max(0, boxMinY - radius);
            int donorY1 = Mathf.Min(h - 1, boxMaxY + radius);

            var decontamPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            int winSide = 2 * radius + 1;
            float interiorBgDensityMin = Mathf.Max(1f, winSide * winSide * DecontamInteriorBgFrac);
            // α 分解の対象(0<strength<しきい)は選択の縁の細い帯であることが多い。その数を先に数え、
            // 候補の窓を直接足す方が安ければそうする(矩形全体の窓和 ×4 と作業配列 8 本を払わない)。
            // 足す値は 0..255 の整数(と個数)で、窓和は最大 (2·12+1)²·255 < 2^24 なので、浮動小数の
            // 窓和(BoxFilterSum)も常に正確な整数=どちらで求めても同じ値で、出力はビット単位で同じ。
            long candCount = CountDecontamCandidates(strength, w, boxMinX, boxMinY, boxMaxX, boxMaxY,
                interiorThreshold, decontamPo);
            if (candCount == 0) return;
            long boxArea = (long)(boxMaxX - boxMinX + 1) * (boxMaxY - boxMinY + 1);
            int costRatio = solidifyOnly ? DecontamDirectCostRatioSolid : DecontamDirectCostRatioFull;
            if (candCount * winSide * winSide <= costRatio * boxArea)
            {
                DecontaminateAaDirect(originalPixels, strength, w, h, sampleColor, targetColor, radius,
                    interiorThreshold, interiorBgDensityMin, aaMask, decontaminatedPixels, maskExcluded,
                    boxMinX, boxMinY, boxMaxX, boxMaxY, solidifyOnly, decontamPo);
                return;
            }

            // 局所 BG 推定: strength=0 のピクセルだけを使った近傍和とその密度
            // 0..255 のスケールで計算（後で divide で平均化）
            // null 初期化してから try 内で Rent することでリークを防ぐ。
            float[] wR = null, wG = null, wB = null, wD = null;
            float[] bgRSum = null, bgGSum = null, bgBSum = null, bgDensity = null;
            try
            {
            // 内部の固めだけ(solidifyOnly)なら、要るのは背景の密度だけ。
            if (!solidifyOnly)
            {
                wR = s_floatPool.Rent(len);
                wG = s_floatPool.Rent(len);
                wB = s_floatPool.Rent(len);
                bgRSum = s_floatPool.Rent(len);
                bgGSum = s_floatPool.Rent(len);
                bgBSum = s_floatPool.Rent(len);
            }
            wD = s_floatPool.Rent(len);
            bgDensity = s_floatPool.Rent(len);
            // ドナー範囲だけを「行ごとにゼロ化 → ドナー画素だけ充填」する(Rent はゼロ初期化を
            // 保証しない)。範囲外は BoxFilterSum から読まれないので未初期化のままでよい。
            int donorSpan = donorX1 - donorX0 + 1;
            Parallel.For(donorY0, donorY1 + 1, decontamPo, y =>
            {
                int rowOff = y * w;
                int beg = rowOff + donorX0;
                Array.Clear(wD, beg, donorSpan);
                if (!solidifyOnly)
                {
                    Array.Clear(wR, beg, donorSpan);
                    Array.Clear(wG, beg, donorSpan);
                    Array.Clear(wB, beg, donorSpan);
                }
                for (int x = donorX0; x <= donorX1; x++)
                {
                    int i = rowOff + x;
                    // アルファが0のピクセルはRGBがゴミデータ(黒など)の可能性が高いためBG推定から除外。
                    // 除外マスク画素も除く: strength=0 だが背景ではなく「保護されたパーツ」(サンプル同色で
                    // あり得る)ため、ドナーに入れると BG 推定がサンプル色側へ汚染され、α 分解の前提
                    // (BG=非対象色)が崩れてマスク境界の外側に誤色の点ノイズを塗ってしまう。
                    if (strength[i] <= 0f && originalPixels[i].a > 0 &&
                        (maskExcluded == null || !maskExcluded[i]))
                    {
                        wD[i] = 1f;
                        if (!solidifyOnly)
                        {
                            wR[i] = originalPixels[i].r;
                            wG[i] = originalPixels[i].g;
                            wB[i] = originalPixels[i].b;
                        }
                    }
                }
            });
            // BG 推定(R/G/B/density)。各 ch を順に処理する(融合版は temp ストリームが 4 本同時に
            // なりメモリ帯域律速のこの処理ではキャッシュスラッシングで遅くなったため単一版に戻した)。
            if (!solidifyOnly)
            {
                BoxFilterSum(wR, bgRSum, w, h, radius, ct, boxMinX, boxMinY, boxMaxX, boxMaxY);
                BoxFilterSum(wG, bgGSum, w, h, radius, ct, boxMinX, boxMinY, boxMaxX, boxMaxY);
                BoxFilterSum(wB, bgBSum, w, h, radius, ct, boxMinX, boxMinY, boxMaxX, boxMaxY);
            }
            BoxFilterSum(wD, bgDensity, w, h, radius, ct, boxMinX, boxMinY, boxMaxX, boxMaxY);

            float sR = sampleColor.r * 255f;
            float sG = sampleColor.g * 255f;
            float sB = sampleColor.b * 255f;
            float tR = targetColor.r * 255f;
            float tG = targetColor.g * 255f;
            float tB = targetColor.b * 255f;

            Parallel.For(boxMinY, boxMaxY + 1, decontamPo, y =>
            {
            int rowOff = y * w;
            for (int x = boxMinX; x <= boxMaxX; x++)
            {
                int i = rowOff + x;
                float s = strength[i];
                if (s <= 0f || s >= interiorThreshold) continue;
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
                    continue;
                }
                if (solidifyOnly) continue;

                float bR = bgRSum[i] / density;
                float bG = bgGSum[i] / density;
                float bB = bgBSum[i] / density;

                float dirR = sR - bR;
                float dirG = sG - bG;
                float dirB = sB - bB;
                float dirSq = dirR * dirR + dirG * dirG + dirB * dirB;
                if (dirSq < DecontamDegenEps) continue; // sample ≈ BG → α が定義できない

                float pR = originalPixels[i].r;
                float pG = originalPixels[i].g;
                float pB = originalPixels[i].b;

                float dot = (pR - bR) * dirR + (pG - bG) * dirG + (pB - bB) * dirB;
                float alpha = dot / dirSq;
                if (alpha < 0f) alpha = 0f;
                else if (alpha > 1f) alpha = 1f;

                // BG–sample 線分から大きく外れる色は、α 分解の前提が成り立たないためスキップする。
                float projR = bR + alpha * dirR;
                float projG = bG + alpha * dirG;
                float projB = bB + alpha * dirB;
                float distSq = (pR - projR) * (pR - projR) + (pG - projG) * (pG - projG) + (pB - projB) * (pB - projB);
                if (distSq > DecontamLineDistSqMax) continue; // 許容誤差。各チャンネル約31のズレまで許容

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
            }
            });
            }
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

        // ‖sample - BG‖² の下限(≈1 階調)。これ未満は sample≈BG で α が定義できない。
        private const float DecontamDegenEps = 1f;

        // BG–sample 線分からの距離² の上限(各 ch 約 31 階調)。これを超える色は α 分解の前提
        // (2 色の混色)が成り立たない。AA デコンタミの両経路と無彩フチ消しで共通。
        private const float DecontamLineDistSqMax = 3000f;

        // 候補の窓を直接足す方を選ぶ目安: 候補数 × 窓の面積 ≤ この値 × 矩形の面積。
        // 矩形全体の窓和は窓の半径に依らず 1 画素あたり一定(作業配列の充填 + 窓和の 2 パス)で、
        // 色の和も要る経路(4 ch)は密度だけの経路(1 ch)の約 3.5 倍かかる。.NET 8 の実測
        // (4K、半径 4)で損益分岐はおよそ 45 と 13。読み比べの誤差を見込んで少し手前に置く。
        private const int DecontamDirectCostRatioFull = 32;
        private const int DecontamDirectCostRatioSolid = 8;

        /// <summary>矩形内の α 分解の対象(0 &lt; strength &lt; interiorThreshold)の数。</summary>
        private static long CountDecontamCandidates(float[] strength, int w,
            int boxMinX, int boxMinY, int boxMaxX, int boxMaxY, float interiorThreshold, ParallelOptions po)
        {
            long total = 0;
            Parallel.For(boxMinY, boxMaxY + 1, po, () => 0L, (y, _, acc) =>
            {
                int rowOff = y * w;
                for (int x = boxMinX; x <= boxMaxX; x++)
                {
                    float s = strength[rowOff + x];
                    if (!(s <= 0f || s >= interiorThreshold)) acc++;   // 本体の候補判定と同じ形(NaN も同じ側)
                }
                return acc;
            }, acc => Interlocked.Add(ref total, acc));
            return total;
        }

        /// <summary>
        /// DecontaminateAaBoundary の候補がまばらなときの経路。背景(ドナー)の窓和を、候補の画素に
        /// ついてだけ窓を直接足して求める。ドナーの条件・判定・再合成は矩形全体の経路と同じ。
        /// 候補を固める(strength=1)書き込みは並行して読まれるが、ドナーの条件(strength ≤ 0)は
        /// 候補(strength &gt; 0)の書き換えで変わらないので、読む順序に依存しない。
        /// </summary>
        private static void DecontaminateAaDirect(
            Color32[] originalPixels, float[] strength, int w, int h,
            Color sampleColor, Color targetColor, int radius,
            float interiorThreshold, float interiorBgDensityMin,
            bool[] aaMask, Color32[] decontaminatedPixels, bool[] maskExcluded,
            int boxMinX, int boxMinY, int boxMaxX, int boxMaxY, bool solidifyOnly, ParallelOptions po)
        {
            float sR = sampleColor.r * 255f;
            float sG = sampleColor.g * 255f;
            float sB = sampleColor.b * 255f;
            float tR = targetColor.r * 255f;
            float tG = targetColor.g * 255f;
            float tB = targetColor.b * 255f;
            Parallel.For(boxMinY, boxMaxY + 1, po, y =>
            {
                int rowOff = y * w;
                int wy0 = Mathf.Max(0, y - radius), wy1 = Mathf.Min(h - 1, y + radius);
                for (int x = boxMinX; x <= boxMaxX; x++)
                {
                    int i = rowOff + x;
                    float s = strength[i];
                    if (s <= 0f || s >= interiorThreshold) continue;
                    int wx0 = Mathf.Max(0, x - radius), wx1 = Mathf.Min(w - 1, x + radius);
                    int sumR = 0, sumG = 0, sumB = 0, cnt = 0;
                    for (int yy = wy0; yy <= wy1; yy++)
                    {
                        int r2 = yy * w;
                        for (int xx = wx0; xx <= wx1; xx++)
                        {
                            int j = r2 + xx;
                            if (!(strength[j] <= 0f)) continue;   // ドナーの条件は矩形全体の経路と同じ形
                            Color32 o = originalPixels[j];
                            if (o.a == 0) continue;
                            if (maskExcluded != null && maskExcluded[j]) continue;
                            sumR += o.r; sumG += o.g; sumB += o.b; cnt++;
                        }
                    }
                    float density = cnt;
                    if (density < interiorBgDensityMin)
                    {
                        strength[i] = 1f;   // 背景が実質無い=選択の内部(矩形全体の経路と同じ)
                        continue;
                    }
                    if (solidifyOnly) continue;

                    float bR = sumR / density;
                    float bG = sumG / density;
                    float bB = sumB / density;

                    float dirR = sR - bR;
                    float dirG = sG - bG;
                    float dirB = sB - bB;
                    float dirSq = dirR * dirR + dirG * dirG + dirB * dirB;
                    if (dirSq < DecontamDegenEps) continue;

                    float pR = originalPixels[i].r;
                    float pG = originalPixels[i].g;
                    float pB = originalPixels[i].b;

                    float dot = (pR - bR) * dirR + (pG - bG) * dirG + (pB - bB) * dirB;
                    float alpha = dot / dirSq;
                    if (alpha < 0f) alpha = 0f;
                    else if (alpha > 1f) alpha = 1f;

                    float projR = bR + alpha * dirR;
                    float projG = bG + alpha * dirG;
                    float projB = bB + alpha * dirB;
                    float distSq = (pR - projR) * (pR - projR) + (pG - projG) * (pG - projG) + (pB - projB) * (pB - projB);
                    if (distSq > DecontamLineDistSqMax) continue;

                    float oneMinusAlpha = 1f - alpha;
                    float resR = alpha * tR + oneMinusAlpha * bR;
                    float resG = alpha * tG + oneMinusAlpha * bG;
                    float resB = alpha * tB + oneMinusAlpha * bB;

                    aaMask[i] = true;
                    decontaminatedPixels[i] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(resR), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(resG), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(resB), 0, 255),
                        originalPixels[i].a);
                }
            });
        }

        /// <summary>
        /// 無彩(白↔黒)再着色のエッジに残る「地色の残り」フチ消し(無彩ゾーンのみ呼ばれる)。
        /// 二値マッチ+デコンタミは選択 tolerance ちょうどで止まるため、その外側 1〜2px に
        /// 「地色↔背景の混色で、背景より地色寄り(=明るい)」画素が残り、暗い再着色色に対して明るい
        /// フチに見える。ここをマッチ境界の外側 AchromaFringeMatchRadius px に限り α 分解
        /// (出力 = α·target + (1-α)·背景)で背景側へ寄せてフチを消す。背景優勢(α 小)の画素だけ
        /// 対象にし、白寄り(α≈1)の画素は除外して白拒否を維持する。脚色でなく元の混色の打ち消し。
        ///
        /// maskExcluded: 除外マスク画素の位置(null=マスクなし)。呼び出し側は
        /// bbox±AchromaFringeExclusionMargin の範囲を必ず埋めること。
        /// DecontaminateAaBoundary と同じ 2 つの理由で参照する:
        ///   - BG ドナーから隠す … 除外画素は strength=0 だが「背景」ではない(サンプル同色の
        ///     保護パーツであり得る)。ドナーに入れると BG 推定がサンプル色で汚染される。
        ///   - 書き込み対象から外す … ユーザーが「触るな」と指定した画素であり、
        ///     target 混色を塗るのはマスク契約の違反。
        /// </summary>
        private static void CleanAchromaFringe(
            Color32[] pixels, Color32[] originalPixels, float[] strength, float[] claimed,
            int w, int h, Color sampleColor, Color targetColor,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY, CancellationToken ct = default,
            bool[] maskExcluded = null)
        {
            float sR = sampleColor.r * 255f, sG = sampleColor.g * 255f, sB = sampleColor.b * 255f;
            float tR = targetColor.r * 255f, tG = targetColor.g * 255f, tB = targetColor.b * 255f;
            int x0 = Mathf.Max(0, bbMinX - AchromaFringeMatchRadius);
            int x1 = Mathf.Min(w - 1, bbMaxX + AchromaFringeMatchRadius);
            int y0 = Mathf.Max(0, bbMinY - AchromaFringeMatchRadius);
            int y1 = Mathf.Min(h - 1, bbMaxY + AchromaFringeMatchRadius);
            if (x1 < x0 || y1 < y0) return;   // 対象矩形が空(マッチ皆無) → 何もしない
            // フチ消しが触るのは矩形 [x0..x1]×[y0..y1] のうち、マッチ境界の外側(マッチから
            // AchromaFringeMatchRadius 以内の未マッチ画素)の細い帯だけ。
            //   ・「マッチが近くにあるか」は、範囲 F(矩形 ± 背景の窓の半径)の整数の累積和から引く
            //   ・背景(ドナー)の窓和は、帯の画素についてだけ窓を直接足す
            // 以前は窓和(半径 4)を作業配列 10 本と BoxFilterSum ×5 で矩形全体に作っていた。足す値は
            // 0..255 の整数(と個数)で、窓和は最大 (2·4+1)²·255 < 2^24 なので浮動小数でも常に正確な整数。
            // ここで直接足した値と同じになり、出力はビット単位で同じ。
            const int FringeBgRadius = AchromaFringeBgRadius;
            int fx0 = Mathf.Max(0, x0 - FringeBgRadius), fx1 = Mathf.Min(w - 1, x1 + FringeBgRadius);
            int fy0 = Mathf.Max(0, y0 - FringeBgRadius), fy1 = Mathf.Min(h - 1, y1 + FringeBgRadius);
            int fw = fx1 - fx0 + 1, fh = fy1 - fy0 + 1;
            int[] satM = null;
            try
            {
                satM = s_intPool.Rent(fw * fh);
                var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
                // マッチ指標(strength > 0.05)の累積和。近傍の窓(±AchromaFringeMatchRadius)は F の内側か、
                // F と同じく画像の端で切られる(F = 矩形 ± 背景の窓の半径 ⊇ 矩形 ± マッチの窓の半径)。
                BuildCountSat(strength, satM, w, fx0, fy0, fx1, fy1, po, 0.05f);
                var satML = satM;
                Parallel.For(y0, y1 + 1, po, y =>
                {
                    int row = y * w;
                    int wy0 = Mathf.Max(0, y - FringeBgRadius), wy1 = Mathf.Min(h - 1, y + FringeBgRadius);
                    for (int xs = x0; xs <= x1; xs += MixSegment)
                    {
                    int xe = Mathf.Min(x1, xs + MixSegment - 1);
                    // 行の区間の ±AchromaFringeMatchRadius にマッチが無ければ、区間のどの画素も「マッチ境界の
                    // 近傍」でない(画素の窓は区間の窓の内側)。画素ごとの判定と同じ結論なのでまとめて飛ばす。
                    if (CountInRect(satML, fw, fx0, fy0, fx1, fy1,
                            xs - AchromaFringeMatchRadius, y - AchromaFringeMatchRadius,
                            xe + AchromaFringeMatchRadius, y + AchromaFringeMatchRadius) < 1)
                        continue;
                    for (int x = xs; x <= xe; x++)
                    {
                        int i = row + x;
                        if (strength[i] > 1e-4f) continue;            // マッチ済みは既存処理が担当
                        if (maskExcluded != null && maskExcluded[i]) continue; // 除外マスクは不可侵
                        if (claimed != null && claimed[i] > 0.001f) continue; // 上位ゾーン占有は不可侵
                        if (CountInWindow(satML, fw, fx0, fy0, fx1, fy1, x, y, AchromaFringeMatchRadius) < 1)
                            continue;                                 // マッチ境界の近傍のみ
                        // 背景候補(非マッチ かつ α>0。除外マスク画素はドナーから隠す=マスク中立化。
                        // DecontaminateAaBoundary と同じ扱い)の窓和。
                        int sumR = 0, sumG = 0, sumB = 0, cnt = 0;
                        int wx0 = Mathf.Max(0, x - FringeBgRadius), wx1 = Mathf.Min(w - 1, x + FringeBgRadius);
                        for (int yy = wy0; yy <= wy1; yy++)
                        {
                            int r2 = yy * w;
                            for (int xx = wx0; xx <= wx1; xx++)
                            {
                                int j = r2 + xx;
                                if (!(strength[j] <= 0f)) continue;   // 以前のドナー充填と同じ形(NaN も同じ側)
                                Color32 o = originalPixels[j];
                                if (o.a == 0) continue;
                                if (maskExcluded != null && maskExcluded[j]) continue;
                                sumR += o.r; sumG += o.g; sumB += o.b; cnt++;
                            }
                        }
                        float density = cnt;
                        if (density < 1f) continue;
                        float bR = sumR / density, bG = sumG / density, bB = sumB / density;
                        float dirR = sR - bR, dirG = sG - bG, dirB = sB - bB;
                        float dirSq = dirR * dirR + dirG * dirG + dirB * dirB;
                        if (dirSq < DecontamDegenEps) continue;       // sample≈BG → α 未定義
                        float pR = originalPixels[i].r, pG = originalPixels[i].g, pB = originalPixels[i].b;
                        float alpha = ((pR - bR) * dirR + (pG - bG) * dirG + (pB - bB) * dirB) / dirSq;
                        // 地色の残りがある背景優勢画素のみ。地色寄り(α≈1)は除外=白拒否を維持。
                        if (alpha < AchromaFringeMinAlpha || alpha > AchromaFringeMaxAlpha) continue;
                        float projR = bR + alpha * dirR, projG = bG + alpha * dirG, projB = bB + alpha * dirB;
                        float distSq = (pR - projR) * (pR - projR) + (pG - projG) * (pG - projG) + (pB - projB) * (pB - projB);
                        if (distSq > DecontamLineDistSqMax) continue; // 線から外れる=別色 → 触らない
                        float om = 1f - alpha;
                        pixels[i] = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tR + om * bR), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tG + om * bG), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tB + om * bB), 0, 255),
                            originalPixels[i].a);
                        if (claimed != null) claimed[i] = 1f;
                    }
                    }
                });
            }
            finally
            {
                if (satM != null) s_intPool.Return(satM);
            }
        }

        /// <summary>
        /// src &gt; minExclusive を 1 と数えた個数の累積和を、範囲 [x0..x1]×[y0..y1] について作る。
        /// sat[(y−y0)·satW + (x−x0)] = (x0,y0) からその画素までの矩形の個数(satW = x1−x0+1)。
        /// 余白の行・列を持たないので、4K 全面でも配列プールの上限(4096²)に収まる。
        /// 行ごとの累積は行並列、列方向の累積は列の帯ごとに並列(どちらも整数加算なので順序に依存しない)。
        /// </summary>
        private static void BuildCountSat(float[] src, int[] sat, int w, int x0, int y0, int x1, int y1,
            ParallelOptions po, float minExclusive = 0.5f)
        {
            int satW = x1 - x0 + 1, rows = y1 - y0 + 1;
            Parallel.For(0, rows, po, r =>
            {
                int srcRow = (y0 + r) * w;
                int o = r * satW;
                int acc = 0;
                for (int x = x0; x <= x1; x++)
                {
                    if (src[srcRow + x] > minExclusive) acc++;
                    sat[o + (x - x0)] = acc;
                }
            });
            const int Band = 64;
            int bands = (satW + Band - 1) / Band;
            Parallel.For(0, bands, po, b =>
            {
                int c0 = b * Band, c1 = Mathf.Min(satW, c0 + Band);
                for (int r = 1; r < rows; r++)
                {
                    int o = r * satW, prev = o - satW;
                    for (int c = c0; c < c1; c++) sat[o + c] += sat[prev + c];
                }
            });
        }

        // 混色帯の解析と無彩フチ消し(CleanAchromaFringe)で、早い打ち切りをまとめて判定する行の区間の長さ(px)。
        private const int MixSegment = 32;

        /// <summary>矩形 [rx0..rx1]×[ry0..ry1] のうち範囲 [x0..x1]×[y0..y1] に入る部分の個数(BuildCountSat の表から)。</summary>
        private static int CountInRect(int[] sat, int satW, int x0, int y0, int x1, int y1,
            int rx0, int ry0, int rx1, int ry1)
        {
            int xa = Mathf.Max(x0, rx0) - x0, xb = Mathf.Min(x1, rx1) - x0;
            int ya = Mathf.Max(y0, ry0) - y0, yb = Mathf.Min(y1, ry1) - y0;
            if (xb < xa || yb < ya) return 0;
            int s = sat[yb * satW + xb];
            if (xa > 0) s -= sat[yb * satW + xa - 1];
            if (ya > 0)
            {
                int up = (ya - 1) * satW;
                s -= sat[up + xb];
                if (xa > 0) s += sat[up + xa - 1];
            }
            return s;
        }

        /// <summary>(x, y) を中心とする ±r の窓のうち範囲 [x0..x1]×[y0..y1] に入る部分の個数(BuildCountSat の表から)。</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int CountInWindow(int[] sat, int satW, int x0, int y0, int x1, int y1, int x, int y, int r)
        {
            int xa = Mathf.Max(x0, x - r) - x0, xb = Mathf.Min(x1, x + r) - x0;
            int ya = Mathf.Max(y0, y - r) - y0, yb = Mathf.Min(y1, y + r) - y0;
            int s = sat[yb * satW + xb];
            if (xa > 0) s -= sat[yb * satW + xa - 1];
            if (ya > 0)
            {
                int up = (ya - 1) * satW;
                s -= sat[up + xb];
                if (xa > 0) s += sat[up + xa - 1];
            }
            return s;
        }
    }
}
