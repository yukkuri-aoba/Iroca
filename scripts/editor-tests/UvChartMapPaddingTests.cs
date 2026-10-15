// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// メッシュ右クリックの UV チャート地図は、にじみ代を割り当てたあと半径の外を落とした持ち主だけを持つ
    /// (<see cref="UvChartMap.AssignPaddingBaked"/>)。従来の「AssignPadding してから同じ半径で引く」と
    /// どの画素・どのマスクも同じになることを、ランダムな三角形で確かめる。
    /// AssignPadding 側(ハーネスが距離と持ち主を書き出す経路)のビット不変は refactor_abcheck の uvcharts が見る。
    /// </summary>
    public class UvChartMapPaddingTests
    {
        // 離れた三角形と、辺を共有する四角(2 三角形で 1 チャート)を混ぜる。UV は 0..1 の外へ少しはみ出させる。
        private static List<UvChartSource> RandomSources(int seed)
        {
            var rng = new System.Random(seed);
            float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
            var sources = new List<UvChartSource>();
            for (int s = 0; s < 3; s++)
            {
                var uv = new List<Vector2>();
                var tris = new List<int>();
                for (int k = 0; k < 6; k++)
                {
                    float cx = R(-0.05f, 1.05f), cy = R(-0.05f, 1.05f), r = R(0.01f, 0.12f);
                    int b = uv.Count;
                    if (k % 2 == 0)
                    {
                        uv.Add(new Vector2(cx - r, cy - r)); uv.Add(new Vector2(cx + r, cy - r));
                        uv.Add(new Vector2(cx + r, cy + r)); uv.Add(new Vector2(cx - r, cy + r));
                        tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                    }
                    else
                    {
                        uv.Add(new Vector2(cx, cy + r)); uv.Add(new Vector2(cx - r, cy - r)); uv.Add(new Vector2(cx + R(0f, r), cy - r));
                        tris.AddRange(new[] { b, b + 1, b + 2 });
                    }
                }
                sources.Add(new UvChartSource
                {
                    name = "s" + s,
                    uv = uv.ToArray(),
                    triangles = tris.ToArray(),
                    scale = s == 2 ? new Vector2(0.5f, 0.5f) : Vector2.one,
                    offset = s == 2 ? new Vector2(0.25f, 0.5f) : Vector2.zero,
                });
            }
            return sources;
        }

        [TestCase(200, 150, 1, -1)]   // -1 = 既定の半径
        [TestCase(200, 150, 2, 0)]
        [TestCase(97, 61, 3, 3)]
        [TestCase(256, 256, 4, 20)]
        public void Baked_MatchesAssignPaddingThenLookup(int w, int h, int seed, int radius)
        {
            var sources = RandomSources(seed);
            var full = UvChartMap.Build(w, h, sources);
            full.AssignPadding();
            var baked = UvChartMap.Build(w, h, sources);
            if (radius < 0) radius = baked.DefaultPaddingRadius;
            baked.AssignPaddingBaked(radius);

            Assert.Greater(full.ChartCount, 0);
            int owned = 0;
            for (int y = -1; y <= h; y++)
                for (int x = -1; x <= w; x++)
                {
                    int want = full.ChartAt(x, y, radius);
                    Assert.AreEqual(want, baked.ChartAt(x, y, radius), $"({x}, {y})");
                    if (want >= 0) owned++;
                }
            Assert.Greater(owned, 0);

            System.Func<int, bool>[] selects = { c => c == 0, c => c % 2 == 1, c => true };
            foreach (var sel in selects)
                CollectionAssert.AreEqual(full.MaskOf(sel, radius), baked.MaskOf(sel, radius));
            CollectionAssert.AreEqual(full.ExcludeOtherSources(new[] { 1 }, radius),
                                      baked.ExcludeOtherSources(new[] { 1 }, radius));
        }

        [Test]
        public void Baked_DropsArraysAndRejectsOtherRadius()
        {
            var map = UvChartMap.Build(64, 64, RandomSources(5));
            map.AssignPaddingBaked(4);
            Assert.IsNull(map.Raster);
            Assert.IsNull(map.OwnerDistance);
            Assert.Throws<System.InvalidOperationException>(() => map.ChartAt(1, 1, 5));
            Assert.Throws<System.InvalidOperationException>(() => map.MaskOf(c => true, 3));
            Assert.Throws<System.InvalidOperationException>(() => map.AssignPadding());
        }

        [TestCase(0)]
        [TestCase(4)]
        public void Unpadded_LooksUpRasterOnly(int radius)
        {
            var map = UvChartMap.Build(120, 90, RandomSources(6));
            var raster = map.Raster;
            for (int y = 0; y < 90; y++)
                for (int x = 0; x < 120; x++)
                    Assert.AreEqual(raster[y * 120 + x], map.ChartAt(x, y, radius), $"({x}, {y})");
            var dist = map.OwnerDistance;
            for (int i = 0; i < raster.Length; i++)
                Assert.AreEqual(raster[i] >= 0 ? 0 : int.MaxValue, dist[i]);
        }
    }
}
