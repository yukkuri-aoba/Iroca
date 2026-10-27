// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 手を止めたときの作業配列の縮小(<see cref="NativeMemoryLifecycle"/>。0.4.0 の計画の 2-2)。計算が全部終わってから
    /// しばらく次の計算が無ければ、作業配列のプールの空きを OS へ返す。その間に計算が始まれば返さない。
    /// 待つ時間はテストの間だけ短くする。
    /// </summary>
    public class NativeMemoryLifecycleTests
    {
        private int _savedIdleMs;

        [SetUp]
        public void SetUp()
        {
            _savedIdleMs = NativeMemoryLifecycle.IdleTrimMilliseconds;
            NativeMemoryLifecycle.IdleTrimMilliseconds = 500;
            PixelProcessor.ReleaseWorkBuffers();
            Assume.That(NativeMemoryLifecycle.RunningWork, Is.EqualTo(0), "ほかの計算が走っている");
        }

        [TearDown]
        public void TearDown()
        {
            NativeMemoryLifecycle.IdleTrimMilliseconds = _savedIdleMs;
            PixelProcessor.ReleaseWorkBuffers();
        }

        [Test]
        public void IdleTrim_ReleasesFreeWorkArraysAfterTheLastWorkEnds()
        {
            RunWorkThatLeavesAFreeBlock();
            Assert.Greater(WorkPools.Float.FreeBytes, 0, "テストの前提: 空きが残っている");
            Assert.IsTrue(WaitUntil(() => WorkPools.Float.FreeBytes == 0, 5000), "手を止めても空きが返らない");
        }

        [Test]
        public void IdleTrim_IsCancelledByNewWork()
        {
            RunWorkThatLeavesAFreeBlock();
            NativeMemoryLifecycle.WorkStarted();   // 待っている間に次の計算が始まる
            try
            {
                Thread.Sleep(NativeMemoryLifecycle.IdleTrimMilliseconds * 3);
                Assert.Greater(WorkPools.Float.FreeBytes, 0, "計算中なのに空きを返した");
            }
            finally
            {
                NativeMemoryLifecycle.WorkEnded();
            }
            Assert.IsTrue(WaitUntil(() => WorkPools.Float.FreeBytes == 0, 5000), "計算が終わった後も空きが返らない");
        }

        // 作業配列を 1 本借りて返す計算(終わるとプールに空きが 1 本残る)。
        private static unsafe void RunWorkThatLeavesAFreeBlock()
        {
            NativeMemoryLifecycle.WorkStarted();
            try
            {
                float* p = WorkPools.Float.Rent(1 << 16);
                WorkPools.Float.Return(p);
            }
            finally
            {
                NativeMemoryLifecycle.WorkEnded();
            }
        }

        private static bool WaitUntil(System.Func<bool> done, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (!done())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(20);
            }
            return true;
        }
    }
}
