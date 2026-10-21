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
                yield return RunDiscSuggestion(svc, "iroca-test-disc");
                // いろかのウィンドウを閉じたとき(推論のワーカーを手放す)の後も、作り直して同じように取れる。
                // 別の鍵にして、エンコーダーも作り直したワーカーで走らせる。
                svc.ReleaseGpuResources();
                yield return RunDiscSuggestion(svc, "iroca-test-disc-after-release");
            }
            finally { LogAssert.ignoreFailingMessages = false; }
        }

        static IEnumerator RunDiscSuggestion(IMaskSuggestService svc, string sourceKey)
        {
            Assert.IsTrue(svc.TryEnsureModels(), $"モデルのロード失敗: {svc.ErrorMessage}");
            Assert.AreEqual(MaskSuggestPhase.Idle, svc.Phase, svc.ErrorMessage);

            var src = NativePixels.CopyOf(DiscImage(), Size, Size);
            svc.SetSource(sourceKey, src);
            src.Release();   // サービスが自分の参照を持つ
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

        [Test]
        public void LeftoverModelImportFolders_OnlyIrocasOwnAreRemoved()
        {
            // モデルの変換の途中で Unity が落ちると Assets に一時フォルダが残る。次の変換の前に、
            // 名前が一時フォルダの形で中身がいろかのモデルだけのものに限って消す（AI 統合アセンブリの内部なので
            // 名前で呼ぶ）。
            var repo = System.Type.GetType("Iroca.SentisIntegration.SentisModelRepository, Iroca.SentisIntegration");
            if (repo == null) Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            var sweep = repo.GetMethod("DeleteLeftoverTempDirs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(sweep, "片付けの関数が見つからない（名前を変えたらこのテストも直す）");

            string Dir(string suffix) => System.IO.Path.Combine(Application.dataPath, "IrocaModelImportTemp_" + suffix);
            string ours = Dir(System.Guid.NewGuid().ToString("N"));
            string withUserFile = Dir(System.Guid.NewGuid().ToString("N"));
            string otherName = Dir("mine");
            var dirs = new[] { ours, withUserFile, otherName };
            try
            {
                // 取り込ませない（AssetDatabase を更新しない）ので、ファイルを直接置く。
                foreach (var d in dirs)
                {
                    System.IO.Directory.CreateDirectory(d);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(d, "mobile_sam_encoder.onnx"), "x");
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(withUserFile, "notes.txt"), "x");

                sweep.Invoke(null, null);

                Assert.IsFalse(System.IO.Directory.Exists(ours), "残った一時フォルダを消していない");
                Assert.IsTrue(System.IO.Directory.Exists(withUserFile), "ほかのファイルが入ったフォルダを消した");
                Assert.IsTrue(System.IO.Directory.Exists(otherName), "一時フォルダの形でない名前のフォルダを消した");
            }
            finally
            {
                foreach (var d in dirs)
                    if (System.IO.Directory.Exists(d)) System.IO.Directory.Delete(d, recursive: true);
            }
        }
    }
}
