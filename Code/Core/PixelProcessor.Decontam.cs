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
            var decontamPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
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
            const float DegenEps = 1f; // ‖sample - BG‖² 下限（≈1 階調）

            int winSide = 2 * radius + 1;
            float interiorBgDensityMin = Mathf.Max(1f, winSide * winSide * DecontamInteriorBgFrac);
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
                if (dirSq < DegenEps) continue; // sample ≈ BG → α が定義できない

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
                if (distSq > 3000f) continue; // 許容誤差。各チャンネル約31のズレまで許容

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
                    for (int x = x0; x <= x1; x++)
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
                                if (strength[j] > 0f) continue;
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
                if (satM != null) s_intPool.Return(satM);
            }
        }

        // ───────── 混色帯(選択境界の AA・にじみ)の再着色 ─────────
        // 選択境界の画素は「素材と隣のものが混ざった色」で出来ている: p = α·F + (1−α)·B
        // (F = その場の素材色、B = 隣のもの、α = 素材の被覆率)。正しい色替えは素材のぶんだけを差し替えること:
        //     p' = α·F' + (1−α)·B = p + α·(F' − F)        F' = 素材色 F を再着色した色
        // 以前は混色の画素を
        //   ・マッチが落とす(元の色のまま残る = 縁に元の色の点・線)
        //   ・緩和マッチ(穴埋め・境界回復)が拾って「素材の色の一種」として全強度で再着色する(暗い灰との
        //     混色が明るく濁る。tolerance を上げると、ほぼ無彩の地まで縁から数 px 入り、輪のように残る)
        //   ・部分 strength で元の色と再着色をブレンドする / α·target + (1−α)·背景へ置き換える(α は主サンプル
        //     色への射影、色は生の target なので、陰影のある素材では素材色も塗り色も合わない)
        // のどれかで扱っていて、どれも上の式にならなかった。有彩サンプルのゾーンで境界クリーンアップが ON の
        // とき(混色帯モード)は、選択は主マッチの結果だけにして(緩和マッチは行わない)、境界の帯をここで
        // 上の式で塗る。
        //
        // α の求め方: 色度(RGB から平均を引いた成分 = 無彩軸に直交する 2 次元)で測る。
        //   c(p) = α·c(F) + (1−α)·c(B)
        // 無彩の背景は白でも灰でも黒でも c(B)=0 になるので、白い文字と暗い灰の地が同じ窓にあっても
        // (三者の境でも)α が背景の明るさに依らず決まる。RGB 3 成分の射影では背景を 1 色に決める必要が
        // あり、白と暗い灰の中間の灰を「素材が混ざった」と誤認する。
        // c(B) は、近くの「面になっている未選択画素」(3×3 が全部未選択で色度が揃っている = 混色でない)の
        // うち最も近いものから取る(仮説 1)。それで説明できない、または近くに面が無い(幅 2px 以下の線・文字)
        // ときは無彩の背景とみなす(仮説 2)。細い線は全画素が混色なので、面を作れない。透明(α = 0)から
        // ±MixBandRadius 以内も面にしない。UV 島の外周は素材と未知の下地の AA・余白(髪束の縁の幅 3px の
        // 淡い帯など)で、そこを面とみなすと帯が「背景」として元の色のまま縁取りに残る。
        // 色度だけでは決めきれないので、次の整合も見る(ClassifyMixture):
        //   ・素材と近くの面の色度が近く α が決まらないときは、画素が面と同じ色なら面の一部、そうでなければ
        //     無彩の背景の仮説で見る。
        //   ・無彩との混色は素材の色相をそのまま保つので、無彩の背景の仮説では向きのずれを約 6° までしか
        //     許さない(色相の近い別の色の縁取り・飾りを「素材 + 無彩」と取り違えない)。
        //   ・無彩の背景の仮説では、ほぼ無彩の画素(色度 ≤ MixFlatChroma)を混色とみなさない。わずかに色のある地
        //     (生成り・暖かい白、色度 10 前後)は、色のついた地そのものとしても説明でき、素材の寄与と区別できない。
        //   ・明るさも合成の式で説明できること(仮説 1: 面の明るさで、仮説 2: 暗黙の無彩 (p − αF)/(1 − α) が
        //     0..255 に収まること)。補色どうしの境の灰の線は、色度だけなら両者の混色に見える。
        //   ・色度は素材の向きなのに明るさが説明できない画素は、混色ではなく素材の濃淡(ハイライト・陰)。
        //   ・α が 1 を明らかに超える(素材色 F より鮮やか)なら、その F はこの画素の出所ではない。
        //   ・ほぼ無彩の画素は、有彩の面との混色(仮説 1)としては見ない(無彩の面となら見る)。補色に近い 2 面の境の灰の線は、
        //     色度でも明るさでも両者の混色と区別できないことがあり、素材の色へ寄せると区切り線が消える。
        // F は近くの「内側の選択画素」(3×3 が全部選択済み)の平均。まず窓 ±MixBandRadius、無ければ
        // 近傍半径まで輪を広げる(素材の細い線は、太い本体が数 px 離れている)。近くに確かな選択が無い
        // (素材そのものが細い線・点で、どの画素も混色)とき、または近くの F では説明できないときは、
        // ゾーンのサンプルのうち最も鮮やかな色を F にする。陰影が線形(暗い所 = 同じ色を暗くしたもの)なら、
        // どの明るさの素材色を F にしても α·(F'−F) は同じ値になるので、最も鮮やかな色で測れば α が 1 を
        // 超えない。F' は F を RecolorPixel に通した色(= パイプラインが素材の内側を塗る色そのもの)。
        //
        // 選択済み(strength ≥ MixSolidStrength)の画素を上書きするのは、色度の射影が近くの内側画素の
        // どれよりも低い(= 近くの素材の色のばらつきでは説明できない)ときだけ。素材自身の模様・陰影は
        // 今までどおり RecolorPixel が塗る。含めるマスクの画素は、利用者が素材と指定したので解析しない。
        //
        // 近くに内側画素(芯)はあるがその色度が MixMinChroma 未満(暗い陰・淡い明部)の所は、どの画素も解析
        // しない(従来の経路 = 主マッチの strength で塗る)。素材の色度が小さいと α を色度で測れず、測れない
        // α で主マッチを上書きすると、確かな選択が元の色へ戻る(未選択の点の周りが四角い穴になる)、わずかに
        // 色のある地が色づく、縁のぼけた所が元の色の帯になる、のどれかになる。
        private const int   MixBandRadius      = 2;      // 境界の帯の幅: 選択の境からこの px 以内
        // 「素材として確かに選択された」とみなす strength 下限(デコンタミの内部判定と同じ 0.97)。これ未満の
        // 弱い選択(ハイライト伝播、彩度の確信度で割り引かれた画素)は、未選択と同じく「混色かもしれない
        // 画素」として調べる。
        private const float MixSolidStrength   = DecontaminationInteriorThreshold;
        // 素材の色度の最小の大きさと、素材と背景の色度の最小の差(0..255 の RGB)。これ未満では 8bit の
        // 1 階調が α の 4% 以上に当たり、α が雑音で決まる。
        private const float MixMinChroma       = 24f;
        private const float MixMinSeparation   = 24f;
        // 色度が線分 c(B)–c(F) から外れてよい量。絶対値(8bit の量子化と雑音のぶん)と、その画素の色度が
        // 背景から離れた距離に対する比(約 14° の向きのずれ)の大きいほう。比を線分の長さで取ると、α の
        // 低い所で許容が広すぎて、素材の色度と斜めに少し重なるだけの別の色(赤い糸の隣の生成りの布)を
        // 「素材が 1〜2 割混ざった」と誤認する。
        private const float MixResidAbs        = 8f;
        private const float MixResidFrac       = 0.25f;
        // 背景が無彩(仮説 2、または無彩の面)のときの向きのずれの比(約 6°)。無彩と混ぜても色相は変わらないので、
        // ずれは素材色 F の推定誤差と量子化だけ。有彩の面のときは面の色度の推定誤差が乗るので MixResidFrac。
        private const float MixHueFrac         = 0.10f;
        // 上の許容に足す、素材色を推定した画素の色度のばらつき(標準偏差)の倍率。ノイズ・質感のある素材では、
        // 混色の画素の色度も同じだけ揺れる。
        private const float MixNoiseK          = 2f;
        // 明るさ(RGB の平均)が合成の式から外れてよい量。素材色 F は近くの内側画素の平均なので、陰影の
        // 勾配のぶん(数 px で 20 階調前後)はずれる。仮説 1 では、素材と面の明るさの差に対する比も許す。
        private const float MixLightAbs        = 24f;
        private const float MixLightFrac       = 0.25f;
        // 「面」とみなす色度の揃い方(3×3 の隣との色度の差の上限、0..255)。明るさの違い(陰影・織り目)は
        // 色度に出ないので数えない。
        private const float MixFlatChroma      = 12f;
        // これ未満は素材がほぼ乗っていない(変えない)。無彩フチ消しの下限(AchromaFringeMinAlpha)と同じ 5%:
        // 残る色みは最大でも 13 階調ほどで見えず、背景の画素を数階調だけ動かす無駄な変化を作らない。
        private const float MixAlphaMin        = AchromaFringeMinAlpha;
        // これ以上は素材そのもの。未選択なら、マッチが意図して落とした画素(包絡ゲート・彩度ガード等)とみて
        // 触らない。1 + MixAlphaOver を超える(素材色 F より明らかに鮮やか)なら、その F は出所ではない。
        private const float MixAlphaPure       = 0.90f;
        private const float MixAlphaOver       = 0.15f;
        private const float MixCoreRangeMargin = 0.03f;  // 「内側画素のどれよりも低い」の余白
        // mixAlpha の値: 負 = 対象外(従来の経路で塗る)、0..1 = 被覆率(合成の式で塗る)、
        // MixAsMaterial = 弱く選択された画素だが素材そのもの・素材の濃淡(全強度で再着色する)。
        private const float MixAsMaterial      = 2f;
        // AnalyzeMixtureBand の「面か」の覚え書きの値(0/1 = まだ調べていない)。
        private const float FlatYes = 3f, FlatNo = 4f;

        // ClassifyMixture の判定。
        private const int MixOther = 0, MixMixture = 1, MixMaterial = 2, MixBackground = 3, MixOver = 4;

        /// <summary>
        /// 混色帯モードのゾーンか。境界クリーンアップ ON、境界ぼかし無し、主マッチがグレーモードでなく、ゾーンの
        /// サンプル(主 + 内部)のうち最も鮮やかな色の色度が MixMinChroma 以上(= 素材の色度で被覆率を測れる)のとき。
        /// 境界ぼかしは strength を空間に広げて「未選択の面」(背景の手がかり)を消すので、被覆率の推定と両立しない。
        /// そのときは従来の経路で塗る(ぼかしを意図して使う設定なので、従来どおりの見た目を保つ)。
        /// 主サンプル 1 色だけで決めると、同じ素材でもクリックした濃淡で判定が割れる(自動調整は内部サンプルに
        /// 素材の濃淡を足すので、最も鮮やかな色はクリック位置に依らない)。無彩のサンプルは色度で被覆率を
        /// 測れないので従来の経路(緩和マッチ + α 再合成 + 無彩フチ消し)のまま。
        /// </summary>
        private static bool IsMixtureBandZone(ColorZone zone, bool useDecontamination, float edgeFeather)
        {
            if (!useDecontamination || zone.mode != SelectionMode.ColorPick || edgeFeather > 0.01f) return false;
            Color.RGBToHSV(zone.sampleColor, out _, out float sS, out float sV);
            if (sS <= ColorZone.GrayModeEffectiveChromaThreshold(sV, zone.chromaThreshold)) return false;
            return ChromaSq(MostChromaticSample(zone)) * (255f * 255f) >= MixMinChroma * MixMinChroma;
        }

        /// <summary>ゾーンのサンプル(主 + 内部)のうち、色度(RGB − 平均)が最も大きい色。</summary>
        private static Color MostChromaticSample(ColorZone zone)
        {
            Color best = zone.sampleColor;
            float bestC = ChromaSq(best);
            var extra = zone.extraSamples;
            if (extra != null)
                for (int i = 0; i < extra.Count; i++)
                {
                    float c2 = ChromaSq(extra[i]);
                    if (c2 > bestC) { bestC = c2; best = extra[i]; }
                }
            return best;
        }

        private static float ChromaSq(Color c)
        {
            float m = (c.r + c.g + c.b) * (1f / 3f);
            float r = c.r - m, g = c.g - m, b = c.b - m;
            return r * r + g * g + b * b;
        }

        /// <summary>
        /// 画素 p を「素材 F と背景の混色」として説明できるかを判定する(上のコメント参照)。色度 c と明るさ m
        /// (RGB の平均)はどれも 0..255。hasB = 近くの面(仮説 1 の背景)があるか。
        /// 戻り値: MixMixture(alpha = 被覆率)/ MixMaterial(素材そのもの・素材の濃淡)/ MixBackground(素材が
        /// 乗っていない)/ MixOver(F より鮮やか = この F は出所でない)/ MixOther(説明できない)。
        /// </summary>
        private static int ClassifyMixture(
            float cpR, float cpG, float cpB, float pm,
            float cfR, float cfG, float cfB, float cf2, float fm,
            bool hasB, float cbR, float cbG, float cbB, float bm, float noise,
            out float alpha)
        {
            alpha = 0f;
            float residAbs2 = MixResidAbs * MixResidAbs;
            float flat2 = MixFlatChroma * MixFlatChroma;
            float noiseTol = MixNoiseK * noise;
            float cp2 = cpR * cpR + cpG * cpG + cpB * cpB;
            float cb2 = cbR * cbR + cbG * cbG + cbB * cbB;
            // ほぼ無彩の画素は、有彩の面との混色としては見ない(仮説 2 だけで見る)。
            if (hasB && (cp2 > flat2 || cb2 <= flat2))
            {
                float qR = cpR - cbR, qG = cpG - cbG, qB = cpB - cbB;
                float q2 = qR * qR + qG * qG + qB * qB;
                float dR = cfR - cbR, dG = cfG - cbG, dB = cfB - cbB;
                float d2 = dR * dR + dG * dG + dB * dB;
                if (d2 < MixMinSeparation * MixMinSeparation)
                {
                    // 素材と面の色度が近く α が決まらない。面と同じ色なら面の一部、そうでなければ仮説 2 で見る。
                    if (q2 <= residAbs2 && Mathf.Abs(pm - bm) <= MixLightAbs) return MixBackground;
                }
                else
                {
                    float a = (qR * dR + qG * dG + qB * dB) / d2;
                    float eR = qR - a * dR, eG = qG - a * dG, eB = qB - a * dB;
                    float res2 = eR * eR + eG * eG + eB * eB;
                    float frac = cb2 <= flat2 ? MixHueFrac : MixResidFrac;
                    float tol = Mathf.Max(MixResidAbs, frac * Mathf.Sqrt(q2)) + noiseTol;
                    float lightRes = Mathf.Abs(pm - (a * fm + (1f - a) * bm));
                    float lightTol = Mathf.Max(MixLightAbs, MixLightFrac * Mathf.Abs(fm - bm));
                    if (res2 <= tol * tol && lightRes <= lightTol)
                    {
                        int k = ClassifyAlpha(a);
                        if (k == MixMixture) alpha = a;
                        return k;
                    }
                }
            }
            // 仮説 2: 背景は無彩(明るさは未知)。無彩と混ぜても色相は変わらない。
            float a1 = (cpR * cfR + cpG * cfG + cpB * cfB) / cf2;
            float e1R = cpR - a1 * cfR, e1G = cpG - a1 * cfG, e1B = cpB - a1 * cfB;
            float r1 = e1R * e1R + e1G * e1G + e1B * e1B;
            float tol1 = Mathf.Max(MixResidAbs, MixHueFrac * Mathf.Sqrt(cp2)) + noiseTol;
            if (r1 > tol1 * tol1) return MixOther;
            int k1 = ClassifyAlpha(a1);
            if (k1 != MixMixture) return k1;
            // ほぼ無彩の画素: わずかに色のある地そのものとしても説明でき、素材の寄与と区別できない。
            if (cp2 <= flat2) return MixBackground;
            // 暗黙の無彩の背景 (p − αF)/(1 − α) の明るさが 0..255 に収まるか。収まらない = 素材の濃淡。
            float rem = pm - a1 * fm;
            float bn = Mathf.Clamp(rem / (1f - a1), 0f, 255f);
            if (Mathf.Abs(rem - (1f - a1) * bn) > MixLightAbs) return MixMaterial;
            alpha = a1;
            return MixMixture;
        }

        // 被覆率 α の範囲による判定。負(画素が背景より素材から遠い)は合成では説明できない。
        private static int ClassifyAlpha(float a)
        {
            if (a > 1f + MixAlphaOver) return MixOver;
            if (a >= MixAlphaPure) return MixMaterial;
            if (a < -MixAlphaMin) return MixOther;
            if (a < MixAlphaMin) return MixBackground;
            return MixMixture;
        }

        /// <summary>
        /// 混色帯を解析し、合成の式で塗る画素について mixAlpha[i] = α (0..1) と mixedPixels[i] = p + α·(F'−F) を
        /// 書く(上のコメント参照)。対象外の画素は mixAlpha[i] = −1 のまま(呼び出し側の従来経路で塗る)。
        /// 読むのは originalPixels / strength だけ、書くのは自分の画素の mixAlpha / mixedPixels だけなので
        /// 行並列でも読み書きが交わらない。再着色ループが mixAlpha ≥ 0 の画素をこの結果で塗る。
        /// radius: 素材・背景を探す近傍半径(境界クリーンアップの近傍半径)。
        /// zoneMaterial: 近くの素材色で説明できない画素で、素材色に使う色(MostChromaticSample)。
        /// includedPx: 含めるマスクの画素(null = なし)。利用者が素材と指定したので解析せず、素材色の推定にも使わない。
        /// exMin/exMax: 書き込んだ可能性のある矩形(strength の bbox ± 近傍半径)。ループはここまで回す。
        /// maskExcluded: 除外マスク画素(null=マスクなし)。呼び出し側は矩形 ± (2·近傍半径+1) まで埋めること。
        /// 除外画素には書かず、背景の参照にも使わない(strength=0 だが背景ではない保護パーツでありうる)。
        /// </summary>
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

        private static void AnalyzeMixtureBand(
            Color32[] originalPixels, float[] strength, int w, int h, int radius,
            in RecolorParams rc, float[] regMidMap, float regLmid,
            Color zoneMaterial, bool[] includedPx,
            Color32[] mixedPixels, float[] mixAlpha,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY,
            out int exMinX, out int exMinY, out int exMaxX, out int exMaxY,
            bool[] maskExcluded, CancellationToken ct = default)
        {
            const int R = MixBandRadius;
            int B = Mathf.Max(radius, R + 1);
            int ex0 = Mathf.Max(0, bbMinX - B), ex1 = Mathf.Min(w - 1, bbMaxX + B);
            int ey0 = Mathf.Max(0, bbMinY - B), ey1 = Mathf.Min(h - 1, bbMaxY + B);
            exMinX = ex0; exMinY = ey0; exMaxX = ex1; exMaxY = ey1;
            // 素材を探す窓(±B)が読む範囲 G(その画素の 3×3 の選択数 n3 を使う)と、窓和の入力に要る範囲。
            int gx0 = Mathf.Max(0, ex0 - B), gx1 = Mathf.Min(w - 1, ex1 + B);
            int gy0 = Mathf.Max(0, ey0 - B), gy1 = Mathf.Min(h - 1, ey1 + B);
            int sx0 = Mathf.Max(0, gx0 - B), sx1 = Mathf.Min(w - 1, gx1 + B);
            int sy0 = Mathf.Max(0, gy0 - B), sy1 = Mathf.Min(h - 1, gy1 + B);

            int len = w * h;
            // 窓和(近傍の選択数)は、範囲 S の整数の累積和から窓ごとに 4 回の読み出しで引く。sel / any は 0/1
            // なので、以前の BoxFilterSum(浮動小数の窓和)は常に正確な整数で、ここで引く値と同じ。
            // 累積和は範囲 S と同じ寸法 (satW × satH)。
            int satW = sx1 - sx0 + 1, satH = sy1 - sy0 + 1;
            float[] sel = null, any = null;
            int[] satSel = null, satAny = null;
            try
            {
                sel = s_floatPool.Rent(len);
                any = s_floatPool.Rent(len);
                satSel = s_intPool.Rent(satW * satH);
                satAny = s_intPool.Rent(satW * satH);
                var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
                var selL = sel; var anyL = any;
                Parallel.For(sy0, sy1 + 1, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = sx0; x <= sx1; x++)
                    {
                        int i = rowOff + x;
                        float s = originalPixels[i].a > 0 ? strength[i] : 0f;
                        // 含める画素は選択には数えるが、素材色の推定(確かな選択)には使わない。
                        selL[i] = s >= MixSolidStrength && (includedPx == null || !includedPx[i]) ? 1f : 0f;
                        anyL[i] = s > 0f ? 1f : 0f;
                    }
                });
                BuildCountSat(sel, satSel, w, sx0, sy0, sx1, sy1, po);
                BuildCountSat(any, satAny, w, sx0, sy0, sx1, sy1, po);
                var satSelL = satSel; var satAnyL = satAny;
                // 窓(画像の内側に切った範囲)は常に S の内側か、S と同じく画像の端で切られる
                // (S = 調べる範囲 ± 2B、窓は最大 ±B を G = 調べる範囲 ± B から取る)ので、S で切ってよい。
                // ここから any は「面か」の覚え書きに使う(窓和を取り終えたので不要)。値 0/1 = まだ調べていない、
                // FlatYes / FlatNo = 調べた結果。背景の候補を探す範囲(G)は any を埋めた範囲(S)の内側にある。
                // 同じ画素を別の行のスレッドが同時に調べても、書く値は同じなので競合しない。
                var flatL = any;

                var rcL = rc;
                float minChromaSq = MixMinChroma * MixMinChroma;
                float flatSq = MixFlatChroma * MixFlatChroma;
                int minSolidNear = 2 * B + 1;      // 未選択側: 窓 ±B を横切る 1 本の線ぶんの選択済み画素

                // 近くの素材色で説明できない画素で使う素材色(ゾーンの最も鮮やかなサンプル)と、その再着色後の色。
                byte zRb = (byte)Mathf.Clamp(Mathf.RoundToInt(zoneMaterial.r * 255f), 0, 255);
                byte zGb = (byte)Mathf.Clamp(Mathf.RoundToInt(zoneMaterial.g * 255f), 0, 255);
                byte zBb = (byte)Mathf.Clamp(Mathf.RoundToInt(zoneMaterial.b * 255f), 0, 255);
                float zM = (zRb + zGb + zBb) * (1f / 3f);
                float zcR = zRb - zM, zcG = zGb - zM, zcB = zBb - zM;
                float zc2 = zcR * zcR + zcG * zcG + zcB * zcB;
                bool hasZoneMaterial = zc2 >= minChromaSq;
                Color32 zTo = hasZoneMaterial
                    ? RecolorPixel(zRb, zGb, zBb, Mathf.Max(zRb, Mathf.Max(zGb, zBb)) / 255f, 1f, in rcL, regLmid)
                    : default;

                Parallel.For(ey0, ey1 + 1, po, y =>
                {
                    // 素材の画素の候補(窓 ±B の中)の位置。行ごとに 1 回だけ確保する。
                    var setIdx = new int[(2 * B + 1) * (2 * B + 1)];
                    int row = y * w;
                    for (int x = ex0; x <= ex1; x++)
                    {
                        int i = row + x;
                        mixAlpha[i] = -1f;
                        bool isSolid = selL[i] > 0f;
                        if (isSolid)
                        {
                            // 窓 ±R が全部選択済み = 内側。境界の帯ではない。
                            int cnt = (Mathf.Min(w - 1, x + R) - Mathf.Max(0, x - R) + 1)
                                    * (Mathf.Min(h - 1, y + R) - Mathf.Max(0, y - R) + 1);
                            if (CountInWindow(satSelL, satW, sx0, sy0, sx1, sy1, x, y, R) >= cnt) continue;
                        }
                        // 未選択・弱い選択の画素: 近く(±B)にまとまった選択があるか、選択(強さを問わず)から
                        // ±R 以内にあるものだけを調べる。
                        int nBi = CountInWindow(satSelL, satW, sx0, sy0, sx1, sy1, x, y, B);
                        if (!isSolid && nBi < minSolidNear
                            && CountInWindow(satAnyL, satW, sx0, sy0, sx1, sy1, x, y, R) <= 0) continue;
                        if (includedPx != null && includedPx[i]) continue;
                        Color32 op = originalPixels[i];
                        if (op.a == 0) continue;
                        if (maskExcluded != null && maskExcluded[i]) continue;
                        float st = strength[i];

                        // F: 近くの「内側の選択画素」(3×3 が全部選択済み)の平均。窓 ±R に無ければ輪を 1px ずつ
                        // 広げ、最初に見つかった輪で止める。それでも無ければ、未選択・弱い選択の画素に限り、
                        // 選択済み画素すべて(細い素材の線)で同じことをする。
                        int setN = 0;
                        if (nBi > 0)
                        {
                            for (int pass = 0; pass < 2 && setN == 0; pass++)
                            {
                                if (pass == 1 && isSolid) break;
                                for (int d = R; d <= B && setN == 0; d++)
                                {
                                    int y0w = Mathf.Max(0, y - d), y1w = Mathf.Min(h - 1, y + d);
                                    int x0w = Mathf.Max(0, x - d), x1w = Mathf.Min(w - 1, x + d);
                                    for (int yy = y0w; yy <= y1w; yy++)
                                    {
                                        int r2 = yy * w;
                                        int ady = yy > y ? yy - y : y - yy;
                                        for (int xx = x0w; xx <= x1w; xx++)
                                        {
                                            // d > R のときは輪(チェビシェフ距離 = d)だけを見る。
                                            if (d > R && ady < d && (xx > x ? xx - x : x - xx) < d) continue;
                                            int j = r2 + xx;
                                            if (j == i || selL[j] <= 0f) continue;
                                            if (pass == 0 && CountInWindow(satSelL, satW, sx0, sy0, sx1, sy1, xx, yy, 1) < 9) continue;
                                            setIdx[setN++] = j;
                                        }
                                    }
                                }
                            }
                        }
                        float lfR = 0f, lfG = 0f, lfB = 0f, lcR = 0f, lcG = 0f, lcB = 0f, lc2 = 0f, lfM = 0f, lNoise = 0f;
                        bool local = setN > 0;
                        if (local)
                        {
                            for (int k = 0; k < setN; k++)
                            {
                                Color32 o = originalPixels[setIdx[k]];
                                lfR += o.r; lfG += o.g; lfB += o.b;
                            }
                            float inv = 1f / setN;
                            lfR *= inv; lfG *= inv; lfB *= inv;
                            lfM = (lfR + lfG + lfB) * (1f / 3f);
                            lcR = lfR - lfM; lcG = lfG - lfM; lcB = lfB - lfM;
                            lc2 = lcR * lcR + lcG * lcG + lcB * lcB;
                            // 素材色を推定した画素の色度のばらつき(ノイズ・質感の大きさ)。
                            float v = 0f;
                            for (int k = 0; k < setN; k++)
                            {
                                Color32 o = originalPixels[setIdx[k]];
                                float oM = (o.r + o.g + o.b) * (1f / 3f);
                                float dr = o.r - oM - lcR, dg = o.g - oM - lcG, db = o.b - oM - lcB;
                                v += dr * dr + dg * dg + db * db;
                            }
                            lNoise = Mathf.Sqrt(v * inv);
                            // 無彩に近い素材(ごく暗い陰・淡い明部): α を色度で測れないので解析しない(上のコメント参照)。
                            if (lc2 < minChromaSq) continue;
                        }
                        if (!local && !hasZoneMaterial) continue;

                        float pM = (op.r + op.g + op.b) * (1f / 3f);
                        float cpR = op.r - pM, cpG = op.g - pM, cpB = op.b - pM;
                        if (isSolid)
                        {
                            // 色度の射影が、素材色の推定に使った画素(無ければゾーンの素材色)のどれよりも低いときだけ
                            // 調べる。それ以上なら素材自身の模様・陰影の範囲(RecolorPixel が塗る)。
                            float piMin;
                            float piP;
                            if (local)
                            {
                                piP = (cpR * lcR + cpG * lcG + cpB * lcB) / lc2;
                                piMin = float.MaxValue;
                                for (int k = 0; k < setN; k++)
                                {
                                    Color32 o = originalPixels[setIdx[k]];
                                    float oM = (o.r + o.g + o.b) * (1f / 3f);
                                    float pi = ((o.r - oM) * lcR + (o.g - oM) * lcG + (o.b - oM) * lcB) / lc2;
                                    if (pi < piMin) piMin = pi;
                                }
                            }
                            else
                            {
                                piP = (cpR * zcR + cpG * zcG + cpB * zcB) / zc2;
                                piMin = 1f;
                            }
                            if (piP >= piMin - MixCoreRangeMargin) continue;
                        }

                        // c(B) の候補: 近くの「面になっている未選択画素」。輪を 1px ずつ広げ、最初に見つかった輪の平均。
                        float bR = 0f, bG = 0f, bB = 0f;
                        int bN = 0;
                        for (int d = 1; d <= B && bN == 0; d++)
                        {
                            int y0w = Mathf.Max(0, y - d), y1w = Mathf.Min(h - 1, y + d);
                            int x0w = Mathf.Max(0, x - d), x1w = Mathf.Min(w - 1, x + d);
                            for (int yy = y0w; yy <= y1w; yy++)
                            {
                                int r2 = yy * w;
                                int ady = yy > y ? yy - y : y - yy;
                                for (int xx = x0w; xx <= x1w; xx++)
                                {
                                    if (ady < d && (xx > x ? xx - x : x - xx) < d) continue;
                                    int j = r2 + xx;
                                    if (strength[j] > 0f) continue;
                                    Color32 o = originalPixels[j];
                                    if (o.a == 0 || (maskExcluded != null && maskExcluded[j])) continue;
                                    float fc = flatL[j];
                                    if (fc < FlatYes)
                                    {
                                        // 面の判定: 3×3 の隣が全部未選択で、色度が揃っていて、±R に透明が無い。
                                        float oM = (o.r + o.g + o.b) * (1f / 3f);
                                        float ocR = o.r - oM, ocG = o.g - oM, ocB = o.b - oM;
                                        bool flat = true;
                                        int ty1 = Mathf.Min(h - 1, yy + R), tx0 = Mathf.Max(0, xx - R), tx1 = Mathf.Min(w - 1, xx + R);
                                        for (int ny = Mathf.Max(0, yy - R); ny <= ty1 && flat; ny++)
                                        {
                                            int r3 = ny * w;
                                            for (int nx = tx0; nx <= tx1; nx++)
                                                if (originalPixels[r3 + nx].a == 0) { flat = false; break; }
                                        }
                                        for (int ny = yy - 1; ny <= yy + 1 && flat; ny++)
                                        {
                                            if (ny < 0 || ny >= h) continue;
                                            int r3 = ny * w;
                                            for (int nx = xx - 1; nx <= xx + 1; nx++)
                                            {
                                                if (nx < 0 || nx >= w) continue;
                                                int k = r3 + nx;
                                                if (k == j) continue;
                                                if (strength[k] > 0f) { flat = false; break; }
                                                Color32 q = originalPixels[k];
                                                float qM = (q.r + q.g + q.b) * (1f / 3f);
                                                float eR0 = q.r - qM - ocR, eG0 = q.g - qM - ocG, eB0 = q.b - qM - ocB;
                                                if (eR0 * eR0 + eG0 * eG0 + eB0 * eB0 > flatSq) { flat = false; break; }
                                            }
                                        }
                                        fc = flat ? FlatYes : FlatNo;
                                        flatL[j] = fc;
                                    }
                                    if (fc != FlatYes) continue;
                                    bR += o.r; bG += o.g; bB += o.b; bN++;
                                }
                            }
                        }
                        float cbR = 0f, cbG = 0f, cbB = 0f, bM = 0f;
                        if (bN > 0)
                        {
                            float inv = 1f / bN;
                            float mR = bR * inv, mG = bG * inv, mB = bB * inv;
                            bM = (mR + mG + mB) * (1f / 3f);
                            cbR = mR - bM; cbG = mG - bM; cbB = mB - bM;
                        }

                        // 近くの素材色で判定し、それで説明できない(より鮮やか / そもそも無い)ときはゾーンの素材色で。
                        float alpha = 0f;
                        int kind = MixOther;
                        bool useLocal = local;
                        if (local)
                            kind = ClassifyMixture(cpR, cpG, cpB, pM, lcR, lcG, lcB, lc2, lfM,
                                bN > 0, cbR, cbG, cbB, bM, lNoise, out alpha);
                        if ((!local || kind == MixOver) && hasZoneMaterial)
                        {
                            useLocal = false;
                            kind = ClassifyMixture(cpR, cpG, cpB, pM, zcR, zcG, zcB, zc2, zM,
                                bN > 0, cbR, cbG, cbB, bM, lNoise, out alpha);
                        }

                        if (kind == MixMaterial)
                        {
                            // 素材そのもの・素材の濃淡。弱く選択されているなら全強度で塗る(確かな選択と未選択は従来どおり)。
                            if (!isSolid && st > 0f) mixAlpha[i] = MixAsMaterial;
                            continue;
                        }
                        if (kind == MixBackground)
                        {
                            if (st > 0f) { mixAlpha[i] = 0f; mixedPixels[i] = op; }   // 選択されているが素材が乗っていない → 変えない
                            continue;
                        }
                        if (kind != MixMixture) continue;   // 説明できない画素は従来の経路(strength どおり)

                        // F' − F: 素材色を、パイプラインが内側を塗るのと同じ関数に通す。
                        byte fRb, fGb, fBb;
                        Color32 fTo;
                        if (useLocal)
                        {
                            fRb = (byte)Mathf.Clamp(Mathf.RoundToInt(lfR), 0, 255);
                            fGb = (byte)Mathf.Clamp(Mathf.RoundToInt(lfG), 0, 255);
                            fBb = (byte)Mathf.Clamp(Mathf.RoundToInt(lfB), 0, 255);
                            float fV = Mathf.Max(fRb, Mathf.Max(fGb, fBb)) / 255f;
                            int fAt = setIdx[0];
                            float regL = (regMidMap != null && regMidMap[fAt] > 0f) ? regMidMap[fAt] : regLmid;
                            fTo = RecolorPixel(fRb, fGb, fBb, fV, 1f, in rcL, regL);
                        }
                        else
                        {
                            fRb = zRb; fGb = zGb; fBb = zBb;
                            fTo = zTo;
                        }
                        mixedPixels[i] = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(op.r + alpha * (fTo.r - fRb)), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(op.g + alpha * (fTo.g - fGb)), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(op.b + alpha * (fTo.b - fBb)), 0, 255),
                            op.a);
                        mixAlpha[i] = alpha;
                    }
                });
            }
            finally
            {
                if (satAny != null) s_intPool.Return(satAny);
                if (satSel != null) s_intPool.Return(satSel);
                if (any != null) s_floatPool.Return(any);
                if (sel != null) s_floatPool.Return(sel);
            }
        }
    }
}
