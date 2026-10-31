// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;

namespace Iroca
{
    /// <summary>
    /// 選択の再設計(T5)の L2: テクスチャの中の素材の地図。領域(<see cref="RegionMap"/>)の色の分布の谷で素材に分ける。
    /// テクスチャごとに 1 回作り、クリックは素材を 1 つ選ぶだけにする(クリックの位置で結果がぶれにくい)。
    /// <para>
    /// 「近い色」はテクスチャの中で相対的に決める(10-24 のユーザー決定)ので、幅を固定せず、そのテクスチャの色の分布に谷があるかで分ける。
    /// 面積で重みをつけるので、縁の混色(面積が小さい)は 2 つの素材の橋にならない。場所は見ない(同じ色の別部品は同じ素材)。
    /// </para>
    /// <list type="number">
    /// <item>(a, b) を明度で割り、半径だけ対数で縮めた平面で、領域の面積の分布の山に分ける。同じ素材の陰影は明度が変わっても
    ///   (a, b) / L があまり動かず、彩度の揺れは比で効くので、低彩度は細かく、高彩度は粗く見る平面になる。</item>
    /// <item>山の中を明度の分布の深い谷で分ける(白・灰・黒は同じ山に入る)。</item>
    /// <item>白に近い素材と、なだらかにつながる素材をまとめる。白い布の影は色づくので、白と影が別の山に分かれるが、
    ///   グラデの続きは同じ素材(なだらかな接触は <see cref="RegionAdjacency.SoftContact"/>)。</item>
    /// </list>
    /// </summary>
    internal sealed unsafe class MaterialMap
    {
        // 色の平面の半径の縮め方: r' = log(1 + r / PlaneChromaScale)。
        internal const double PlaneChromaScale = 0.01;
        // 明度で割るときの下限(黒の近くで (a, b) / L が暴れないように)。
        internal const double PlaneLightFloor = 0.1;
        // 色の平面の分布のぼかしの幅(山の細かさ)。
        internal const double PlaneBandwidth = 0.3;
        // 色の平面のマスの大きさと、平面の端(r' の上限より十分外)。
        internal const double PlaneCell = 0.05;
        internal const double PlaneExtent = 4.5;
        // 明度の分布のマスの数とぼかしの幅(明度 0〜1 に対する幅)。
        internal const int LightBins = 128;
        internal const double LightBandwidth = 0.03;
        // 谷の深さ: 谷が両隣の山の低い方のこの割合より低いときだけ、素材を分ける。
        internal const double LightValleyRatio = 0.3;
        // 白に近い素材のまとめ直し: なだらかな接触の長さ / 小さい方の素材の境界の長さ がこれ以上の組。
        internal const double SoftMergeContact = 0.25;
        // 組のどちらかが、色の平面でこの半径以内(白に近い)のときだけ。色のある素材どうしはまとめない(肌と頬の赤みなど)。
        internal const double SoftMergeNeutralRadius = 0.75;
        // 組の色の平面の距離の上限。
        internal const double MergeDistance = 2.0;
        // ぼかしの窓の半径(幅の何倍まで見るか)。
        internal const double KernelTruncate = 4.0;
        // これより小さい領域(縁・点)をクリックしたら、色の近い隣の大きい領域へ移る。
        internal const int AnchorArea = 64;
        // 「色の近い隣」の OkLab の距離。
        internal const double AnchorNear = 0.05;
        // 移る回数の上限。
        internal const int AnchorHops = 3;
        // AI 提案の投票: クリックの素材が提案の中でこの割合より少なければ、提案の中でいちばん多い素材へ移る。
        internal const double VoteMinorityShare = 0.1;
        // 投票は、クリックした領域が見える画素のこの割合以下のときだけ(縁や点をクリックしたときの救い)。
        // 十分に大きな領域をクリックしたなら、その素材を選んだとみなす(提案が隣の部品を含むことがあるので)。
        internal const double VoteRegionShare = 1e-4;
        // 色の平面で近い組をまとめる条件(縞・市松の 2 トーン): 平面の距離、境界のうち相手との接触の割合、
        // 接触の長さ(同じ面積の正方形の周の何倍か)、面積の釣り合い。
        internal const double InterleaveDistance = 1.0;
        internal const double InterleaveContact = 0.7;
        internal const double InterleaveLength = 3.0;
        internal const double InterleaveArea = 0.25;
        // 素材の内側を AI 提案の色で絞る管: 提案の中の領域の面積がこれ以上のときだけ。
        internal const double TubeMinEvidence = 64.0;
        // 管の線を引くときの、明度で分ける区間の数。
        internal const int TubeSegments = 4;
        // 管の幅と明度の範囲を測る分位(提案の縁に混じる少数の色を入れない)。
        internal const double TubeQuantile = 0.95;
        // 管の幅の倍率と下限、明度の範囲の余白。
        internal const double TubeWidthScale = 1.5;
        internal const double TubeWidthFloor = 0.02;
        internal const double TubeLightMargin = 0.15;
        // 彩度の高い素材は (a, b) の揺れも大きいので、管の幅は線の彩度のこの割合までは広げる。
        internal const double TubeChromaShare = 0.4;
        // ゼロ割りと重みの 0 を避ける小さな値。
        private const double TubeGuard = 1e-3;
        // スポイト色がクリックの素材の色と言えるか(その素材のどこかの領域との OkLab の距離)。
        internal const double SampleOnMaterial = 0.03;

