// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // ゾーンリストの描画・編集（追加/削除/並べ替え）。並び順＝優先度。
    // GUILayout 安全のため、確定したミューテーションは次の Layout イベントで適用する（遅延ミューテーション）。
    public partial class IrocaWindow
    {
        // Foldout
        [SerializeField] private bool zonesFoldout = true;

        // GUILayout安全な変更保留フラグ
        // ExitGUI() をネストしたレイアウトグループ内から呼ぶと
        // Layout/Repaint 間のコントロール数不一致が起きるため、
        // 変更を次の Layout イベント開始時まで遅延させる。
        private bool _pendingAddZone;
        private int _pendingRemoveZoneIndex = -1;
        private EditMode? _pendingEditMode;

        // ゾーン並べ替え（ドラッグ）用。並び順が優先度なので、リスト上のドラッグで優先度を変える。
        // _dragZoneIndex: 現在ドラッグ中のゾーン index（-1 = ドラッグなし）。
        // _pendingReorder*: 確定した移動を次の Layout イベントで適用する（遅延ミューテーション）。
        private int _dragZoneIndex = -1;
        private int _pendingReorderFrom = -1;
        private int _pendingReorderTo = -1;
        // 掴んだ位置とゾーン上端の差。ゴースト(追従パネル)を掴んだ位置基準で描くために保持。
        private float _dragGrabOffsetY;

        private void ProcessPendingZoneChanges()
        {
            if (Event.current.type != EventType.Layout) return;

            if (_pendingRemoveZoneIndex >= 0)
            {
                int idx = _pendingRemoveZoneIndex;
                _pendingRemoveZoneIndex = -1;
                // bool[] バッファを _session.maskState に同期してから Undo 登録、削除後に再同期。
                // これでゾーン削除1回 = Undo 1ステップで完全復元できる。
                _maskView.SyncBuffersToState();
                Undo.RegisterCompleteObjectUndo(this, "Remove Zone");
                _maskView.OnZoneAboutToBeRemoved(idx);
                zones.RemoveAt(idx);
                _maskView.SyncBuffersToState();
                MarkPreviewDirty();
            }
            if (_pendingAddZone)
            {
                _pendingAddZone = false;
                Undo.RegisterCompleteObjectUndo(this, "Add Zone");
                var newZone = new ColorZone();
                newZone.EnsureId();
                zones.Add(newZone);
                MarkPreviewDirty();
            }
            if (_pendingReorderFrom >= 0 && _pendingReorderTo >= 0)
            {
                int from = _pendingReorderFrom;
                int to = _pendingReorderTo;
                _pendingReorderFrom = -1;
                _pendingReorderTo = -1;
                if (from != to && from >= 0 && from < zones.Count && to >= 0 && to < zones.Count)
                {
                    // マスク本体は zone.id キーで管理されるため移動不要。並び順(優先度)のみ変更し、
                    // 編集中マスクターゲット(index 参照)を移動に追従させる。Undo 1ステップで復元可能。
                    _maskView.SyncBuffersToState();
                    Undo.RegisterCompleteObjectUndo(this, "Reorder Zone");
                    var moved = zones[from];
                    zones.RemoveAt(from);
                    zones.Insert(to, moved);
                    _maskView.OnZoneReordered(from, to);
                    _maskView.SyncBuffersToState();
                    MarkPreviewDirty();
                }
            }
            if (_pendingEditMode.HasValue)
            {
                Undo.RecordObject(this, "Change Edit Mode");
                editMode = _pendingEditMode.Value;
                _pendingEditMode = null;
            }
        }

        // ───────────────────────── ゾーンリスト ───────────────────────────

        // ゾーンリストのヘッダ行は毎フレーム×ゾーン数で描画されるため、GUIStyle/GUIContent を
        // 静的キャッシュして毎フレームのアロケーションを避ける。文字列は Localization 由来なので、
        // 言語切替時(CurrentLanguage 変化)だけ再構築する。GUIContent は IMGUI が即時消費するため
        // 単一インスタンスの共有で問題ない。
        private static GUIStyle s_dragHandleStyle;
        private static LanguageMode s_zoneCacheLang = (LanguageMode)(-1);
        private static GUIContent s_dragHandleContent, s_zoneEnabledContent, s_zoneNameContent,
            s_removeZoneContent, s_editMaskActiveContent, s_editMaskInactiveContent,
            s_autoTuneEnabledContent, s_autoTuneDisabledContent,
            s_eyedropperIdleContent, s_eyedropperActiveContent;

        private static void EnsureZoneListCache()
        {
            if (s_dragHandleStyle == null)
            {
                s_dragHandleStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
            }
            if (s_zoneCacheLang == Localization.CurrentLanguage && s_dragHandleContent != null)
                return;
            s_zoneCacheLang = Localization.CurrentLanguage;
            s_dragHandleContent       = new GUIContent("☰", Localization.ZoneDragHandleTooltip);
            s_zoneEnabledContent      = new GUIContent("", Localization.ZoneEnabledTooltip);
            s_zoneNameContent         = new GUIContent("", Localization.ZoneNameTooltip);
            s_removeZoneContent       = new GUIContent("×", Localization.RemoveZoneTooltip);
            s_editMaskActiveContent   = new GUIContent(Localization.EditMaskActiveLabel, Localization.EditMaskTooltip);
            s_editMaskInactiveContent = new GUIContent(Localization.EditMaskInactiveLabel, Localization.EditMaskTooltip);
            s_autoTuneEnabledContent  = new GUIContent(Localization.AutoTune, Localization.AutoTuneTooltip);
            s_autoTuneDisabledContent = new GUIContent(Localization.AutoTune, Localization.AutoTuneDisabledTooltip);
            s_eyedropperIdleContent   = new GUIContent(Localization.EyedropperIdle, Localization.EyedropperTooltip);
            s_eyedropperActiveContent = new GUIContent(Localization.EyedropperActive, Localization.EyedropperTooltip);
        }

        // かんたん / 上級 モード切替。上級でゾーンの詳細パラメータ（エッジ・彩度・
        // シャドウ/ハイライト等）と加工設定の詳細を表示する。ゾーン foldout の開閉に
        // 関係なく常に見えるよう、foldout ヘッダの前に描画する。
        private void DrawModeToggle()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                new GUIContent(Localization.EditMode, Localization.EditModeTooltip),
                GUILayout.Width(70));
            // かんたんモード（Simple）はまだ実用段階でないため UI から隠す（2026-06 一時対応）。
            // Normal/Advanced のみ表示し、Simple のセッションは Normal に正規化する。
            // 再有効化するときは下の 3 択へ戻し、Auto Tune ボタンと
            // ScheduleAutoTune/ProcessPendingAutoTune のコメントアウトも併せて解除する。
            if (editMode == EditMode.Simple) editMode = EditMode.Normal;
            int cur = (int)editMode - 1; // Normal=0, Advanced=1（Simple を隠したぶん 1 ずらす）
            int next = GUILayout.Toolbar(cur,
                new[]
                {
                    /* Localization.SimpleMode, */
                    new GUIContent(Localization.NormalMode, Localization.EditModeToolbarTooltip),
                    new GUIContent(Localization.AdvancedShort, Localization.EditModeToolbarTooltip),
                });
            if (next != cur && next >= 0)
            {
                // 制御数が変わるため、ExitGUI 相当の崩れを避けて次の Layout で適用する
                // （_pendingEditMode 遅延ミューテーション）。Simple を隠したぶん +1 して enum に戻す。
                _pendingEditMode = (EditMode)(next + 1);
                Repaint();
            }
            // かんたんモードの自動調整は裏で走り、ウィンドウをブロックしない。
            // 進行中・予約中であることを軽い文言で示す（操作は妨げない）。
            // ※自動調整を隠している間は発火しないが、再有効化に備えて残す。
            if ((_autoTuneJob.IsRunning && !_autoTuneIsManual) || _pendingAutoTuneZoneId != null)
                GUILayout.Label(Localization.AutoTuningInProgress, EditorStyles.miniLabel,
                    GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void DrawZoneList()
        {
            DrawModeToggle();
            zonesFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(zonesFoldout, Localization.StepPrefixZones + Localization.ColorZones);
            if (!zonesFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            // 並び順＝優先度。重なりは上のゾーンのみ適用され、下のゾーンのマスクとして機能する。
            if (zones.Count >= 2)
                EditorGUILayout.HelpBox(Localization.ZonePriorityHelp, MessageType.None);

            // 外部要因（削除等）で範囲外になったドラッグ状態をリセット。
            if (_dragZoneIndex >= zones.Count) _dragZoneIndex = -1;

            // ドラッグハンドル用スタイルとヘッダ行の GUIContent は静的キャッシュを使う
            // (毎フレーム×ゾーン数のアロケーション回避。言語切替時のみ再構築)。
            EnsureZoneListCache();
            // 各ゾーンの矩形を記録し、ドロップ位置の判定とインジケータ描画に使う。
            var zoneRects = new List<Rect>(zones.Count);

            int removeIndex = -1;
            for (int i = 0; i < zones.Count; i++)
            {
                if (DrawZoneCard(zones[i], i)) removeIndex = i;
                // ドロップ位置判定・インジケータ描画用に、このゾーン全体の矩形を記録。
                // GetLastRect は Layout パスではダミー値だが、判定・描画は非 Layout パスでのみ行う。
                zoneRects.Add(GUILayoutUtility.GetLastRect());
                EditorGUILayout.Space(2);
            }

            // ── ドラッグ並べ替えの処理（インジケータ描画 / ドロップ確定）──
            HandleZoneReorderDrag(zoneRects);

            if (removeIndex >= 0)
            {
                _pendingRemoveZoneIndex = removeIndex;
                Repaint();
            }

            if (GUILayout.Button(new GUIContent(Localization.AddZone, Localization.AddZoneTooltip)))
            {
                _pendingAddZone = true;
                Repaint();
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        // 1 ゾーン分のカード（ヘッダ行＋マスク編集＋採色＋自動調整＋許容範囲＋連続領域＋
        // 変更先/模様保持/出力彩度＋通常以上の詳細）を描画する。
        // 戻り値 true = このカードの削除(×)ボタンが押された。
        private bool DrawZoneCard(ColorZone zone, int index)
        {
            bool removeRequested = false;
            zone.EnsureId();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header row
            EditorGUILayout.BeginHorizontal();
            // ドラッグハンドル: 掴んでリストを並べ替える＝優先度を変える。
            // 幅は行高(singleLineHeight)に追従させ、エディタのフォントサイズが大きいときも
            // 縦長に潰れないようにする（高さだけ追従して幅が固定だと非対称になる）。
            float rowH = EditorGUIUtility.singleLineHeight;
            GUILayout.Label(s_dragHandleContent,
                s_dragHandleStyle, GUILayout.Width(rowH), GUILayout.Height(rowH));
            Rect handleRect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.Pan);
            if (GUI.enabled && Event.current.type == EventType.MouseDown
                && handleRect.Contains(Event.current.mousePosition))
            {
                _dragZoneIndex = index;
                // ハンドルはゾーン上端付近にあるので、ここを掴み位置の基準にする。
                _dragGrabOffsetY = Event.current.mousePosition.y - handleRect.y;
                Event.current.Use();
            }
            zone.enabled = UndoHelper.ToggleLeft(this,
                s_zoneEnabledContent,
                zone.enabled, GUILayout.Width(rowH));
            zone.name = UndoHelper.TextField(this,
                s_zoneNameContent,
                zone.name);
            if (GUILayout.Button(s_removeZoneContent, GUILayout.Width(IrocaConsts.Layout.RemoveButtonWidth)))
            {
                removeRequested = true;
            }
            EditorGUILayout.EndHorizontal();

            // ゾーンマスク編集ボタン（フル幅・状態連動）
            // 押したら「このゾーンを編集対象にする」だけでなく、そのままプレビュー上を
            // ドラッグして塗れるようペイントモード(maskPaintActive)も同時に ON にする。
            // 以前は編集対象の選択だけで、実際に塗るには除外マスク欄の「除外／含める」を
            // 別途押して maskPaintActive を立てる必要があった。ボタンが「編集中」表示なのに
            // ドラッグしても塗れず、バグに見えていたため一体化する。
            {
                bool isActive = _maskView.activeMaskTarget == index;
                var prevBg = GUI.backgroundColor;
                if (isActive) GUI.backgroundColor = IrocaColors.ActiveMaskTarget;
                if (GUILayout.Button(isActive ? s_editMaskActiveContent : s_editMaskInactiveContent))
                {
                    if (isActive)
                    {
                        // 「編集中（クリックで解除）」→ 編集終了。共通マスクへ戻し、ペイントも止める。
                        _maskView.activeMaskTarget = -1;
                        _maskView.maskPaintActive = false;
                    }
                    else
                    {
                        // このゾーンを編集対象にし、すぐ塗れるようペイントモードへ（既定＝除外ブラシ）。
                        _maskView.activeMaskTarget = index;
                        _maskView.maskFoldout = true;
                        _maskView.maskPaintActive = true;
                        _maskView.brushEraseMode = false;
                    }
                    _maskView.maskDirty = true;
                    Repaint();
                }
                GUI.backgroundColor = prevBg;
            }

            // 自動調整ボタンは「サンプルカラー」の直下に配置する（採色 → 自動調整 の流れ）。

            // ─── UV矩形モード選択UI ───
            // UV矩形モードは実装継続中のため当面 UI から非表示。
            // zone.mode = UndoHelper.EnumPopup(this,
            //     new GUIContent(Localization.SelectionMode, Localization.SelectionModeTooltip),
            //     zone.mode);

            // ColorPick UI（常時表示）
            Color prevSampleColor = zone.sampleColor;
            zone.sampleColor = UndoHelper.ColorField(this,
                new GUIContent(Localization.SampleColor, Localization.SampleColorTooltip),
                zone.sampleColor);
            if (zone.sampleColor != prevSampleColor)
            {
                // スポイト/カラーフィールドで色を取った＝サンプル指定済み。意図的な白選択を
                // 「未指定の白」と区別し、白い服・白髪などでも自動調整を許可できるようにする。
                zone.sampleColorSet = true;
                // サンプルカラーが変わったら、自動トーン抽出で生成済みの内部サンプルは
                // 古いパーツのものになるためクリアする（次の自動調整で作り直す）。
                if (zone.extraSamples != null && zone.extraSamples.Count > 0)
                    zone.extraSamples.Clear();
                MarkPreviewDirty();
            }

            // ─── プレビュー直接スポイト ───
            // カラーピッカーを経由せず、プレビュー上のクリックでこのゾーンのサンプルカラーを
            // 実テクスチャ画素から直接取得する（PreviewView 側が実画素を読む）。読み取り不可では押せない。
            {
                bool canSample = sourceTexture != null && IsReadable(sourceTexture)
                    && zone.mode == SelectionMode.ColorPick;
                bool armed = !string.IsNullOrEmpty(zone.id) && EyedropperZoneId == zone.id;
                using (new EditorGUI.DisabledScope(!canSample))
                {
                    var prevBg = GUI.backgroundColor;
                    if (armed) GUI.backgroundColor = IrocaColors.ActiveMaskTarget;
                    if (GUILayout.Button(armed ? s_eyedropperActiveContent : s_eyedropperIdleContent))
                    {
                        // トグル：武装↔解除。武装は id で保持し、並べ替え・削除で別ゾーンを指さないようにする
                        // （クリックで一発取得→自動解除）。
                        zone.EnsureId();
                        EyedropperZoneId = armed ? null : zone.id;
                        Repaint();
                    }
                    GUI.backgroundColor = prevBg;
                }
            }

            // ─── 自動調整ボタン ───
            // スポイト1点から、パーツの濃淡（暗部/中間/明部）を内部で自動サンプリングして
            // 許容範囲などを最適化する。ユーザーが濃淡を手で採り直す必要はない。
            {
                bool canTune =
                    sourceTexture != null
                    && IsReadable(sourceTexture)
                    && zone.mode == SelectionMode.ColorPick
                    && zone.HasSampleColor;
                using (new EditorGUI.DisabledScope(!canTune))
                {
                    if (GUILayout.Button(canTune ? s_autoTuneEnabledContent : s_autoTuneDisabledContent))
                    {
                        RunAutoTune(zone);
                    }
                }
            }

            zone.tolerance = UndoHelper.Slider(this,
                new GUIContent(Localization.Tolerance, Localization.ToleranceTooltip),
                zone.tolerance, 0f, 1f);

            // ─── 連続領域モード (Flood Fill / 連結成分アンカリング) ───
            // 既定は自動アンカリング(シード不要)。確信度の高い芯を含む連結領域だけ残し、
            // 物理的に離れた同色パーツや背景へのにじみを自動除去する。シードは任意の上書き。
            EditorGUILayout.Space(2);
            bool prevUseFloodFill = zone.useFloodFill;
            zone.useFloodFill = UndoHelper.Toggle(this,
                new GUIContent(Localization.UseFloodFill, Localization.UseFloodFillTooltip),
                zone.useFloodFill);
            if (zone.useFloodFill != prevUseFloodFill) MarkPreviewDirty();

            if (zone.useFloodFill)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.BeginHorizontal();
                    string seedLabel = zone.seedUV.x >= 0f
                        ? $"UV ({zone.seedUV.x:F3}, {zone.seedUV.y:F3})"
                        : Localization.FloodFillSeedNotSet;
                    EditorGUILayout.LabelField(
                        new GUIContent(Localization.FloodFillSeedPoint, Localization.FloodFillSeedHint),
                        seedLabel);
                    // シード指定時のみ「自動へ戻す」クリアを出す。
                    using (new EditorGUI.DisabledScope(zone.seedUV.x < 0f))
                    {
                        if (GUILayout.Button(
                            new GUIContent(Localization.FloodFillClear, Localization.FloodFillClearTooltip),
                            GUILayout.Width(52)))
                        {
                            Undo.RecordObject(this, "Clear Flood Fill Seed");
                            zone.seedUV = new UnityEngine.Vector2(-1f, -1f);
                            MarkPreviewDirty();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            // ─── UV矩形モード UI ───
            // UV矩形モードは実装継続中のため当面 UI から非表示。
            // else
            // {
            //     EditorGUILayout.LabelField(
            //         new GUIContent(Localization.UVRect, Localization.UVRectTooltip));
            //     using (new EditorGUI.IndentLevelScope())
            //     {
            //         float x = UndoHelper.Slider(this, "X", zone.uvRect.x, 0f, 1f);
            //         float y = UndoHelper.Slider(this, "Y", zone.uvRect.y, 0f, 1f);
            //         float w = UndoHelper.Slider(this, "W", zone.uvRect.width, 0f, 1f);
            //         float h = UndoHelper.Slider(this, "H", zone.uvRect.height, 0f, 1f);
            //         zone.uvRect = new Rect(x, y, w, h);
            //     }
            // }

            zone.targetColor = UndoHelper.ColorField(this,
                new GUIContent(Localization.TargetColor, Localization.TargetColorTooltip),
                zone.targetColor);
            zone.valueBlend = UndoHelper.Slider(this,
                new GUIContent(Localization.PatternPreserve, Localization.PatternPreserveTooltip),
                zone.valueBlend, 0f, 1f);
            zone.outputSaturation = UndoHelper.Slider(this,
                new GUIContent(Localization.OutputSaturation, Localization.OutputSaturationTooltip),
                zone.outputSaturation, 0f, 1f);

            // ─── 通常モード以上で表示する標準の調整項目 ───
            // かんたんモードでは核となる色・許容範囲・模様保持・出力彩度だけを見せ、
            // エッジ/彩度/シャドウ・ハイライト等の調整は「自動調整」に委ねる。
            // 通常モードは初見の圧を下げるためゾーンごとに「詳細設定」へ畳む（既定で閉じる）。
            // 上級モードは「すべて見たい」という明示的な選択なので、畳まず常に展開する。
            if (editMode == EditMode.Advanced)
            {
                DrawZoneAdvancedParams(zone);
            }
            else if (editMode == EditMode.Normal)
            {
                EditorGUILayout.Space(2);
                zone.detailFoldout = EditorGUILayout.Foldout(
                    zone.detailFoldout, Localization.ZoneDetailFoldout, true);
                if (zone.detailFoldout)
                    DrawZoneAdvancedParams(zone);
            }

            EditorGUILayout.EndVertical();
            return removeRequested;
        }

        // 通常/上級モードで表示する詳細パラメータ（アンカー正規化・エッジ・彩度・
        // シャドウ/ハイライト、上級限定のマッチング距離重み、既定へ戻すボタン）。
        private void DrawZoneAdvancedParams(ColorZone zone)
        {
            zone.autoRecolorAnchor = UndoHelper.Toggle(this,
                new GUIContent(Localization.AutoRecolorAnchor, Localization.AutoRecolorAnchorTooltip),
                zone.autoRecolorAnchor);
            zone.edgeSoftness = UndoHelper.Slider(this,
                new GUIContent(Localization.EdgeSoftness, Localization.EdgeSoftnessTooltip),
                zone.edgeSoftness, 0f, 1f);
            zone.saturationStrictness = UndoHelper.Slider(this,
                new GUIContent(Localization.SaturationStrictness, Localization.SaturationStrictnessTooltip),
                zone.saturationStrictness, 0f, 1f);
            zone.saturationGuard = UndoHelper.Slider(this,
                new GUIContent(Localization.SaturationGuard, Localization.SaturationGuardTooltip),
                zone.saturationGuard, 0f, 1f);

            zone.highlightRecovery = UndoHelper.Toggle(this,
                new GUIContent(Localization.HighlightRecovery, Localization.HighlightRecoveryTooltip),
                zone.highlightRecovery);

            // ハイライト帯の拡張は「ハイライト補助」が ON のときのみ有効なので、
            // OFF のときはグレーアウトして関係を明示する。
            using (new EditorGUI.DisabledScope(!zone.highlightRecovery))
            {
                EditorGUI.indentLevel++;
                zone.highlightBandExpand = UndoHelper.Toggle(this,
                    new GUIContent(Localization.HighlightBandExpand, Localization.HighlightBandExpandTooltip),
                    zone.highlightBandExpand);
                EditorGUI.indentLevel--;
            }

            zone.applyHighlightWash = UndoHelper.Toggle(this,
                new GUIContent(Localization.ApplyHighlightWash, Localization.ApplyHighlightWashTooltip),
                zone.applyHighlightWash);

            // 俯瞰スポイト補正(wash サンプル自動導出)は「ハイライト白寄せ合成」が ON の
            // ときのみ意味を持つので、OFF のときはグレーアウトして関係を明示する。
            using (new EditorGUI.DisabledScope(!zone.applyHighlightWash))
            {
                EditorGUI.indentLevel++;
                zone.autoHighlightSample = UndoHelper.Toggle(this,
                    new GUIContent(Localization.AutoHighlightSample, Localization.AutoHighlightSampleTooltip),
                    zone.autoHighlightSample);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Localization.ShadowHighlightSection, EditorStyles.boldLabel);

            zone.shadowDesaturation = UndoHelper.Slider(this,
                new GUIContent(Localization.ShadowDesaturation, Localization.ShadowDesaturationTooltip),
                zone.shadowDesaturation, 0f, 1f);
            zone.shadowForgivenessSatMin = UndoHelper.Slider(this,
                new GUIContent(Localization.ShadowForgivenessSatMin, Localization.ShadowForgivenessSatMinTooltip),
                zone.shadowForgivenessSatMin, 0f, 1f);
            zone.chromaThreshold = UndoHelper.Slider(this,
                new GUIContent(Localization.ChromaThreshold, Localization.ChromaThresholdTooltip),
                zone.chromaThreshold, 0f, 1f);

            // ─── 上級モードのみ: マッチング距離の内部重み ───
            if (editMode == EditMode.Advanced)
            {
                zone.valueWeight = UndoHelper.Slider(this,
                    new GUIContent(Localization.ValueWeight, Localization.ValueWeightTooltip),
                    zone.valueWeight, 0f, 1f);
                zone.satDistWeight = UndoHelper.Slider(this,
                    new GUIContent(Localization.SatDistWeight, Localization.SatDistWeightTooltip),
                    zone.satDistWeight, 0f, 1f);
                zone.satRampScale = UndoHelper.Slider(this,
                    new GUIContent(Localization.SatRampScale, Localization.SatRampScaleTooltip),
                    zone.satRampScale, 0.01f, 0.5f);
            }

            // 詳細パラメータを既定値へ戻す（色・許容範囲・名前は保持）。通常/上級どちらでも表示。
            EditorGUILayout.Space(2);
            if (GUILayout.Button(new GUIContent(Localization.ResetZoneTuning, Localization.ResetZoneTuningTooltip)))
            {
                Undo.RegisterCompleteObjectUndo(this, "Reset Zone Tuning");
                zone.ResetTuningToDefault();
                MarkPreviewDirty();
            }
        }

        // ドラッグ中ゾーンのインジケータ描画とドロップ確定。zoneRects は各ゾーンカードの矩形。
        // 判定・描画は非 Layout パスでのみ行う（Layout パスの GetLastRect はダミー値のため）。
        private void HandleZoneReorderDrag(List<Rect> zoneRects)
        {
            if (!(_dragZoneIndex >= 0 && _dragZoneIndex < zoneRects.Count && zoneRects.Count > 0))
                return;

            var evt = Event.current;
            float my = evt.mousePosition.y;

            // ドラッグ中ゾーンを「掴み位置オフセットぶん」投影した想定矩形。
            // ゾーンは縦長なので、中心同士を比較すると隣の高さの半分も運ぶ必要があり、
            // 「かなり上まで持っていかないと入れ替わらない」状態になる。
            // 代わりに、この投影矩形が隣ゾーンに少しでも重なった瞬間に入れ替える
            // ことで、移動距離をゾーン高さに依存しない最小限にする。
            float projTop = my - _dragGrabOffsetY;
            float projBottom = projTop + zoneRects[_dragZoneIndex].height;
            // 隙間や微小なブレで誤入れ替えしない最小の重なり量(px)。
            float overlapTrigger = EditorGUIUtility.singleLineHeight * 0.6f;

            // 挿入スロット(0..count)。既定は移動なし。
            int slot = _dragZoneIndex;
            // 上方向: 上端が重なった最上位ゾーンの「前」に挿入。
            for (int k = 0; k < _dragZoneIndex; k++)
            {
                if (projTop < zoneRects[k].yMax - overlapTrigger) { slot = k; break; }
            }
            // 下方向: 下端が重なった最下位ゾーンの「後ろ」に挿入。
            if (slot == _dragZoneIndex)
            {
                for (int k = zoneRects.Count - 1; k > _dragZoneIndex; k--)
                {
                    if (projBottom > zoneRects[k].yMin + overlapTrigger) { slot = k + 1; break; }
                }
            }
            // remove 後の挿入 index に変換（自分より後ろへ落とすと 1 詰まる）。
            int insertAt = slot > _dragZoneIndex ? slot - 1 : slot;

            if (evt.type == EventType.Repaint)
            {
                Rect src = zoneRects[_dragZoneIndex];
                Color accent = IrocaColors.ActiveMaskTarget;

                // 1. 元のスロットを暗転して「ここを移動中」と示す。
                EditorGUI.DrawRect(src, new Color(0f, 0f, 0f, 0.18f));

                // 2. 挿入位置のライン。
                float lineY = slot < zoneRects.Count
                    ? zoneRects[slot].yMin
                    : zoneRects[zoneRects.Count - 1].yMax;
                EditorGUI.DrawRect(new Rect(src.xMin, lineY - 1.5f, src.width, 3f), accent);

                // 3. マウスに追従するゴースト(ヘッダー帯を模した浮遊パネル)。
                float gh = EditorGUIUtility.singleLineHeight + 8f;
                float gy = evt.mousePosition.y - _dragGrabOffsetY;
                Rect ghost = new Rect(src.xMin, gy, src.width, gh);
                Color fill = accent; fill.a = 0.35f;
                EditorGUI.DrawRect(ghost, fill);
                DrawRectOutline(ghost, accent, 1f);

                var dz = zones[_dragZoneIndex];
                // 変更先カラーのスウォッチ。
                Rect swatch = new Rect(ghost.x + 22f, ghost.y + 5f, 14f, gh - 10f);
                Color sw = dz.targetColor; sw.a = 1f;
                EditorGUI.DrawRect(swatch, sw);
                DrawRectOutline(swatch, new Color(0f, 0f, 0f, 0.4f), 1f);
                // ゾーン名ラベル。
                string gname = string.IsNullOrEmpty(dz.name) ? Localization.UnnamedZone : dz.name;
                GUI.Label(new Rect(swatch.xMax + 6f, ghost.y + 3f, ghost.width - 64f, EditorGUIUtility.singleLineHeight),
                    new GUIContent("☰  " + gname), EditorStyles.boldLabel);
            }
            else if (evt.type == EventType.MouseDrag)
            {
                evt.Use();
                Repaint();
            }
            else if (evt.type == EventType.MouseUp)
            {
                if (insertAt != _dragZoneIndex)
                {
                    _pendingReorderFrom = _dragZoneIndex;
                    _pendingReorderTo = insertAt;
                }
                _dragZoneIndex = -1;
                evt.Use();
                Repaint();
            }
        }

        // 矩形の枠線を 4 本の細い矩形で描く（Repaint 中のゴースト/スウォッチ枠用）。
        private static void DrawRectOutline(Rect r, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMax - thickness, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, thickness, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.yMin, thickness, r.height), color);
        }
    }
}
