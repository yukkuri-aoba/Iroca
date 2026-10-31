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

        // 「スポイトで変えたい色を選ぶ / 別の色を追加」のクリックで作るゾーン（NewZoneEyedropperId 参照）。
        // プレビューの MouseDown で受け、ゾーンの追加と色の設定を次の Layout イベントで一緒に行う。
        private bool _pendingNewZoneFromSample;
        private Color _pendingNewZoneColor;
        private Vector2 _pendingNewZoneUV;

        // 「いま直しているゾーン」= マスクの編集対象(_maskView.activeMaskTarget)。ブラシと、どのゾーンも塗って
        // いない所での右クリックの「ここも塗る / ここは塗らない / この部分だけ塗る」は、ここに当たる(塗られている所の
        // 右クリックは、塗っているゾーンに当たる。PreviewView.ShowPreviewContextMenu)。専用の選択操作は
        // 足さず(手数を増やさない)、最後に触ったゾーン(作った・カードのどこかを押した。スポイトや自動調整も
        // カードのボタンから始まる)へ追従させる。どれかはカードの枠で示す(ゾーンが 2 つ以上のとき。DrawZoneCard)。
        private void SetCurrentZoneIndex(int index)
        {
            if (_maskView == null || index < 0 || index >= zones.Count || _maskView.activeMaskTarget == index) return;
            _maskView.activeMaskTarget = index;
            _maskView.maskDirty = true;     // 明るく出すマスク(編集中)が変わる
            Repaint();
        }

        // 前の描画で記録したゾーンカードの矩形(カードを押したらそのゾーンを直す対象にする判定用)。
        [System.NonSerialized] private List<Rect> _zoneCardRects;

        internal void RequestNewZoneFromSample(Color picked, Vector2 uv)
        {
            _pendingNewZoneFromSample = true;
            _pendingNewZoneColor = picked;
            _pendingNewZoneUV = uv;
            Repaint();
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
                // ゾーンとそのマスクを消す(Undo 1 ステップで両方戻る。EditingSession.RemoveZoneAt)。
                if (Editing.RemoveZoneAt(idx))
                    _maskView.OnZoneRemoved(idx);
            }
            if (_pendingAddZone)
            {
                _pendingAddZone = false;
                SetCurrentZoneIndex(Editing.AddZone());
            }
            if (_pendingNewZoneFromSample)
            {
                _pendingNewZoneFromSample = false;
                SetCurrentZoneIndex(Editing.AddZoneFromSample(_pendingNewZoneColor, _pendingNewZoneUV));
                zonesFoldout = true;
            }
            if (_pendingReorderFrom >= 0 && _pendingReorderTo >= 0)
            {
                int from = _pendingReorderFrom;
                int to = _pendingReorderTo;
                _pendingReorderFrom = -1;
                _pendingReorderTo = -1;
                // 並び順(優先度)だけを変え、編集中マスクターゲット(index 参照)を移動に追従させる。
                if (Editing.MoveZone(from, to))
                    _maskView.OnZoneReordered(from, to);
            }
        }

        // ゾーンリストのヘッダ行は毎フレーム×ゾーン数で描画されるため、GUIStyle/GUIContent を
        // 静的キャッシュして毎フレームのアロケーションを避ける。文字列は Localization 由来なので、
        // 言語切替時(CurrentLanguage 変化)だけ再構築する。GUIContent は IMGUI が即時消費するため
        // 単一インスタンスの共有で問題ない。
        private static GUIStyle s_dragHandleStyle;
        private static LanguageMode s_zoneCacheLang = (LanguageMode)(-1);
        private static GUIContent[] s_languageLabels;
        private static GUIContent s_dragHandleContent, s_zoneEnabledContent, s_zoneNameContent,
            s_removeZoneContent, s_zoneSoloContent,
            s_autoTuneEnabledContent, s_autoTuneDisabledContent,
            s_eyedropperIdleContent, s_eyedropperActiveContent,
            s_seedPickIdleContent, s_seedPickActiveContent,
            s_sampleUvMissingContent;

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
            s_languageLabels = new[]
            {
                new GUIContent(Localization.LangAuto, Localization.LanguageToolbarTooltip),
                new GUIContent(Localization.LangJapanese, Localization.LanguageToolbarTooltip),
                new GUIContent(Localization.LangEnglish, Localization.LanguageToolbarTooltip),
            };
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

            // 並び順＝優先度（重なりは上のゾーンのみ適用）。説明は ☰ ハンドルのツールチップ
            // (ZoneDragHandleTooltip) に集約し、常時表示の HelpBox は置かない。

            if (_dragZoneIndex >= zones.Count) _dragZoneIndex = -1;

            // ドラッグハンドル用スタイルとヘッダ行の GUIContent は静的キャッシュを使う
            // (毎フレーム×ゾーン数のアロケーション回避。言語切替時のみ再構築)。
            EnsureZoneListCache();
            var zoneRects = new List<Rect>(zones.Count);

            // カードのどこかを押したら、そのゾーンを「いま直しているゾーン」にする。カードの中の操作より
            // 先に見るので、前の描画で記録した矩形で判定する(イベントは消費しない＝押した操作はそのまま効く)。
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && _zoneCardRects != null)
            {
                for (int i = 0; i < _zoneCardRects.Count && i < zones.Count; i++)
                {
                    if (_zoneCardRects[i].Contains(ev.mousePosition)) { SetCurrentZoneIndex(i); break; }
                }
            }

            int removeIndex = -1;
            for (int i = 0; i < zones.Count; i++)
            {
                if (DrawZoneCard(zones[i], i)) removeIndex = i;
                // ドロップ位置判定・インジケータ描画用に、このゾーン全体の矩形を記録。
                // GetLastRect は Layout パスではダミー値だが、判定・描画は非 Layout パスでのみ行う。
                zoneRects.Add(GUILayoutUtility.GetLastRect());
                EditorGUILayout.Space(2);
            }

            if (ev.type == EventType.Repaint) _zoneCardRects = zoneRects;

            HandleZoneReorderDrag(zoneRects);

            if (removeIndex >= 0)
            {
                _pendingRemoveZoneIndex = removeIndex;
                Repaint();
            }

            DrawAddColorRow();

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        // 色を足す入口は「スポイトで色を選ぶ → プレビューをクリック」に一本化する（NewZoneEyedropperId 参照）。
        // 有効なゾーンが無いときは大きく出して開始点にし（条件は NextStepHint の NextStepAddZone と揃える）、
        // あるときは「別の色を追加」として 2 色目以降も同じ操作で足す。以前は 2 色目から
        // 「+ ゾーン追加」→「スポイト」→ クリックの 3 手で、1 色目と操作が違っていた（2026-10-05）。
        // 空のゾーンを作る従来の入口は、色を数値で入れたいとき用に横へ小さく残す。
        private void DrawAddColorRow()
        {
            bool first = !HasEnabledZone;
            bool canSample = CanReadSource(SourceTexture);
            bool armed = EyedropperZoneId == NewZoneEyedropperId;
            var height = GUILayout.Height(first
                ? EditorGUIUtility.singleLineHeight * 1.8f
                : EditorGUIUtility.singleLineHeight + 2f);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!canSample))
            {
                var content = TempContent.Of(
                    armed ? Localization.StartWithEyedropperActive
                          : first ? Localization.StartWithEyedropper : Localization.AddColorWithEyedropper,
                    Localization.StartWithEyedropperTooltip);
                // MinWidth(0): 狭い設定列でも右の「+ 空のゾーン」を押し出さず、こちらが縮む。
                if (ArmedToggleButton(content, armed, height, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true)))
                {
                    // トグル: 武装↔解除。シード指定と排他（どちらもプレビューの素のクリックを取る）。
                    EyedropperZoneId = armed ? null : NewZoneEyedropperId;
                    if (!armed) SeedPickZoneId = null;
                    Repaint();
                }
            }

            if (GUILayout.Button(TempContent.Of(Localization.AddZone, Localization.AddZoneTooltip),
                    height, GUILayout.ExpandWidth(false)))
            {
                _pendingAddZone = true;
                Repaint();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 武装トグルボタン（色の追加・ゾーンのスポイト・シード指定）。武装中は背景を強調色にし、押されたら true を返す。
        /// 武装は出力を変えないので、ボタンが立てる GUI.changed を押す前の値へ戻す。
        /// 設定列は ChangeCheck で囲まれており、立ったままだと押すたびにプレビューを再生成していた。
        /// 押されたときの処理（武装 id の付け替え・Repaint）は GUI.changed にも背景色にも触れないので、
        /// 戻した後に呼び出し側で行う。DisabledScope は呼び出し側に置く。
        /// </summary>
        private static bool ArmedToggleButton(GUIContent content, bool armed, params GUILayoutOption[] options)
        {
            var prevBg = GUI.backgroundColor;
            if (armed) GUI.backgroundColor = IrocaColors.ActiveMaskTarget;
            bool prevChanged = GUI.changed;
            bool clicked = GUILayout.Button(content, options);
            GUI.changed = prevChanged;
            GUI.backgroundColor = prevBg;
            return clicked;
        }

        // 1 ゾーン分のカード（ヘッダ行＋採色/変更先＋自動調整＋許容範囲＋模様保持/出力彩度＋
        // 畳んだ詳細設定）を描画する。
        // 戻り値 true = このカードの削除(×)ボタンが押された。
        private bool DrawZoneCard(ColorZone zone, int index)
        {
            bool removeRequested = false;
            zone.EnsureId();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (DrawZoneHeaderRow(zone, index)) removeRequested = true;

            // ゾーン別マスクの編集は除外マスク欄の「編集対象」プルダウン＋「ブラシで編集」に
            // 一本化した（かつてここにあった専用ボタンは経路重複のため削除）。

            // UV矩形モードは実装継続中のため当面 UI から非表示。
            // zone.mode = UndoHelper.EnumPopup(UndoTarget,
            //     new GUIContent(Localization.SelectionMode, Localization.SelectionModeTooltip),
            //     zone.mode);

            DrawZoneColorRows(zone);

            // スポイト位置が無いときだけ知らせる。自動調整はこの位置に AI マスク提案をかけて証拠にするため、
            // 位置が無いゾーン（カラーフィールドで色を決めた／Undo で無効化された）では
            // 導出経路が変わる。押してから通知で知るのでは遅いので、事前に見えるようにする。
            // 位置があるのは普通の状態なので、常時の 1 行は出さない（2026-10-04、カードの常時表示を減らした）。
            if (zone.HasSampleColor && !zone.HasSampleUV)
            {
                // 折り返しあり: 英語文は狭い設定列の 1 行に収まらず、末尾（肝心の
                // "AI suggestion"）が欠けていた。GUILayout.Label は折り返し後の高さを確保する
                // （EditorGUILayout.LabelField は 1 行固定高で、折り返した 2 行目が隠れる）。
                GUILayout.Label(s_sampleUvMissingContent, EditorStyles.wordWrappedMiniLabel);
            }

            DrawZoneAutoTuneButton(zone);

            zone.tolerance = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.Tolerance, Localization.ToleranceTooltip),
                zone.tolerance, 0f, 1f);

            // UV矩形モードは実装継続中のため当面 UI から非表示。
            // else
            // {
            //     EditorGUILayout.LabelField(
            //         new GUIContent(Localization.UVRect, Localization.UVRectTooltip));
            //     using (new EditorGUI.IndentLevelScope())
            //     {
            //         float x = UndoHelper.Slider(UndoTarget, "X", zone.uvRect.x, 0f, 1f);
            //         float y = UndoHelper.Slider(UndoTarget, "Y", zone.uvRect.y, 0f, 1f);
            //         float w = UndoHelper.Slider(UndoTarget, "W", zone.uvRect.width, 0f, 1f);
            //         float h = UndoHelper.Slider(UndoTarget, "H", zone.uvRect.height, 0f, 1f);
            //         zone.uvRect = new Rect(x, y, w, h);
            //     }
            // }

            // 変更先カラーは自動調整の入力なのでボタンの上（採色の直下）へ移した。
            // ここには自動調整が書き換える出力系スライダーだけを残す。
            zone.valueBlend = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.PatternPreserve, Localization.PatternPreserveTooltip),
                zone.valueBlend, 0f, 1f);
            zone.outputSaturation = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.OutputSaturation, Localization.OutputSaturationTooltip),
                zone.outputSaturation, 0f, 1f);

            // 基本の項目（色・自動調整・許容範囲・模様保持・出力彩度）だけを常時見せ、
            // 残り（連続領域モードを含む）は「詳細設定」へ畳む（既定で閉じる）。旧・上級モード限定だった
            // マッチング距離の重みもこの中に入っている（モードトグル廃止。DrawZoneList 参照）。
            EditorGUILayout.Space(2);
            zone.detailFoldout = EditorGUILayout.Foldout(
                zone.detailFoldout,
                TempContent.Of(Localization.ZoneDetailFoldout, Localization.ZoneDetailFoldoutTooltip),
                true);
            if (zone.detailFoldout)
                DrawZoneAdvancedParams(zone);

            EditorGUILayout.EndVertical();

            // いま直しているゾーン(ブラシと、塗られていない所での右クリックの宛先)を枠で示す。ゾーンが 1 つなら自明なので出さない。
            if (Event.current.type == EventType.Repaint && zones.Count >= 2
                && _maskView != null && _maskView.activeMaskTarget == index)
            {
                DrawRectOutline(GUILayoutUtility.GetLastRect(), IrocaColors.ActiveMaskTarget, 2f);
            }
            return removeRequested;
        }

        // カードの見出し行（ドラッグハンドル・有効・名前・ソロ・削除）。
        // 戻り値 true = 削除(×)ボタンが押された。
        private bool DrawZoneHeaderRow(ColorZone zone, int index)
        {
            bool removeRequested = false;
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
            zone.enabled = UndoHelper.ToggleLeft(UndoTarget,
                s_zoneEnabledContent,
                zone.enabled, GUILayout.Width(rowH));
            // MinWidth(0): ラベル無しでも EditorGUILayout.TextField(GUIContent, ...) は
            // 「labelWidth + fieldWidth + 5」を最小幅として要求する。ソロボタンを足した
            // この行は設定列で最も幅を要求し、スクロール内容がカラムより広がって全行の右端が
            // 縦スクロールバーの下に隠れていた。名前欄だけを伸縮させて行をカラム内に収める。
            zone.name = UndoHelper.TextField(UndoTarget,
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
                if (GUILayout.Button(s_zoneSoloContent, GUILayout.Width(IrocaUiConsts.Layout.SmallButtonWidth)))
                {
                    zone.EnsureId();
                    SetSoloZone(soloed ? null : zone.id);
                    Repaint();
                }
                GUI.backgroundColor = prevSoloBg;
            }

            if (GUILayout.Button(s_removeZoneContent, GUILayout.Width(IrocaUiConsts.Layout.RemoveButtonWidth)))
            {
                removeRequested = true;
            }
            EditorGUILayout.EndHorizontal();
            return removeRequested;
        }

        // カードの色の 2 行（サンプルカラー＋スポイト、変更先カラー）。
        private void DrawZoneColorRows(ColorZone zone)
        {
            // ColorPick UI（常時表示）。カラーフィールドとプレビュー直接スポイトは
            // どちらも sampleColor を決める手段なので 1 行に統合してカードの行数を抑える。
            // 色の 2 行だけラベル幅を詰める。設定列のラベル幅は最長ラベルに合わせて広がるので
            // （英語表示で顕著）、スポイトボタン(96px)と並ぶサンプルカラーの色見本が ~20px まで
            // 潰れていた。色見本に最低 ColorSwatchMinWidth を残し、変更先カラーも同じ幅にして
            // 2 つの色見本の左端を揃える。どちらのラベルも短いので詰めても欠けない。
            const float ColorSwatchMinWidth = 56f;
            const float CardChrome = 16f; // helpBox の余白＋ボタン間隔
            float prevColorRowsLabelWidth = EditorGUIUtility.labelWidth;
            if (_settingsContentWidth > 1f)
            {
                EditorGUIUtility.labelWidth = Mathf.Min(prevColorRowsLabelWidth, Mathf.Max(95f,
                    _settingsContentWidth - CardChrome
                    - IrocaUiConsts.Layout.EyedropperButtonWidth - ColorSwatchMinWidth));
            }

            EditorGUILayout.BeginHorizontal();
            Color prevSampleColor = zone.sampleColor;
            // MinWidth(0): 見出し行の名前欄と同じ理由（ラベル付きフィールドの最小幅＋スポイト
            // ボタン幅が狭いカラムの内容幅を超える）。カラーフィールド側を縮めて行を収める。
            zone.sampleColor = UndoHelper.ColorField(UndoTarget,
                TempContent.Of(Localization.SampleColor, Localization.SampleColorTooltip),
                zone.sampleColor,
                GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            if (zone.sampleColor != prevSampleColor)
            {
                // スポイト/カラーフィールドで色を取った＝サンプル指定済み。意図的な白選択を
                // 「未指定の白」と区別し、白い服・白髪などでも自動調整を許可できるようにする。
                zone.sampleColorSet = true;
                // カラーフィールドで変えた色はクリック画素の色ではないので、スポイト位置
                // （自動調整が AI 提案の証拠を取る位置）を無効化する。次のスポイトで付き直す。
                // UndoHelper.ColorField が代入前に記録済みなので、Undo で色と位置が対で戻る。
                zone.ClearSampleUV();
                // サンプルカラーが変わったら、自動トーン抽出で生成済みの内部サンプルは
                // 古いパーツのものになるためクリアする（次の自動調整で作り直す）。
                if (zone.extraSamples != null && zone.extraSamples.Count > 0)
                    zone.extraSamples.Clear();
                Editing.NotifyChanged(EditChange.Content);
            }

            // カラーピッカーを経由せず、プレビュー上のクリックでこのゾーンのサンプルカラーを
            // 実テクスチャ画素から直接取得する（PreviewView 側が実画素を読む）。画素を取れないときだけ押せない。
            {
                bool canSample = CanReadSource(SourceTexture)
                    && zone.mode == SelectionMode.ColorPick;
                bool armed = !string.IsNullOrEmpty(zone.id) && EyedropperZoneId == zone.id;
                using (new EditorGUI.DisabledScope(!canSample))
                {
                    if (ArmedToggleButton(armed ? s_eyedropperActiveContent : s_eyedropperIdleContent, armed,
                        GUILayout.Width(FitButtonWidth(IrocaUiConsts.Layout.EyedropperButtonWidth,
                            s_eyedropperIdleContent.text, s_eyedropperActiveContent.text))))
                    {
                        // トグル：武装↔解除。武装は id で保持し、並べ替え・削除で別ゾーンを指さないようにする
                        // （クリックで一発取得→自動解除）。
                        zone.EnsureId();
                        EyedropperZoneId = armed ? null : zone.id;
                        // シード指定と排他（どちらもプレビューの素のクリックを取る）。
                        if (!armed) SeedPickZoneId = null;
                        Repaint();
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            // 自動調整の直上に置く。ZoneAutoTuner は sampleColor だけでなく targetColor も
            // 入力に取り（両者の明度差から模様保持 valueBlend を決める）、変更先が未決のまま
            // 押すと既定色を前提とした結果になる。「元の色 → 変更先の色 → 自動調整」の順に
            // 並べることで、入力が全てボタンの上・書き換わる項目が全て下に揃う。
            Color prevTargetColor = zone.targetColor;
            zone.targetColor = UndoHelper.ColorField(UndoTarget,
                TempContent.Of(Localization.TargetColor, Localization.TargetColorTooltip),
                zone.targetColor);
            // 変更先を選んだ＝手順案内を「自動調整」へ進める（白を選んだ場合も既定の白と区別する）。
            // UndoHelper.ColorField が代入前に記録済みなので、Undo で色とフラグが対で戻る。
            if (zone.targetColor != prevTargetColor) zone.targetColorSet = true;
            EditorGUIUtility.labelWidth = prevColorRowsLabelWidth;
        }

        // スポイト1点から、パーツの濃淡（暗部/中間/明部）を内部で自動サンプリングして
        // 許容範囲などを最適化する。ユーザーが濃淡を手で採り直す必要はない。
        private void DrawZoneAutoTuneButton(ColorZone zone)
        {
            bool canTune =
                CanReadSource(SourceTexture)
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

        // 連続領域モード（つながった塊だけに絞る）とシード。既定（自動アンカリング・シードなし）のまま
        // 使う人がほとんどなので、ゾーンカードの常時表示から詳細設定の「選択の範囲」へ移した（2026-10-04）。
        // シードはプレビューの Shift+クリックでも置け、置いた位置はプレビューに目印が出る。
        private void DrawZoneFloodFillParams(ColorZone zone)
        {
            // 既定は自動アンカリング(シード不要)。確信度の高い芯を含む連結領域だけ残し、
            // 物理的に離れた同色パーツや背景へのにじみを自動除去する。シードは任意の上書き。
            bool prevUseFloodFill = zone.useFloodFill;
            zone.useFloodFill = UndoHelper.Toggle(UndoTarget,
                TempContent.Of(Localization.UseFloodFill, Localization.UseFloodFillTooltip),
                zone.useFloodFill);
            if (zone.useFloodFill != prevUseFloodFill) Editing.NotifyChanged(EditChange.Content);

            if (zone.useFloodFill)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    string seedLabel = zone.seedUV.x >= 0f
                        ? $"UV ({zone.seedUV.x:F3}, {zone.seedUV.y:F3})"
                        : Localization.FloodFillSeedNotSet;
                    // 値とボタンは 2 行に分ける。1 行（ラベル＋値＋指定＋クリア）だと、設定列の
                    // 下限幅では値に ~30px しか残らず、「自動 (シードなし)」も UV 座標も
                    // 途中で切れて読めなかった。MinWidth(0) は値テキストの実幅を行の最小幅に
                    // しないため（長い UV 文字列で行がカラムより広がり、右端が隠れる）。
                    EditorGUILayout.LabelField(
                        TempContent.Of(Localization.FloodFillSeedPoint, Localization.FloodFillSeedHint),
                        TempContent.Of(seedLabel, Localization.FloodFillSeedHint),
                        GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));

                    // ボタン行は値と同じ列（ラベル幅ぶん右）から始め、どの項目の操作かを揃えて示す。
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUIUtility.labelWidth);

                    // シードを「このゾーンに」置くための武装ボタン（スポイトと同じ一発取得）。
                    // Shift+クリック経路は残すが、あちらは対象ゾーンを「マスク編集対象、無ければ
                    // 先頭の該当ゾーン」と暗黙に選ぶため、どのゾーンへ入るかが画面から読めなかった。
                    {
                        bool seedArmed = !string.IsNullOrEmpty(zone.id) && SeedPickZoneId == zone.id;
                        if (ArmedToggleButton(seedArmed ? s_seedPickActiveContent : s_seedPickIdleContent, seedArmed,
                                GUILayout.MinWidth(0)))
                        {
                            zone.EnsureId();
                            SeedPickZoneId = seedArmed ? null : zone.id;
                            // スポイトと排他（どちらもプレビューの素のクリックを取る）。
                            if (!seedArmed) EyedropperZoneId = null;
                            Repaint();
                        }
                    }

                    using (new EditorGUI.DisabledScope(zone.seedUV.x < 0f))
                    {
                        if (GUILayout.Button(
                            TempContent.Of(Localization.FloodFillClear, Localization.FloodFillClearTooltip),
                            GUILayout.MinWidth(0)))
                        {
                            Editing.ClearFloodFillSeed(zone);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

        }

        // 開発者向けの設定（ハイライト白寄せ合成とその自動補正・マッチング距離の重み）を出すか。
        // どれも既定 OFF／内部の距離式そのもので、普段の色替えで触る必要が無いのに詳細設定の項目数を
        // 増やし、どれを触ればよいかを分かりにくくしていたので既定では隠す（2026-10-04）。
        // 切り替えはウィンドウのタブの「⋮」メニュー（AddItemsToMenu）。自動調整はこれらを書き換えないが、
        // プリセットで既定から変わっているゾーンでは効いている値が見えなくならないよう、設定に関係なく出す。
        private const string ShowDeveloperSettingsPrefKey = IrocaPrefs.ShowDeveloperSettings;
        private static bool? s_showDeveloperSettings;
        private static bool ShowDeveloperSettings
        {
            get => s_showDeveloperSettings ??= EditorPrefs.GetBool(ShowDeveloperSettingsPrefKey, false);
            set
            {
                s_showDeveloperSettings = value;
                EditorPrefs.SetBool(ShowDeveloperSettingsPrefKey, value);
            }
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent(Localization.ShowDeveloperSettings), ShowDeveloperSettings, () =>
            {
                ShowDeveloperSettings = !ShowDeveloperSettings;
                Repaint();
            });
        }

        private static ColorZone s_zoneDefaults;
        private static ColorZone ZoneDefaults => s_zoneDefaults ??= new ColorZone();

        // 既定から変わっていたので出したゾーン（id）。一度出したらウィンドウを開いている間は出し続ける。
        // 値を既定へ戻した瞬間に項目ごと消えると、ドラッグ中のスライダーが途中で無くなる。
        [System.NonSerialized] private HashSet<string> _devParamsRevealed;

        private bool ShowDevParams(ColorZone zone, string group, bool isDefault)
        {
            if (ShowDeveloperSettings) return true;
            _devParamsRevealed ??= new HashSet<string>();
            string key = (zone.id ?? "") + "/" + group;
            if (!isDefault) _devParamsRevealed.Add(key);
            return _devParamsRevealed.Contains(key);
        }

        private static bool HighlightWashIsDefault(ColorZone z) =>
            z.applyHighlightWash == ZoneDefaults.applyHighlightWash
            && z.autoHighlightSample == ZoneDefaults.autoHighlightSample;

        private static bool MatchingWeightsAreDefault(ColorZone z) =>
            Mathf.Approximately(z.valueWeight, ZoneDefaults.valueWeight)
            && Mathf.Approximately(z.satDistWeight, ZoneDefaults.satDistWeight)
            && Mathf.Approximately(z.satRampScale, ZoneDefaults.satRampScale);

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
            DrawZoneFloodFillParams(zone);
            zone.edgeSoftness = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.EdgeSoftness, Localization.EdgeSoftnessTooltip),
                zone.edgeSoftness, 0f, 1f);
            zone.saturationStrictness = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.SaturationStrictness, Localization.SaturationStrictnessTooltip),
                zone.saturationStrictness, 0f, 1f);
            zone.saturationGuard = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.SaturationGuard, Localization.SaturationGuardTooltip),
                zone.saturationGuard, 0f, 1f);
            zone.holeFillMargin = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.HoleFillMargin, Localization.HoleFillMarginTooltip),
                zone.holeFillMargin, 0f, 1f);

            // ── ハイライト（光沢・明部の拾い方と塗り方） ──
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Localization.ZoneGroupHighlight, EditorStyles.boldLabel);

            zone.highlightRecovery = UndoHelper.Toggle(UndoTarget,
                TempContent.Of(Localization.HighlightRecovery, Localization.HighlightRecoveryTooltip),
                zone.highlightRecovery);

            // ハイライト帯の拡張は「ハイライト補助」が ON のときのみ有効なので、
            // OFF のときはグレーアウトして関係を明示する。
            using (new EditorGUI.DisabledScope(!zone.highlightRecovery))
            {
                EditorGUI.indentLevel++;
                zone.highlightBandExpand = UndoHelper.Toggle(UndoTarget,
                    TempContent.Of(Localization.HighlightBandExpand, Localization.HighlightBandExpandTooltip),
                    zone.highlightBandExpand);
                EditorGUI.indentLevel--;
            }

            // 白寄せ合成とその自動補正は開発者向け（ShowDeveloperSettings 参照）。
            if (ShowDevParams(zone, "wash", HighlightWashIsDefault(zone)))
            {
                zone.applyHighlightWash = UndoHelper.Toggle(UndoTarget,
                    TempContent.Of(Localization.ApplyHighlightWash, Localization.ApplyHighlightWashTooltip),
                    zone.applyHighlightWash);

                // 俯瞰スポイト補正(wash サンプル自動導出)は「ハイライト白寄せ合成」が ON の
                // ときのみ意味を持つので、OFF のときはグレーアウトして関係を明示する。
                using (new EditorGUI.DisabledScope(!zone.applyHighlightWash))
                {
                    EditorGUI.indentLevel++;
                    zone.autoHighlightSample = UndoHelper.Toggle(UndoTarget,
                        TempContent.Of(Localization.AutoHighlightSample, Localization.AutoHighlightSampleTooltip),
                        zone.autoHighlightSample);
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Localization.ShadowHighlightSection, EditorStyles.boldLabel);

            zone.shadowDesaturation = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.ShadowDesaturation, Localization.ShadowDesaturationTooltip),
                zone.shadowDesaturation, 0f, 1f);
            zone.shadowForgivenessSatMin = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.ShadowForgivenessSatMin, Localization.ShadowForgivenessSatMinTooltip),
                zone.shadowForgivenessSatMin, 0f, 1f);
            zone.shadowValueFloor = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.ShadowValueFloor, Localization.ShadowValueFloorTooltip),
                zone.shadowValueFloor, 0f, 1f);
            zone.partSatCeiling = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.PartSatCeiling, Localization.PartSatCeilingTooltip),
                zone.partSatCeiling, 0f, 1f);
            zone.partHueBand = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.PartHueBand, Localization.PartHueBandTooltip),
                zone.partHueBand, 0f, 0.5f);
            zone.chromaThreshold = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.ChromaThreshold, Localization.ChromaThresholdTooltip),
                zone.chromaThreshold, 0f, 1f);
            zone.chromaCeiling = UndoHelper.Slider(UndoTarget,
                TempContent.Of(Localization.ChromaCeiling, Localization.ChromaCeilingTooltip),
                zone.chromaCeiling, 0f, 1f);

            // ── 色の写り方（選んだ画素をどう塗るか。選択範囲は変えない） ──
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Localization.ZoneGroupRecolor, EditorStyles.boldLabel);
            zone.autoRecolorAnchor = UndoHelper.Toggle(UndoTarget,
                TempContent.Of(Localization.AutoRecolorAnchor, Localization.AutoRecolorAnchorTooltip),
                zone.autoRecolorAnchor);

            // ── マッチング距離の重み（内部の距離式そのもの。開発者向け、ShowDeveloperSettings 参照） ──
            if (ShowDevParams(zone, "weights", MatchingWeightsAreDefault(zone)))
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(Localization.ZoneGroupMatching, EditorStyles.boldLabel);
                zone.valueWeight = UndoHelper.Slider(UndoTarget,
                    TempContent.Of(Localization.ValueWeight, Localization.ValueWeightTooltip),
                    zone.valueWeight, 0f, 1f);
                zone.satDistWeight = UndoHelper.Slider(UndoTarget,
                    TempContent.Of(Localization.SatDistWeight, Localization.SatDistWeightTooltip),
                    zone.satDistWeight, 0f, 1f);
                zone.satRampScale = UndoHelper.Slider(UndoTarget,
                    TempContent.Of(Localization.SatRampScale, Localization.SatRampScaleTooltip),
                    zone.satRampScale, 0.01f, 0.5f);
            }

            EditorGUILayout.Space(2);
            if (GUILayout.Button(TempContent.Of(Localization.ResetZoneTuning, Localization.ResetZoneTuningTooltip)))
            {
                Editing.ResetZoneTuning(zone);
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
                    TempContent.Of("☰  " + gname), EditorStyles.boldLabel);
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
