// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // いろか メインウィンドウ。責務別に partial ファイルへ分割している:
    //   IrocaWindow.cs          … 本体(横断フィールド・ライフサイクル・共通ヘルパー)
    //   IrocaWindow.Layout.cs   … OnGUI とレイアウト/セクション描画
    //   IrocaWindow.ZoneList.cs … ゾーンリストの描画・並べ替え・遅延ミューテーション
    //   IrocaWindow.AutoTune.cs … 自動調整(ZoneAutoTuner)連携
    public partial class IrocaWindow : EditorWindow
    {
        // ── ユーザー入力・設定 ──
        // [SerializeField] を付けることで、スクリプト再コンパイル時に Unity が
        // EditorWindow の状態をシリアライズ/復元し、入力内容が失われにくくなる。
        [SerializeField] private Texture2D sourceTexture;
        internal Texture2D SourceTexture { get => sourceTexture; set => sourceTexture = value; }
        internal IrocaSessionState Session => _session;

        // パイプライン透明化機能（Code.Debug/ asmdef がある場合のみ実体が入る）。
        // PreviewView が ProcessPixelsArray に渡したインスタンスをここに保管し、
        // DebugView が後から読み出す。Debug 機能未導入なら常に null。
        internal IDebugCapture LatestDebugCapture { get; set; }

        // ── プレビューパリティキャッシュ(メインプレビューがフル画像で解いた連結成分 keep と
        //    再着色アンカー/wash/領域L統計を、詳細プレビュー(クロップ)へ転写して出力を一致させる)。
        //    公開後は不変として扱い、メインプレビュージョブは未公開の新規インスタンスにだけ書く。
        //    詳細側は最新の公開キャッシュを寸法一致で参照する。
        [System.NonSerialized] internal PreviewParityCache previewParityCache;

        // ── プレビュー直接スポイト ──
        // スポイトモードで武装中のゾーン id（null/空 = 解除）。プレビュー上のクリックで
        // そのゾーンのサンプルカラーを実テクスチャ画素から取得する（一発で自動解除）。
        // index でなく id で保持するのは、武装中にゾーンを並べ替え・削除しても別ゾーンに
        // 色が入らないようにするため（自動調整の GUID 方式と同様、FindZoneById で解決）。
        // 一時状態なのでドメインリロードをまたいで保持しない（NonSerialized）。
        [System.NonSerialized] private string _eyedropperZoneId;
        internal string EyedropperZoneId { get => _eyedropperZoneId; set => _eyedropperZoneId = value; }

        // ── 各 View からの再描画通知用 ──
        internal void MarkPreviewDirty() { if (_previewView != null) _previewView.previewDirty = true; }
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

        // ── View インスタンス（状態は各 View が自前で保持） ──
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

        // ── _session への薄いアクセサ ──
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
            window.titleContent = new GUIContent(
                Localization.WindowTitle,
                EditorGUIUtility.IconContent("d_Image Icon").image);
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
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
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
            MarkPreviewDirty();
            Repaint();
        }

        internal ColorZone FindZoneById(string id)
        {
            if (string.IsNullOrEmpty(id) || _session?.zones == null) return null;
            foreach (var z in _session.zones)
                if (z != null && z.id == id) return z;
            return null;
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
            SavePersistedSessionForCurrentTexture();
            RememberLastEditedTexture();
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
        internal void ApplyMaskFromPreset(IrocaPresetData data) => _maskView?.ApplyFromPreset(data);
        internal void WriteMaskToPreset(IrocaPresetData data) => _maskView?.WriteToPreset(data);
        internal void ResetActiveMaskTarget() => _maskView?.ResetActiveTarget();
        internal float ExportSectionHeight => _exportView.GetSectionHeight();

        // ── プレビュー / エクスポート用フォワーダ ──
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
            return maskOk;
        }

        /// <summary>
        /// 現在のテクスチャに保存済みのセッション（ゾーン/色/処理設定）を読み込んで適用し、
        /// 続けてマスクを復元する。保存が無ければ既定値（空ゾーン）にリセットする。
        /// テクスチャ切替時・初回自動ロード時に呼ぶ。
        /// </summary>
        private void LoadPersistedSessionForCurrentTexture()
        {
            string path = CurrentTexturePath(sourceTexture);
            IrocaSessionState loaded = null;
            _sessionLoadFailed = false;
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

            _session = IrocaSessionState.CreateDefault();   // ゾーン・色・処理設定を既定へ
            _maskView?.ClearBuffersOnTextureChange();        // マスク bool[] バッファを全消去
            _maskView?.SyncBuffersToState();                 // 空バッファを maskState へ反映（＝空マスク）
            EnsureAllZoneIds();

            _previewView?.InvalidateSourceCache();
            MarkPreviewDirty();
            Repaint();
        }
    }
}
