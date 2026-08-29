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
        // 証拠つき導出(母集団=セグメント)で、上の門を掛けない「クリックがトーン連結域の端に居る」
        // 判定。連結域 [loBin, hiBin] の中でクリック V bin の相対位置 t=(click-lo)/(hi-lo) が
        // [NormEdgeFrac, 1-NormEdgeFrac] の外なら端。地色には両側に陰影が付く(暗部と明部が地色を
        // 挟む)ので、連結域の端は地色ではなく明端/暗端の塗り。端は人口が多いこともあり
        // (実測 2026-08-29: スニーカーの明部帯 V≈0.95 が最頻帯の 1/3 の面積)、人口だけで判定する
        // 上の門はそれを「主要な色帯の中」と誤認して起点のまま残す。起点が明端だと近傍窓の P95 で
        // 導く tolerance が中間調の地色に対するハイライト(色相が回ってやや脱彩)に届かない
        // (recall 0.982、見逃し 84k px。最頻 bin へ寄せると tol 0.08→0.12、recall 0.999)。
        // 門を全面的に外す案は、当たっているクリックまで動かして 5/42 ケースで precision を
        // 落としたので採らない(実測 2026-08-29: boots/skirt/black/eye で IoU −0.025〜−0.053)。
        private const float NormEdgeFrac = 0.10f;

        // 無彩クリック用の分解能。母集団の彩度が [0, AchromaClusterSatMax] の狭帯に潰れるので、
        // 有彩と同じ「彩度 [0,1] を 16 分割」では tint の有無(白布 S≈0.05 と純白 S=0)が同じ bin に
        // 落ち、最頻値が地色と端を区別できない。彩度軸を帯幅で正規化し、明度も細かく取る
        // (近白の V=0.95 と V=1.00 は 32 分割では隣接 bin=クリックが常に地色帯の内側と判定される)。
        private const int NormAchromaSatBins = 16;
        private const int NormAchromaValBins = 128;

        /// <summary>
        /// クリック色 <paramref name="zone"/>.sampleColor をパーツの代表地色へ正規化する。
        /// 正規化できないとき（候補画素が少ない / トーン連結域を確定できない / クリックが既に
        /// 地色帯 / 移動量が NormMinShift 未満）は false を返し、呼び出し側はクリック色をそのまま使う。
        ///
        /// 母集団の採り方だけがクリック彩度で変わり、以降の手順（V 連結域 → 最頻 (S,V) bin →
        /// ±1 bin 平均 → 裾判定）は共通。
        /// </summary>
        private static bool TryNormalizeSample(
            Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, HsvGrid hsv, out Color normalized,
            out float clickT, bool edgeBypass = false)
        {
            normalized = zone.sampleColor;
            clickT = -1f;
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);

            // 中性境界(R≒G≒B)より下のクリックだけを無彩として扱う。無彩 tolerance の分岐点
            // (AchromaSampleSatMax=0.15)まで広げてはいけない: 0.02〜0.15 の「色味の乗ったグレー」
            // には使える色相があり、色相帯を捨てると同じ明度帯に居る別の低彩度素材と混ざる。
            // 実測 2026-08-19: 暖色グレーのパンツ(84,81,77 / h≈0.08)をクリックすると、無彩母集団の
            // 最頻値が面積の大きい真無彩のゴーグル(56,56,56)に落ち、代表地色がそちらへ乗り換えて
            // IoU 0.211→0.000(まったく別のパーツを塗る)になった。中性境界は彩度整合ゲートと同じ
            // 「これ未満の tint は色として扱わない」線なので、判断基準を 1 本に揃える。
            if (sS <= ColorZone.ChromaGateActivateSat)
            {
                // 真の無彩クリック（白/グレー/黒の布）。色相が定まらないので同色相帯は使えないが、
                // 「無彩寄り」という彩度条件がその代わりになる（有彩の別素材はここで落ち、白/中間/黒の
                // 別クラスタは V 連結域が落とす。彩度上限は無彩 tolerance 導出のクラスタ条件と同一）。
                //
                // これが無いと、無彩クリックだけがクリック 1 texel の HSV を起点にしたままになり、
                // トーンの端（白布の純白部分・黒布の最暗部）を踏んだときに導出がパーツ本体を覆えない。
                // 実測 2026-08-19（実 C# ハーネス --autotune、GT 内の V パーセンタイル 6 位置）:
                // quanstella-white は明部クリックで recall 0.92→0.60、quanstella-black は IoU が
                // 位置で 0.72〜0.98、feina-goggles は 0.73〜1.00 に振れていた（有彩は正規化済みで
                // ブレ 0.001〜0.017）。
                return TryNormalizeFromPopulation(pixels, w, h, zone, excluded, maskW, maskH, hsv,
                    sH, sS, sV, useHueBand: false, satFloor: 0f, satCeil: AchromaClusterSatMax,
                    satBinSpan: AchromaClusterSatMax,
                    satBins: NormAchromaSatBins, valBins: NormAchromaValBins, edgeBypass,
                    out normalized, out clickT);
            }

            if (sS < AchromaSampleSatMax) return false; // 色味の乗ったグレー: 従来どおりクリック色のまま

            // 無彩画素は Unity の RGBToHSV が hue=0 へ丸めるため、暖色クリックへ大量に誤混入する。
            // クリック彩度の床（下記）は通常この無彩床より高いが、低彩度クリックでも下回らないようにする。
            float satFloor = Mathf.Max(AchromaSampleSatMax, sS * NormSatFloorFrac);
            return TryNormalizeFromPopulation(pixels, w, h, zone, excluded, maskW, maskH, hsv,
                sH, sS, sV, useHueBand: true, satFloor: satFloor, satCeil: float.MaxValue,
                satBinSpan: 1f, satBins: NormSatBins, valBins: NormValBins, edgeBypass,
                out normalized, out clickT);
        }

        /// <summary>
        /// 彩度帯 [<paramref name="satFloor"/>, <paramref name="satCeil"/>) の（必要なら同色相の）
        /// 画素を母集団として代表地色を推定する共通部。
        /// </summary>
        private static bool TryNormalizeFromPopulation(
            Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, HsvGrid hsv,
            float sH, float sS, float sV, bool useHueBand, float satFloor, float satCeil,
            float satBinSpan, int satBins, int valBins, bool edgeBypass, out Color normalized,
            out float clickT)
        {
            normalized = zone.sampleColor;
            clickT = -1f;

            int SB = satBins, VB = valBins;
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
                    if (pS >= satCeil) continue;         // 有彩の別素材を除外（無彩クリック時のみ有効な上限）
                    if (useHueBand)
                    {
                        float hd = Mathf.Abs(pH - sH); if (hd > 0.5f) hd = 1f - hd;
                        if (hd >= AutoToneHueBand) continue; // 別色相パーツを除外
                    }

                    int sb = Mathf.Clamp((int)(pS / satBinSpan * SB), 0, SB - 1);
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

            // 【無彩のみ】最頻値がトーン連結域の端に立っているなら、それは地色ではなくベタ塗り。
            // 素材の地色には必ず両側に陰影が付く(暗部と明部が地色を挟む)ので、最頻 bin は分布の
            // 内側に来る。逆に、テクスチャへ焼かれた背景レイヤーや UV パディングは単一色の巨大な
            // 塊なので、無彩母集団の最頻値を奪ったうえで V の端（純白なら上端・黒なら下端）に立つ。
            // 有彩クリックは色相帯が背景を除くのでこの現象が起きず、判定も掛けない（出力不変）。
            //
            // 実測 2026-08-19（実 C# ハーネス --autotune）: 純白 BG レイヤーを持つテクスチャで、
            // 白い衣装のどこをクリックしても代表地色が (255,255,255)=背景に落ち、衣装の地色
            // (229,230,242) を踏んだ良いクリックまで recall 0.92→0.60 へ引きずられた。
            // この門で背景を弾くと、地色が分布の内側にある被写体（黒衣装 (32,31,38)・
            // ゴーグル (54,54,54)）の正規化はそのまま効く。
            if (!useHueBand && (bestVb <= loBin || bestVb >= hiBin)) return false;

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
            //
            // 無彩クリックではこの門を掛けない。門は「裾(ツヤ・柄)を踏んだときだけ直す」ための
            // もので、閾値 1/10 は『ツヤや柄はパーツ面積の 1% 前後』という有彩パーツの前提で
            // 引かれている。無彩の布は陰影のランプそのものが面積の大半を占めるため、地色から
            // 大きく外れた暗部を踏んでも「主要な色帯の中」と判定されてしまい、直すべきクリックが
            // 素通りする(実測 2026-08-19: ゴーグルの最暗部クリックで IoU 0.726、地色クリックなら
            // 0.995。黒衣装も最暗部 0.715 対 地色 0.982)。無彩側は上の「連結域の端＝ベタ塗り」門が
            // 暴走を止めるので、地色帯へは常に寄せてよい(既に地色なら下の NormMinShift で止まる)。
            // edgeBypass(証拠つき導出: 母集団がセグメント)では、クリックがトーン連結域の端に
            // 居るときだけこの門を掛けない(NormEdgeFrac の説明を参照)。
            if (useHueBand)
            {
                int clickSb = Mathf.Clamp((int)(sS / satBinSpan * SB), 0, SB - 1);
                int clickVb = Mathf.Clamp((int)(sV * VB), 0, VB - 1);
                clickT = hiBin > loBin
                    ? Mathf.Clamp01((clickVb - loBin) / (float)(hiBin - loBin)) : 0.5f;
                bool atEdge = clickT < NormEdgeFrac || clickT > 1f - NormEdgeFrac;
                int clickN = 0;
                for (int sb = Mathf.Max(0, clickSb - 1); sb <= Mathf.Min(SB - 1, clickSb + 1); sb++)
                    for (int vb = Mathf.Max(0, clickVb - 1); vb <= Mathf.Min(VB - 1, clickVb + 1); vb++)
                        clickN += cnt[sb * VB + vb];
                if (clickN >= n2 * NormOutlierFrac && !(edgeBypass && atEdge)) return false;
            }

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