        private readonly RegionMap _regions;
        private readonly double[] _lab;
        private readonly int[] _neighborStart;
        private readonly int[] _neighbors;

        /// <summary>素材の数(番号は 1〜Count)。</summary>
        public int Count { get; private set; }
        /// <summary>領域の番号 → 素材の番号。添字 0(透明)は 0。大きさは領域の数なので画素数ほど大きくならない。</summary>
        public readonly int[] MaterialOfRegion;

        private MaterialMap(RegionMap regions)
        {
            _regions = regions;
            int n = regions.RegionCount;
            MaterialOfRegion = new int[n + 1];
            _lab = new double[(n + 1) * 3];
            RegionInfo* table = regions.Regions;
            for (int r = 1; r <= n; r++)
            {
                OkLab.RgbToOklab(table[r].R / 255f, table[r].G / 255f, table[r].B / 255f, out float L, out float a, out float b);
                _lab[r * 3] = L;
                _lab[r * 3 + 1] = a;
                _lab[r * 3 + 2] = b;
            }

            // 隣の一覧
            _neighborStart = new int[n + 2];
            RegionAdjacency* adjacency = regions.Adjacency;
            int pairCount = regions.AdjacencyCount;
            for (int k = 0; k < pairCount; k++)
            {
                _neighborStart[adjacency[k].A + 1]++;
                _neighborStart[adjacency[k].B + 1]++;
            }
            for (int r = 1; r <= n + 1; r++) _neighborStart[r] += _neighborStart[r - 1];
            _neighbors = new int[_neighborStart[n + 1]];
            var fill = (int[])_neighborStart.Clone();
            for (int k = 0; k < pairCount; k++)
            {
                _neighbors[fill[adjacency[k].A]++] = adjacency[k].B;
                _neighbors[fill[adjacency[k].B]++] = adjacency[k].A;
            }
        }

        public static MaterialMap Build(RegionMap regions)
        {
            var map = new MaterialMap(regions);
            map.Divide();
            return map;
        }

        private void Divide()
        {
            int n = _regions.RegionCount;
            RegionInfo* table = _regions.Regions;
            int size = (int)(PlaneExtent * 2.0 / PlaneCell) + 1;

            // (1) 色の平面の山
            var cellOfRegion = new int[n + 1];
            var planeA = new double[n + 1];
            var planeB = new double[n + 1];
            var hist = new double[size * size];
            for (int r = 1; r <= n; r++)
            {
                double light = Math.Max(_lab[r * 3], PlaneLightFloor);
                double a = _lab[r * 3 + 1] / light, b = _lab[r * 3 + 2] / light;
                double radius = Math.Sqrt(a * a + b * b);
                double scale = Math.Log(1.0 + radius / PlaneChromaScale) / Math.Max(radius, 1e-9);
                planeA[r] = a * scale;
                planeB[r] = b * scale;
                int ix = Clamp((int)((planeA[r] + PlaneExtent) / PlaneCell), size);
                int iy = Clamp((int)((planeB[r] + PlaneExtent) / PlaneCell), size);
                cellOfRegion[r] = iy * size + ix;
                hist[cellOfRegion[r]] += table[r].Area;
            }
            double[] density = Blur2D(hist, size, PlaneBandwidth / PlaneCell);
            int[] peak = ClimbToPeak(density, size);

            // (2) 山ごとに、明度の分布の谷で分ける
            var byPeak = new Dictionary<int, List<int>>();
            for (int r = 1; r <= n; r++)
            {
                int key = peak[cellOfRegion[r]];
                if (!byPeak.TryGetValue(key, out var list)) byPeak[key] = list = new List<int>();
                list.Add(r);
            }
            var keys = new List<int>(byPeak.Keys);
            keys.Sort();
            int next = 1;
            var light1 = new double[LightBins];
            var kernel = GaussianKernel(LightBandwidth * LightBins);
            foreach (int key in keys)
            {
                var regions = byPeak[key];
                Array.Clear(light1, 0, LightBins);
                foreach (int r in regions) light1[LightBin(r)] += table[r].Area;
                double[] d = Blur1D(light1, LightBins, kernel);
                List<int> cuts = ValleyCuts(d);
                foreach (int r in regions)
                {
                    int bin = LightBin(r), segment = 0;
                    while (segment < cuts.Count && cuts[segment] <= bin) segment++;
                    MaterialOfRegion[r] = next + segment;
                }
                next += cuts.Count + 1;
            }
            Count = next - 1;
            MergeMaterials(planeA, planeB);
        }

