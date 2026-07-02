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
    // PixelProcessor: 連結成分アンカリング(flood fill)と、フル画像で解いた keep/マップの部分クロップ転写。
    internal static partial class PixelProcessor
    {
        // 連結成分アンカリングの確信度コア絶対床(matchConf 不在時のフォールバック専用)。
        private const float CoreAbsoluteFloor = 0.6f;

        /// <summary>
        /// 連結成分アンカリング: strength&gt;0 の画素を4連結でラベリングし、確信度コアを含む成分のみ残す。
        /// コア = matchConf &gt; 0(= マッチ距離が固定半径 ColorZone.CoreMatchDistance 未満 = サンプル色に
        /// 確信できる近さ。matchConf が null のときのみ従来の strength&gt;=max(P75,CoreAbsoluteFloor) に
        /// フォールバック)。matchConf は strength(有彩では彩度ゲートのみ/edgeSoftness=0 で二値)と違い
        /// 「色がどれだけ近いか」を表すため:
        ///   ・色は遠いが彩度が高い別素材の塊 → matchConf=0 → コアにならず落ちる(過選択ノイズを除去)
        ///   ・色は近いが strength が中位の分離領域(陰影/別アイランド) → matchConf&gt;0 → 残る(取りこぼし防止)
        /// 半径が固定なので tolerance を上げても下げてもコア判定は安定(tolerance≤半径なら全マッチが
        /// コア=FF 実質 OFF で取りこぼしゼロ、tolerance&gt;半径で初めて遠い滲みを落とす)。サイズでは
        /// 切らない（小さくても高信頼な成分＝三角などは残す）。確信できるコアが一つも無い(全体が弱マッチ)
        /// 場合は recall 保護のため絞り込まない(=連結制約 OFF と同挙動)。
        /// seedX&gt;=0 のときはその成分だけを残す上書きモード(無効シード=bleed/背景上 なら自動へフォールバック)。
        /// 連結予測子(strength&gt;0 &amp;&amp; α&gt;=128)・4近傍は BuildComponentMedianLMap と一致させ成分定義を統一する。
        /// </summary>
        private static void ApplyConnectedComponentMask(
            float[] strength, float[] matchConf, Color32[] px, int w, int h, int seedX, int seedY)
        {
            // matched 画素(strength>0 && α>=128)の bbox。ラベリングは bbox 内に限定(全画素確保を回避)。
            int minX = w, maxX = -1, minY = h, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rb = y * w;
                for (int x = 0; x < w; x++)
                    if (strength[rb + x] > 0f && px[rb + x].a >= 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            }
            if (maxX < 0) return; // マッチ皆無

            // フォールバック用コア閾値(matchConf 不在時のみ使用)= 正の strength 群の P75 と絶対床の
            // 大きい方。matchConf があるときは固定半径ベースの色一致確信度で判定するので不要。
            float coreThreshold = 0f;
            int posCount = 0;
            if (matchConf == null)
            {
                var sHist = new int[256];
                for (int y = minY; y <= maxY; y++)
                {
                    int rb = y * w;
                    for (int x = minX; x <= maxX; x++)
                    {
                        int gi = rb + x;
                        if (strength[gi] > 0f && px[gi].a >= 128)
                        {
                            sHist[Mathf.Clamp((int)(strength[gi] * 255f), 0, 255)]++;
                            posCount++;
                        }
                    }
                }
                coreThreshold = Mathf.Max(
                    HistValueAtPercentile(sHist, posCount, 0.75f, 1f), CoreAbsoluteFloor);
            }

            // コア判定: matchConf があれば色一致確信度(>0 = 固定半径内)で、無ければ strength 閾値で判定。
            bool IsCore(int gi) => matchConf != null
                ? matchConf[gi] > 0f
                : strength[gi] >= coreThreshold;

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            int[] label = new int[bw * bh];
            var hasCore = new List<bool>();   // hasCore[lab-1] = その成分にコア画素があるか
            var queue = new Queue<int>();

            for (int ly = 0; ly < bh; ly++)
            {
                for (int lx = 0; lx < bw; lx++)
                {
                    int li = ly * bw + lx;
                    if (label[li] != 0) continue;
                    if (!(strength[(ly + minY) * w + (lx + minX)] > 0f
                          && px[(ly + minY) * w + (lx + minX)].a >= 128)) continue;

                    int lab = hasCore.Count + 1;
                    bool core = false;
                    label[li] = lab;
                    queue.Enqueue(li);
                    while (queue.Count > 0)
                    {
                        int ci = queue.Dequeue();
                        int cx = ci % bw, cy = ci / bw;
                        if (IsCore((cy + minY) * w + (cx + minX))) core = true;
                        TryEnq(ci - 1, cx > 0);
                        TryEnq(ci + 1, cx < bw - 1);
                        TryEnq(ci - bw, cy > 0);
                        TryEnq(ci + bw, cy < bh - 1);
                    }
                    hasCore.Add(core);

                    void TryEnq(int ni, bool inBounds)
                    {
                        if (!inBounds || label[ni] != 0) return;
                        int nx = ni % bw, ny = ni / bw;
                        if (!(strength[(ny + minY) * w + (nx + minX)] > 0f
                              && px[(ny + minY) * w + (nx + minX)].a >= 128)) return;
                        label[ni] = lab;
                        queue.Enqueue(ni);
                    }
                }
            }

            // 残す成分を決定。seed 上書き優先、無効/未指定ならコア規則。
            int keepLabel = 0; // 0 = コア規則, >0 = その label だけ残す
            if (seedX >= minX && seedX <= maxX && seedY >= minY && seedY <= maxY)
            {
                int sl = label[(seedY - minY) * bw + (seedX - minX)];
                if (sl != 0) keepLabel = sl; // 有効シード → その成分のみ
                // sl==0(bleed/背景上) → 自動へフォールバック(keepLabel=0 のまま)
            }

            if (keepLabel == 0)
            {
                bool anyCore = false;
                for (int c = 0; c < hasCore.Count; c++) if (hasCore[c]) { anyCore = true; break; }
                if (!anyCore) return; // 確信できるコアが皆無 → 絞り込まない(recall 保護)
            }

            for (int ly = 0; ly < bh; ly++)
            {
                int rb = (ly + minY) * w;
                for (int lx = 0; lx < bw; lx++)
                {
                    int lab = label[ly * bw + lx];
                    if (lab == 0) continue;
                    bool keep = keepLabel > 0 ? (lab == keepLabel) : hasCore[lab - 1];
                    if (!keep) strength[rb + (lx + minX)] = 0f;
                }
            }
        }

        /// <summary>
        /// 連結成分アンカリングの keep(フル画像で解いた「残す画素」1bit/画素)を部分クロップへ転写する。
        /// クロップの各画素をフル座標(originX/Y オフセット)で keep 参照し、残さない画素の strength を 0 化。
        /// これにより詳細プレビュー(クロップ)が大域演算を再実行せずにメイン/最終と完全一致する。
        /// </summary>
        // フル画像 per-pixel マップ(成分別中央値 L 等)から、クロップ(originX/Y, w×h)に対応する
        // 矩形を切り出してクロップ座標の新しい配列に詰める。詳細プレビューがフル画像と同じ成分基準で
        // 再着色できるようにするための転写。行ごとに連続コピーするだけ(全画素 1 回読み)。
        private static float[] CropFullMidMap(float[] full, int w, int h, int originX, int originY, int fullW)
        {
            var map = new float[w * h];
            for (int y = 0; y < h; y++)
                System.Array.Copy(full, (y + originY) * fullW + originX, map, y * w, w);
            return map;
        }

        private static void ApplyCachedKeepMask(
            float[] strength, int w, int h, int originX, int originY, int fullW, ulong[] keep)
        {
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * w;
                int fRow = (y + originY) * fullW + originX;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOff + x;
                    if (strength[i] <= 0f) continue;
                    int gi = fRow + x;
                    int word = gi >> 6;
                    if (word < 0 || word >= keep.Length || (keep[word] & (1UL << (gi & 63))) == 0UL)
                        strength[i] = 0f;
                }
            }
        }
    }
}
