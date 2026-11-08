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
    internal static unsafe partial class PixelProcessor
    {
        // 連結成分アンカリングの確信度コア絶対床(matchConf 不在時のフォールバック専用)。
        private const float CoreAbsoluteFloor = 0.6f;
        // 陰影の明度下限(shadowValueFloor)と別パーツの彩度上限(partSatCeiling)で成分を残す条件:
        // 成分の画素の過半が包絡の内側(下限以上・上限以下)なら「本体+その陰影」とみなす。包絡自体が
        // クリック島の広がりより 25% 外に置かれる(ZoneAutoTuner.Evidence.cs の EvShadowFloorMarginFrac・
        // 彩度上限の ÷0.75)ので、同素材の成分は大半が内側に入る。過半が外側の成分は、同色の縁取りや
        // 陰が一部に付いていても、大半が別の明るさ・鮮やかさの別部位として落とす。
        // 実測(2026-10-20、ワンショット 57 件): 1/4 → 1/2 で設計 IoU 0.834 → 0.839(改善 8・悪化 0)。
        // 全ゲートの割合を上げると 0.6 までは伸びるが 0.75 で同素材の陰の多い成分(上衣・瞳)を落として
        // 崩れるので、崖から離れた過半にする。
        private const float ShadowFloorKeepFrac = 0.5f;
        // 別パーツの色相幅(partHueBand)で成分を残す条件(成分の画素のうち帯に入る割合)。明度・彩度より
        // 緩い 1/4 にする: 陰やツヤで色相がずれる素材(髪)では、同じ素材の離れた房でも過半が帯の外に
        // 出る。過半にすると茶髪で房が落ちて再現率 1.000 → 0.965・残存島 3.5% だった(2026-10-20)。
        private const float PartHueKeepFrac = 0.25f;

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
        ///
        /// 陰影の明度下限(shadowValueFloor &gt; 0 のとき): 明度(pixV)がこの値以上の画素が成分の
        /// ShadowFloorKeepFrac に満たない成分 = 「大半がこの素材の陰影レンジより暗い、同色相の離れた
        /// 別パーツ」を落とす(ColorZone.shadowValueFloor
        /// のコメント参照)。本体に地続きの深い影は、成分に明るい画素が含まれるので残る。シード上書きモード
        /// (ユーザーが成分を明示)では適用しない。コアが皆無で絞り込まない場合も下限だけは適用する。
        ///
        /// 別パーツの彩度上限(partSatCeiling &gt; 0 のとき): 明度下限の対。彩度(pixS)がこの値以下の画素が
        /// 成分の ShadowFloorKeepFrac に満たない成分 = 「全体がこの素材の彩度の広がりより鮮やかな別パーツ」を
        /// 落とす(ColorZone.partSatCeiling のコメント参照)。適用条件は明度下限と同じ。
        ///
        /// 別パーツの色相幅(partHueBand &gt; 0 のとき): 色相(pixH)が sampleH から ±partHueBand に入る画素が
        /// 成分の PartHueKeepFrac に満たない成分を落とす。彩度が hueMinSat 未満の画素は色相が不安定
        /// なので帯の内側として数える(ColorZone.partHueBand のコメント参照)。グレーモードかどうかの判定は
        /// 呼び出し側が行い、無彩サンプルでは 0 を渡す。
        ///
        /// スポイト位置(anchorX/anchorY &gt;= 0 のとき): その画素を含む成分は、上の 3 つの包絡ゲートでは
        /// 落とさない。包絡はスポイト位置の島から導いた値なので、その成分が包絡の外になるのは「クリックした
        /// 部位が、地続きの別パーツより小さい(包絡の内側が成分の ShadowFloorKeepFrac・PartHueKeepFrac 未満)」
        /// ときだけで、成分ごと落とすとクリックした場所が何も変わらなくなる。巻き込みへ戻すほうが、
        /// 原因が見えて直せる。コア規則は変えない。
        /// </summary>
        private static void ApplyConnectedComponentMask(
            float* strength, bool* matchConf, Color32* px, int w, int h, int seedX, int seedY,
            PixelHsv hsv = default, float shadowValueFloor = 0f, float partSatCeiling = 0f,
            float partHueBand = 0f, float sampleH = 0f, float hueMinSat = 0f,
            int anchorX = -1, int anchorY = -1,
            CancellationToken ct = default)
        {
            float* pixH = hsv.H, pixS = hsv.S, pixV = hsv.V; int* pixIdx = hsv.Index;
            bool useValueFloor = shadowValueFloor > 0f && pixV != null;
            bool useSatCeil = partSatCeiling > 0f && pixS != null;
            bool useHueBand = partHueBand > 0f && pixH != null && pixS != null;
            bool useFloor = useValueFloor || useSatCeil || useHueBand;   // 色の包絡ゲート(明度下限 / 彩度上限 / 色相幅)のどれかが有効
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
                        if (strength[gi] > 0f && px[gi].a >= OpaqueAlphaMin)
                        {
                            sHist[Mathf.Clamp((int)(strength[gi] * 255f), 0, 255)]++;
                            posCount++;
                        }
                    }
                }
                coreThreshold = Mathf.Max(
                    Histogram.ValueAtPercentile(sHist, posCount, 0.75f, 1f), CoreAbsoluteFloor);
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
            int* runX0 = null, runX1 = null, parent = null, comp = null;
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
                    bool m = strength[gi] > 0f && px[gi].a >= OpaqueAlphaMin;
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
                    if (!(strength[gi] > 0f && px[gi].a >= OpaqueAlphaMin)) { lx++; continue; }
                    int s0 = lx;
                    while (lx < bw && strength[grb + lx] > 0f && px[grb + lx].a >= OpaqueAlphaMin) lx++;
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

            // 成分番号: 結合では小さい方の根を親にするので、成分の根はその成分で最も小さい run の番号になる。
            // r の小さい順に見れば、根に出会うのは成分の中で最初(Find(r) == r)で、残りの run は根より後に来る。
            // 根に出会った順に番号を振るのは、以前の辞書(根 → 番号を初出の順に)と同じ番号になる。
            comp = s_intPool.Rent(R);
            int compCount = 0;
            for (int r = 0; r < R; r++)
            {
                int root = Find(r);
                comp[r] = root == r ? compCount++ : comp[root];
            }

            // コアの有無と包絡の内側の画素数は、run ごとに行並列で数えてから成分へ足す(整数の和なので
            // 足す順に依らない)。以前は全 run の画素を 1 スレッドでなめていた(肌のような大きい成分で律速)。
            var hasCore = new bool[compCount];
            byte* runCore = s_bytePool.Rent(R);
            try
            {
                var runCoreL = runCore; var runX0C = runX0; var runX1C = runX1;
                float coreThr = coreThreshold;
                Parallel.For(0, bh, ccPo, ly =>
                {
                    int grb = (ly + minY) * w + minX;
                    for (int k = rowOff[ly]; k < rowOff[ly + 1]; k++)
                    {
                        byte found = 0;
                        int x1 = runX1C[k];
                        for (int lx = runX0C[k]; lx <= x1; lx++)
                        {
                            int gi = grb + lx;
                            if (useConf ? matchConf[gi] : strength[gi] >= coreThr) { found = 1; break; }
                        }
                        runCoreL[k] = found;
                    }
                });
                for (int r = 0; r < R; r++)
                    if (runCore[r] != 0) hasCore[comp[r]] = true;
            }
            finally
            {
                s_bytePool.Return(runCore);
            }

            // 陰影の明度下限: 成分ごとに「下限に達する画素の割合」を集計する(全 run 走査)。
            // 「1 画素でも達すれば残す」では、暗い別部位に同色の明るい縁取りが地続きで付いている
            // だけで成分全体が残る(実測: 桃ベージュ上衣の床 0.52 に対し、暗紫ジャケットは縁取り
            // Color2 が同色の明色なので生き残った)。割合で判定する。
            // 別パーツの彩度上限・色相幅も同じ形で数える(包絡の内側の画素の割合)。それぞれ独立に判定し、
            // 全部を満たす成分だけを残す(暗すぎる・鮮やかすぎる・色相がずれている、は別々の証拠)。
            bool[] reachesFloor = null;
            if (useFloor)
            {
                var total = new int[compCount];
                var reach = useValueFloor ? new int[compCount] : null;
                var within = useSatCeil ? new int[compCount] : null;
                var inBand = useHueBand ? new int[compCount] : null;
                // run ごとの数を行並列で数える(run の画素数は x1 - x0 + 1 なので数え直さない)。
                int* runReach = useValueFloor ? s_intPool.Rent(R) : null;
                int* runWithin = useSatCeil ? s_intPool.Rent(R) : null;
                int* runInBand = useHueBand ? s_intPool.Rent(R) : null;
                try
                {
                    var runX0C = runX0; var runX1C = runX1;
                    var rReach = runReach; var rWithin = runWithin; var rInBand = runInBand;
                    Parallel.For(0, bh, ccPo, ly =>
                    {
                        int grb = (ly + minY) * w + minX;
                        for (int k = rowOff[ly]; k < rowOff[ly + 1]; k++)
                        {
                            int x0 = runX0C[k], x1 = runX1C[k];
                            if (useValueFloor)
                            {
                                int n = 0;
                                for (int lx = x0; lx <= x1; lx++)
                                    if (pixV[HsvSlot(pixIdx, grb + lx)] >= shadowValueFloor) n++;
                                rReach[k] = n;
                            }
                            if (useSatCeil)
                            {
                                int n = 0;
                                for (int lx = x0; lx <= x1; lx++)
                                    if (pixS[HsvSlot(pixIdx, grb + lx)] <= partSatCeiling) n++;
                                rWithin[k] = n;
                            }
                            if (useHueBand)
                            {
                                int n = 0;
                                for (int lx = x0; lx <= x1; lx++)
                                {
                                    int gi = grb + lx;
                                    if (pixS[HsvSlot(pixIdx, gi)] < hueMinSat) { n++; continue; }   // 色相が不安定 = 反証にしない
                                    float hd = Mathf.Abs(pixH[HsvSlot(pixIdx, gi)] - sampleH);
                                    if (hd > 0.5f) hd = 1f - hd;
                                    if (hd < partHueBand) n++;
                                }
                                rInBand[k] = n;
                            }
                        }
                    });
                    for (int r = 0; r < R; r++)
                    {
                        int c = comp[r];
                        total[c] += runX1[r] - runX0[r] + 1;
                        if (useValueFloor) reach[c] += runReach[r];
                        if (useSatCeil) within[c] += runWithin[r];
                        if (useHueBand) inBand[c] += runInBand[r];
                    }
                }
                finally
                {
                    if (runInBand != null) s_intPool.Return(runInBand);
                    if (runWithin != null) s_intPool.Return(runWithin);
                    if (runReach != null) s_intPool.Return(runReach);
                }
                reachesFloor = new bool[compCount];
                for (int c = 0; c < compCount; c++)
                    reachesFloor[c] = (!useValueFloor || reach[c] >= total[c] * ShadowFloorKeepFrac)
                                   && (!useSatCeil || within[c] >= total[c] * ShadowFloorKeepFrac)
                                   && (!useHueBand || inBand[c] >= total[c] * PartHueKeepFrac);
                // スポイト位置を含む成分は包絡ゲートで落とさない(上の summary 参照)。
                if (anchorX >= minX && anchorX <= maxX && anchorY >= minY && anchorY <= maxY)
                {
                    int aly = anchorY - minY, alx = anchorX - minX;
                    for (int k = rowOff[aly]; k < rowOff[aly + 1]; k++)
                        if (alx >= runX0[k] && alx <= runX1[k]) { reachesFloor[comp[k]] = true; break; }
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

            bool coreRule = keepComp < 0;
            if (coreRule)
            {
                bool anyCore = false;
                for (int c = 0; c < compCount; c++) if (hasCore[c]) { anyCore = true; break; }
                if (!anyCore)
                {
                    if (!useFloor) return; // 確信できるコアが皆無 → 絞り込まない(recall 保護)
                    coreRule = false;      // コア規則は使わず、明度下限だけを適用する
                }
            }

            var keepCompLocal = keepComp;
            var hasCoreArr = hasCore;
            var coreRuleL = coreRule;
            var floorArr = reachesFloor;
            var runX0L = runX0; var runX1L = runX1; var compL = comp;
            Parallel.For(0, bh, ccPo, ly =>
            {
                int rb = (ly + minY) * w + minX;
                for (int k = rowOff[ly]; k < rowOff[ly + 1]; k++)
                {
                    int c = compL[k];
                    bool keep = keepCompLocal >= 0 ? (c == keepCompLocal)
                              : (!coreRuleL || hasCoreArr[c]) && (floorArr == null || floorArr[c]);
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
            DropDetachedTranslucent(strength, px, w, h,
                seedX >= 0 ? seedX : anchorX, seedX >= 0 ? seedY : anchorY, ct);
        }

        /// <summary>
        /// 半透明の選択画素(strength &gt; 0 かつ α &lt; OpaqueAlphaMin)のうち、残った不透明な選択にも
        /// 指定の位置(シード、無ければスポイト位置)にも 4 近傍で地続きにつながらないものを選択から外す。
        /// 連結成分は不透明な画素だけで作る(半透明の余白で島どうしがつながらないように)ので、半透明の画素は
        /// 上のどのゲートも通らずに選ばれたまま残り、別の部位の半透明の重ね(瞳の上の重ね・肌の重ね)を
        /// 丸ごと塗っていた(2026-10-20、ワンショット p50: 過検出 922 万のうち 49.5 万、肌の過検出の 58%・瞳の 77%)。
        /// 部位の縁の AA や、残した部位に重なる半透明の層は地続きなので残る。
        /// </summary>
        private static void DropDetachedTranslucent(float* strength, Color32* px, int w, int h,
            int keepX, int keepY, CancellationToken ct)
        {
            int len = w * h;
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            // 半透明の選択画素を行並列で数え、番地の一覧にする。以降の段(種・広げる・外す)はこの一覧と
            // その 4 近傍だけを見る(以前は全画素を 1 スレッドで 3〜4 回なめていた)。
            var rowT = new int[h + 1];
            Parallel.For(0, h, po, y =>
            {
                int rb = y * w, n = 0;
                for (int x = 0; x < w; x++)
                    if (strength[rb + x] > 0f && px[rb + x].a < OpaqueAlphaMin) n++;
                rowT[y + 1] = n;
            });
            for (int y = 0; y < h; y++) rowT[y + 1] += rowT[y];
            int nT = rowT[h];
            if (nT == 0) return;
            int* tIdx = s_intPool.Rent(nT);
            int* stack = null;
            bool* reached = null;
            try
            {
                var tIdxL = tIdx;
                Parallel.For(0, h, po, y =>
                {
                    int rb = y * w, k = rowT[y];
                    for (int x = 0; x < w; x++)
                        if (strength[rb + x] > 0f && px[rb + x].a < OpaqueAlphaMin) tIdxL[k++] = rb + x;
                });
                // reached は半透明の選択画素の番地でしか読まない(Push は IsT を確かめてから呼ぶ)ので、
                // その番地だけを書けばよい。
                reached = s_boolPool.Rent(len);
                stack = s_intPool.Rent(nT);
                var reachedL = reached;
                // 種: 残った不透明な選択に 4 近傍で接する半透明の選択画素。種の判定は不透明な画素しか
                // 読まないので、行の順に依らない。
                Parallel.For(0, h, po, y =>
                {
                    for (int k = rowT[y], kEnd = rowT[y + 1]; k < kEnd; k++)
                    {
                        int i = tIdxL[k];
                        int x = i - y * w;
                        reachedL[i] =
                               (x > 0 && strength[i - 1] > 0f && px[i - 1].a >= OpaqueAlphaMin)
                            || (x + 1 < w && strength[i + 1] > 0f && px[i + 1].a >= OpaqueAlphaMin)
                            || (y > 0 && strength[i - w] > 0f && px[i - w].a >= OpaqueAlphaMin)
                            || (y + 1 < h && strength[i + w] > 0f && px[i + w].a >= OpaqueAlphaMin);
                    }
                });
                int sp = 0;
                for (int k = 0; k < nT; k++)
                    if (reached[tIdx[k]]) stack[sp++] = tIdx[k];
                bool IsT(int i) => strength[i] > 0f && px[i].a < OpaqueAlphaMin;
                void Push(int i) { if (!reached[i]) { reached[i] = true; stack[sp++] = i; } }
                // 指定位置の半透明の選択画素も種にする。届く集合は広げる順に依らない。
                if (keepX >= 0 && keepX < w && keepY >= 0 && keepY < h && IsT(keepY * w + keepX))
                    Push(keepY * w + keepX);
                while (sp > 0)
                {
                    int i = stack[--sp];
                    int x = i % w, y = i / w;
                    if (x > 0 && IsT(i - 1)) Push(i - 1);
                    if (x + 1 < w && IsT(i + 1)) Push(i + 1);
                    if (y > 0 && IsT(i - w)) Push(i - w);
                    if (y + 1 < h && IsT(i + w)) Push(i + w);
                }
                Parallel.For(0, h, po, y =>
                {
                    for (int k = rowT[y], kEnd = rowT[y + 1]; k < kEnd; k++)
                    {
                        int i = tIdxL[k];
                        if (!reachedL[i]) strength[i] = 0f;
                    }
                });
            }
            finally
            {
                if (stack != null) s_intPool.Return(stack);
                if (reached != null) s_boolPool.Return(reached);
                s_intPool.Return(tIdx);
            }
        }
    }
}
