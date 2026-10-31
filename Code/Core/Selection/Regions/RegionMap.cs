// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Iroca
{
    /// <summary>領域の層の決め方。</summary>
    internal struct RegionMapSettings
    {
        /// <summary>隣り合う画素をつなぐ色の差の上限(RGBA の各成分の差の最大)。</summary>
        public int StepMax;
        /// <summary>1 つの領域の中の色の幅の上限(RGB の各成分の最大と最小の差)。なだらかなグラデが別の色へ続くときはここで分かれる。</summary>
        public int WidthMax;
        /// <summary>これより画素の少ない塊は領域にせず、縁や点として周りの領域へ割り当てる。</summary>
        public int MinArea;

        public static RegionMapSettings Default => new RegionMapSettings
        {
            StepMax = RegionMap.DefaultStepMax,
            WidthMax = RegionMap.DefaultWidthMax,
            MinArea = RegionMap.DefaultMinArea,
        };
    }

    /// <summary>1 つの領域の表。番号は 1 から。</summary>
    internal struct RegionInfo
    {
        public int Area;
        /// <summary>縁や点として割り当てられた画素を除いた数。</summary>
        public int InteriorArea;
        public int MinX, MinY, MaxX, MaxY;
        /// <summary>内側の画素の平均の色(0〜255)。内側が無ければ全部の画素の平均。</summary>
        public float R, G, B;
        /// <summary>色の範囲。R | G &lt;&lt; 8 | B &lt;&lt; 16 に詰める。</summary>
        public int BoxMin, BoxMax;
    }

    /// <summary>隣り合う 2 つの領域(A &lt; B)。</summary>
    internal struct RegionAdjacency
    {
        public int A, B;
        /// <summary>接している辺の数。</summary>
        public int Contact;
        /// <summary>そのうち、どちらかの画素が 2 つの領域の混色のもの。</summary>
        public int MixtureContact;
        /// <summary>
        /// そのうち、色の差が <see cref="RegionMapSettings.StepMax"/> 以下の辺(なだらかな接触)。幅の上限で帯に切れたグラデの境目はここに入る。
        /// 同じ素材の陰影の強い手掛かり(別の素材の境目は、ふつう色が飛ぶか輪郭線を挟む)。
        /// </summary>
        public int SoftContact;
    }

    /// <summary>
    /// 選択の再設計(T5)の L1: テクスチャを色の境目で区切った領域の地図。テクスチャごとに 1 回作り、中身は画素の色だけで決まる。
    /// 領域は「差が <see cref="RegionMapSettings.StepMax"/> 以下の隣でつながり、色の幅が <see cref="RegionMapSettings.WidthMax"/> 以下の塊」で、
    /// 差の小さい辺から順につなぐので、走査の向きの偏りが出にくい。幅の上限があるので、グラデは帯に分かれることがあるが、
    /// 別の色まで 1 つの領域に飲み込まない(分かれすぎは判断の単位が増えるだけで、飲み込みは後の段で直せない)。
    /// <see cref="RegionMapSettings.MinArea"/> より小さい塊は縁や点とみなし、近くの 2 領域の混色なら被覆率の高い側へ、
    /// そうでなければ色の近い隣へ割り当てる。
    /// 中身はヒープの外に持つので、使い終わったら <see cref="Dispose"/> する。
    /// </summary>
    internal sealed unsafe class RegionMap : IDisposable
    {
        internal const int DefaultStepMax = 12;
        internal const int DefaultWidthMax = 16;
        internal const int DefaultMinArea = 4;
        // 混色の相手を探す窓の半径(縁のにじみの幅)。
        internal const int MixtureWindowRadius = 2;
        // 2 色を結ぶ線分からの色のずれがこれ以下なら混色とみなす。
        internal const int MixtureResidualMax = 8;
        // 両端の色の差がこれより小さいと、混色と揺れを区別できない。
        internal const int MixtureEndsMin = 24;

        /// <summary><see cref="Mix"/> の値: 領域の内側。</summary>
        internal const byte MixInterior = 0;
        /// <summary><see cref="Mix"/> の値: 混色でない小さな塊の画素。128 以上は、割り当てた領域の被覆率(値 / 255)。</summary>
        internal const byte MixThin = 1;

        private const int MaxMixtureCandidates = 4;

        public readonly int Width, Height;
        private readonly NativeBuffer _labels;
        private readonly NativeBuffer _mix;
        private NativeBuffer _regions;
        private NativeBuffer _mixtureIndex, _mixturePartner;

        /// <summary>領域の数(番号は 1〜RegionCount)。</summary>
        public int RegionCount { get; private set; }
        /// <summary>画素 → 領域の番号。0 は透明(α = 0)。並びは画素と同じ。</summary>
        public int* Labels => (int*)_labels.Ptr;
        /// <summary>画素の種類(<see cref="MixInterior"/>・<see cref="MixThin"/>・128 以上は被覆率)。</summary>
        public byte* Mix => (byte*)_mix.Ptr;
        /// <summary>混色の画素の位置(<see cref="MixturePixels"/> 個)。</summary>
        public int* MixtureIndex => _mixtureIndex != null ? (int*)_mixtureIndex.Ptr : null;
        /// <summary>混色の画素の、割り当てなかった側の領域(<see cref="MixtureIndex"/> と同じ並び)。</summary>
        public int* MixturePartner => _mixturePartner != null ? (int*)_mixturePartner.Ptr : null;
        /// <summary>領域の表。添字は領域の番号(0 は使わない)。</summary>
        public RegionInfo* Regions => (RegionInfo*)_regions.Ptr;

        /// <summary>隣り合う領域の組。</summary>
        public readonly List<RegionAdjacency> Adjacency = new List<RegionAdjacency>();
        /// <summary>隣り合う見える画素の組の、色の差の分布(添字が差)。テクスチャの揺れの推定に使う。</summary>
        public readonly long[] StepHistogram = new long[256];
        /// <summary>同じ色の塊の数。</summary>
        public int FlatCount { get; private set; }
        /// <summary>小さな塊の画素の数。</summary>
        public int SmallPixels { get; private set; }
        /// <summary>そのうち 2 領域の混色と分かった画素の数。</summary>
        public int MixturePixels { get; private set; }
        /// <summary>段ごとの時間(ms): 同じ色の塊・つなぐ・小さな塊・表。</summary>
        public readonly long[] PhaseMs = new long[4];

        private RegionMap(int width, int height)
        {
            Width = width;
            Height = height;
            _labels = new NativeBuffer((long)width * height * sizeof(int), NativeUsage.Selection);
            _mix = new NativeBuffer((long)width * height, NativeUsage.Selection);
        }

        public void Dispose()
        {
            _labels.Release();
            _mix.Release();
            _regions?.Release();
            _regions = null;
            _mixtureIndex?.Release();
            _mixturePartner?.Release();
            _mixtureIndex = _mixturePartner = null;
        }

        private static int Find(int* parent, int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        private static int PackRgb(Color32 c) => c.r | (c.g << 8) | (c.b << 16);

        private static int ColorStep(Color32 a, Color32 b)
        {
            int d = Math.Abs(a.r - b.r);
            d = Math.Max(d, Math.Abs(a.g - b.g));
            d = Math.Max(d, Math.Abs(a.b - b.b));
            return Math.Max(d, Math.Abs(a.a - b.a));
        }

        private static int RgbDistance(Color32 a, Color32 b)
        {
            int d = Math.Abs(a.r - b.r);
            d = Math.Max(d, Math.Abs(a.g - b.g));
            return Math.Max(d, Math.Abs(a.b - b.b));
        }

        /// <summary>画素(並びは GetPixels32 と同じ)から領域の地図を作る。呼び出し側が <see cref="Dispose"/> する。</summary>
        public static RegionMap Build(Color32* px, int w, int h, in RegionMapSettings settings)
        {
            if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), $"invalid size: {w}x{h}");
            var map = new RegionMap(w, h);
            try
            {
                var sw = Stopwatch.StartNew();
                using (var scratch = new WorkScratch())
                {
                    int flatCount = LabelFlat(px, w, h, map.Labels, scratch);
                    map.FlatCount = flatCount;
                    map.PhaseMs[0] = sw.ElapsedMilliseconds;
                    sw.Restart();

                    int* parent = scratch.Int(Math.Max(1, flatCount), clear: false);
                    int* area = scratch.Int(Math.Max(1, flatCount), clear: true);
                    MergeFlat(px, w, h, map.Labels, flatCount, parent, area, settings, map.StepHistogram, scratch);
                    map.PhaseMs[1] = sw.ElapsedMilliseconds;
                    sw.Restart();

                    map.AssignSmall(px, parent, area, flatCount, settings.MinArea, scratch);
                    map.PhaseMs[2] = sw.ElapsedMilliseconds;
                    sw.Restart();
                }
                map.BuildTables(px, settings.StepMax);
                map.PhaseMs[3] = sw.ElapsedMilliseconds;
                return map;
            }
            catch
            {
                map.Dispose();
                throw;
            }
        }

        // 同じ色(RGBA)でつながる画素に 0 からの番号を振る。透明な画素は -1。戻り値は塊の数。
        private static int LabelFlat(Color32* px, int w, int h, int* labels, WorkScratch scratch)
        {
            int len = w * h;
            int* parent = scratch.Int(len, clear: false);
            int next = 0;
            for (int y = 0, i = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, i++)
                {
                    if (px[i].a == 0) { labels[i] = -1; continue; }
                    uint c = *(uint*)(px + i);
                    int left = x > 0 && *(uint*)(px + i - 1) == c ? labels[i - 1] : -1;
                    int down = y > 0 && *(uint*)(px + i - w) == c ? labels[i - w] : -1;
                    if (left < 0 && down < 0)
                    {
                        parent[next] = next;
                        labels[i] = next++;
                    }
                    else if (left >= 0 && down >= 0)
                    {
                        int a = Find(parent, left), b = Find(parent, down);
                        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                        labels[i] = Math.Min(a, b);
                    }
                    else
                    {
                        labels[i] = Math.Max(left, down);
                    }
                }
            }

            // 根の番号順に詰める(親は必ず自分より小さい番号なので、順に見れば根まで解決できる)。
            int count = 0;
            for (int k = 0; k < next; k++)
                parent[k] = parent[k] == k ? -(++count) : parent[parent[k]];
            for (int i = 0; i < len; i++)
                if (labels[i] >= 0) labels[i] = -parent[labels[i]] - 1;
            return count;
        }

        // 同じ色の塊を、差の小さい辺から順につなぐ。つないだ先の色の幅が上限を超える辺は使わない。
        private static void MergeFlat(Color32* px, int w, int h, int* labels, int flatCount,
            int* parent, int* area, in RegionMapSettings s, long[] hist, WorkScratch scratch)
        {
            int len = w * h;
            int stepMax = Math.Min(s.StepMax, 255);
            int* boxMin = scratch.Int(Math.Max(1, flatCount), clear: false);
            int* boxMax = scratch.Int(Math.Max(1, flatCount), clear: false);
            for (int k = 0; k < flatCount; k++) parent[k] = k;
            for (int y = 0, i = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, i++)
                {
                    int a = labels[i];
                    if (a < 0) continue;
                    if (area[a]++ == 0) boxMin[a] = boxMax[a] = PackRgb(px[i]);
                    if (x + 1 < w && labels[i + 1] >= 0)
                        hist[labels[i + 1] == a ? 0 : ColorStep(px[i], px[i + 1])]++;
                    if (y + 1 < h && labels[i + w] >= 0)
                        hist[labels[i + w] == a ? 0 : ColorStep(px[i], px[i + w])]++;
                }
            }

            // 差ごとの置き場所を決める。差 0 は塊の内側なのでつなぐ辺に入らない。
            var offset = new long[stepMax + 2];
            long total = 0;
            for (int d = 1; d <= stepMax; d++)
            {
                offset[d] = total;
                total += hist[d];
            }
            offset[stepMax + 1] = total;
            if (total == 0) return;
            if (total > int.MaxValue / 2)
                throw new InvalidOperationException($"edges too many: {total}");
            int* ends = scratch.Int((int)total * 2, clear: false);
            var fill = (long[])offset.Clone();
            for (int y = 0, i = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, i++)
                {
                    int a = labels[i];
                    if (a < 0) continue;
                    if (x + 1 < w && labels[i + 1] >= 0 && labels[i + 1] != a)
                    {
                        int d = ColorStep(px[i], px[i + 1]);
                        if (d <= stepMax)
                        {
                            long at = fill[d]++ * 2;
                            ends[at] = a;
                            ends[at + 1] = labels[i + 1];
                        }
                    }
                    if (y + 1 < h && labels[i + w] >= 0 && labels[i + w] != a)
                    {
                        int d = ColorStep(px[i], px[i + w]);
                        if (d <= stepMax)
                        {
                            long at = fill[d]++ * 2;
                            ends[at] = a;
                            ends[at + 1] = labels[i + w];
                        }
                    }
                }
            }

            int widthMax = s.WidthMax;
            for (long e = 0; e < total; e++)
            {
                int ra = Find(parent, ends[e * 2]);
                int rb = Find(parent, ends[e * 2 + 1]);
                if (ra == rb) continue;
                int minA = boxMin[ra], minB = boxMin[rb], maxA = boxMax[ra], maxB = boxMax[rb];
                int rMin = Math.Min(minA & 255, minB & 255), rMax = Math.Max(maxA & 255, maxB & 255);
                if (rMax - rMin > widthMax) continue;
                int gMin = Math.Min((minA >> 8) & 255, (minB >> 8) & 255), gMax = Math.Max((maxA >> 8) & 255, (maxB >> 8) & 255);
                if (gMax - gMin > widthMax) continue;
                int bMin = Math.Min((minA >> 16) & 255, (minB >> 16) & 255), bMax = Math.Max((maxA >> 16) & 255, (maxB >> 16) & 255);
                if (bMax - bMin > widthMax) continue;
                if (area[ra] < area[rb]) { int t = ra; ra = rb; rb = t; }
                parent[rb] = ra;
                area[ra] += area[rb];
                boxMin[ra] = rMin | (gMin << 8) | (bMin << 16);
                boxMax[ra] = rMax | (gMax << 8) | (bMax << 16);
            }
        }

        // 塊に領域の番号を付ける。小さな塊の画素は、混色なら被覆率の高い側へ、そうでなければ色の近い隣へ割り当てる。
        private void AssignSmall(Color32* px, int* parent, int* area, int flatCount, int minArea, WorkScratch scratch)
        {
            const int Pending = -2, InFrontier = -3;
            int w = Width, h = Height, len = w * h;
            int* labels = Labels;
            byte* mix = Mix;
            int* regionOf = scratch.Int(Math.Max(1, flatCount), clear: false);
            int regions = 0;
            for (int k = 0; k < flatCount; k++)
            {
                int root = Find(parent, k);
                if (root != k) continue;
                regionOf[k] = area[k] >= minArea ? ++regions : Pending;
            }

            int small = 0;
            for (int i = 0; i < len; i++)
            {
                int flat = labels[i];
                if (flat < 0) { labels[i] = 0; mix[i] = MixInterior; continue; }
                int region = regionOf[Find(parent, flat)];
                labels[i] = region;
                mix[i] = MixInterior;
                if (region == Pending) small++;
            }
            SmallPixels = small;
            if (small == 0) { RegionCount = regions; return; }

            int* list = scratch.Int(small, clear: false);
            int* assigned = scratch.Int(small, clear: false);
            int n = 0;
            for (int i = 0; i < len; i++)
                if (labels[i] == Pending) list[n++] = i;

            // 窓の中で見るのは大きな領域の画素だけ(割り当ての順で結果が変わらないように、書き戻しは後で)。
            int mixture = 0;
            int* partners = scratch.Int(small, clear: false);
            for (int k = 0; k < n; k++)
            {
                int i = list[k];
                if (TryMixture(px, labels, w, h, i % w, i / w, out int label, out int partner, out byte coverage))
                {
                    assigned[k] = label;
                    partners[k] = partner;
                    mix[i] = coverage;
                    mixture++;
                }
                else
                {
                    assigned[k] = Pending;
                    mix[i] = MixThin;
                }
            }
            MixturePixels = mixture;
            if (mixture > 0)
            {
                _mixtureIndex = new NativeBuffer((long)mixture * sizeof(int), NativeUsage.Selection);
                _mixturePartner = new NativeBuffer((long)mixture * sizeof(int), NativeUsage.Selection);
                int at = 0;
                for (int k = 0; k < n; k++)
                {
                    if (mix[list[k]] < 128) continue;
                    MixtureIndex[at] = list[k];
                    MixturePartner[at] = partners[k];
                    at++;
                }
            }
            for (int k = 0; k < n; k++)
                labels[list[k]] = assigned[k];

            // 残りは外側から順に、色の近い隣の領域へ。
            int* cur = scratch.Int(small, clear: false);
            int* next = scratch.Int(small, clear: false);
            int nCur = 0;
            for (int k = 0; k < n; k++)
            {
                int i = list[k];
                if (labels[i] != Pending) continue;
                int x = i % w, y = i / w;
                if ((x > 0 && labels[i - 1] > 0) || (x + 1 < w && labels[i + 1] > 0)
                    || (y > 0 && labels[i - w] > 0) || (y + 1 < h && labels[i + w] > 0))
                {
                    labels[i] = InFrontier;
                    cur[nCur++] = i;
                }
            }
            while (nCur > 0)
            {
                for (int k = 0; k < nCur; k++)
                {
                    int i = cur[k], x = i % w, y = i / w;
                    int best = 0, bestDist = int.MaxValue;
                    if (x > 0) Consider(px, labels, i, i - 1, ref best, ref bestDist);
                    if (x + 1 < w) Consider(px, labels, i, i + 1, ref best, ref bestDist);
                    if (y > 0) Consider(px, labels, i, i - w, ref best, ref bestDist);
                    if (y + 1 < h) Consider(px, labels, i, i + w, ref best, ref bestDist);
                    assigned[k] = best;
                }
                for (int k = 0; k < nCur; k++)
                    labels[cur[k]] = assigned[k];
                int nNext = 0;
                for (int k = 0; k < nCur; k++)
                {
                    int i = cur[k], x = i % w, y = i / w;
                    if (x > 0 && labels[i - 1] == Pending) { labels[i - 1] = InFrontier; next[nNext++] = i - 1; }
                    if (x + 1 < w && labels[i + 1] == Pending) { labels[i + 1] = InFrontier; next[nNext++] = i + 1; }
                    if (y > 0 && labels[i - w] == Pending) { labels[i - w] = InFrontier; next[nNext++] = i - w; }
                    if (y + 1 < h && labels[i + w] == Pending) { labels[i + w] = InFrontier; next[nNext++] = i + w; }
                }
                int* swap = cur; cur = next; next = swap;
                nCur = nNext;
            }

            // どの領域にも届かない小さな塊(透明に囲まれた小さな島)は、それぞれ 1 つの領域にする。
            for (int k = 0; k < n; k++)
            {
                if (labels[list[k]] != Pending) continue;
                int id = ++regions;
                int top = 0;
                cur[top++] = list[k];
                labels[list[k]] = id;
                while (top > 0)
                {
                    int i = cur[--top], x = i % w, y = i / w;
                    if (x > 0 && labels[i - 1] == Pending) { labels[i - 1] = id; cur[top++] = i - 1; }
                    if (x + 1 < w && labels[i + 1] == Pending) { labels[i + 1] = id; cur[top++] = i + 1; }
                    if (y > 0 && labels[i - w] == Pending) { labels[i - w] = id; cur[top++] = i - w; }
                    if (y + 1 < h && labels[i + w] == Pending) { labels[i + w] = id; cur[top++] = i + w; }
                }
            }
            RegionCount = regions;
        }

        private static void Consider(Color32* px, int* labels, int i, int neighbor, ref int best, ref int bestDist)
        {
            int l = labels[neighbor];
            if (l <= 0) return;
            int d = RgbDistance(px[i], px[neighbor]);
            if (d < bestDist)
            {
                bestDist = d;
                best = l;
            }
        }

        // 窓の中の 2 つの領域の色(いちばん近い画素の色。グラデの中でもその場の色になる)を結ぶ線分に乗っていれば混色。
        private static bool TryMixture(Color32* px, int* labels, int w, int h, int x, int y, out int label, out int partner, out byte coverage)
        {
            label = 0;
            partner = 0;
            coverage = 0;
            int* candLabel = stackalloc int[MaxMixtureCandidates];
            int* candDist = stackalloc int[MaxMixtureCandidates];
            int* candIndex = stackalloc int[MaxMixtureCandidates];
            int count = 0;
            int y0 = Math.Max(0, y - MixtureWindowRadius), y1 = Math.Min(h - 1, y + MixtureWindowRadius);
            int x0 = Math.Max(0, x - MixtureWindowRadius), x1 = Math.Min(w - 1, x + MixtureWindowRadius);
            for (int ny = y0; ny <= y1; ny++)
            {
                for (int nx = x0; nx <= x1; nx++)
                {
                    int ni = ny * w + nx;
                    int l = labels[ni];
                    if (l <= 0) continue;
                    int dist = Math.Abs(nx - x) + Math.Abs(ny - y);
                    int found = -1;
                    for (int c = 0; c < count; c++)
                        if (candLabel[c] == l) { found = c; break; }
                    if (found >= 0)
                    {
                        if (dist < candDist[found]) { candDist[found] = dist; candIndex[found] = ni; }
                    }
                    else if (count < MaxMixtureCandidates)
                    {
                        candLabel[count] = l;
                        candDist[count] = dist;
                        candIndex[count] = ni;
                        count++;
                    }
                }
            }
            if (count < 2) return false;

            Color32 p = px[y * w + x];
            float bestResidual = float.MaxValue, bestT = 0f;
            int bestA = 0, bestB = 0;
            for (int a = 0; a < count; a++)
            {
                Color32 ca = px[candIndex[a]];
                for (int b = a + 1; b < count; b++)
                {
                    Color32 cb = px[candIndex[b]];
                    if (RgbDistance(ca, cb) < MixtureEndsMin) continue;
                    float dr = ca.r - cb.r, dg = ca.g - cb.g, db = ca.b - cb.b;
                    float t = ((p.r - cb.r) * dr + (p.g - cb.g) * dg + (p.b - cb.b) * db) / (dr * dr + dg * dg + db * db);
                    t = t < 0f ? 0f : t > 1f ? 1f : t;
                    float res = Math.Max(Math.Abs(p.r - (cb.r + t * dr)),
                        Math.Max(Math.Abs(p.g - (cb.g + t * dg)), Math.Abs(p.b - (cb.b + t * db))));
                    if (res < bestResidual)
                    {
                        bestResidual = res;
                        bestT = t;
                        bestA = candLabel[a];
                        bestB = candLabel[b];
                    }
                }
            }
            if (bestResidual > MixtureResidualMax) return false;
            label = bestT >= 0.5f ? bestA : bestB;
            partner = bestT >= 0.5f ? bestB : bestA;
            float cover = Math.Max(bestT, 1f - bestT);
            coverage = (byte)Math.Max(128, Math.Min(255, (int)(cover * 255f + 0.5f)));
            return true;
        }

        private void BuildTables(Color32* px, int stepMax)
        {
            int w = Width, h = Height, count = RegionCount;
            int* labels = Labels;
            byte* mix = Mix;
            _regions = new NativeBuffer((long)(count + 1) * sizeof(RegionInfo), NativeUsage.Selection);
            RegionInfo* table = Regions;
            new Span<RegionInfo>(table, count + 1).Clear();
            using var sums = new NativeBuffer((long)(count + 1) * 6 * sizeof(long), NativeUsage.Work);
            long* sum = (long*)sums.Ptr;
            new Span<long>(sum, (count + 1) * 6).Clear();
            for (int r = 1; r <= count; r++)
            {
                table[r].MinX = w;
                table[r].MinY = h;
                table[r].MaxX = -1;
                table[r].MaxY = -1;
                table[r].BoxMin = 255 | (255 << 8) | (255 << 16);
            }
            var pairs = new Dictionary<long, int>();
            for (int y = 0, i = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, i++)
                {
                    int l = labels[i];
                    if (l <= 0) continue;
                    ref RegionInfo info = ref table[l];
                    Color32 c = px[i];
                    info.Area++;
                    if (x < info.MinX) info.MinX = x;
                    if (x > info.MaxX) info.MaxX = x;
                    if (y < info.MinY) info.MinY = y;
                    if (y > info.MaxY) info.MaxY = y;
                    int at = l * 6;
                    sum[at] += c.r;
                    sum[at + 1] += c.g;
                    sum[at + 2] += c.b;
                    if (mix[i] == MixInterior)
                    {
                        info.InteriorArea++;
                        sum[at + 3] += c.r;
                        sum[at + 4] += c.g;
                        sum[at + 5] += c.b;
                        info.BoxMin = Math.Min(info.BoxMin & 255, c.r) | (Math.Min((info.BoxMin >> 8) & 255, c.g) << 8)
                            | (Math.Min((info.BoxMin >> 16) & 255, c.b) << 16);
                        info.BoxMax = Math.Max(info.BoxMax & 255, c.r) | (Math.Max((info.BoxMax >> 8) & 255, c.g) << 8)
                            | (Math.Max((info.BoxMax >> 16) & 255, c.b) << 16);
                    }
                    if (x + 1 < w) NotePair(px, i, i + 1, labels, mix, pairs, stepMax);
                    if (y + 1 < h) NotePair(px, i, i + w, labels, mix, pairs, stepMax);
                }
            }
            for (int r = 1; r <= count; r++)
            {
                ref RegionInfo info = ref table[r];
                int at = r * 6;
                int offset = info.InteriorArea > 0 ? 3 : 0;
                float n = Math.Max(1, info.InteriorArea > 0 ? info.InteriorArea : info.Area);
                info.R = sum[at + offset] / n;
                info.G = sum[at + offset + 1] / n;
                info.B = sum[at + offset + 2] / n;
            }
        }

        private void NotePair(Color32* px, int i, int j, int* labels, byte* mix, Dictionary<long, int> pairs, int stepMax)
        {
            int a = labels[i], b = labels[j];
            if (a == b || b <= 0) return;
            if (a > b) { int t = a; a = b; b = t; }
            // 上位と下位に分けて詰めない(long の GetHashCode は両者の XOR で、番号の近い組がみな衝突する)。
            long key = a * (RegionCount + 1L) + b;
            int isMixture = mix[i] >= 128 || mix[j] >= 128 ? 1 : 0;
            int isSoft = ColorStep(px[i], px[j]) <= stepMax ? 1 : 0;
            if (pairs.TryGetValue(key, out int index))
            {
                RegionAdjacency e = Adjacency[index];
                e.Contact++;
                e.MixtureContact += isMixture;
                e.SoftContact += isSoft;
                Adjacency[index] = e;
            }
            else
            {
                pairs.Add(key, Adjacency.Count);
                Adjacency.Add(new RegionAdjacency { A = a, B = b, Contact = 1, MixtureContact = isMixture, SoftContact = isSoft });
            }
        }
    }
}
