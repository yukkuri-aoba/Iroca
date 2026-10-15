// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests
{
    /// <summary>
    /// AI マスク提案の実推論。Unity 2022.3（Sentis 2.1）と Unity 6（Inference Engine 2.2 以降）で、
    /// 同じコードが「パッケージを読み込み、モデルを変換・ロードし、推論して、クリックした物の形を返す」
    /// ところまで実機で通ることを見る。パッケージ未導入・モデル未配置の環境では Ignore する。
    /// 合成画像（灰地の赤い円）の中心をクリックし、円と提案マスクの IoU を測る。
    /// IROCA_AI_REPORT にパスがあれば、環境ごとの値を比べられるよう 1 行追記する。
    /// </summary>
    public class MaskSuggestInferenceTests
    {
        const int Size = 256;
        const float Radius = 70f;
        const double TimeoutSeconds = 600;

        static Color32[] DiscImage()
        {
            var px = new Color32[Size * Size];
            float c = (Size - 1) * 0.5f;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    bool inside = (x - c) * (x - c) + (y - c) * (y - c) <= Radius * Radius;
                    px[y * Size + x] = inside ? new Color32(200, 30, 30, 255) : new Color32(150, 150, 150, 255);
                }
            return px;
        }

        static IEnumerator WaitFor(System.Func<bool> done, string what)
        {
            double end = EditorApplication.timeSinceStartup + TimeoutSeconds;
            while (!done())
            {
                if (EditorApplication.timeSinceStartup > end)
                    Assert.Fail($"{what} が {TimeoutSeconds} 秒で終わりませんでした（Phase={MaskSuggestBridge.Service.Phase}, {MaskSuggestBridge.Service.ErrorMessage}）");
                yield return null;
            }
        }

        [UnityTest, Timeout(1200000)]
        public IEnumerator Suggestion_ForSyntheticDisc_CoversTheDisc()
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            if (!MaskSuggestBridge.ModelFilesPresent) Assert.Ignore("AI モデルが未配置");

            // 画面を持たない batchmode（-nographics）では、GPUCompute を選んだ推論エンジンが
            // 「Kernel ... not found」を LogError で出してから Iroca が CPU へ切り替える。
            // その切り替え後に推論が通ることを見たいので、ここだけエラーログで落とさない。
            LogAssert.ignoreFailingMessages = true;
            try
            {
                yield return RunDiscSuggestion(svc);
            }
            finally { LogAssert.ignoreFailingMessages = false; }
        }

        static IEnumerator RunDiscSuggestion(IMaskSuggestService svc)
        {
            Assert.IsTrue(svc.TryEnsureModels(), $"モデルのロード失敗: {svc.ErrorMessage}");
            Assert.AreEqual(MaskSuggestPhase.Idle, svc.Phase, svc.ErrorMessage);

            svc.SetSource("iroca-test-disc", DiscImage(), Size, Size);
            // エンコード完了の直後にデコーダーの暖機（捨て推論）が Decoding 表示で 1 回走るので、
            // Encoding が終わっただけでなく Idle に戻るまで待つ。
            yield return WaitFor(
                () => svc.Phase == MaskSuggestPhase.Idle || svc.Phase == MaskSuggestPhase.Error,
                "画像のエンコードと暖機");
            Assert.AreEqual(MaskSuggestPhase.Idle, svc.Phase, svc.ErrorMessage);

            Assert.IsTrue(svc.RequestProposal(0.5f, 0.5f, MaskSuggestGranularity.Auto));
            yield return WaitFor(
                () => svc.Phase == MaskSuggestPhase.ProposalReady || svc.Phase == MaskSuggestPhase.Error,
                "クリックの推論");
            Assert.AreEqual(MaskSuggestPhase.ProposalReady, svc.Phase, svc.ErrorMessage);
            Assert.IsTrue(svc.TryTakeProposal(out var proposal));
            Assert.AreEqual((Size, Size), (proposal.width, proposal.height));

            float c = (Size - 1) * 0.5f;
            int inter = 0, union = 0;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    bool truth = (x - c) * (x - c) + (y - c) * (y - c) <= Radius * Radius;
                    bool got = proposal.maskBottomUp[y * Size + x];
                    if (truth && got) inter++;
                    if (truth || got) union++;
                }
            float iou = union == 0 ? 0f : inter / (float)union;

            string report = System.Environment.GetEnvironmentVariable("IROCA_AI_REPORT");
            if (!string.IsNullOrEmpty(report))
                System.IO.File.AppendAllText(report,
                    $"{Application.unityVersion}\tcpu={svc.UsesCpuBackend}\tiou={iou:F4}\tscore={proposal.score:F4}\tarea={proposal.areaFrac:F4}\n");

            Assert.GreaterOrEqual(iou, 0.9f, $"円を取れていない（IoU {iou:F3}, score {proposal.score:F3}）");
        }
    }
}
