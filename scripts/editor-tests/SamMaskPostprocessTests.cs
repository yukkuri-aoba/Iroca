// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Iroca.EditorTests {
    /// <summary>
    /// AI 提案のロジットを元の寸法へ拡大する所(<see cref="SamMaskPostprocess.UpscaleChannel(float[], int, int, int, int, int, CancellationToken)"/>)
    /// の後始末。マスクを確保した後の並列ループが取り消しで抜けても、確保したマスクを返す(以前は誰にも渡さないまま
    /// 捨て、4K で 16MB がファイナライザまで残っていた。点検 2026-11-09 領域 6。Ops は f36b074e)。
    /// </summary>
    public class SamMaskPostprocessTests {
        // 2 段目(元の寸法のマスクを書くループ)が、確保を見てから取り消すまでより十分に長くかかる大きさ。
        private const int Tex = 8192;

        [Test]
        public void CancelWhileWritingTheMask_ReturnsTheMask() {
            const int lowRes = SamMaskPostprocess.LowRes;
            var logits = new float[lowRes * lowRes];
            for (int i = 0; i < logits.Length; i++) {
                logits[i] = (i % 7) - 3f;
            }
            long before = IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask);

            bool cancelledAfterAllocating = false;
            for (int attempt = 0; attempt < 5 && !cancelledAfterAllocating; attempt++) {
                using (var cts = new CancellationTokenSource()) {
                    Exception error = null;
                    NativeBools result = null;
                    var worker = new Thread(() => {
                        try {
                            result = SamMaskPostprocess.UpscaleChannel(logits, 0, Tex, Tex,
                                SamImageOps.InputSize, SamImageOps.InputSize, cts.Token);
                        } catch (Exception e) {
                            error = e;
                        }
                    });
                    worker.Start();
                    // マスクを確保したら(2 段目に入ったら)取り消す。
                    var watch = Stopwatch.StartNew();
                    bool allocated = false;
                    while (worker.IsAlive && watch.Elapsed.TotalSeconds < 30) {
                        if (IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask) > before) {
                            allocated = true;
                            break;
                        }
                        Thread.SpinWait(20);
                    }
                    cts.Cancel();
                    Assert.IsTrue(worker.Join(30000), "拡大が終わらない");
                    result?.Release();
                    if (error != null && !(error is OperationCanceledException)) {
                        Assert.Fail("取り消し以外の例外: " + error);
                    }
                    cancelledAfterAllocating = allocated && error is OperationCanceledException;
                    Assert.AreEqual(before, IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask),
                        $"試行 {attempt}: 取り消した拡大のマスクが返っていない(確保 {allocated}・{(error != null ? "取り消し" : "完了")})");
                }
            }
            Assert.IsTrue(cancelledAfterAllocating, "テストの前提: マスクを確保した後に取り消せなかった(2 段目が速すぎる)");
        }
    }
}
