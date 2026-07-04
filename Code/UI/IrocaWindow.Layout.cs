// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // ウィンドウ全体のレイアウト・描画（OnGUI とその直下のセクション描画）。
    // 横並び/縦並びの分岐、ヘッダー、テクスチャフィールド、処理設定、ジョブオーバーレイ。
    public partial class IrocaWindow
    {
        // Foldout
        [SerializeField] private bool processingFoldout = true;

        private Vector2 scrollPos;
        // 横並びレイアウトの左右カラム用スクロール
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

            DrawHeader();

            // ジョブ実行中はウィンドウ内 UI を全て無効化する。
            // ただしジョブのオーバーレイ（進捗バー＋キャンセル）は DisabledScope の外で
            // 描画し、キャンセルだけは押せるようにする。
            // 自動調整は「手動実行（ボタン）」のときだけウィンドウ全体をブロックする。
            // かんたんモードの自動実行は裏で走らせ、操作を妨げない。
            bool blocking = (_exportView != null && _exportView.IsExporting)
                || (_autoTuneJob.IsRunning && _autoTuneIsManual);
            EditorGUI.BeginDisabledGroup(blocking);

            bool sideBySide = position.width >= IrocaConsts.Layout.SideBySideMinWidth;

            // position.height はウィンドウ枠（タイトル/タブバー）を含むため、
            // 実描画領域はそれより低い。エクスポートが画面外に押し出されないよう安全マージンを引く。
            float availableContentH = position.height - IrocaConsts.Layout.WindowChromeMargin;
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
                DrawJobOverlay();
                // 進捗バーを次フレームで更新するため、ジョブ中は継続的に再描画を要求する。
                Repaint();
            }
        }

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

        // ── 横並びレイアウト: 上部テクスチャ（フル幅）＋左（設定）／右（プレビュー）＋下部エクスポート ──
        private void DrawSideBySideLayout(float availableContentH, float exportH)
        {
            // ── 上部: テクスチャフィールド（フル幅） ──
            EditorGUI.BeginChangeCheck();
            DrawTextureField();

            // ── 横並び: 左（設定）＋ 右（プレビュー） ──
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
                if (measured > 1f) _sideBySideTopHeight = measured;
            }
            // 未計測の初回フレームのみ決定論フォールバック（テクスチャ設定済み相当の見積もり）。
            float fallbackTopH = EditorStyles.toolbar.fixedHeight
                + 4f + (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) * 2 + 4f;
            float topOverheadH = _sideBySideTopHeight > 1f ? _sideBySideTopHeight : fallbackTopH;
            float horizH = Mathf.Max(
                IrocaConsts.Layout.MiddleAreaMinHeight,
                availableContentH - topOverheadH - exportH);

            EditorGUILayout.BeginHorizontal(GUILayout.Height(horizH));

            // 左カラム: ゾーン設定 + 処理設定 + マスク + プリセット
            float leftWidth = Mathf.Clamp(
                position.width * IrocaConsts.Layout.LeftColumnRatio,
                IrocaConsts.Layout.LeftColumnMin,
                IrocaConsts.Layout.LeftColumnMax);
            EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
            // 縦バーを常時確保し、横バーは無効化する。簡易オーバーロードは縦バーが内容高で
            // 出入り(トグル)し、その都度コンテンツ幅が ~13px 変わって設定UIが左右にガクつく
            // (プレビュー生成で上部高/列高がわずかに揺れると境界付近でトグルしやすい)。
            // 常時確保すれば内容の有無に関わらず横位置が一定になる。
            leftScrollPos = EditorGUILayout.BeginScrollView(leftScrollPos,
                false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.ExpandHeight(true));

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

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // 右カラム: プレビュー
            // ExpandHeight な ScrollView で囲うことで、プレビューが
            // 横並びセクション高（horizH）を超えても列内でスクロールするようになり、
            // 下部のエクスポートセクションを押し出さない。
            EditorGUILayout.BeginVertical();
            // 横バーは無効化(GUIStyle.none)。この外側 ScrollView は縦オーバーフロー専用で、
            // 横スクロールは内側プレビューに任せる。横を許すと子へ無制限の幅を提供してしまい、
            // 内側プレビュー枠が確定せずはみ出し、外側の横バーがプレビューの横パンを横取りする。
            rightScrollPos = EditorGUILayout.BeginScrollView(rightScrollPos,
                false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.ExpandHeight(true));
            // プレビュー枠が右カラム高に収まるよう動的に縮むためのカラム高を渡す。
            _previewView.availableColumnHeight = horizH;
            _previewView.Draw();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // ── 下部: エクスポート（フル幅・常に表示） ──
            // 一括適用は実装継続中のため当面 UI から非表示。
            // _exportView.DrawBatchSection();
            _exportView.DrawExportSection();
        }

        // ── 縦並びレイアウト（ウィンドウ幅が狭い場合）: 上部スクロール＋下部エクスポート ──
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
            // 縦並びでは上部スクロール領域全体がプレビューのカラムに相当する。
            // プレビュー枠より上の実測高は PreviewView 側が差し引く。
            _previewView.availableColumnHeight = topScrollH;
            _previewView.Draw();

            // パイプライン透明化（Debug View）の描画フック。
            // Code/Debug/ asmdef がない or 未登録なら subscriber がいないので何も描画されない。
            // メインスクロール領域内に置くことで、エクスポートのピン留めと
            // 競合せず、スクロールで到達できるようにする。
            DebugCaptureHooks.RaiseDrawFoldout(this);

            EditorGUILayout.EndScrollView();

            // ── 下部: エクスポート（フル幅・常に表示） ──
            // 一括適用は実装継続中のため当面 UI から非表示。
            // _exportView.DrawBatchSection();
            _exportView.DrawExportSection();
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

        // ───────────────────────── ヘッダー ───────────────────────────

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Language selector
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
            using (new EditorGUI.DisabledScope(sourceTexture == null))
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

        // ───────────────────────── テクスチャフィールド ───────────────────────────

        private void DrawTextureField()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localization.StepPrefixTexture + Localization.SourceTexture, EditorStyles.boldLabel);

            // 開始点が分かりにくいので、テクスチャ未設定時だけ一連の流れを案内する。
            if (sourceTexture == null)
                EditorGUILayout.HelpBox(Localization.WorkflowHint, MessageType.Info);

            var newTex = (Texture2D)EditorGUILayout.ObjectField(
                Localization.Texture, sourceTexture, typeof(Texture2D), false);
            if (newTex != sourceTexture)
            {
                // 旧テクスチャのマスク＋セッション（ゾーン/色/処理設定）を永続化。
                // マスク保存失敗時はユーザーに通知（黙って消えないように）。
                if (!SavePersistedSessionForCurrentTexture())
                    ShowNotification(new GUIContent($"{Localization.Error}: {Localization.MaskSaveFailed}"));
                // _session をまるごと差し替えるため、深い Undo (RegisterCompleteObjectUndo) を使う。
                Undo.RegisterCompleteObjectUndo(this, "Change Source Texture");
                sourceTexture = newTex;
                // テクスチャが変わったのでソースピクセルキャッシュを無効化
                _previewView.InvalidateSourceCache();
                _maskView.ClearBuffersOnTextureChange();
                if (sourceTexture != null)
                {
                    var path = AssetDatabase.GetAssetPath(sourceTexture);
                    _exportView.SetSourceTextureBaseName(Path.GetFileNameWithoutExtension(path));
                }
                // 新テクスチャのセッション（ゾーン/色/処理設定）＋マスクを復元（保存が無ければ既定へ）。
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

        // ───────────────────────── 処理設定 ───────────────────────────

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
