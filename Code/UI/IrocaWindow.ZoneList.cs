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
        [SerializeField] private bool zonesFoldout = true;

        // ExitGUI() をネストしたレイアウトグループ内から呼ぶと
        // Layout/Repaint 間のコントロール数不一致が起きるため、
        // 変更を次の Layout イベント開始時まで遅延させる。
        private bool _pendingAddZone;
        private int _pendingRemoveZoneIndex = -1;

        // UI から追加する新規ゾーンの初期許容範囲。
        //
        // ColorZone のフィールド既定は 0 で、これは「まだ何も指定していない」状態を表す値
        // （距離 0 の画素しか一致しない＝プレビューが一切変わらない）。自動調整で埋める前提の
        // 設計だったが、自動調整には AI モデルが要るため、未導入のユーザーはスポイトで色を
        // 取っても何も起きず「壊れている」ようにしか見えなかった（2026-09-11 の UX 見直し）。
        //
        // 0.2 は zones JSON 経路（MCP・batchmode）の既定 ZonesJsonDefaults.Tolerance と同値で、
        // 回帰テストのシナリオ既定（fixtures.ZoneSpec.tolerance）とも一致する実績のある動作点。
        // ★ColorZone.cs 側の既定は変えない★ — あちらは JSON／プリセットから値が入る前提の
        // データ既定であり、ハーネス（回帰テスト・視覚ゲート）が読む唯一の正でもある。
        // ここで入れるのは「UI で新しく作ったゾーンの初期値」だけ。
        private const float NewZoneInitialTolerance = 0.2f;

        /// <summary>
        /// 新規ゾーンの既定名。既存と重複しない最小の番号を振る。
        /// 全ゾーンが同名（"Zone"）だと、マスク編集ウィンドウの「編集対象」プルダウンや
        /// ドラッグ中のゴーストで見分けがつかなかった。
        /// </summary>
        private string NextZoneName()
        {
            var used = new HashSet<string>();
            foreach (var z in zones)
                if (z != null && !string.IsNullOrEmpty(z.name)) used.Add(z.name);
            for (int n = 1; n <= zones.Count + 1; n++)
            {
                string candidate = string.Format(Localization.NewZoneNameFormat, n);
                if (!used.Contains(candidate)) return candidate;
            }
            return string.Format(Localization.NewZoneNameFormat, zones.Count + 1);
        }

        // ゾーン並べ替え（ドラッグ）用。並び順が優先度なので、リスト上のドラッグで優先度を変える。
        // _dragZoneIndex: 現在ドラッグ中のゾーン index（-1 = ドラッグなし）。
        // _pendingReorder*: 確定した移動を次の Layout イベントで適用する（遅延ミューテーション）。
        private int _dragZoneIndex = -1;
        private int _pendingReorderFrom = -1;
        private int _pendingReorderTo = -1;
        // 掴んだ位置とゾーン上端の差。ゴースト(追従パネル)を掴んだ位置基準で描くために保持。
        private float _dragGrabOffsetY;
        // ドラッグ操作の hotControl 用 ID。HandleZoneReorderDrag で毎フレーム確保して保持し、
        // MouseDown で hotControl に据える。hotControl を取ると Unity がマウスをキャプチャし、
        // ウィンドウ外リリースでも MouseUp が届く＝_dragZoneIndex が残留して次の無関係な MouseUp で
        // 誤並べ替え（＝優先度変更＝出力変化）が確定する事故を防ぐ。
        private int _dragControlId;

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
                // 「色を選べば何か変わる」状態から始められるようにする（定数のコメント参照）。
                newZone.tolerance = NewZoneInitialTolerance;
                newZone.name = NextZoneName();
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
        }

        // ゾーンリストのヘッダ行は毎フレーム×ゾーン数で描画されるため、GUIStyle/GUIContent を
        // 静的キャッシュして毎フレームのアロケーションを避ける。文字列は Localization 由来なので、
        // 言語切替時(CurrentLanguage 変化)だけ再構築する。GUIContent は IMGUI が即時消費するため
        // 単一インスタンスの共有で問題ない。
        private static GUIStyle s_dragHandleStyle;
        private static LanguageMode s_zoneCacheLang = (LanguageMode)(-1);
        private static GUIContent s_dragHandleContent, s_zoneEnabledContent, s_zoneNameContent,
            s_removeZoneContent, s_zoneSoloContent,
            s_autoTuneEnabledContent, s_autoTuneDisabledContent,
            s_eyedropperIdleContent, s_eyedropperActiveContent,
            s_seedPickIdleContent, s_seedPickActiveContent,
            s_sampleUvPresentContent, s_sampleUvMissingContent;

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
            s_autoTuneEnabledContent  = new GUIContent(Localization.AutoTune, Localization.AutoTuneTooltip);
            s_autoTuneDisabledContent = new GUIContent(Localization.AutoTune, Localization.AutoTuneDisabledTooltip);
            s_eyedropperIdleContent   = new GUIContent(Localization.EyedropperIdle, Localization.EyedropperTooltip);
            s_eyedropperActiveContent = new GUIContent(Localization.EyedropperActive, Localization.EyedropperTooltip);
            s_zoneSoloContent         = new GUIContent(Localization.ZoneSolo, Localization.ZoneSoloTooltip);
            s_seedPickIdleContent     = new GUIContent(Localization.FloodFillSeedPick, Localization.FloodFillSeedPickTooltip);
            s_seedPickActiveContent   = new GUIContent(Localization.FloodFillSeedPickActive, Localization.FloodFillSeedPickTooltip);
            s_sampleUvPresentContent  = new GUIContent(Localization.SampleUvPresent, Localization.SampleUvTooltip);
            s_sampleUvMissingContent  = new GUIContent(Localization.SampleUvMissing, Localization.SampleUvTooltip);
        }

        private void DrawZoneList()
        {
            // 「通常 / 上級」モードトグルは廃止した。ゾーンごとの「詳細設定」折りたたみと
            // 同じ「どこまで見せるか」の軸を二重に制御しており（プリセットの「詳細」を
            // 含めると三重）、どちらを触ればよいのか分からなかった。表示の深さは
            // 「畳まれた折りたたみを開く」の一段に統一している（2026-09-11 の UX 見直し）。
            // 旧・上級限定のパラメータは、ゾーン側は「詳細設定」内の「マッチング距離の重み」、
            // 加工設定側は「詳細設定」の折りたたみへ移した。
            zonesFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(zonesFoldout,
                Localization.StepPrefixZones + Localization.ColorZones
                + (StepZonesDone ? Localization.StepDoneMark : ""));
            if (!zonesFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            // かんたんモードの自動調整は裏で走り、ウィンドウをブロックしない。
            // 進行中・予約中であることを軽い文言で示す（操作は妨げない）。
            // ※自動調整の自動実行を隠している間は発火しないが、再有効化に備えて残す。
            if ((_autoTuneJob.IsRunning && !_autoTuneIsManual) || _pendingAutoTuneZoneId != null)
                GUILayout.Label(Localization.AutoTuningInProgress, EditorStyles.miniLabel);

            // 並び順＝優先度（重なりは上のゾーンのみ適用）。説明は ☰ ハンドルのツールチップ
            // (ZoneDragHandleTooltip) に集約し、常時表示の HelpBox は置かない。

            if (_dragZoneIndex >= zones.Count) _dragZoneIndex = -1;

            // ドラッグハンドル用スタイルとヘッダ行の GUIContent は静的キャッシュを使う
            // (毎フレーム×ゾーン数のアロケーション回避。言語切替時のみ再構築)。
            EnsureZoneListCache();
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

        // 1 ゾーン分のカード（ヘッダ行＋マスク編集＋採色/変更先＋自動調整＋許容範囲＋連続領域＋
        // 模様保持/出力彩度＋通常以上の詳細）を描画する。
        // 戻り値 true = このカードの削除(×)ボタンが押された。
        private bool DrawZoneCard(ColorZone zone, int index)
        {
            bool removeRequested = false;
            zone.EnsureId();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

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
                _dragGrabOffsetY = Event.current.mousePosition.y - handleRect.y;
                // マウスキャプチャを取得（ウィンドウ外リリースでも MouseUp を確実に受け取るため）。
                // _dragControlId は前フレームの HandleZoneReorderDrag が確保した安定 ID。
                GUIUtility.hotControl = _dragControlId;
                Event.current.Use();
            }
            zone.enabled = UndoHelper.ToggleLeft(this,
                s_zoneEnabledContent,
                zone.enabled, GUILayout.Width(rowH));
            // MinWidth(0): ラベル無しでも EditorGUILayout.TextField(GUIContent, ...) は
            // 「labelWidth + fieldWidth + 5」を最小幅として要求する。ソロボタンを足した
            // この行は設定列で最も幅を要求し、スクロール内容がカラムより広がって全行の右端が
            // 縦スクロールバーの下に隠れていた。名前欄だけを伸縮させて行をカラム内に収める。
            zone.name = UndoHelper.TextField(this,
                s_zoneNameContent,
                zone.name,
                GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));

            // ソロ表示: このゾーンだけでプレビューを作り直す。ゾーンが実際にどこを拾って
            // いるかを確かめる手段が差分表示（全ゾーン混在）しか無かったため追加した。
            // 表示専用で、保存内容には影響しない（Undo にも載せない＝編集ではない）。
            {
                bool soloed = !string.IsNullOrEmpty(zone.id) && SoloZoneId == zone.id;
                var prevSoloBg = GUI.backgroundColor;
                if (soloed) GUI.backgroundColor = IrocaColors.ActiveMaskTarget;
                if (GUILayout.Button(s_zoneSoloContent, GUILayout.Width(IrocaConsts.Layout.SmallButtonWidth)))
                {
                    zone.EnsureId();
                    SetSoloZone(soloed ? null : zone.id);
                    Repaint();
                }
                GUI.backgroundColor = prevSoloBg;
            }

            if (GUILayout.Button(s_removeZoneContent, GUILayout.Width(IrocaConsts.Layout.RemoveButtonWidth)))
            {
                removeRequested = true;
            }
            EditorGUILayout.EndHorizontal();

            // ゾーン別マスクの編集は除外マスク欄の「編集対象」プルダウン＋「ブラシで編集」に
            // 一本化した（かつてここにあった専用ボタンは経路重複のため削除）。

            // UV矩形モードは実装継続中のため当面 UI から非表示。
            // zone.mode = UndoHelper.EnumPopup(this,
            //     new GUIContent(Localization.SelectionMode, Localization.SelectionModeTooltip),
            //     zone.mode);

            // ColorPick UI（常時表示）。カラーフィールドとプレビュー直接スポイトは
            // どちらも sampleColor を決める手段なので 1 行に統合してカードの行数を抑える。
            EditorGUILayout.BeginHorizontal();
            Color prevSampleColor = zone.sampleColor;
            // MinWidth(0): 見出し行の名前欄と同じ理由（ラベル付きフィールドの最小幅＋スポイト
            // ボタン幅が狭いカラムの内容幅を超える）。カラーフィールド側を縮めて行を収める。
            zone.sampleColor = UndoHelper.ColorField(this,
                new GUIContent(Localization.SampleColor, Localization.SampleColorTooltip),
                zone.sampleColor,
                GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            if (zone.sampleColor != prevSampleColor)
            {
                // スポイト/カラーフィールドで色を取った＝サンプル指定済み。意図的な白選択を
                // 「未指定の白」と区別し、白い服・白髪などでも自動調整を許可できるようにする。
                zone.sampleColorSet = true;
                // カラーフィールドで変えた色はクリック画素の色ではないので、スポイト位置
                // （自動調整が AI 提案の証拠を取る位置）を無効化する。次のスポイトで付き直す。
                zone.sampleUV = new UnityEngine.Vector2(-1f, -1f);
                ForgetSampleUvColor(zone);
                // サンプルカラーが変わったら、自動トーン抽出で生成済みの内部サンプルは
                // 古いパーツのものになるためクリアする（次の自動調整で作り直す）。
                if (zone.extraSamples != null && zone.extraSamples.Count > 0)
                    zone.extraSamples.Clear();
                MarkPreviewDirty();
            }

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
                    if (GUILayout.Button(armed ? s_eyedropperActiveContent : s_eyedropperIdleContent,
                        GUILayout.Width(IrocaConsts.Layout.EyedropperButtonWidth)))
                    {
                        // トグル：武装↔解除。武装は id で保持し、並べ替え・削除で別ゾーンを指さないようにする
                        // （クリックで一発取得→自動解除）。
                        zone.EnsureId();
                        EyedropperZoneId = armed ? null : zone.id;
                        // シード指定と排他（どちらもプレビューの素のクリックを取る）。
                        if (!armed) SeedPickZoneId = null;
                        Repaint();
                    }
                    GUI.backgroundColor = prevBg;
                }
            }
            EditorGUILayout.EndHorizontal();

            // 自動調整の直上に置く。ZoneAutoTuner は sampleColor だけでなく targetColor も
            // 入力に取り（両者の明度差から模様保持 valueBlend を決める）、変更先が未決のまま
            // 押すと既定色を前提とした結果になる。「元の色 → 変更先の色 → 自動調整」の順に
            // 並べることで、入力が全てボタンの上・書き換わる項目が全て下に揃う。
            zone.targetColor = UndoHelper.ColorField(this,
                new GUIContent(Localization.TargetColor, Localization.TargetColorTooltip),
                zone.targetColor);

            // スポイト位置の有無。自動調整はこの位置に AI マスク提案をかけて証拠にするため、
            // 位置が無いゾーン（カラーフィールドで色を決めた／Undo で無効化された）では
            // 導出経路が変わる。押してから通知で知るのでは遅いので、事前に見えるようにする。
            if (zone.HasSampleColor)
            {
                EditorGUILayout.LabelField(
                    zone.HasSampleUV ? s_sampleUvPresentContent : s_sampleUvMissingContent,
                    EditorStyles.miniLabel);
            }

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
                    // MinWidth(0): 値テキストの実幅を行の最小幅にしない。狭いカラムでは
                    // この行（ラベル＋値＋クリアボタン）が設定列で最も幅を要求し、
                    // 超過分が右端で切れて隣のクリアボタンごと隠れていた。
                    EditorGUILayout.LabelField(
                        new GUIContent(Localization.FloodFillSeedPoint, Localization.FloodFillSeedHint),
                        new GUIContent(seedLabel),
                        GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));

                    // シードを「このゾーンに」置くための武装ボタン（スポイトと同じ一発取得）。
                    // Shift+クリック経路は残すが、あちらは対象ゾーンを「マスク編集対象、無ければ
                    // 先頭の該当ゾーン」と暗黙に選ぶため、どのゾーンへ入るかが画面から読めなかった。
                    {
                        bool seedArmed = !string.IsNullOrEmpty(zone.id) && SeedPickZoneId == zone.id;
                        var prevSeedBg = GUI.backgroundColor;
                        if (seedArmed) GUI.backgroundColor = IrocaColors.ActiveMaskTarget;
                        if (GUILayout.Button(seedArmed ? s_seedPickActiveContent : s_seedPickIdleContent,
                                GUILayout.Width(IrocaConsts.Layout.SmallButtonWidth)))
                        {
                            zone.EnsureId();
                            SeedPickZoneId = seedArmed ? null : zone.id;
                            // スポイトと排他（どちらもプレビューの素のクリックを取る）。
                            if (!seedArmed) EyedropperZoneId = null;
                            Repaint();
                        }
                        GUI.backgroundColor = prevSeedBg;
                    }

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

            // 変更先カラーは自動調整の入力なのでボタンの上（採色の直下）へ移した。
            // ここには自動調整が書き換える出力系スライダーだけを残す。
            zone.valueBlend = UndoHelper.Slider(this,
                new GUIContent(Localization.PatternPreserve, Localization.PatternPreserveTooltip),
                zone.valueBlend, 0f, 1f);
            zone.outputSaturation = UndoHelper.Slider(this,
                new GUIContent(Localization.OutputSaturation, Localization.OutputSaturationTooltip),
                zone.outputSaturation, 0f, 1f);

            // 基本の項目（色・許容範囲・連続領域・模様保持・出力彩度）だけを常時見せ、
            // 残りは「詳細設定」へ畳む（既定で閉じる）。旧・上級モード限定だった
            // マッチング距離の重みもこの中に入っている（モードトグル廃止。DrawZoneList 参照）。
            EditorGUILayout.Space(2);
            zone.detailFoldout = EditorGUILayout.Foldout(
                zone.detailFoldout,
                new GUIContent(Localization.ZoneDetailFoldout, Localization.ZoneDetailFoldoutTooltip),
                true);
            if (zone.detailFoldout)
                DrawZoneAdvancedParams(zone);

            EditorGUILayout.EndVertical();
            return removeRequested;
        }

        // 「詳細設定」を開いたときに出るパラメータ。
        //
        // 役割ごとに小見出しで束ねる。以前は「彩度制限」「彩度ガード」「シャドウ彩度低下」
        // 「シャドウ巻き込み最低彩度」「自動しきい値(無彩色判定)」が同じ平面に並んでおり、
        // どれも名前に“彩度”が入るのに効く対象が違うため、どれを触るべきか読み取れなかった
        // （2026-09-11 の UX 見直し）。並び順は「どの画素を選ぶか → 明部 → 暗部/無彩色 →
        // 選んだ画素をどう塗るか → 内部の距離重み」で、処理の流れと一致させている。
        private void DrawZoneAdvancedParams(ColorZone zone)
        {
            // ── 選択の範囲（どの画素を対象にするか） ──
            EditorGUILayout.LabelField(Localization.ZoneGroupSelection, EditorStyles.boldLabel);
            zone.edgeSoftness = UndoHelper.Slider(this,
                new GUIContent(Localization.EdgeSoftness, Localization.EdgeSoftnessTooltip),
                zone.edgeSoftness, 0f, 1f);
            zone.saturationStrictness = UndoHelper.Slider(this,
                new GUIContent(Localization.SaturationStrictness, Localization.SaturationStrictnessTooltip),
                zone.saturationStrictness, 0f, 1f);
            zone.saturationGuard = UndoHelper.Slider(this,
                new GUIContent(Localization.SaturationGuard, Localization.SaturationGuardTooltip),
                zone.saturationGuard, 0f, 1f);

            // ── ハイライト（光沢・明部の拾い方と塗り方） ──
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Localization.ZoneGroupHighlight, EditorStyles.boldLabel);

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
            zone.shadowValueFloor = UndoHelper.Slider(this,
                new GUIContent(Localization.ShadowValueFloor, Localization.ShadowValueFloorTooltip),
                zone.shadowValueFloor, 0f, 1f);
            zone.chromaThreshold = UndoHelper.Slider(this,
                new GUIContent(Localization.ChromaThreshold, Localization.ChromaThresholdTooltip),
                zone.chromaThreshold, 0f, 1f);
            zone.chromaCeiling = UndoHelper.Slider(this,
                new GUIContent(Localization.ChromaCeiling, Localization.ChromaCeilingTooltip),
                zone.chromaCeiling, 0f, 1f);

            // ── 色の写り方（選んだ画素をどう塗るか。選択範囲は変えない） ──
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Localization.ZoneGroupRecolor, EditorStyles.boldLabel);
            zone.autoRecolorAnchor = UndoHelper.Toggle(this,
                new GUIContent(Localization.AutoRecolorAnchor, Localization.AutoRecolorAnchorTooltip),
                zone.autoRecolorAnchor);

            // ── マッチング距離の重み（旧・上級モード限定。内部の距離式そのもの） ──
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Localization.ZoneGroupMatching, EditorStyles.boldLabel);
            zone.valueWeight = UndoHelper.Slider(this,
                new GUIContent(Localization.ValueWeight, Localization.ValueWeightTooltip),
                zone.valueWeight, 0f, 1f);
            zone.satDistWeight = UndoHelper.Slider(this,
                new GUIContent(Localization.SatDistWeight, Localization.SatDistWeightTooltip),
                zone.satDistWeight, 0f, 1f);
            zone.satRampScale = UndoHelper.Slider(this,
                new GUIContent(Localization.SatRampScale, Localization.SatRampScaleTooltip),
                zone.satRampScale, 0.01f, 0.5f);

            EditorGUILayout.Space(2);
            if (GUILayout.Button(new GUIContent(Localization.ResetZoneTuning, Localization.ResetZoneTuningTooltip)))
            {
                Undo.RegisterCompleteObjectUndo(this, "Reset Zone Tuning");
                zone.ResetTuningToDefault();
                MarkPreviewDirty();
            }
        }

        // 判定・描画は非 Layout パスでのみ行う（Layout パスの GetLastRect はダミー値のため）。
        private void HandleZoneReorderDrag(List<Rect> zoneRects)
        {
            // hotControl 用 ID を毎フレーム無条件に確保して IMGUI の ID 割り当てを安定させる。
            // MouseDown（カード描画中＝この呼び出しより前）は前フレームの値を読むが、ドラッグ中は
            // UI 構造が不変なので同値になる。
            _dragControlId = GUIUtility.GetControlID(FocusType.Passive);

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

            int slot = _dragZoneIndex;
            for (int k = 0; k < _dragZoneIndex; k++)
            {
                if (projTop < zoneRects[k].yMax - overlapTrigger) { slot = k; break; }
            }
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

                EditorGUI.DrawRect(src, new Color(0f, 0f, 0f, 0.18f));

                float lineY = slot < zoneRects.Count
                    ? zoneRects[slot].yMin
                    : zoneRects[zoneRects.Count - 1].yMax;
                EditorGUI.DrawRect(new Rect(src.xMin, lineY - 1.5f, src.width, 3f), accent);

                float gh = EditorGUIUtility.singleLineHeight + 8f;
                float gy = evt.mousePosition.y - _dragGrabOffsetY;
                Rect ghost = new Rect(src.xMin, gy, src.width, gh);
                Color fill = accent; fill.a = 0.35f;
                EditorGUI.DrawRect(ghost, fill);
                DrawRectOutline(ghost, accent, 1f);

                var dz = zones[_dragZoneIndex];
                Rect swatch = new Rect(ghost.x + 22f, ghost.y + 5f, 14f, gh - 10f);
                Color sw = dz.targetColor; sw.a = 1f;
                EditorGUI.DrawRect(swatch, sw);
                DrawRectOutline(swatch, new Color(0f, 0f, 0f, 0.4f), 1f);
                string gname = string.IsNullOrEmpty(dz.name) ? Localization.UnnamedZone : dz.name;
                GUI.Label(new Rect(swatch.xMax + 6f, ghost.y + 3f, ghost.width - 64f, EditorGUIUtility.singleLineHeight),
                    new GUIContent("☰  " + gname), EditorStyles.boldLabel);
            }
            else if (evt.type == EventType.MouseDrag)
            {
                if (GUIUtility.hotControl == _dragControlId)
                {
                    evt.Use();
                    Repaint();
                }
            }
            else if (evt.type == EventType.MouseUp)
            {
                // 自分がキャプチャした MouseUp のときだけ確定する（ウィンドウ外リリースでも hotControl
                // 経由で必ずここに届く）。hotControl が自分のものでない無関係な MouseUp では並べ替えない。
                if (GUIUtility.hotControl == _dragControlId)
                {
                    GUIUtility.hotControl = 0;
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
        }

        private static void DrawRectOutline(Rect r, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMax - thickness, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, thickness, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.yMin, thickness, r.height), color);
        }
    }
}
