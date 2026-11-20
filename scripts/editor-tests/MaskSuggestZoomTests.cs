// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// AI マスク提案のズームの第 2 段(小さい部品をクリックしたとき、周りを切り出してもう一度推論する)を、本物の推論で通す。
    /// MaskSuggestInferenceTests の円は画像が 256 四方でズームしないので、ここでは 1024 四方の灰地に小さい赤い円を置く。
    ///   - ズームの結果を返す(用途・粒度で後処理の段が変わる 3 通り: 右クリックの自動 = 色で選ぶ、右クリックの細かい、自動調整の証拠)
    ///   - 切り出しのエンコード・デコードが失敗したら、第 1 段の提案を精密化して返す(エラーにしない)
    /// パッケージ未導入・モデル未配置なら Ignore(ML ホストでだけ走る)。IROCA_AI_REPORT にパスがあれば、
    /// ケースごとに範囲のハッシュを 1 行追記する(作り替えの前後で提案が変わっていないかを比べる材料)。
    /// </summary>
    public class MaskSuggestZoomTests {
        const int Size = 1024;
        const int DiscX = 300, DiscY = 700;   // 下原点の画素
        const float Radius = 24f;
        const double TimeoutSeconds = 600;

        static Color32[] SmallDiscImage() {
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++) {
                for (int x = 0; x < Size; x++) {
                    bool inside = (x - DiscX) * (x - DiscX) + (y - DiscY) * (y - DiscY) <= Radius * Radius;
                    px[y * Size + x] = inside ? new Color32(200, 30, 30, 255) : new Color32(150, 150, 150, 255);
                }
            }
            return px;
        }

        static IEnumerator WaitFor(IMaskSuggestService svc, System.Func<bool> done, string what) {
            double end = EditorApplication.timeSinceStartup + TimeoutSeconds;
            while (!done()) {
                if (EditorApplication.timeSinceStartup > end) {
                    Assert.Fail($"{what} が {TimeoutSeconds} 秒で終わりませんでした（Phase={svc.Phase}, {svc.ErrorMessage}）");
                }
                yield return null;
            }
        }

        static IMaskSuggestService ServiceOrIgnore() {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            if (!MaskSuggestBridge.ModelFilesPresent) {
                Assert.Ignore("AI モデルが未配置");
            }
            return svc;
        }

        const System.Reflection.BindingFlags Statics = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // 1 クリック分を回して、提案を確かめる。zoomRan は「このクリックでズームの第 2 段へ進んだか」(サービスの内部。無ければ確かめない)。
        static IEnumerator RunClick(IMaskSuggestService svc, string label, MaskSuggestGranularity granularity, MaskSuggestUse use,
                                    System.Action beforeClick = null) {
            Assert.IsTrue(svc.TryEnsureModels(), $"モデルのロード失敗: {svc.ErrorMessage}");
            var src = NativePixels.CopyOf(SmallDiscImage(), Size, Size);
            // 鍵は毎回変える(切り出しの埋め込みの控えに当てず、切り出しのエンコードを通す)。
            svc.SetSource("iroca-test-zoom-" + label + "-" + System.Guid.NewGuid().ToString("N"), src);
            src.Release();
            yield return WaitFor(svc, () => svc.Phase == MaskSuggestPhase.Idle || svc.Phase == MaskSuggestPhase.Error, "画像のエンコードと暖機");
            Assert.AreEqual(MaskSuggestPhase.Idle, svc.Phase, svc.ErrorMessage);

            beforeClick?.Invoke();
            float u = (DiscX + 0.5f) / Size, v = (DiscY + 0.5f) / Size;
            Assert.IsTrue(svc.RequestProposal(u, v, granularity, use));
            yield return WaitFor(svc, () => svc.Phase == MaskSuggestPhase.ProposalReady || svc.Phase == MaskSuggestPhase.Error, "クリックの推論");
            Assert.AreEqual(MaskSuggestPhase.ProposalReady, svc.Phase, svc.ErrorMessage);
            Assert.IsTrue(svc.TryTakeProposal(out var proposal));
            Assert.AreEqual((Size, Size), (proposal.width, proposal.height));
            bool[] got = proposal.mask.ToArray();
            proposal.Release();

            var zoomRanField = svc.GetType().GetField("_clickZoomRan", Instance);
            bool? zoomRan = zoomRanField == null ? (bool?)null : (bool)zoomRanField.GetValue(svc);

            int inter = 0, union = 0;
            uint hash = 2166136261u;
            for (int y = 0; y < Size; y++) {
                for (int x = 0; x < Size; x++) {
                    bool truth = (x - DiscX) * (x - DiscX) + (y - DiscY) * (y - DiscY) <= Radius * Radius;
                    bool m = got[y * Size + x];
                    if (truth && m) {
                        inter++;
                    }
                    if (truth || m) {
                        union++;
                    }
                    hash = (hash ^ (m ? 1u : 0u)) * 16777619u;
                }
            }
            float iou = union == 0 ? 0f : inter / (float)union;
            string report = System.Environment.GetEnvironmentVariable("IROCA_AI_REPORT");
            if (!string.IsNullOrEmpty(report)) {
                System.IO.File.AppendAllText(report,
                    $"zoom:{label}\t{Application.unityVersion}\tcpu={svc.UsesCpuBackend}\tzoomRan={zoomRan}\tiou={iou:F4}" +
                    $"\tscore={proposal.score:F4}\tarea={proposal.areaFrac:F6}\tflood={proposal.floodWarning}\tmask={hash:x8}\n");
            }
            if (zoomRan.HasValue) {
                Assert.IsTrue(zoomRan.Value, $"{label}: ズームの第 2 段へ進んでいない(テストの前提: 小さい円の第 1 段の範囲が小さい。IoU {iou:F3})");
            }
            Assert.GreaterOrEqual(iou, 0.8f, $"{label}: 円を取れていない（IoU {iou:F3}, score {proposal.score:F3}）");
        }

        [UnityTest, Timeout(1800000)]
        public IEnumerator Zoom_SmallDisc_ReturnsTheZoomedProposal() {
            var svc = ServiceOrIgnore();
            LogAssert.ignoreFailingMessages = true;   // -nographics のときの GPU の失敗(CPU へ切り替える)
            try {
                yield return RunClick(svc, "region-auto", MaskSuggestGranularity.Auto, MaskSuggestUse.Region);
                yield return RunClick(svc, "region-fine", MaskSuggestGranularity.Fine, MaskSuggestUse.Region);
                yield return RunClick(svc, "evidence-auto", MaskSuggestGranularity.Auto, MaskSuggestUse.Evidence);
            } finally { LogAssert.ignoreFailingMessages = false; }
        }

        // 切り出しのエンコード・デコードの失敗はテスト専用の印で 1 回ずつ起こす(AI 統合アセンブリの内部なので名前で呼ぶ)。
        [UnityTest, Timeout(1800000)]
        public IEnumerator ZoomFailure_FallsBackToTheRefinedStage1Proposal() {
            var svc = ServiceOrIgnore();
            var type = svc.GetType();
            var failEncodes = type.GetField("FailNextZoomEncodesForTests", Statics);
            var failDecodes = type.GetField("FailNextZoomDecodesForTests", Statics);
            if (failEncodes == null || failDecodes == null) {
                Assert.Ignore("サービスが別の実装(テスト用の印が無い)");
            }
            LogAssert.ignoreFailingMessages = true;   // 第 1 段へ戻すときの警告と、-nographics のときの GPU の失敗
            try {
                yield return RunClick(svc, "encode-fail", MaskSuggestGranularity.Auto, MaskSuggestUse.Region,
                    () => failEncodes.SetValue(null, 1));
                Assert.AreEqual(0, (int)failEncodes.GetValue(null), "テストの前提: 切り出しのエンコードの失敗を起こしていない");
                yield return RunClick(svc, "decode-fail", MaskSuggestGranularity.Auto, MaskSuggestUse.Region,
                    () => failDecodes.SetValue(null, 1));
                Assert.AreEqual(0, (int)failDecodes.GetValue(null), "テストの前提: 切り出しのデコードの失敗を起こしていない");
            } finally {
                failEncodes.SetValue(null, 0);
                failDecodes.SetValue(null, 0);
                LogAssert.ignoreFailingMessages = false;
            }
        }
    }
}
