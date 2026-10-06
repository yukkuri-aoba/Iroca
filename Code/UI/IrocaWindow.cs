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
    public partial class IrocaWindow : EditorWindow, IHasCustomMenu
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

        internal void MarkPreviewDirty() { if (_previewView != null) _previewView.previewDirty = true; }

        /// <summary>プレビュー再生成(プロキシ段なし)。確定表示がある前提の差分更新用
        /// (詳細は PreviewView.MarkDirtyFullRefine 参照)。</summary>
        internal void MarkPreviewDirtyFullRefine() => _previewView?.MarkDirtyFullRefine();
        internal void MarkMaskDirty() { if (_maskView != null) _maskView.maskDirty = true; }
        // プレビューが別ウィンドウ(IrocaPreviewWindow)へ切り出されているときは、そちらも
        // 再描画する。PreviewView は自分の実測値の収束・生成ジョブのポーリング・パン/ズームの
        // 反映をこの要求に頼っているため、描画先の窓に届かないと表示が旧フレームで固まる。
        internal void RequestRepaint() { Repaint(); IrocaPreviewWindow.RepaintIfOpen(); }

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

        [MenuItem(IrocaConsts.MenuPath, priority = 100)]
        public static void ShowWindow()
        {
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
            if (window.position.width < 728 || window.position.height < 786)
                window.position = new Rect(window.position.x, window.position.y, 728, 786);
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
        /// </summary>
        internal static void DrawMissingHostNotice()
        {
            EditorGUILayout.HelpBox(Localization.BrushPaletteNoHost, MessageType.Info);
            if (GUILayout.Button(new GUIContent(Localization.OpenIrocaWindow, Localization.OpenIrocaWindowTooltip)))
                ShowWindow();
        }

        private void OnEnable()
        {
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

            // 初回オープン（sourceTexture 未設定）時のみ、前回編集していたテクスチャを自動ロードし、
            // そのテクスチャのセッション（ゾーン/色/処理設定）＋マスクをまるごと復元する。
            // ドメインリロード/レイアウト復元では sourceTexture と _session が既にメモリ上にあるので、
            // ディスクから読み直さず（未保存の変更を潰さないため）、マスクバッファの再展開だけ行う。
            if (sourceTexture == null && TryAutoLoadLastEditedTexture())
                LoadPersistedSessionForCurrentTexture();
            else
                _maskView.RestoreFromSession();

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
            _maskView?.SuspendTransientState();
            SavePersistedSessionForCurrentTexture();
            RememberLastEditedTexture();
        }

        private void OnUndoRedoPerformed()
        {
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
