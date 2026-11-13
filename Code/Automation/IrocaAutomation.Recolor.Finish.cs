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
    // iroca_recolor のうち、仕上げ(比較画像・アバターへの登録・PNG の書き出し)。
    // 入口(Recolor・RecolorStatus)と依頼の寿命は IrocaAutomation.Recolor.cs。
    public static partial class IrocaAutomation {
        /// <summary>
        /// avatar・preview の仕上げの比較画像(作業解像度の色替え)を背景で始める(メインスレッド)。終わったら
        /// <see cref="CompleteRecolor"/> が受け取る。png は <see cref="StartPngExport"/>。
        /// </summary>
        private static void StartCompare(RecolorJob job) {
            var state = job.state;
            foreach (var z in state.zones) {
                if (z != null) {
                    z.UpdateCacheIfNeeded();
                }
            }
            var work = job.work.AddRef();   // 背景の比較が読む間の参照
            var ct = job.cts.Token;
            NativeMemoryLifecycle.WorkStarted();   // 手を止めたときの縮小と、全部返す予約がこの計算を待つように
            // 取り消しのトークンは Task.Run に渡さない(始まる前に取り消されると中身が走らず、参照を返せない)。
            job.compare = Task.Run(() => {
                try {
                    ct.ThrowIfCancellationRequested();
                    return Compare(work, state, ct, keepAfter: false, out _);
                } finally {
                    work.Release();
                    NativeMemoryLifecycle.WorkEnded();
                }
            });
        }

        // avatar・preview の仕上げの残り(メインスレッド): 比較画像を受け取り、登録まで行って、最後の結果を返す。
        private static string CompleteRecolor(RecolorJob job) {
            var r = job.r;
            var state = job.state;
            var tex = job.tex;
            var task = job.compare;
            if (task.IsCanceled) {
                return FinishRecolor(r, "the recolor was cancelled.");
            }
            if (task.IsFaulted) {
                var ex = task.Exception?.GetBaseException();
                if (ex is OperationCanceledException) {
                    return FinishRecolor(r, "the recolor was cancelled.");
                }
                return FinishRecolor(r, $"{ex?.GetType().Name}: {ex?.Message}");
            }
            FillResult(job, task.Result);

            // ── 反映 ──
            if (r.apply == ApplyAvatar) {
                // 自動調整を待つ間に Play に入っていたら登録しない(依頼の時点と同じ判定)。
                string blocked = AvatarApplyBlocked(EditorApplication.isPlayingOrWillChangePlaymode, NdmfStateForApply);
                if (blocked != null) {
                    return FinishRecolor(r, blocked);
                }
                // ここへ来るのは、途中経過を返して待った後だけ(最初の呼び出しは、比較画像の計算を始めた所で途中経過を返す)。
                string edited = EditedWhileWaiting(job);
                if (edited != null) {
                    return FinishRecolor(r, $"while this call was waiting, {edited}, so the result was not applied "
                        + "(to keep those edits). Call iroca_recolor again to apply it on top of the current state.");
                }
                // 待つ間に登録先が消えた・そのシーンが閉じられた、テクスチャが消えたなら、レシピを作る前に断る。作ってから気づくと
                // (登録の計画が消えたオブジェクトを読んで例外になる)、どこにも登録されないレシピのアセットが Assets に残る。
                if (job.target == null || !RecipeRegistration.IsSceneObject(job.target)) {
                    return FinishRecolor(r, $"'{r.avatar}' was deleted or its scene was closed while this call was waiting, "
                        + "so nothing was registered. Call iroca_inspect to pick a target again.");
                }
                if (tex == null) {
                    return FinishRecolor(r, $"the texture '{r.texture}' was deleted while this call was waiting, so nothing was registered.");
                }
                // 書き込み先と同じレシピ(どちらも無いときを含む)を表示している窓だけ、書いた後に読み直す。別のレシピを
                // 表示している窓は、そちらへ保存するので古い内容で上書きする心配は無く、読み直すと窓の未保存の編集を捨てて
                // 書き込み先へ結び替えてしまう。
                bool reloadWindow = EditingWindows.Current.IsShowing(tex, out var shownRecipe) && shownRecipe == job.recipe;
                // 新しく作るレシピは Undo に積まない(作ったアセットのファイルは Undo で消せない。UI の登録と同じ)。
                // Ctrl+Z ではコンポーネントへの登録が外れるので、使われないレシピが残るだけになる。
                // 既存のレシピの書き換えは、下の Undo グループに記録してから書く。
                var recipe = job.recipe;
                if (recipe == null) {
                    recipe = RecipeStore.Create(tex, state);
                }
                r.recipe = AssetDatabase.GetAssetPath(recipe);

                // UI の登録と同じ: 書き出した画像を差しているマテリアルを元へ戻し、コンポーネントに足す(Undo 1 回)。
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(UndoName);
                // 戻すのは .mat そのもの。登録先の外(開いているシーン)でも使われているマテリアルは、ほかのアバターまで
                // 元の色に戻るので書き換えない(窓の登録と違い、確認画面が無い)。パス付きで知らせる。
                var plan = RegistrationService.Describe(job.target, tex, recipe);
                int switched = RegistrationService.SwitchAndAttach(plan, recipe, keepShared: true, out _);
                if (switched > 0) {
                    r.warnings.Add($"switched {switched} material(s) from the exported image back to the source texture. "
                        + "The material files themselves were changed, so other scenes or prefabs that use them also show the source texture.");
                }
                // 消えたレシピ(Missing)の欄は外さない(確認画面が無い。窓の登録は確認を取って外す)。ビルドはそこで止まる。
                if (plan.lostSlots > 0) {
                    r.warnings.Add($"'{r.avatar}' has {plan.lostSlots} recipe entr{(plan.lostSlots == 1 ? "y" : "ies")} whose recipe asset "
                        + "no longer exists (Missing). They were left as they are, and uploading stops on them until the recipe "
                        + "assets are restored (for example with Git) or the entries are removed from the Iroca Recolor component "
                        + "(registering from the Iroca window removes them after asking).");
                }
                foreach (var m in plan.shared) {
                    string label = AssetDatabase.GetAssetPath(m);
                    if (string.IsNullOrEmpty(label)) {
                        label = m.name;
                    }
                    r.warnings.Add($"left {label} on the exported image: it is also used outside '{r.avatar}' in the open scenes, "
                        + "and switching it back would change those objects too. The recolor does not apply to it until it uses "
                        + "the source texture (switch it in the Inspector, or give this avatar its own material).");
                }
                if (recipe == job.recipe) {
                    // RecipeStore.Save は(窓の自動保存と二重にならないよう)Undo に積まないので、書く前の中身をここで
                    // 記録する。窓を閉じていても Ctrl+Z でレシピの中身まで戻る(以前は窓で開いているときだけ戻った)。
                    Undo.RegisterCompleteObjectUndo(recipe, UndoName);
                    RecipeStore.Save(recipe, state);
                }
                // 今の版で色替えして登録したので、今の版の仕上がりで確かめたことにする(中身が同じでも。窓の登録と同じ)。
                RecipeStore.MarkOutputChecked(recipe);
                // 窓の読み直し(読み直す前の窓を記録する)も同じグループに入れ、1 回の Ctrl+Z でレシピと窓がそろって戻るようにする。
                if (reloadWindow) {
                    EditingWindows.Current.ReloadFromRecipe(recipe, UndoName);
                }
                // Attach が付ける名前(Iroca: Register Recolor)ではなく、外からの色替えだと分かる名前にする。
                Undo.SetCurrentGroupName(UndoName);
                Undo.CollapseUndoOperations(undoGroup);
            }
            return FinishRecolor(r, null);
        }

        /// <summary>
        /// 登録の依頼が自動調整を待つ間に、書き込み先のレシピか、書いた後に読み直す窓の中身が編集されていたら、その説明。
        /// 無ければ null。以前は、待つ間に窓でした手直しが「依頼の時点の内容と AI の変更」で置き換わっていた
        /// (Ctrl+Z で手直しは戻るが、そのときは AI の変更が消える)。編集されていたら上書きしないでやめ、AI に
        /// 呼び直してもらう(呼び直せば今の内容を土台にする)。
        /// </summary>
        private static string EditedWhileWaiting(RecolorJob job) {
            bool windowIsTarget = EditingWindows.Current.IsShowing(job.tex, out var shown) && shown == job.recipe;
            if (job.windowJsonAtRequest != null) {
                // 依頼の時点では、書き込み先と同じレシピの窓の中身を土台にした。窓の自動保存がその内容をレシピへ書くことは
                // あるので、レシピではなく窓の中身を比べる(閉じた・切り替えたときは、閉じる前の手直しを確かめられない)。
                if (!windowIsTarget) {
                    return "the Iroca window was closed or switched to another texture or recipe";
                }
                return JsonUtility.ToJson(EditingWindows.Current.EditingStateOf(job.tex)) != job.windowJsonAtRequest
                    ? "the texture was edited in the Iroca window" : null;
            }
            if (job.recipe != null && job.recipe.sessionJson != job.recipeJsonAtRequest) {
                return "the recipe was changed (for example, edited in the Iroca window)";
            }
            if (windowIsTarget) {
                // 待つ間に窓で開かれた。窓の中身が土台(レシピ)と同じでなければ、読み直しで窓の手直しを置き換えてしまう。
                string expected = job.recipe != null ? JsonUtility.ToJson(RecipeStore.Load(job.recipe)) : null;
                if (expected == null || JsonUtility.ToJson(EditingWindows.Current.EditingStateOf(job.tex)) != expected) {
                    return "the texture was opened and edited in the Iroca window";
                }
            }
            return null;
        }

        /// <summary>png の仕上げで背景のスレッドが作るもの。</summary>
        private sealed class PngExport {
            public Comparison comparison;
            // ヒープの外。受け取った側(CompletePngExport・取り消し)が手放す。
            public NativeArray<byte> png;

            // メインスレッドから呼ぶ。何度呼んでもよい。
            public void DisposePng() {
                if (png.IsCreated) {
                    png.Dispose();
                }
                png = default;
            }
        }

        /// <summary>
        /// png の仕上げを始める(メインスレッド)。取り込みで縮めて編集していたときは、UI の書き出しと同じく元の解像度で
        /// 読み直す(デコードは Unity の API なのでここで)。比較画像・色替え・PNG 化は背景のスレッドで行う。
        /// 始められなければ理由、始めたら null。
        /// </summary>
        private static unsafe string StartPngExport(RecolorJob job) {
            var state = job.state;
            foreach (var z in state.zones) {
                if (z != null) {
                    z.UpdateCacheIfNeeded();
                }
            }
            NativePixels full = null;
            if (!job.workIsFullResolution) {
                // 「適用して保存」と同じ読み方(ExportService。PNG/JPG はファイルを直接、ほかは取り込み済みテクスチャ)。
                string srcAbs = PhysicalPath(job.r.texture);
                if (srcAbs == null) {
                    return "failed to read the source texture at its original resolution.";
                }
                var fullKind = ExportPipeline.ReadSource(srcAbs, job.tex, workingSize: false, out full, out string openError);
                if (!ExportPipeline.HasPixels(fullKind)) {
                    return fullKind == ExportPipeline.SourceKind.Locked ? UnreadablePixels(fullKind, openError)
                        : "failed to read the source texture at its original resolution.";
                }
            }
            var work = job.work.AddRef();   // 背景の仕上げが読む間の参照
            var ct = job.cts.Token;
            NativeMemoryLifecycle.WorkStarted();   // 手を止めたときの縮小と、全部返す予約がこの計算を待つように
            // 取り消しのトークンは Task.Run に渡さない(始まる前に取り消されると中身が走らず、参照を返せない)。
            job.export = Task.Run(() => {
                NativePixels after = null;
                try {
                    ct.ThrowIfCancellationRequested();
                    // 作業解像度の画素が元の解像度の画素そのもの(IsFullResolution)なら、比較画像のために色替えした
                    // 画素が書き出す画素(同じ画素に同じ編集状態で SessionRecolor.Apply)。色替えをもう一度しない。
                    var result = new PngExport { comparison = Compare(work, state, ct, keepAfter: full == null, out after) };
                    if (full == null) {
                        result.png = ExportService.EncodePng(after);
                    } else {
                        // 変化の指標は作業解像度で数えてあるので、元の解像度では色替えだけ。
                        SessionRecolor.Apply(full.Ptr, full.Width, full.Height, state, ct);
                        ct.ThrowIfCancellationRequested();
                        result.png = ExportService.EncodePng(full);
                    }
                    return result;
                } finally {
                    // 画素は書き出した後は要らない(8K で 256MB ずつ)。
                    after?.Release();
                    full?.Release();
                    work.Release();
                    NativeMemoryLifecycle.WorkEnded();
                }
            });
            return null;
        }

        // png の仕上げの残り(メインスレッド): 結果を受け取り、書き込んで取り込む。
        private static string CompletePngExport(RecolorJob job) {
            // 元の解像度で読み直して色替えしたときは、その寸法の作業配列(8K で GB 単位)を OS へ返す
            // (UI の書き出しと同じ。作業解像度のままなら、窓のプレビューと同じ寸法なので手放さない)。
            if (!job.workIsFullResolution) {
                PixelProcessor.ReleaseWorkBuffersInBackground();
            }
            var r = job.r;
            var task = job.export;
            if (task.IsCanceled) {
                return FinishRecolor(r, "the recolor was cancelled.");
            }
            if (task.IsFaulted) {
                var ex = task.Exception?.GetBaseException();
                if (ex is OperationCanceledException) {
                    return FinishRecolor(r, "the recolor was cancelled.");
                }
                return FinishRecolor(r, $"{ex?.GetType().Name}: {ex?.Message}");
            }
            var export = task.Result;
            try {
                FillResult(job, export.comparison);
                // 待つ間に、出力先へ別の書き出しがされていることがある。書く直前にもう一度確かめる(依頼の時点と同じ判定)。
                string outError = ResolvePngOutput(job.req, r.texture, out string outAbs);
                if (outError != null) {
                    return FinishRecolor(r, outError);
                }
                // 取り込み設定は元から引き継ぐ(窓の書き出しの既定と同じ。既定の設定だと Max Size 2048 などで粗くなる)。
                ExportService.WriteAndImport(outAbs, export.png, PhysicalPath(r.texture), inheritImportSettings: true);
                r.output = outAbs;
                return FinishRecolor(r, null);
            } finally { export.DisposePng(); }
        }

        private const string UndoName = "Iroca: Recolor (automation)";
    }
}
