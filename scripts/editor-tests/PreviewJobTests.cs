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
    }
}
