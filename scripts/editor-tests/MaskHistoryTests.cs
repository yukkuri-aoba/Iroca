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

        // ─── PaintedBy: その画素を塗った範囲の操作が塗った画素(右クリックの「ここのマスクを消す」) ───

        private long CommitRegion(long basis) => _history.Capture(_masks, basis, regionOp: true);

        private List<int> Painted(MaskHistory history, long revision, string zoneId, bool include, int pixel) {
            var m = history.PaintedBy(revision, zoneId, include, pixel);
            if (m == null) {
                return null;
            }
            try { return Pixels(m); } finally { m.Release(); }
        }

        [Test]
        public void PaintedBy_ReturnsWhatTheRegionOperationTurnedOn() {
            long v0 = Commit(0);
            _masks.WritableLayer(false, "z").Set(4, true);
            long v1 = CommitRegion(v0);
            _masks.WritableLayer(false, "z").Set(5, true);
            _masks.WritableLayer(false, "z").Set(900, true);   // 同じ操作で塗った、離れた所
            _masks.WritableLayer(true, "z").Set(6, true);      // 同じ操作が別の層に塗った画素は入らない
            long v2 = CommitRegion(v1);
            _masks.WritableLayer(false, "z").Set(7, true);
            long v3 = Commit(v2);                              // ブラシ(範囲の操作でない)

            CollectionAssert.AreEqual(new[] { 5, 900 }, Painted(_history, v3, "z", false, 5), "2 回目の操作が立てた画素だけ");
            CollectionAssert.AreEqual(new[] { 5, 900 }, Painted(_history, v3, "z", false, 900));
            CollectionAssert.AreEqual(new[] { 4 }, Painted(_history, v3, "z", false, 4), "1 回目の操作");
            CollectionAssert.AreEqual(new[] { 6 }, Painted(_history, v3, "z", true, 6), "含めるの層");
            Assert.IsNull(Painted(_history, v3, "z", false, 7), "ブラシで塗った画素は分からない扱い");
            Assert.IsNull(Painted(_history, v3, "z", false, 8), "立っていない画素");
            Assert.IsNull(Painted(_history, v3, null, false, 4), "層が無い");
            Assert.IsNull(Painted(_history, v3, "z", false, -1), "範囲の外");
            Assert.IsNull(Painted(_history, v3, "z", false, W * H), "範囲の外");
            Assert.IsNull(Painted(_history, v3 + 1, "z", false, 4), "持っていない版");
            CollectionAssert.AreEqual(new[] { 4 }, Painted(_history, v1, "z", false, 4), "Undo で戻った先の版から見ても同じ");
            Assert.IsNull(Painted(_history, v1, "z", false, 5), "いまの版より後で塗った画素は見ない");
        }

        [Test]
        public void PaintedBy_TheFirstMaskOfAll_WhenThePreviousVersionHadNoSize() {
            // マスクがまだ無いとき(寸法 0)の版の次に、最初の範囲の操作が来る(ウィンドウを開いて最初の右クリック)。
            var empty = new MaskWorkspace();
            long v0 = _history.Capture(empty, 0);
            _masks.WritableLayer(false, "z").Set(4, true);
            long v1 = CommitRegion(v0);

            CollectionAssert.AreEqual(new[] { 4 }, Painted(_history, v1, "z", false, 4));
        }

        [Test]
        public void PaintedBy_PixelsThatWereThereBeforeTheHistory_AreUnknown() {
            _masks.WritableLayer(false, "z").Set(4, true);
            long v0 = Commit(0);                               // 開いたときからあったマスク
            _masks.WritableLayer(false, "z").Set(5, true);
            long v1 = CommitRegion(v0);

            Assert.IsNull(Painted(_history, v1, "z", false, 4), "履歴より前に塗った画素");
            Assert.IsNull(Painted(_history, v0, "z", false, 4));
            CollectionAssert.AreEqual(new[] { 5 }, Painted(_history, v1, "z", false, 5));
        }

        [Test]
        public void PaintedBy_APixelClearedAndPaintedAgain_BelongsToTheLaterOperation() {
            long v0 = Commit(0);
            _masks.WritableLayer(false, "z").Set(4, true);
            _masks.WritableLayer(false, "z").Set(5, true);
            long v1 = CommitRegion(v0);
            _masks.WritableLayer(false, "z").Set(4, false);
            long v2 = Commit(v1);
            _masks.WritableLayer(false, "z").Set(4, true);
            _masks.WritableLayer(false, "z").Set(6, true);
            long v3 = CommitRegion(v2);

            CollectionAssert.AreEqual(new[] { 4, 6 }, Painted(_history, v3, "z", false, 4), "塗り直した操作");
            CollectionAssert.AreEqual(new[] { 4, 5 }, Painted(_history, v3, "z", false, 5), "最初の操作(いまは消えた画素も含む)");
        }

        [Test]
        public void PaintedBy_TheCommonMask_AndUnchangedVersionsInBetween() {
            long v0 = Commit(0);
            _masks.WritableCommon().Set(3, true);
            long v1 = CommitRegion(v0);
            _masks.WritableLayer(false, "z").Set(9, true);     // 共通の層は変わらない版(同じ塊を共有する)
            long v2 = Commit(v1);
            long v3 = CommitRegion(v2);

            CollectionAssert.AreEqual(new[] { 3 }, Painted(_history, v3, null, false, 3));
        }

        [Test]
        public void PaintedBy_SurvivesTheSpill() {
            string path = SpillFile();
            try {
                long v0 = Commit(0);
                _masks.WritableLayer(false, "z").Set(4, true);
                long v1 = CommitRegion(v0);
                _masks.WritableLayer(false, "z").Set(5, true);
                long v2 = Commit(v1);
                _history.SaveTo(path);

                var loaded = new MaskHistory();
                try {
                    Assert.IsTrue(loaded.LoadFrom(path));
                    CollectionAssert.AreEqual(new[] { 4 }, Painted(loaded, v2, "z", false, 4), "範囲の操作の印も読み戻す");
                    Assert.IsNull(Painted(loaded, v2, "z", false, 5), "ブラシの版はブラシのまま");
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
