// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    // 強度の範囲(バウンディングボックス)。選択の後段と再着色の段が、走査を選択の画素の周りに限るために使う。
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// strength &gt; thr かつ α&gt;=128 の画素を囲むバウンディングボックスを求める。
        /// 連結成分系(連結成分アンカリング / 成分別 L マップ)が「連結予測子」と同じ条件で使う共通版。
        /// 行ごとに独立に求めた min/max をマージするだけなので、逐次走査と結果は同一(順序非依存)。
        /// </summary>
        /// <returns>該当画素が 1 つ以上あれば true。</returns>
        private static bool TryComputeMatchedBBox(float* strength, Color32* px, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default)
        {
            int lminX = w, lmaxX = -1, lminY = h, lmaxY = -1;
            object gate = new object();
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            Parallel.For(0, h, po,
                () => (minX: w, maxX: -1, minY: h, maxY: -1),
                (y, _, loc) =>
                {
                    int rb = y * w;
                    for (int x = 0; x < w; x++)
                        if (strength[rb + x] > thr && px[rb + x].a >= OpaqueAlphaMin)
                        {
                            if (x < loc.minX) loc.minX = x;
                            if (x > loc.maxX) loc.maxX = x;
                            if (y < loc.minY) loc.minY = y;
                            if (y > loc.maxY) loc.maxY = y;
                        }
                    return loc;
                },
                loc =>
                {
                    lock (gate)
                    {
                        if (loc.minX < lminX) lminX = loc.minX;
                        if (loc.maxX > lmaxX) lmaxX = loc.maxX;
                        if (loc.minY < lminY) lminY = loc.minY;
                        if (loc.maxY > lmaxY) lmaxY = loc.maxY;
                    }
                });
            minX = lminX; minY = lminY; maxX = lmaxX; maxY = lmaxY;
            return lmaxX >= 0;
        }

        /// <summary>
        /// strength &gt; thr の画素を囲むバウンディングボックス(min/max)を求める。
        /// 後段パス(穴埋め/境界回復/ブラー)を実マッチ範囲＋余白に限定し、全画素走査を避けるために使う。
        /// </summary>
        /// <returns>マッチ画素が 1 つ以上あれば true(false のとき bbox は空で、後段パスは no-op)。</returns>
        private static bool TryComputeStrengthBBox(float* strength, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default)
        {
            // 行ごとに独立に求めた min/max をマージするだけなので順序非依存(逐次走査と同一結果)。
            // 4K では全画素走査なので、単スレッドのままだと後段 bbox の算出自体が無視できない。
            int lminX = w, lmaxX = -1, lminY = h, lmaxY = -1;
            object gate = new object();
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            Parallel.For(0, h, po,
                () => (minX: w, maxX: -1, minY: h, maxY: -1),
                (y, _, loc) =>
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
                        if (rowMinX < loc.minX) loc.minX = rowMinX;
                        if (rowMaxX > loc.maxX) loc.maxX = rowMaxX;
                        if (y < loc.minY) loc.minY = y;
                        if (y > loc.maxY) loc.maxY = y;
                    }
                    return loc;
                },
                loc =>
                {
                    lock (gate)
                    {
                        if (loc.minX < lminX) lminX = loc.minX;
                        if (loc.maxX > lmaxX) lmaxX = loc.maxX;
                        if (loc.minY < lminY) lminY = loc.minY;
                        if (loc.maxY > lmaxY) lmaxY = loc.maxY;
                    }
                });
            minX = lminX; minY = lminY; maxX = lmaxX; maxY = lmaxY;
            return lmaxX >= 0;
        }
    }
}
