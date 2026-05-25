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
        private bool advancedMode { get => _session.advancedMode; set => _session.advancedMode = value; }
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

        // GUILayout安全な変更保留フラグ
        // ExitGUI() をネストしたレイアウトグループ内から呼ぶと
        // Layout/Repaint 間のコントロール数不一致が起きるため、
        // 変更を次の Layout イベント開始時まで遅延させる。
        private bool _pendingAddZone;
        private int _pendingRemoveZoneIndex = -1;
        private bool? _pendingAdvancedMode;

        // 自動調整の非同期ジョブ。メインスレッドで pixels を取得し、
        // バックグラウンドで ZoneAutoTuner.Analyze を走らせる。
        [System.NonSerialized] private readonly PreviewJob<ZoneAutoTuner.TuneResult> _autoTuneJob = new PreviewJob<ZoneAutoTuner.TuneResult>();
        [System.NonSerialized] private readonly PreviewJobProgress _autoTuneProgress = new PreviewJobProgress();
        // apply 時に zone を特定するための GUID。世代不一致で apply が破棄されるか、
        // ユーザーが zone を削除/追加した場合に備えて id で再ルックアップする。
        [System.NonSerialized] private string _autoTuneTargetZoneId;

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
            // Unity 標準 Undo の戻り/進みに合わせて bool[] バッファを _session.maskState から再展開
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
            if (_pendingAdvancedMode.HasValue)
            {
                Undo.RecordObject(this, "Toggle Advanced Mode");
                advancedMode = _pendingAdvancedMode.Value;
                _pendingAdvancedMode = null;
            }
        }

        private void OnGUI()
        {
            // Ctrl+Z / Ctrl+Y は Unity 標準 Undo に統合済みのため、独自処理は不要。
            ProcessPendingZoneChanges();

            // 英語表示が初めて使われたときに AI 機械翻訳である旨を一度だけ告知する。
            // Layout イベント時のみ実行し、描画途中のモーダル表示を避ける。
            if (Event.current.type == EventType.Layout)
                Localization.MaybeShowEnglishTranslationNotice();

            DrawHeader();

            // ジョブ実行中はウィンドウ内 UI を全て無効化する。
            // ただしジョブのオーバーレイ（進捗バー＋キャンセル）は DisabledScope の外で
            // 描画し、キャンセルだけは押せるようにする。
            bool blocking = (_exportView != null && _exportView.IsExporting) || _autoTuneJob.IsRunning;
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
                float topOverheadH = EditorStyles.toolbar.fixedHeight
                    + 4f + (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) * 2 + 4f;
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
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();

                // 右カラム: プレビュー
                // ExpandHeight な ScrollView で囲うことで、プレビューが
                // 横並びセクション高（horizH）を超えても列内でスクロールするようになり、
                // 下部のエクスポートセクションを押し出さない。
                EditorGUILayout.BeginVertical();
                rightScrollPos = EditorGUILayout.BeginScrollView(rightScrollPos, GUILayout.ExpandHeight(true));
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

                scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(topScrollH));

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
            EditorGUILayout.LabelField(Localization.SourceTexture, EditorStyles.boldLabel);

            var newTex = (Texture2D)EditorGUILayout.ObjectField(
                Localization.Texture, sourceTexture, typeof(Texture2D), false);
            if (newTex != sourceTexture)
            {
                _maskView.SaveToSession();                   // persist mask for old texture
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

        private void DrawZoneList()
        {
            zonesFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(zonesFoldout, Localization.ColorZones);
            if (!zonesFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            int removeIndex = -1;
            for (int i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                zone.EnsureId();
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Header row
                EditorGUILayout.BeginHorizontal();
                zone.enabled = UndoHelper.ToggleLeft(this,
                    new GUIContent("", Localization.ZoneEnabledTooltip),
                    zone.enabled, GUILayout.Width(16));
                zone.name = UndoHelper.TextField(this,
                    new GUIContent("", Localization.ZoneNameTooltip),
                    zone.name);
                EditorGUILayout.LabelField(
                    new GUIContent(Localization.LayerIndex, Localization.LayerIndexTooltip),
                    GUILayout.Width(14));
                zone.layerIndex = Mathf.Max(0, UndoHelper.IntField(this, zone.layerIndex, GUILayout.Width(30)));
                if (GUILayout.Button(new GUIContent("×", Localization.RemoveZoneTooltip), GUILayout.Width(VACCConsts.Layout.RemoveButtonWidth)))
                {
                    removeIndex = i;
                }
                EditorGUILayout.EndHorizontal();

                // ゾーンマスク編集ボタン（フル幅・状態連動）
                {
                    bool isActive = _maskView.activeMaskTarget == i;
                    var prevBg = GUI.backgroundColor;
                    if (isActive) GUI.backgroundColor = VACCColors.ActiveMaskTarget;
                    string label = isActive ? Localization.EditMaskActiveLabel : Localization.EditMaskInactiveLabel;
                    if (GUILayout.Button(new GUIContent(label, Localization.EditMaskTooltip)))
                    {
                        _maskView.activeMaskTarget = isActive ? -1 : i;
                        _maskView.maskFoldout = true;
                        _maskView.maskDirty = true;
                        Repaint();
                    }
                    GUI.backgroundColor = prevBg;
                }

                // 自動調整ボタン（フル幅）
                {
                    bool canTune =
                        sourceTexture != null
                        && IsReadable(sourceTexture)
                        && zone.mode == SelectionMode.ColorPick
                        && zone.sampleColor != Color.white;
                    string tip = canTune ? Localization.AutoTuneTooltip : Localization.AutoTuneDisabledTooltip;
                    using (new EditorGUI.DisabledScope(!canTune))
                    {
                        if (GUILayout.Button(new GUIContent(Localization.AutoTune, tip)))
                        {
                            RunAutoTune(zone);
                        }
                    }
                }

                // ─── UV矩形モード選択UI ───
                // UV矩形モードは実装継続中のため当面 UI から非表示。
                // zone.mode = UndoHelper.EnumPopup(this,
                //     new GUIContent(Localization.SelectionMode, Localization.SelectionModeTooltip),
                //     zone.mode);

                // ColorPick UI（常時表示）
                zone.sampleColor = UndoHelper.ColorField(this,
                    new GUIContent(Localization.SampleColor, Localization.SampleColorTooltip),
                    zone.sampleColor);
                zone.tolerance = UndoHelper.Slider(this,
                    new GUIContent(Localization.Tolerance, Localization.ToleranceTooltip),
                    zone.tolerance, 0f, 1f);

                /*
                // ─── Flood Fill UI ───
                // 連続領域モードは実装継続中のため当面 UI から非表示。
                EditorGUILayout.Space(2);
                zone.useFloodFill = UndoHelper.Toggle(this,
                    new GUIContent(Localization.UseFloodFill, Localization.UseFloodFillTooltip),
                    zone.useFloodFill);

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
                        if (GUILayout.Button(
                            new GUIContent(Localization.FloodFillClear, Localization.FloodFillClearTooltip),
                            GUILayout.Width(52)))
                        {
                            Undo.RecordObject(this, "Clear Flood Fill Seed");
                            zone.seedUV = new UnityEngine.Vector2(-1f, -1f);
                        }
                        EditorGUILayout.EndHorizontal();

                        if (advancedMode)
                        {
                            zone.edgeStopThreshold = UndoHelper.Slider(this,
                                new GUIContent(Localization.EdgeStopThreshold, Localization.EdgeStopThresholdTooltip),
                                zone.edgeStopThreshold, 0f, 0.5f);
                        }
                    }
                }
                */

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

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(Localization.IsJapanese ? "=== シャドウ・ハイライト詳細設定 ===" : "=== Shadow/Highlight Details ===", EditorStyles.boldLabel);

                zone.shadowDesaturation = UndoHelper.Slider(this,
                    new GUIContent(Localization.ShadowDesaturation, Localization.ShadowDesaturationTooltip),
                    zone.shadowDesaturation, 0f, 1f);
                zone.shadowForgivenessSatMin = UndoHelper.Slider(this,
                    new GUIContent(Localization.ShadowForgivenessSatMin, Localization.ShadowForgivenessSatMinTooltip),
                    zone.shadowForgivenessSatMin, 0f, 1f);
                zone.chromaThreshold = UndoHelper.Slider(this,
                    new GUIContent(Localization.IsJapanese ? "自動しきい値(無彩色判定)" : "Auto Grayscale Threshold", Localization.IsJapanese ? "スポイトで取ったサンプルの彩度がこの値以下の場合は、自動的に【無彩色(黒/グレー)】として認識され、色相を無視して綺麗に抽出します。" : "If the sample saturation is below this value, it automatically ignores hue and extracts pure grayscale nicely."),
                    zone.chromaThreshold, 0f, 1f);

                if (advancedMode)
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

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
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

        private void RunAutoTune(ColorZone zone)
        {
            if (_autoTuneJob.IsRunning) return;
            if (zone == null) return;

            // ─── 上書き確認はジョブ開始“前”に行う ───
            // 完了後にモーダルを出すと Editor がブロックされ、ユーザーの
            // 「他の作業がしたい」要望が満たされない。ラベルは pixels 解析に
            // 依存しない per-zone 判定なのでメインスレッドで先に確定できる。
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
                try { pixels = tex.GetPixels32(); }
                catch (UnityEngine.UnityException) { pixels = null; }
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

            bool newAdvancedMode = EditorGUILayout.Toggle(
                new GUIContent(Localization.AdvancedMode, Localization.AdvancedModeTooltip),
                advancedMode);
            if (newAdvancedMode != advancedMode)
            {
                // 切替は ProcessPendingZoneChanges 経由で適用されるため、
                // ここでは Undo 登録せず保留フラグだけ立てる（ProcessPendingZoneChanges 側で登録される）。
                _pendingAdvancedMode = newAdvancedMode;
                Repaint();
            }

            if (advancedMode)
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
            try
            {
                tex.GetPixel(0, 0);
                return true;
            }
            catch (UnityException)
            {
                // Read/Write Enabled がオフのテクスチャに GetPixel すると UnityException。
                // これは想定済みなので false 返却で扱う。
                return false;
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                return false;
            }
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
    }
}
