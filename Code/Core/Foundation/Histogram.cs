// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 整数のヒストグラムの並列集計と分位点(0.4.0 の計画の 3-5 で PixelProcessor から切り出した)。
    /// 再着色の統計(アンカー)・無彩パスの地色・スポイト補正・旧の選択が使う。
    /// </summary>
    internal static unsafe class Histogram
    {
        /// <summary>集計対象が行レンジ [yFrom,yTo) のデリゲート。戻り値は集計した画素数。</summary>
        internal delegate int RowChunk(int yFrom, int yTo, int[] localHist);

        /// <summary>
        /// 行レンジ [yFrom,yTo) をチャンク分割し、チャンクごとにスレッドローカルのヒストグラムへ
        /// 集計してからマージする。bbox の行だけを分割したいとき(領域統計を実マッチ範囲に限定する
        /// とき)に使う。ヒストグラムは整数カウントの加算だけで**集計順に依存しない**ので、結果は
        /// 単スレッド逐次版と完全に同値(=percentile もビット不変)。
        /// </summary>
        /// <returns>全チャンクの集計画素数の合計。</returns>
        /// <param name="maxWorkers">並列度の上限(0 = 既定の <see cref="CoreParallelism.Max"/>)。スレッドローカルの
        /// ヒストグラムはワーカーごとに 1 本なので、ビンが多いときに確保量を抑えるのに使う。</param>
        internal static int AccumulateParallelRows(int yFrom, int yTo, int[] hist,
            RowChunk chunk, CancellationToken ct = default, int maxWorkers = 0)
        {
            if (yTo <= yFrom) return 0;
            int dop = CoreParallelism.Max();
            if (maxWorkers > 0 && maxWorkers < dop) dop = maxWorkers;
            var po = new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct };
            int total = 0;
            object gate = new object();
            Parallel.ForEach(Partitioner.Create(yFrom, yTo), po,
                () => new int[hist.Length],
                (range, _, local) =>
                {
                    int c = chunk(range.Item1, range.Item2, local);
                    if (c != 0) Interlocked.Add(ref total, c);
                    return local;
                },
                local =>
                {
                    lock (gate)
                        for (int b = 0; b < local.Length; b++) hist[b] += local[b];
                });
            return total;
        }

        /// <summary><see cref="AccumulateParallelRowsNative"/> の、集計対象が行レンジ [yFrom,yTo) のデリゲート。</summary>
        internal delegate int RowChunkNative(int yFrom, int yTo, int* localHist);

        /// <summary>
        /// <see cref="AccumulateParallelRows"/> の、ヒストグラム(<paramref name="hist"/>、長さ <paramref name="bins"/>)と
        /// ワーカーごとの集計をヒープの外に置く版。ビンが色の数ほど多いとき(アンカーの色ごとの数え上げ)に使う
        /// (0.4.0 の計画の 3-12。管理配列の版だと、ワーカーの数 × 色の数の配列を呼ぶたびに Mono のヒープに積んでいた)。
        /// ワーカーごとの集計は作業配列のプールから借りて 0 で埋め、集計が終わったら(取り消されたときも)まとめて返す。
        /// 整数の加算だけなので、結果は管理配列の版と同じ。
        /// </summary>
        internal static int AccumulateParallelRowsNative(int yFrom, int yTo, int* hist, int bins,
            RowChunkNative chunk, CancellationToken ct = default, int maxWorkers = 0)
        {
            if (yTo <= yFrom) return 0;
            int dop = CoreParallelism.Max();
            if (maxWorkers > 0 && maxWorkers < dop) dop = maxWorkers;
            var po = new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct };
            int total = 0;
            object gate = new object();
            var rented = new List<IntPtr>();
            try
            {
                Parallel.ForEach(Partitioner.Create(yFrom, yTo), po,
                    () =>
                    {
                        int* p = WorkPools.Int.Rent(bins);
                        lock (gate) rented.Add((IntPtr)p);
                        new Span<int>(p, bins).Clear();
                        return (IntPtr)p;
                    },
                    (range, _, local) =>
                    {
                        int c = chunk(range.Item1, range.Item2, (int*)local);
                        if (c != 0) Interlocked.Add(ref total, c);
                        return local;
                    },
                    local =>
                    {
                        int* p = (int*)local;
                        lock (gate)
                            for (int b = 0; b < bins; b++) hist[b] += p[b];
                    });
            }
            finally
            {
                // 中断中(ドメインのアンロード)は、並列で使っている途中のスレッドが残っていることがあるので返さない。
                if (!IrocaNativeMemory.CurrentThreadAborting)
                    foreach (var p in rented) WorkPools.Int.Return((int*)p);
            }
            return total;
        }

        /// <summary>256bin ヒストグラムの percentile(0..1) を実値で返す(値域 [0, scale])。
        /// bin→値は b/(Length-1)·scale(256bin なら b/255·scale)。線形補間の percentile に対し
        /// 最大 1bin(scale/255)の離散化差を許容する(auto_wash_sample の前例に従う)。</summary>
        internal static float ValueAtPercentile(int[] hist, int total, float pct, float scale)
        {
            int target = Mathf.Clamp(Mathf.CeilToInt(total * pct), 1, total);
            int cum = 0;
            for (int b = 0; b < hist.Length; b++)
            {
                cum += hist[b];
                if (cum >= target) return b / (float)(hist.Length - 1) * scale;
            }
            return scale;
        }

        /// <summary>
        /// ビン from 以上で、前後 halfWidth ビンを足した窓の合計がいちばん大きいビン(ならした最頻値)。同じ合計なら若いビン。
        /// 8 ビットの画素を細かいビンに入れると、隣り合うビンの間で数が割れて山がぎざぎざになるので、窓でならしてから取る。
        /// </summary>
        internal static int SmoothedModeBin(int[] hist, int halfWidth, int from = 0)
        {
            int best = from, bestSum = -1;
            for (int b = from; b < hist.Length; b++)
            {
                int sum = WindowSum(hist, b, halfWidth);
                if (sum > bestSum) { bestSum = sum; best = b; }
            }
            return best;
        }

        /// <summary>
        /// ビン lo〜hi(ヒストグラムの外は切り詰める)でいちばん数の多いビン。同じ数なら若いビン。
        /// </summary>
        internal static int MaxBin(int[] hist, int lo, int hi)
        {
            lo = Math.Max(0, lo);
            hi = Math.Min(hist.Length - 1, hi);
            int best = lo;
            for (int b = lo + 1; b <= hi; b++)
                if (hist[b] > hist[best]) best = b;
            return best;
        }

        /// <summary>
        /// center の前後 halfWidth ビンの合計。ヒストグラムの外にはみ出した分は 0 として数える。
        /// </summary>
        internal static int WindowSum(int[] hist, int center, int halfWidth)
        {
            int sum = 0;
            int lo = Math.Max(0, center - halfWidth), hi = Math.Min(hist.Length - 1, center + halfWidth);
            for (int k = lo; k <= hi; k++) sum += hist[k];
            return sum;
        }
    }
}
