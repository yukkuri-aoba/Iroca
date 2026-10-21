// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスク対象選択・ブラシ入力・オーバーレイ生成(MaskPaintView.Overlay.cs)・bool[] バッファと
    /// _session.maskState の同期を担当する。
    /// ゾーン本体の編集 UI、プレビュー生成本体、プリセット一覧、エクスポートは扱わない。
    /// </summary>
    [System.Serializable]
    internal partial class MaskPaintView
    {
        // どのマスクを編集対象にするか。-1 = 共通マスク、0 以上 = zones[index]。
        public int activeMaskTarget = -1;
        public int brushSize = 8;
        public bool brushEraseMode; // false = ペイント、true = 消去(いずれも編集中レイヤーに対して)
        // 編集対象レイヤー。false = 除外マスク、true = 含めるマスク。
        // 含めるはゾーン単位のみ(共通マスクには存在しない)。共通ターゲット選択時は
        // Draw 側で false へ強制リセットされる。
        public bool editIncludeLayer;
        public bool maskFoldout = true;

        // 共通マスク（フル解像度、true = 除外）。全ゾーンに適用される。
        [System.NonSerialized] public bool[] exclusionMask;
        [System.NonSerialized] public int maskWidth, maskHeight;

        // ゾーン別マスク: key = ColorZone.id。配列サイズは maskWidth * maskHeight。
        // 値が null のエントリは持たない（存在しない = 全ピクセル非除外扱い）。
        [System.NonSerialized] public Dictionary<string, bool[]> zoneMasks = new Dictionary<string, bool[]>();

        // ゾーン別「含める」マスク: key = ColorZone.id、true = 強制的に色替えへ含める。
        // 除外と同じキャンバス寸法(maskWidth × maskHeight)。null 値のエントリは持たない。
        [System.NonSerialized] public Dictionary<string, bool[]> zoneIncludeMasks = new Dictionary<string, bool[]>();

        // マスクオーバーレイ（テクスチャは都度再構築するのでシリアライズ不要）
        [System.NonSerialized] public Texture2D maskOverlayTexture;
        [System.NonSerialized] public Texture2D zoneMaskOverlayTexture;
        [System.NonSerialized] private bool _maskDirty = true;
        public bool maskDirty
        {
            get => _maskDirty;
            set
            {
                _maskDirty = value;
                // マスクが変わったら、別ウィンドウ(プレビュー・マスク編集)にも描き直しを知らせる(IrocaWindow.DrawStamp)。
                if (value) _host?.BumpUiState();
            }
        }

        // 直近に構築を開始したオーバーレイの寸法(スケジュール時に更新)。テクスチャ実寸では
        // なくこれと比べて再構築要否を判断する: マスクが空だとテクスチャは解放されるので
        // 実寸比較では毎フレーム不一致になり、適用前に比べると完了までスケジュールし直し続ける。
        [System.NonSerialized] public int overlayBuiltW, overlayBuiltH;

        // ペイント中のオーバーレイ直接書き込み管理。
        // _overlayDirectPendingApply: SetPixels32 済みで Apply 待ち(ドラッグイベント単位でまとめる)。
        // _strokeHadDirectOverlayWrite: このストロークで直接書き込みに成功したか。成功後は
        // 進行中の非同期再構築結果を適用しない(clone 時点より新しいスタンプが一瞬消えるため)。
        [System.NonSerialized] private bool _overlayDirectPendingApply;
        [System.NonSerialized] private bool _strokeHadDirectOverlayWrite;

        [System.NonSerialized] public bool isPainting;
        [System.NonSerialized] public Vector2 lastPaintUV = -Vector2.one;
        // ペイント中の RebuildMaskOverlay 間引き用タイムスタンプ（PreviewView から参照）。
        [System.NonSerialized] public double lastOverlayRebuildTime;

        // ストローク中フラグ（同一ストロークで二重 Undo 登録しないため）
        [System.NonSerialized] public bool _maskStrokeStarted;

        // 現在のテクスチャの MaskCache ファイルが「存在するのに読めなかった」フラグ。
        // true の間は空保存での削除・無退避の上書きを抑止する（一時的な読込失敗が
        // データ恒久消失に化けるのを防ぐ）。有効な内容を保存できたら解除。
        [System.NonSerialized] private bool _maskLoadFailed;

        public const string CommonMaskKey = "__common__";

        [System.NonSerialized] public bool maskPaintActive;

        [System.NonSerialized] private IrocaWindow _host;

        // AI マスク提案(Sentis 統合が存在するときのみ生成される。不在なら常に null = 機能 OFF)
        [System.NonSerialized] private MaskSuggestController _suggestController;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
        }

        /// <summary>AI マスク提案コントローラ(遅延生成)。Sentis 不在時は null。</summary>
        public MaskSuggestController SuggestController
        {
            get
            {
                if (_suggestController == null && MaskSuggestBridge.Available && _host != null)
                {
                    _suggestController = new MaskSuggestController();
                    _suggestController.Initialize(_host, this);
                }
                return _suggestController;
            }
        }

        /// <summary>生成済みのときだけ返す(参照しても生成しない)。</summary>
        public MaskSuggestController SuggestControllerIfCreated => _suggestController;

        // 右クリックメニューの「パーツ」(メッシュの UV の島)操作(遅延生成)。
        [System.NonSerialized] private MeshPartController _meshParts;

        /// <summary>メッシュの UV の島を使う操作のコントローラ(遅延生成)。</summary>
        public MeshPartController MeshParts
        {
            get
            {
                if (_meshParts == null && _host != null)
                {
                    _meshParts = new MeshPartController();
                    _meshParts.Initialize(_host, this);
                }
                return _meshParts;
            }
        }

        public void Draw()
        {
            maskFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(maskFoldout, Localization.ExclusionMask);
            if (!maskFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EnforceLayerConsistency();

            // マスク編集の操作と状態(対象ゾーン・種類・ツール・ブラシ・AI 提案)は
            // すべて MaskBrushWindow へ集約した。編集中に見ているのは「編集ウィンドウ +
            // プレビュー」であり、対象ゾーンだけがここ(左カラム・要スクロール)に残っていると、
            // 塗る前に別ウィンドウへ視線を往復することになる(2026-08-22 のユーザー指摘)。
            // ここに残すのは入口と、いま何を編集する状態かの読み取り専用サマリだけ。
            EditorGUILayout.LabelField(
                new GUIContent(string.Format(Localization.MaskCurrentTargetFormat,
                                             ActiveTargetName(), ActiveLayerName()),
                               Localization.MaskCurrentTargetTooltip),
                EditorStyles.miniLabel);

            var prevBg = GUI.backgroundColor;
            if (maskPaintActive) GUI.backgroundColor = IrocaColors.ActiveMaskTarget;
            if (GUILayout.Button(new GUIContent(Localization.MaskEditOpen, Localization.MaskEditOpenTooltip)))
            {
                // 初回は「押せば塗れる」ようブラシを ON にする。
                if (!maskPaintActive) ActivateBrush(erase: false);
                MaskBrushWindow.Open(_host);
            }
            GUI.backgroundColor = prevBg;

            // 右クリックの「パーツ」操作と UV の島の表示に使うメッシュ。パレットを開かなくても
            // 見つかったか・どれを使っているかが分かり、ここで差し替えられる(2026-09-30)。
            EditorGUILayout.Space(2);
            MeshPartSection.Draw(_host, this);

            // AI の「一度きりの有効化」(Sentis 導入・モデル取得)はウィンドウ上部のバナーへ移した
            // (MaskSuggestSection.DrawSetupBanner)。ここに置くと、マスク欄を畳んでいる間は
            // 見えず、機能の存在にも準備が要ることにも気づけなかった(2026-09-11 の UX 見直し)。

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        /// <summary>いま編集対象になっているマスクのゾーン名(共通マスクなら共通の表示名)。</summary>
        public string ActiveTargetName()
        {
            var zones = _host.Session?.zones;
            if (IsCommonTarget(zones))
                return Localization.MaskTargetCommon;
            string raw = zones[activeMaskTarget].name;
            return string.IsNullOrEmpty(raw) ? Localization.UnnamedZone : raw;
        }

        /// <summary>いま編集対象になっているマスクの種類の表示名(除外 / 含める)。</summary>
        public string ActiveLayerName()
            => editIncludeLayer ? Localization.Include : Localization.Exclude;

        /// <summary>ブラシペイントモードを ON にする。</summary>
        public void ActivateBrush(bool erase)
        {
            maskPaintActive = true;
            brushEraseMode = erase;
        }

        /// <summary>ブラシペイントモードを OFF にする。</summary>
        public void DeactivateBrush()
        {
            maskPaintActive = false;
        }

        /// <summary>
        /// 編集対象が共通マスクか。-1 と、ゾーン削除などで範囲外になった番号はどちらも共通として扱う。
        /// zones は呼び出し側で渡す(Session が null のとき共通扱いにするか例外にするかは呼び出し側で決まる)。
        /// </summary>
        private bool IsCommonTarget(List<ColorZone> zones)
            => activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count;

        /// <summary>
        /// レイヤー選択の整合を保つ: 含めるレイヤーはゾーン単位のみなので、編集対象が
        /// 共通マスクのときは除外レイヤーへ戻す(ゾーン削除・対象切替の過渡で不整合になり得る)。
        /// </summary>
        private void EnforceLayerConsistency()
        {
            if (!editIncludeLayer) return;
            var zones = _host.Session?.zones;
            if (IsCommonTarget(zones))
            {
                editIncludeLayer = false;
                maskDirty = true;
            }
        }

        /// <summary>
        /// マスク編集ウィンドウ（MaskBrushWindow）の中身。**マスク編集の操作と状態はここに全部ある**
        /// のが設計上の約束で、メインウィンドウ側には入口と読み取り専用サマリしか置かない。
        ///
        /// 上から「対象(どのゾーンの) → 種類(除外/含める) → ツール(塗る/消す) → ブラシサイズ →
        /// AI 提案(プレビューの右クリックメニューで使う)の粒度と推論の状態 → 取り消し/クリア」の順。
        /// ユーザーが決める順序どおりに並べてある。
        /// </summary>
        public void DrawBrushPalette()
        {
            EnforceLayerConsistency();

            // 1. 対象ゾーン。ここに無いと「塗る直前にメインウィンドウへ視線を往復する」ことになる
            //    (元はメインの左カラムにあり、スクロールしないと見えなかった)。
            DrawMaskTargetSelector();

            bool stateChanged = false;
            var prevBg = GUI.backgroundColor;

            // 2. マスクの種類(除外/含める)。
            DrawLayerKindSelector();

            // 3. ツール選択: 塗る / 消す(上で選んだ対象・種類に対して作用する)。
            //    AI 提案とメッシュのパーツ操作はモードを持たず、プレビューの右クリックメニューから
            //    その場で選ぶ(2026-09-30。以前はここに「AI 提案」ツールがあり、よく使う操作が
            //    このパレットを開いてモードに入らないと使えなかった)。
            var ctl = SuggestController;
            bool paintActive = maskPaintActive && !brushEraseMode;
            bool eraseActive = maskPaintActive && brushEraseMode;
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = paintActive ? IrocaColors.ExcludeButton : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.BrushPaint, Localization.BrushPaintTooltip),
                    EditorStyles.miniButtonLeft))
            {
                if (paintActive) DeactivateBrush();
                else ActivateBrush(erase: false);
                stateChanged = true;
            }
            GUI.backgroundColor = eraseActive ? IrocaColors.IncludeButton : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.BrushErase, Localization.BrushEraseTooltip),
                    EditorStyles.miniButtonRight))
            {
                if (eraseActive) DeactivateBrush();
                else ActivateBrush(erase: true);
                stateChanged = true;
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            // 4. ブラシサイズ。
            brushSize = EditorGUILayout.IntSlider(
                new GUIContent(Localization.BrushSize, Localization.BrushSizeTooltip),
                brushSize, 1, 64);

            // 5. AI 提案(右クリックメニューから使う)の粒度と推論の状態。Sentis が載っているときだけ。
            if (ctl != null)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(
                    new GUIContent(Localization.AiSuggestSectionHeader, Localization.AiSuggestSectionHeaderTooltip),
                    EditorStyles.boldLabel);
                MaskSuggestSection.DrawToolControls(_host, this);
            }

            EditorGUILayout.Space(2);

            // 6. 取り消し / クリア。
            // Unity 標準 Undo に統合済みのため、専用ボタンは PerformUndo の薄いショートカットとして残す。
            // ★ラベルは「マスクを元に戻す」にしないこと★ — PerformUndo の対象は直前の操作であり、
            // マスク編集とは限らない。マスク限定の Undo を名乗ると、スライダー変更やシーン編集が
            // 巻き戻ったときに破壊的なサプライズになる（Localization.UndoMask のコメント参照）。
            if (GUILayout.Button(new GUIContent(Localization.UndoMask, Localization.UndoMaskTooltip)))
            {
                Undo.PerformUndo();
            }

            // クリアは「対象 × 種類」で決まる 1 枚だけを消す操作なので、その 2 つを選ぶ
            // プルダウン/ボタンと同じウィンドウに置く(元はメインウィンドウ側にあり、
            // 何を消すのかがその場で確認できなかった)。
            if (GUILayout.Button(new GUIContent(
                    string.Format(Localization.ClearMaskTargetFormat,
                                  ActiveTargetName(), ActiveLayerName()),
                    Localization.ClearMaskTooltip)))
            {
                // bool[] バッファを _session.maskState に同期してから Undo 登録、クリア後に再同期。
                SyncBuffersToState();
                Undo.RegisterCompleteObjectUndo(_host, "Clear Mask");
                ClearActiveMask();
                SyncBuffersToState();
                maskDirty = true;
                _host.MarkPreviewDirty();
            }

            EditorGUILayout.HelpBox(
                (maskPaintActive ? Localization.MaskHint : Localization.MaskHintPaintOff)
                + "\n" + Localization.MaskHintContextMenu,
                MessageType.Info);

            if (stateChanged)
                _host.RequestRepaint();
        }

        /// <summary>
        /// 「マスクの種類」(除外/含める)の切り替え行。ブラシパレット(DrawBrushPalette)から呼ぶ。
        /// 含めるはゾーン単位のみなので、共通マスクが編集対象のときは無効化する。
        /// </summary>
        public void DrawLayerKindSelector()
        {
            var zones = _host.Session?.zones;
            bool commonTarget = IsCommonTarget(zones);
            EnforceLayerConsistency();
            var prevBg = GUI.backgroundColor;
            EditorGUILayout.LabelField(
                new GUIContent(Localization.MaskLayerKind, Localization.MaskLayerKindTooltip),
                EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = !editIncludeLayer ? IrocaColors.ExcludeButton : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.Exclude, Localization.ExcludeTooltip),
                    EditorStyles.miniButtonLeft)
                && editIncludeLayer)
            {
                editIncludeLayer = false;
                maskDirty = true;
                _host.RequestRepaint();
            }
            using (new EditorGUI.DisabledScope(commonTarget))
            {
                GUI.backgroundColor = editIncludeLayer ? IrocaColors.IncludeButton : Color.white;
                if (GUILayout.Button(
                        new GUIContent(Localization.Include, Localization.IncludeLayerTooltip),
                        EditorStyles.miniButtonRight)
                    && !editIncludeLayer)
                {
                    editIncludeLayer = true;
                    maskDirty = true;
                    _host.RequestRepaint();
                }
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();
        }

        // マスク対象プルダウンの GUIContent[] は毎フレーム再生成されアロケーションを生むため、
        // ゾーン数・各ゾーン名・言語が変わったときだけ作り直してキャッシュする。
        [System.NonSerialized] private GUIContent[] _targetOptionsCache;
        [System.NonSerialized] private string[] _targetOptionsNames;
        [System.NonSerialized] private LanguageMode _targetOptionsLang = (LanguageMode)(-1);

        private GUIContent[] GetMaskTargetOptions(List<ColorZone> zones, int zoneCount)
        {
            bool rebuild = _targetOptionsCache == null
                || _targetOptionsCache.Length != zoneCount + 1
                || _targetOptionsNames == null
                || _targetOptionsLang != Localization.CurrentLanguage;
            if (!rebuild)
            {
                for (int i = 0; i < zoneCount; i++)
                {
                    if (_targetOptionsNames[i] != (zones[i].name ?? "")) { rebuild = true; break; }
                }
            }
            if (rebuild)
            {
                _targetOptionsCache = new GUIContent[zoneCount + 1];
                _targetOptionsNames = new string[zoneCount];
                _targetOptionsLang = Localization.CurrentLanguage;
                _targetOptionsCache[0] = new GUIContent(Localization.MaskTargetCommon);
                for (int i = 0; i < zoneCount; i++)
                {
                    string raw = zones[i].name ?? "";
                    _targetOptionsNames[i] = raw;
                    _targetOptionsCache[i + 1] = new GUIContent(string.IsNullOrEmpty(raw) ? Localization.UnnamedZone : raw);
                }
            }
            return _targetOptionsCache;
        }

        /// <summary>
        /// 編集対象プルダウン。-1 = 共通マスク、0 以上 = zones[index] のマスク。
        /// </summary>
        private void DrawMaskTargetSelector()
        {
            _host.EnsureAllZoneIds();
            var zones = _host.Session.zones;

            int zoneCount = zones != null ? zones.Count : 0;
            var options = GetMaskTargetOptions(zones, zoneCount);

            int selectedIdx = Mathf.Clamp(activeMaskTarget + 1, 0, options.Length - 1);
            int next = EditorGUILayout.Popup(
                new GUIContent(Localization.MaskTarget, Localization.MaskTargetTooltip),
                selectedIdx, options);
            if (next != selectedIdx)
            {
                activeMaskTarget = next - 1;
                maskDirty = true;
                _host.RequestRepaint();
            }
        }

        /// <summary>
        /// マスクの座標系（maskWidth/maskHeight）を sourceTexture に揃える。
        /// 解像度が変わったときは既存マスクを新しい座標系へ最近傍でリスケールする
        /// （破棄すると import Max Size を変えただけで描いたマスクが消え、
        /// その状態が次回保存で永続化されてしまう）。
        /// 共通マスク <see cref="exclusionMask"/> の確保は行わない（アクティブターゲットが
        /// ゾーンだけの場合に zone-only mask を巻き込んで消さないため）。
        /// </summary>
        public void EnsureMasks()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null) return;
            CanvasSizeFor(sourceTexture, out int w, out int h);

            if (maskWidth != w || maskHeight != h)
            {
                int oldW = maskWidth, oldH = maskHeight;
                bool hadData = exclusionMask != null || zoneMasks.Count > 0 || zoneIncludeMasks.Count > 0;
                if (hadData && oldW > 0 && oldH > 0)
                {
                    exclusionMask = RescaleMask(exclusionMask, oldW, oldH, w, h);
                    RescaleLayer(zoneMasks, oldW, oldH, w, h);
                    RescaleLayer(zoneIncludeMasks, oldW, oldH, w, h);
                    Debug.Log($"[Iroca] テクスチャ解像度の変更 ({oldW}x{oldH} → {w}x{h}) に合わせてマスクをリスケールしました。");
                }
                else
                {
                    exclusionMask = null;
                    zoneMasks.Clear();
                    zoneIncludeMasks.Clear();
                }
                maskWidth = w;
                maskHeight = h;
                maskDirty = true;
            }
        }

        // マスクの土台の寸法の覚え(EnsureMasks はブラシの 1 スタンプごとにも呼ばれるので、取り込み設定を
        // 引くのはテクスチャか中身(再インポート)か取り込み後の寸法が変わったときだけにする)。
        [System.NonSerialized] private Texture2D _canvasFor;
        [System.NonSerialized] private Hash128 _canvasHash;
        [System.NonSerialized] private int _canvasImportW, _canvasImportH, _canvasW, _canvasH;

        /// <summary>
        /// マスクの土台の寸法 = 元画像(ファイル)の寸法(<see cref="MaskRegionEdit.CanvasSize"/>)。処理と書き出しは
        /// 元画像の解像度で行うので、取り込みの最大サイズで縮んだ寸法(既定 2048)に合わせると、4K の元画像では
        /// 塗った境目が書き出しで 2×2 画素単位に粗くなっていた。元画像の寸法が取れないテクスチャ(生成物など)は
        /// 取り込み後の寸法。
        /// </summary>
        private void CanvasSizeFor(Texture2D tex, out int w, out int h)
        {
            var hash = tex.imageContentsHash;
            if (_canvasFor != tex || _canvasHash != hash
                || _canvasImportW != tex.width || _canvasImportH != tex.height)
            {
                int fileW = 0, fileH = 0;
                string path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter ti)
                    ti.GetSourceTextureWidthAndHeight(out fileW, out fileH);
                MaskRegionEdit.CanvasSize(fileW, fileH, tex.width, tex.height, out _canvasW, out _canvasH);
                _canvasFor = tex;
                _canvasHash = hash;
                _canvasImportW = tex.width;
                _canvasImportH = tex.height;
            }
            w = _canvasW;
            h = _canvasH;
        }

        /// <summary>
        /// 旧解像度の bool マスクを新解像度へ最近傍でリスケールする。
        /// サンプリング式は処理側（PixelProcessor の <c>mx = x * maskW / texW</c>）と同じ
        /// 整数切り捨てで、適用結果の対応関係を保つ。<c>null</c> や不整合サイズは <c>null</c> を返す。
        /// </summary>
        private static bool[] RescaleMask(bool[] src, int oldW, int oldH, int newW, int newH)
        {
            if (src == null || src.Length != oldW * oldH || newW <= 0 || newH <= 0) return null;
            var dst = new bool[newW * newH];
            for (int y = 0; y < newH; y++)
            {
                int oy = (int)((long)y * oldH / newH);
                int rowOld = oy * oldW;
                int rowNew = y * newW;
                for (int x = 0; x < newW; x++)
                {
                    int ox = (int)((long)x * oldW / newW);
                    dst[rowNew + x] = src[rowOld + ox];
                }
            }
            return dst;
        }

        /// <summary>
        /// ゾーン別マスクの辞書（<see cref="zoneMasks"/> = 除外 / <see cref="zoneIncludeMasks"/> = 含める）の
        /// 全エントリを <see cref="RescaleMask"/> で新解像度へ揃える。リスケールできないエントリは削除する。
        /// </summary>
        private static void RescaleLayer(Dictionary<string, bool[]> layer, int oldW, int oldH, int w, int h)
        {
            var keys = new List<string>(layer.Keys);
            foreach (var key in keys)
            {
                var scaled = RescaleMask(layer[key], oldW, oldH, w, h);
                if (scaled != null) layer[key] = scaled;
                else layer.Remove(key); // 不変条件: null 値のエントリは持たない
            }
        }

        /// <summary>
        /// 共通マスク bool[] を遅延確保する。<see cref="EnsureMasks"/> は座標系のみを扱い、
        /// このメソッドは「実際に共通マスクへ書き込む直前」にだけ呼ぶ。
        /// </summary>
        private bool[] EnsureCommonMask()
        {
            EnsureMasks();
            if (maskWidth <= 0 || maskHeight <= 0) return null;
            int len = maskWidth * maskHeight;
            if (exclusionMask == null || exclusionMask.Length != len)
                exclusionMask = new bool[len];
            return exclusionMask;
        }

        /// <summary>
        /// 指定ゾーンの、<paramref name="layer"/>（<see cref="zoneMasks"/> = 除外 /
        /// <see cref="zoneIncludeMasks"/> = 含める）のマスクを確保（存在しなければ新規作成）して返す。
        /// </summary>
        private bool[] EnsureZoneLayer(Dictionary<string, bool[]> layer, string zoneId)
        {
            EnsureMasks();
            if (string.IsNullOrEmpty(zoneId)) return null;
            if (maskWidth <= 0 || maskHeight <= 0) return null;
            int len = maskWidth * maskHeight;
            if (!layer.TryGetValue(zoneId, out var m) || m == null || m.Length != len)
            {
                m = new bool[len];
                layer[zoneId] = m;
            }
            return m;
        }

        /// <summary>
        /// 現在のアクティブターゲット（共通 or ゾーン）× レイヤー(除外 or 含める)の
        /// マスク配列を返す。必要なら確保する。
        /// 共通マスクは実際にペイント先になった時だけ確保される（zone-only mask の保全のため）。
        /// 共通ターゲット × 含めるレイヤーは存在しない組み合わせで null を返す
        /// (UI 側は EnforceLayerConsistency がこの状態を解消する。データ経路の防御)。
        /// </summary>
        public bool[] GetActiveMaskArray()
        {
            var zones = _host.Session.zones;
            if (IsCommonTarget(zones))
                return editIncludeLayer ? null : EnsureCommonMask();

            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            return editIncludeLayer ? EnsureZoneLayer(zoneIncludeMasks, zone.id) : EnsureZoneLayer(zoneMasks, zone.id);
        }

        /// <summary>
        /// 右クリックメニューの宛先(どのゾーンに × 何をするか)。zoneId = null は全ゾーン共通で、
        /// 共通にできるのは「ここは塗らない」だけ(含めるはゾーン単位のみ)。
        /// ゾーンは番号でなく ID で持つ(AI の推論待ちの間に並び替え・削除があっても取り違えない)。
        /// </summary>
        internal readonly struct MaskDestination
        {
            public readonly string zoneId;
            public readonly MaskRegionOp op;
            public MaskDestination(string zoneId, MaskRegionOp op)
            {
                this.zoneId = zoneId;
                this.op = op;
            }
        }

        /// <summary>いまの編集対象(パレットで選んだゾーン。未選択は共通)。null = 共通。</summary>
        public string ActiveTargetZoneId()
        {
            var zones = _host.Session?.zones;
            if (IsCommonTarget(zones)) return null;
            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            return zone.id;
        }

        private ColorZone FindZone(string zoneId)
            => zoneId == null ? null : _host.Session?.zones?.Find(z => z != null && z.id == zoneId);

        /// <summary>
        /// 範囲(マスクと同じ寸法・下原点)に宛先の操作を当て、1 つの Undo ストロークにする
        /// (規則は <see cref="MaskRegionEdit"/>)。AI 提案と右クリックメニューの共通の出口。
        /// 宛先のゾーンが消えた・共通に塗らない以外を当てようとした・寸法違いなら false。
        /// </summary>
        public bool ApplyRegion(bool[] region, MaskDestination d, out MaskRegionEditResult result)
        {
            result = default;
            EnsureMasks();
            int len = maskWidth * maskHeight;
            if (region == null || len <= 0 || region.Length != len) return false;

            bool[] exclude, include = null, common = null;
            if (d.zoneId == null)
            {
                if (d.op != MaskRegionOp.DontPaintHere) return false;
                exclude = EnsureCommonMask();
            }
            else
            {
                if (FindZone(d.zoneId) == null) return false;
                common = exclusionMask;
                if (d.op == MaskRegionOp.PaintHere)
                {
                    include = EnsureZoneLayer(zoneIncludeMasks, d.zoneId);
                    zoneMasks.TryGetValue(d.zoneId, out exclude);
                }
                else
                {
                    exclude = EnsureZoneLayer(zoneMasks, d.zoneId);
                    zoneIncludeMasks.TryGetValue(d.zoneId, out include);
                }
            }
            if (exclude != null && exclude.Length != len) return false;
            if (include != null && include.Length != len) return false;
            if (common != null && common.Length != len) common = null;

            BeginStroke();
            result = MaskRegionEdit.Apply(d.op, region, exclude, include, common);
            EndStroke();
            maskDirty = true;
            _host.MarkPreviewDirtyFullRefine();
            _host.RequestRepaint();
            return true;
        }

        /// <summary>
        /// <see cref="ApplyRegion"/> の結果を知らせる文。何をしたか(Ctrl+Z で戻せる)に加えて、
        /// 指示どおりに塗られない理由(共通の「塗らない」との重なり・許容範囲 0)があれば添える。
        /// </summary>
        public string DescribeRegionEdit(MaskDestination d, bool ok, MaskRegionEditResult r)
        {
            if (!ok) return Localization.NotifyMaskNotAdded;
            if (r.changed == 0) return Localization.NotifyMaskNoChange;
            var zone = FindZone(d.zoneId);
            string name = zone == null ? null : (string.IsNullOrEmpty(zone.name) ? Localization.UnnamedZone : zone.name);
            string msg = d.op switch
            {
                MaskRegionOp.PaintHere => string.Format(Localization.NotifyPaintHereFormat, name),
                MaskRegionOp.OnlyThisPart => string.Format(Localization.NotifyOnlyThisPartFormat, name),
                _ => zone == null ? Localization.NotifyDontPaintHereCommon
                                  : string.Format(Localization.NotifyDontPaintHereFormat, name),
            };
            if (r.commonOverlap > 0) msg += "\n" + Localization.NotifyCommonOverlap;
            // この部分だけ: 範囲の中は色で選ぶので、許容範囲 0 では中も塗られない(旧来の「許容 0 → 含める」の手順の名残)。
            if (d.op == MaskRegionOp.OnlyThisPart && zone != null && zone.tolerance <= 0f)
                msg += "\n" + Localization.NotifyOnlyThisZeroTolerance;
            // 自動調整の前は、明るい所で取った色だと許容 0.20 では部分の中を取りこぼすことがある
            // (2026-10-06 の計測で範囲内 IoU 0.14、自動調整後は 1.00)。自動調整は除外を見るので、
            // 押すと部分の中だけを見て合わせ直す。
            else if (d.op == MaskRegionOp.OnlyThisPart && zone != null && _host.IsUntunedNewZone(zone))
                msg += "\n" + Localization.NotifyOnlyThisAutoTuneHint;
            return msg;
        }

        private void ClearActiveMask()
        {
            var zones = _host.Session.zones;
            if (activeMaskTarget < 0)
            {
                // 共通ターゲットに含めるレイヤーは存在しない(EnforceLayerConsistency 済み)。
                if (!editIncludeLayer) exclusionMask = null;
            }
            else if (zones != null && activeMaskTarget < zones.Count)
            {
                var zone = zones[activeMaskTarget];
                zone.EnsureId();
                if (editIncludeLayer) zoneIncludeMasks.Remove(zone.id);
                else zoneMasks.Remove(zone.id);
            }
        }

        /// <summary>
        /// 指定インデックスのゾーンが削除されるタイミングで、紐付くマスクを破棄する。
        /// </summary>
        public void OnZoneAboutToBeRemoved(int index)
        {
            var zones = _host.Session.zones;
            if (zones == null || index < 0 || index >= zones.Count) return;
            string id = zones[index].id;
            if (!string.IsNullOrEmpty(id))
            {
                zoneMasks.Remove(id);
                zoneIncludeMasks.Remove(id);
            }

            // 編集対象(=いま直しているゾーン)を消したら、残ったゾーンのうち同じ位置(末尾なら 1 つ上)へ移す。
            // 共通へ戻すと、右クリックの「ここも塗る」などがゾーン無しの扱いになってしまう。
            int remaining = zones.Count - 1;
            if (activeMaskTarget == index) activeMaskTarget = remaining > 0 ? Mathf.Min(index, remaining - 1) : -1;
            else if (activeMaskTarget > index) activeMaskTarget--;

            maskDirty = true;
        }

        /// <summary>
        /// ゾーンを from から to(remove 後の挿入 index)へ移動した際に、編集中マスクターゲット
        /// (index 参照)を追従させる。マスク本体は zone.id キーで管理されるため移動は不要。
        /// </summary>
        public void OnZoneReordered(int from, int to)
        {
            if (activeMaskTarget >= 0)
            {
                int a = activeMaskTarget;
                if (a == from)
                {
                    a = to;
                }
                else
                {
                    if (a > from) a--;
                    if (a >= to) a++;
                }
                activeMaskTarget = a;
            }
            maskDirty = true;
        }

        /// <summary>
        /// ブラシの半径(マスクの画素単位)。brushSize は全体表示のときのプレビューの格子セル単位の半径
        /// (塗られる円の直径は 2×サイズ+1 セル)で、拡大するとその倍率だけ小さく塗る: 画面上のブラシの
        /// 大きさは拡大しても変わらず、拡大すれば細かく塗れる(4K の元画像でも、拡大して最小のブラシなら 1 画素)。
        /// gridW は表示プレビューの画素格子の幅(previewTexture の実寸)、previewZoom は全体表示を 1 とする拡大率。
        /// </summary>
        public float BrushRadiusPx(int gridW, float previewZoom)
        {
            if (maskWidth <= 0) return 0f;
            float cellPx = gridW > 0 ? maskWidth / (float)gridW : 1f;
            return (Mathf.Max(1, brushSize) + 0.5f) * cellPx / Mathf.Max(1f, previewZoom);
        }

        /// <summary>
        /// ブラシ 1 スタンプ分を塗る。uvPos はテクスチャの UV、radiusPx はマスクの画素単位の半径
        /// (<see cref="BrushRadiusPx"/>)。円の中のマスク画素を塗る(<see cref="MaskRegionEdit.StampDisk"/>)。
        ///
        /// 以前は表示プレビューの格子セル(4K で約 11 画素四方)単位で塗っていた(縮小表示と書き出しの見た目を
        /// 一致させるため)。マスクは元画像の解像度で持ち、拡大表示ではその範囲を等倍で重ねて見せるので、
        /// 画素単位で塗っても拡大して見れば塗ったとおりに見える。全体表示のプレビュー(縮小)は、ブロック先頭の
        /// 1 画素を読むので、円の縁が 1 セル程度ずれて見えることがある。
        /// </summary>
        public void PaintMask(Vector2 uvPos, float radiusPx)
        {
            bool[] target = GetActiveMaskArray();
            if (target == null) return;
            if (maskWidth <= 0 || maskHeight <= 0) return;

            float cx = uvPos.x * maskWidth, cy = uvPos.y * maskHeight;
            bool value = !brushEraseMode;
            // 後から塗ったほうが勝つ: ゾーンの「含める」を塗ったら、ブラシの下の同じゾーンの「除外」を消す(逆も同じ)。
            // 合成は「除外が勝つ」ので、消さないと含めるを塗っても効かず、重ね表示も 2 色が混ざる(2026-10-06)。
            bool[] opposite = value ? OppositeZoneLayer() : null;
            MaskRegionEdit.StampDisk(target, opposite, maskWidth, maskHeight, cx, cy, radiusPx, value,
                                     out int x0, out int y0, out int x1, out int y1);
            if (x1 <= x0) return;

            // オーバーレイ直接書き込み: 非同期再構築のスロットル待ちとフル解像度 bool[] clone をストローク中に
            // 発生させず、塗った所をすぐ見せる。全体用のオーバーレイ(表示格子の整数倍)と、拡大表示用の
            // 等倍オーバーレイ(見えている範囲)の両方へ、同じ円を各自の解像度で書く。書けないとき(未生成)は
            // 従来どおり maskDirty を立てて非同期再構築に任せる。ストロークの終わりに正規の再構築で揃う。
            var overlayTex = ActiveOverlayTexture(out Color32 overlayColor);
            Color32 c = value ? overlayColor : default;
            bool direct = false;
            if (overlayTex != null)
            {
                float sx = overlayTex.width / (float)maskWidth, sy = overlayTex.height / (float)maskHeight;
                direct = StampOverlayDisk(overlayTex, cx * sx, cy * sy, radiusPx * sx, c);
            }
            StampDetailOverlay(cx, cy, radiusPx, c);

            if (direct)
            {
                _overlayDirectPendingApply = true;
                _strokeHadDirectOverlayWrite = true;
            }
            else
            {
                maskDirty = true;
            }
        }

        /// <summary>
        /// オーバーレイのテクスチャへ円(中心・半径はテクセル単位)を直接書く。円の中にテクセルの中心が入る所
        /// (円が小さければ中心のテクセル)を color にする。行ごとに SetPixels32。書いたら true。
        /// </summary>
        private static bool StampOverlayDisk(Texture2D tex, float cx, float cy, float r, Color32 color)
        {
            int w = tex.width, h = tex.height;
            if (w <= 0 || h <= 0) return false;
            float r2 = r * r;
            int ccx = Mathf.FloorToInt(cx), ccy = Mathf.FloorToInt(cy);
            int ry0 = Mathf.Max(0, Mathf.FloorToInt(cy - r - 0.5f));
            int ry1 = Mathf.Min(h - 1, Mathf.CeilToInt(cy + r - 0.5f));
            bool wrote = false;
            for (int y = ry0; y <= ry1; y++)
            {
                float dy = y + 0.5f - cy;
                float rem = r2 - dy * dy;
                int sx0 = int.MaxValue, sx1 = int.MinValue;
                if (rem >= 0f)
                {
                    float half = Mathf.Sqrt(rem);
                    sx0 = Mathf.CeilToInt(cx - half - 0.5f);
                    sx1 = Mathf.FloorToInt(cx + half - 0.5f);
                }
                if (y == ccy) { sx0 = Mathf.Min(sx0, ccx); sx1 = Mathf.Max(sx1, ccx); }
                sx0 = Mathf.Max(0, sx0);
                sx1 = Mathf.Min(w - 1, sx1);
                if (sx1 < sx0) continue;
                int n = sx1 - sx0 + 1;
                var row = new Color32[n];
                for (int k = 0; k < n; k++) row[k] = color;
                tex.SetPixels32(sx0, y, n, 1, row);
                wrote = true;
            }
            return wrote;
        }

        /// <summary>
        /// ブラシで塗るとき、ブラシの下で消す反対側の層(編集中のゾーンの、含めるなら除外・除外なら含める)。
        /// 無ければ null(確保はしない)。共通マスクを塗るときも null: 共通の除外は「どのゾーンでも塗らない」
        /// 指定で合成でもゾーンの含めるに勝つので、ゾーンの含めるは消さずに残す。
        /// 反対側を消したセルの表示は、同じゾーン用オーバーレイへの直接書き込み(新しい層の色で上書き)と、
        /// ストローク終了時の再構築(EndStroke の maskDirty)で揃う。
        /// </summary>
        private bool[] OppositeZoneLayer()
        {
            var zones = _host.Session.zones;
            if (IsCommonTarget(zones)) return null;
            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            var layers = editIncludeLayer ? zoneMasks : zoneIncludeMasks;
            return layers.TryGetValue(zone.id, out var m) && m != null && m.Length == maskWidth * maskHeight
                ? m : null;
        }

        /// <summary>
        /// ペイントストローク開始時に呼び、ストローク前の状態を Unity Undo に登録する。
        /// 同一ストローク内で複数回呼ばれても _maskStrokeStarted で抑止される。
        /// </summary>
        public void BeginStroke()
        {
            if (_maskStrokeStarted) return;
            _strokeHadDirectOverlayWrite = false;
            // bool[] バッファの内容を _session.maskState に書き戻してから Undo 登録すれば、
            // 戻し操作でストローク開始前の状態に確実に復元できる。
            SyncBuffersToState();
            Undo.RegisterCompleteObjectUndo(_host, "Paint Mask Stroke");
            _maskStrokeStarted = true;
        }

        /// <summary>
        /// ペイントストローク終了時に呼び、ストローク後の bool[] を _session.maskState へ反映する。
        /// 次回の Undo でストローク開始前へ戻すための「変更後の状態」が確定する。
        /// </summary>
        public void EndStroke()
        {
            if (!_maskStrokeStarted) return;
            SyncBuffersToState();
            _maskStrokeStarted = false;
            // ストローク中の直接書き込みは表示テクスチャのみの更新なので、確定時に一度だけ
            // 正規の再構築を予約して収束させる(保留した非同期結果や対象切替の過渡も含む)。
            maskDirty = true;
        }

        /// <summary>
        /// 現在のマスク状態をスナップショット化する（deep clone）。
        /// </summary>
        public MaskSnapshot BuildSnapshot()
        {
            // bool[] を 1bit/画素の ulong[] にパックする(従来の deep clone は 4K で 16.7MB/枚、
            // プレビュー再生成・ペイントのたびに発生し GC を圧迫していた)。パックは clone と同じ
            // O(N) だがアロケーションが 1/8 になる。作業用マスク(exclusionMask/zoneMasks)は bool[] のまま。
            // 組み立て規則は Core の MaskSnapshot.FromBuffers が唯一の正(保存済みマスクから
            // 作り直す非破壊ビルドと同じ規則を通す)。
            return MaskSnapshot.FromBuffers(maskWidth, maskHeight,
                exclusionMask, zoneMasks, zoneIncludeMasks);
        }

        /// <summary>
        /// 編集対象(=いま直しているゾーン)を既定へ戻す。ゾーンがあれば先頭のゾーン、無ければ共通。
        /// 既定をゾーンにするのは、右クリックの「ここも塗る / この部分だけ塗る」とブラシの「含める」が
        /// ゾーン単位でしか使えず、共通のままだと作った直後に選べない項目が出るため(2026-10-06)。
        /// 共通はブラシのパレットや右クリックの「直すゾーンを変える」で明示的に選べる。
        /// </summary>
        public void ResetActiveTarget()
        {
            var zones = _host?.Session?.zones;
            activeMaskTarget = zones != null && zones.Count > 0 ? 0 : -1;
            EnforceLayerConsistency();
            maskDirty = true;
        }

        /// <summary>
        /// オーバーレイテクスチャと進行中のオーバーレイ生成ジョブを破棄する。
        /// </summary>
        public void ReleaseOverlayTextures()
        {
            SuspendTransientState();
            _overlayJob.Dispose();
            _detailOverlayJob.Dispose();
            // AI 提案の進行中推論を破棄し、**静的サービスへの購読も解除する**(ウィンドウ破棄時)。
            // 解除しないと閉じたウィンドウのコントローラがイベント経由で生き残り、再オープン後に
            // 新しいコントローラから提案を横取りする(MaskSuggestController.Shutdown 参照)。
            // 参照も落として、万一この後に触られても新しいインスタンスが作り直されるようにする。
            _suggestController?.Shutdown();
            _suggestController = null;
            // メッシュの探索結果とチャート地図(4K で数十 MB)も手放す。次の右クリックで作り直す。
            _meshParts = null;
        }

        public void SuspendTransientState()
        {
            _overlayJob.Cancel();
            _pendingOverlayResult = null;
            _detailOverlayJob.Cancel();
            _pendingDetailOverlay = null;
            _detailOverlayDirectPending = false;
            _detailApplied = default;
            TextureSlot.Release(ref detailMaskOverlayTexture);
            TextureSlot.Release(ref detailZoneOverlayTexture);
            isPainting = false;
            _maskStrokeStarted = false;
            _overlayDirectPendingApply = false;
            _strokeHadDirectOverlayWrite = false;
            lastPaintUV = -Vector2.one;
            TextureSlot.Release(ref maskOverlayTexture);
            TextureSlot.Release(ref zoneMaskOverlayTexture);
        }

    }
}
