// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iroca {
    /// <summary>
    /// 1 行の run(条件を満たす画素の横並び)を、左から順に列挙する(<see cref="RunLabeler.Label(int, RowRunScanner, ParallelOptions, CancellationToken, bool)"/>
    /// に渡す)。<paramref name="x0"/> が null なら数えるだけ。null でなければ、i 番目の run の左端を x0[i]、右端(含む)を x1[i] に書く。
    /// 戻り値は run の数(どちらの呼び方でも同じ数を返すこと)。行ごとに 1 回ずつ、別々のスレッドから呼ばれる。
    /// </summary>
    internal unsafe delegate int RowRunScanner(int row, int* x0, int* x1);

    /// <summary>
    /// <see cref="RunLabeler"/> の結果: 行ごとの run と、その連結成分(4 近傍)。配列はプールから借りているので、使い終えたら
    /// Dispose で返す(run が 1 つも無ければ何も借りていない)。
    /// </summary>
    internal unsafe struct RunLabels : IDisposable {
        /// <summary>run の数。</summary>
        public int Count;
        /// <summary>行 r の run は [RowStart[r], RowStart[r + 1])。長さは行数 + 1。run は行の順、行の中では左から順に並ぶ。</summary>
        public int[] RowStart;
        /// <summary>run の左端と右端(右端を含む)。列挙した側の座標(矩形で列挙したなら矩形の左端が 0)。</summary>
        public int* X0, X1;
        /// <summary>run の行(列挙した側の行番号)。<c>withRows</c> で頼んだときだけ。</summary>
        public int* Y;
        /// <summary>
        /// run の属する成分の根。根はその成分でいちばん小さい run 番号(= 成分の最初の画素を含む run)なので、run を番号の順に見ると、
        /// 根に出会う順が成分の最初の画素のラスタ順になる。確定済みで、読むだけ(並列に読んでよい)。
        /// </summary>
        public int* Root;

        public void Dispose() {
            if (Root != null) { WorkPools.Int.Return(Root); Root = null; }
            if (Y != null) { WorkPools.Int.Return(Y); Y = null; }
            if (X1 != null) { WorkPools.Int.Return(X1); X1 = null; }
            if (X0 != null) { WorkPools.Int.Return(X0); X0 = null; }
        }
    }

    /// <summary>
    /// 連結成分のラベル付け(4 近傍)を、行の run と union-find で行う。Core に同じ手順が 5 か所あった(選択の連結成分・無彩の
    /// 成分ごとの明るさ・ハイライトの帯の成長・閉領域の検出・AI 提案の後処理)のを、ここへまとめた(2026-11-13)。
    /// <para>
    /// run は行の中の左右の連結そのもの、上下の行で x の範囲が重なる run の結合は縦の 4 近傍の連結そのものなので、成分の分け方は
    /// 画素を 1 つずつ辿る探索と厳密に同じ。結合では小さい方の根を親にするので、根は成分でいちばん小さい run 番号に決まり、
    /// 結ぶ順には依らない。
    /// </para>
    /// どの画素を run にするかは呼ぶ側が行ごとに列挙する(<see cref="RowRunScanner"/>)。画素ごとの述語を受けないのは、エディタの
    /// 既定の最適化では述語がインライン化されず、画素ごとの呼び出しが残るため。
    /// </summary>
    internal static unsafe class RunLabeler {
        /// <summary>
        /// <paramref name="rows"/> 行の run を列挙して(行並列)、連結成分にまとめる。<paramref name="withRows"/> なら run の行
        /// (<see cref="RunLabels.Y"/>)も作る。取り消されたら、借りた配列を返して OperationCanceledException。
        /// </summary>
        public static RunLabels Label(int rows, RowRunScanner scan, ParallelOptions po, CancellationToken ct, bool withRows = false) {
            var runs = new RunLabels { RowStart = new int[rows + 1] };
            int[] rowStart = runs.RowStart;
            Parallel.For(0, rows, po, r => rowStart[r + 1] = scan(r, null, null));
            for (int r = 0; r < rows; r++) {
                rowStart[r + 1] += rowStart[r];
            }
            int count = rowStart[rows];
            runs.Count = count;
            if (count == 0) {
                return runs;
            }
            try {
                int* x0 = runs.X0 = WorkPools.Int.Rent(count);
                int* x1 = runs.X1 = WorkPools.Int.Rent(count);
                int* y = runs.Y = withRows ? WorkPools.Int.Rent(count) : null;
                Parallel.For(0, rows, po, r => {
                    int k = rowStart[r];
                    int n = scan(r, x0 + k, x1 + k);
                    if (y != null) {
                        for (int i = 0; i < n; i++) {
                            y[k + i] = r;
                        }
                    }
                });

                int* parent = runs.Root = WorkPools.Int.Rent(count);
                // 上下の行で x の範囲が重なる run を結ぶ。run が多いときは、行を帯に分けて帯の中を並列に結び(帯ごとに run の番号の
                // 範囲が分かれるので、ほかの帯の親には触らない)、最後に帯の境目の 2 行だけを順に結ぶ。根は「成分でいちばん小さい
                // run 番号」に決まるので、結ぶ順が変わっても結果は同じ。
                int bands = BandCount(rows, count, po.MaxDegreeOfParallelism);
                if (bands <= 1) {
                    for (int k = 0; k < count; k++) {
                        parent[k] = k;
                    }
                    LinkRows(rowStart, x0, x1, parent, 0, rows - 1, ct);
                } else {
                    Parallel.For(0, bands, po, b => {
                        int first = BandStart(rows, bands, b), end = BandStart(rows, bands, b + 1);
                        for (int k = rowStart[first]; k < rowStart[end]; k++) {
                            parent[k] = k;
                        }
                        LinkRows(rowStart, x0, x1, parent, first, end - 1, ct);
                    });
                    for (int b = 1; b < bands; b++) {
                        LinkRowPair(rowStart, x0, x1, parent, BandStart(rows, bands, b) - 1);
                    }
                }
                // 根を確定させる。親はいつも自分以下の番号(小さい方を親にし、経路の短縮も親を小さくするだけ)なので、番号の小さい順に
                // 見れば、親はもう確定した根になっている。1 段たどるだけで済む。
                for (int k = 0; k < count; k++) {
                    parent[k] = parent[parent[k]];
                }
                return runs;
            } catch {
                runs.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 印の配列(<paramref name="mark"/>。行の幅 <paramref name="stride"/>)の、(<paramref name="x0"/>, <paramref name="y0"/>) から
        /// <paramref name="width"/>×<paramref name="rows"/> の矩形の中で、印の立った画素の連結成分を求める。run の座標は矩形の左上が (0, 0)。
        /// </summary>
        public static RunLabels Label(bool* mark, int stride, int x0, int y0, int width, int rows, ParallelOptions po,
            CancellationToken ct) {
            return Label(rows, (r, rx0, rx1) => {
                bool* row = mark + (long)(r + y0) * stride + x0;
                int n = 0, x = 0;
                while (x < width) {
                    if (!row[x]) { x++; continue; }
                    int s = x;
                    while (x < width && row[x]) {
                        x++;
                    }
                    if (rx0 != null) { rx0[n] = s; rx1[n] = x - 1; }
                    n++;
                }
                return n;
            }, po, ct);
        }

        /// <summary>
        /// 成分に 0 からの番号を振る(<paramref name="comp"/>[run] = 成分の番号。長さは run の数)。番号は、成分の最初の画素(ラスタ順)の順。
        /// 戻り値は成分の数。
        /// </summary>
        public static int NumberComponents(in RunLabels runs, int* comp) {
            int* root = runs.Root;
            int n = 0;
            for (int k = 0; k < runs.Count; k++) {
                // 根は成分でいちばん小さい run なので、根に出会うのは成分の中で最初。残りの run は根より後に来る。
                comp[k] = root[k] == k ? n++ : comp[root[k]];
            }
            return n;
        }

        // 帯に分けて結ぶのは、run がこの数以上で、1 つの帯がこの行数以上になるときだけ(小さいと、並列の起動のほうが高い)。
        private const int BandMinRuns = 1 << 14;
        private const int BandMinRows = 64;

        private static int BandCount(int rows, int runCount, int maxDegreeOfParallelism) {
            if (runCount < BandMinRuns) {
                return 1;
            }
            int workers = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
            return Math.Max(1, Math.Min(workers, rows / BandMinRows));
        }

        // 帯 b の最初の行(b = bands なら行数)。
        private static int BandStart(int rows, int bands, int b) => (int)((long)rows * b / bands);

        // 行 firstRow..lastRow の中で、隣り合う 2 行の run を結ぶ。
        private static void LinkRows(int[] rowStart, int* x0, int* x1, int* parent, int firstRow, int lastRow, CancellationToken ct) {
            for (int r = firstRow; r < lastRow; r++) {
                if ((r & 255) == 0) {
                    ct.ThrowIfCancellationRequested();
                }
                LinkRowPair(rowStart, x0, x1, parent, r);
            }
        }

        // 行 r と r + 1 の run のうち、x の範囲が重なるものを結ぶ。どちらの行も左から並んでいるので、2 本の列を併合の要領で進める。
        private static void LinkRowPair(int[] rowStart, int* x0, int* x1, int* parent, int r) {
            int i = rowStart[r], iEnd = rowStart[r + 1];
            int j = iEnd, jEnd = rowStart[r + 2];
            while (i < iEnd && j < jEnd) {
                if (x0[i] <= x1[j] && x0[j] <= x1[i]) {
                    int a = Find(parent, i), b = Find(parent, j);
                    if (a != b) {
                        // 小さい方の根を親にする(根 = 成分でいちばん小さい run 番号)。
                        if (b < a) { int t = a; a = b; b = t; }
                        parent[b] = a;
                    }
                }
                if (x1[i] < x1[j]) {
                    i++;
                } else {
                    j++;
                }
            }
        }

        private static int Find(int* parent, int a) {
            while (parent[a] != a) {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }
    }
}
