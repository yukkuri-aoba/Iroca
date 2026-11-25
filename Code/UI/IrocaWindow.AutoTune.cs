// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    public unsafe partial class IrocaWindow {
        // 実行中の自動調整(証拠待ちと解析。null = していない)。流れは AutoTuneService が持ち、ここは上書きの確認・
        // 進み具合・通知・Undo・由来の記録(ウィンドウだけのもの)。
        [System.NonSerialized] private AutoTuneService.Run _autoTune;
        // 最後に描いた進み具合(%)と段階。変わったときだけ描き直す(EditorApplication.update は毎回描き直すには速すぎる)。
        [System.NonSerialized] private int _autoTuneShownPct = -1;
        [System.NonSerialized] private bool _autoTuneShownPreparing;

        /// <summary>
        /// スポイトのクリックで始めた自動調整の当て先(<see cref="_autoTune"/> と対。null = 「自動調整」ボタンの自動調整か、走っていない)。
        /// 待つ間はゾーンを足さず・色も替えず(塗らない)、結果が届いたら追加・色・調整の値・外す範囲を「元に戻す」1 回分で書く
        /// (<see cref="ApplyPickAutoTune"/>)。取り消したら何も書かない(クリックの前のまま)。自動調整できなかったら、クリックした
        /// ときに自動調整しない場合と同じに塗る(<see cref="FinishPickWithoutTune"/>)。
        /// </summary>
        private sealed class PickTune {
            // 新しいゾーン(まだリストに無い。色と位置を入れたもの)。取り直しなら null。
            public ColorZone newZone;
            // 取り直しの当て先のゾーンの id(新しいゾーンなら null)。
            public string zoneId;
            // スポイトで取った色と位置。
            public Color picked;
            public Vector2 uv;
        }

        [System.NonSerialized] private PickTune _pickTune;

        // 自動調整の漏れの確かめで回した選択を、当てた後のプレビュー(確定段)へ引き渡す先(AutoTuneService.Run.HandOverSelection)。
        // 始めたときのプレビューの選択キャッシュとその世代・原本(参照は足さない。同じものかを比べるだけ)。原本がプレビューの原本
        // そのもの(trueSource)のときだけ作る。当てるときに 1 回だけ使う(_autoTune と対。取り消し・失敗では捨てる)。
        private sealed class SelectionHandoff {
            public SelectionCache cache;
            public int epoch;
            public NativePixels source;
        }

        [System.NonSerialized] private SelectionHandoff _autoTuneHandoff;

        // 引き渡す先を控える(始める前に呼ぶ。世代は選択を作り始める前に読む)。原本がプレビューの原本でなければ null。
        private SelectionHandoff PlanSelectionHandoff(bool trueSource, NativePixels pixels) {
            var cache = trueSource ? _previewView?.Pipeline?.Selection : null;
            if (cache == null) {
                return null;
            }
            return new SelectionHandoff { cache = cache, epoch = cache.Epoch, source = pixels };
        }

        // 当てた後(ゾーンの値と外す範囲を書いた後、次のプレビューの作り直しより前)に、漏れの確かめで回した選択をプレビューの
        // 選択キャッシュへ引き渡す。今もプレビューが同じキャッシュ・同じ原本・同じ寸法で塗るときだけ(違えば何もしない)。
        // 世代と原本のハッシュは Core(SelectionCache.TryAdopt)が確かめ、使うかは塗る側の鍵と除外の比べ直しが決める。
        private void HandOverAutoTuneSelection(AutoTuneService.Run run) {
            var h = _autoTuneHandoff;
            _autoTuneHandoff = null;
            var pipeline = _previewView?.Pipeline;
            if (h == null || pipeline == null || !ReferenceEquals(pipeline.Selection, h.cache)
                || !ReferenceEquals(pipeline.TrueSource, h.source)
                || pipeline.TrueSourceW != run.TextureWidth || pipeline.TrueSourceH != run.TextureHeight) {
                return;
            }
            run.HandOverSelection(h.cache, h.epoch);
        }

        // スポイトでクリックしたら、その場で自動調整までかけてから塗るか(Preferences の「いろか」。既定はオン。2026-11-23 ユーザー決定)。
        // オフなら、クリックしてすぐ新しいゾーンの許容範囲の初期値で塗る(「自動調整」はボタンで)。
        private static bool? s_autoTuneOnPick;

        internal static bool AutoTuneOnPick {
            get => s_autoTuneOnPick ??= EditorPrefs.GetBool(IrocaPrefs.AutoTuneOnPick, true);
            set {
                s_autoTuneOnPick = value;
                EditorPrefs.SetBool(IrocaPrefs.AutoTuneOnPick, value);
            }
        }

        /// <summary>走っている自動調整(テストが進めるため。null = 走っていない)。</summary>
        internal AutoTuneService.Run RunningAutoTune => _autoTune;

        /// <summary>走っている自動調整が、スポイトのクリックで始めたものか(まだ何も当てていない)。</summary>
        internal bool PickAutoTunePending => _pickTune != null;

        // ゾーン id → 最後に適用した自動調整の由来(AutoTuneService.Run.Provenance: 証拠の有無・正規化の有無・導出診断)。
        // 再現データ書き出し(ReproDump)が meta.json に載せる。テクスチャ固有の一時情報なので Undo/プリセットには載せない。
        [System.NonSerialized]
        private Dictionary<string, string> _autoTuneProvenance;

        internal string AutoTuneProvenance(string zoneId) {
            if (_autoTuneProvenance == null || string.IsNullOrEmpty(zoneId)) {
                return "";
            }
            return _autoTuneProvenance.TryGetValue(zoneId, out var s) ? s : "";
        }

        // 最後に適用した自動調整の「入力」(ゾーン id 毎。AutoTuneService.Input の説明)。
        [System.NonSerialized]
        private Dictionary<string, AutoTuneService.Input> _autoTuneInputs;

        internal bool TryGetAutoTuneInput(string zoneId, out AutoTuneService.Input input) {
            input = null;
            if (_autoTuneInputs == null || string.IsNullOrEmpty(zoneId)) {
                return false;
            }
            return _autoTuneInputs.TryGetValue(zoneId, out input) && input != null;
        }

        // セッションに無いゾーン(削除した・別のテクスチャに切り替えた)の入力を捨てる。プレビュー生成ごとに
        // 選択キャッシュと同じ id の集合で刈る。削除を Undo で戻したゾーンは入力が無い状態になり、
        // ドメインリロードの後と同じく、再現データに載らず次の自動調整では上書きの確認が出る(安全側)。
        // 由来(_autoTuneProvenance)も同じ id の集合で刈る(以前は一度も刈らず、別のテクスチャのゾーンの由来が残った。点検 自-8)。
        internal void RetainAutoTuneInputs(ICollection<string> liveZoneIds) {
            PruneByLiveIds(_autoTuneInputs, liveZoneIds);
            PruneByLiveIds(_autoTuneProvenance, liveZoneIds);
        }

        private static void PruneByLiveIds<T>(Dictionary<string, T> records, ICollection<string> liveZoneIds) {
            if (records == null || records.Count == 0) {
                return;
            }
            List<string> gone = null;
            foreach (var id in records.Keys) {
                if (!liveZoneIds.Contains(id)) {
                    (gone ??= new List<string>()).Add(id);
                }
            }
            if (gone == null) {
                return;
            }
            foreach (var id in gone) {
                records.Remove(id);
            }
        }

        // 自動調整の入力と由来はテクスチャ固有。テクスチャの読み込み・リセット・Undo でのテクスチャの復元で空にする
        // (プリセットやレシピの複製では別のテクスチャも同じ id のゾーンを持つので、id の刈り込みでは落ちない)。
        private void ForgetAutoTuneRecords() {
            _autoTuneInputs?.Clear();
            _autoTuneProvenance?.Clear();
        }

        // 「パーツをユーザーが粗く囲った」情報を tolerance 導出に活用する。
        // マスク未使用なら null。
        private NativeBools BuildCombinedExclusionForZone(ColorZone zone, out int mw, out int mh) {
            mw = _maskView != null ? _maskView.maskWidth : 0;
            mh = _maskView != null ? _maskView.maskHeight : 0;
            if (_maskView == null || mw <= 0 || mh <= 0) {
                return null;
            }
            return _maskView.Workspace.CombinedExclusion(zone.id);
        }

        // internal は WindowUndoTests が走っている途中の Undo を確かめるため。
        internal void RunAutoTune(ColorZone zone) {
            if (_autoTune != null || zone == null) {
                return;
            }

            if (!ConfirmAutoTuneOverwriteIfNeeded(zone)) {
                return;
            }

            zone.EnsureId();

            // メインスレッド前処理: 原本の画素と除外マスクはバックグラウンドへ持ち込めないので、ここで用意する。
            PrepareAutoTunePixels(out NativePixels pixels, out bool trueSource);
            if (pixels == null) {
                // GetPixels32 が失敗（非 Readable / 一時例外）。null を背景ジョブへ渡すと
                // NullReference の生メッセージ通知になるので、読める文言で知らせて中止する。
                ShowNotification(new GUIContent(Localization.TextureReadError));
                return;
            }
            // pixels の参照はここで持ち、最後に返す(自動調整は自分の参照を足す)。
            try {
                // 証拠の元のキーは、画素がプレビュー・右クリックの AI 提案と同じ原本のときだけ渡す
                // (フォールバックの画素で同じキーを使うと、別の画素の埋め込みと取り違える)。
                string key = trueSource && _previewView != null ? _previewView.TrueSourceCacheKey() : null;
                // 除外マスクとマスクのスナップショットの持ち主は自動調整になる(終わったら返す)。スナップショットは、漏れの確かめで
                // 導いた値の選択をプレビューと同じ設定・マスクで回すのに使う。
                NativeBools excluded = BuildCombinedExclusionForZone(zone, out int mw, out int mh);
                var handoff = PlanSelectionHandoff(trueSource, pixels);
                var run = AutoTuneService.Start(zone, pixels, key, excluded, mw, mh,
                    RecolorSettings.From(Session), BuildMaskSnapshot(), keepSelection: handoff != null);
                if (run.Status == AutoTuneService.Status.Failed) {
                    NotifyAutoTuneFailure(run);
                    return;
                }
                _autoTuneHandoff = handoff;
                if (run.ConventionalReason == AutoTuneService.ReasonNoSamplePosition) {
                    ShowNotification(new GUIContent(Localization.AutoTuneNoSamplePosition));
                }
                _autoTune = run;
                _autoTuneShownPct = -1;
                EditorApplication.update -= TickAutoTune;
                EditorApplication.update += TickAutoTune;
                Repaint();
            } finally { pixels.Release(); }
        }

        // ─────────────── スポイトのクリックで自動調整する(2026-11-23) ───────────────
        // 「スポイトで変えたい色を選ぶ / 別の色を追加」のクリックと、既にあるゾーンのスポイトの取り直しで、自動調整まで済ませてから
        // 1 回で塗る。流れ(証拠待ち → 解析 → 漏れの確かめ)は「自動調整」ボタンと同じ AutoTuneService、待つ間の見せ方・取り消し・
        // 後始末も同じ(_autoTune を共有する)。違うのは当て方だけ: 待つ間は何も書かず、届いたら追加・色・値・外す範囲を
        // 「元に戻す」1 回分で書く。上書きの確認(モーダル)は出さない(取り直しで調整し直すのは、手で触っていないゾーンだけ)。
        // AI が無い・失敗・期限切れのときは、知らせを出して自動調整しないときと同じに塗る(モーダルの導入案内は出さない)。
        // ハーネスの --oneshot・--script と自動化(MCP)は、今までどおり「クリック → 塗り先 → 自動調整」の順で呼ぶ。この順の違いで
        // 変わるのは模様保持だけで、塗り先を変えたときに導き直す(Core の ZoneEdits.SetTarget)。

        /// <summary>
        /// 「スポイトで変えたい色を選ぶ / 別の色を追加」のクリック(<see cref="RequestNewZoneFromSample"/> の後で呼ぶ)。設定が
        /// オンで AI が使えれば自動調整を始め(ゾーンは結果が届いてから足す)、そうでなければすぐにゾーンを足す。
        /// </summary>
        internal void AddZoneByEyedropper(Color picked, Vector2 uv) {
            if (StartPickAutoTune(null, picked, uv)) {
                RequestRepaint();
                return;
            }
            SetCurrentZoneIndex(Editing.AddZoneFromSample(picked, uv));
            zonesFoldout = true;
            // 別窓(プレビュー・マスク編集)も描き直す(新しいゾーンの見本・カーソル)。
            RequestRepaint();
        }

        /// <summary>
        /// 既にあるゾーンのスポイトのクリック。色か位置が変わり、そのゾーンの調整の値を利用者が手で触っていなければ
        /// (<see cref="ZoneParamsUntouchedByUser"/>)、取り直した色で自動調整し直す(結果が届くまで色も替えない)。
        /// そうでなければ、今までどおり色と位置だけを替える。
        /// </summary>
        internal void ApplyEyedropperSample(ColorZone zone, Color picked, Vector2 uv) {
            if (zone == null) {
                return;
            }
            bool changes = ZoneEdits.SampleColorChanges(zone, picked) || zone.sampleUV != uv;
            if (changes && zone.mode == SelectionMode.ColorPick && ZoneParamsUntouchedByUser(zone)
                && StartPickAutoTune(zone, picked, uv)) {
                RequestRepaint();
                return;
            }
            Editing.ApplySample(zone, picked, uv);
        }

        // スポイトのクリックの自動調整を始める。始めたら true(結果は TickAutoTune が当てる)。設定がオフ・ほかの自動調整が
        // 走っている・始められなかったときは false で、呼び出し側が自動調整しないときと同じに塗る(始められなかった理由は知らせる)。
        private bool StartPickAutoTune(ColorZone existing, Color picked, Vector2 uv) {
            if (!AutoTuneOnPick || _autoTune != null || SourceTexture == null) {
                return false;
            }
            bool repick = existing != null;
            // AI(Sentis とモデル)が無ければ待たない。導入の案内は窓の上の帯が出している。
            if (!MaskSuggestBridge.Available || !MaskSuggestSection.ToolReady) {
                NotifyPickWithoutTune(Localization.AutoTuneNeedsAi, repick);
                return false;
            }

            // 調整するゾーン: 新しいゾーンはリストに足さずに持ち、取り直しは今のゾーンの写しへ取った色を入れる(どちらも、今
            // スポイトで塗ったら出来るゾーンと同じ。規則は Core の ZoneEdits)。
            ColorZone tuneZone;
            if (repick) {
                existing.EnsureId();
                tuneZone = existing.Clone();
                ZoneEdits.ApplySample(tuneZone, picked, uv, ZoneEdits.SampleColorChanges(tuneZone, picked));
            } else {
                tuneZone = Editing.NewZoneFromSample(picked, uv);
            }

            PrepareAutoTunePixels(out NativePixels pixels, out bool trueSource);
            if (pixels == null) {
                NotifyPickWithoutTune(Localization.TextureReadError, repick);
                return false;
            }
            try {
                string key = trueSource && _previewView != null ? _previewView.TrueSourceCacheKey() : null;
                NativeBools excluded = BuildCombinedExclusionForZone(tuneZone, out int mw, out int mh);
                var handoff = PlanSelectionHandoff(trueSource, pixels);
                var run = AutoTuneService.Start(tuneZone, pixels, key, excluded, mw, mh,
                    RecolorSettings.From(Session), BuildMaskSnapshot(), keepSelection: handoff != null);
                if (run.Status == AutoTuneService.Status.Failed) {
                    NotifyPickWithoutTune(PickSkipReason(run), repick);
                    return false;
                }
                _autoTune = run;
                _autoTuneHandoff = handoff;
                _pickTune = new PickTune {
                    newZone = repick ? null : tuneZone,
                    zoneId = repick ? existing.id : null,
                    picked = picked,
                    uv = uv,
                };
                _autoTuneShownPct = -1;
                EditorApplication.update -= TickAutoTune;
                EditorApplication.update += TickAutoTune;
                return true;
            } finally { pixels.Release(); }
        }

        /// <summary>
        /// スポイトのボタンを押したとき(設定がオンなら)、クリックの後の待ちを短くするため、AI のモデルの読み込みと元の画像の
        /// 埋め込みの計算を先に始める。AI が無い・エラー中なら何もしない(知らせも出さない)。
        /// </summary>
        internal void WarmAiForPick() {
            if (!AutoTuneOnPick || _previewView == null || SourceTexture == null) {
                return;
            }
            var svc = MaskSuggestBridge.Service;
            if (svc == null || svc.Phase == MaskSuggestPhase.NoModel || svc.Phase == MaskSuggestPhase.Error) {
                return;
            }
            _previewView.WarmAiSource(SourceTexture);
        }

        // 届いた自動調整の結果を、スポイトのクリックの当て先へ書く(「元に戻す」1 回分: 新しいゾーンの追加・取った色と位置・
        // 調整の値・外す範囲)。
        private void ApplyPickAutoTune(AutoTuneService.Run run, PickTune pick) {
            try { ApplyPickAutoTuneCore(run, pick); } finally { run.ReleaseLeakExclusion(); }
        }

        private void ApplyPickAutoTuneCore(AutoTuneService.Run run, PickTune pick) {
            bool add = pick.newZone != null;
            var zone = add ? pick.newZone : FindZoneById(pick.zoneId);
            if (zone == null) {
                return;   // 取り直しの間にゾーンが消えた(Undo・テクスチャの切り替えでは待ちごと止めるので、ふつうは来ない)
            }
            NativeBools leak = null;
            if (run.HasLeakExclusion && _maskView != null) {
                _maskView.EnsureMasks();
                leak = run.LeakRegionFor(_maskView.Workspace, SourceTexture);
            }
            bool leakApplied = false;
            MaskRegionEditResult leakResult = default;
            int index = -1;
            string undoName = add ? "Add Zone" : "Sample Color";
            try {
                // 結果は押してから数秒後に届くので、待っている間の別の操作とは別の「元に戻す」1 回分にする(SeparateUndo)。
                // 先にマスクの今の中身を版にしてから文書を記録する(後で同じ 1 回分の中にゾーンの「塗らない」を書くため。
                // ApplyAutoTuneCore と同じ理由)。外す範囲は、ゾーンがリストに入ってから書く(書き先をゾーンの id で引く)。
                SeparateUndo.Record(undoName, () => {
                    if (add) {
                        index = Editing.AddPreparedZone(zone, undoName);
                    } else {
                        Editing.RecordMaskEdit(undoName);
                        ZoneEdits.ApplySample(zone, pick.picked, pick.uv, ZoneEdits.SampleColorChanges(zone, pick.picked));
                    }
                    if (leak != null) {
                        var dest = new MaskPaintView.MaskDestination(zone.id, MaskRegionOp.DontPaintHere);
                        leakApplied = _maskView.ApplyRegion(leak, dest, out leakResult);
                    }
                    run.ApplyTo(zone);
                });
            } finally {
                leak?.Release();
            }
            HandOverAutoTuneSelection(run);
            RecordAppliedAutoTune(run, zone, leakApplied, leakResult);
            if (index >= 0) {
                SetCurrentZoneIndex(index);
                zonesFoldout = true;
            }
            RequestRepaint();
        }

        // スポイトのクリックの自動調整ができなかった(取り消し以外)。自動調整しないときと同じに塗り、理由を知らせる。
        private void FinishPickWithoutTune(AutoTuneService.Run run, PickTune pick) {
            if (run.Failure == AutoTuneService.Failure.Cancelled) {
                return;   // 取り消した: 何も当てない(クリックの前のまま)
            }
            if (run.Failure == AutoTuneService.Failure.AnalysisFailed) {
                var ex = run.Error;
                Debug.LogError($"[Iroca] Auto-tune failed: {ex?.Message}\n{ex?.StackTrace}");
            }
            bool repick = pick.newZone == null;
            // 待っている間の別の操作とは別の「元に戻す」1 回分にする。
            SeparateUndo.Record(repick ? "Sample Color" : "Add Zone", () => {
                if (repick) {
                    var zone = FindZoneById(pick.zoneId);
                    if (zone != null) {
                        Editing.ApplySample(zone, pick.picked, pick.uv);
                    }
                } else {
                    SetCurrentZoneIndex(Editing.AddZoneFromSample(pick.picked, pick.uv));
                    zonesFoldout = true;
                }
            });
            NotifyPickWithoutTune(PickSkipReason(run), repick);
            RequestRepaint();
        }

        // 自動調整できなかった理由(知らせの 1 行目)。「自動調整」ボタンの知らせと同じ文言を使う。
        private static string PickSkipReason(AutoTuneService.Run run) {
            switch (run.Failure) {
                case AutoTuneService.Failure.AiUnavailable:
                    return Localization.AutoTuneNeedsAi;
                case AutoTuneService.Failure.AiError:
                    return string.Format(Localization.AutoTuneAiError, run.FailureDetail);
                case AutoTuneService.Failure.SourceNotShared:
                    return Localization.AutoTuneSourceNotShared;
                case AutoTuneService.Failure.Timeout:
                    return Localization.AutoTuneEvidenceTimeout;
                case AutoTuneService.Failure.AnalysisFailed:
                    return $"{Localization.Error}: {run.Error?.Message}";
                default:
                    return Localization.AutoTuneEvidenceCancelled;
            }
        }

        private void NotifyPickWithoutTune(string reason, bool repick) {
            string painted = repick
                ? Localization.PickAutoTuneSkippedRepick
                : string.Format(Localization.PickAutoTuneSkippedFormat,
                    EditingSession.NewZoneInitialTolerance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            ShowNotification(new GUIContent(reason + "\n" + painted));
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
        private bool ConfirmAutoTuneOverwriteIfNeeded(ColorZone zone) {
            if (editMode == EditMode.Simple) {
                return true;
            }
            // 失って困るのは「ユーザーが手で入れた値」だけ。新規ゾーンの初期値のままでも、
            // 直前の自動調整が入れた値のままでも確認は要らない（どちらも手調整ではないのに
            // 毎回ダイアログが出て、押すたびに一手増えていた）。
            if (ZoneParamsUntouchedByUser(zone)) {
                return true;
            }

            var overwritten = AutoTuneService.UserOverwrittenSettings(zone);
            if (overwritten.Count == 0) {
                return true;
            }

            string body = Localization.AutoTuneOverwriteBody(overwritten);
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
        private void PrepareAutoTunePixels(out NativePixels pixels, out bool trueSource) {
            pixels = null;
            trueSource = false;
            var tex = SourceTexture;
            if (tex == null) {
                return;
            }

            try {
                EditorUtility.DisplayProgressBar(Localization.AutoTune, Localization.AnalyzingTexture, 0.1f);
                if (_previewView != null &&
                    _previewView.TryGetTrueSourcePixels(tex, out var shared, out _, out _)) {
                    pixels = shared.AddRef();
                    trueSource = true;
                    return;
                }

                pixels = TextureIO.IsReadable(tex) ? WorkingResolution.NativePixelsOf(tex, null) : null;
            } catch (UnityEngine.UnityException) { pixels = null; } finally { EditorUtility.ClearProgressBar(); }
        }

        // EditorApplication.update から毎フレーム呼ばれる。Unity はそこで例外を受け止めないので、投げたまま
        // 購読を残すと、窓を閉じるまで毎フレーム、後ろに並んだ他のツールの更新まで飛ばし続ける。
        // 例外のときは自動調整をやめて購読を外す。internal は EditMode テストが、偽物の AI で進めた自動調整を当てさせるため。
        internal void TickAutoTune() {
            try { TickAutoTuneCore(); } catch (System.Exception e) {
                EditorApplication.update -= TickAutoTune;
                Debug.LogException(e);
                try {
                    CancelAutoTune();
                    ShowNotification(new GUIContent(Localization.AutoTuneEvidenceCancelled));
                } catch (System.Exception e2) { Debug.LogException(e2); }
            }
        }

        private void TickAutoTuneCore() {
            var run = _autoTune;
            // 破棄済みウィンドウ（fake-null）や終わった後は自己解除する。
            if (this == null || run == null) {
                EditorApplication.update -= TickAutoTune;
                return;
            }
            run.Advance();
            var pick = _pickTune;
            switch (run.Status) {
                case AutoTuneService.Status.Done:
                    StopAutoTuneTick();
                    if (pick != null) {
                        ApplyPickAutoTune(run, pick);
                    } else {
                        ApplyAutoTune(run);
                    }
                    return;
                case AutoTuneService.Status.Failed:
                    StopAutoTuneTick();
                    _autoTuneHandoff = null;
                    if (pick != null) {
                        FinishPickWithoutTune(run, pick);
                    } else {
                        NotifyAutoTuneFailure(run);
                    }
                    return;
            }
            // 準備の進み(モデルロード・埋め込み計算)と解析の進みを進捗バーに映す。
            int pct = Mathf.RoundToInt(run.Progress * 100f);
            if (pct != _autoTuneShownPct || run.EvidencePreparing != _autoTuneShownPreparing) {
                _autoTuneShownPct = pct;
                _autoTuneShownPreparing = run.EvidencePreparing;
                Repaint();
            }
        }

        // 待ちの印を外す(スポイトのクリックの当て先も捨てる。届く前に止めたら、何も当てない)。
        private void StopAutoTuneTick() {
            _autoTune = null;
            _pickTune = null;
            EditorApplication.update -= TickAutoTune;
        }

        // internal は AutoTuneLeakCheckTests が、確かめの結果を当てる Undo を確かめるため。
        internal void ApplyAutoTune(AutoTuneService.Run run) {
            try { ApplyAutoTuneCore(run); } finally { run.ReleaseLeakExclusion(); }
        }

        private void ApplyAutoTuneCore(AutoTuneService.Run run) {
            // 実行中に削除・追加された可能性があるため、live zone を id で引き直す。
            var targetZone = FindZoneById(run.ZoneId);
            if (targetZone == null) {
                return;
            }

            // 漏れの確かめで外す範囲(別の部品の陰・ツヤだけが塗られていた所)を、マスクの寸法にする。
            NativeBools leak = null;
            if (run.HasLeakExclusion && _maskView != null) {
                _maskView.EnsureMasks();
                leak = run.LeakRegionFor(_maskView.Workspace, SourceTexture);
            }
            bool leakApplied = false;
            MaskRegionEditResult leakResult = default;
            try {
                // 結果は押してから数秒後に届くので、待っている間の別の操作とは別の「元に戻す」1 回分にする。外す範囲は同じ 1 回分で、
                // 右クリックの「ここは塗らない」と同じ入口から書く(塗った範囲の記録が残り、「ここのマスクを消す」で戻せる)。
                // マスクを先に書く: その記録(RecordMaskEdit)が今のマスクを版にしてから文書を記録するので、後のゾーンの値の記録と
                // 合わせて、Undo で値とマスクの両方が押す前へ戻る(逆の順だと、先に記録した文書の版がマスクの履歴に無い)。
                SeparateUndo.Record("Auto-tune Zone", () => {
                    if (leak != null) {
                        var dest = new MaskPaintView.MaskDestination(targetZone.id, MaskRegionOp.DontPaintHere);
                        leakApplied = _maskView.ApplyRegion(leak, dest, out leakResult);
                    }
                    Undo.RegisterCompleteObjectUndo(UndoTarget, "Auto-tune Zone");
                    run.ApplyTo(targetZone);
                });
            } finally {
                leak?.Release();
            }
            HandOverAutoTuneSelection(run);
            RecordAppliedAutoTune(run, targetZone, leakApplied, leakResult);
        }

        // 当てた後の記録と知らせ(「自動調整」ボタンとスポイトのクリックで共通): 入力と由来をゾーンの id で覚え、編集を知らせ、
        // 完了を知らせる。
        private void RecordAppliedAutoTune(AutoTuneService.Run run, ColorZone targetZone, bool leakApplied,
            MaskRegionEditResult leakResult) {
            (_autoTuneInputs ??= new Dictionary<string, AutoTuneService.Input>())[targetZone.id] = run.ToInput();
            Editing.NotifyChanged(EditChange.Content);
            (_autoTuneProvenance ??= new Dictionary<string, string>())[targetZone.id] = run.Provenance;
            if (MaskSuggestPerf.Enabled && run.Result.evidenceDiag != null) {
                MaskSuggestPerf.Log($"自動調整の証拠: {run.Result.evidenceDiag}");
            }
            if (MaskSuggestPerf.Enabled && run.LeakDiag.Length > 0) {
                MaskSuggestPerf.Log($"自動調整の漏れの確かめ: {run.LeakDiag}");
            }
            string done = run.UsedEvidence ? Localization.AutoTuneDoneWithEvidence : Localization.AutoTuneDone;
            if (leakApplied && leakResult.changed > 0) {
                done += "\n" + string.Format(Localization.AutoTuneLeakExcludedFormat, run.LeakExcludedParts);
            }
            ShowNotification(new GUIContent(done));
            Repaint();
        }

        // 始められなかった・途中で終わった理由を知らせる(解析は始めない。従来導出へは落とさない)。
        private void NotifyAutoTuneFailure(AutoTuneService.Run run) {
            switch (run.Failure) {
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
                case AutoTuneService.Failure.AnalysisFailed: {
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

        // 自動調整の中止（ユーザーの中止・ウィンドウ無効化/破棄）。証拠待ちなら解析は始めない。スポイトのクリックの自動調整なら、
        // ゾーンを足さず色も替えない(クリックの前のまま)。internal は EditMode テストが、進み具合の「キャンセル」と同じ中止を呼ぶため。
        internal void CancelAutoTune() {
            var run = _autoTune;
            if (run == null) {
                return;
            }
            StopAutoTuneTick();
            _autoTuneHandoff = null;
            run.Cancel();
        }

        // テクスチャの切り替え・レシピの読み直し・Undo: 押したときの状態はもう無いので、待ちも解析も止めて知らせる。当て先は id で
        // 引き直すが、プリセットやレシピの複製では別のテクスチャのゾーンも同じ id を持ち、Undo で戻したゾーンは
        // 取り消した位置から導いた値で上書きされる。
        private void AbortAutoTuneForEditChange() {
            if (_autoTune == null) {
                return;
            }
            bool pick = _pickTune != null;
            CancelAutoTune();
            ShowNotification(new GUIContent(pick ? Localization.PickAutoTuneCancelledByEdit : Localization.AutoTuneCancelledByEdit));
            Repaint();
        }
    }
}
