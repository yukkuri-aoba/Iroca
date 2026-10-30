// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    public unsafe partial class IrocaWindow
    {
        // 実行中の自動調整(証拠待ちと解析。null = していない)。流れは AutoTuneService が持ち、ここは上書きの確認・
        // 進み具合・通知・Undo・由来の記録(ウィンドウだけのもの)。
        [System.NonSerialized] private AutoTuneService.Run _autoTune;
        // 最後に描いた進み具合(%)と段階。変わったときだけ描き直す(EditorApplication.update は毎回描き直すには速すぎる)。
        [System.NonSerialized] private int _autoTuneShownPct = -1;
        [System.NonSerialized] private bool _autoTuneShownPreparing;

        // ゾーン id → 最後に適用した自動調整の由来(AutoTuneService.Run.Provenance: 証拠の有無・正規化の有無・導出診断)。
        // 再現データ書き出し(ReproDump)が meta.json に載せる。テクスチャ固有の一時情報なので Undo/プリセットには載せない。
        [System.NonSerialized]
        private Dictionary<string, string> _autoTuneProvenance;

        internal string AutoTuneProvenance(string zoneId)
        {
            if (_autoTuneProvenance == null || string.IsNullOrEmpty(zoneId)) return "";
            return _autoTuneProvenance.TryGetValue(zoneId, out var s) ? s : "";
        }

        // 最後に適用した自動調整の「入力」(ゾーン id 毎。AutoTuneService.Input の説明)。
        [System.NonSerialized]
        private Dictionary<string, AutoTuneService.Input> _autoTuneInputs;

        internal bool TryGetAutoTuneInput(string zoneId, out AutoTuneService.Input input)
        {
            input = null;
            if (_autoTuneInputs == null || string.IsNullOrEmpty(zoneId)) return false;
            return _autoTuneInputs.TryGetValue(zoneId, out input) && input != null;
        }

        // セッションに無いゾーン(削除した・別のテクスチャに切り替えた)の入力を捨てる。プレビュー生成ごとに
        // 選択キャッシュと同じ id の集合で刈る。削除を Undo で戻したゾーンは入力が無い状態になり、
        // ドメインリロードの後と同じく、再現データに載らず次の自動調整では上書きの確認が出る(安全側)。
        internal void RetainAutoTuneInputs(ICollection<string> liveZoneIds)
        {
            if (_autoTuneInputs == null || _autoTuneInputs.Count == 0) return;
            List<string> gone = null;
            foreach (var id in _autoTuneInputs.Keys)
                if (!liveZoneIds.Contains(id)) (gone ??= new List<string>()).Add(id);
            if (gone == null) return;
            foreach (var id in gone) _autoTuneInputs.Remove(id);
        }

        // 「パーツをユーザーが粗く囲った」情報を tolerance 導出に活用する。
        // マスク未使用なら null。
        private bool[] BuildCombinedExclusionForZone(ColorZone zone, out int mw, out int mh)
        {
            mw = _maskView != null ? _maskView.maskWidth : 0;
            mh = _maskView != null ? _maskView.maskHeight : 0;
            if (_maskView == null || mw <= 0 || mh <= 0) return null;
            return _maskView.Workspace.CombinedExclusion(zone.id);
        }

        private void RunAutoTune(ColorZone zone)
        {
            if (_autoTune != null || zone == null) return;

            if (!ConfirmAutoTuneOverwriteIfNeeded(zone))
                return;

            zone.EnsureId();

            // メインスレッド前処理: 原本の画素と除外マスクはバックグラウンドへ持ち込めないので、ここで用意する。
            PrepareAutoTunePixels(out NativePixels pixels, out bool trueSource);
            if (pixels == null)
            {
                // GetPixels32 が失敗（非 Readable / 一時例外）。null を背景ジョブへ渡すと
                // NullReference の生メッセージ通知になるので、読める文言で知らせて中止する。
                ShowNotification(new GUIContent(Localization.TextureReadError));
                return;
            }
            // pixels の参照はここで持ち、最後に返す(自動調整は自分の参照を足す)。
            try
            {
                bool[] excluded = BuildCombinedExclusionForZone(zone, out int mw, out int mh);
                // 証拠の元のキーは、画素がプレビュー・右クリックの AI 提案と同じ原本のときだけ渡す
                // (フォールバックの画素で同じキーを使うと、別の画素の埋め込みと取り違える)。
                string key = trueSource && _previewView != null ? _previewView.TrueSourceCacheKey() : null;
                var run = AutoTuneService.Start(zone, _session, pixels, key, excluded, mw, mh);
                if (run.Status == AutoTuneService.Status.Failed)
                {
                    NotifyAutoTuneFailure(run);
                    return;
                }
                if (run.ConventionalReason == AutoTuneService.ReasonNoSamplePosition)
                    ShowNotification(new GUIContent(Localization.AutoTuneNoSamplePosition));
                _autoTune = run;
                _autoTuneShownPct = -1;
                EditorApplication.update -= TickAutoTune;
                EditorApplication.update += TickAutoTune;
                Repaint();
            }
            finally { pixels.Release(); }
        }

        /// <summary>
        /// 導出パラメータが「ユーザーの手調整を含まない」状態か(<see cref="AutoTuneService.UntouchedByUser"/>。
        /// 自動調整済みかどうかは、このウィンドウが持つ最後の入力で見る)。
        /// </summary>
        private bool ZoneParamsUntouchedByUser(ColorZone zone)
            => AutoTuneService.UntouchedByUser(zone, TryGetAutoTuneInput(zone?.id, out var input) ? input : null);

        // ─── 上書き確認はジョブ開始“前”に行う ───
        // 完了後にモーダルを出すと Editor がブロックされ、ユーザーの
        // 「他の作業がしたい」要望が満たされない。ラベルは pixels 解析に
        // 依存しない per-zone 判定なのでメインスレッドで先に確定できる。
        // 確認が要るのは、ユーザーが手調整した値を上書きするときだけ。
        // 旧かんたんモード（Simple）は詳細パラメータを自動管理する前提なので確認を出さない
        // （モード切替 UI は廃止したが、古いセッションファイルに Simple が残り得るので判定は残す）。
        // 戻り値 false = ユーザーがキャンセル（呼び出し側は実行を中止する）。
        private bool ConfirmAutoTuneOverwriteIfNeeded(ColorZone zone)
        {
            if (editMode == EditMode.Simple) return true;
            // 失って困るのは「ユーザーが手で入れた値」だけ。新規ゾーンの初期値のままでも、
            // 直前の自動調整が入れた値のままでも確認は要らない（どちらも手調整ではないのに
            // 毎回ダイアログが出て、押すたびに一手増えていた）。
            if (ZoneParamsUntouchedByUser(zone)) return true;

            var overwritten = ZoneAutoTuner.PreviewOverwrittenSettings(zone);
            if (overwritten.Count == 0) return true;

            // applyGlobals は事後判定だが、true になる条件下では globals は既に default
            // のため AutoTuneOverwriteBody の includesGlobals=true の差分は表示しない。
            string body = Localization.AutoTuneOverwriteBody(overwritten, includesGlobals: false);
            return EditorUtility.DisplayDialog(
                Localization.AutoTuneConfirmTitle, body,
                Localization.OK, Localization.Cancel);
        }

        // 画素取得はメインスレッド必須で、大きいテクスチャでは一瞬フリーズする。
        // 完全な非同期化はできないため、モーダル進捗バーで「解析中」を明示し、
        // 無言の固まりに見えないようにする（バックグラウンド解析本体は別途
        // ウィンドウ内進捗バー＋キャンセルで表示される）。
        //
        // 解析対象は選択・プレビューと同じ true source（ディスク原本を作業解像度にそろえたもの）を使う。
        // インポート済みテクスチャ（圧縮で劣化）を解析すると、彩度/距離分布が実処理経路とずれて
        // 導出パラメータが歪む。通常はプレビューが同じキャッシュを温めて
        // いるため追加コストはない。true source が取れないときのみ GetPixels32 へフォールバック。
        // trueSource: 画素がプレビュー/AI 提案と同じ true source キャッシュ（同一の画素）か。
        // pixels は呼び出し側が参照を 1 つ持つ(使い終わったら Release する)。
        private void PrepareAutoTunePixels(out NativePixels pixels, out bool trueSource)
        {
            pixels = null;
            trueSource = false;
            var tex = sourceTexture;
            if (tex == null) return;

            try
            {
                EditorUtility.DisplayProgressBar(Localization.AutoTune, Localization.AnalyzingTexture, 0.1f);
                if (_previewView != null &&
                    _previewView.TryGetTrueSourcePixels(tex, out var shared, out _, out _))
                {
                    pixels = shared.AddRef();
                    trueSource = true;
                    return;
                }

                pixels = NativePixels.CopyOf(tex.GetPixels32(), tex.width, tex.height);
            }
            catch (UnityEngine.UnityException) { pixels = null; }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // EditorApplication.update から毎フレーム呼ばれる。Unity はそこで例外を受け止めないので、投げたまま
        // 購読を残すと、窓を閉じるまで毎フレーム、後ろに並んだ他のツールの更新まで飛ばし続ける。
        // 例外のときは自動調整をやめて購読を外す。
        private void TickAutoTune()
        {
            try { TickAutoTuneCore(); }
            catch (System.Exception e)
            {
                EditorApplication.update -= TickAutoTune;
                Debug.LogException(e);
                try
                {
                    CancelAutoTune();
                    ShowNotification(new GUIContent(Localization.AutoTuneEvidenceCancelled));
                }
                catch (System.Exception e2) { Debug.LogException(e2); }
            }
        }

        private void TickAutoTuneCore()
        {
            var run = _autoTune;
            // 破棄済みウィンドウ（fake-null）や終わった後は自己解除する。
            if (this == null || run == null)
            {
                EditorApplication.update -= TickAutoTune;
                return;
            }
            run.Advance();
            switch (run.Status)
            {
                case AutoTuneService.Status.Done:
                    StopAutoTuneTick();
                    ApplyAutoTune(run);
                    return;
                case AutoTuneService.Status.Failed:
                    StopAutoTuneTick();
                    NotifyAutoTuneFailure(run);
                    return;
            }
            // 準備の進み(モデルロード・埋め込み計算)と解析の進みを進捗バーに映す。
            int pct = Mathf.RoundToInt(run.Progress * 100f);
            if (pct != _autoTuneShownPct || run.EvidencePreparing != _autoTuneShownPreparing)
            {
                _autoTuneShownPct = pct;
                _autoTuneShownPreparing = run.EvidencePreparing;
                Repaint();
            }
        }

        private void StopAutoTuneTick()
        {
            _autoTune = null;
            EditorApplication.update -= TickAutoTune;
        }

        private void ApplyAutoTune(AutoTuneService.Run run)
        {
            // 実行中に削除・追加された可能性があるため、live zone を id で引き直す。
            var targetZone = FindZoneById(run.ZoneId);
            if (targetZone == null) return;

            // 結果は押してから数秒後に届くので、待っている間の別の操作とは別の「元に戻す」1 回分にする。
            SeparateUndo.Record("Auto-tune Zone", () =>
            {
                Undo.RegisterCompleteObjectUndo(this, "Auto-tune Zone");
                run.ApplyTo(targetZone, _session);
            });
            (_autoTuneInputs ??= new Dictionary<string, AutoTuneService.Input>())[targetZone.id] = run.ToInput();
            Editing.NotifyChanged(EditChange.Content);
            (_autoTuneProvenance ??= new Dictionary<string, string>())[targetZone.id] = run.Provenance;
            if (MaskSuggestPerf.Enabled && run.Result.evidenceDiag != null)
                MaskSuggestPerf.Log($"自動調整の証拠: {run.Result.evidenceDiag}");
            ShowNotification(new GUIContent(
                run.UsedEvidence ? Localization.AutoTuneDoneWithEvidence : Localization.AutoTuneDone));
            Repaint();
        }

        // 始められなかった・途中で終わった理由を知らせる(解析は始めない。従来導出へは落とさない)。
        private void NotifyAutoTuneFailure(AutoTuneService.Run run)
        {
            switch (run.Failure)
            {
                case AutoTuneService.Failure.AiUnavailable:
                    // AI が無ければ中止して導入を案内する。
                    ShowNotification(new GUIContent(Localization.AutoTuneNeedsAi));
                    MaskSuggestSetupPrompt.PromptIfNeeded();
                    break;
                case AutoTuneService.Failure.AiError:
                    ShowNotification(new GUIContent(string.Format(Localization.AutoTuneAiError, run.FailureDetail)));
                    break;
                case AutoTuneService.Failure.SourceNotShared:
                    ShowNotification(new GUIContent(Localization.AutoTuneSourceNotShared));
                    break;
                case AutoTuneService.Failure.Timeout:
                    ShowNotification(new GUIContent(Localization.AutoTuneEvidenceTimeout));
                    break;
                case AutoTuneService.Failure.AnalysisFailed:
                {
                    var ex = run.Error;
                    Debug.LogError($"[Iroca] Auto-tune failed: {ex?.Message}\n{ex?.StackTrace}");
                    ShowNotification(new GUIContent($"{Localization.Error}: {ex?.Message}"));
                    break;
                }
                case AutoTuneService.Failure.Cancelled:
                    break;   // 押した人が止めた
                default:
                    ShowNotification(new GUIContent(Localization.AutoTuneEvidenceCancelled));
                    break;
            }
            Repaint();
        }

        // 自動調整の中止（ユーザーの中止・ウィンドウ無効化/破棄）。証拠待ちなら解析は始めない。
        private void CancelAutoTune()
        {
            var run = _autoTune;
            if (run == null) return;
            StopAutoTuneTick();
            run.Cancel();
        }

        // テクスチャの切り替え・レシピの読み直し: 待っている証拠は前の元のものなので、待ちをやめて知らせる
        // (前はコントローラが右クリックの提案と一緒に取り消していた)。解析が始まっていれば、そのまま当てる
        // (当て先は id で引き直すので、別のテクスチャのゾーンには当たらない)。
        private void AbortEvidenceWaitForSourceChange()
        {
            if (_autoTune == null || _autoTune.Status != AutoTuneService.Status.WaitingForEvidence) return;
            CancelAutoTune();
            ShowNotification(new GUIContent(Localization.AutoTuneEvidenceCancelled));
            Repaint();
        }
    }
}
