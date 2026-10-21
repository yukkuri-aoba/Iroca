// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Iroca.EditorTests
{
    /// <summary>
    /// <see cref="PreviewJob{T}"/> は、キャンセルした前の計算が抜け終わってから次の計算を始める
    /// (同じジョブの計算が重なると、作業配列のプールがもう 1 組を借りてずっと持ち続けるため)。
    /// 重ならないこと・最後の結果だけが適用されること・途中でキャンセルされた計算は始めないこと・
    /// 前の計算が例外で終わっても次が動くことを確かめる。
    /// </summary>
    public class PreviewJobTests
    {
        private static void PumpUntil(Func<bool> done, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (!done())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("ジョブが終わらない");
                PreviewJobMainThread.Drain();
                Thread.Sleep(2);
            }
            PreviewJobMainThread.Drain();
        }

        // キャンセルされるまで待ち、気づいてからも少しのあいだ動き続ける(作業配列を返すまでの後始末に相当)。
        private static int BlockUntilCancelled(CancellationToken token, ref int running, ref int maxRunning, ManualResetEventSlim started)
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            started?.Set();
            try
            {
                token.WaitHandle.WaitOne(5000);
                Thread.Sleep(50);
                token.ThrowIfCancellationRequested();
                return -1;
            }
            finally { Interlocked.Decrement(ref running); }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int cur;
            while ((cur = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
        }

        [Test]
        public void NextWorkStartsAfterCancelledWorkUnwinds()
        {
            using (var job = new PreviewJob<int>())
            using (var started = new ManualResetEventSlim())
            {
                int running = 0, maxRunning = 0, applied = 0;
                job.Schedule(t => BlockUntilCancelled(t, ref running, ref maxRunning, started), r => applied = r);
                Assert.IsTrue(started.Wait(5000), "1 本目が始まらない");

                job.Schedule(t =>
                {
                    int now = Interlocked.Increment(ref running);
                    InterlockedMax(ref maxRunning, now);
                    Interlocked.Decrement(ref running);
                    return 2;
                }, r => applied = r);

                PumpUntil(() => Volatile.Read(ref applied) != 0);
                Assert.AreEqual(2, applied, "最後に投げた計算の結果だけが適用される");
                Assert.AreEqual(1, maxRunning, "キャンセルした計算と次の計算が同時に走った");
                Assert.IsFalse(job.IsRunning);
            }
        }

        [Test]
        public void WorkCancelledBeforeItStartsIsSkipped()
        {
            using (var job = new PreviewJob<int>())
            using (var started = new ManualResetEventSlim())
            {
                int running = 0, maxRunning = 0, applied = 0, startedCount = 0;
                job.Schedule(t =>
                {
                    Interlocked.Increment(ref startedCount);
                    return BlockUntilCancelled(t, ref running, ref maxRunning, started);
                }, r => applied = r);
                Assert.IsTrue(started.Wait(5000), "1 本目が始まらない");

                // 1 本目が抜ける前に続けて投げる。間の 3 本は始まる前にキャンセルされるので、計算しない。
                for (int i = 0; i < 3; i++)
                {
                    job.Schedule(t =>
                    {
                        Interlocked.Increment(ref startedCount);
                        return BlockUntilCancelled(t, ref running, ref maxRunning, null);
                    }, r => applied = r);
                }
                job.Schedule(t => { Interlocked.Increment(ref startedCount); return 5; }, r => applied = r);

                PumpUntil(() => Volatile.Read(ref applied) != 0);
                Assert.AreEqual(5, applied);
                Assert.AreEqual(2, startedCount, "始まる前にキャンセルされた計算まで走った");
                Assert.AreEqual(1, maxRunning);
            }
        }

        [Test]
        public void FailedWorkDoesNotBlockNextWork()
        {
            using (var job = new PreviewJob<int>())
            {
                Exception error = null;
                job.Schedule(t => throw new InvalidOperationException("boom"), r => { }, ex => error = ex);
                PumpUntil(() => error != null);
                Assert.IsInstanceOf<InvalidOperationException>(error);

                int applied = 0;
                job.Schedule(t => 7, r => applied = r);
                PumpUntil(() => applied != 0);
                Assert.AreEqual(7, applied);
            }
        }

        // 後始末(cleanup)は、計算した・始まる前に取り消された・例外で終わった、のどれでも 1 回ずつ走る
        // (ジョブが読むために足した NativePixels の参照を返す所なので、取りこぼすと Mono のヒープの外に残る)。
        // 計算は済んだが apply しなかった結果は discard に渡る。
        [Test]
        public void CleanupRunsOnceForEveryScheduleAndStaleResultsAreDiscarded()
        {
            using (var job = new PreviewJob<int>())
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int cleanups = 0, applied = 0;
                var discarded = new System.Collections.Concurrent.ConcurrentBag<int>();
                // 1 本目: 取り消しを無視して最後まで計算する(結果は古くなるので discard へ)。
                job.Schedule(t => { started.Set(); release.Wait(5000); return 1; }, r => applied = r,
                    cleanup: () => Interlocked.Increment(ref cleanups), discard: r => discarded.Add(r));
                Assert.IsTrue(started.Wait(5000), "1 本目が始まらない");
                // 2 本目: 始まる前に取り消される(計算しない)。
                job.Schedule(t => 2, r => applied = r,
                    cleanup: () => Interlocked.Increment(ref cleanups), discard: r => discarded.Add(r));
                // 3 本目: 例外で終わる。4 本目は 3 本目の後に並ぶので、3 本目も始まる前に取り消される。
                job.Schedule(t => throw new InvalidOperationException("boom"), r => applied = r,
                    cleanup: () => Interlocked.Increment(ref cleanups), discard: r => discarded.Add(r));
                job.Schedule(t => 4, r => applied = r,
                    cleanup: () => Interlocked.Increment(ref cleanups), discard: r => discarded.Add(r));
                release.Set();

                PumpUntil(() => Volatile.Read(ref applied) != 0 && Volatile.Read(ref cleanups) == 4);
                Assert.AreEqual(4, applied, "最後の結果だけが適用される");
                Assert.AreEqual(4, cleanups, "後始末が予約ごとに 1 回ずつ走っていない");
                CollectionAssert.AreEquivalent(new[] { 1 }, discarded.ToArray(), "古くなった結果が discard に渡っていない");
            }
        }

        // 破棄済みのジョブへの予約は計算しないが、後始末は呼ぶ(呼び出し側が足した参照を返すため)。
        [Test]
        public void ScheduleAfterDisposeStillRunsCleanup()
        {
            var job = new PreviewJob<int>();
            job.Dispose();
            int cleanups = 0, applied = 0;
            job.Schedule(t => 1, r => applied = r, cleanup: () => cleanups++);
            PreviewJobMainThread.Drain();
            Assert.AreEqual(1, cleanups);
            Assert.AreEqual(0, applied);
        }

        // 使い回しの出力バッファは、プールだけが参照を持っているもの(参照 1)しか渡さない。
        // 拡大表示の切り出し中・シーンへの反映中のもの(ほかの持ち主がいる)は渡さず、閉じたら全部手放す。
        [Test]
        public void OutputBuffersHandOutOnlyUnsharedBuffersAndReleaseOnClear()
        {
            long before = IrocaNativeMemory.LiveBytes;
            var buffers = new PreviewOutputBuffers();
            var a = NativePixels.Allocate(8, 8);
            var held = a.AddRef();               // ほかの持ち主(切り出し中のジョブなど)
            buffers.Retire(a);                    // プールへ(参照を渡す)
            Assert.IsNull(buffers.Take(8, 8), "ほかに持ち主がいるのに渡した");
            held.Release();
            Assert.IsNull(buffers.Take(4, 4), "寸法が違うのに渡した");
            var got = buffers.Take(8, 8);
            Assert.AreSame(a, got, "持ち主がいなくなったのに渡さない");
            Assert.AreEqual(1, got.RefCount);

            // 置くのは 1 本まで(2 本目を置くと古い方を手放す)。
            var b = NativePixels.Allocate(8, 8);
            buffers.Retire(got);
            buffers.Retire(b);
            Assert.AreEqual(0, a.RefCount, "古い方を手放していない");
            buffers.Clear();
            Assert.AreEqual(0, b.RefCount, "Clear で手放していない");

            // 閉じた後に(止めたジョブから)戻ってきたものは置かずに手放す。
            var c = NativePixels.Allocate(8, 8);
            buffers.Close();
            buffers.Retire(c);
            Assert.AreEqual(0, c.RefCount, "閉じた後に置いた");
            Assert.IsNull(buffers.Take(8, 8));
            Assert.AreEqual(before, IrocaNativeMemory.LiveBytes, "ネイティブメモリが残った");
        }
    }
}
