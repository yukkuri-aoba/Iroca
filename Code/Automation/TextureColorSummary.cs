// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// テクスチャの主な色を数色に要約する(AI エージェントが「どこをスポイトするか」を決める手がかり)。
    /// 色替えの処理(選択・再着色)には使わない。UI にも出ない。
    ///
    /// 縮小した画像の不透明な画素を OKLab で k-means にかけ、近すぎる色をまとめ、ごく小さい色を落とす。
    /// 各色には、その色のいちばん大きな塊の中で縁から最も遠い位置(UV。左下原点)を代表点として付ける。
    /// 自動調整はスポイト位置を含む塊を軸にするので、塊の内側の点を渡せば狙った部位に当たりやすい。
    /// 乱数の種は固定なので、同じ画素からは同じ結果になる。
    /// </summary>
    internal static class TextureColorSummary
    {
        /// <summary>解析する画像の長辺(これより大きければ面積平均で縮める)。</summary>
        internal const int AnalysisMaxSide = 256;

        // 不透明とみなす α(アトラスの透明な余白を数えない)。
        private const byte OpaqueAlpha = 128;
        // これより近い色(OKLab のユークリッド距離)は 1 色にまとめる。陰影の段差程度は残る。
        private const float MergeDistance = 0.04f;
        // 不透明な画素に占める割合がこれ未満の色は落とす(1 色は必ず残す)。
        private const float MinFraction = 0.005f;
        private const int Iterations = 12;
        private const int Seed = 12345;

        internal sealed class Entry
        {
            public Color32 color;        // その色に属する画素の平均(sRGB)
            public float fraction;       // 不透明な画素に占める割合
            public Vector2 uv;           // 代表点(左下原点の UV、0..1)
            public float regionShare;    // その色のうち、代表点のある塊が占める割合
        }

        /// <summary>
        /// 画素(下原点、w×h)を最大 maxColors 色に要約する。割合の大きい順。不透明な画素が無ければ空。
        /// </summary>
        public static List<Entry> Summarize(Color32[] pixels, int w, int h, int maxColors)
        {
            var result = new List<Entry>();
            if (pixels == null || w <= 0 || h <= 0 || maxColors <= 0) return result;

            Downscale(pixels, w, h, out var small, out int sw, out int sh);

            // 不透明な画素と、その OKLab
            var index = new List<int>();
            for (int i = 0; i < small.Length; i++)
                if (small[i].a >= OpaqueAlpha) index.Add(i);
            int n = index.Count;
            if (n == 0) return result;
            var lab = new float[n * 3];
            for (int j = 0; j < n; j++)
            {
                var c = small[index[j]];
                ToOklab(c, out lab[j * 3], out lab[j * 3 + 1], out lab[j * 3 + 2]);
            }

            int k = Math.Min(maxColors, n);
            var centers = InitCenters(lab, n, k);
            var assign = new int[n];
            for (int it = 0; it < Iterations; it++)
            {
                bool changed = AssignAll(lab, n, centers, k, assign);
                Recenter(lab, n, centers, k, assign);
                if (!changed && it > 0) break;
            }

            // 近すぎる色をまとめる(大きい方へ寄せる)
            var counts = new int[k];
            for (int j = 0; j < n; j++) counts[assign[j]]++;
            var parent = new int[k];
            for (int c = 0; c < k; c++) parent[c] = c;
            for (int a = 0; a < k; a++)
                for (int b = a + 1; b < k; b++)
                {
                    int ra = Root(parent, a), rb = Root(parent, b);
                    if (ra == rb || counts[ra] == 0 || counts[rb] == 0) continue;
                    if (Dist2(centers, ra, centers, rb) >= MergeDistance * MergeDistance) continue;
                    int keep = counts[ra] >= counts[rb] ? ra : rb, drop = keep == ra ? rb : ra;
                    parent[drop] = keep;
                    counts[keep] += counts[drop];
                    counts[drop] = 0;
                }
            for (int j = 0; j < n; j++) assign[j] = Root(parent, assign[j]);

            // 画素ごとのラベル(透明は -1)
            var label = new int[small.Length];
            for (int i = 0; i < label.Length; i++) label[i] = -1;
            for (int j = 0; j < n; j++) label[index[j]] = assign[j];

            var order = new List<int>();
            for (int c = 0; c < k; c++) if (counts[c] > 0) order.Add(c);
            order.Sort((a, b) => counts[b] != counts[a] ? counts[b].CompareTo(counts[a]) : a.CompareTo(b));

            foreach (int c in order)
            {
                float fraction = counts[c] / (float)n;
                if (fraction < MinFraction && result.Count > 0) continue;
                long r = 0, g = 0, bl = 0;
                for (int j = 0; j < n; j++)
                {
                    if (assign[j] != c) continue;
                    var px = small[index[j]];
                    r += px.r; g += px.g; bl += px.b;
                }
                int cnt = counts[c];
                Representative(label, sw, sh, c, out int rx, out int ry, out int regionSize);
                result.Add(new Entry
                {
                    color = new Color32((byte)(r / cnt), (byte)(g / cnt), (byte)(bl / cnt), 255),
                    fraction = fraction,
                    uv = new Vector2((rx + 0.5f) / sw, (ry + 0.5f) / sh),
                    regionShare = regionSize / (float)cnt,
                });
            }
            return result;
        }

        /// <summary>長辺が <see cref="AnalysisMaxSide"/> 以下なら複製せずそのまま、超えるなら面積平均で縮める。</summary>
        internal static void Downscale(Color32[] pixels, int w, int h, out Color32[] small, out int sw, out int sh)
            => DownscaleTo(pixels, w, h, AnalysisMaxSide, out small, out sw, out sh);

        internal static void DownscaleTo(Color32[] pixels, int w, int h, int maxSide, out Color32[] small, out int sw, out int sh)
        {
            int longSide = Math.Max(w, h);
            if (longSide <= maxSide) { small = pixels; sw = w; sh = h; return; }
            float s = maxSide / (float)longSide;
            sw = Math.Max(1, Mathf.RoundToInt(w * s));
            sh = Math.Max(1, Mathf.RoundToInt(h * s));
            small = WorkingResolution.ResizeArea(pixels, w, h, sw, sh);
        }

        // ─── k-means ───

        // k-means++(種は固定)。
        private static float[] InitCenters(float[] lab, int n, int k)
        {
            var rng = new System.Random(Seed);
            var centers = new float[k * 3];
            int first = rng.Next(n);
            Array.Copy(lab, first * 3, centers, 0, 3);
            var d2 = new double[n];
            for (int c = 1; c < k; c++)
            {
                double sum = 0;
                for (int j = 0; j < n; j++)
                {
                    float best = float.MaxValue;
                    for (int q = 0; q < c; q++) best = Math.Min(best, Dist2(lab, j, centers, q));
                    d2[j] = best;
                    sum += best;
                }
                int pick = 0;
                if (sum > 0)
                {
                    double t = rng.NextDouble() * sum;
                    for (pick = 0; pick < n - 1; pick++) { t -= d2[pick]; if (t <= 0) break; }
                }
                else pick = rng.Next(n);
                Array.Copy(lab, pick * 3, centers, c * 3, 3);
            }
            return centers;
        }

        private static bool AssignAll(float[] lab, int n, float[] centers, int k, int[] assign)
        {
            bool changed = false;
            for (int j = 0; j < n; j++)
            {
                int best = 0; float bestD = float.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    float d = Dist2(lab, j, centers, c);
                    if (d < bestD) { bestD = d; best = c; }
                }
                if (assign[j] != best) { assign[j] = best; changed = true; }
            }
            return changed;
        }

        private static void Recenter(float[] lab, int n, float[] centers, int k, int[] assign)
        {
            var sum = new double[k * 3];
            var cnt = new int[k];
            for (int j = 0; j < n; j++)
            {
                int c = assign[j];
                cnt[c]++;
                sum[c * 3] += lab[j * 3]; sum[c * 3 + 1] += lab[j * 3 + 1]; sum[c * 3 + 2] += lab[j * 3 + 2];
            }
            for (int c = 0; c < k; c++)
            {
                if (cnt[c] == 0) continue;   // 空になった中心は動かさない(まとめる段で消える)
                centers[c * 3] = (float)(sum[c * 3] / cnt[c]);
                centers[c * 3 + 1] = (float)(sum[c * 3 + 1] / cnt[c]);
                centers[c * 3 + 2] = (float)(sum[c * 3 + 2] / cnt[c]);
            }
        }

        private static float Dist2(float[] a, int i, float[] b, int j)
        {
            float d0 = a[i * 3] - b[j * 3], d1 = a[i * 3 + 1] - b[j * 3 + 1], d2 = a[i * 3 + 2] - b[j * 3 + 2];
            return d0 * d0 + d1 * d1 + d2 * d2;
        }

        private static int Root(int[] parent, int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        // ─── 代表点 ───

        /// <summary>
        /// ラベル c のいちばん大きな 4 連結の塊の中で、塊の外(画像の外も含む)から最も遠い画素。
        /// 同じ距離なら走査順で先のもの。
        /// </summary>
        private static void Representative(int[] label, int w, int h, int c, out int bx, out int by, out int regionSize)
        {
            var comp = new int[label.Length];   // 0 = 未訪問、それ以外 = 塊の番号
            int bestId = 0, bestSize = 0, next = 0;
            var stack = new Stack<int>();
            for (int i = 0; i < label.Length; i++)
            {
                if (label[i] != c || comp[i] != 0) continue;
                int id = ++next, size = 0;
                comp[i] = id;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    size++;
                    int x = p % w, y = p / w;
                    if (x > 0) Visit(p - 1);
                    if (x < w - 1) Visit(p + 1);
                    if (y > 0) Visit(p - w);
                    if (y < h - 1) Visit(p + w);
                }
                if (size > bestSize) { bestSize = size; bestId = id; }

                void Visit(int q)
                {
                    if (label[q] != c || comp[q] != 0) return;
                    comp[q] = id;
                    stack.Push(q);
                }
            }
            regionSize = bestSize;

            // 塊の外までの距離(チャンファー 3-4 の 2 回走査)。画像の外は距離 0 の外側とみなす。
            const int Inf = int.MaxValue / 4;
            var dist = new int[label.Length];
            for (int i = 0; i < dist.Length; i++) dist[i] = comp[i] == bestId ? Inf : 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (dist[i] == 0) continue;
                    int d = dist[i];
                    d = Math.Min(d, x > 0 ? dist[i - 1] + 3 : 3);
                    d = Math.Min(d, y > 0 ? dist[i - w] + 3 : 3);
                    d = Math.Min(d, x > 0 && y > 0 ? dist[i - w - 1] + 4 : 4);
                    d = Math.Min(d, x < w - 1 && y > 0 ? dist[i - w + 1] + 4 : 4);
                    dist[i] = d;
                }
            for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x;
                    if (dist[i] == 0) continue;
                    int d = dist[i];
                    d = Math.Min(d, x < w - 1 ? dist[i + 1] + 3 : 3);
                    d = Math.Min(d, y < h - 1 ? dist[i + w] + 3 : 3);
                    d = Math.Min(d, x < w - 1 && y < h - 1 ? dist[i + w + 1] + 4 : 4);
                    d = Math.Min(d, x > 0 && y < h - 1 ? dist[i + w - 1] + 4 : 4);
                    dist[i] = d;
                }

            int best = -1, bestD = -1;
            for (int i = 0; i < dist.Length; i++)
                if (comp[i] == bestId && dist[i] > bestD) { bestD = dist[i]; best = i; }
            bx = best % w;
            by = best / w;
        }

        // ─── 色空間 ───
        // 製品の変換(Core の OkLab)をそのまま使う。

        private static void ToOklab(Color32 c, out float L, out float a, out float b)
            => OkLab.RgbToOklab(c.r, c.g, c.b, out L, out a, out b);
    }
}
