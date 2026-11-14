// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// AI マスク提案の実推論。Unity 2022.3（Sentis 2.1）と Unity 6（Inference Engine 2.2 以降）で、
    /// 同じコードが「パッケージを読み込み、モデルを変換・ロードし、推論して、クリックした物の形を返す」
    /// ところまで実機で通ることを見る。パッケージ未導入・モデル未配置の環境では Ignore する。
    /// 合成画像（灰地の赤い円）の中心をクリックし、円と提案マスクの IoU を測る。
    /// IROCA_AI_REPORT にパスがあれば、環境ごとの値を比べられるよう 1 行追記する。
    /// </summary>
    public class MaskSuggestInferenceTests {
        const int Size = 256;
        const float Radius = 70f;
        const double TimeoutSeconds = 600;

        static Color32[] DiscImage() {
            var px = new Color32[Size * Size];
            float c = (Size - 1) * 0.5f;
            for (int y = 0; y < Size; y++) {
                for (int x = 0; x < Size; x++) {
                    bool inside = (x - c) * (x - c) + (y - c) * (y - c) <= Radius * Radius;
                    px[y * Size + x] = inside ? new Color32(200, 30, 30, 255) : new Color32(150, 150, 150, 255);
                }
            }
            return px;
        }

        static IEnumerator WaitFor(System.Func<bool> done, string what) {
            double end = EditorApplication.timeSinceStartup + TimeoutSeconds;
            while (!done()) {
                if (EditorApplication.timeSinceStartup > end) {
                    Assert.Fail($"{what} が {TimeoutSeconds} 秒で終わりませんでした（Phase={MaskSuggestBridge.Service.Phase}, {MaskSuggestBridge.Service.ErrorMessage}）");
                }
                yield return null;
            }
        }

        [UnityTest, Timeout(1200000)]
        public IEnumerator Suggestion_ForSyntheticDisc_CoversTheDisc() {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            if (!MaskSuggestBridge.ModelFilesPresent) {
                Assert.Ignore("AI モデルが未配置");
            }

            // 画面を持たない batchmode（-nographics）では、GPUCompute を選んだ推論エンジンが
            // 「Kernel ... not found」を LogError で出してから Iroca が CPU へ切り替える。
            // その切り替え後に推論が通ることを見たいので、ここだけエラーログで落とさない。
            LogAssert.ignoreFailingMessages = true;
            try {
                yield return RunDiscSuggestion(svc, "iroca-test-disc");
                // いろかのウィンドウを閉じたとき(推論のワーカーを手放す)の後も、作り直して同じように取れる。
                // 別の鍵にして、エンコーダーも作り直したワーカーで走らせる。
                svc.ReleaseGpuResources();
                yield return RunDiscSuggestion(svc, "iroca-test-disc-after-release");
            } finally { LogAssert.ignoreFailingMessages = false; }
        }

        static IEnumerator RunDiscSuggestion(IMaskSuggestService svc, string sourceKey, System.Action beforeClick = null) {
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

            beforeClick?.Invoke();
            Assert.IsTrue(svc.RequestProposal(0.5f, 0.5f, MaskSuggestGranularity.Auto, MaskSuggestUse.Region));
            yield return WaitFor(
                () => svc.Phase == MaskSuggestPhase.ProposalReady || svc.Phase == MaskSuggestPhase.Error,
                "クリックの推論");
            Assert.AreEqual(MaskSuggestPhase.ProposalReady, svc.Phase, svc.ErrorMessage);
            Assert.IsTrue(svc.TryTakeProposal(out var proposal));
            Assert.AreEqual((Size, Size), (proposal.width, proposal.height));
            // 範囲はヒープの外。サービスから直接受け取ったので自分で返す。
            bool[] gotMask = proposal.mask.ToArray();
            proposal.Release();

            float c = (Size - 1) * 0.5f;
            int inter = 0, union = 0;
            for (int y = 0; y < Size; y++) {
                for (int x = 0; x < Size; x++) {
                    bool truth = (x - c) * (x - c) + (y - c) * (y - c) <= Radius * Radius;
                    bool got = gotMask[y * Size + x];
                    if (truth && got) {
                        inter++;
                    }
                    if (truth || got) {
                        union++;
                    }
                }
            }
            float iou = union == 0 ? 0f : inter / (float)union;

            string report = System.Environment.GetEnvironmentVariable("IROCA_AI_REPORT");
            if (!string.IsNullOrEmpty(report)) {
                System.IO.File.AppendAllText(report,
                    $"{Application.unityVersion}\tcpu={svc.UsesCpuBackend}\tiou={iou:F4}\tscore={proposal.score:F4}\tarea={proposal.areaFrac:F4}\n");
            }

            Assert.GreaterOrEqual(iou, 0.9f, $"円を取れていない（IoU {iou:F3}, score {proposal.score:F3}）");
        }

        // いろかのウィンドウを閉じるとワーカーを手放す。開き直して右クリックのメニューを開くと、モデルの準備
        // (TryEnsureModels)を通らずに元だけが渡される(AiSuggestBroker.Prepare)。そのときも作り直して解析し、
        // ワーカーが無いことを GPU の失敗と取り違えて CPU へ落ちない(落ちると次のリロードまで遅いまま)。
        [UnityTest, Timeout(1200000)]
        public IEnumerator SetSource_AfterReleasingTheWorkers_RecreatesThemWithTheSameBackend() {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            if (!MaskSuggestBridge.ModelFilesPresent) {
                Assert.Ignore("AI モデルが未配置");
            }

            LogAssert.ignoreFailingMessages = true;   // -nographics のときの GPU の失敗は上のテストと同じ
            try {
                // 別の鍵で 1 回解析して、この環境で動くバックエンドに落ち着かせる。
                yield return RunDiscSuggestion(svc, "iroca-test-disc-before-release");
                bool cpuBefore = svc.UsesCpuBackend;

                svc.ReleaseGpuResources();
                var src = NativePixels.CopyOf(DiscImage(), Size, Size);
                svc.SetSource("iroca-test-disc-prepare-" + System.Guid.NewGuid().ToString("N"), src);
                src.Release();
                yield return WaitFor(
                    () => svc.Phase == MaskSuggestPhase.Idle || svc.Phase == MaskSuggestPhase.Error,
                    "ワーカーを手放した後の解析");
                Assert.AreEqual(MaskSuggestPhase.Idle, svc.Phase, svc.ErrorMessage);
                Assert.AreEqual(cpuBefore, svc.UsesCpuBackend, "ワーカーが無いだけなのにバックエンドが変わった");
            } finally { LogAssert.ignoreFailingMessages = false; }
        }

        // GPU の失敗の扱い。GPU 固有の失敗(ドライバ・シェーダ)は CPU に切り替えてやり直し、ワーカーを手放した後
        // (いろかの窓を開き直したとき)にもう一度だけ GPU を試す。2 回失敗したら、以後は CPU のまま。
        // 失敗はテスト専用の印で 1 回ずつ起こす(AI 統合アセンブリの内部なので名前で呼ぶ)。GPU で推論している環境でだけ意味がある。
        [UnityTest, Timeout(1200000)]
        public IEnumerator GpuFailures_FallBackToCpu_AndTheGpuIsRetriedOnce() {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            if (!MaskSuggestBridge.ModelFilesPresent) {
                Assert.Ignore("AI モデルが未配置");
            }
            var type = svc.GetType();
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var failEncodes = type.GetField("FailNextGpuEncodesForTests", statics);
            var failDecodes = type.GetField("FailNextGpuDecodesForTests", statics);
            var failCreations = type.GetField("FailNextGpuWorkerCreationsForTests", statics);
            var gpuFailures = type.GetField("_gpuFailures",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (failEncodes == null || failDecodes == null || failCreations == null || gpuFailures == null) {
                Assert.Ignore("サービスが別の実装(テスト用の印が無い)");
            }
            // 失敗の数を 0 に戻して、ワーカーを作り直させる(このテストの前後で、ほかのテストに持ち越さない)。
            void Reset() {
                failEncodes.SetValue(null, 0);
                failDecodes.SetValue(null, 0);
                failCreations.SetValue(null, 0);
                gpuFailures.SetValue(svc, 0);
                svc.ReleaseGpuResources();
            }
            string id = System.Guid.NewGuid().ToString("N");

            LogAssert.ignoreFailingMessages = true;   // 切り替えの警告と、-nographics のときの GPU の失敗
            try {
                Reset();
                yield return RunDiscSuggestion(svc, "iroca-test-gpu-first-" + id);
                if (svc.UsesCpuBackend) {
                    Assert.Ignore("この環境は GPU で推論していない(-nographics など)");
                }

                // エンコードの失敗(1 回目): CPU に切り替えてやり直し、提案まで通る。
                failEncodes.SetValue(null, 1);
                yield return RunDiscSuggestion(svc, "iroca-test-gpu-encode-" + id);
                Assert.AreEqual(0, (int)failEncodes.GetValue(null), "テストの前提: 失敗を起こしていない");
                Assert.IsTrue(svc.UsesCpuBackend, "GPU のエンコードの失敗の後、CPU に切り替えていない");

                // ワーカーを手放す(窓を閉じる)と、次はもう一度 GPU を試す。
                svc.ReleaseGpuResources();
                yield return RunDiscSuggestion(svc, "iroca-test-gpu-retry-" + id);
                Assert.IsFalse(svc.UsesCpuBackend, "ワーカーを手放した後、GPU を試し直していない(1 回の失敗でずっと CPU のまま)");

                // クリックのデコードの失敗(2 回目): CPU に切り替えて、そのクリックをやり直す(エラーにしない)。
                yield return RunDiscSuggestion(svc, "iroca-test-gpu-decode-" + id, () => failDecodes.SetValue(null, 1));
                Assert.AreEqual(0, (int)failDecodes.GetValue(null), "テストの前提: 失敗を起こしていない");
                Assert.IsTrue(svc.UsesCpuBackend, "GPU のデコードの失敗の後、CPU に切り替えていない");

                // 2 回失敗した後は、手放しても CPU のまま(毎回 GPU の失敗を待たせない)。
                svc.ReleaseGpuResources();
                yield return RunDiscSuggestion(svc, "iroca-test-gpu-stay-" + id);
                Assert.IsTrue(svc.UsesCpuBackend, "2 回失敗した後も GPU を試した");

                // ワーカーを作れないとき: CPU で作り直す(以前はそのままエラーだった)。
                Reset();
                failCreations.SetValue(null, 1);
                Assert.IsTrue(svc.TryEnsureModels(), $"GPU でワーカーを作れないとき、CPU で作り直していない: {svc.ErrorMessage}");
                Assert.AreEqual(0, (int)failCreations.GetValue(null), "テストの前提: 失敗を起こしていない");
                Assert.IsTrue(svc.UsesCpuBackend, "ワーカーの作成の失敗の後、CPU に切り替えていない");
            } finally {
                Reset();
                LogAssert.ignoreFailingMessages = false;
            }
        }

        // 埋め込みの計算の進みは、百分率が変わったときだけ知らせる(知らせるたびに、開いている窓を描き直す)。
        // AI 統合アセンブリの内部なので名前で呼ぶ。
        [Test]
        public void EncodeProgress_IsReportedOnlyWhenThePercentChanges() {
            var pump = System.Type.GetType("Iroca.SentisIntegration.EditorIteratorPump, Iroca.SentisIntegration");
            if (pump == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            var changed = pump.GetMethod("PercentChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(changed, "進みの判定の関数が見つからない（名前を変えたらこのテストも直す）");

            int Reports(int totalSteps) {
                var args = new object[] { 0, totalSteps, -1 };
                int n = 0;
                for (int step = 1; step < totalSteps; step++) {
                    args[0] = step;
                    if ((bool)changed.Invoke(null, args)) {
                        n++;
                    }
                }
                return n;
            }
            Assert.LessOrEqual(Reports(10000), 100, "1 万ステップの計算で、百分率より細かく知らせた");
            Assert.AreEqual(100, Reports(10000), "百分率が変わるたびには知らせる");
            Assert.AreEqual(2, Reports(3), "ステップが少ないときは、進むたびに知らせる");
        }

        // GPU の推論カーネルの準備(プロジェクトで初めて GPU の推論を使うとき、全カーネルのコンパイルでエディタの更新 1 回が
        // 十数秒止まっていたのを、1 回の MoveNext で 1 つずつに割る)。Sentis の Resources の下のコンピュートシェーダを、
        // カーネルの番号の範囲の外まで回して終わること・エラーを出さないこと・進みが 1 まで届くことを見る
        // (範囲の外の扱いが Unity の版で変わったら、ここで分かる)。AI 統合アセンブリの内部なので名前で呼ぶ。
        [Test]
        public void GpuKernelPrewarm_VisitsTheKernelsOfEveryShaderAndEnds() {
            var type = System.Type.GetType("Iroca.SentisIntegration.SentisKernelPrewarm, Iroca.SentisIntegration");
            if (type == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) {
                Assert.Ignore("この環境はコンピュートシェーダを使えない(-nographics など。推論は CPU で、準備は通らない)");
            }
            int shaders = Resources.LoadAll<ComputeShader>("Sentis").Length;
            Assert.Greater(shaders, 0, "Sentis のコンピュートシェーダが Resources/Sentis に無い(置き場が変わったら準備が何もしない)");

            float last = 0f;
            int reports = 0;
            System.Action<float> onProgress = p => {
                Assert.GreaterOrEqual(p, last, "進みが戻った");
                last = p;
                reports++;
            };
            var prewarm = (IEnumerator)System.Activator.CreateInstance(type,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { onProgress }, null);
            int steps = 0;
            while (prewarm.MoveNext()) {
                steps++;
                Assert.Less(steps, 100000, "終わらない");
            }
            int kernels = (int)type.GetProperty("Kernels").GetValue(prewarm);
            Assert.GreaterOrEqual(kernels, shaders, "どのシェーダにもカーネルが 1 つはあるはず");
            Assert.AreEqual(shaders, reports, "シェーダを 1 つ終えるごとに進みを知らせる");
            Assert.AreEqual(1f, last, 1e-6f, "進みが 1 まで届かない");
        }

        [Test]
        public void LeftoverModelImportFolders_OnlyIrocasOwnAreRemoved() {
            // モデルの変換の途中で Unity が落ちると Assets に一時フォルダが残る。次の変換の前に、
            // 名前が一時フォルダの形で中身がいろかのモデルだけのものに限って消す（AI 統合アセンブリの内部なので
            // 名前で呼ぶ）。
            var repo = System.Type.GetType("Iroca.SentisIntegration.SentisModelRepository, Iroca.SentisIntegration");
            if (repo == null) {
                Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            }
            var sweep = repo.GetMethod("DeleteLeftoverTempDirs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(sweep, "片付けの関数が見つからない（名前を変えたらこのテストも直す）");

            string Dir(string suffix) => System.IO.Path.Combine(Application.dataPath, "IrocaModelImportTemp_" + suffix);
            string ours = Dir(System.Guid.NewGuid().ToString("N"));
            string withUserFile = Dir(System.Guid.NewGuid().ToString("N"));
            string otherName = Dir("mine");
            var dirs = new[] { ours, withUserFile, otherName };
            try {
                // 取り込ませない（AssetDatabase を更新しない）ので、ファイルを直接置く。
                foreach (var d in dirs) {
                    System.IO.Directory.CreateDirectory(d);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(d, "mobile_sam_encoder.onnx"), "x");
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(withUserFile, "notes.txt"), "x");

                sweep.Invoke(null, null);

                Assert.IsFalse(System.IO.Directory.Exists(ours), "残った一時フォルダを消していない");
                Assert.IsTrue(System.IO.Directory.Exists(withUserFile), "ほかのファイルが入ったフォルダを消した");
                Assert.IsTrue(System.IO.Directory.Exists(otherName), "一時フォルダの形でない名前のフォルダを消した");
            } finally {
                foreach (var d in dirs) {
                    if (System.IO.Directory.Exists(d)) {
                        System.IO.Directory.Delete(d, recursive: true);
                    }
                }
            }
        }
    }
}