        // 素材をまとめる(素材の番号は根の番号のまま。飛び番になる)。色の平面で近い組のうち、次のどちらかを満たすもの:
        // - 白に近い素材と、なだらかにつながる素材(白飛びへ続くツヤやグラデの続き)
        // - 互いの境界のほとんどが相手との接触で、接触が面積に比べて長く、面積も釣り合う組(縞・市松の 2 トーン。別の素材ならここまで入り組まない)
        private void MergeMaterials(double[] planeA, double[] planeB)
        {
            RegionInfo* table = _regions.Regions;
            int m = Count + 1;
            var border = new double[m];
            var pairs = new Dictionary<long, (double contact, double soft)>();
            RegionAdjacency* adjacency = _regions.Adjacency;
            for (int k = 0; k < _regions.AdjacencyCount; k++)
            {
                ref RegionAdjacency e = ref adjacency[k];
                int a = MaterialOfRegion[e.A], b = MaterialOfRegion[e.B];
                if (a == b) continue;
                border[a] += e.Contact;
                border[b] += e.Contact;
                long key = (long)Math.Min(a, b) * m + Math.Max(a, b);
                pairs.TryGetValue(key, out var v);
                pairs[key] = (v.contact + e.Contact, v.soft + e.SoftContact);
            }
            if (pairs.Count == 0) return;

            var weight = new double[m];
            var centerA = new double[m];
            var centerB = new double[m];
            for (int r = 1; r <= _regions.RegionCount; r++)
            {
                int k = MaterialOfRegion[r];
                weight[k] += table[r].Area;
                centerA[k] += table[r].Area * planeA[r];
                centerB[k] += table[r].Area * planeB[r];
            }
            var radius = new double[m];
            for (int k = 0; k < m; k++)
            {
                double w = Math.Max(weight[k], 1e-6);
                centerA[k] /= w;
                centerB[k] /= w;
                radius[k] = Math.Sqrt(centerA[k] * centerA[k] + centerB[k] * centerB[k]);
            }

            var root = new int[m];
            for (int k = 0; k < m; k++) root[k] = k;
            int Find(int k)
            {
                while (root[k] != k)
                {
                    root[k] = root[root[k]];
                    k = root[k];
                }
                return k;
            }
            var keys = new List<long>(pairs.Keys);
            keys.Sort();
            foreach (long key in keys)
            {
                int a = (int)(key / m), b = (int)(key % m);
                double da = centerA[a] - centerA[b], db = centerB[a] - centerB[b];
                double distance = Math.Sqrt(da * da + db * db);
                double smaller = Math.Min(weight[a], weight[b]);
                bool softToWhite = distance <= MergeDistance && Math.Min(radius[a], radius[b]) <= SoftMergeNeutralRadius
                    && pairs[key].soft >= SoftMergeContact * Math.Max(1.0, Math.Min(border[a], border[b]));
                bool interleaved = distance <= InterleaveDistance && smaller >= InterleaveArea * Math.Max(weight[a], weight[b])
                    && pairs[key].contact >= InterleaveContact * Math.Max(border[a], border[b])
                    && pairs[key].contact >= InterleaveLength * 4.0 * Math.Sqrt(smaller);
                if (!softToWhite && !interleaved) continue;
                int ra = Find(a), rb = Find(b);
                if (ra != rb) root[rb] = ra;
            }
            for (int r = 1; r <= _regions.RegionCount; r++) MaterialOfRegion[r] = Find(MaterialOfRegion[r]);
        }

