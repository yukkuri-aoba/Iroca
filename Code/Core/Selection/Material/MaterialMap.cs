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
            foreach (var e in regions.Adjacency)
            {
                _neighborStart[e.A + 1]++;
                _neighborStart[e.B + 1]++;
            }
            for (int r = 1; r <= n + 1; r++) _neighborStart[r] += _neighborStart[r - 1];
            _neighbors = new int[_neighborStart[n + 1]];
            var fill = (int[])_neighborStart.Clone();
            foreach (var e in regions.Adjacency)
            {
                _neighbors[fill[e.A]++] = e.B;
                _neighbors[fill[e.B]++] = e.A;
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
            var hist = new double[size * size];
            for (int r = 1; r <= n; r++)
            {
                double light = Math.Max(_lab[r * 3], PlaneLightFloor);
                double a = _lab[r * 3 + 1] / light, b = _lab[r * 3 + 2] / light;
                double radius = Math.Sqrt(a * a + b * b);
                double scale = Math.Log(1.0 + radius / PlaneChromaScale) / Math.Max(radius, 1e-9);
                int ix = Clamp((int)((a * scale + PlaneExtent) / PlaneCell), size);
                int iy = Clamp((int)((b * scale + PlaneExtent) / PlaneCell), size);
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
