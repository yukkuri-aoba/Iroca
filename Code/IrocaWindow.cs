// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
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

        // ── 連続領域モードの keep キャッシュ(メインプレビューがフル画像で解いた結果を
        //    詳細プレビューへ転写して一致させる)。公開後は不変として扱い、メインプレビュージョブは
        //    未公開の新規インスタンスにだけ書く。詳細側は最新の公開キャッシュを寸法一致で参照する。
        [System.NonSerialized] internal FloodFillKeepCache floodFillKeepCache;

        // ── 各 View からの再描画通知用 ──
        internal void MarkPreviewDirty() { if (_previewView != null) _previewView.previewDirty = true; }
        internal void MarkMaskDirty() { if (_maskView != null) _maskView.maskDirty = true; }
        internal void RequestRepaint() { Repaint(); }

        // ── View インスタンス（状態は各 View が自前で保持） ──
        [SerializeField] private ExportView _exportView = new ExportView();
        [SerializeField] private PresetsView _presetsView = new PresetsView();
        [SerializeField] internal MaskPaintView _maskView = new MaskPaintView();
        [SerializeField] private PreviewView _previewView = new PreviewView();

        // 編集状態（ゾーン定義・処理パラメータ・マスク状態）。
        // 個別 [SerializeField] フィールド群を IrocaSessionState に集約したもの。
        [SerializeField] private IrocaSessionState _session = IrocaSessionState.CreateDefault();

        // SerializedObject(this) 経由のプロパティ編集基盤。
        // ColorZoneDrawer / PropertyField 経由の編集と、Undo・previewDirty 判定の起点に使う。
        private SerializedObject _windowSerializedObject;
        private SerializedProperty _sessionProperty;
        private SerializedProperty _zonesProperty;

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
            if (window.position.width < 800 || window.position.height < 800)
                window.position = new Rect(window.position.x, window.position.y, 800, 800);
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
            _windowSerializedObject = new SerializedObject(this);
            _sessionProperty = _windowSerializedObject.FindProperty(nameof(_session));
            _zonesProperty = _sessionProperty?.FindPropertyRelative(nameof(IrocaSessionState.zones));
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

        private ColorZone FindZoneById(string id)
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
        internal void ApplyMaskFromPreset(IrocaPresetData data) => _maskView?.ApplyFromPreset(data);
        internal void WriteMaskToPreset(IrocaPresetData data) => _maskView?.WriteToPreset(data);
        internal void ResetActiveMaskTarget() => _maskView?.ResetActiveTarget();
        internal float ExportSectionHeight => _exportView.GetSectionHeight();

        // ── プレビュー / エクスポート用フォワーダ ──
        internal MaskSnapshot BuildMaskSnapshot() => _maskView?.BuildSnapshot();

        // bool[] バッファを _session.maskState に書き戻す（Undo 登録前のスナップショット確定用）。
        // プリセット読込の Undo 登録（PresetsView.LoadPreset）から呼ばれる。
        internal void SyncMaskBuffersToState() => _maskView?.SyncBuffersToState();
    }
}
