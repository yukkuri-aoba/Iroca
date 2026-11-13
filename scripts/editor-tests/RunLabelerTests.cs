// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Iroca.EditorTests {
    /// <summary>
    /// 連結成分のラベル付けの共通の部品(<see cref="RunLabeler"/>)。乱数の印で、画素を 1 つずつ辿る素朴な 4 近傍の探索と比べる:
    /// run が印の立った画素をちょうど覆うこと、成分の分け方が同じこと、根が成分でいちばん小さい run であること、成分の番号が
    /// 最初の画素のラスタ順であること。選択・無彩・ハイライトの帯・閉領域・AI 提案の後処理が、この部品の上に載っている。
    /// </summary>
    public unsafe class RunLabelerTests {
        // 印の配列は W×H。その中の矩形 (X0, Y0) から BW×BH だけを見る(矩形の外の印は無視されること)。
        // 小さい寸法は順に結ぶ道、大きい寸法(run が多く行も多い)は帯に分けて並列に結ぶ道を通る(Size で切り替える)。
        private int W = 41, H = 29, X0 = 3, Y0 = 2, BW = 33, BH = 24;

        private void Size(bool large) {
            if (large) {
                W = 420; H = 360; X0 = 5; Y0 = 4; BW = 400; BH = 340;
            } else {
                W = 41; H = 29; X0 = 3; Y0 = 2; BW = 33; BH = 24;
            }
        }

        [SetUp]
        public void SetUp() => Size(large: false);

        private bool[] RandomMarks(int seed, double density) {
            var rng = new System.Random(seed);
            var m = new bool[W * H];
            for (int i = 0; i < m.Length; i++) {
                m[i] = rng.NextDouble() < density;
            }
            return m;
        }

        // 矩形の中を、ラスタ順に見つけた順に番号を振る素朴な探索(4 近傍)。印の無い画素は -1。
        private int[] NaiveLabels(bool[] mark, out int count) {
            var label = new int[BW * BH];
            for (int i = 0; i < label.Length; i++) {
                label[i] = -1;
            }
            count = 0;
            var stack = new Stack<int>();
            for (int ly = 0; ly < BH; ly++) {
                for (int lx = 0; lx < BW; lx++) {
                    if (!mark[(ly + Y0) * W + lx + X0] || label[ly * BW + lx] >= 0) {
                        continue;
                    }
                    int id = count++;
                    label[ly * BW + lx] = id;
                    stack.Push(ly * BW + lx);
                    while (stack.Count > 0) {
                        int p = stack.Pop();
                        int px = p % BW, py = p / BW;
                        foreach (var (nx, ny) in new[] { (px - 1, py), (px + 1, py), (px, py - 1), (px, py + 1) }) {
                            if (nx < 0 || ny < 0 || nx >= BW || ny >= BH) {
                                continue;
                            }
                            int q = ny * BW + nx;
                            if (mark[(ny + Y0) * W + nx + X0] && label[q] < 0) {
                                label[q] = id;
                                stack.Push(q);
                            }
                        }
                    }
                }
            }
            return label;
        }

        [TestCase(1, 0.15, false)]
        [TestCase(2, 0.45, false)]
        [TestCase(3, 0.60, false)]
        [TestCase(4, 0.85, false)]
        [TestCase(5, 1.00, false)]
        // 帯に分けて並列に結ぶ道(run が 1.6 万以上・行が 128 以上)。密度 0.6 前後は、帯の境目をまたぐ成分が多い。
        [TestCase(11, 0.45, true)]
        [TestCase(12, 0.60, true)]
        [TestCase(13, 0.75, true)]
        public void Label_MatchesANaiveFloodFill(int seed, double density, bool large) {
            Size(large);
            var mark = RandomMarks(seed, density);
            var expected = NaiveLabels(mark, out int expectedCount);
            var po = new ParallelOptions { MaxDegreeOfParallelism = 4 };
            fixed (bool* p = mark) {
                var runs = RunLabeler.Label(p, W, X0, Y0, BW, BH, po, CancellationToken.None);
                try {
                    if (large) {
                        Assert.GreaterOrEqual(runs.Count, 1 << 14, "テストの前提: 帯に分けて結ぶ道を通る数の run がある");
                    }
                    Assert.AreEqual(BH + 1, runs.RowStart.Length);
                    Assert.AreEqual(runs.Count, runs.RowStart[BH]);
                    // run は印の立った画素をちょうど覆い、行の中で左から重ならずに並び、途切れるまで続く(最大の区間)。
                    var covered = new bool[BW * BH];
                    for (int ly = 0; ly < BH; ly++) {
                        int last = -2;
                        for (int k = runs.RowStart[ly]; k < runs.RowStart[ly + 1]; k++) {
                            Assert.Greater(runs.X0[k], last + 1, $"行 {ly}: run がつながっているのに分かれている・順が違う");
                            Assert.GreaterOrEqual(runs.X1[k], runs.X0[k]);
                            for (int lx = runs.X0[k]; lx <= runs.X1[k]; lx++) {
                                covered[ly * BW + lx] = true;
                            }
                            last = runs.X1[k];
                        }
                    }
                    for (int i = 0; i < covered.Length; i++) {
                        Assert.AreEqual(expected[i] >= 0, covered[i], $"画素 {i}: run が印と合わない");
                    }

                    // 成分の分け方が同じで、根は成分でいちばん小さい run。成分の番号は最初の画素のラスタ順(素朴な探索の番号と同じ)。
                    int[] comp = new int[runs.Count];
                    int count;
                    fixed (int* c = comp) {
                        count = RunLabeler.NumberComponents(runs, c);
                    }
                    Assert.AreEqual(expectedCount, count, "成分の数");
                    var smallestRun = new Dictionary<int, int>();
                    for (int ly = 0; ly < BH; ly++) {
                        for (int k = runs.RowStart[ly]; k < runs.RowStart[ly + 1]; k++) {
                            int id = expected[ly * BW + runs.X0[k]];
                            for (int lx = runs.X0[k]; lx <= runs.X1[k]; lx++) {
                                Assert.AreEqual(id, expected[ly * BW + lx], "1 つの run が 2 つの成分にまたがった");
                            }
                            if (!smallestRun.ContainsKey(id)) {
                                smallestRun[id] = k;
                            }
                            Assert.AreEqual(smallestRun[id], runs.Root[k], $"run {k}: 根が成分でいちばん小さい run ではない");
                            Assert.AreEqual(id, comp[k], $"run {k}: 成分の番号が最初の画素の順ではない");
                        }
                    }
                } finally { runs.Dispose(); }
            }
        }

        [Test]
        public void Label_NoMarks_BorrowsNothing() {
            var mark = new bool[W * H];
            fixed (bool* p = mark) {
                var runs = RunLabeler.Label(p, W, X0, Y0, BW, BH, new ParallelOptions(), CancellationToken.None);
                Assert.AreEqual(0, runs.Count);
                Assert.IsTrue(runs.X0 == null && runs.X1 == null && runs.Root == null && runs.Y == null);
                runs.Dispose();   // 何も借りていなくても呼べる
            }
        }

        // 行ごとの列挙を渡す口: run の行(Y)も作れる。行は列挙した側の番号。
        [Test]
        public void Label_WithAScanner_RecordsTheRowOfEachRun() {
            var mark = RandomMarks(7, 0.5);
            fixed (bool* p = mark) {
                bool* m = p;
                int w = W, ox = X0, oy = Y0, bw = BW;
                var runs = RunLabeler.Label(BH, (row, x0, x1) => {
                    int n = 0, x = 0;
                    while (x < bw) {
                        if (!m[(row + oy) * w + x + ox]) { x++; continue; }
                        int s = x;
                        while (x < bw && m[(row + oy) * w + x + ox]) {
                            x++;
                        }
                        if (x0 != null) { x0[n] = s; x1[n] = x - 1; }
                        n++;
                    }
                    return n;
                }, new ParallelOptions { MaxDegreeOfParallelism = 3 }, CancellationToken.None, withRows: true);
                try {
                    Assert.Greater(runs.Count, 0);
                    for (int ly = 0; ly < BH; ly++) {
                        for (int k = runs.RowStart[ly]; k < runs.RowStart[ly + 1]; k++) {
                            Assert.AreEqual(ly, runs.Y[k]);
                        }
                    }
                    // 印の配列を渡す口と同じ結果。
                    var byMarks = RunLabeler.Label(p, W, X0, Y0, BW, BH, new ParallelOptions(), CancellationToken.None);
                    try {
                        Assert.AreEqual(byMarks.Count, runs.Count);
                        for (int k = 0; k < runs.Count; k++) {
                            Assert.AreEqual((byMarks.X0[k], byMarks.X1[k], byMarks.Root[k]), (runs.X0[k], runs.X1[k], runs.Root[k]));
                        }
                    } finally { byMarks.Dispose(); }
                } finally { runs.Dispose(); }
            }
        }

        [Test]
        public void Label_Cancelled_Throws() {
            var mark = RandomMarks(9, 0.5);
            using (var cts = new CancellationTokenSource()) {
                cts.Cancel();
                fixed (bool* p = mark) {
                    bool* m = p;
                    int w = W, ox = X0, oy = Y0, bw = BW, bh = BH;
                    Assert.Catch<System.OperationCanceledException>(() =>
                        RunLabeler.Label(m, w, ox, oy, bw, bh, new ParallelOptions(), cts.Token).Dispose());
                }
            }
        }
    }
}