        private int LightBin(int r) => Clamp((int)(_lab[r * 3] * LightBins), LightBins);

        private static int Clamp(int v, int size) => v < 0 ? 0 : v >= size ? size - 1 : v;

        // 山と山の間の谷のうち、十分に深いものの位置(明度のマス)。
        private static List<int> ValleyCuts(double[] d)
        {
            var cuts = new List<int>();
            int last = -1;
            for (int j = 0; j < d.Length; j++)
            {
                if (d[j] <= 0.0) continue;
                bool rising = j == 0 || d[j] >= d[j - 1];
                bool falling = j == d.Length - 1 || d[j] > d[j + 1];
                if (!rising || !falling) continue;
                if (last >= 0)
                {
                    int valley = last;
                    for (int v = last; v <= j; v++)
                        if (d[v] < d[valley]) valley = v;
                    if (d[valley] < LightValleyRatio * Math.Min(d[last], d[j])) cuts.Add(valley);
                }
                last = j;
            }
            return cuts;
        }

        private static double[] GaussianKernel(double sigma)
        {
            int radius = (int)(KernelTruncate * sigma + 0.5);
            var kernel = new double[radius * 2 + 1];
            double sum = 0.0;
            for (int i = -radius; i <= radius; i++)
            {
                kernel[i + radius] = Math.Exp(-0.5 * i * i / (sigma * sigma));
                sum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
            return kernel;
        }

        private static double[] Blur1D(double[] src, int size, double[] kernel)
        {
            int radius = kernel.Length / 2;
            var dst = new double[size];
            for (int i = 0; i < size; i++)
            {
                double v = 0.0;
                int k0 = Math.Max(-radius, -i), k1 = Math.Min(radius, size - 1 - i);
                for (int k = k0; k <= k1; k++) v += src[i + k] * kernel[k + radius];
                dst[i] = v;
            }
            return dst;
        }

        private static double[] Blur2D(double[] src, int size, double sigma)
        {
            double[] kernel = GaussianKernel(sigma);
            int radius = kernel.Length / 2;
            var rows = new double[size * size];
            var dst = new double[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double value = src[y * size + x];
                    if (value == 0.0) continue;
                    int k0 = Math.Max(-radius, -x), k1 = Math.Min(radius, size - 1 - x);
                    for (int k = k0; k <= k1; k++) rows[y * size + x + k] += value * kernel[k + radius];
                }
            }
            for (int y = 0; y < size; y++)
            {
                int k0 = Math.Max(-radius, -y), k1 = Math.Min(radius, size - 1 - y);
                for (int k = k0; k <= k1; k++)
                {
                    double w = kernel[k + radius];
                    int from = y * size, to = (y + k) * size;
                    for (int x = 0; x < size; x++) dst[to + x] += rows[from + x] * w;
                }
            }
            return dst;
        }

