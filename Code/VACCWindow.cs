// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow : EditorWindow
    {
        // ── ユーザー入力・設定 ──
        // [SerializeField] を付けることで、スクリプト再コンパイル時に Unity が
        // EditorWindow の状態をシリアライズ/復元し、入力内容が失われにくくなる。
        [SerializeField] private Texture2D sourceTexture;
        internal Texture2D SourceTexture { get => sourceTexture; set => sourceTexture = value; }
        internal VACCSessionState Session => _session;

        // パイプライン透明化機能（Code.Debug/ asmdef がある場合のみ実体が入る）。
        // PreviewView が ProcessPixelsArray に渡したインスタンスをここに保管し、
        // DebugView が後から読み出す。Debug 機能未導入なら常に null。
        internal IDebugCapture LatestDebugCapture { get; set; }

        // ── 連続領域モードの keep キャッシュ(メインプレビューがフル画像で解いた結果を
        //    詳細プレビューへ転写して完全一致させる)。世代スタンプで陳腐キャッシュの誤適用を防ぐ。
        //    公開後は不変として扱い、メインプレビュージョブは未公開の新規インスタンスにだけ書く。
        [System.NonSerialized] internal FloodFillKeepCache floodFillKeepCache;
        [System.NonSerialized] internal int floodFillKeepGen;

        // ── 各 View からの再描画通知用 ──
        internal void MarkPreviewDirty() { if (_previewView != null) _previewView.previewDirty = true; }
        internal void MarkMaskDirty() { if (_maskView != null) _maskView.maskDirty = true; }
        internal void RequestRepaint() { Repaint(); }
        private Vector2 scrollPos;

        // ── View インスタンス（状態は各 View が自前で保持） ──
        [SerializeField] private ExportView _exportView = new ExportView();
        [SerializeField] private PresetsView _presetsView = new PresetsView();
        [SerializeField] internal MaskPaintView _maskView = new MaskPaintView();
        [SerializeField] private PreviewView _previewView = new PreviewView();

        // 編集状態（ゾーン定義・処理パラメータ・マスク状態）。
        // Phase 4a で個別 [SerializeField] フィールド群から VACCSessionState に集約。
        [SerializeField] private VACCSessionState _session = VACCSessionState.CreateDefault();

        // SerializedObject(this) 経由のプロパティ編集基盤。
        // Phase 4b で ColorZoneDrawer / PropertyField への移行時に使用し、
        // Phase 4c で ApplyModifiedProperties() の戻り値を previewDirty 判定に一元化する。
        private SerializedObject _windowSerializedObject;
        private SerializedProperty _sessionProperty;
        private SerializedProperty _zonesProperty;

        // ── _session への薄いアクセサ ──
        // Phase 4a では partial class 内のコードに最小限の変更で済むよう、
        // 既存フィールド名と同じプロパティ経由で _session 内のフィールドへアクセスする。
        // Phase 4b で View 分離する際、これらのプロパティは _session.xxx の直接参照へ置換される。
        private List<ColorZone> zones { get => _session.zones; set => _session.zones = value; }
        private float edgeFeather { get => _session.edgeFeather; set => _session.edgeFeather = value; }
        private int antiAliasCleanup { get => _session.antiAliasCleanup; set => _session.antiAliasCleanup = value; }
        private bool useDecontamination { get => _session.useDecontamination; set => _session.useDecontamination = value; }
        private int decontaminationRadius { get => _session.decontaminationRadius; set => _session.decontaminationRadius = value; }
        private EditMode editMode { get => _session.editMode; set => _session.editMode = value; }
        private int holeFillPasses { get => _session.holeFillPasses; set => _session.holeFillPasses = value; }
        private int holeFillMinNeighbors { get => _session.holeFillMinNeighbors; set => _session.holeFillMinNeighbors = value; }
        private float relaxedSatMin { get => _session.relaxedSatMin; set => _session.relaxedSatMin = value; }
        private float relaxedSatRamp { get => _session.relaxedSatRamp; set => _session.relaxedSatRamp = value; }

        // Foldouts
        [SerializeField] private bool zonesFoldout = true;
        [SerializeField] private bool processingFoldout = true;

        // 横並びレイアウトの左右カラム用スクロール
        private Vector2 leftScrollPos;
        // プレビュー側の縦オーバーフロー用。テクスチャ画像が大きいと PreviewView の
        // 内部 ScrollView 高さ（最大 ~528）＋ラベル類が横並びセクション高を超え、
        // エクスポートセクションを画面外へ押し出してしまうため、外側にも ScrollView を挟む。
        private Vector2 rightScrollPos;

        // 横並びモードのテクスチャフィールド込み上部（toolbar＋テクスチャフィールド）の実高を
        // Repaint 時に実測しキャッシュする。WorkflowHint/ReadWriteError の HelpBox は可変高で
        // 固定見積もりに入らないため、前フレーム Repaint の実測値を今フレームの horizH 算出に使う
        // （Layout/Repaint で同一値→GUILayout 整合）。0（未計測）時は決定論フォールバックを使う。
        // PreviewView._viewportWidth と同方針。
        [System.NonSerialized] private float _sideBySideTopHeight;

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

        // 自動調整の非同期ジョブ。メインスレッドで pixels を取得し、
        // バックグラウンドで ZoneAutoTuner.Analyze を走らせる。
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

        [MenuItem(VACCConsts.MenuPath, priority = 100)]
        public static void ShowWindow()
        {
            var window = GetWindow<VACCWindow>(Localization.WindowTitle);
            window.titleContent = new GUIContent(
                Localization.WindowTitle,
                EditorGUIUtility.IconContent("d_Image Icon").image);
            window.minSize = new Vector2(340, 500);
            if (window.position.width < 800 || window.position.height < 800)
                window.position = new Rect(window.position.x, window.position.y, 800, 800);
        }

        private void OnEnable()
        {
            _session ??= VACCSessionState.CreateDefault();
            _exportView ??= new ExportView();
            _exportView.Initialize(this);
            _presetsView ??= new PresetsView();
            _presetsView.Initialize(this);
            _maskView ??= new MaskPaintView();
            _maskView.Initialize(this);
            _previewView ??= new PreviewView();
            _previewView.Initialize(this);
            _windowSerializedObject = new SerializedObject(this);
            _sessionProperty = _windowSerializedObject.FindProperty(nameof(_session));
            _zonesProperty = _sessionProperty?.FindPropertyRelative(nameof(VACCSessionState.zones));
            EnsureAllZoneIds();
            // AssetWatcher の delete フックを取りこぼした場合の保険として、
            // ウィンドウを開いた時に MaskCache の orphan ファイルを掃除する。
            MaskFileStore.CleanupOrphans();
            _maskView.RestoreFromSession();
            // Unity 標準 Undo の戻り/進みに合わせて bool[] バッファを _session.maskState から再展開。
            // 二重購読を避けるため一度外してから登録する（PreviewJobMainThread.Install と同じ防御）。
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            _previewView?.Suspend();
            _maskView?.SuspendTransientState();
            _maskView.SaveToSession();
            _sessionProperty = null;
            _zonesProperty = null;
            _windowSerializedObject?.Dispose();
            _windowSerializedObject = null;
        }

        private void OnUndoRedoPerformed()
        {
            // _session.maskState の RLE 文字列が Undo で書き戻されたので、
            // bool[] バッファを再展開してオーバーレイ・プレビューを更新させる。
            if (_maskView != null)
            {
                _maskView.SyncBuffersFromState();
                _maskView.maskDirty = true;
            }
            MarkPreviewDirty();
            Repaint();
        }

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

        private void OnGUI()
        {
            // Ctrl+Z / Ctrl+Y は Unity 標準 Undo に統合済みのため、独自処理は不要。
            ProcessPendingZoneChanges();
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

            bool sideBySide = position.width >= VACCConsts.Layout.SideBySideMinWidth;

            // position.height はウィンドウ枠（タイトル/タブバー）を含むため、
            // 実描画領域はそれより低い。エクスポートが画面外に押し出されないよう安全マージンを引く。
            float availableContentH = position.height - VACCConsts.Layout.WindowChromeMargin;
            float exportH = _exportView.GetSectionHeight();

            if (sideBySide)
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
                    VACCConsts.Layout.MiddleAreaMinHeight,
                    availableContentH - topOverheadH - exportH);

                EditorGUILayout.BeginHorizontal(GUILayout.Height(horizH));

                // 左カラム: ゾーン設定 + 処理設定 + マスク + プリセット
                float leftWidth = Mathf.Clamp(
                    position.width * VACCConsts.Layout.LeftColumnRatio,
                    VACCConsts.Layout.LeftColumnMin,
                    VACCConsts.Layout.LeftColumnMax);
                EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
                leftScrollPos = EditorGUILayout.BeginScrollView(leftScrollPos, GUILayout.ExpandHeight(true));

                DrawZoneList();
                DrawProcessingSection();
                _maskView.Draw();

                if (EditorGUI.EndChangeCheck())
                {
                    MarkPreviewDirty();
                }

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
                _previewView.Draw();
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();

                EditorGUILayout.EndHorizontal();

                // ── 下部: エクスポート（フル幅・常に表示） ──
                // 一括適用は実装継続中のため当面 UI から非表示。
                // _exportView.DrawBatchSection();
                _exportView.DrawExportSection();
            }
            else
            {
                // ── 縦並びレイアウト（ウィンドウ幅が狭い場合） ──
                // エクスポートを常にウィンドウ下部に表示するため、上部だけをスクロール領域にする。
                float toolbarH = EditorStyles.toolbar.fixedHeight + 4f;
                float topScrollH = Mathf.Max(
                    VACCConsts.Layout.MiddleAreaMinHeight,
                    availableContentH - toolbarH - exportH);

                // 横バーは無効化(GUIStyle.none)。この外側 ScrollView は縦スクロール専用で、
                // 横スクロールは内側プレビューに任せる（横並びレイアウトと同じ理由）。
                scrollPos = EditorGUILayout.BeginScrollView(scrollPos,
                    false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                    GUILayout.Height(topScrollH));

                EditorGUI.BeginChangeCheck();

                DrawTextureField();
                DrawZoneList();
                DrawProcessingSection();
                _maskView.Draw();

                if (EditorGUI.EndChangeCheck())
                {
                    MarkPreviewDirty();
                }

                _presetsView.Draw();
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

        private void DrawJobOverlay()
        {
            _exportView?.DrawJobOverlay();

            if (_autoTuneJob.IsRunning)
            {
                EditorGUILayout.Space(2);
                var rect = EditorGUILayout.GetControlRect(false, 18f);
                float pct = _autoTuneProgress.Value;
                EditorGUI.ProgressBar(rect, pct, $"{Localization.AutoTune}  {Mathf.RoundToInt(pct * 100f)}%");
                if (GUILayout.Button(Localization.Cancel, GUILayout.Height(22)))
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
            var labels = new[] { Localization.LangAuto, Localization.LangJapanese, Localization.LangEnglish };
            int current = (int)Localization.CurrentLanguage;
            int next = GUILayout.Toolbar(current, labels, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            if (next != current)
            {
                Localization.CurrentLanguage = (LanguageMode)next;
                Localization.SaveLanguagePreference();
                Repaint();
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(Localization.Credit, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
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
                // 旧テクスチャのマスクを永続化。失敗時はユーザーに通知（黙って消えないように）。
                if (!_maskView.SaveToSession())
                    ShowNotification(new GUIContent($"{Localization.Error}: {Localization.MaskSaveFailed}"));
                Undo.RecordObject(this, "Change Source Texture");
                sourceTexture = newTex;
                MarkPreviewDirty();
                // テクスチャが変わったのでソースピクセルキャッシュを無効化
                _previewView.InvalidateSourceCache();
                _maskView.ClearBuffersOnTextureChange();
                if (sourceTexture != null)
                {
                    var path = AssetDatabase.GetAssetPath(sourceTexture);
                    _exportView.SetSourceTextureBaseName(Path.GetFileNameWithoutExtension(path));
                }
                _maskView.RestoreFromSession();              // load mask for new texture
            }

            if (sourceTexture != null && !IsReadable(sourceTexture))
            {
                EditorGUILayout.HelpBox(Localization.ReadWriteError, MessageType.Error);

                if (GUILayout.Button(Localization.EnableReadWrite))
                {
                    EnableReadWrite(sourceTexture);
                }
            }

            EditorGUILayout.Space(4);
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
            s_autoTuneEnabledContent, s_autoTuneDisabledContent;

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
                new[] { /* Localization.SimpleMode, */ Localization.NormalMode, Localization.AdvancedShort });
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
            var dragHandleStyle = s_dragHandleStyle;
            // 各ゾーンの矩形を記録し、ドロップ位置の判定とインジケータ描画に使う。
            var zoneRects = new List<Rect>(zones.Count);

            int removeIndex = -1;
            for (int i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                zone.EnsureId();
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Header row
                EditorGUILayout.BeginHorizontal();
                // ドラッグハンドル: 掴んでリストを並べ替える＝優先度を変える。
                // 幅は行高(singleLineHeight)に追従させ、エディタのフォントサイズが大きいときも
                // 縦長に潰れないようにする（高さだけ追従して幅が固定だと非対称になる）。
                float rowH = EditorGUIUtility.singleLineHeight;
                GUILayout.Label(s_dragHandleContent,
                    dragHandleStyle, GUILayout.Width(rowH), GUILayout.Height(rowH));
                Rect handleRect = GUILayoutUtility.GetLastRect();
                EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.Pan);
                if (GUI.enabled && Event.current.type == EventType.MouseDown
                    && handleRect.Contains(Event.current.mousePosition))
                {
                    _dragZoneIndex = i;
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
                if (GUILayout.Button(s_removeZoneContent, GUILayout.Width(VACCConsts.Layout.RemoveButtonWidth)))
                {
                    removeIndex = i;
                }
                EditorGUILayout.EndHorizontal();

                // ゾーンマスク編集ボタン（フル幅・状態連動）
                {
                    bool isActive = _maskView.activeMaskTarget == i;
                    var prevBg = GUI.backgroundColor;
                    if (isActive) GUI.backgroundColor = VACCColors.ActiveMaskTarget;
                    if (GUILayout.Button(isActive ? s_editMaskActiveContent : s_editMaskInactiveContent))
                    {
                        _maskView.activeMaskTarget = isActive ? -1 : i;
                        _maskView.maskFoldout = true;
                        _maskView.maskDirty = true;
                        Repaint();
                    }
                    GUI.backgroundColor = prevBg;
                }

                // 自動調整ボタン（フル幅）── まだ実用段階でないため UI から隠す（2026-06 一時対応）。
                // 再有効化するときは下のブロックのコメントを外す（RunAutoTune 本体は残してある）。
                /*
                {
                    bool canTune =
                        sourceTexture != null
                        && IsReadable(sourceTexture)
                        && zone.mode == SelectionMode.ColorPick
                        && zone.sampleColor != Color.white;
                    using (new EditorGUI.DisabledScope(!canTune))
                    {
                        if (GUILayout.Button(canTune ? s_autoTuneEnabledContent : s_autoTuneDisabledContent))
                        {
                            RunAutoTune(zone);
                        }
                    }
                }
                */

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
                // かんたんモードでは、サンプルカラーが変わったら自動調整を予約する。
                // 詳細パラメータ（巻き込み抑制の shadowForgivenessSatMin 等）を手で触らせず、
                // 自動調整に委ねることで簡易ユーザーでも誤爆を抑えられるようにする。
                // 通常/上級モードは従来通り手動操作なので自動実行しない。
                // ── 自動調整はまだ実用段階でないため無効化（2026-06 一時対応）。再有効化時にコメントを外す。
                // if (editMode == EditMode.Simple && zone.sampleColor != prevSampleColor)
                //     ScheduleAutoTune(zone);
                _ = prevSampleColor; // 上記コメントアウト中の未使用警告回避（再有効化時に削除）
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
                // 通常モードは従来通りこれらを手動表示し、上級モードはさらに内部パラメータも出す。
                if (editMode != EditMode.Simple)
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

                EditorGUILayout.EndVertical();
                // ドロップ位置判定・インジケータ描画用に、このゾーン全体の矩形を記録。
                // GetLastRect は Layout パスではダミー値だが、判定・描画は非 Layout パスでのみ行う。
                zoneRects.Add(GUILayoutUtility.GetLastRect());
                EditorGUILayout.Space(2);
            }

            // ── ドラッグ並べ替えの処理（インジケータ描画 / ドロップ確定）──
            if (_dragZoneIndex >= 0 && _dragZoneIndex < zoneRects.Count && zoneRects.Count > 0)
            {
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
                    Color accent = VACCColors.ActiveMaskTarget;

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

            if (removeIndex >= 0)
            {
                _pendingRemoveZoneIndex = removeIndex;
                Repaint();
            }

            if (GUILayout.Button(Localization.AddZone))
            {
                _pendingAddZone = true;
                Repaint();
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        // 矩形の枠線を 4 本の細い矩形で描く（Repaint 中のゴースト/スウォッチ枠用）。
        private static void DrawRectOutline(Rect r, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMax - thickness, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, thickness, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.yMin, thickness, r.height), color);
        }

        // ───────────────────────── 自動調整 ───────────────────────────

        // 自動調整に渡す除外マスク（共通 ∪ このゾーン専用）を OR 結合して返す。
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

        // かんたんモード用: サンプルカラーが変わったら自動調整をデバウンス予約する。
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

        // デバウンス経過後、条件を満たせば自動調整を裏で実行する。OnGUI 冒頭から呼ぶ。
        private void ProcessPendingAutoTune()
        {
            if (_pendingAutoTuneZoneId == null) return;
            // 何らかの自動調整（手動含む）が走っている間は待つ。
            if (_autoTuneJob.IsRunning) { Repaint(); return; }
            // デバウンス時間を進めるため、未到達なら再描画を要求して待つ。
            if (EditorApplication.timeSinceStartup - _pendingAutoTuneTime < AutoTuneDebounceSeconds)
            {
                Repaint();
                return;
            }

            string id = _pendingAutoTuneZoneId;
            _pendingAutoTuneZoneId = null;

            // かんたんモード以外へ切り替わっていたら自動実行しない（手動操作を尊重）。
            if (editMode != EditMode.Simple) return;
            var zone = FindZoneById(id);
            if (zone == null) return;
            // 手動ボタンの canTune と同じ発火条件。
            if (sourceTexture == null || !IsReadable(sourceTexture)
                || zone.mode != SelectionMode.ColorPick || zone.sampleColor == Color.white)
                return;

            RunAutoTune(zone, auto: true);
        }

        private void RunAutoTune(ColorZone zone, bool auto = false)
        {
            if (_autoTuneJob.IsRunning) return;
            if (zone == null) return;

            // 実行種別を記録（手動のときだけウィンドウをブロック＋モーダル進捗を出す）。
            _autoTuneIsManual = !auto;

            // ─── 上書き確認はジョブ開始“前”に行う（上級モードの手動実行時のみ）───
            // 完了後にモーダルを出すと Editor がブロックされ、ユーザーの
            // 「他の作業がしたい」要望が満たされない。ラベルは pixels 解析に
            // 依存しない per-zone 判定なのでメインスレッドで先に確定できる。
            // かんたんモードでは詳細パラメータは自動管理（手で変更しない）なので、
            // 自動実行・手動実行ともに上書き確認は出さない。確認が要るのは通常/上級モードで
            // ユーザーが手調整した値を上書きする手動実行のときだけ。
            if (!auto && editMode != EditMode.Simple)
            {
                var previewLabels = ZoneAutoTuner.PreviewOverwrittenLabels(zone);
                if (previewLabels.Count > 0)
                {
                    // applyGlobals は事後判定だが、true になる条件下では globals は既に default
                    // のため AutoTuneOverwriteBody の includesGlobals=true の差分は表示しない。
                    string body = Localization.AutoTuneOverwriteBody(previewLabels, includesGlobals: false);
                    if (!EditorUtility.DisplayDialog(
                            Localization.AutoTuneConfirmTitle, body,
                            Localization.OK, Localization.Cancel))
                    {
                        return;
                    }
                }
            }

            zone.EnsureId();
            string targetId = zone.id;

            // メインスレッド前処理: Texture2D.GetPixels32 と除外マスク構築は
            // バックグラウンドへ持ち込めないので、ここで配列化しておく。
            Color32[] pixels = null;
            int texW = 0, texH = 0;
            var tex = sourceTexture;
            if (tex != null)
            {
                texW = tex.width;
                texH = tex.height;
                // GetPixels32 はメインスレッド必須で、大きいテクスチャでは一瞬フリーズする。
                // 完全な非同期化はできないため、手動実行のときだけモーダル進捗バーで「解析中」を
                // 明示し、無言の固まりに見えないようにする（バックグラウンド解析本体は別途
                // ウィンドウ内進捗バー＋キャンセルで表示される）。かんたんモードの自動実行では
                // 色を変えるたびにモーダルが点滅すると煩いので出さず、裏で静かに走らせる。
                try
                {
                    if (!auto)
                        EditorUtility.DisplayProgressBar(Localization.AutoTune, Localization.AnalyzingTexture, 0.1f);
                    pixels = tex.GetPixels32();
                }
                catch (UnityEngine.UnityException) { pixels = null; }
                finally { if (!auto) EditorUtility.ClearProgressBar(); }
            }
            bool[] excluded = BuildCombinedExclusionForZone(zone, out int mw, out int mh);

            // ZoneAutoTuner.Analyze 内部から触れる session 状態のスナップショット。
            // 直接 _session を渡しても今回は読み取りしかしないが、明示的にスナップショット化する。
            var session = _session;

            _autoTuneTargetZoneId = targetId;
            _autoTuneProgress.Reset();
            _autoTuneProgress.Report(0.05f);

            _autoTuneJob.Schedule(
                work: ct =>
                {
                    _autoTuneProgress.Report(0.10f);
                    var result = ZoneAutoTuner.Analyze(pixels, texW, texH, zone, session, excluded, mw, mh);
                    _autoTuneProgress.Report(1.0f);
                    return result;
                },
                apply: result =>
                {
                    // ジョブ完走時点で zone が消えている / 別 zone に切り替わっている可能性に備え、
                    // id で再ルックアップする。
                    var targetZone = FindZoneById(_autoTuneTargetZoneId);
                    if (targetZone == null) return;

                    // 事前確認済みなのでここではダイアログを出さず、結果を即適用する。
                    Undo.RegisterCompleteObjectUndo(this, "Auto-tune Zone");
                    targetZone.tolerance               = result.tolerance;
                    targetZone.saturationStrictness    = result.saturationStrictness;
                    targetZone.saturationGuard         = result.saturationGuard;
                    targetZone.chromaThreshold         = result.chromaThreshold;
                    targetZone.highlightRecovery       = result.highlightRecovery;
                    targetZone.valueBlend              = result.valueBlend;
                    targetZone.edgeSoftness            = result.edgeSoftness;
                    targetZone.shadowDesaturation      = result.shadowDesaturation;
                    targetZone.shadowForgivenessSatMin = result.shadowForgivenessSatMin;
                    if (result.applyGlobals)
                    {
                        antiAliasCleanup   = result.antiAliasCleanup;
                        useDecontamination = result.useDecontamination;
                    }
                    MarkPreviewDirty();
                    ShowNotification(new GUIContent(Localization.AutoTune));
                    Repaint();
                },
                onError: ex =>
                {
                    Debug.LogError($"[VACC] Auto-tune failed: {ex.Message}\n{ex.StackTrace}");
                    ShowNotification(new GUIContent($"{Localization.Error}: {ex.Message}"));
                });
        }

        private ColorZone FindZoneById(string id)
        {
            if (string.IsNullOrEmpty(id) || _session?.zones == null) return null;
            foreach (var z in _session.zones)
                if (z != null && z.id == id) return z;
            return null;
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

        // ───────────────────────── ユーティリティ ───────────────────────────

        internal static bool IsReadable(Texture2D tex)
        {
            // isReadable は CPU 側からピクセル読み取り可能かを示すネイティブプロパティ。
            // 旧実装の GetPixel(0,0)+例外捕捉(Read/Write 無効テクスチャで UnityException を
            // 投げる)と等価で、Read/Write 無効アセット=false、LoadImage 生成の一時テクスチャ=
            // true を正しく返す。PreviewView.Draw / DrawTextureField / ゾーンごとの canTune 判定から
            // 毎フレーム複数回呼ばれるため、例外コストとネイティブ往復を 1 プロパティ読みに削減する。
            return tex != null && tex.isReadable;
        }

        // Assets 配下のパスを "Assets/..." 形式の相対パスに正規化する。
        // 既に "Assets/" で始まる相対パスでも、絶対パスでも受け付ける。
        // Assets 配下でない場合は null を返す。
        internal static string ToAssetsRelative(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/") || normalized == "Assets")
                return normalized;
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(dataPath + "/"))
                return "Assets" + normalized.Substring(dataPath.Length);
            return null;
        }

        internal static void EnableReadWrite(Texture2D tex)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            // SaveAndReimport はディスク上の import 設定を書き換えるため Undo 不可。
            // 不可逆な操作として明示確認を取る。
            if (!EditorUtility.DisplayDialog(
                    Localization.Confirm,
                    Localization.EnableReadWriteConfirm,
                    Localization.OK,
                    Localization.Cancel))
            {
                return;
            }

            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        private void OnDestroy()
        {
            // PreviewJob 内部の CancellationToken でバックグラウンドタスクを即時中断し、
            // 以降の apply / onError も _disposed フラグで抑止する。
            _previewView?.Dispose();
            _exportView?.Dispose();
            _autoTuneJob?.Dispose();
            _maskView?.SaveToSession();
            _maskView?.ReleaseOverlayTextures();
        }

        // ─────────────────────── 共通ヘルパー ────────────────────

        /// <summary>
        /// 現在の zones に対して id 未設定のものへ GUID を振る。
        /// </summary>
        internal void EnsureAllZoneIds()
        {
            if (_session?.zones == null) return;
            for (int i = 0; i < _session.zones.Count; i++)
                _session.zones[i]?.EnsureId();
        }

        // ── プリセット連携用フォワーダ（PresetsView から呼ばれる） ──
        internal void ApplyMaskFromPreset(VACCPresetData data) => _maskView?.ApplyFromPreset(data);
        internal void WriteMaskToPreset(VACCPresetData data) => _maskView?.WriteToPreset(data);
        internal void ResetActiveMaskTarget() => _maskView?.ResetActiveTarget();
        internal float ExportSectionHeight => _exportView.GetSectionHeight();

        // ── プレビュー / エクスポート用フォワーダ ──
        internal MaskSnapshot BuildMaskSnapshot() => _maskView?.BuildSnapshot();

        // bool[] バッファを _session.maskState に書き戻す（Undo 登録前のスナップショット確定用）。
        // プリセット読込の Undo 登録（PresetsView.LoadPreset）から呼ばれる。
        internal void SyncMaskBuffersToState() => _maskView?.SyncBuffersToState();
    }
}
