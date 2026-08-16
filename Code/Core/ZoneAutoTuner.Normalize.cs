// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    // ZoneAutoTuner: スポイト位置の正規化（クリック画素 → パーツの代表地色）。
    internal static partial class ZoneAutoTuner
    {
        // ─────────────────── スポイト位置の正規化 ───────────────────
        // 【解決する問題】自動調整の導出はすべて「クリックした 1 texel の HSV」を起点にしていた。
        // そのためパーツの中でどこをクリックするかで結果が大きく変わる（実測 2026-08-16、実 C#
        // ハーネス --autotune、6 被写体 × クリック 6 位置）:
        //   - 脱彩したツヤをクリックすると彩度バンド下限 satFloor = max(sS*frac, …) が一緒に落ち、
        //     帯が開いて別素材まで巻き込む（スニーカー precision 0.99→0.69、スカート IoU 0.77→0.26）。
        //   - 許容値そのものが位置で最大 3.4 倍ぶれる（ブーツ 0.080〜0.273）。
        // クリック位置は「どのパーツを指したか」の情報であって「そのパーツの地色が何か」ではない。
        // ここでクリック色をパーツの代表地色へ寄せてから解析すれば、以降の全導出（彩度バンド・
        // トーン代表・許容値・彩度ガード・シャドウ免除）が同じ入力を見るので位置に依存しなくなる。
        //
        // 【手順】いずれもテクスチャ統計のみで決まり、特定色・座標・キャラに依存しない。
        //   1. クリックと同色相帯（|Δhue| < AutoToneHueBand）かつ「クリック以上の彩度」を持つ
        //      不透明画素を集める。彩度下限をクリック自身に置くのは、陰影とツヤは地色から彩度を
        //      落とす方向に働くため（＝クリックは地色以下の彩度に落ちることはあっても、地色より
        //      濃い側へ外れることは少ない）。この下限が無いと、同色相帯に居る大面積のほぼ無彩な
        //      別素材に最頻値を奪われる（実測: 暗い星柄スカートで代表色が S=0.96 の地色ではなく
        //      隣の S=0.23 の暗色へ飛び、recall 0.87→0.62）。下限より濃い地色は必ず残るので、
        //      パーツ内のどこをクリックしても同じ最頻値へ収束する。
        //   2. その V ヒストグラムから、クリックの V を含むトーン連結域だけを残す
        //      （DeriveAutoTonalSamples と同じ TryConnectedValueRange）。同色相でもベース明度が
        //      段違いの別素材（暗い革地に対する明るい生地など）へ代表色が飛ぶのを防ぐ。
        //   3. 連結域内で最頻の (S,V) bin（±1 bin）の平均 RGB を代表地色とする。ツヤ・深い影・柄は
        //      面積比で少数なので、最頻値は地色に落ちる。クリックがツヤでも影でも同じ bin に来る。
        //
        // 【適用範囲】無彩クリック（sS < AchromaSampleSatMax）は色相が定まらず、同じ手順では
        // パーツを特定できないので対象外（従来どおりクリック色をそのまま使う）。
        private const int   NormSatBins   = 16;   // 彩度の分解能。bin 内は実色の平均を取るので粗くてよい
        private const int   NormValBins   = 32;   // 明度の分解能。粗いほど連結が切れにくく、代表色は平均で決まる
        private const float NormSatFloorFrac = 0.95f; // 候補の彩度下限 = クリック彩度 × これ（8bit 量子化ぶんの緩み）
        private const float NormMinShift  = 0.02f; // これ未満の移動なら正規化しない（クリックが既に地色）
        // クリックが「パーツの主要な色帯の中」に居るかの判定。クリック bin 近傍の画素数が
        // 地色帯(最頻 bin 近傍)のこの割合に満たなければ、クリックはツヤ・深い影・柄といった裾。
        // 陰影は地色を数 bin に広げるので、地色を踏んだクリックの近傍は最頻値の数分の 1 には収まる
        // 一方、スペキュラや柄はパーツ面積の 1% 前後しかない。1/10 はその間に十分な余裕で入る。
        //
        // この門を付ける理由: 正規化を無条件に効かせると、既に地色を踏んでいる良いクリックまで
        // 動かしてしまい、代表色推定のわずかな偏りがそのまま品質低下になった(実測: 正解サンプルで
        // quanstella-skirt precision 0.95→0.54、合成 adjacent_similar が隣接パーツへ滲んで
        // precision 1.00→0.50)。正規化は「外した位置を救う」ための補正であって、
        // 「当たっている位置を作り直す」ためのものではない。
        private const float NormOutlierFrac = 0.10f;

        /// <summary>
        /// クリック色 <paramref name="zone"/>.sampleColor をパーツの代表地色へ正規化する。
        /// 正規化できないとき（無彩クリック / 候補画素が少ない / トーン連結域を確定できない /
        /// 移動量が NormMinShift 未満）は false を返し、呼び出し側はクリック色をそのまま使う。
        /// </summary>
        private static bool TryNormalizeSample(
            Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, HsvGrid hsv, out Color normalized)
        {
            normalized = zone.sampleColor;
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            if (sS < AchromaSampleSatMax) return false; // 無彩クリックは色相でパーツを特定できない

            // 無彩画素は Unity の RGBToHSV が hue=0 へ丸めるため、暖色クリックへ大量に誤混入する。
            // クリック彩度の床（下記）は通常この無彩床より高いが、低彩度クリックでも下回らないようにする。
            float satFloor = Mathf.Max(AchromaSampleSatMax, sS * NormSatFloorFrac);

            int SB = NormSatBins, VB = NormValBins;
            var cnt = new int[SB * VB];
            var sumR = new float[SB * VB];
            var sumG = new float[SB * VB];
            var sumB = new float[SB * VB];
            var vHist = new int[VB];
            int total = 0;

            int stride = hsv.stride;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float pH = hsv.h[grow + gx], pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];
                    if (pS < satFloor) continue;         // クリックより淡い側（＝別素材/脱彩の裾）を除外
                    float hd = Mathf.Abs(pH - sH); if (hd > 0.5f) hd = 1f - hd;
                    if (hd >= AutoToneHueBand) continue; // 別色相パーツを除外

                    int sb = Mathf.Clamp((int)(pS * SB), 0, SB - 1);
                    int vb = Mathf.Clamp((int)(pV * VB), 0, VB - 1);
                    int i = sb * VB + vb;
                    cnt[i]++;
                    sumR[i] += c.r / 255f;
                    sumG[i] += c.g / 255f;
                    sumB[i] += c.b / 255f;
                    vHist[vb]++;
                    total++;
                }
            }
            if (total < AutoToneMinPixels) return false;
            if (!TryConnectedValueRange(vHist, total, sV, out int loBin, out int hiBin)) return false;

            // トーン連結域内で最も画素の多い (S,V) bin = パーツの地色。
            // 彩度だけを最頻値にして明度は連結域全体で平均する版も試したが、明るい柄やツヤを持つ
            // パーツでは代表色が「テクスチャに実在しない中間色」へ流れ、そこから導出した許容値では
            // 地色をほとんど拾えなくなった（実測: 暗い星柄スカートで recall 0.87→0.02）。
            // 面積で決まる最頻値は、クリックがツヤでも深い影でも同じ bin に落ちる。
            int bestSb = -1, bestVb = -1, bestCnt = 0;
            for (int sb = 0; sb < SB; sb++)
            {
                for (int vb = loBin; vb <= hiBin; vb++)
                {
                    int n = cnt[sb * VB + vb];
                    if (n > bestCnt) { bestCnt = n; bestSb = sb; bestVb = vb; }
                }
            }
            if (bestSb < 0) return false;

            // 量子化で山が隣の bin へ割れても代表色がぶれないよう ±1 bin まで含めて平均する。
            int sbLo = Mathf.Max(0, bestSb - 1), sbHi = Mathf.Min(SB - 1, bestSb + 1);
            int vbLo = Mathf.Max(loBin, bestVb - 1), vbHi = Mathf.Min(hiBin, bestVb + 1);
            float r = 0f, g = 0f, b = 0f;
            int n2 = 0;
            for (int sb = sbLo; sb <= sbHi; sb++)
            {
                for (int vb = vbLo; vb <= vbHi; vb++)
                {
                    int i = sb * VB + vb;
                    r += sumR[i]; g += sumG[i]; b += sumB[i];
                    n2 += cnt[i];
                }
            }
            if (n2 < MinNearSampleCount) return false; // 代表色の平均を取るには標本が足りない

            // クリックが既に地色帯に居るなら触らない（同じ ±1 bin の窓で数えて比較する）。
            int clickSb = Mathf.Clamp((int)(sS * SB), 0, SB - 1);
            int clickVb = Mathf.Clamp((int)(sV * VB), 0, VB - 1);
            int clickN = 0;
            for (int sb = Mathf.Max(0, clickSb - 1); sb <= Mathf.Min(SB - 1, clickSb + 1); sb++)
                for (int vb = Mathf.Max(0, clickVb - 1); vb <= Mathf.Min(VB - 1, clickVb + 1); vb++)
                    clickN += cnt[sb * VB + vb];
            if (clickN >= n2 * NormOutlierFrac) return false;

            float inv = 1f / n2;
            var rep = new Color(r * inv, g * inv, b * inv, 1f);
            if (ColorDist(rep, zone.sampleColor) < NormMinShift) return false; // 既に地色
            normalized = rep;
            return true;
        }

        /// <summary>
        /// V ヒストグラム上で、明度 <paramref name="sV"/> を含む「トーン連結域」の bin 範囲を返す。
        ///
        /// シェーディングは V 上で連続に分布するため、同一パーツの V ヒストグラムはサンプル V を
        /// 含むひと続きの山になる。(ほぼ)空の谷を挟んで現れる別クラスタは、同色相・彩度バンド内でも
        /// ベース明度の異なる別パーツ(暗い革地の靴に対する明るい生地部分など、同じ色味で明度だけが
        /// 段違いの隣接素材)なので母集団から外す。gapFloor は総数比なのでスケール不変。
        /// 1 bin だけの欠けは疎なシェーディングとして橋渡しする。
        /// </summary>
        /// <returns>false = サンプル V の近くに実在 bin が無く、連結域を確定できない。</returns>
        private static bool TryConnectedValueRange(int[] vHist, int total, float sV,
            out int loBin, out int hiBin)
        {
            int VB = vHist.Length;
            int sBin = Mathf.Clamp((int)(sV * VB), 0, VB - 1);
            int gapFloor = Mathf.Max(2, Mathf.CeilToInt(total * AutoToneGapFloorFrac));
            loBin = hiBin = sBin;
            if (vHist[sBin] < gapFloor)
            {
                // サンプル bin 自体が疎(クリック画素が彩度床の直下等)なら最寄りの実在 bin へ寄せる
                int nearest = -1;
                for (int off = 1; off < VB; off++)
                {
                    if (sBin - off >= 0 && vHist[sBin - off] >= gapFloor) { nearest = sBin - off; break; }
                    if (sBin + off < VB && vHist[sBin + off] >= gapFloor) { nearest = sBin + off; break; }
                }
                if (nearest < 0) return false;
                sBin = nearest;
            }
            loBin = hiBin = sBin;
            while (loBin > 0)
            {
                if (vHist[loBin - 1] >= gapFloor) { loBin--; continue; }
                if (loBin > 1 && vHist[loBin - 2] >= gapFloor) { loBin -= 2; continue; }
                break;
            }
            while (hiBin < VB - 1)
            {
                if (vHist[hiBin + 1] >= gapFloor) { hiBin++; continue; }
                if (hiBin < VB - 2 && vHist[hiBin + 2] >= gapFloor) { hiBin += 2; continue; }
                break;
            }
            return true;
        }
    }
}
