// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 編集中のマスクの履歴(<see cref="MaskHistory"/>)。版へ戻すと同じ中身になること、変わらない層は共有して量が増えないこと、
    /// やり直しの分を手放すこと、上限で古い版を手放すこと、持っているものを全部返すこと。
    /// </summary>
    public class MaskHistoryTests
    {
        private const int W = 40, H = 30;

        private MaskWorkspace _masks;
        private MaskHistory _history;
        private long _liveBefore;

        [SetUp]
        public void SetUp()
        {
            _liveBefore = IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask);
            _masks = new MaskWorkspace();
            _masks.Reset(W, H);
            _history = new MaskHistory();
        }

        [TearDown]
        public void TearDown()
        {
            _masks.Clear();
            _history.Clear();
            Assert.AreEqual(0, _history.Bytes, "手放した量と数えた量が合わない");
            Assert.AreEqual(_liveBefore, IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask), "マスクのメモリが残っている");
        }

        private static List<int> Pixels(NativeBitMask m)
        {
            var got = new List<int>();
            if (m == null) return got;
            for (int i = 0; i < m.Length; i++) if (m.Get(i)) got.Add(i);
            return got;
        }

        private long Commit(long basis) => _history.Capture(_masks, basis);

        [Test]
        public void Restore_BringsBackEveryLayer()
        {
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
        public void Restore_OfDifferentSize_SetsTheSize()
        {
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
        public void Restore_UnknownRevision_LeavesMasksAlone()
        {
            _masks.WritableCommon().Set(1, true);
            Commit(0);
            Assert.IsFalse(_history.TryRestore(12345, _masks));
            CollectionAssert.AreEqual(new[] { 1 }, Pixels(_masks.Common));
        }

        [Test]
        public void Restore_KeepsLayersWhoseContentIsTheSame()
        {
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
        public void Capture_SharesUnchangedLayers()
        {
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
        public void Capture_SkipsEmptyLayers()
        {
            _masks.WritableLayer(false, "empty");
            long v0 = Commit(0);
            Assert.AreEqual(0, _history.Bytes, "全部 0 の層は載せない(保存形式と同じ)");
            _masks.WritableCommon().Set(1, true);
            Commit(v0);
            Assert.IsTrue(_history.TryRestore(v0, _masks));
            Assert.IsNull(_masks.Layer(false, "empty"), "戻すと全部 0 の層は無くなる(保存形式から戻すときと同じ)");
        }

        [Test]
        public void Capture_DropsTheRedoBranch()
        {
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
        public void Capture_RevisionsNeverRepeat()
        {
            long v = Commit(0);
            for (int i = 0; i < 100; i++)
            {
                long next = Commit(v);
                Assert.Greater(next, v);
                v = next;
            }
        }

        [Test]
        public void OverTheLimit_DropsOldestVersionsButKeepsCurrentAndPrevious()
        {
            var small = new MaskHistory(maxBytes: 1);
            try
            {
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
            }
            finally { small.Clear(); }
        }
    }
}
