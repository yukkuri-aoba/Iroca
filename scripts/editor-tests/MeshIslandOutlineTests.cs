// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// プレビューに重ねる UV の島の輪郭(<see cref="MeshPartController.ComputeIslandOutline"/>)。
    /// 輪郭 = ほかの三角形と共有していない UV の辺。内側の対角線を描かないこと、
    /// UV を重ねて使う左右対称のパーツでも輪郭が消えないことを見る。
    /// </summary>
    public class MeshIslandOutlineTests
    {
        // (x0,y0)-(x1,y1) の四角を 2 つの三角形で。頂点は四角ごとに別(base から 4 個)
        private static void AddQuad(List<Vector2> uv, List<int> tris, float x0, float y0, float x1, float y1,
                                    bool flipWinding = false)
        {
            int b = uv.Count;
            uv.Add(new Vector2(x0, y0)); uv.Add(new Vector2(x1, y0)); uv.Add(new Vector2(x1, y1)); uv.Add(new Vector2(x0, y1));
            if (flipWinding) tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            else tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }

        private static UvChartSource Source(List<Vector2> uv, List<int> tris)
            => new UvChartSource { name = "test", uv = uv.ToArray(), triangles = tris.ToArray() };

        private static int SegmentCount(Vector2[] outline)
        {
            Assert.AreEqual(0, outline.Length % 2, "端点は 2 つで 1 本");
            return outline.Length / 2;
        }

        private static bool IsDiagonal(Vector2 a, Vector2 b)
            => !Mathf.Approximately(a.x, b.x) && !Mathf.Approximately(a.y, b.y);

        [Test]
        public void TwoSeparateQuadsGiveEightEdgesWithoutDiagonals()
        {
            var uv = new List<Vector2>(); var tris = new List<int>();
            AddQuad(uv, tris, 0.1f, 0.1f, 0.4f, 0.4f);
            AddQuad(uv, tris, 0.6f, 0.6f, 0.9f, 0.9f);

            var outline = MeshPartController.ComputeIslandOutline(new[] { Source(uv, tris) });
            Assert.AreEqual(8, SegmentCount(outline));
            for (int i = 0; i < outline.Length; i += 2)
                Assert.IsFalse(IsDiagonal(outline[i], outline[i + 1]), "四角の内側の対角線は輪郭ではない");
        }

        [Test]
        public void AdjacentQuadsShareTheirCommonEdge()
        {
            // UV 上でつながった 2 つの四角(頂点は別々でも UV が同じ辺は共有とみなす)= 1 つの島の輪郭 6 本
            var uv = new List<Vector2>(); var tris = new List<int>();
            AddQuad(uv, tris, 0.1f, 0.1f, 0.3f, 0.3f);
            AddQuad(uv, tris, 0.3f, 0.1f, 0.5f, 0.3f);

            var outline = MeshPartController.ComputeIslandOutline(new[] { Source(uv, tris) });
            Assert.AreEqual(6, SegmentCount(outline));
        }

        [Test]
        public void MirroredCopyWithSameUvKeepsItsOutline()
        {
            // 左右対称のパーツが同じ UV を使う(三角形の向きは反転)。重なった 2 枚で輪郭が消えてはいけない
            var uv = new List<Vector2>(); var tris = new List<int>();
            AddQuad(uv, tris, 0.2f, 0.2f, 0.6f, 0.6f);
            AddQuad(uv, tris, 0.2f, 0.2f, 0.6f, 0.6f, flipWinding: true);

            var outline = MeshPartController.ComputeIslandOutline(new[] { Source(uv, tris) });
            Assert.AreEqual(4, SegmentCount(outline));
        }

        [Test]
        public void TilingAndOffsetAreApplied()
        {
            var uv = new List<Vector2>(); var tris = new List<int>();
            AddQuad(uv, tris, 0f, 0f, 0.5f, 0.5f);
            var src = Source(uv, tris);
            src.scale = new Vector2(0.5f, 1f);
            src.offset = new Vector2(0.25f, 0f);

            var outline = MeshPartController.ComputeIslandOutline(new[] { src });
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (var p in outline) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); }
            Assert.AreEqual(0.25f, minX, 1e-6f);
            Assert.AreEqual(0.5f, maxX, 1e-6f);
        }

        [Test]
        public void NullOrBrokenSourcesAreSkipped()
        {
            var uv = new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up };
            var broken = new UvChartSource { name = "broken", uv = uv.ToArray(), triangles = new[] { 0, 1, 9 } };
            var outline = MeshPartController.ComputeIslandOutline(new[] { null, broken, new UvChartSource() });
            Assert.AreEqual(0, outline.Length);
        }
    }
}
