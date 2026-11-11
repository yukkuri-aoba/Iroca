// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 再着色の before/after を並べた「比較パネル画像」と、変化量メトリクスを生成する純ユーティリティ。
    ///
    /// 目的: ヘッドレス自動化(<see cref="IrocaAutomation"/>)経由で AI エージェントが色替えを実行するとき、
    /// エージェントは結果テクスチャを直接は「見られない」。そこで
    ///   ・比較パネル PNG（左=変換前 / 中=変換後 / 右=変化画素をマゼンタ表示）
    ///   ・変化量の数値（変化率・最大連結成分の占有率＝過検出/散在の兆候）
    /// を返し、エージェントがパネルを Read で開いて目視し、数値で裏取りできるようにする。
    ///
    /// 再着色アルゴリズム本体（<see cref="PixelProcessor"/>）には一切関与しない診断用の後段生成。
    /// UnityEditor 非依存(Color32 と System のみ)なので headless ハーネスでも実行・検証できる。
    /// 画素はポインタ版で読む(自動化 API はヒープの外の画素を写さずに渡す。0.4.0 の計画の A6)。画素数の大きさの
    /// 作業(変化の印・連結成分のスタック)もヒープの外に置く。配列版はポインタ版へ転送する。
    /// </summary>
    internal static unsafe class RecolorPreview {
        /// <summary>比較パネル各タイルの最大辺(px)。これを超える入力はアスペクト比維持で縮小する。</summary>
        public const int DefaultMaxTile = 512;

        /// <summary>タイル間の仕切り(px)。</summary>
        public const int PanelGutter = 4;

        /// <summary>変化量メトリクス。過検出(散在)や変換漏れの兆候を数値で表す。</summary>
        public struct ChangeMetrics {
            public int totalPixels;
            public int changedPixels;
            public float changedFraction;      // changedPixels / totalPixels
            // 変化領域のバウンディングボックス(px)。変化ゼロなら全て 0。
            public int bboxX, bboxY, bboxW, bboxH;
            // 変化画素のうち最大 4-連結成分が占める割合(0..1)。1 に近い＝まとまった 1 領域(健全)、
            // 小さい＝散在(過検出/ノイズの兆候)。
            public float largestComponentFraction;
            public int componentCount;         // 4-連結成分数
        }

        // 変化判定: RGBA のいずれかが異なれば変化とみなす(α は再着色で不変前提だが安全側で含める)。
        private static bool Changed(Color32 a, Color32 b) =>
            a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a;

        /// <summary>before/after を突き合わせて変化量メトリクスを算出する。</summary>
        public static ChangeMetrics ComputeMetrics(Color32[] before, Color32[] after, int w, int h) {
            if (before == null || after == null || before.Length != after.Length ||
                before.Length != w * h || w <= 0 || h <= 0) {
                return new ChangeMetrics { totalPixels = Math.Max(0, w * h) };
            }
            fixed (Color32* b = before, a = after) {
                return ComputeMetrics(b, a, w, h);
            }
        }

        /// <summary>
        /// <see cref="ComputeMetrics(Color32[],Color32[],int,int)"/> のポインタ版。before/after は w*h 画素以上を、
        /// 返るまで保つこと(null なら空のメトリクス)。
        /// </summary>
        public static ChangeMetrics ComputeMetrics(Color32* before, Color32* after, int w, int h) {
            var m = new ChangeMetrics { totalPixels = Math.Max(0, w * h) };
            if (before == null || after == null || w <= 0 || h <= 0) {
                return m;
            }

            var changed = NativeBitMask.Allocate(w, h);
            try {
                ulong* bits = changed.Bits;
                // 変化の印・数・外接矩形を、64 画素の語の区間ごとに並列に求めてから合わせる(区間の境目が語の境目なので、
                // 2 本のスレッドが同じ語を書かない。数は整数の和、矩形は最小・最大なので、結果は 1 本で回したのと同じ)。
                int len = w * h;
                int words = (len + 63) >> 6;
                int chunks = (words + MetricsWordsPerChunk - 1) / MetricsWordsPerChunk;
                var parts = new MetricsPart[chunks];
                Action<int> scan = c => {
                    int i0 = c * MetricsWordsPerChunk * 64;
                    int i1 = Math.Min(len, i0 + MetricsWordsPerChunk * 64);
                    var part = new MetricsPart { minX = w, minY = h, maxX = -1, maxY = -1 };
                    int y = i0 / w, x = i0 - y * w;
                    for (int i = i0; i < i1; i++) {
                        if (Changed(before[i], after[i])) {
                            bits[i >> 6] |= 1UL << (i & 63); part.cnt++;
                            if (x < part.minX) {
                                part.minX = x;
                            }
                            if (x > part.maxX) {
                                part.maxX = x;
                            }
                            if (y < part.minY) {
                                part.minY = y;
                            }
                            if (y > part.maxY) {
                                part.maxY = y;
                            }
                        }
                        if (++x == w) {
                            x = 0;
                            y++;
                        }
                    }
                    parts[c] = part;
                };
                if (chunks <= 1) {
                    scan(0);
                } else {
                    System.Threading.Tasks.Parallel.For(0, chunks,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = CoreParallelism.Max() }, scan);
                }
                int minX = w, minY = h, maxX = -1, maxY = -1, cnt = 0;
                foreach (var part in parts) {
                    cnt += part.cnt;
                    minX = Math.Min(minX, part.minX);
                    minY = Math.Min(minY, part.minY);
                    maxX = Math.Max(maxX, part.maxX);
                    maxY = Math.Max(maxY, part.maxY);
                }

                m.changedPixels = cnt;
                m.changedFraction = m.totalPixels > 0 ? (float)cnt / m.totalPixels : 0f;
                if (cnt > 0) {
                    m.bboxX = minX; m.bboxY = minY;
                    m.bboxW = maxX - minX + 1; m.bboxH = maxY - minY + 1;
                    LargestComponent(bits, w, h, cnt, out int largest, out int comps);
                    m.largestComponentFraction = (float)largest / cnt;
                    m.componentCount = comps;
                }
                return m;
            } finally { changed.Release(); }
        }

        // ComputeMetrics の並列の 1 区間の語の数(256K 画素)と、区間ごとの結果。
        private const int MetricsWordsPerChunk = 4096;

        private struct MetricsPart {
            public int cnt, minX, minY, maxX, maxY;
        }

        // 4-連結成分の最大サイズと個数を求める。走査中に changed を消費(visited 兼用)するので破壊的。
        // スタックは changed 画素数で上限が付く(各画素は高々 1 回 push される)。
        // 始点は画素の番号の小さい順に探すが、印の無い語(64 画素)はまとめて飛ばす(変化が少なくても全画素を
        // 1 つずつ見ていた)。探す順は同じなので、数と大きさは変わらない。
        private static void LargestComponent(ulong* changed, int w, int h, int changedCount,
            out int largest, out int componentCount) {
            largest = 0; componentCount = 0;
            using var stackMem = new NativeBuffer((long)changedCount * sizeof(int), NativeUsage.Other);
            int* stack = (int*)stackMem.Ptr;
            int n = w * h;
            int words = (n + 63) >> 6;
            for (int start = 0; start < n; start++) {
                if ((start & 63) == 0) {
                    // 語の頭で、その語に印が 1 つも無ければ次の語の頭へ(ループの start++ で 64 進む)
                    int wi = start >> 6;
                    while (wi < words && changed[wi] == 0UL) {
                        wi++;
                    }
                    if (wi >= words) {
                        break;
                    }
                    start = wi << 6;
                }
                if (!Take(changed, start)) {
                    continue;
                }
                componentCount++;
                int sp = 0, size = 0;
                stack[sp++] = start;
                while (sp > 0) {
                    int p = stack[--sp]; size++;
                    int px = p % w;
                    if (px > 0) {
                        int q = p - 1;
                        if (Take(changed, q)) {
                            stack[sp++] = q;
                        }
                    }
                    if (px < w - 1) {
                        int q = p + 1;
                        if (Take(changed, q)) {
                            stack[sp++] = q;
                        }
                    }
                    if (p >= w) {
                        int q = p - w;
                        if (Take(changed, q)) {
                            stack[sp++] = q;
                        }
                    }
                    if (p < n - w) {
                        int q = p + w;
                        if (Take(changed, q)) {
                            stack[sp++] = q;
                        }
                    }
                }
                if (size > largest) {
                    largest = size;
                }
            }
        }

        // 印が立っていれば下ろして true(訪れた印を兼ねる)。
        private static bool Take(ulong* bits, int i) {
            ulong bit = 1UL << (i & 63);
            if ((bits[i >> 6] & bit) == 0UL) {
                return false;
            }
            bits[i >> 6] &= ~bit;
            return true;
        }

        /// <summary>入力(w×h)をアスペクト比維持で maxTile 以内へ縮小したときのタイル寸法を求める(拡大はしない)。</summary>
        public static void ComputeTileSize(int w, int h, int maxTile, out int tileW, out int tileH) {
            if (w <= 0 || h <= 0) { tileW = 1; tileH = 1; return; }
            int m = Math.Max(w, h);
            if (m <= maxTile) { tileW = w; tileH = h; return; }
            double s = (double)maxTile / m;
            tileW = Math.Max(1, (int)Math.Round(w * s));
            tileH = Math.Max(1, (int)Math.Round(h * s));
        }

        /// <summary>
        /// 比較パネル画像を Color32 配列で組み立てる。横並びで [変換前 | 変換後 | 変化マップ] の 3 タイル。
        /// 変化マップは変換前のグレースケール(暗)にマゼンタで変化画素を重ね、過検出を目視しやすくする。
        /// 返り値は <paramref name="panelW"/>×<paramref name="panelH"/> の行優先(GetPixels32 と同じ下→上)配列。
        /// </summary>
        public static Color32[] BuildComparisonPanel(Color32[] before, Color32[] after, int w, int h,
            int maxTile, out int panelW, out int panelH) {
            if (before == null || after == null || before.Length != w * h || after.Length != w * h) {
                throw new ArgumentException("before/after must have w*h pixels");
            }
            fixed (Color32* b = before, a = after) {
                return BuildComparisonPanel(b, a, w, h, maxTile, out panelW, out panelH);
            }
        }

        /// <summary>
        /// <see cref="BuildComparisonPanel(Color32[],Color32[],int,int,int,out int,out int)"/> のポインタ版。
        /// before/after は w*h 画素を、返るまで保つこと。返すパネルは縮めたタイルの大きさ(管理配列)。
        /// </summary>
        public static Color32[] BuildComparisonPanel(Color32* before, Color32* after, int w, int h,
            int maxTile, out int panelW, out int panelH) {
            ComputeTileSize(w, h, maxTile, out int tw, out int th);
            var tileBefore = DownscaleBox(before, w, h, tw, th);
            var tileAfter = DownscaleBox(after, w, h, tw, th);
            var tileChange = BuildChangeTile(before, after, w, h, tw, th);

            panelW = tw * 3 + PanelGutter * 2;
            panelH = th;
            var panel = new Color32[panelW * panelH];
            var gutter = new Color32(32, 32, 32, 255);
            for (int i = 0; i < panel.Length; i++) {
                panel[i] = gutter;
            }

            Blit(panel, panelW, tileBefore, tw, th, 0);
            Blit(panel, panelW, tileAfter, tw, th, tw + PanelGutter);
            Blit(panel, panelW, tileChange, tw, th, tw * 2 + PanelGutter * 2);
            return panel;
        }

        // src(w×h)を tw×th へボックス平均で縮小する。等倍(tw==w,th==h)なら実質コピー。
        private static Color32[] DownscaleBox(Color32* src, int w, int h, int tw, int th) {
            var dst = new Color32[tw * th];
            ForRows(th, dy => {
                int sy0 = (int)((long)dy * h / th);
                int sy1 = (int)((long)(dy + 1) * h / th);
                if (sy1 <= sy0) {
                    sy1 = sy0 + 1;
                }
                for (int dx = 0; dx < tw; dx++) {
                    int sx0 = (int)((long)dx * w / tw);
                    int sx1 = (int)((long)(dx + 1) * w / tw);
                    if (sx1 <= sx0) {
                        sx1 = sx0 + 1;
                    }
                    long r = 0, g = 0, b = 0, a = 0; int n = 0;
                    for (int sy = sy0; sy < sy1 && sy < h; sy++) {
                        int row = sy * w;
                        for (int sx = sx0; sx < sx1 && sx < w; sx++) {
                            var c = src[row + sx];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    }
                    dst[dy * tw + dx] = n > 0
                        ? new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n))
                        : new Color32(0, 0, 0, 255);
                }
            });
            return dst;
        }

        // タイルの行 [0, th) を並列に処理する(行ごとに書く先が別で、どう分けても結果は同じ)。比較は背景で走るが、
        // 4K の入力を縮小のために 3 回(変換前・変換後・変化マップ)全画素読むので、1 本では待ちが長い。
        private static void ForRows(int th, Action<int> row) {
            System.Threading.Tasks.Parallel.For(0, th,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = CoreParallelism.Max() }, row);
        }

        // 変化マップタイル。縮小ブロック内に 1 つでも変化画素があればマゼンタ(=変化を潰さない max プーリング)、
        // なければ変換前の輝度を暗く落としたグレー。
        private static Color32[] BuildChangeTile(Color32* before, Color32* after, int w, int h, int tw, int th) {
            var dst = new Color32[tw * th];
            var hot = new Color32(255, 0, 255, 255);
            ForRows(th, dy => {
                int sy0 = (int)((long)dy * h / th);
                int sy1 = (int)((long)(dy + 1) * h / th);
                if (sy1 <= sy0) {
                    sy1 = sy0 + 1;
                }
                for (int dx = 0; dx < tw; dx++) {
                    int sx0 = (int)((long)dx * w / tw);
                    int sx1 = (int)((long)(dx + 1) * w / tw);
                    if (sx1 <= sx0) {
                        sx1 = sx0 + 1;
                    }
                    bool any = false; long lum = 0; int n = 0;
                    for (int sy = sy0; sy < sy1 && sy < h; sy++) {
                        int row = sy * w;
                        for (int sx = sx0; sx < sx1 && sx < w; sx++) {
                            int i = row + sx;
                            if (Changed(before[i], after[i])) {
                                any = true;
                            }
                            var c = before[i];
                            lum += (long)(c.r * 0.299f + c.g * 0.587f + c.b * 0.114f);
                            n++;
                        }
                    }
                    if (any) {
                        dst[dy * tw + dx] = hot;
                    } else {
                        byte g = (byte)(n > 0 ? lum / n * 35 / 100 : 0);
                        dst[dy * tw + dx] = new Color32(g, g, g, 255);
                    }
                }
            });
            return dst;
        }

        // タイルをパネルの水平オフセット xOff へ貼り込む(行優先)。
        private static void Blit(Color32[] dst, int dstW, Color32[] tile, int tw, int th, int xOff) {
            for (int y = 0; y < th; y++) {
                Array.Copy(tile, y * tw, dst, y * dstW + xOff, tw);
            }
        }
    }
}
