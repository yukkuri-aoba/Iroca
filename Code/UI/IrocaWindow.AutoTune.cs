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
            if (_autoTuneJob.IsRunning) return;
            if (zone == null) return;

            _autoTuneIsManual = !auto;

            if (!ConfirmAutoTuneOverwriteIfNeeded(zone, auto))
                return;

            zone.EnsureId();

            // メインスレッド前処理: Texture2D.GetPixels32 と除外マスク構築は
            // バックグラウンドへ持ち込めないので、ここで配列化しておく。
            PrepareAutoTunePixels(auto, out Color32[] pixels, out int texW, out int texH);
            if (pixels == null)
            {
                // GetPixels32 が失敗（非 Readable / 一時例外）。null を背景ジョブへ渡すと
                // NullReference の生メッセージ通知になるので、読める文言で知らせて中止する。
                ShowNotification(new GUIContent(Localization.TextureReadError));
                return;
            }
            bool[] excluded = BuildCombinedExclusionForZone(zone, out int mw, out int mh);

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
        private void PrepareAutoTunePixels(bool auto, out Color32[] pixels, out int texW, out int texH)
        {
            pixels = null;
            texW = 0;
            texH = 0;
            var tex = sourceTexture;
            if (tex == null) return;

            try
            {
                if (!auto)
                    EditorUtility.DisplayProgressBar(Localization.AutoTune, Localization.AnalyzingTexture, 0.1f);
                if (_previewView != null &&
                    _previewView.TryGetTrueSourcePixels(tex, out pixels, out texW, out texH))
                    return;

                texW = tex.width;
                texH = tex.height;
                pixels = tex.GetPixels32();
            }
            catch (UnityEngine.UnityException) { pixels = null; }
            finally { if (!auto) EditorUtility.ClearProgressBar(); }
        }

        private void ScheduleAutoTuneJob(ColorZone zone, Color32[] pixels, int texW, int texH, bool[] excluded, int mw, int mh)
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
                    var result = ZoneAutoTuner.Analyze(pixels, texW, texH, zoneSnapshot, sessionSnapshot, excluded, mw, mh, ct,
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
                    ShowNotification(new GUIContent(Localization.AutoTuneDone));
                    Repaint();
                },
                onError: ex =>
                {
                    Debug.LogError($"[Iroca] Auto-tune failed: {ex.Message}\n{ex.StackTrace}");
                    ShowNotification(new GUIContent($"{Localization.Error}: {ex.Message}"));
                });
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
