// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    public partial class IrocaWindow
    {
        [System.NonSerialized] private readonly PreviewJob<ZoneAutoTuner.TuneResult> _autoTuneJob = new PreviewJob<ZoneAutoTuner.TuneResult>();
        [System.NonSerialized] private readonly PreviewJobProgress _autoTuneProgress = new PreviewJobProgress();
        // apply 時に zone を特定するための GUID。世代不一致で apply が破棄されるか、
        // ユーザーが zone を削除/追加した場合に備えて id で再ルックアップする。
        [System.NonSerialized] private string _autoTuneTargetZoneId;

        // 自動調整の実行種別。手動実行（ボタン）のときだけウィンドウ全体をブロックし
        // モーダル進捗を出す。かんたんモードの自動実行ではブロックせず裏で走らせる。
        [System.NonSerialized] private bool _autoTuneIsManual = true;
        // かんたんモードの自動調整デバウンス。サンプルカラーが変わってから一定時間
        // 落ち着いたら自動調整を 1 回だけ走らせる（カラーピッカーのドラッグ連発を間引く）。
        [System.NonSerialized] private string _pendingAutoTuneZoneId;
        [System.NonSerialized] private double _pendingAutoTuneTime;
        private const double AutoTuneDebounceSeconds = 0.4;

        // 証拠つき自動調整: スポイト位置の AI 提案セグメントを待っている状態。提案が届くか、
        // 期限切れ・取消で従来導出へ進む。待ち中の入力はジョブ開始時にそのまま渡す。
        [System.NonSerialized] private bool _evidenceWaiting;
        [System.NonSerialized] private double _evidenceDeadline;
        [System.NonSerialized] private string _evidenceZoneId;
        [System.NonSerialized] private Color32[] _evidencePixels;
        [System.NonSerialized] private int _evidenceTexW, _evidenceTexH, _evidenceMw, _evidenceMh;
        [System.NonSerialized] private bool[] _evidenceExcluded;
        // 証拠要求がまだ受理されていない(AI が準備中で Busy)。Tick で再要求する。
        [System.NonSerialized] private bool _evidencePending;
        // 証拠なしで解析するときの理由(由来の記録用。null = 証拠あり/理由なし)。
        [System.NonSerialized] private string _autoTuneConventionalReason;

        // ゾーン id → 最後に適用した自動調整の由来(証拠の有無・正規化の有無・導出診断)。
        // 再現データ書き出し(ReproDump)が meta.json に載せる。書き出された失敗ケースが
        // 「証拠経路を通ったのか、AI 未ウォームで従来導出だったのか」を後から判別できないと、
        // どちらの経路を直せばよいか分からない(2026-08-29: スニーカー明部クリックの再現データで
        // 証拠なし導出と byte 一致したが、証拠ありでも同じ結果になる位置だったため区別できなかった)。
        // テクスチャ固有の一時情報なので Undo/プリセットには載せない。
        [System.NonSerialized]
        private System.Collections.Generic.Dictionary<string, string> _autoTuneProvenance;

        internal string AutoTuneProvenance(string zoneId)
        {
            if (_autoTuneProvenance == null || string.IsNullOrEmpty(zoneId)) return "";
            return _autoTuneProvenance.TryGetValue(zoneId, out var s) ? s : "";
        }

        // 最後に適用した自動調整の「入力」(ゾーン id 毎)。再現データ書き出し(ReproDump)が
        // meta.json / zone{i}_evidence.png に載せ、dev_safe の test_repro_cases が同じ入力から
        // 導出をやり直す(ワンショットの再現)。書き出しの zones.json は自動調整「後」の値なので、
        // これが無いと「自動調整がどう導出したか」は後から再現できない(2026-09-02)。
        // テクスチャ固有の一時情報なので Undo/プリセットには載せない。
        internal sealed class AutoTuneInput
        {
            public Color sample;          // 導出に渡したサンプル色(正規化前のスポイト色)
            public Color target;
            public bool[] evidence;       // 証拠マスク(下原点。null=証拠なし導出)
            public int evW, evH;
            public int texW, texH;
            public bool maskUsed;         // 除外マスク(共通∪ゾーン別)を導出に渡したか
            public ZoneAutoTuner.TuneResult applied;
        }
        [System.NonSerialized]
        private System.Collections.Generic.Dictionary<string, AutoTuneInput> _autoTuneInputs;

        internal bool TryGetAutoTuneInput(string zoneId, out AutoTuneInput input)
        {
            input = null;
            if (_autoTuneInputs == null || string.IsNullOrEmpty(zoneId)) return false;
            return _autoTuneInputs.TryGetValue(zoneId, out input) && input != null;
        }

        // ゾーンのパラメータが、その自動調整の適用値のまま(手で触っていない)か。
        // 再現側はこれが真のときだけ「再導出した値 = 書き出しの値」を契約にできる。
        internal static bool AutoTuneParamsIntact(ColorZone zone, AutoTuneInput input)
        {
            if (zone == null || input == null) return false;
            var r = input.applied;
            static bool Eq(float a, float b) => Mathf.Abs(a - b) <= 1e-5f;
            var expectedSample = r.hasNormalizedSample ? r.normalizedSample : input.sample;
            int autoCount = r.autoSamples != null ? r.autoSamples.Count : 0;
            return Eq(zone.tolerance, r.tolerance)
                && Eq(zone.saturationStrictness, r.saturationStrictness)
                && Eq(zone.saturationGuard, r.saturationGuard)
                && Eq(zone.chromaThreshold, r.chromaThreshold)
                && zone.highlightRecovery == r.highlightRecovery
                && Eq(zone.valueBlend, r.valueBlend)
                && Eq(zone.edgeSoftness, r.edgeSoftness)
                && Eq(zone.shadowDesaturation, r.shadowDesaturation)
                && Eq(zone.shadowForgivenessSatMin, r.shadowForgivenessSatMin)
                && Eq(zone.sampleColor.r, expectedSample.r) && Eq(zone.sampleColor.g, expectedSample.g)
                && Eq(zone.sampleColor.b, expectedSample.b)
                && (zone.extraSamples != null ? zone.extraSamples.Count : 0) == autoCount;
        }
        // 証拠待ちの上限。埋め込み計算(テクスチャ毎 1 回)は CPU バックエンドの大きな
        // テクスチャで数十秒かかり得るので、短い期限で諦めて従来導出へ落とさない
        // (以前の 8 秒は「待たない方針」の名残。期限切れは中止して案内する)。
        private const double EvidenceWaitSeconds = 180.0;

        // 「パーツをユーザーが粗く囲った」情報を tolerance 導出に活用する。
        // マスク未使用なら null。
        private bool[] BuildCombinedExclusionForZone(ColorZone zone, out int mw, out int mh)
        {
            mw = _maskView != null ? _maskView.maskWidth : 0;
            mh = _maskView != null ? _maskView.maskHeight : 0;
            if (_maskView == null || mw <= 0 || mh <= 0) return null;

            bool[] common = _maskView.exclusionMask;
            bool[] zoneMask = null;
            if (!string.IsNullOrEmpty(zone.id) && _maskView.zoneMasks != null)
                _maskView.zoneMasks.TryGetValue(zone.id, out zoneMask);

            int len = mw * mh;
            bool commonOk = common != null && common.Length >= len;
            bool zoneOk = zoneMask != null && zoneMask.Length >= len;
            if (!commonOk && !zoneOk) return null;

            var combined = new bool[len];
            for (int i = 0; i < len; i++)
                combined[i] = (commonOk && common[i]) || (zoneOk && zoneMask[i]);
            return combined;
        }

        // 進行中の（古い色の）自動実行は破棄して、最新の色で取り直す。
        private void ScheduleAutoTune(ColorZone zone)
        {
            if (zone == null) return;
            zone.EnsureId();
            _pendingAutoTuneZoneId = zone.id;
            _pendingAutoTuneTime = EditorApplication.timeSinceStartup;
            if (_autoTuneJob.IsRunning && !_autoTuneIsManual)
                _autoTuneJob.Cancel();
            Repaint();
        }

        private void ProcessPendingAutoTune()
        {
            if (_pendingAutoTuneZoneId == null) return;
            if (_autoTuneJob.IsRunning) { Repaint(); return; }
            if (EditorApplication.timeSinceStartup - _pendingAutoTuneTime < AutoTuneDebounceSeconds)
            {
                Repaint();
                return;
            }

            string id = _pendingAutoTuneZoneId;
            _pendingAutoTuneZoneId = null;

            if (editMode != EditMode.Simple) return;
            var zone = FindZoneById(id);
            if (zone == null) return;
            if (sourceTexture == null || !IsReadable(sourceTexture)
                || zone.mode != SelectionMode.ColorPick || !zone.HasSampleColor)
                return;

            RunAutoTune(zone, auto: true);
        }

        private void RunAutoTune(ColorZone zone, bool auto = false)
        {
            if (_autoTuneJob.IsRunning || _evidenceWaiting) return;
            if (zone == null) return;

            _autoTuneIsManual = !auto;

            if (!ConfirmAutoTuneOverwriteIfNeeded(zone, auto))
                return;

            zone.EnsureId();

            // メインスレッド前処理: Texture2D.GetPixels32 と除外マスク構築は
            // バックグラウンドへ持ち込めないので、ここで配列化しておく。
            PrepareAutoTunePixels(auto, out Color32[] pixels, out int texW, out int texH, out bool trueSource);
            if (pixels == null)
            {
                // GetPixels32 が失敗（非 Readable / 一時例外）。null を背景ジョブへ渡すと
                // NullReference の生メッセージ通知になるので、読める文言で知らせて中止する。
                ShowNotification(new GUIContent(Localization.TextureReadError));
                return;
            }
            bool[] excluded = BuildCombinedExclusionForZone(zone, out int mw, out int mh);

            // 証拠つき導出: スポイト位置の AI 提案セグメントが届いてから解析を始める。
            // AI が無ければ中止して導入を案内し、準備中なら待つ(従来導出へは落とさない)。
            // 証拠なしで解析するのは、スポイト位置そのものが無いゾーン(カラーフィールド指定)だけ。
            switch (BeginAutoTuneEvidence(zone, pixels, texW, texH, excluded, mw, mh, trueSource))
            {
                case EvidenceStart.Waiting:
                    return;
                case EvidenceStart.NoPosition:
                    ShowNotification(new GUIContent(Localization.AutoTuneNoSamplePosition));
                    _autoTuneConventionalReason = "no-sample-position";
                    ScheduleAutoTuneJob(zone, pixels, texW, texH, excluded, mw, mh);
                    return;
                default:
                    return; // Unavailable: 通知・案内済み。解析は始めない。
            }
        }

        // ─── 上書き確認はジョブ開始“前”に行う（通常/上級モードの手動実行時のみ）───
        // 完了後にモーダルを出すと Editor がブロックされ、ユーザーの
        // 「他の作業がしたい」要望が満たされない。ラベルは pixels 解析に
        // 依存しない per-zone 判定なのでメインスレッドで先に確定できる。
        // かんたんモードでは詳細パラメータは自動管理（手で変更しない）なので、
        // 自動実行・手動実行ともに上書き確認は出さない。確認が要るのは通常/上級モードで
        // ユーザーが手調整した値を上書きする手動実行のときだけ。
        // 戻り値 false = ユーザーがキャンセル（呼び出し側は実行を中止する）。
        private bool ConfirmAutoTuneOverwriteIfNeeded(ColorZone zone, bool auto)
        {
            if (auto || editMode == EditMode.Simple) return true;

            var previewLabels = ZoneAutoTuner.PreviewOverwrittenLabels(zone);
            if (previewLabels.Count == 0) return true;

            // applyGlobals は事後判定だが、true になる条件下では globals は既に default
            // のため AutoTuneOverwriteBody の includesGlobals=true の差分は表示しない。
            string body = Localization.AutoTuneOverwriteBody(previewLabels, includesGlobals: false);
            return EditorUtility.DisplayDialog(
                Localization.AutoTuneConfirmTitle, body,
                Localization.OK, Localization.Cancel);
        }

        // 画素取得はメインスレッド必須で、大きいテクスチャでは一瞬フリーズする。
        // 完全な非同期化はできないため、手動実行のときだけモーダル進捗バーで「解析中」を
        // 明示し、無言の固まりに見えないようにする（バックグラウンド解析本体は別途
        // ウィンドウ内進捗バー＋キャンセルで表示される）。かんたんモードの自動実行では
        // 色を変えるたびにモーダルが点滅すると煩いので出さず、裏で静かに走らせる。
        //
        // 解析対象は選択・プレビュー・エクスポートと同じ true source（ディスク原本）を使う。
        // インポート済みテクスチャ（maxTextureSize 縮小・圧縮）を解析すると、彩度/距離分布が
        // 実処理経路とずれて導出パラメータが歪む。通常はプレビューが同じキャッシュを温めて
        // いるため追加コストはない。true source が取れないときのみ GetPixels32 へフォールバック。
        // trueSource: 画素がプレビュー/AI 提案と同じ true source キャッシュ（同一配列）か。
        // 証拠要求はこの配列とキーで SetSource するので、フォールバック画素では要求しない。
        private void PrepareAutoTunePixels(bool auto, out Color32[] pixels, out int texW, out int texH,
            out bool trueSource)
        {
            pixels = null;
            texW = 0;
            texH = 0;
            trueSource = false;
            var tex = sourceTexture;
            if (tex == null) return;

            try
            {
                if (!auto)
                    EditorUtility.DisplayProgressBar(Localization.AutoTune, Localization.AnalyzingTexture, 0.1f);
                if (_previewView != null &&
                    _previewView.TryGetTrueSourcePixels(tex, out pixels, out texW, out texH))
                {
                    trueSource = true;
                    return;
                }

                texW = tex.width;
                texH = tex.height;
                pixels = tex.GetPixels32();
            }
            catch (UnityEngine.UnityException) { pixels = null; }
            finally { if (!auto) EditorUtility.ClearProgressBar(); }
        }

        // evidence: 証拠マスク（true=このゾーンの素材そのもの。スポイト位置の AI 提案セグメント。
        // pixels と同じ下原点並び）。null なら従来導出。
        private void ScheduleAutoTuneJob(ColorZone zone, Color32[] pixels, int texW, int texH, bool[] excluded, int mw, int mh,
            bool[] evidence = null, int evW = 0, int evH = 0)
        {
            zone.EnsureId();
            _autoTuneTargetZoneId = zone.id;

            // 背景解析には live の zone / session を直接渡さず、値等価コピーを渡す。
            // かんたんモードの非ブロック実行では解析中も編集可能で、live を渡すと sampleColor 変異で
            // 混成サンプルから無意味な tolerance を導出したり、session.zones 列挙中の add/remove で
            // InvalidOperationException になり得る（プレビュー/エクスポート経路は既に Clone 済み）。
            var zoneSnapshot = zone.Clone();
            var sessionSnapshot = SnapshotSessionForAutoTune();

            _autoTuneProgress.Reset();
            _autoTuneProgress.Report(0.05f);

            _autoTuneJob.Schedule(
                work: ct =>
                {
                    _autoTuneProgress.Report(0.10f);
                    // ct を Analyze へ渡す。新しい自動調整が来て前ジョブが Cancel されたら、
                    // 各解析ステップ間で停止しゾンビ実行(CPU 2 倍/進捗バー飛び)を防ぐ。
                    // 解析の各ステップから進捗を受け取る。渡さないと 0.10 → 1.00 の 2 点しか
                    // 報告されず、進捗バーが 10% で固まってから完了に飛ぶ。
                    var result = evidence != null
                        ? ZoneAutoTuner.AnalyzeWithEvidence(pixels, texW, texH, zoneSnapshot, sessionSnapshot,
                            evidence, evW, evH, excluded, mw, mh, ct, p => _autoTuneProgress.Report(p))
                        : ZoneAutoTuner.Analyze(pixels, texW, texH, zoneSnapshot, sessionSnapshot, excluded, mw, mh, ct,
                            p => _autoTuneProgress.Report(p));
                    _autoTuneProgress.Report(1.0f);
                    return result;
                },
                apply: result =>
                {
                    // 実行中に削除・追加された可能性があるため、live zone を id で引き直す。
                    var targetZone = FindZoneById(_autoTuneTargetZoneId);
                    if (targetZone == null) return;

                    Undo.RegisterCompleteObjectUndo(this, "Auto-tune Zone");
                    // 導出結果 → ゾーン/グローバルの写像は TuneResult.ApplyTo が単一の正
                    // （headless ハーネスの --autotune と共有。ここに項目を並べ直さない）。
                    result.ApplyTo(targetZone, _session);
                    _autoTuneInputs ??= new System.Collections.Generic.Dictionary<string, AutoTuneInput>();
                    _autoTuneInputs[targetZone.id] = new AutoTuneInput
                    {
                        sample = zoneSnapshot.sampleColor, target = zoneSnapshot.targetColor,
                        evidence = evidence, evW = evW, evH = evH, texW = texW, texH = texH,
                        maskUsed = excluded != null, applied = result,
                    };
                    MarkPreviewDirty();
                    // 証拠が実際に導出に使われたか（汚染セグメント等で従来へ戻った場合は "fallback"）。
                    bool usedEvidence = !string.IsNullOrEmpty(result.evidenceDiag)
                        && !result.evidenceDiag.StartsWith("fallback");
                    _autoTuneProvenance ??= new System.Collections.Generic.Dictionary<string, string>();
                    string reason = _autoTuneConventionalReason;
                    _autoTuneConventionalReason = null;
                    _autoTuneProvenance[targetZone.id] =
                        (evidence != null ? (usedEvidence ? "evidence" : "evidence-fallback")
                                          : "conventional" + (reason != null ? "(" + reason + ")" : ""))
                        + $"; normalized={result.hasNormalizedSample}"
                        + (string.IsNullOrEmpty(result.evidenceDiag) ? "" : "; " + result.evidenceDiag);
                    if (MaskSuggestPerf.Enabled && result.evidenceDiag != null)
                        MaskSuggestPerf.Log($"自動調整の証拠: {result.evidenceDiag}");
                    ShowNotification(new GUIContent(
                        usedEvidence ? Localization.AutoTuneDoneWithEvidence : Localization.AutoTuneDone));
                    Repaint();
                },
                onError: ex =>
                {
                    Debug.LogError($"[Iroca] Auto-tune failed: {ex.Message}\n{ex.StackTrace}");
                    ShowNotification(new GUIContent($"{Localization.Error}: {ex.Message}"));
                });
        }

        // ─── 証拠つき自動調整（スポイト位置の AI 提案セグメントを教師にする） ───
        // 従来導出は「クリック 1 texel + 近傍窓」の当て推量で母集団を作るため、ハイライト
        // （明度↑彩度↓の非対称な逸脱）を原理的に取りこぼす。スポイトした位置に AI マスク提案
        // （SAM）を 1 回かけ、そのセグメントを ZoneAutoTuner.AnalyzeWithEvidence の証拠に渡す
        // （実測 42 ケース非退行、明部クリック IoU 0.15→0.82 等。ZoneAutoTuner.Evidence.cs 参照）。
        //
        // 2026-08-29 まで「AI が温まっていなければ待たず従来導出」だったが、その従来導出が
        // ハイライトを取りこぼす当の経路で、ユーザーには「自動調整が壊れる」としか見えなかった。
        // 今は AI が無ければ中止して導入を案内し(MaskSuggestSetupPrompt)、準備中なら届くまで待つ。
        private enum EvidenceStart { Waiting, NoPosition, Unavailable }

        private EvidenceStart BeginAutoTuneEvidence(ColorZone zone, Color32[] pixels, int texW, int texH,
            bool[] excluded, int mw, int mh, bool trueSource)
        {
            if (!zone.HasSampleUV) return EvidenceStart.NoPosition;
            if (!MaskSuggestSetupPrompt.Ready)
            {
                ShowNotification(new GUIContent(Localization.AutoTuneNeedsAi));
                MaskSuggestSetupPrompt.PromptIfNeeded(force: true);
                return EvidenceStart.Unavailable;
            }
            if (!trueSource || _maskView == null || _previewView == null || _maskView.SuggestController == null)
            {
                ShowNotification(new GUIContent(Localization.AutoTuneSourceNotShared));
                return EvidenceStart.Unavailable;
            }

            _evidenceZoneId = zone.id;
            _evidencePixels = pixels;
            _evidenceTexW = texW;
            _evidenceTexH = texH;
            _evidenceExcluded = excluded;
            _evidenceMw = mw;
            _evidenceMh = mh;
            _evidenceWaiting = true;
            _evidencePending = true;
            _evidenceDeadline = EditorApplication.timeSinceStartup + EvidenceWaitSeconds;
            _autoTuneProgress.Reset();
            _autoTuneProgress.Report(0.02f);
            EditorApplication.update -= TickEvidenceWait;
            EditorApplication.update += TickEvidenceWait;
            if (!TryStartEvidenceRequest()) return EvidenceStart.Unavailable;
            Repaint();
            return EvidenceStart.Waiting;
        }

        // 証拠要求を(再)試行する。Busy なら pending のまま(Tick が再試行)。
        // 戻り値 false = AI が使えない(待ちを畳んで通知済み)。
        private bool TryStartEvidenceRequest()
        {
            var zone = FindZoneById(_evidenceZoneId);
            var ctl = _maskView?.SuggestController;
            if (zone == null || ctl == null || !zone.HasSampleUV)
            {
                AbortEvidenceWait(Localization.AutoTuneEvidenceCancelled);
                return false;
            }
            var r = ctl.RequestEvidence(zone.sampleUV.x, zone.sampleUV.y, _evidencePixels, _evidenceTexW, _evidenceTexH,
                _previewView.TrueSourceCacheKey(), OnAutoTuneEvidence);
            switch (r)
            {
                case MaskSuggestController.EvidenceRequest.Started:
                    _evidencePending = false;
                    return true;
                case MaskSuggestController.EvidenceRequest.Busy:
                    return true;
                default:
                {
                    var svc = MaskSuggestBridge.Service;
                    string why = svc != null && svc.Phase == MaskSuggestPhase.Error && !string.IsNullOrEmpty(svc.ErrorMessage)
                        ? string.Format(Localization.AutoTuneAiError, svc.ErrorMessage)
                        : Localization.AutoTuneNeedsAi;
                    AbortEvidenceWait(why);
                    if (svc == null || svc.Phase == MaskSuggestPhase.NoModel)
                        MaskSuggestSetupPrompt.PromptIfNeeded(force: true);
                    return false;
                }
            }
        }

        // 提案の到着（null = 取消・破棄）。null なら中止(従来導出へは落とさない)。
        private void OnAutoTuneEvidence(MaskSuggestProposal proposal)
        {
            if (!_evidenceWaiting) return;
            var zone = FindZoneById(_evidenceZoneId);
            var pixels = _evidencePixels;
            int texW = _evidenceTexW, texH = _evidenceTexH;
            var excluded = _evidenceExcluded;
            int mw = _evidenceMw, mh = _evidenceMh;
            StopEvidenceWait();
            if (zone == null) return; // 待ち中に削除された

            if (proposal == null || proposal.maskBottomUp == null || proposal.width <= 0 || proposal.height <= 0)
            {
                ShowNotification(new GUIContent(Localization.AutoTuneEvidenceCancelled));
                Repaint();
                return;
            }
            // 背景まで広がった提案（floodWarning）は素材の証拠にならない。この場合だけは証拠なしで
            // 解析する(AI は使えたが証拠が取れなかった。理由は通知と由来に残す)。
            if (proposal.floodWarning)
            {
                ShowNotification(new GUIContent(Localization.AutoTuneEvidenceUnusable));
                _autoTuneConventionalReason = "evidence-flood";
                ScheduleAutoTuneJob(zone, pixels, texW, texH, excluded, mw, mh);
                return;
            }
            ScheduleAutoTuneJob(zone, pixels, texW, texH, excluded, mw, mh,
                proposal.maskBottomUp, proposal.width, proposal.height);
        }

        private void TickEvidenceWait()
        {
            // 破棄済みウィンドウ（fake-null）や待ち終了後は自己解除する。
            if (this == null || !_evidenceWaiting)
            {
                EditorApplication.update -= TickEvidenceWait;
                return;
            }
            if (_evidencePending)
            {
                if (!TryStartEvidenceRequest()) return;
                // 準備の進み(モデルロード・埋め込み計算)を進捗バーに映す。
                var svc = MaskSuggestBridge.Service;
                float prep = svc != null ? Mathf.Clamp01(svc.Progress) : 0f;
                _autoTuneProgress.Report(0.02f + 0.28f * prep);
                Repaint();
            }
            if (EditorApplication.timeSinceStartup < _evidenceDeadline) return;
            // 期限切れ: 中止して案内する（後から届いた提案はコントローラが捨てる）。
            AbortEvidenceWait(Localization.AutoTuneEvidenceTimeout);
        }

        // 証拠待ちを畳んで通知する(解析は始めない)。
        private void AbortEvidenceWait(string message)
        {
            _maskView?.SuggestControllerIfCreated?.CancelEvidence();
            StopEvidenceWait();
            ShowNotification(new GUIContent(message));
            Repaint();
        }

        // 証拠待ちの中止（ユーザーの中止・ウィンドウ無効化/破棄）。解析は始めない。
        private void CancelEvidenceWait()
        {
            if (!_evidenceWaiting) return;
            _maskView?.SuggestControllerIfCreated?.CancelEvidence();
            StopEvidenceWait();
        }

        private void StopEvidenceWait()
        {
            _evidenceWaiting = false;
            _evidencePending = false;
            _evidencePixels = null;
            _evidenceExcluded = null;
            _evidenceZoneId = null;
            EditorApplication.update -= TickEvidenceWait;
        }

        // 背景の ZoneAutoTuner.Analyze へ渡す session のスナップショット。Analyze が読むのは
        // antiAliasCleanup / useDecontamination / zones のみなので、それらを値コピーし、zones は
        // リストごとクローンして列挙中の構造変更（InvalidOperationException）を断つ。
        private IrocaSessionState SnapshotSessionForAutoTune()
        {
            var snap = new IrocaSessionState
            {
                antiAliasCleanup   = _session.antiAliasCleanup,
                useDecontamination = _session.useDecontamination,
            };
            snap.zones.Clear();
            if (_session.zones != null)
            {
                foreach (var z in _session.zones)
                    if (z != null) snap.zones.Add(z.Clone());
            }
            return snap;
        }
    }
}