        // 各マスから、周り 8 マスでいちばん高いマスへ登り、着いた峰のマスの番号を返す。
        private static int[] ClimbToPeak(double[] density, int size)
        {
            var parent = new int[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int here = y * size + x, best = here;
                    double bestValue = density[here];
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= size) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= size) continue;
                            double v = density[ny * size + nx];
                            if (v > bestValue)
                            {
                                bestValue = v;
                                best = ny * size + nx;
                            }
                        }
                    }
                    parent[here] = best;
                }
            }
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int i = 0; i < parent.Length; i++)
                {
                    int up = parent[parent[i]];
                    if (up != parent[i])
                    {
                        parent[i] = up;
                        changed = true;
                    }
                }
            }
            return parent;
        }

        /// <summary>小さな領域(縁・点)をクリックしたら、色の近い隣のいちばん大きい領域へ移す(地色への正規化)。</summary>
        public int AnchorRegion(int home)
        {
            RegionInfo* table = _regions.Regions;
            int r = home;
            for (int hop = 0; hop < AnchorHops; hop++)
            {
                if (table[r].Area >= AnchorArea) return r;
                int start = _neighborStart[r], end = _neighborStart[r + 1];
                if (start == end) return r;
                int bestNear = 0, bestAny = 0;
                for (int k = start; k < end; k++)
                {
                    int nb = _neighbors[k];
                    if (bestAny == 0 || table[nb].Area > table[bestAny].Area) bestAny = nb;
                    double dL = _lab[nb * 3] - _lab[r * 3], da = _lab[nb * 3 + 1] - _lab[r * 3 + 1], db = _lab[nb * 3 + 2] - _lab[r * 3 + 2];
                    if (dL * dL + da * da + db * db <= AnchorNear * AnchorNear
                        && (bestNear == 0 || table[nb].Area > table[bestNear].Area))
                        bestNear = nb;
                }
                int best = bestNear != 0 ? bestNear : bestAny;
                if (table[best].Area <= table[r].Area) return r;
                r = best;
            }
            return r;
        }

        private double DistanceTo(int r, double L, double a, double b)
        {
            double dL = _lab[r * 3] - L, da = _lab[r * 3 + 1] - a, db = _lab[r * 3 + 2] - b;
            return Math.Sqrt(dL * dL + da * da + db * db);
        }

        // 色がいちばん近い領域(縁や点を基準にしないよう、大きい領域の中から。無ければ全部から)。
        private int NearestRegion(double L, double a, double b)
        {
            RegionInfo* table = _regions.Regions;
            int best = 0, bestAny = 0;
            double bestDist = double.MaxValue, bestAnyDist = double.MaxValue;
            for (int r = 1; r <= _regions.RegionCount; r++)
            {
                double d = DistanceTo(r, L, a, b);
                if (d < bestAnyDist) { bestAnyDist = d; bestAny = r; }
                if (table[r].Area >= AnchorArea && d < bestDist) { bestDist = d; best = r; }
            }
            return best != 0 ? best : bestAny;
        }

        private int HomeRegionOfUv(UnityEngine.Vector2 uv, int w, int h)
        {
            PreviewCoords.UvToPixel(uv.x, uv.y, w, h, out int x, out int y);
            return _regions.Labels[y * w + x];
        }

        /// <summary>
        /// スポイト色の素材。スポイト位置があり、その素材がスポイト色を含むならそれ(同じ色の別の素材と迷わない)。
        /// そうでなければ色がいちばん近い領域の素材(スポイト位置が無いゾーン、自動調整が地色へ置き換えたゾーン)。
        /// </summary>
        private int MaterialOfColor(UnityEngine.Color color, bool hasUv, UnityEngine.Vector2 uv, int w, int h)
        {
            OkLab.RgbToOklab(color.r, color.g, color.b, out float L, out float a, out float b);
            if (hasUv)
            {
                int home = HomeRegionOfUv(uv, w, h);
                if (home > 0)
                {
                    int byUv = MaterialOfRegion[AnchorRegion(home)];
                    for (int r = 1; r <= _regions.RegionCount; r++)
                        if (MaterialOfRegion[r] == byUv && DistanceTo(r, L, a, b) <= SampleOnMaterial) return byUv;
                }
            }
            int nearest = NearestRegion(L, a, b);
            return nearest > 0 ? MaterialOfRegion[nearest] : 0;
        }

        /// <summary>
        /// ゾーンが選ぶ領域(添字は領域の番号)。スポイト色と内部サンプルの素材を全部選ぶ。場所は見ない(同じ色の別部品は同じ素材)。
        /// シード(Shift+クリック)があるときだけ、シードの領域からつながる領域に絞る。
        /// </summary>
        public bool[] SelectedRegions(ColorZone zone, int w, int h)
        {
            int n = _regions.RegionCount;
            var wanted = new bool[Count + 1];
            int mine = MaterialOfColor(zone.sampleColor, zone.HasSampleUV, zone.sampleUV, w, h);
            if (mine > 0) wanted[mine] = true;
            if (zone.extraSamples != null)
            {
                foreach (var extra in zone.extraSamples)
                {
                    int m = MaterialOfColor(extra, false, default, w, h);
                    if (m > 0) wanted[m] = true;
                }
            }
            var selected = new bool[n + 1];
            for (int r = 1; r <= n; r++) selected[r] = wanted[MaterialOfRegion[r]];
            if (zone.materialTube != null && zone.materialTube.Length >= 3) ApplyTube(selected, zone.materialTube);
            ApplyWidth(selected, wanted, zone);
            if (zone.useFloodFill && zone.seedUV.x >= 0f)
            {
                int seed = HomeRegionOfUv(zone.seedUV, w, h);
                if (seed > 0 && selected[seed]) KeepConnected(selected, seed);
            }
            return selected;
        }

        private void KeepConnected(bool[] selected, int seed)
        {
            var reached = new bool[selected.Length];
            var stack = new Stack<int>();
            reached[seed] = true;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int r = stack.Pop();
                for (int k = _neighborStart[r]; k < _neighborStart[r + 1]; k++)
                {
                    int nb = _neighbors[k];
                    if (selected[nb] && !reached[nb])
                    {
                        reached[nb] = true;
                        stack.Push(nb);
                    }
                }
            }
            Array.Copy(reached, selected, selected.Length);
        }

        // 重みつきの分位(値の小さい順に重みを足し、q に届いた所の値)。
        private static double WeightedQuantile(List<(double value, double weight)> sorted, double total, double q)
        {
            double target = q * total, sum = 0.0;
            for (int i = 0; i < sorted.Count; i++)
            {
                sum += sorted[i].weight;
                if (sum >= target) return sorted[i].value;
            }
            return sorted[sorted.Count - 1].value;
        }

        private static double WeightedQuantile(List<(double value, double weight)> items, double q)
        {
            items.Sort((a, b) => a.value.CompareTo(b.value));
            double total = 0.0;
            foreach (var it in items) total += it.weight;
            return WeightedQuantile(items, total, q);
        }

        // 折れ線からの (a, b) の距離。線の外は、暗い側は黒へ・明るい側は白へ向かう直線(黒・白との混色)で延ばす。
        // tube = [幅, 明度の下限, 明度の上限, L0, a0, b0, L1, a1, b1, ...]。chroma はその明度での線の彩度。
        private static double TubeResidual(float[] tube, double L, double a, double b, out double chroma)
        {
            int knots = (tube.Length - 3) / 3;
            double ea, eb;
            double firstL = tube[3], lastL = tube[3 + (knots - 1) * 3];
            if (L < firstL)
            {
                double f = Math.Min(1.0, Math.Max(0.0, L / Math.Max(firstL, TubeGuard)));
                ea = tube[4] * f;
                eb = tube[5] * f;
            }
            else if (L > lastL)
            {
                double f = Math.Min(1.0, Math.Max(0.0, (1.0 - L) / Math.Max(1.0 - lastL, TubeGuard)));
                ea = tube[3 + (knots - 1) * 3 + 1] * f;
                eb = tube[3 + (knots - 1) * 3 + 2] * f;
            }
            else
            {
                int k = 0;
                while (k < knots - 2 && L > tube[3 + (k + 1) * 3]) k++;
                double L0 = tube[3 + k * 3], L1 = knots > 1 ? tube[3 + (k + 1) * 3] : L0;
                double t = L1 > L0 ? (L - L0) / (L1 - L0) : 0.0;
                int next = knots > 1 ? k + 1 : k;
                ea = tube[3 + k * 3 + 1] + (tube[3 + next * 3 + 1] - tube[3 + k * 3 + 1]) * t;
                eb = tube[3 + k * 3 + 2] + (tube[3 + next * 3 + 2] - tube[3 + k * 3 + 2]) * t;
            }
            chroma = Math.Sqrt(ea * ea + eb * eb);
            double da = a - ea, db = b - eb;
            return Math.Sqrt(da * da + db * db);
        }

        /// <summary>
        /// 素材の内側を絞る管を、AI 提案の中にあるその素材の領域から作る(提案に無い色の別部品を落とす)。
        /// 提案の中の領域が足りなければ null(素材を丸ごと選ぶ)。
        /// </summary>
        private float[] FitTube(int material, float[] perRegion)
        {
            RegionInfo* table = _regions.Regions;
            var teach = new List<int>();
            double evidence = 0.0;
            for (int r = 1; r <= _regions.RegionCount; r++)
            {
                if (MaterialOfRegion[r] != material || perRegion[r] < 0.5f * Math.Max(table[r].Area, 1)) continue;
                teach.Add(r);
                evidence += perRegion[r];
            }
            // 領域が折れ線の区間より少ないと線を引けない(広い素材の一部の、ツヤの数領域だけが提案に入ったときなど)。
            if (teach.Count < TubeSegments || evidence < TubeMinEvidence) return null;

            var byL = new List<(double value, double weight)>(teach.Count);
            foreach (int r in teach) byL.Add((_lab[r * 3], Math.Max(perRegion[r], TubeGuard)));
            byL.Sort((x, y) => x.value.CompareTo(y.value));
            double total = 0.0;
            foreach (var it in byL) total += it.weight;

            // 明度の区間ごとの中央の色をつないで折れ線にする
            var knots = new List<float>();
            double previousL = double.NegativeInfinity;
            for (int seg = 0; seg < TubeSegments; seg++)
            {
                double lo = WeightedQuantile(byL, total, seg / (double)TubeSegments);
                double hi = WeightedQuantile(byL, total, (seg + 1) / (double)TubeSegments);
                var ls = new List<(double value, double weight)>();
                var es = new List<(double value, double weight)>();
                var bs = new List<(double value, double weight)>();
                foreach (int r in teach)
                {
                    double L = _lab[r * 3];
                    if (L < lo || L > hi) continue;
                    double w = Math.Max(perRegion[r], TubeGuard);
                    ls.Add((L, w));
                    es.Add((_lab[r * 3 + 1], w));
                    bs.Add((_lab[r * 3 + 2], w));
                }
                if (ls.Count == 0) continue;
                double kL = WeightedQuantile(ls, 0.5);
                if (kL - previousL <= 1e-5) continue;
                knots.Add((float)kL);
                knots.Add((float)WeightedQuantile(es, 0.5));
                knots.Add((float)WeightedQuantile(bs, 0.5));
                previousL = kL;
            }
            var tube = new float[3 + knots.Count];
            knots.CopyTo(tube, 3);

            var residuals = new List<(double value, double weight)>(teach.Count);
            foreach (int r in teach)
                residuals.Add((TubeResidual(tube, _lab[r * 3], _lab[r * 3 + 1], _lab[r * 3 + 2], out _), Math.Max(perRegion[r], TubeGuard)));
            double width = Math.Max(WeightedQuantile(residuals, TubeQuantile) * TubeWidthScale, TubeWidthFloor);
            tube[0] = (float)width;
            tube[1] = (float)(WeightedQuantile(byL, total, 1.0 - TubeQuantile) - TubeLightMargin);
            tube[2] = (float)(WeightedQuantile(byL, total, TubeQuantile) + TubeLightMargin);
            return tube;
        }

        // 広さ(許容のスライダー)。既定の値では素材そのまま。下げると素材の中でスポイト色から遠い領域から落とし、
        // 上げるとスポイト色に近い別の素材から足す。どちらも値に対して単調で、選択はつながって広がる。
        private void ApplyWidth(bool[] selected, bool[] wanted, ColorZone zone)
        {
            double width = zone.tolerance, standard = ZonesJsonDefaults.Tolerance;
            if (width == standard) return;
            OkLab.RgbToOklab(zone.sampleColor.r, zone.sampleColor.g, zone.sampleColor.b, out float L, out float a, out float b);
            RegionInfo* table = _regions.Regions;
            if (width < standard)
            {
                var kept = new List<(double value, double weight)>();
                for (int r = 1; r < selected.Length; r++)
                    if (selected[r]) kept.Add((DistanceTo(r, L, a, b), table[r].Area));
                if (kept.Count == 0) return;
                double limit = WeightedQuantile(kept, Math.Max(width, 0.0) / standard);
                for (int r = 1; r < selected.Length; r++)
                    if (selected[r] && DistanceTo(r, L, a, b) > limit) selected[r] = false;
                return;
            }

            // 上げる側は、素材の代表色(面積の重みの平均)がスポイト色から reach 以内の素材を足す。
            // 上げ始めは細かく動くように 2 乗にする(OkLab の距離は黒と白で約 1)。
            double t = (width - standard) / (1.0 - standard);
            double reach = t * t;
            var weight = new double[Count + 1];
            var sum = new double[(Count + 1) * 3];
            for (int r = 1; r < selected.Length; r++)
            {
                int m = MaterialOfRegion[r];
                weight[m] += table[r].Area;
                sum[m * 3] += table[r].Area * _lab[r * 3];
                sum[m * 3 + 1] += table[r].Area * _lab[r * 3 + 1];
                sum[m * 3 + 2] += table[r].Area * _lab[r * 3 + 2];
            }
            var add = new bool[Count + 1];
            for (int m = 1; m <= Count; m++)
            {
                if (weight[m] <= 0.0 || wanted[m]) continue;
                double dL = sum[m * 3] / weight[m] - L, da = sum[m * 3 + 1] / weight[m] - a, db = sum[m * 3 + 2] / weight[m] - b;
                add[m] = dL * dL + da * da + db * db <= reach * reach;
            }
            for (int r = 1; r < selected.Length; r++)
                if (add[MaterialOfRegion[r]]) selected[r] = true;
        }

        private void ApplyTube(bool[] selected, float[] tube)
        {
            for (int r = 1; r < selected.Length; r++)
            {
                if (!selected[r]) continue;
                double L = _lab[r * 3];
                double residual = TubeResidual(tube, L, _lab[r * 3 + 1], _lab[r * 3 + 2], out double chroma);
                if (L < tube[1] || L > tube[2] || residual > Math.Max(tube[0], TubeChromaShare * chroma))
                    selected[r] = false;
            }
        }

        /// <summary>
        /// クリックの素材を決め(AI 提案は投票に使う)、スポイト色をその素材の地色(いちばん大きい領域の色)にする。
        /// </summary>
        public void TuneZone(ColorZone zone, int w, int h, bool[] evidence, int evidenceW, int evidenceH)
        {
            int n = _regions.RegionCount;
            int home = zone.HasSampleUV ? HomeRegionOfUv(zone.sampleUV, w, h) : 0;
            if (home <= 0)
            {
                OkLab.RgbToOklab(zone.sampleColor.r, zone.sampleColor.g, zone.sampleColor.b, out float L, out float a, out float b);
                home = NearestRegion(L, a, b);
            }
            if (home <= 0) return;
            float[] perRegion = null;
            if (evidence != null && evidenceW > 0 && evidenceH > 0)
            {
                perRegion = new float[n + 1];
                int* labels = _regions.Labels;
                for (int y = 0; y < evidenceH; y++)
                {
                    int ty = y * h / evidenceH;
                    for (int x = 0; x < evidenceW; x++)
                    {
                        if (!evidence[y * evidenceW + x]) continue;
                        perRegion[labels[ty * w + x * w / evidenceW]] += 1f;
                    }
                }
                perRegion[0] = 0f;
            }
            int material = PickMaterial(home, perRegion);
            zone.materialTube = perRegion != null ? FitTube(material, perRegion) : null;
            zone.tolerance = ZonesJsonDefaults.Tolerance;
            RegionInfo* table = _regions.Regions;
            int largest = 0;
            for (int r = 1; r <= n; r++)
                if (MaterialOfRegion[r] == material && (largest == 0 || table[r].Area > table[largest].Area)) largest = r;
            if (largest == 0) return;
            float alpha = zone.sampleColor.a;
            zone.sampleColor = new UnityEngine.Color(table[largest].R / 255f, table[largest].G / 255f, table[largest].B / 255f, alpha);
            if (zone.extraSamples != null) zone.extraSamples.Clear();
            // スポイト位置も地色の領域へ移す(投票で素材を移したとき、元の位置を見て元の素材へ戻らないように)。
            int* regionOf = _regions.Labels;
            for (int y = table[largest].MinY; y <= table[largest].MaxY; y++)
            {
                for (int x = table[largest].MinX; x <= table[largest].MaxX; x++)
                {
                    if (regionOf[y * w + x] != largest) continue;
                    zone.sampleUV = new UnityEngine.Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                    return;
                }
            }
        }

        /// <summary>
        /// クリックした領域の素材。<paramref name="evidencePerRegion"/> は AI 提案の領域ごとの画素数(添字は領域の番号。無ければ null)。
        /// クリックの素材が提案の中で少数なら、提案の中でいちばん多い素材を選ぶ(端の色や縁をクリックしたときの救い)。
        /// </summary>
        public int PickMaterial(int homeRegion, float[] evidencePerRegion)
        {
            int mine = MaterialOfRegion[AnchorRegion(homeRegion)];
            if (evidencePerRegion == null) return mine;
            RegionInfo* table = _regions.Regions;
            long visible = 0;
            for (int r = 1; r <= _regions.RegionCount; r++) visible += table[r].Area;
            if (table[homeRegion].Area > VoteRegionShare * visible) return mine;
            var share = new double[Count + 1];
            double total = 0.0;
            int n = Math.Min(evidencePerRegion.Length - 1, _regions.RegionCount);
            for (int r = 1; r <= n; r++)
            {
                double v = evidencePerRegion[r];
                if (v <= 0.0) continue;
                share[MaterialOfRegion[r]] += v;
                total += v;
            }
            if (total <= 0.0 || share[mine] >= VoteMinorityShare * total) return mine;
            int best = mine;
            for (int m = 1; m <= Count; m++)
                if (share[m] > share[best]) best = m;
            return best;
        }
    }
}
