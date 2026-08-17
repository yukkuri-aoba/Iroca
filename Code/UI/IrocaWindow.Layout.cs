// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    public partial class IrocaWindow
    {
        [SerializeField] private bool processingFoldout = true;

        private Vector2 scrollPos;
        private Vector2 leftScrollPos;
        // プレビュー側の縦オーバーフロー用。プレビュー枠はカラム高に収まるよう動的に縮む
        // (PreviewView.availableColumnHeight)が、下限(MinViewportHeight)まで縮んでも
        // 収まらない低ウィンドウではあふれるため、外側にも ScrollView を挟んで逃がす。
        private Vector2 rightScrollPos;

        // 横並びモードのテクスチャフィールド込み上部（toolbar＋テクスチャフィールド）の実高を
        // Repaint 時に実測しキャッシュする。WorkflowHint/ReadWriteError の HelpBox は可変高で
        // 固定見積もりに入らないため、前フレーム Repaint の実測値を今フレームの horizH 算出に使う
        // （Layout/Repaint で同一値→GUILayout 整合）。0（未計測）時は決定論フォールバックを使う。
        // PreviewView._viewportWidth と同方針。
        [System.NonSerialized] private float _sideBySideTopHeight;

        // ジョブオーバーレイ（進捗バー＋キャンセル）の実測高。オーバーレイは DisabledScope の外・
        // 本体レイアウトの後ろに積まれるため、その分を availableContentH から引かないと
        // キャンセルボタンがウィンドウ下端で切れて押せなくなる（ジョブ中の唯一の脱出口が消える）。
        // _sideBySideTopHeight と同方針で前フレーム Repaint の実測値を使う（定数で見積もると
        // オーバーレイの中身を変えたときに黙ってずれる）。0（未計測）時は決定論フォールバック。
        [System.NonSerialized] private float _jobOverlayHeight;

        /// <summary>
        /// ジョブ実行中で UI 操作を止めるべきか。エクスポート中と、手動実行の自動調整中は
        /// 操作を受け付けない（かんたんモードの裏実行は妨げない）。
        /// 別ウィンドウへ切り出したプレビュー(IrocaPreviewWindow)も同じ条件で無効化し、
        /// 本体が止まっている間にプレビュー上のペイント/スポイトだけ通ってしまうのを防ぐ。
        /// </summary>
        internal bool IsJobBlockingUI =>
            (_exportView != null && _exportView.IsExporting)
            || (_autoTuneJob.IsRunning && _autoTuneIsManual);

        private void OnGUI()
        {
            // Ctrl+Z / Ctrl+Y は Unity 標準 Undo に統合済みのため、独自処理は不要。
            ProcessPendingZoneChanges();

            // スポイト武装中にテクスチャが外れた／対象ゾーンが消えたら解除（クリックで解けなくなるのを防ぐ）。
            if (!string.IsNullOrEmpty(_eyedropperZoneId) &&
                (sourceTexture == null || FindZoneById(_eyedropperZoneId) == null))
                _eyedropperZoneId = null;
            // かんたんモードで予約された自動調整を、デバウンス経過後に裏で実行する。
            // ── 自動調整はまだ実用段階でないため無効化（2026-06 一時対応）。再有効化時にコメントを外す。
            // ProcessPendingAutoTune();

            // 英語表示が初めて使われたときに AI 機械翻訳である旨を一度だけ告知する。
            // Layout イベント時のみ実行し、描画途中のモーダル表示を避ける。
            if (Event.current.type == EventType.Layout)
                Localization.MaybeShowEnglishTranslationNotice();

            // ジョブ実行中はウィンドウ内 UI を全て無効化する。
            // ただしジョブのオーバーレイ（進捗バー＋キャンセル）は DisabledScope の外で
            // 描画し、キャンセルだけは押せるようにする。
            // 自動調整は「手動実行（ボタン）」のときだけウィンドウ全体をブロックする。
            // かんたんモードの自動実行は裏で走らせ、操作を妨げない。
            bool blocking = IsJobBlockingUI;

            // ヘッダーは DisabledScope の外に置く（言語切替・クレジットはジョブ中でも安全）。
            // ただしリセットはセッション状態を破壊的に書き換えるので、ジョブ完了時の apply と
            // 競合しないよう blocking を渡してヘッダー内で個別に無効化する。
            DrawHeader(blocking);

            EditorGUI.BeginDisabledGroup(blocking);

            bool sideBySide = position.width >= IrocaConsts.Layout.SideBySideMinWidth;

            // position.height はウィンドウ枠（タイトル/タブバー）を含むため、
            // 実描画領域はそれより低い。エクスポートが画面外に押し出されないよう安全マージンを引く。
            // ジョブ中はオーバーレイが本体レイアウトの後ろに積まれるので、その高さも先に引く
            // （引かないとキャンセルボタンが下端で切れ、ジョブ中の唯一の脱出口が押せなくなる）。
            float availableContentH = position.height - IrocaConsts.Layout.WindowChromeMargin
                                      - (blocking ? JobOverlayReserve() : 0f);
            float exportH = _exportView.GetSectionHeight();

            if (sideBySide)
                DrawSideBySideLayout(availableContentH, exportH);
            else
                DrawVerticalLayout(availableContentH, exportH);

            EditorGUI.EndDisabledGroup();

            // DisabledScope の外でジョブオーバーレイ（進捗バー＋キャンセル）を描画。
            // ウィンドウ全体が無効化されていてもキャンセルだけは押せる。
            if (blocking)
            {
                var overlayRect = EditorGUILayout.BeginVertical();
                DrawJobOverlay();
                EditorGUILayout.EndVertical();

                // 実測値を次フレームの予約高に使う。値が変わったら追い再描画を 1 回要求する
                // （_sideBySideTopHeight と同方針。予約高はオーバーレイ自身の高さに影響しないので収束する）。
                if (Event.current.type == EventType.Repaint && overlayRect.height > 1f
                    && Mathf.Abs(overlayRect.height - _jobOverlayHeight) > 0.5f)
                {
                    _jobOverlayHeight = overlayRect.height;
                    Repaint();
                }
                // 進捗バーを次フレームで更新するため、ジョブ中は継続的に再描画を要求する。
                Repaint();
            }
            else
            {
                _jobOverlayHeight = 0f;
            }
        }

        /// <summary>
        /// ジョブオーバーレイのために本体レイアウトから引いておく高さ。
        /// 実測値があればそれを、未計測の初回フレームだけ決定論フォールバックを返す。
        /// </summary>
        private float JobOverlayReserve()
        {
            if (_jobOverlayHeight > 1f) return _jobOverlayHeight;
            // 初回フレーム用の見積もり: Space(2) + 進捗バー 18 + キャンセル 22 + Space(2) + 行間。
            return 2f + 18f + 22f + 2f + EditorGUIUtility.standardVerticalSpacing * 3f;
        }

        // 設定列のプレフィックスラベル幅。既定(150)のままだと狭いカラムでは
        // 「ラベル150＋スライダー最小幅＋数値フィールド」等の行最小幅がカラム幅を超え、
        // 横スクロールバーを無効化している設定列では超過分が右端で切れて
        // プレビューの下に隠れて見える。カラム幅に比例させ、行がカラム内に収まるようにする。
        // 下限 95 はラベルが読める最低限、上限 150 は Unity 既定（広いカラムでは従来どおり）。
        private static float SettingsLabelWidth(float contentWidth)
            => Mathf.Clamp(contentWidth * 0.45f, 95f, 150f);

        // 左カラム（横並び）/ 上部スクロール（縦並び）共通の設定スタック。
        // 横並び・縦並び双方から呼ぶことで描画の重複を避ける。
        // 呼び出し側の BeginChangeCheck/EndChangeCheck に挟まれて previewDirty 判定に使われる。
        // マスク UI(_maskView.Draw)はここに含めない: ブラシサイズ・ペイントモード切替・foldout 開閉など
        // マスク内容と無関係な操作でも previewDirty が立ち 4K フル再生成が走ってしまうため。マスク内容の
        // 変更はストローク時に maskDirty 経由で別途プレビュー再生成をトリガするので、Draw は呼び出し側が
        // ChangeCheck の外で行う。
        private void DrawLeftColumnSettings()
        {
            DrawZoneList();
            DrawProcessingSection();
        }

        private void DrawSideBySideLayout(float availableContentH, float exportH)
        {
            EditorGUI.BeginChangeCheck();
            DrawTextureField();

            // エクスポートセクションを常にウィンドウ下部に表示するため、
            // 横並び領域の高さを「描画領域高 - ヘッダー/テクスチャフィールド - エクスポート高」に制限する。
            //
            // 上部（toolbar＋テクスチャフィールド）の高さは固定では見積もれない。テクスチャ未設定時の
            // WorkflowHint や ReadWrite 不可時のエラー HelpBox が可変高で挿入され、固定見積もりだと
            // horizH が過大になりエクスポートが画面外へはみ出すため。DrawTextureField 直後の
            // GetLastRect().yMax はウィンドウ最上部(y=0)からの絶対値＝上部全体高そのものなので、
            // それを Repaint 時に実測してキャッシュし、horizH 算出に使う。
            if (Event.current.type == EventType.Repaint)
            {
                float measured = GUILayoutUtility.GetLastRect().yMax;
                // 値が変わったら追い再描画を 1 回要求する。エディタウィンドウは要求が無い限り
                // 再描画されないため、これが無いと HelpBox の出入り等で上部高が変わった操作の
                // 最終フレームが旧値の horizH のまま画面に固定される(PreviewView の chrome
                // 実測と同方針)。上部高は horizH に依存しないため 1 回で収束しループしない。
                if (measured > 1f && Mathf.Abs(measured - _sideBySideTopHeight) > 0.5f)
                {
                    _sideBySideTopHeight = measured;
                    Repaint();
                }
            }
            // 未計測の初回フレームのみ決定論フォールバック（テクスチャ設定済み相当の見積もり）。
            float fallbackTopH = EditorStyles.toolbar.fixedHeight
                + 4f + (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) * 2 + 4f;
            float topOverheadH = _sideBySideTopHeight > 1f ? _sideBySideTopHeight : fallbackTopH;
            float horizH = Mathf.Max(
                IrocaConsts.Layout.MiddleAreaMinHeight,
                availableContentH - topOverheadH - exportH);

            EditorGUILayout.BeginHorizontal(GUILayout.Height(horizH));

            float leftWidth = Mathf.Clamp(
                position.width * IrocaConsts.Layout.LeftColumnRatio,
                IrocaConsts.Layout.LeftColumnMin,
                IrocaConsts.Layout.LeftColumnMax);
            // 等倍(100%)プレビューがバー無しで収まる幅を右カラムへ優先確保する。
            // 比率どおりだと既定ウィンドウ幅(800)で右カラムが 512px 画像に ~32px 届かず
            // 横スクロールバーが常時出るため、左カラムが下限(LeftColumnMin)までの範囲で譲る。
            // 下限は既に狭いウィンドウで常用される幅なので設定 UI は崩れない。
            leftWidth = Mathf.Max(IrocaConsts.Layout.LeftColumnMin,
                Mathf.Min(leftWidth, position.width - IrocaConsts.Layout.PreviewColumnReserve));
            EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
            // 縦バーを常時確保し、横バーは無効化する。簡易オーバーロードは縦バーが内容高で
            // 出入り(トグル)し、その都度コンテンツ幅が ~13px 変わって設定UIが左右にガクつく
            // (プレビュー生成で上部高/列高がわずかに揺れると境界付近でトグルしやすい)。
            // 常時確保すれば内容の有無に関わらず横位置が一定になる。
            leftScrollPos = EditorGUILayout.BeginScrollView(leftScrollPos,
                false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.ExpandHeight(true));

            // 設定列の実内容幅（カラム幅 − 常時表示の縦スクロールバー）に合わせて
            // ラベル幅を縮め、行の右端（数値フィールド・ボタン）が切れないようにする。
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = SettingsLabelWidth(
                leftWidth - GUI.skin.verticalScrollbar.fixedWidth);

            DrawLeftColumnSettings();

            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewDirty();
            }

            // マスク UI は ChangeCheck の外・スクロール領域内で描画する(DrawLeftColumnSettings のコメント参照)。
            _maskView.Draw();

            _presetsView.Draw();

            // パイプライン透明化（Debug View）の描画フック。
            // Code/Debug/ asmdef がない or 未登録なら subscriber がいないので何も描画されない。
            // 左カラムのスクロール領域内に置くことで、エクスポートのピン留めと
            // 競合せず、スクロールで到達できるようにする。
            DebugCaptureHooks.RaiseDrawFoldout(this);

            EditorGUIUtility.labelWidth = prevLabelWidth;
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // ExpandHeight な ScrollView で囲うことで、プレビューが
            // 横並びセクション高（horizH）を超えても列内でスクロールするようになり、
            // 下部のエクスポートセクションを押し出さない。
            float rightWidth = Mathf.Max(0f, position.width - leftWidth);
            EditorGUILayout.BeginVertical();
            // 横バーは描かない(GUIStyle.none)。この外側 ScrollView は縦オーバーフロー専用で、
            // 横スクロールは内側プレビューに任せる。ただし GUIStyle.none は「描かない・幅0」で
            // あってレイアウト上の横スクロールを禁止はしないので、カラム幅より最小幅の大きい子
            // (狭幅時の操作行など)があると、この ScrollView はカラムより広いクライアント幅を
            // 子へ配る。プレビュー枠が一緒に広がらないよう、枠幅は availableColumnWidth 経由で
            // 明示的に固定する(PreviewView 側 frameW のコメント参照)。
            rightScrollPos = EditorGUILayout.BeginScrollView(rightScrollPos,
                false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.ExpandHeight(true));
            // 別ウィンドウへ切り出している間は本体では描かない(同一 PreviewView の二重
            // レイアウトを避ける。理由は IrocaPreviewWindow のクラスコメント)。
            if (IrocaPreviewWindow.IsOpen)
            {
                DrawPreviewDetachedSection(canReattach: true);
            }
            else
            {
                // プレビュー枠が右カラム高に収まるよう動的に縮むためのカラム高と、
                // 枠幅を固定するためのカラム幅を渡す。
                _previewView.availableColumnHeight = horizH;
                _previewView.availableColumnWidth = rightWidth;
                _previewView.Draw();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // 一括適用は実装継続中のため当面 UI から非表示。
            // _exportView.DrawBatchSection();
            _exportView.DrawExportSection();
        }

        private void DrawVerticalLayout(float availableContentH, float exportH)
        {
            // エクスポートを常にウィンドウ下部に表示するため、上部だけをスクロール領域にする。
            float toolbarH = EditorStyles.toolbar.fixedHeight + 4f;
            float topScrollH = Mathf.Max(
                IrocaConsts.Layout.MiddleAreaMinHeight,
                availableContentH - toolbarH - exportH);

            // 横バーは無効化(GUIStyle.none)。この外側 ScrollView は縦スクロール専用で、
            // 横スクロールは内側プレビューに任せる（横並びレイアウトと同じ理由）。
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos,
                false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.Height(topScrollH));

            // 縦並び＝狭いウィンドウなので、設定行が右端で切れないよう
            // ラベル幅を内容幅（ウィンドウ幅 − 縦スクロールバー）に追従させる。
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = SettingsLabelWidth(
                position.width - GUI.skin.verticalScrollbar.fixedWidth);

            EditorGUI.BeginChangeCheck();

            DrawTextureField();
            DrawLeftColumnSettings();

            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewDirty();
            }

            // マスク UI は ChangeCheck の外・スクロール領域内で描画する(DrawLeftColumnSettings のコメント参照)。
            _maskView.Draw();

            _presetsView.Draw();

            // 縦並び(＝狭幅)ではプレビューを本体に描かない。設定列の下へ積まれると、
            // プレビューに割ける高さも幅も足りず検分に使えないため、別ウィンドウへ
            // 切り出す導線だけを置く。ウィンドウを広げれば従来どおり本体内に戻る。
            DrawPreviewDetachedSection(canReattach: false);

            // パイプライン透明化（Debug View）の描画フック。
            // Code/Debug/ asmdef がない or 未登録なら subscriber がいないので何も描画されない。
            // メインスクロール領域内に置くことで、エクスポートのピン留めと
            // 競合せず、スクロールで到達できるようにする。
            DebugCaptureHooks.RaiseDrawFoldout(this);

            EditorGUIUtility.labelWidth = prevLabelWidth;
            EditorGUILayout.EndScrollView();

            // 一括適用は実装継続中のため当面 UI から非表示。
            // _exportView.DrawBatchSection();
            _exportView.DrawExportSection();
        }

        // 縦並び(狭幅)では常にここを描き、横並びでは別ウィンドウが開いている間だけ描く。
        // canReattach=false(縦並び)では「本体に戻す」を出さない。戻しても設定列の下に
        // 押し出されて実用にならず、押した直後にまたこの案内へ戻るだけになるため。
        private void DrawPreviewDetachedSection(bool canReattach)
        {
            EditorGUILayout.LabelField(
                Localization.StepPrefixPreview + Localization.Preview, EditorStyles.boldLabel);

            bool open = IrocaPreviewWindow.IsOpen;
            EditorGUILayout.HelpBox(
                open ? Localization.PreviewDetachedActive : Localization.PreviewDetachedNarrowHint,
                MessageType.Info);

            if (GUILayout.Button(new GUIContent(
                    open ? Localization.FocusPreviewWindow : Localization.OpenPreviewWindow,
                    Localization.OpenPreviewWindowTooltip)))
            {
                if (open) IrocaPreviewWindow.FocusIfOpen();
                else IrocaPreviewWindow.Open(this);
            }

            if (open && canReattach &&
                GUILayout.Button(new GUIContent(
                    Localization.ReattachPreview, Localization.ReattachPreviewTooltip)))
            {
                IrocaPreviewWindow.CloseIfOpen();
            }

            EditorGUILayout.Space(4);
        }

        private void DrawJobOverlay()
        {
            _exportView?.DrawJobOverlay();

            if (_autoTuneJob.IsRunning)
            {
                EditorGUILayout.Space(2);
                var rect = EditorGUILayout.GetControlRect(false, 18f);
                float pct = _autoTuneProgress.Value;
                EditorGUI.ProgressBar(rect, pct, $"{Localization.AutoTune}  {Mathf.RoundToInt(pct * 100f)}%");
                if (GUILayout.Button(new GUIContent(Localization.Cancel, Localization.CancelActionTooltip), GUILayout.Height(22)))
                {
                    _autoTuneJob.Cancel();
                }
                EditorGUILayout.Space(2);
            }
        }

        /// <param name="jobBlocking">
        /// ジョブ実行中か。ヘッダー自体は DisabledScope の外に置く（言語切替・クレジットは
        /// ジョブ中でも安全で、むしろ待ち時間に触れて困らない）が、セッション状態を破壊的に
        /// 書き換えるリセットだけはここで無効化する。
        /// </param>
        private void DrawHeader(bool jobBlocking)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var labels = new[]
            {
                new GUIContent(Localization.LangAuto, Localization.LanguageToolbarTooltip),
                new GUIContent(Localization.LangJapanese, Localization.LanguageToolbarTooltip),
                new GUIContent(Localization.LangEnglish, Localization.LanguageToolbarTooltip),
            };
            int current = (int)Localization.CurrentLanguage;
            int next = GUILayout.Toolbar(current, labels, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            if (next != current)
            {
                Localization.CurrentLanguage = (LanguageMode)next;
                Localization.SaveLanguagePreference();
                Repaint();
            }

            GUILayout.FlexibleSpace();

            // 現在のテクスチャの編集内容（ゾーン・色・処理設定・マスク）を初期状態へ戻す逃げ道。
            // 確認ダイアログを挟み、Undo 登録するので誤操作しても「元に戻す」で復元できる。
            // ジョブ実行中は無効化する: エクスポート/自動調整の完了時 apply がリセット後の
            // セッションへ書き戻し、状態不整合を生み得るため。
            using (new EditorGUI.DisabledScope(sourceTexture == null || jobBlocking))
            {
                if (GUILayout.Button(new GUIContent(Localization.ResetSession, Localization.ResetSessionTooltip),
                        EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog(
                            Localization.Confirm, Localization.ResetSessionConfirm, Localization.OK, Localization.Cancel))
                        ResetCurrentSession();
                }
            }

            if (GUILayout.Button(new GUIContent(Localization.Credit, Localization.CreditTooltip), EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
            {
                EditorUtility.DisplayDialog(Localization.CreditTitle, Localization.CreditBody, Localization.OK);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawTextureField()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localization.StepPrefixTexture + Localization.SourceTexture, EditorStyles.boldLabel);

            // 開始点が分かりにくいので、テクスチャ未設定時だけ一連の流れを案内する。
            if (sourceTexture == null)
                EditorGUILayout.HelpBox(Localization.WorkflowHint, MessageType.Info);

            var newTex = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent(Localization.Texture, Localization.TextureTooltip),
                sourceTexture, typeof(Texture2D), false);
            if (newTex != sourceTexture)
            {
                // マスク保存失敗時はユーザーに通知（黙って消えないように）。
                if (!SavePersistedSessionForCurrentTexture())
                    ShowNotification(new GUIContent($"{Localization.Error}: {Localization.MaskSaveFailed}"));
                // _session をまるごと差し替えるため、深い Undo (RegisterCompleteObjectUndo) を使う。
                Undo.RegisterCompleteObjectUndo(this, "Change Source Texture");
                sourceTexture = newTex;
                _previewView.InvalidateSourceCache();
                _maskView.ClearBuffersOnTextureChange();
                if (sourceTexture != null)
                {
                    var path = AssetDatabase.GetAssetPath(sourceTexture);
                    _exportView.SetSourceTextureBaseName(Path.GetFileNameWithoutExtension(path));
                }
                LoadPersistedSessionForCurrentTexture();
                RememberLastEditedTexture();
            }

            if (sourceTexture != null && !IsReadable(sourceTexture))
            {
                EditorGUILayout.HelpBox(Localization.ReadWriteError, MessageType.Error);

                if (GUILayout.Button(new GUIContent(Localization.EnableReadWrite, Localization.EnableReadWriteTooltip)))
                {
                    EnableReadWrite(sourceTexture);
                }
            }

            EditorGUILayout.Space(4);
        }

        private void DrawProcessingSection()
        {
            processingFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(processingFoldout, Localization.Processing);
            if (!processingFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            edgeFeather = UndoHelper.Slider(this,
                new GUIContent(Localization.EdgeFeather, Localization.EdgeFeatherTooltip),
                edgeFeather, 0f, 5f);

            antiAliasCleanup = UndoHelper.IntSlider(this,
                new GUIContent(Localization.AntiAliasCleanup, Localization.AntiAliasCleanupTooltip),
                antiAliasCleanup, 0, 5);

            useDecontamination = UndoHelper.Toggle(this,
                new GUIContent(Localization.UseDecontamination, Localization.UseDecontaminationTooltip),
                useDecontamination);

            // 編集モードの切替はゾーンリスト上部の「かんたん / 通常 / 上級」トグルに一本化した
            // （DrawModeToggle）。穴埋め・境界復元・α分解半径は上級モード時のみ表示する。
            if (editMode == EditMode.Advanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    holeFillPasses = UndoHelper.IntSlider(this,
                        new GUIContent(Localization.HoleFillPasses, Localization.HoleFillPassesTooltip),
                        holeFillPasses, 0, 10);
                    holeFillMinNeighbors = UndoHelper.IntSlider(this,
                        new GUIContent(Localization.HoleFillMinNeighbors, Localization.HoleFillMinNeighborsTooltip),
                        holeFillMinNeighbors, 1, 8);
                    relaxedSatMin = UndoHelper.Slider(this,
                        new GUIContent(Localization.RelaxedSatMin, Localization.RelaxedSatMinTooltip),
                        relaxedSatMin, 0f, 0.2f);
                    relaxedSatRamp = UndoHelper.Slider(this,
                        new GUIContent(Localization.RelaxedSatRamp, Localization.RelaxedSatRampTooltip),
                        relaxedSatRamp, 0.01f, 0.3f);
                    using (new EditorGUI.DisabledScope(!useDecontamination))
                    {
                        decontaminationRadius = UndoHelper.IntSlider(this,
                            new GUIContent(Localization.DecontaminationRadius, Localization.DecontaminationRadiusTooltip),
                            decontaminationRadius, 1, 12);
                    }
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }
    }
}
