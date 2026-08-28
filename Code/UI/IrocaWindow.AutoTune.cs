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
        // 温まった AI(埋め込み計算済み)のデコードはズーム再推論込みでも数秒かからない。
        // これを大きく超える待ちは何かが詰まっているので、従来導出で進めて体験を守る。
        private const double EvidenceWaitSeconds = 8.0;

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

            // 証拠つき導出: スポイト位置の AI 提案セグメントが取れる状況なら、届いてから解析を
            // 始める。取れない状況（Sentis/モデル不在・埋め込み未計算・カラーフィールド指定で
            // 位置なし）は従来どおり即開始する。
            if (TryRequestAutoTuneEvidence(zone, pixels, texW, texH, excluded, mw, mh, trueSource))
                return;
            ScheduleAutoTuneJob(zone, pixels, texW, texH, excluded, mw, mh);
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
                    // スポイト位置の正規化: クリックした 1 texel がツヤや深い影でも、パーツの
                    // 代表地色を基準色に据え直す。以降の選択・再着色がクリック位置に依存しなくなる
                    // （スウォッチの色も代表地色へ変わるので、何が基準かが UI から見て分かる）。
                    if (result.hasNormalizedSample)
                        targetZone.sampleColor         = result.normalizedSample;
                    targetZone.tolerance               = result.tolerance;
                    targetZone.saturationStrictness    = result.saturationStrictness;
                    targetZone.saturationGuard         = result.saturationGuard;
                    targetZone.chromaThreshold         = result.chromaThreshold;
                    targetZone.highlightRecovery       = result.highlightRecovery;
                    targetZone.valueBlend              = result.valueBlend;
                    targetZone.edgeSoftness            = result.edgeSoftness;
                    targetZone.shadowDesaturation      = result.shadowDesaturation;
                    targetZone.shadowForgivenessSatMin = result.shadowForgivenessSatMin;
                    // ユーザーが複数スポイトする代わりに、アルゴリズムがパーツの濃淡を自動取得した結果。
                    // 選択（マッチング）の和集合に使われ、出力色は主サンプル基準のまま変わらない。
                    targetZone.extraSamples = result.autoSamples ?? new System.Collections.Generic.List<Color>();
                    if (result.applyGlobals)
                    {
                        antiAliasCleanup   = result.antiAliasCleanup;
                        useDecontamination = result.useDecontamination;
                    }
                    MarkPreviewDirty();
                    // 証拠が実際に導出に使われたか（汚染セグメント等で従来へ戻った場合は "fallback"）。
                    bool usedEvidence = !string.IsNullOrEmpty(result.evidenceDiag)
                        && !result.evidenceDiag.StartsWith("fallback");
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
        // 待たない方針: AI が温まっていない（モデル未ロード・埋め込み未計算・提案処理中）ときは
        // 要求せず従来導出で即進める。埋め込み計算は裏で始まり、次回の自動調整から効く。
        private bool TryRequestAutoTuneEvidence(ColorZone zone, Color32[] pixels, int texW, int texH,
            bool[] excluded, int mw, int mh, bool trueSource)
        {
            if (!trueSource || !zone.HasSampleUV) return false;
            if (_maskView == null || _previewView == null || !MaskSuggestBridge.Available) return false;
            var ctl = _maskView.SuggestController;
            if (ctl == null) return false;
            if (!ctl.RequestEvidence(zone.sampleUV.x, zone.sampleUV.y, pixels, texW, texH,
                    _previewView.TrueSourceCacheKey(), OnAutoTuneEvidence))
                return false;

            _evidenceZoneId = zone.id;
            _evidencePixels = pixels;
            _evidenceTexW = texW;
            _evidenceTexH = texH;
            _evidenceExcluded = excluded;
            _evidenceMw = mw;
            _evidenceMh = mh;
            _evidenceWaiting = true;
            _evidenceDeadline = EditorApplication.timeSinceStartup + EvidenceWaitSeconds;
            _autoTuneProgress.Reset();
            _autoTuneProgress.Report(0.02f);
            EditorApplication.update -= TickEvidenceWait;
            EditorApplication.update += TickEvidenceWait;
            Repaint();
            return true;
        }

        // 提案の到着（null = 取消・破棄）。どちらでも解析へ進む（証拠の有無だけが違う）。
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

            // 背景まで広がった提案（floodWarning）は素材の証拠にならないので渡さない
            // （AnalyzeWithEvidence の汚染判定でも弾かれるが、使えないと分かっているものは渡さない）。
            bool usable = proposal != null && proposal.maskBottomUp != null
                && proposal.width > 0 && proposal.height > 0 && !proposal.floodWarning;
            if (usable)
                ScheduleAutoTuneJob(zone, pixels, texW, texH, excluded, mw, mh,
                    proposal.maskBottomUp, proposal.width, proposal.height);
            else
                ScheduleAutoTuneJob(zone, pixels, texW, texH, excluded, mw, mh);
        }

        private void TickEvidenceWait()
        {
            // 破棄済みウィンドウ（fake-null）や待ち終了後は自己解除する。
            if (this == null || !_evidenceWaiting)
            {
                EditorApplication.update -= TickEvidenceWait;
                return;
            }
            if (EditorApplication.timeSinceStartup < _evidenceDeadline) return;
            // 期限切れ: 証拠は諦めて従来導出で進める（後から届いた提案はコントローラが捨てる）。
            _maskView?.SuggestControllerIfCreated?.CancelEvidence();
            OnAutoTuneEvidence(null);
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
