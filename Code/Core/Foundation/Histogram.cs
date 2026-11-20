// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 整数のヒストグラムの並列集計と分位点(0.4.0 の計画の 3-5 で PixelProcessor から切り出した)。
    /// 再着色の統計(アンカー)・無彩パスの地色・スポイト補正・選択が使う。
    /// </summary>
    internal static unsafe class Histogram {
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
            RowChunk chunk, CancellationToken ct = default, int maxWorkers = 0) {
            if (yTo <= yFrom) {
                return 0;
            }
            int dop = CoreParallelism.Max();
            if (maxWorkers > 0 && maxWorkers < dop) {
                dop = maxWorkers;
            }
            var po = CoreParallelism.Options(ct, dop);
            int total = 0;
            object gate = new object();
            Parallel.ForEach(Partitioner.Create(yFrom, yTo), po,
                () => new int[hist.Length],
                (range, _, local) => {
                    int c = chunk(range.Item1, range.Item2, local);
                    if (c != 0) {
                        Interlocked.Add(ref total, c);
                    }
                    return local;
                },
                local => {
                    lock (gate) {
                        for (int b = 0; b < local.Length; b++) {
                            hist[b] += local[b];
                        }
                    }
                });
            return total;
        }

        /// <summary><see cref="AccumulateParallelRowsLong"/> の、集計対象が行レンジ [yFrom,yTo) のデリゲート。</summary>
        internal delegate void RowChunkLong(int yFrom, int yTo, long[] localHist);

        /// <summary>
        /// <see cref="AccumulateParallelRows"/> の long 版(重みつきの集計用。基準色の推定)。ビンは数百本なので管理配列で足りる。
        /// 整数の加算だけなので、結果は集計の順にも並列の分け方にも依らない。
        /// </summary>
        internal static void AccumulateParallelRowsLong(int yFrom, int yTo, long[] hist,
            RowChunkLong chunk, CancellationToken ct = default) {
            if (yTo <= yFrom) {
                return;
            }
            var po = CoreParallelism.Options(ct);
            object gate = new object();
            int bins = hist.Length;
            Parallel.ForEach(Partitioner.Create(yFrom, yTo), po,
                () => new long[bins],
                (range, _, local) => {
                    chunk(range.Item1, range.Item2, local);
                    return local;
                },
                local => {
                    lock (gate) {
                        for (int b = 0; b < local.Length; b++) {
                            hist[b] += local[b];
                        }
                    }
                });
        }

        /// <summary><see cref="AccumulateParallelRowsNative"/> の、集計対象が行レンジ [yFrom,yTo) のデリゲート。</summary>
        internal delegate int RowChunkNative(int yFrom, int yTo, int* localHist);

        /// <summary>
        /// <see cref="AccumulateParallelRows"/> の、ヒストグラム(<paramref name="hist"/>、長さ <paramref name="bins"/>)と
        /// ワーカーごとの集計をヒープの外に置く版。ビンが色の数ほど多いとき(アンカーの色ごとの数え上げ)に使う
        /// (0.4.0 の計画の 3-12。管理配列の版だと、ワーカーの数 × 色の数の配列を呼ぶたびに Mono のヒープに積んでいた)。
        /// ワーカーごとの集計は作業配列のプールから借りて 0 で埋め、集計が終わったら(取り消されたときも)まとめて返す。
        /// 整数の加算だけなので、結果は管理配列の版と同じ。
        /// 長い繰り返しでは、Parallel.ForEach がワーカーを途中で入れ替える(入れ替わるたびに初期化が呼ばれる)。抜けた
        /// ワーカーの集計は空きの一覧へ置き、次のワーカーがその続きに足すので、借りる本数は同時に動くワーカーの数
        /// (並列度)までで止まる(2026-11-13 まで、入れ替わるたびに色の数ぶんを借り足していた)。
        /// </summary>
        internal static int AccumulateParallelRowsNative(int yFrom, int yTo, int* hist, int bins,
            RowChunkNative chunk, CancellationToken ct = default, int maxWorkers = 0) {
            if (yTo <= yFrom) {
                return 0;
            }
            int dop = CoreParallelism.Max();
            if (maxWorkers > 0 && maxWorkers < dop) {
                dop = maxWorkers;
            }
            var po = CoreParallelism.Options(ct, dop);
            int total = 0;
            object gate = new object();
            var rented = new List<IntPtr>();
            var idle = new Stack<IntPtr>();   // 抜けたワーカーが置いていった集計(中身は足し込み待ち。消さずに続きへ足す)
            try {
                Parallel.ForEach(Partitioner.Create(yFrom, yTo), po,
                    () => {
                        lock (gate) {
                            if (idle.Count > 0) {
                                return idle.Pop();
                            }
                        }
                        int* p = WorkPools.Int.Rent(bins);
                        lock (gate) {
                            rented.Add((IntPtr)p);
                        }
                        new Span<int>(p, bins).Clear();
                        return (IntPtr)p;
                    },
                    (range, _, local) => {
                        int c = chunk(range.Item1, range.Item2, (int*)local);
                        if (c != 0) {
                            Interlocked.Add(ref total, c);
                        }
                        return local;
                    },
                    local => {
                        lock (gate) {
                            idle.Push(local);
                        }
                    });
                // ワーカーごとの集計を足し込む。以前は各ワーカーの終わりにロックの中で「ワーカーの数 × 色の数」を
                // 直列に足していた。ここではビンの区間に分けて並列に、区間ごとに全ワーカーの分を足す。
                // 整数の加算なので、足す順に依らず結果は同じ。借りた集計(rented)がちょうどワーカーごとの集計。
                MergeLocals(hist, bins, rented, po);
            } finally {
                // 中断中(ドメインのアンロード)かはプールの Return が見て、そのときは貸し出し中のまま残す(借りた側は見ない)。
                foreach (var p in rented) {
                    WorkPools.Int.Return((int*)p);
                }
            }
            return total;
        }

        // hist[b] += Σ locals[k][b](b ∈ [0, bins))。ビンを区間に分けて並列に足す(区間が 1 つなら 1 本)。
        private static void MergeLocals(int* hist, int bins, List<IntPtr> locals, ParallelOptions po) {
            int n = locals.Count;
            if (n == 0) {
                return;
            }
            var ptrs = locals.ToArray();
            const int Chunk = 1 << 14;
            int chunks = (bins + Chunk - 1) / Chunk;
            if (chunks <= 1) {
                AddRange(hist, ptrs, 0, bins);
                return;
            }
            Parallel.For(0, chunks, po, c => {
                int b0 = c * Chunk;
                AddRange(hist, ptrs, b0, Math.Min(bins, b0 + Chunk));
            });
        }

        private static void AddRange(int* hist, IntPtr[] locals, int b0, int b1) {
            for (int k = 0; k < locals.Length; k++) {
                int* p = (int*)locals[k];
                for (int b = b0; b < b1; b++) {
                    hist[b] += p[b];
                }
            }
        }

        /// <summary>256bin ヒストグラムの percentile(0..1) を実値で返す(値域 [0, scale])。
        /// bin→値は b/(Length-1)·scale(256bin なら b/255·scale)。線形補間の percentile に対し
        /// 最大 1bin(scale/255)の離散化差を許容する(auto_wash_sample の前例に従う)。</summary>
        internal static float ValueAtPercentile(int[] hist, int total, float pct, float scale) {
            int target = Mathf.Clamp(Mathf.CeilToInt(total * pct), 1, total);
            int b = FirstBinReaching(hist, hist.Length, target);
            return b >= 0 ? b / (float)(hist.Length - 1) * scale : scale;
        }

        /// <summary>
        /// ヒストグラムの先頭 <paramref name="bins"/> 個を若い順に足し、累積が <paramref name="target"/> に届いた最初のビンを
        /// 返す(届かなければ −1)。分位点を求める各所(選択・再着色・自動調整)が共有するのは、この数え方だけ。
        /// 目標の数の決め方(切り上げ・四捨五入・下限)、ビンを値へ直す対応(下端・中心・上端)、届かないときの値は
        /// 呼ぶ側ごとに違い、そろえると出力が変わるので、呼ぶ側に残している。
        /// </summary>
        internal static int FirstBinReaching(int[] hist, int bins, int target) {
            int cum = 0;
            for (int b = 0; b < bins; b++) {
                cum += hist[b];
                if (cum >= target) {
                    return b;
                }
            }
            return -1;
        }
    }
}
