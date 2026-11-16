// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 影の色をマテリアルごとに合わせるときの、ゾーンの選び方(<see cref="ZoneShareGrid.Choose"/>)と、ゾーンの色の表
    /// (<see cref="ZoneColors"/>)。マテリアルが映す所を一番多く塗ったゾーンを選び、色替えしていない所の方が多ければ選ばない。
    /// </summary>
    public class ZoneShareGridTests {
        // 4 点の格子(4×1)。
        private static ZoneShareGrid Grid(float[][] strengths, bool[] counted = null, params string[] ids)
            => ZoneShareGrid.FromStrengths(4, 1, 4, 1, ids, strengths, counted ?? new[] { true, true, true, true });

        [Test]
        public void Choose_TheZonePaintingTheMostOfTheArea() {
            var grid = Grid(new[] { new[] { 1f, 1f, 1f, 0f }, new[] { 0f, 0f, 0f, 1f } }, null, "a", "b");
            Assert.AreEqual("a", grid.Choose(null), "全体では a が多い");
            Assert.AreEqual("b", grid.Choose(new[] { false, false, false, true }), "右端だけを映すマテリアルは b");
            Assert.AreEqual("a", grid.Choose(new bool[4]), "映す所が分からなければ全体で選ぶ");
        }

        [Test]
        public void Choose_None_WhenMostOfTheAreaIsNotRecolored() {
            var grid = Grid(new[] { new[] { 1f, 0f, 0f, 0f } }, null, "a");
            Assert.IsNull(grid.Choose(null), "色替えしていない所の方が多いのに選んだ");
            Assert.IsNull(Grid(new[] { new[] { 1f, 1f, 0f, 0f } }, null, "a").Choose(null), "同じなら変えない");
            Assert.AreEqual("a", grid.Choose(new[] { true, false, false, false }), "映す所を塗っていれば選ぶ");
        }

        [Test]
        public void Choose_SkipsTransparentPoints() {
            var grid = Grid(new[] { new[] { 1f, 0f, 0f, 0f } }, new[] { true, false, false, false }, "a");
            Assert.AreEqual("a", grid.Choose(null), "透明な点を色替えしていない所として数えた");
            Assert.IsNull(grid.Choose(new[] { false, true, true, true }), "不透明な点の無い所で選んだ");
        }

        [Test]
        public void Choose_UpperZoneTakesFirst() {
            // 上のゾーンが 0.6 取ると、下のゾーンは残りの 0.4 しか取れない(合成と同じ)。
            var grid = Grid(new[] { new[] { 0.6f, 0.6f, 0.6f, 0.6f }, new[] { 1f, 1f, 1f, 1f } }, null, "upper", "lower");
            Assert.AreEqual("upper", grid.Choose(null));
            // 選択を控えていないゾーン(null)は、どこも塗らない。
            var missing = Grid(new[] { null, new[] { 1f, 1f, 1f, 1f } }, null, "missing", "b");
            Assert.AreEqual("b", missing.Choose(null));
        }

        [Test]
        public void WriteRead_RoundTrips() {
            var grid = Grid(new[] { new[] { 1f, 1f, 0f, 0f }, new[] { 0f, 0f, 1f, 0f } }, new[] { true, true, true, false }, "a", "b");
            ZoneShareGrid read;
            using (var stream = new MemoryStream()) {
                grid.Write(stream);
                stream.Position = 0;
                read = ZoneShareGrid.Read(stream);
            }
            Assert.IsNotNull(read);
            Assert.AreEqual(grid.GridW, read.GridW);
            Assert.AreEqual(grid.ZoneCount, read.ZoneCount);
            Assert.AreEqual("a", read.Choose(null));
            Assert.AreEqual("b", read.Choose(new[] { false, false, true, true }), "透明な点の印が戻っていない");
            using (var broken = new MemoryStream(new byte[] { 1, 2, 3 })) {
                Assert.IsNull(ZoneShareGrid.Read(broken), "壊れた中身を読んだ");
            }
        }

        [Test]
        public void GridSize_KeepsTheAspectAndTheLimit() {
            ZoneShareGrid.GridSize(4096, 2048, out int w, out int h);
            Assert.LessOrEqual(w * h, ZoneShareGrid.TargetCells);
            Assert.AreEqual(2f, w / (float)h, 0.02f);
            ZoneShareGrid.GridSize(16, 8, out w, out h);
            Assert.AreEqual((16, 8), (w, h), "小さい画像を細かくした");
            var samples = ZoneShareGrid.SampleIndices(16, 8, 4, 2);
            Assert.AreEqual(8, samples.Length);
            Assert.AreEqual(2 * 16 + 2, samples[0], "点の中心の画素");
        }

        [Test]
        public void ZoneColors_OnlyEnabledZonesWithColors() {
            var used = Zone(true, true);
            var disabled = Zone(false, true);
            var noSample = Zone(true, false);
            var zones = new List<ColorZone> { used, disabled, noSample };
            var colors = ZoneColors.From(zones);
            Assert.IsTrue(colors.TryGet(used.id, out var sample, out var target));
            Assert.AreEqual(used.sampleColor, sample);
            Assert.AreEqual(used.targetColor, target);
            Assert.IsFalse(colors.TryGet(disabled.id, out _, out _), "無効なゾーン");
            Assert.IsFalse(colors.TryGet(noSample.id, out _, out _), "サンプル色が無いゾーン");
            Assert.IsFalse(colors.TryGet(null, out _, out _));

            Assert.IsTrue(colors.SameAs(zones));
            used.targetColor = Color.yellow;
            Assert.IsFalse(colors.SameAs(zones), "色を変えたのに同じとした");
            Assert.IsTrue(ZoneColors.Empty.SameAs(null));
        }

        private static ColorZone Zone(bool enabled, bool sampleSet) {
            var z = new ColorZone {
                enabled = enabled,
                sampleColor = new Color(0.2f, 0.4f, 0.8f),
                sampleColorSet = sampleSet,
                targetColor = new Color(0.1f, 0.7f, 0.2f),
            };
            z.EnsureId();
            return z;
        }
    }
}
