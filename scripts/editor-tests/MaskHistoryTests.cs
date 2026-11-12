// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Iroca.EditorTests {
    /// <summary>
    /// 編集中のマスクの履歴(<see cref="MaskHistory"/>)。版へ戻すと同じ中身になること、変わらない層は共有して量が増えないこと、
    /// やり直しの分を手放すこと、上限で古い版を手放すこと、ファイルへ退避して読み戻せること(壊れたファイルは読まない)、
    /// 持っているものを全部返すこと。
    /// </summary>
    public class MaskHistoryTests {
        private const int W = 40, H = 30;

        private MaskWorkspace _masks;
        private MaskHistory _history;
        private long _liveBefore;

        [SetUp]
        public void SetUp() {
            _liveBefore = IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask);
            _masks = new MaskWorkspace();
            _masks.Reset(W, H);
            _history = new MaskHistory();
        }

        [TearDown]
        public void TearDown() {
            _masks.Clear();
            _history.Clear();
            Assert.AreEqual(0, _history.Bytes, "手放した量と数えた量が合わない");
            Assert.AreEqual(_liveBefore, IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask), "マスクのメモリが残っている");
        }

        private static List<int> Pixels(NativeBitMask m) {
            var got = new List<int>();
            if (m == null) {
                return got;
            }
            for (int i = 0; i < m.Length; i++) {
                if (m.Get(i)) {
                    got.Add(i);
                }
            }
            return got;
        }

        private long Commit(long basis) => _history.Capture(_masks, basis);

        [Test]
        public void Restore_BringsBackEveryLayer() {
            long v0 = Commit(0);
            _masks.WritableCommon().Set(3, true);
            _masks.WritableLayer(false, "z").Set(4, true);
            _masks.WritableLayer(true, "z").Set(5, true);
            long v1 = Commit(v0);
            _masks.WritableCommon().Set(6, true);
            _masks.RemoveZone("z");
            long v2 = Commit(v1);

            Assert.IsTrue(_history.TryRestore(v1, _masks));
            CollectionAssert.AreEqual(new[] { 3 }, Pixels(_masks.Common));
            CollectionAssert.AreEqual(new[] { 4 }, Pixels(_masks.Layer(false, "z")));
            CollectionAssert.AreEqual(new[] { 5 }, Pixels(_masks.Layer(true, "z")));

            Assert.IsTrue(_history.TryRestore(v0, _masks));
            Assert.IsNull(_masks.Common, "何も無かった版では層も無い");
            Assert.IsFalse(_masks.HasLayers);

            Assert.IsTrue(_history.TryRestore(v2, _masks));
            CollectionAssert.AreEqual(new[] { 3, 6 }, Pixels(_masks.Common));
            Assert.IsNull(_masks.Layer(false, "z"));
        }

        [Test]
        public void Restore_OfDifferentSize_SetsTheSize() {
            _masks.WritableCommon().Set(1, true);
            long v0 = Commit(0);
            _masks.Reset(8, 8);
            _masks.WritableCommon().Set(2, true);
            long v1 = Commit(v0);

            Assert.IsTrue(_history.TryRestore(v0, _masks));
            Assert.AreEqual((W, H), (_masks.Width, _masks.Height));
            CollectionAssert.AreEqual(new[] { 1 }, Pixels(_masks.Common));
            Assert.IsTrue(_history.TryRestore(v1, _masks));
            Assert.AreEqual((8, 8), (_masks.Width, _masks.Height));
        }

        [Test]
        public void Restore_UnknownRevision_LeavesMasksAlone() {
            _masks.WritableCommon().Set(1, true);
            Commit(0);
            Assert.IsFalse(_history.TryRestore(12345, _masks));
            CollectionAssert.AreEqual(new[] { 1 }, Pixels(_masks.Common));
        }

        [Test]
        public void Restore_KeepsLayersWhoseContentIsTheSame() {
            _masks.WritableCommon().Set(1, true);
            _masks.WritableLayer(false, "z").Set(2, true);
            long v0 = Commit(0);
            var sameLayer = _masks.Layer(false, "z");
            _masks.WritableCommon().Set(7, true);
            Commit(v0);

            Assert.IsTrue(_history.TryRestore(v0, _masks));
            Assert.AreSame(sameLayer, _masks.Layer(false, "z"), "変わっていない層は展開し直さない");
            Assert.AreEqual(1, sameLayer.RefCount);
            CollectionAssert.AreEqual(new[] { 1 }, Pixels(_masks.Common));
        }

        [Test]
        public void Capture_SharesUnchangedLayers() {
            _masks.WritableCommon().Set(1, true);
            _masks.WritableLayer(false, "z").Set(2, true);
            long v = Commit(0);
            long oneVersion = _history.Bytes;
            Assert.Greater(oneVersion, 0);

            // 中身の同じ版を重ねても量は増えない。
            v = Commit(v);
            Assert.AreEqual(oneVersion, _history.Bytes);
            Assert.AreEqual(2, _history.Count);

            // 1 層だけ変えたら、増えるのはその層の分だけ。
            _masks.WritableCommon().Set(9, true);
            Commit(v);
            Assert.Greater(_history.Bytes, oneVersion);
            Assert.Less(_history.Bytes, oneVersion * 2);
        }

        [Test]
        public void Capture_SkipsEmptyLayers() {
            _masks.WritableLayer(false, "empty");
            long v0 = Commit(0);
            Assert.AreEqual(0, _history.Bytes, "全部 0 の層は載せない(保存形式と同じ)");
            _masks.WritableCommon().Set(1, true);
            Commit(v0);
            Assert.IsTrue(_history.TryRestore(v0, _masks));
            Assert.IsNull(_masks.Layer(false, "empty"), "戻すと全部 0 の層は無くなる(保存形式から戻すときと同じ)");
        }

        [Test]
        public void Capture_DropsTheRedoBranch() {
            long v0 = Commit(0);
            _masks.WritableCommon().Set(1, true);
            long v1 = Commit(v0);
            _masks.WritableCommon().Set(2, true);
            long v2 = Commit(v1);

            // v1 まで戻してから新しく編集すると、Unity のやり直しの分は消えるので v2 も手放す。
            Assert.IsTrue(_history.TryRestore(v1, _masks));
            _masks.WritableCommon().Set(3, true);
            long v3 = Commit(v1);

            Assert.IsFalse(_history.Contains(v2));
            Assert.IsTrue(_history.Contains(v1));
            Assert.IsTrue(_history.Contains(v3));
            Assert.Greater(v3, v2, "番号は使い回さない");
        }

        [Test]
        public void Capture_RevisionsNeverRepeat() {
            long v = Commit(0);
            for (int i = 0; i < 100; i++) {
                long next = Commit(v);
                Assert.Greater(next, v);
                v = next;
            }
        }

        // ─── 範囲の操作が塗った画素の記録(MaskWorkspace.Patches)も版に載る ───

        private unsafe void Region(string zoneId, MaskRegionOp op, params int[] on) {
            var region = new bool[W * H];
            foreach (int i in on) {
                region[i] = true;
            }
            fixed (bool* p = region) {
                Assert.IsTrue(_masks.ApplyRegion(p, zoneId, op, null, out _));
            }
        }

        private static List<List<int>> PatchPixels(MaskWorkspace masks) {
            var all = new List<List<int>>();
            foreach (var p in masks.Patches) {
                var m = p.Decode();
                try { all.Add(Pixels(m)); } finally { m.Release(); }
            }
            return all;
        }

        [Test]
        public void Restore_BringsBackThePatchesOfThatVersion() {
            long v0 = Commit(0);
            Region("z", MaskRegionOp.DontPaintHere, 4, 900);
            long v1 = Commit(v0);
            Region("z", MaskRegionOp.DontPaintHere, 5);
            Region(null, MaskRegionOp.DontPaintHere, 7);
            long v2 = Commit(v1);

            Assert.IsTrue(_history.TryRestore(v1, _masks));
            Assert.AreEqual(1, _masks.Patches.Count, "その版の時点の記録だけ");
            CollectionAssert.AreEqual(new[] { 4, 900 }, PatchPixels(_masks)[0]);
            Assert.IsTrue(_masks.ClearMaskAt("z", 4, 0, 0, null, out var r));
            Assert.AreEqual(2, r.changed, "戻した版でも、入れたときの範囲で消える");

            Assert.IsTrue(_history.TryRestore(v2, _masks));
            Assert.AreEqual(3, _masks.Patches.Count, "消す前の版の記録が戻る(履歴は自分の写しを持つ)");
            Assert.IsNull(_masks.Patches[2].ZoneId);

            Assert.IsTrue(_history.TryRestore(v0, _masks));
            Assert.AreEqual(0, _masks.Patches.Count, "マスクが無かった版には記録も無い");
        }

        [Test]
        public void Capture_SharesUnchangedPatches() {
            long v0 = Commit(0);
            Region("z", MaskRegionOp.DontPaintHere, 4, 900);
            long v1 = Commit(v0);
            long bytes = _history.Bytes;
            _masks.WritableCommon().Set(3, true);          // 記録は変わらない
            long v2 = Commit(v1);
            _masks.WritableCommon().Set(6, true);
            Commit(v2);
            // 増えるのは共通の層の RLE の分だけ(1 画素なら 9 + 4 × 3 バイト、2 画素なら 9 + 4 × 5 バイト)。記録の写しは増えない。
            Assert.AreEqual(bytes + 21 + 29, _history.Bytes, "中身が同じ記録は前の版と共有する");
        }

        [Test]
        public void Spill_KeepsThePatches() {
            string path = SpillFile();
            try {
                long v0 = Commit(0);
                Region("z", MaskRegionOp.DontPaintHere, 4, 900);
                long v1 = Commit(v0);
                Region("z", MaskRegionOp.PaintHere, 5);
                long v2 = Commit(v1);
                long bytes = _history.Bytes;
                _history.SaveTo(path);

                var loaded = new MaskHistory();
                try {
                    Assert.IsTrue(loaded.LoadFrom(path));
                    Assert.AreEqual(bytes, loaded.Bytes, "共有していた塊は共有のまま");
                    var restored = new MaskWorkspace();
                    try {
                        Assert.IsTrue(loaded.TryRestore(v2, restored));
                        Assert.AreEqual(2, restored.Patches.Count);
                        Assert.IsTrue(restored.Patches[1].Include);
                        CollectionAssert.AreEqual(new[] { 4, 900 }, PatchPixels(restored)[0]);
                        Assert.IsTrue(loaded.TryRestore(v1, restored));
                        Assert.AreEqual(1, restored.Patches.Count);
                    } finally { restored.Clear(); }
                } finally { loaded.Clear(); }
            } finally { File.Delete(path); }
        }

        private string SpillFile() => Path.Combine(Path.GetTempPath(), "iroca-mask-history-" + Guid.NewGuid().ToString("N") + ".bin");

        [Test]
        public void Spill_RoundTripsEveryVersionAndKeepsSharing() {
            string path = SpillFile();
            try {
                long v0 = Commit(0);
                _masks.WritableCommon().Set(3, true);
                _masks.WritableLayer(false, "z").Set(4, true);
                long v1 = Commit(v0);
                _masks.WritableLayer(true, "z").Set(5, true);
                long v2 = Commit(v1);
                long bytes = _history.Bytes;
                _history.SaveTo(path);

                var loaded = new MaskHistory();
                try {
                    Assert.IsTrue(loaded.LoadFrom(path));
                    Assert.AreEqual(3, loaded.Count);
                    Assert.AreEqual(bytes, loaded.Bytes, "共有していた塊は共有のまま");

                    var restored = new MaskWorkspace();
                    try {
                        Assert.IsTrue(loaded.TryRestore(v1, restored));
                        Assert.AreEqual((W, H), (restored.Width, restored.Height));
                        CollectionAssert.AreEqual(new[] { 3 }, Pixels(restored.Common));
                        CollectionAssert.AreEqual(new[] { 4 }, Pixels(restored.Layer(false, "z")));
                        Assert.IsNull(restored.Layer(true, "z"));
                        Assert.IsTrue(loaded.TryRestore(v2, restored));
                        CollectionAssert.AreEqual(new[] { 5 }, Pixels(restored.Layer(true, "z")));
                        Assert.IsTrue(loaded.TryRestore(v0, restored));
                        Assert.IsFalse(restored.HasLayers);
                    } finally { restored.Clear(); }

                    Assert.Greater(loaded.Capture(_masks, v2), v2, "読み戻した後の番号も前と重ならない");
                } finally { loaded.Clear(); }
            } finally { File.Delete(path); }
        }

        [Test]
        public void Spill_EmptyHistoryRoundTrips() {
            string path = SpillFile();
            try {
                _history.SaveTo(path);
                var loaded = new MaskHistory();
                Assert.IsTrue(loaded.LoadFrom(path));
                Assert.AreEqual(0, loaded.Count);
            } finally { File.Delete(path); }
        }

        [Test]
        public void Spill_MissingFileLeavesItEmpty() {
            Commit(0);
            Assert.IsFalse(_history.LoadFrom(SpillFile()));
            Assert.AreEqual(0, _history.Count);
            Assert.AreEqual(0, _history.Bytes);
        }

        [Test]
        public void Spill_BrokenFilesAreNotRead() {
            string path = SpillFile();
            try {
                _masks.WritableCommon().Set(3, true);
                _masks.WritableLayer(false, "z").Set(4, true);
                long v0 = Commit(0);
                _masks.WritableCommon().Set(5, true);
                Commit(v0);
                _history.SaveTo(path);
                byte[] good = File.ReadAllBytes(path);

                void AssertUnreadable(byte[] bytes, string why) {
                    File.WriteAllBytes(path, bytes);
                    var h = new MaskHistory();
                    Assert.IsFalse(h.LoadFrom(path), why);
                    Assert.AreEqual(0, h.Count, why);
                    Assert.AreEqual(0, h.Bytes, why);
                }

                // 途中で切れたファイル(書いている途中で落ちた)。どこで切れても読まない。
                for (int len = 0; len < good.Length; len += Math.Max(1, good.Length / 40)) {
                    var cut = new byte[len];
                    Array.Copy(good, cut, len);
                    AssertUnreadable(cut, $"{len} バイトで切れた");
                }

                var badMagic = (byte[])good.Clone();
                badMagic[0] ^= 0xFF;
                AssertUnreadable(badMagic, "見出しが違う");

                var badFormat = (byte[])good.Clone();
                badFormat[4] ^= 0xFF;
                AssertUnreadable(badFormat, "形の版が違う");

                // 最初の塊のラン列を壊す(見出し 4+4+8+4 の次に塊の長さ 8、その次が RLE。ヘッダ 9 バイトの次の最初のラン)。
                // 合計が全画素にならないラン列を展開すると範囲の外へ書くので、読む前に止める。
                var badRun = (byte[])good.Clone();
                badRun[20 + 8 + 9] ^= 0x7F;
                AssertUnreadable(badRun, "ランの合計が合わない");

                var badSize = (byte[])good.Clone();
                badSize[20 + 8] ^= 0x7F;
                AssertUnreadable(badSize, "寸法が合わない");
            } finally { File.Delete(path); }
        }

        [Test]
        public void OverTheLimit_DropsOldestVersionsButKeepsCurrentAndPrevious() {
            var small = new MaskHistory(maxBytes: 1);
            try {
                _masks.WritableCommon().Set(1, true);
                long v0 = small.Capture(_masks, 0);
                _masks.WritableCommon().Set(2, true);
                long v1 = small.Capture(_masks, v0);
                _masks.WritableCommon().Set(3, true);
                long v2 = small.Capture(_masks, v1);

                Assert.IsFalse(small.Contains(v0), "古い版から手放す");
                Assert.IsTrue(small.Contains(v1), "1 つ前は残す");
                Assert.IsTrue(small.Contains(v2));
                Assert.IsFalse(small.TryRestore(v0, _masks));
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, Pixels(_masks.Common), "戻せなければマスクは変えない");
            } finally { small.Clear(); }
        }
    }
}
