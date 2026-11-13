// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    public static partial class IrocaAutomation {
        /// <summary>
        /// 色替えを足して、アバターに登録する(既定)・PNG に書き出す・比較画像だけ作る。
        /// 土台は、そのテクスチャを開いているいろかの窓の編集内容(登録では、窓が書き込み先と同じレシピを表示しているとき
        /// だけ)→ 登録先のレシピ → プロジェクトに 1 つだけのレシピ → 空、の順(preset を渡せばそれ)。changes の 1 件が 1 ゾーンで、UI でスポイトして新しいゾーンを作ったのと同じ
        /// (末尾に足す=優先度は最も低い)。同じ名前のゾーンがあれば、その場所で置き換える。
        /// </summary>
        public static string Recolor(string requestJson) {
            // 待っている依頼があるうちは新しい依頼を断る。以前は前の依頼を黙って捨てていたので、AI が 2 件を並べて
            // 呼ぶと、先の依頼は反映されないまま、問い合わせのどちらかが後の依頼の結果を受け取って取り違えた
            // (RecolorStatus は依頼を見分けない)。問い合わせが途絶えた依頼(AI が待つのをやめた)だけは捨てて受ける。
            // 終わっているのに結果が受け取られていない依頼(サーバーが問い合わせをやめた後に終わった)は、断らずに
            // 捨てて受け、その結末を新しい結果の warnings で知らせる(以前は 60 秒 "still running" で断っていた)。
            var running = s_job;
            string unreceived = null;
            if (running != null) {
                if (running.finalJson != null) {
                    unreceived = DescribeUnreceived(running);
                    s_job = null;
                } else if (EditorApplication.timeSinceStartup - running.lastPolled < AbandonedAfterSeconds) {
                    return FinishRecolor(new RecolorTextureResult(),
                        $"another iroca_recolor call (texture {running.r.texture}) is still running, so this call did nothing. "
                        + "Wait until that call returns its result, then call again. Call iroca_recolor one at a time.");
                } else {
                    CancelRecolorJob();
                }
            }
            double now = EditorApplication.timeSinceStartup;
            var job = new RecolorJob { lastPolled = now, startedAt = now, requestKey = RequestKey(requestJson) };
            if (unreceived != null) {
                job.r.warnings.Add(unreceived);
            }
            try {
                string error = PrepareRecolor(requestJson, job);
                if (error != null) {
                    return FinishRecolor(job.r, error);
                }

                if (job.maskEditList.Count > 0) {
                    job.maskEdits = new AutomationMaskEdits(job.state, job.tex, job.work, job.maskEditList, job.r.warnings, job.r.maskEdits);
                }
                if (job.req.autoTune && job.changedZoneIds.Count > 0) {
                    job.tune = new AutomationAutoTune(job.state, job.tex, job.work, job.changedZoneIds, job.r.warnings);
                }
                string done = AdvanceRecolor(job);
                if (done != null) {
                    return done;
                }

                s_job = job;
                EditorApplication.update -= PumpRecolorJob;
                EditorApplication.update += PumpRecolorJob;
                // スクリプトのリロードで依頼は消える。背景の計算も止めておく。
                AssemblyReloadEvents.beforeAssemblyReload -= CancelRecolorJob;
                AssemblyReloadEvents.beforeAssemblyReload += CancelRecolorJob;
                return PendingJson(job);
            } catch (Exception ex) {
                job.Cancel();
                return FinishRecolor(job.r, $"{ex.GetType().Name}: {ex.Message}");
            } finally {
                // 待たずに終わった(結果・エラーを返した)依頼はここで終える。待つ依頼は仕上げ・取り消しで終える。
                if (!ReferenceEquals(s_job, job)) {
                    job.Finish();
                    ReleaseMemoryIfNoWindow();
                }
            }
        }

        /// <summary>
        /// 途中経過(pending)を返した <see cref="Recolor"/>(自動調整を待つ・PNG を書き出す)の続き。進めて、終わっていれば
        /// 最後の結果(Recolor と同じ形)、まだなら途中経過を返す。MCP の問い合わせ直し(action=status)から呼ぶ。
        /// </summary>
        public static string RecolorStatus() {
            var job = s_job;
            if (job == null) {
                return FinishRecolor(new RecolorTextureResult(), "no recolor is in progress.");
            }
            job.lastPolled = EditorApplication.timeSinceStartup;
            try {
                PumpRecolorJob();
                if (job.finalJson == null) {
                    return PendingJson(job);
                }
                s_job = null;
                return job.finalJson;
            } catch (Exception ex) {
                CancelRecolorJob();
                return FinishRecolor(job.r, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// <see cref="RecolorStatus()"/> に、どの依頼の問い合わせかの確かめを足したもの。<paramref name="requestJson"/> は
        /// <see cref="Recolor"/> に渡したのと同じ依頼(MCP のサーバーは、同じ引数に action="status" を足して問い合わせ直す)。
        /// 今の依頼と違えば断る。以前は、スクリプトの読み直しで依頼が消えた後に別の依頼が始まると、前の依頼の問い合わせ
        /// (サーバーは止めない)が新しい依頼の結果を取った。比べるのは JsonUtility で読み直して書いた形なので、欄の順・空白・
        /// 知らない欄(action)の違いは同じとみなす。
        /// </summary>
        public static string RecolorStatus(string requestJson) {
            var job = s_job;
            if (job != null && requestJson != null && RequestKey(requestJson) != job.requestKey) {
                return FinishRecolor(new RecolorTextureResult(),
                    "the iroca_recolor call this status is for is not in progress any more (Unity reloaded its scripts, or "
                    + $"another call (texture {job.r.texture}) is running now), so its result cannot be returned. Call iroca_recolor again.");
            }
            return RecolorStatus();
        }

        // 依頼を見分ける形(JsonUtility で読み直して書いた形)。読めなければ元の文字列。
        private static string RequestKey(string requestJson) {
            try {
                var req = JsonUtility.FromJson<RecolorRequest>(requestJson ?? "");
                if (req != null) {
                    return JsonUtility.ToJson(req);
                }
            } catch (Exception) {
                // 読めない依頼は Recolor が断る。元の文字列のまま比べる。
            }
            return requestJson ?? "";
        }

        // 終わっていたのに結果が受け取られなかった依頼の結末(次の依頼の warnings に出す)。
        private static string DescribeUnreceived(RecolorJob job) {
            RecolorTextureResult last = null;
            try { last = JsonUtility.FromJson<RecolorTextureResult>(job.finalJson); } catch (Exception) { /* 下で不明として書く */ }
            string what = $"the previous iroca_recolor call (texture {job.r.texture}, apply '{job.r.apply}') finished, "
                + "but its result was never received";
            if (last == null) {
                return what + ".";
            }
            if (!last.ok) {
                return what + $": it failed ({last.error}).";
            }
            return what + (last.apply == ApplyPreview
                ? ": it succeeded (nothing was saved)."
                : ": it succeeded, and what it registered or wrote is kept (check with iroca_inspect).");
        }

        // 途中経過を返した依頼(同時に 1 つ。待っている間の新しい依頼は断る)。
        private static RecolorJob s_job;

        // 問い合わせがこれだけ途絶えた依頼は、AI が待つのをやめたものとして、次の依頼が来たら捨てる。
        // MCP のサーバーは 0.1〜5 秒ごとに問い合わせ直すので、待っている依頼がこれを超えることは無い。
        private const double AbandonedAfterSeconds = 60.0;

        /// <summary>
        /// 1 回の依頼にかける時間の上限(秒)。MCP のサーバーは IrocaRecolorTool の MaxPollSeconds(600 秒)で問い合わせを
        /// やめるので、その前に終えて結果を返す。AI の待ちはマスク操作・ゾーンごとに最大 180 秒
        /// (<see cref="AutoTuneService.EvidenceWaitSeconds"/>)なので、数が多いと積み上がって超え、結果が受け取られないまま
        /// 終わっていた。過ぎたら、マスク操作中ならエラー、自動調整中なら残りのゾーンを飛ばして仕上げる。
        /// </summary>
        internal const double RequestBudgetSeconds = 540.0;

        // EditMode テストだけが立てる(0 = 使わない)。期限で終える経路を短い時間で確かめるため。
        internal static double RequestBudgetSecondsForTests;

        private static double RequestBudget => RequestBudgetSecondsForTests > 0 ? RequestBudgetSecondsForTests : RequestBudgetSeconds;

        private sealed class RecolorJob {
            public RecolorRequest req;
            // 依頼か問い合わせを最後に受けた時刻(EditorApplication.timeSinceStartup)
            public double lastPolled;
            // 依頼を見分ける形(RecolorStatus(string) が問い合わせの依頼と比べる)
            public string requestKey;
            // 依頼を受けた時刻(依頼全体の期限 RequestBudgetSeconds を数える)
            public double startedAt;
            public bool OverBudget => EditorApplication.timeSinceStartup - startedAt > RequestBudget;
            public readonly RecolorTextureResult r = new RecolorTextureResult();
            public Texture2D tex;
            // 作業解像度の原本(ヒープの外。参照を 1 つ持ち、仕上げ・取り消しで返す。背景の仕上げは自分の参照を足す)。
            public NativePixels work;
            public int w, h;
            // 作業解像度で読んだ画素が、書き出しで読む元の解像度の画素と同じか(書き出しで読み直さなくてよいか)。
            public bool workIsFullResolution;
            public GameObject target;
            public IrocaRecipe recipe;
            // 登録で、依頼の時点の窓の中身(書き込み先と同じレシピの窓を土台にしたとき)とレシピの中身。
            // 待つ間にどちらかが編集されていたら、仕上げで上書きせずにやめる(EditedWhileWaiting)。
            public string windowJsonAtRequest;
            public string recipeJsonAtRequest;
            public IrocaSessionState state;
            public readonly List<string> changedZoneIds = new List<string>();
            public readonly List<AutomationMaskEdits.Edit> maskEditList = new List<AutomationMaskEdits.Edit>();
            public AutomationMaskEdits maskEdits;
            public AutomationAutoTune tune;
            // avatar・preview の仕上げの比較画像(作業解像度の色替え)を背景で計算しているもの
            public Task<Comparison> compare;
            // png の仕上げ(比較画像・元の解像度の色替え・PNG 化)を背景で計算しているもの
            public Task<PngExport> export;
            public readonly CancellationTokenSource cts = new CancellationTokenSource();
            // 終わった結果。問い合わせが来なくても update で仕上げまで進めて、ここに置いておく。
            public string finalJson;

            /// <summary>
            /// 依頼を終える(結果・エラーを返した・取り消した)ときに必ず呼ぶ。マスク操作と自動調整を止めて、それぞれが
            /// 足した原本の参照を返し、窓が無ければ AI の推論のワーカー(GPU のメモリ)も手放させ、自分の原本も返す。
            /// 何度呼んでもよい。以前はマスク操作のエラーで終わると自動調整の持つ参照が返らず、8K で 256MB が
            /// ファイナライザまで残っていた(窓の無い自動化は依頼の終わりに全部返す。architecture.md 4 章)。
            /// </summary>
            public void Finish() {
                maskEdits?.Cancel();
                tune?.Cancel();
                tune?.ReleaseAiIfNoWindow();
                maskEdits?.ReleaseAiIfNoWindow();
                ReleasePixels();
                // 終わった依頼は、結果が受け取られるまで(受け取られなければ次の依頼が来るまで)s_job に残る。残すのは結果
                // (finalJson)と、受け取られなかったときに結末を伝えるのに使うもの(r)だけにして、重いものは手放す:
                // 依頼の時点の窓とレシピの中身(マスク込みの JSON)・展開した編集状態・背景の計算の結果(比較画像の PNG など)・
                // マスク操作と自動調整。
                windowJsonAtRequest = null;
                recipeJsonAtRequest = null;
                state = null;
                maskEdits = null;
                tune = null;
                compare = null;
                export = null;
                maskEditList.Clear();
                tex = null;
                target = null;
                recipe = null;
            }

            public void Cancel() {
                cts.Cancel();
                if (export != null) {
                    bool fullResolution = workIsFullResolution;
                    export.ContinueWith(t => {
                        // 元の解像度の作業配列は、取り消した計算が返ってプールへ戻った後に手放す。
                        if (!fullResolution) {
                            PixelProcessor.ReleaseWorkBuffers();
                        }
                        // 受け取らなかった PNG(ヒープの外)はメインスレッドで手放す(書き込んだ後なら手放し済み)。
                        if (t.Status == TaskStatus.RanToCompletion) {
                            PreviewJobMainThread.Post(() => t.Result.DisposePng());
                        }
                    }, TaskScheduler.Default);
                }
                Finish();
            }

            // 作業解像度の原本を返す(何度呼んでもよい)。
            private void ReleasePixels() {
                var px = work;
                work = null;
                px?.Release();
            }
        }

        // update と問い合わせから呼ぶ。進められるところまで進め、終わったら結果を置いて update から外れる。
        private static void PumpRecolorJob() {
            var job = s_job;
            if (job == null) { Unhook(); return; }
            if (job.finalJson != null) {
                return;
            }
            try {
                string done = AdvanceRecolor(job);
                if (done == null) {
                    return;
                }
                job.finalJson = done;
                job.Finish();
            } catch (Exception ex) {
                job.Cancel();
                job.finalJson = FinishRecolor(job.r, $"{ex.GetType().Name}: {ex.Message}");
            }
            ReleaseMemoryIfNoWindow();
            Unhook();
        }

        private static void Unhook() {
            EditorApplication.update -= PumpRecolorJob;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelRecolorJob;
        }

        /// <summary>待っている依頼を捨てる(テストの後片付けにも使う)。</summary>
        internal static void CancelRecolorJob() {
            s_job?.Cancel();
            s_job = null;
            ReleaseMemoryIfNoWindow();
            Unhook();
        }

        // 窓があれば、作業配列は手を止めた 10 秒後に、色の表は窓を閉じるときに返る。窓が無いと次の計算があるとは限らないので、
        // 依頼が終わったらここで返す(背景の計算が残っていれば、それが終わったときに返す)。
        private static void ReleaseMemoryIfNoWindow() {
            if (!EditingSessionRegistry.Any) {
                NativeMemoryLifecycle.ReleaseWhenIdle();
            }
        }

        /// <summary>
        /// 進められるところまで進める(メインスレッド)。終わったら最後の結果、まだ待つなら null。
        /// マスク操作 → 自動調整 → 仕上げの順(自動調整はマスクで外した所を見ない)。
        /// 仕上げの色替えは重い(png は元の解像度の色替えと PNG 化で、16K 級では MCP の 30 秒の時間切れを超える。avatar・preview も
        /// 作業解像度の色替えで、8K ではメインスレッドが数秒止まった)ので、UI の書き出しと同じく計算を背景のスレッドへ回し、
        /// Unity の API(元の画素の読み込み・書き込み・取り込み・登録)だけをメインスレッドで行う。
        /// </summary>
        private static string AdvanceRecolor(RecolorJob job) {
            if (job.maskEdits != null) {
                job.maskEdits.Advance();
                if (job.maskEdits.Error != null) {
                    // 原本の参照と AI の後始末は、呼んだ側の RecolorJob.Finish が行う。
                    return FinishRecolor(job.r, job.maskEdits.Error);
                }
                if (!job.maskEdits.Done) {
                    if (job.OverBudget) {
                        return FinishRecolor(job.r, $"the mask operations did not finish within the time budget of this call "
                            + $"({RequestBudget:0} s; {job.maskEdits.Finished} of {job.maskEdits.Total} done, the AI may be slow on this "
                            + "computer), so nothing was applied. Call again with fewer mask operations, or use by:'island'.");
                    }
                    return null;
                }
            }
            if (job.tune != null && !job.tune.Done) {
                job.tune.Advance();
                if (!job.tune.Done && job.OverBudget) {
                    job.tune.SkipRemaining(RequestBudget);
                }
                if (!job.tune.Done) {
                    return null;
                }
                job.tune.ReleaseAiIfNoWindow();
            }
            job.maskEdits?.ReleaseAiIfNoWindow();
            if (job.r.apply != ApplyPng) {
                if (job.compare == null) {
                    StartCompare(job);
                    return null;
                }
                if (!job.compare.IsCompleted) {
                    return null;
                }
                return CompleteRecolor(job);
            }
            if (job.export == null) {
                string error = StartPngExport(job);
                return error != null ? FinishRecolor(job.r, error) : null;
            }
            if (!job.export.IsCompleted) {
                return null;
            }
            return CompletePngExport(job);
        }

        private static string PendingJson(RecolorJob job) {
            bool editing = job.maskEdits != null && !job.maskEdits.Done;
            bool tuning = job.tune != null && !job.tune.Done;
            var p = new RecolorPending {
                tunedZones = job.tune?.Finished ?? 0,
                zonesToTune = job.tune?.Total ?? 0,
                message = editing
                    ? "Applying the mask operations (the first time on a texture, the AI prepares the image, which can take a while). "
                      + "Call again to get the result."
                    : tuning
                    ? "Auto-tuning the zones (the first time on a texture, the AI prepares the image, which can take a while). "
                      + "Call again to get the result."
                    : job.r.apply == ApplyPng
                    ? "Writing the recolored PNG (a large texture takes a while). Call again to get the result."
                    : "Recoloring the texture for the preview image (a large texture takes a while). Call again to get the result.",
            };
            return JsonUtility.ToJson(p);
        }
    }
}
