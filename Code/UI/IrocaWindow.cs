// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // いろか メインウィンドウ。責務別に partial ファイルへ分割している:
    //   IrocaWindow.cs            … 本体(横断フィールド・ライフサイクル・共通ヘルパー)
    //   IrocaWindow.Layout.cs     … OnGUI とレイアウト/セクション描画
    //   IrocaWindow.ZoneList.cs   … ゾーンリストの描画・並べ替え・遅延ミューテーション
    //   IrocaWindow.AutoTune.cs   … 自動調整(ZoneAutoTuner)連携
    //   IrocaWindow.Recipe.cs     … 非破壊(NDMF)レシピとの結び付け・アバターへの登録・
    //                               シーンのライブプレビューへの通知
    //   IrocaWindow.SceneClick.cs … Scene でモデルをクリックした場所をプレビューで示す
    //   IrocaWindow.Autosave.cs   … 編集内容の自動保存
    public partial class IrocaWindow : EditorWindow, IHasCustomMenu, ISerializationCallbackReceiver
    {
        // [SerializeField] を付けることで、スクリプト再コンパイル時に Unity が
        // EditorWindow の状態をシリアライズ/復元し、入力内容が失われにくくなる。
        [SerializeField] private Texture2D sourceTexture;
        internal Texture2D SourceTexture { get => sourceTexture; set => sourceTexture = value; }
        internal IrocaSessionState Session => _session;

        // パイプライン透明化機能（Code.Debug/ asmdef がある場合のみ実体が入る）。
        // PreviewView が ProcessPixelsArray に渡したインスタンスをここに保管し、
        // DebugView が後から読み出す。Debug 機能未導入なら常に null。
        internal IDebugCapture LatestDebugCapture { get; set; }

        // スポイトモードで武装中のゾーン id（null/空 = 解除）。プレビュー上のクリックで
        // そのゾーンのサンプルカラーを実テクスチャ画素から取得する（一発で自動解除）。
        // index でなく id で保持するのは、武装中にゾーンを並べ替え・削除しても別ゾーンに
        // 色が入らないようにするため（自動調整の GUID 方式と同様、FindZoneById で解決）。
        // 一時状態なのでドメインリロードをまたいで保持しない（NonSerialized）。
        [System.NonSerialized] private string _eyedropperZoneId;
        internal string EyedropperZoneId { get => _eyedropperZoneId; set => _eyedropperZoneId = value; }

        // 「スポイトで変えたい色を選ぶ」（ゾーンがあるときは「スポイトで別の色を追加」）の武装。
        // EyedropperZoneId にこの値を入れると、プレビューのクリックでゾーンを作り、その画素の色を
        // サンプルカラーに入れる（Undo 1 回で戻る）。
        // 以前は「+ ゾーン追加」→「スポイト」→ クリックの 3 手で、最初の 1 手が何のためか分かりにくかった。
        // ゾーン id は GUID なので、制御文字で始まるこの値とは衝突しない。
        internal const string NewZoneEyedropperId = "\u0001new-zone";

        // シード指定モードで武装中のゾーン id（null/空 = 解除）。プレビュー上のクリックで
        // そのゾーンの連続領域シードを置く（一発で自動解除）。スポイトと同じ id 保持なのは
        // 同じ理由（武装中の並べ替え・削除で別ゾーンへ入らないように）。
        //
        // Shift+クリックでもシードは置けるが、その経路は「マスク編集対象のゾーン、無ければ
        // 先頭の該当ゾーン」という暗黙の選び方で、どのゾーンに入るかが画面から分からなかった。
        // ゾーンカードから武装すれば対象が一意に決まる。
        [System.NonSerialized] private string _seedPickZoneId;
        internal string SeedPickZoneId { get => _seedPickZoneId; set => _seedPickZoneId = value; }

        // ソロ表示中のゾーン id（null/空 = 通常表示）。プレビュー（メイン・詳細とも）を
        // このゾーンだけで生成し、「そのゾーンが実際にどこを拾っているか」を確かめられるようにする。
        // ★表示専用★ — エクスポートは常に有効ゾーンすべてを適用する（ExportView が注意を出す）。
        // 一時状態なのでドメインリロードをまたがない（NonSerialized）。
        [System.NonSerialized] private string _soloZoneId;
        internal string SoloZoneId { get => _soloZoneId; set => _soloZoneId = value; }

        /// <summary>
        /// ソロ表示中のゾーン（解除中・対象が消えた場合は null）。
        /// プレビュー生成側がゾーンスナップショットを絞り込むのに使う。
        /// </summary>
        internal ColorZone SoloZone =>
            string.IsNullOrEmpty(_soloZoneId) ? null : FindZoneById(_soloZoneId);

        /// <summary>
        /// ソロ表示の対象を切り替える（null で解除）。
        /// 拡大表示（詳細クロップ）の切り出し元になるフル段の出力は「どのゾーン集合を処理したか」に
        /// 依存するので、必ず捨てる。残すと、切り替え直後にスクロールした箇所だけ切り替え前の色が出る。
        /// </summary>
        internal void SetSoloZone(string zoneId)
        {
            _soloZoneId = string.IsNullOrEmpty(zoneId) ? null : zoneId;
            _previewView?.InvalidateFullOutput();
            MarkPreviewDirty();
        }

        /// <summary>
        /// プレビュー上のクリックの意味を変える一時モード（スポイト・シード指定・
        /// マスクブラシ）と、AI 提案の推論待ちをすべて解除する。Esc キーの受け口。
        /// 戻り値 true = 実際に何かを解除した（呼び出し側がイベントを消費してよい）。
        /// </summary>
        internal bool ClearPreviewInteractionModes()
        {
            bool any = false;
            if (!string.IsNullOrEmpty(_eyedropperZoneId)) { _eyedropperZoneId = null; any = true; }
            if (!string.IsNullOrEmpty(_seedPickZoneId)) { _seedPickZoneId = null; any = true; }
            if (_maskView != null)
            {
                if (_maskView.maskPaintActive) { _maskView.DeactivateBrush(); any = true; }
                var ctl = _maskView.SuggestControllerIfCreated;
                if (ctl != null && ctl.CancelPending()) any = true;
            }
            if (any) RequestRepaint();
            return any;
        }

        /// <summary>
        /// 後から届く結果(自動調整・AI 提案)を、独立した「元に戻す」1 回分として記録する。
        /// Unity の Undo のまとまりはマウスを押したとき・メニューを実行したときにしか切り替わらないので、
        /// 結果を待っている間に別のウィンドウでした操作(物の移動・マテリアルの値の変更など)と同じ 1 回分に
        /// まとまり、Ctrl+Z 1 回で両方が戻っていた。記録の前後で区切る(後ろも区切るのは、届いた直後に
        /// 続く別の操作をこちらへ混ぜないため)。
        /// </summary>
        internal static void RecordAsSeparateUndo(string name, System.Action record)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            int group = Undo.GetCurrentGroup();
            try { record(); }
            finally
            {
                // 記録の中で別の名前が付いても(マスクのストロークなど)、このまとまりの名前で出す。
                Undo.SetCurrentGroupName(name);
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
        }

        internal void MarkPreviewDirty() { if (_previewView != null) _previewView.previewDirty = true; }

        /// <summary>プレビュー再生成(プロキシ段なし)。確定表示がある前提の差分更新用
        /// (詳細は PreviewView.MarkDirtyFullRefine 参照)。</summary>
        internal void MarkPreviewDirtyFullRefine() => _previewView?.MarkDirtyFullRefine();
        internal void MarkMaskDirty() { if (_maskView != null) _maskView.maskDirty = true; }
        // プレビューが別ウィンドウ(IrocaPreviewWindow)へ切り出されているときは、そちらも
        // 再描画する。PreviewView は自分の実測値の収束・生成ジョブのポーリング・パン/ズームの
        // 反映をこの要求に頼っているため、描画先の窓に届かないと表示が旧フレームで固まる。
        internal void RequestRepaint() { BumpUiState(); Repaint(); IrocaPreviewWindow.RepaintIfOpen(); }

        // 別ウィンドウ(プレビュー・マスク編集)が「本体の状態が変わったか」を見る版番号。別ウィンドウは
        // OnInspectorUpdate(約 10 回/秒)でこれを見て、変わったときだけ描き直す(以前は操作しなくても毎回
        // 描き直していた)。増やすのは、プレビューの作り直しの要求(PreviewView.previewDirty)・マスクの変更
        // (MaskPaintView.maskDirty)・再描画の要求(RequestRepaint。ジョブの完了・AI の状態の変化・追い
        // 再描画もここを通る)・いろかのウィンドウへの入力(MayChangeState)。書き出し・自動調整の
        // 始まりと終わりは、別ウィンドウが IsJobBlockingUI を直接比べる(DrawStamp)。
        [System.NonSerialized] private int _uiStateVersion;

        internal void BumpUiState() { unchecked { _uiStateVersion++; } }

        /// <summary>状態を変えうる入力か(描画とレイアウト・マウスの移動だけのイベントは除く)。</summary>
        internal static bool MayChangeState(EventType t) =>
            t != EventType.Layout && t != EventType.Repaint && t != EventType.Used && t != EventType.Ignore
            && t != EventType.MouseMove && t != EventType.MouseEnterWindow && t != EventType.MouseLeaveWindow;

        /// <summary>
        /// 別ウィンドウが最後に描いた時点の本体(と版番号・ジョブ中か)の控え。OnGUI の Repaint のときに
        /// 描く前に <see cref="Record"/> し、OnInspectorUpdate で <see cref="Changed"/> なら描き直す。
        /// </summary>
        internal struct DrawStamp
        {
            private IrocaWindow _host;
            private int _version;
            private bool _blocking;

            public void Record(IrocaWindow host)
            {
                _host = host;
                _version = host != null ? host._uiStateVersion : 0;
                _blocking = host != null && host.IsJobBlockingUI;
            }

            /// <param name="host">いまの本体(無い・破棄済みなら null を渡す)。</param>
            public bool Changed(IrocaWindow host)
            {
                // 破棄された本体も別の物として比べる(Unity の == は破棄済みを null と等しく扱う)。
                if (!ReferenceEquals(host, _host)) return true;
                return host != null && (host._uiStateVersion != _version || host.IsJobBlockingUI != _blocking);
            }
        }

        // 別ウィンドウが本体を見失っているとき、探し直す間隔(探すのはエディタ内の全オブジェクトの走査)。
        internal const double HostSearchIntervalSeconds = 1.0;

        // Play 中のリロード直後に待たせているプレビューを、次の Layout から始める印(TrackDeferredPreview)。
        [System.NonSerialized] private bool _resumeDeferredPreview;

        /// <summary>
        /// Play を始めたときのドメインリロードの直後に待たせている、原本の読み直しとプレビューの作り直し
        /// (<see cref="PreviewView.SourceLoadDeferred"/>)を、このウィンドウか切り出したプレビューが操作されたら
        /// (マウスが入る・動く・クリック・スクロール・キー)始める。両ウィンドウの OnGUI の先頭で呼ぶ。
        /// 操作されたイベントの途中で描くものを変えると IMGUI がエラーを出すので、始めるのは次の Layout から。
        /// 待っている間はマウスの移動と出入りも受け取る(乗せただけで始まるように)。
        /// </summary>
        internal void TrackDeferredPreview(EditorWindow from)
        {
            bool deferred = _previewView != null && _previewView.SourceLoadDeferred;
            if (deferred)
            {
                var type = Event.current.type;
                if (type == EventType.Layout && _resumeDeferredPreview)
                {
                    _previewView.EndSourceLoadDeferral();
                    deferred = false;
                }
                else if (!_resumeDeferredPreview && IsDeferredPreviewTrigger(type))
                {
                    _resumeDeferredPreview = true;
                    RequestRepaint();
                }
            }
            if (!deferred) _resumeDeferredPreview = false;
            if (from.wantsMouseMove != deferred) from.wantsMouseMove = deferred;
            if (from.wantsMouseEnterLeaveWindow != deferred) from.wantsMouseEnterLeaveWindow = deferred;
        }

        private static bool IsDeferredPreviewTrigger(EventType t) =>
            t == EventType.MouseEnterWindow || t == EventType.MouseMove || t == EventType.MouseDown
            || t == EventType.MouseUp || t == EventType.MouseDrag || t == EventType.ScrollWheel
            || t == EventType.ContextClick || t == EventType.KeyDown
            || t == EventType.DragUpdated || t == EventType.DragPerform;

        /// <summary>
        /// ディスク上のソース画素が変わったときに呼ぶ（エクスポートで元ファイルを上書きした等）。
        /// プレビューはディスクの現物を読み直して処理するため、ここで無効化しないと
        /// 「旧画素 × 1 回再着色」を表示したまま実出力だけが「上書き済み画素 × もう 1 回再着色」
        /// ＝二重適用になる。
        /// </summary>
        internal void InvalidateSourceAndRepaint()
        {
            _previewView?.InvalidateSourceCache();
            MarkPreviewDirty();
            Repaint();
        }

        [SerializeField] private ExportView _exportView = new ExportView();
        [SerializeField] private PresetsView _presetsView = new PresetsView();
        [SerializeField] internal MaskPaintView _maskView = new MaskPaintView();
        [SerializeField] private PreviewView _previewView = new PreviewView();
        // プレビューを別ウィンドウ(IrocaPreviewWindow)へ切り出すときの描画委譲先。
        // 状態の所有はここ(本体)のまま＝どちらの窓で描いてもズーム/比較/マスクは同じ。
        internal PreviewView Preview => _previewView;

        // 編集状態（ゾーン定義・処理パラメータ・マスク状態）。
        // 個別 [SerializeField] フィールド群を IrocaSessionState に集約したもの。
        [SerializeField] private IrocaSessionState _session = IrocaSessionState.CreateDefault();

        // partial class 内のコードが、集約前と同じフィールド名のまま _session 内のフィールドへ
        // アクセスできるようにするブリッジ。新規コードは _session.xxx を直接参照してよい。
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

        // ShowWindow が確保する既定サイズ（内訳は ShowWindow 内のコメント参照）。
        private const float DefaultWindowWidth = IrocaConsts.Layout.LeftColumnMin + IrocaConsts.Layout.PreviewColumnReserve; // 320 + 408 = 728
        private const float DefaultWindowHeight = 786f; // 実測値

        [MenuItem(IrocaConsts.MenuPath, priority = 100)]
        public static void ShowWindow()
        {
            // 大きさを決めるのは新しく作ったときだけ。開いている窓の position に代入すると、ドッキング中の窓は
            // Unity がドッキングから外して浮かせる(タブが複数ある区画・分割の中にある場合)。
            bool wasOpen = HasOpenInstances<IrocaWindow>();
            var window = GetWindow<IrocaWindow>(Localization.WindowTitle);
            // アイコン名に "d_" を付けないこと。IconContent は装飾なしの名前を渡すと
            // ダークスキンのとき自動で "d_" 版を探す(無ければ素の名前へフォールバックする)。
            // "d_" を書くとライトスキンでもダーク用アイコンが出る。
            window.titleContent = new GUIContent(
                Localization.WindowTitle,
                EditorGUIUtility.IconContent("Image Icon").image);
            window.minSize = new Vector2(340, 500);
            // 中身が要求するぶんだけの既定サイズ。どちらも実ウィンドウのキャプチャで詰めた値で、
            // これ未満にすると設定行の右端が切れるか、プレビューにスクロールバーが出る。
            //   幅 728 = 左カラム 320(Layout.LeftColumnMin=設定行がすべて見える幅)
            //          + プレビュー予約 408(Layout.PreviewColumnReserve)
            //   高さ 786 = 上部テクスチャ欄＋プレビュー章の見出し/操作行＋エクスポート欄
            //          (新規保存 ON のファイル名行込み)を積んでも、等倍(100%)の
            //          MaxSize(384)px プレビューが縦スクロールバーなしで収まる高さ
            //          (実測で数十 px の余裕。設定列を見渡せるぶんとして残す)。
            if (!wasOpen && (window.position.width < DefaultWindowWidth || window.position.height < DefaultWindowHeight))
                window.position = new Rect(window.position.x, window.position.y, DefaultWindowWidth, DefaultWindowHeight);
        }

        /// <summary>
        /// 開いている IrocaWindow を 1 つ返す（無ければ null）。別ウィンドウ（プレビュー / マスク編集）が
        /// ドメインリロード後などに本体へ再接続するときの規則で、複数ある場合は先頭。
        /// </summary>
        internal static IrocaWindow FindAnyInstance()
        {
            var hosts = Resources.FindObjectsOfTypeAll<IrocaWindow>();
            return hosts.Length > 0 ? hosts[0] : null;
        }

        /// <summary>
        /// 別ウィンドウ（プレビュー / マスク編集）で本体が見つからないときの案内と「Iroca を開く」ボタン。
        /// 押して本体を開いたら true(呼び出し側はすぐ探し直す)。
        /// </summary>
        internal static bool DrawMissingHostNotice()
        {
            EditorGUILayout.HelpBox(Localization.BrushPaletteNoHost, MessageType.Info);
            if (!GUILayout.Button(new GUIContent(Localization.OpenIrocaWindow, Localization.OpenIrocaWindowTooltip)))
                return false;
            ShowWindow();
            return true;
        }

        private void OnEnable()
        {
            // ドメインリロードの書き戻しはここで取り込むので、次の Undo の印として残さない。
            _stateRestoredByUnity = false;
            _session ??= IrocaSessionState.CreateDefault();
            _exportView ??= new ExportView();
            _exportView.Initialize(this);
            _presetsView ??= new PresetsView();
            _presetsView.Initialize(this);
            _maskView ??= new MaskPaintView();
            _maskView.Initialize(this);
            _previewView ??= new PreviewView();
            _previewView.Initialize(this);
            EnsureAllZoneIds();
            // AssetWatcher の delete フックを取りこぼした場合の保険として、
            // ウィンドウを開いた時に MaskCache / SessionCache の orphan ファイルを掃除する。
            MaskFileStore.CleanupOrphans();
            SessionFileStore.CleanupOrphans();

            // 新しく開いたとき（sourceTexture 未設定）と、Unity を起動して最初の有効化（レイアウトからの復元）では、
            // 前回編集していたテクスチャを自動ロードし、そのテクスチャのセッション（ゾーン/色/処理設定）＋
            // マスクをディスクからまるごと復元する。
            // レイアウト復元では sourceTexture と _session もメモリ上に戻るが、Unity がレイアウトを書くのは
            // 正常終了のときだけなので、落ちた後の起動では最後に正常終了したときの古い編集内容が戻る。それを
            // 使うと、次の保存（ドメインリロード・閉じる）で自動保存済みの新しい編集とレシピを上書きしていた。
            // ディスクは編集のたびの自動保存と終了時の保存があるので、レイアウトより古くならない。
            // ドメインリロードでは直前の OnDisable で保存したメモリ上の状態をそのまま使い、マスクバッファの
            // 再展開だけ行う。
            bool firstSinceEditorStart = MarkEnabledSinceEditorStart();
            if (sourceTexture == null || firstSinceEditorStart)
            {
                // 前回のテクスチャを解決できなければ、今のテクスチャ（レイアウトのもの）のまま読む。
                TryAutoLoadLastEditedTexture();
                if (sourceTexture != null) LoadPersistedSessionForCurrentTexture();
            }
            else
                _maskView.RestoreFromSession();

            // Play を始めたときのドメインリロードの直後は、原本の読み直しと色替えの計算し直しを、ウィンドウが
            // 操作されるまで待つ(PreviewView.SourceLoadDeferred)。Play 中以外は今までどおりすぐ読む。
            if (!firstSinceEditorStart && EditorApplication.isPlayingOrWillChangePlaymode)
                _previewView.DeferSourceLoad();

            // Unity 標準 Undo の戻り/進みに合わせて bool[] バッファを _session.maskState から再展開。
            // 二重購読を避けるため一度外してから登録する（PreviewJobMainThread.Install と同じ防御）。
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            // 非破壊ビルドの直前に、編集中の内容を結び付いたレシピへ書き出す。
            NonDestructiveApplier.BeforeApply -= FlushToBoundRecipe;
            NonDestructiveApplier.BeforeApply += FlushToBoundRecipe;
            // 編集が止まって少ししたら保存する(Unity が落ちても直前の編集が残るように)。
            StartAutosave();
            // Scene でモデルをクリックした場所をプレビューで示す(クリックは横から見るだけで消費しない)。
            InstallSceneClick();

            // AI(Sentis + 配布モデル)の準備は、ウィンドウ上部の非モーダルな帯で案内する
            // (MaskSuggestSection.DrawSetupBanner)。以前はここで delayCall からモーダルを
            // 出していたが、開いた瞬間に Editor 全体がブロックされるうえ、「あとで」を選ぶと
            // 案内ごと消えて「自動調整だけが黙って使えない」状態が残った(2026-09-11 の UX 見直し)。
            // 自動調整を押した時点で AI が無ければ、そのときは従来どおりダイアログで案内する
            // (ユーザーの操作に対する直接の応答なので、そこはモーダルでよい)。
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            NonDestructiveApplier.BeforeApply -= FlushToBoundRecipe;
            StopAutosave();   // ここで保存するので予定は消す
            UninstallSceneClick();
            // ウィンドウを閉じたら、シーンのアバターへの編集中の表示もやめる(登録済みならレシピの表示に戻る)。
            LivePreview.SetTarget(null, null);
            // 証拠待ちの自動調整は EditorApplication.update に張っているので、ドメインリロード・
            // ウィンドウ無効化で残さない（届いた提案はコントローラが捨てる）。
            CancelEvidenceWait();
            _previewView?.Suspend();
            // 閉じたら作業配列(ネイティブメモリ)を OS へ返す。取り消したプレビューなど計算中の分は、走り終わってから返す。
            NativeMemoryLifecycle.ReleaseWhenIdle();
            _maskView?.SuspendTransientState();
            SavePersistedSessionForCurrentTexture();
            RememberLastEditedTexture();
        }

        // Unity がこのウィンドウの状態を書き戻した(Undo/Redo・ドメインリロード)印。
        // Undo.undoRedoPerformed はエディタ全体の Undo で呼ばれるので、シーンや別ウィンドウの
        // Ctrl+Z でも、これが立っていなければウィンドウの状態は変わっていない。Undo の書き戻しは
        // シリアライザ経由で OnAfterDeserialize を通る(UndoRestoreSignalTests が実機で確かめている)。
        [System.NonSerialized] private bool _stateRestoredByUnity;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        // 読み込みスレッドから呼ばれることがあるので、印を立てる以外のことはしない。
        void ISerializationCallbackReceiver.OnAfterDeserialize() => _stateRestoredByUnity = true;

        private void OnUndoRedoPerformed()
        {
            // 関係ない Undo(シーンのオブジェクトの移動など)で、全マスクの再展開とフル解像度の
            // 再生成を走らせない。
            if (!_stateRestoredByUnity) return;
            _stateRestoredByUnity = false;

            // _session.maskState の RLE 文字列が Undo で書き戻されたので、
            // bool[] バッファを再展開してオーバーレイ・プレビューを更新させる。
            if (_maskView != null)
            {
                _maskView.SyncBuffersFromState();
                _maskView.maskDirty = true;
                // 保留中の AI 提案は Undo 前の状態に対する提案なので重ねない
                _maskView.SuggestControllerIfCreated?.OnUndoRedoPerformed();
            }
            // スポイト位置（ColorZone.sampleUV）はシリアライズ対象なので、sampleColor と対で
            // Undo が書き戻す。ここで手当てする必要はない。
            // ズーム倍率・スクロール位置は「今どこを見ているか」であって編集内容ではない。
            // Undo の書き戻しで一緒に巻き戻るため、直前の視点へ戻して見ている箇所を保つ。
            _previewView?.RestoreViewStateAfterUndo(this);
            MarkPreviewDirty();
            Repaint();
        }

        /// <summary>
        /// このテクスチャから処理用の画素を取れるか。Read/Write が無効でも、原本が
        /// PNG/JPG ならファイルを直接読めるので作業できる（プレビュー・自動調整・
        /// 書き出しはいずれも同じ true source 経路を使う）。UI の可否判定はこれを見る。
        /// </summary>
        internal bool CanReadSource(Texture2D tex) =>
            tex != null && (IsReadable(tex)
                || (_previewView != null && _previewView.CanProvideSourcePixels(tex)));

        internal ColorZone FindZoneById(string id)
        {
            if (string.IsNullOrEmpty(id) || _session?.zones == null) return null;
            foreach (var z in _session.zones)
                if (z != null && z.id == id) return z;
            return null;
        }

        internal static bool IsReadable(Texture2D tex)
        {
            // isReadable は CPU 側からピクセル読み取り可能かを示すネイティブプロパティ。
            // 旧実装の GetPixel(0,0)+例外捕捉(Read/Write 無効テクスチャで UnityException を
            // 投げる)と等価で、Read/Write 無効アセット=false、LoadImage 生成の一時テクスチャ=
            // true を正しく返す。PreviewView.Draw / DrawTextureField / ゾーンごとの canTune 判定から
            // 毎フレーム複数回呼ばれるため、例外コストとネイティブ往復を 1 プロパティ読みに削減する。
            return tex != null && tex.isReadable;
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
            CancelEvidenceWait();
            _autoTuneJob?.Dispose();
            SavePersistedSessionForCurrentTexture();
            RememberLastEditedTexture();
            _maskView?.ReleaseOverlayTextures();
            // 閉じた後も AI の推論のワーカーが GPU のメモリを持ち続けないように(次に開いたら作り直す)。
            MaskSuggestBridge.Service?.ReleaseGpuResources();
        }

        /// <summary>
        /// 現在の zones に対して id 未設定のものへ GUID を振る。
        /// </summary>
        internal void EnsureAllZoneIds()
        {
            if (_session?.zones == null) return;
            for (int i = 0; i < _session.zones.Count; i++)
                _session.zones[i]?.EnsureId();
        }

        internal void ApplyMaskFromPreset(IrocaPresetData data) => _maskView?.ApplyFromPreset(data);
        internal void WriteMaskToPreset(IrocaPresetData data) => _maskView?.WriteToPreset(data);
        internal void ResetActiveMaskTarget() => _maskView?.ResetActiveTarget();

        internal MaskSnapshot BuildMaskSnapshot() => _maskView?.BuildSnapshot();

        // bool[] バッファを _session.maskState に書き戻す（Undo 登録前のスナップショット確定用）。
        // プリセット読込の Undo 登録（PresetsView.LoadPreset）から呼ばれる。
        internal void SyncMaskBuffersToState() => _maskView?.SyncBuffersToState();

        // ─────────────── セッション永続化（テクスチャ GUID 単位） ───────────────
        // マスクは MaskFileStore、ゾーン・色・処理設定は SessionFileStore が、それぞれ
        // テクスチャ GUID 単位でディスク保存する。ウィンドウを閉じても、開き直した
        // テクスチャの編集内容がまるごと復元される。前回編集テクスチャは EditorPrefs に
        // 記憶し、初回オープン時に自動ロードする。

        // 最後に編集していたテクスチャの GUID を保存する EditorPrefs キー（Editor 再起動を跨ぐ）。
        // EditorPrefs は Unity インストール単位(全プロジェクト共有)なので、プロジェクトパス
        // (Application.dataPath)の安定ハッシュを付与してプロジェクトごとに分離する。これが無いと
        // コピーした別プロジェクトが前プロジェクトの GUID を誤って自動ロードしうる。string.GetHashCode
        // はプロセス間で不安定(乱択化)なため FNV-1a を使う(再起動を跨いで同一キー)。
        private static string LastTextureGuidPrefKey =>
            "Iroca.LastTextureGuid." + StableHashHex(Application.dataPath);

        private static string StableHashHex(string s)
        {
            uint h = 2166136261u; // FNV-1a 32bit
            if (s != null)
                foreach (char c in s) { h ^= c; h *= 16777619u; }
            return h.ToString("X8");
        }

        // このウィンドウが Unity の起動後に有効化されたかの印。SessionState は Unity を閉じると消え、
        // ドメインリロードでは残る。ウィンドウの instanceID はドメインリロードで変わらない。
        private string EnabledSinceEditorStartKey => "Iroca.WindowEnabled." + GetInstanceID();

        /// <summary>Unity を起動してからこのウィンドウの最初の有効化なら true（印を付ける）。ドメインリロード後は false。</summary>
        private bool MarkEnabledSinceEditorStart()
        {
            string key = EnabledSinceEditorStartKey;
            bool first = !SessionState.GetBool(key, false);
            SessionState.SetBool(key, true);
            return first;
        }

        // SessionCache ファイルが「存在するのに読めなかった」フラグ。true の間は空保存での
        // 削除・無退避上書きを抑止する（MaskPaintView._maskLoadFailed と同じ防御）。
        [System.NonSerialized] private bool _sessionLoadFailed;

        private static string CurrentTexturePath(Texture2D tex)
        {
            if (tex == null) return null;
            string path = AssetDatabase.GetAssetPath(tex);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// 現在のテクスチャのマスク＋セッション（ゾーン/色/処理設定）をディスクへ保存する。
        /// マスク保存に失敗したときだけ false（呼び出し側でユーザー通知に使う）。
        /// </summary>
        private bool SavePersistedSessionForCurrentTexture()
        {
            bool maskOk = _maskView == null || _maskView.SaveToSession();
            string path = CurrentTexturePath(sourceTexture);
            if (path != null && _session != null)
                SessionFileStore.SaveSession(path, _session, _sessionLoadFailed);
            // 非破壊のレシピが結び付いていればそちらにも書く(SaveToSession でマスクは state へ同期済み)。
            SaveSessionToBoundRecipe();
            return maskOk;
        }

        /// <summary>
        /// 現在のテクスチャに保存済みのセッション（ゾーン/色/処理設定）を読み込んで適用し、
        /// 続けてマスクを復元する。保存が無ければ既定値（空ゾーン）にリセットする。
        /// テクスチャ切替時・初回自動ロード時に呼ぶ。
        /// </summary>
        private void LoadPersistedSessionForCurrentTexture()
        {
            _sessionLoadFailed = false;
            // 非破壊のレシピがあればそれが正(マスクも含めてレシピから読む)。
            if (TryLoadSessionFromRecipe()) return;

            string path = CurrentTexturePath(sourceTexture);
            IrocaSessionState loaded = null;
            if (path != null)
            {
                loaded = SessionFileStore.LoadSession(path, out bool unreadable);
                _sessionLoadFailed = unreadable;
            }

            // 読み込めた内容 or 既定値で _session をまるごと置き換える。maskState は空にしておき、
            // 直後の RestoreFromSession がディスクのマスクファイルから読み直して上書きする
            // （マスクが無ければ空のまま＝正しい）。
            _session = loaded ?? IrocaSessionState.CreateDefault();
            _session.maskState = new MaskState();
            EnsureAllZoneIds();

            _maskView?.RestoreFromSession();
            MarkPreviewDirty();
        }

        /// <summary>
        /// 現在のテクスチャ GUID を「前回編集テクスチャ」として記憶する（テクスチャ未設定なら空）。
        /// </summary>
        private void RememberLastEditedTexture()
        {
            string path = CurrentTexturePath(sourceTexture);
            string guid = path != null ? AssetDatabase.AssetPathToGUID(path) : "";
            EditorPrefs.SetString(LastTextureGuidPrefKey, guid ?? "");
        }

        /// <summary>
        /// EditorPrefs に記憶した前回編集テクスチャを解決して sourceTexture へ設定する。
        /// 解決できたら true（呼び出し側でセッション/マスク復元を続ける）。
        /// </summary>
        private bool TryAutoLoadLastEditedTexture()
        {
            string guid = EditorPrefs.GetString(LastTextureGuidPrefKey, "");
            if (string.IsNullOrEmpty(guid)) return false;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return false;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) return false;

            sourceTexture = tex;
            _exportView?.SetSourceTextureBaseName(Path.GetFileNameWithoutExtension(path));
            return true;
        }

        /// <summary>
        /// 現在のテクスチャの編集内容（ゾーン・色・処理設定・マスク）をすべて破棄して
        /// 初期状態へ戻す。Undo 登録するので「元に戻す」で復元できる。
        /// ヘッダーの「リセット」ボタンから確認ダイアログを経て呼ばれる。
        /// </summary>
        private void ResetCurrentSession()
        {
            Undo.RegisterCompleteObjectUndo(this, "Reset Iroca Session");

            _session = IrocaSessionState.CreateDefault();
            _maskView?.ClearBuffersOnTextureChange();
            _maskView?.SyncBuffersToState();
            EnsureAllZoneIds();

            _previewView?.InvalidateSourceCache();
            MarkPreviewDirty();
            Repaint();
        }
    }
}
