// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Iroca.EditorTests {
    /// <summary>
    /// MemoryBench の AI 提案の場面（scenario = aiSuggest）。モデルの読み込みと埋め込みの計算のあと、同じ画像の別の
    /// 位置を steps 回クリックし、1 手ごとのメモリを残す（0.4.0 の計画の 0-1 の残りで、1.3 の表の B4）。クリックを
    /// 重ねたときに Mono のヒープやプロセスのメモリが増え続けるか、どこかで止まるかを見る。
    /// 推論は EditorApplication.update で進むので、ほかの場面と違ってフレームを回す（UnityTest）。Sentis とモデルが
    /// 要るので、それらが入ったホストで <c>scripts/Run-EditorTests.ps1 -MemCases &lt;json&gt; -Graphics</c> で回す。
    /// </summary>
    public partial class MemoryBenchTests {
        private const string AiScenario = "aiSuggest";
        private const double AiTimeoutSeconds = 600;

        [UnityTest, Timeout(3600000)]
        public IEnumerator AiSuggestMemory() {
            string casesPath = Environment.GetEnvironmentVariable("IROCA_MEM_CASES");
            string reportPath = Environment.GetEnvironmentVariable("IROCA_MEM_REPORT");
            if (string.IsNullOrEmpty(casesPath) || !File.Exists(casesPath)) {
                Assert.Ignore("IROCA_MEM_CASES が未設定です（メモリ計測は Run-EditorTests.ps1 -MemCases で回す）");
            }
            Assert.IsFalse(string.IsNullOrEmpty(reportPath), "IROCA_MEM_REPORT が未設定です");

            var cfg = JsonUtility.FromJson<CasesDto>(File.ReadAllText(casesPath));
            var cases = cfg.cases.Where(c => c.scenario == AiScenario).ToList();
            if (cases.Count == 0) {
                Assert.Ignore("この回のケースに AI の場面はありません");
            }
            Assert.IsNotNull(MaskSuggestBridge.Service, "AI 統合アセンブリが無いホストです（Sentis を入れたホストで回す）");
            Assert.IsTrue(MaskSuggestBridge.ModelFilesPresent, "AI モデルが未配置です");

            // -nographics で GPU のカーネルが無いと、推論エンジンが LogError してから CPU へ切り替える（MaskSuggestInferenceTests と同じ）。
            LogAssert.ignoreFailingMessages = true;
            var sb = new StringBuilder();
            try {
                foreach (var c in cases) {
                    yield return RunAiCase(c, cfg, sb);
                }
            } finally {
                LogAssert.ignoreFailingMessages = false;
                File.WriteAllText(reportPath, sb.ToString());
            }
        }

        private static IEnumerator RunAiCase(CaseDto c, CasesDto cfg, StringBuilder sb) {
            var tex = ImportBenchTexture(c, out string assetPath);
            var svc = MaskSuggestBridge.Service;
            var owner = new object();
            NativePixels pixels = null;
            try {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                var kind = ExportPipeline.ReadWorkingPixelsNative(Path.GetFullPath(assetPath), tex, out pixels);
                Assert.AreNotEqual(ExportPipeline.SourceKind.Unavailable, kind, $"{c.name}: 原本を読めません");
                Assert.IsTrue(svc.TryEnsureModels(), $"{c.name}: モデルのロード失敗: {svc.ErrorMessage}");

                string key = "membench:" + c.name;
                int answered = 0;
                for (int k = 0; k < cfg.steps; k++) {
                    // 同じ位置に当たらないよう、1 手ごとに位置をずらす（透明な所に当たる手も、製品と同じくそのまま測る）。
                    float u = Mathf.Repeat(0.13f + 0.37f * k, 1f), v = Mathf.Repeat(0.21f + 0.53f * k, 1f);
                    var sw = Stopwatch.StartNew();
                    bool done = false;
                    while (true) {
                        var r = AiSuggestBroker.RequestWhenIdle(owner, key, pixels, u, v, p => {
                            done = true;
                            if (p != null) {
                                answered++;
                            }
                        });
                        if (r == AiSuggestBroker.Request.Started) {
                            break;
                        }
                        Assert.AreNotEqual(AiSuggestBroker.Request.Unavailable, r, $"{c.name}: AI が使えません: {svc.ErrorMessage}");
                        Assert.Less(sw.Elapsed.TotalSeconds, AiTimeoutSeconds, $"{c.name}: AI の準備が終わりません");
                        yield return null;
                    }
                    while (!done) {
                        Assert.Less(sw.Elapsed.TotalSeconds, AiTimeoutSeconds, $"{c.name}: 提案が届きません（手 {k}）");
                        yield return null;
                    }
                    sb.Append(Sample(c.name, "aiClick", k, sw.Elapsed.TotalMilliseconds)).Append('\n');
                }
                Assert.Greater(answered, 0, $"{c.name}: 提案が 1 度も返っていません");
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveAfterSteps", cfg.steps, 0)).Append('\n');
            } finally {
                AiSuggestBroker.Cancel(owner);
                pixels?.Release();
                AiSuggestBroker.ReleaseGpuIfUnused();
                FinishScenario(c, cfg, sb, assetPath);
            }
        }
    }
}
