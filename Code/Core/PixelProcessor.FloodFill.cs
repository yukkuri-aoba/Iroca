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
        /// 切らない（小さくても高信頼な成分＝小さな模様・飾りなどは、それ自体が変換対象なので残す）。
        /// 確信できるコアが一つも無い(全体が弱マッチ)
        /// 場合は recall 保護のため絞り込まない(=連結制約 OFF と同挙動)。
        /// seedX&gt;=0 のときはその成分だけを残す上書きモード(無効シード=bleed/背景上 なら自動へフォールバック)。
        /// 連結予測子(strength&gt;0 &amp;&amp; α&gt;=128)・4近傍は BuildComponentMedianLMap と一致させ成分定義を統一する。
        /// </summary>
        private static void ApplyConnectedComponentMask(
            float[] strength, float[] matchConf, Color32[] px, int w, int h, int seedX, int seedY,
            CancellationToken ct = default)
        {
            // matched 画素(strength>0 && α>=128)の bbox。ラベリングは bbox 内に限定(全画素確保を回避)。
            if (!TryComputeMatchedBBox(strength, px, w, h, 0f,
                    out int minX, out int minY, out int maxX, out int maxY, ct))
                return; // マッチ皆無

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

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            var ccPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };

            // ───────── 連結成分ラベリング: 行 run + union-find ─────────
            // 旧実装は画素単位の逐次 DFS(bbox 全画素 × 4 近傍を単スレッド)。ここでは
            //   ① 行内の連続 matched 区間(run)を行並列で抽出   ← O(N) 並列
            //   ② 上下に x 範囲が重なる run だけを union-find で結合 ← O(R α), R ≪ N
            //   ③ 成分ごとのコア有無を run 単位の連続アクセスで集計
            // に置き換える。run は行内の左右連結そのもの、上下の重なりは縦の 4 近傍連結
            // そのものなので、成分の分割は DFS と厳密に同一。ラベル番号の付き方は変わるが、
            // 判定に使うのは「その成分にコアがあるか」と「シードの属する成分か」だけで
            // 番号に依存しない = 出力ビット不変。
            int[] runX0 = null, runX1 = null, parent = null, comp = null;
            try
            {
            // 連結予測子。bbox 内の隣接判定でしか使わないので gi は呼び出し側が算出済みの値を渡す。
            // コア判定: matchConf があれば色一致確信度(>0 = 固定半径内)で、無ければ strength 閾値で判定。
            bool useConf = matchConf != null;

            var runCount = new int[bh];
            Parallel.For(0, bh, ccPo, ly =>
            {
                int grb = (ly + minY) * w + minX;
                int n = 0;
                bool prev = false;
                for (int lx = 0; lx < bw; lx++)
                {
                    int gi = grb + lx;
                    bool m = strength[gi] > 0f && px[gi].a >= 128;
                    if (m && !prev) n++;
                    prev = m;
                }
                runCount[ly] = n;
            });
            var rowOff = new int[bh + 1];
            for (int ly = 0; ly < bh; ly++) rowOff[ly + 1] = rowOff[ly] + runCount[ly];
            int R = rowOff[bh];
            if (R == 0) return;   // マッチ皆無

            runX0 = s_intPool.Rent(R);
            runX1 = s_intPool.Rent(R);
            Parallel.For(0, bh, ccPo, ly =>
            {
                int grb = (ly + minY) * w + minX;
                int k = rowOff[ly];
                int lx = 0;
                while (lx < bw)
                {
                    int gi = grb + lx;
                    if (!(strength[gi] > 0f && px[gi].a >= 128)) { lx++; continue; }
                    int s0 = lx;
                    while (lx < bw && strength[grb + lx] > 0f && px[grb + lx].a >= 128) lx++;
                    runX0[k] = s0; runX1[k] = lx - 1; k++;
                }
            });

            parent = s_intPool.Rent(R);
            for (int r = 0; r < R; r++) parent[r] = r;
            var par = parent;
            int Find(int x)
            {
                while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; }
                return x;
            }
            for (int ly = 0; ly + 1 < bh; ly++)
            {
                if ((ly & 1023) == 0) ct.ThrowIfCancellationRequested();
                int i = rowOff[ly], iEnd = rowOff[ly + 1];
                int j = rowOff[ly + 1], jEnd = rowOff[ly + 2];
                while (i < iEnd && j < jEnd)
                {
                    if (runX0[i] <= runX1[j] && runX0[j] <= runX1[i])
                    {
                        int a = Find(i), b = Find(j);
                        if (a != b) { if (b < a) { int t = a; a = b; b = t; } par[b] = a; }
                    }
                    if (runX1[i] < runX1[j]) i++; else j++;
                }
            }

            comp = s_intPool.Rent(R);
            var rootToComp = new Dictionary<int, int>();
            int compCount = 0;
            for (int r = 0; r < R; r++)
            {
                int root = Find(r);
                if (!rootToComp.TryGetValue(root, out int c)) { c = compCount++; rootToComp[root] = c; }
                comp[r] = c;
            }

            var hasCore = new bool[compCount];
            for (int ly = 0; ly < bh; ly++)
            {
                if ((ly & 63) == 0) ct.ThrowIfCancellationRequested();
                int grb = (ly + minY) * w + minX;
                for (int k = rowOff[ly]; k < rowOff[ly + 1]; k++)
                {
                    int c = comp[k];
                    if (hasCore[c]) continue;   // 既に確定した成分は走査不要
                    int x1 = runX1[k];
                    for (int lx = runX0[k]; lx <= x1; lx++)
                    {
                        int gi = grb + lx;
                        if (useConf ? matchConf[gi] > 0f : strength[gi] >= coreThreshold)
                        { hasCore[c] = true; break; }
                    }
                }
            }

            // 残す成分を決定。seed 上書き優先、無効/未指定ならコア規則。
            int keepComp = -1; // -1 = コア規則, >=0 = その成分だけ残す
            if (seedX >= minX && seedX <= maxX && seedY >= minY && seedY <= maxY)
            {
                int sly = seedY - minY, slx = seedX - minX;
                for (int k = rowOff[sly]; k < rowOff[sly + 1]; k++)
                    if (slx >= runX0[k] && slx <= runX1[k]) { keepComp = comp[k]; break; }
                // シードが候補領域外なら、自動アンカリングへフォールバックする。
            }

            if (keepComp < 0)
            {
                bool anyCore = false;
                for (int c = 0; c < compCount; c++) if (hasCore[c]) { anyCore = true; break; }
                if (!anyCore) return; // 確信できるコアが皆無 → 絞り込まない(recall 保護)
            }

            var keepCompLocal = keepComp;
            var hasCoreArr = hasCore;
            var runX0L = runX0; var runX1L = runX1; var compL = comp;
            Parallel.For(0, bh, ccPo, ly =>
            {
                int rb = (ly + minY) * w + minX;
                for (int k = rowOff[ly]; k < rowOff[ly + 1]; k++)
                {
                    int c = compL[k];
                    bool keep = keepCompLocal >= 0 ? (c == keepCompLocal) : hasCoreArr[c];
                    if (keep) continue;
                    int x1 = runX1L[k];
                    for (int lx = runX0L[k]; lx <= x1; lx++) strength[rb + lx] = 0f;
                }
            });
            }
            finally
            {
                if (comp != null) s_intPool.Return(comp);
                if (parent != null) s_intPool.Return(parent);
                if (runX1 != null) s_intPool.Return(runX1);
                if (runX0 != null) s_intPool.Return(runX0);
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
