// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca
{
    /// <summary>
    /// 選択の再設計(T5)の L2 の下ごしらえ: テクスチャの中の色の塊。領域の代表の色(OkLab)を、面積の大きい領域から順に代表に立て、
    /// 代表から radius 以内の領域をまとめる。場所は見ない(つながっていなくても同じ色なら同じ塊)。
    /// 代表を動かさないので塊の広さは radius で抑えられ、順番は面積と番号だけで決まる。
    /// </summary>
    internal sealed unsafe class ColorClusters
    {
        /// <summary>塊の数。</summary>
        public int Count { get; private set; }
        /// <summary>領域の番号 → 塊の番号(0 から)。添字 0 は使わない。大きさは領域の数なので画素数ほど大きくならない。</summary>
        public readonly int[] ClusterOfRegion;
        /// <summary>塊の代表の色(OkLab。L, a, b の順に 3 つずつ)。</summary>
        public float[] Leaders { get; private set; }

        private ColorClusters(int regionCount)
        {
            ClusterOfRegion = new int[regionCount + 1];
        }

        public static ColorClusters Build(RegionMap map, float radius)
        {
            int n = map.RegionCount;
            var result = new ColorClusters(n);
            var lab = new float[(n + 1) * 3];
            var order = new int[n];
            var area = new int[n];
            RegionInfo* table = map.Regions;
            for (int r = 1; r <= n; r++)
            {
                OkLab.RgbToOklab(table[r].R / 255f, table[r].G / 255f, table[r].B / 255f,
                    out lab[r * 3], out lab[r * 3 + 1], out lab[r * 3 + 2]);
                order[r - 1] = r;
                area[r - 1] = -table[r].Area;
            }
            Array.Sort(area, order);

            // 面積の大きい順に、どの代表からも radius より遠い領域を新しい代表にする。
            float radius2 = radius * radius;
            var leaders = new float[Math.Max(1, n) * 3];
            int count = 0;
            for (int k = 0; k < n; k++)
            {
                int r = order[k];
                float L = lab[r * 3], a = lab[r * 3 + 1], b = lab[r * 3 + 2];
                bool near = false;
                for (int c = 0; c < count && !near; c++)
                {
                    float dL = L - leaders[c * 3], da = a - leaders[c * 3 + 1], db = b - leaders[c * 3 + 2];
                    near = dL * dL + da * da + db * db <= radius2;
                }
                if (near) continue;
                leaders[count * 3] = L;
                leaders[count * 3 + 1] = a;
                leaders[count * 3 + 2] = b;
                count++;
            }

            // 代表が全部決まってから、各領域をいちばん近い代表へ(先に立った代表へ偏らないように)。
            for (int r = 1; r <= n; r++)
            {
                float L = lab[r * 3], a = lab[r * 3 + 1], b = lab[r * 3 + 2];
                int best = 0;
                float bestDist = float.MaxValue;
                for (int c = 0; c < count; c++)
                {
                    float dL = L - leaders[c * 3], da = a - leaders[c * 3 + 1], db = b - leaders[c * 3 + 2];
                    float d = dL * dL + da * da + db * db;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = c;
                    }
                }
                result.ClusterOfRegion[r] = best;
            }
            result.Count = count;
            Array.Resize(ref leaders, count * 3);
            result.Leaders = leaders;
            return result;
        }
    }
}
