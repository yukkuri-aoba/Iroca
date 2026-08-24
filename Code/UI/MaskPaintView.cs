// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスク対象選択・ブラシ入力・オーバーレイ生成・bool[] バッファと
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
        [System.NonSerialized] public bool maskDirty = true;

        // ペイント中のオーバーレイ直接書き込み管理。
        // _overlayDirectPendingApply: SetPixels32 済みで Apply 待ち(ドラッグイベント単位でまとめる)。
        // _strokeHadDirectOverlayWrite: このストロークで直接書き込みに成功したか。成功後は
        // 進行中の非同期再構築結果を適用しない(clone 時点より新しいスタンプが一瞬消えるため)。
        [System.NonSerialized] private bool _overlayDirectPendingApply;
        [System.NonSerialized] private bool _strokeHadDirectOverlayWrite;

        // 共通(除外)マスクのオーバーレイ色。非同期再構築と直接書き込みで共用。
        private static readonly Color32 ExcludedOverlayColor = new Color32(255, 60, 60, 80);
        // 含めるマスクのオーバーレイ色。ゾーンに依らず固定の緑(「緑 = 含める」を一意にする。
        // 除外側のゾーン色は黄金比生成で緑近傍も出るため、彩度と明度で差を付けている)。
        private static readonly Color32 IncludedOverlayColor = new Color32(50, 230, 110, 100);

        // 編集対象でないマスクのオーバーレイに掛けるアルファ係数。適用中のマスクは
        // すべて表示した上で(「見えないのに効いているマスク」を作らない)、どれを
        // 編集中かは明暗差で示す。0.4 は存在が見えて、かつ編集対象と取り違えない値。
        private const float InactiveOverlayAlphaScale = 0.4f;

        private static Color32 DimOverlayColor(Color32 c)
        {
            c.a = (byte)Mathf.Max(1, Mathf.RoundToInt(c.a * InactiveOverlayAlphaScale));
            return c;
        }

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

        /// <summary>AI 提案モードがプレビューの右クリックを受け取るべきか。</summary>
        public bool AiSuggestArmed => _suggestController != null && _suggestController.Active;

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
            if (maskPaintActive || AiSuggestArmed) GUI.backgroundColor = IrocaColors.ActiveMaskTarget;
            if (GUILayout.Button(new GUIContent(Localization.MaskEditOpen, Localization.MaskEditOpenTooltip)))
            {
                // 初回は「押せば塗れる」ようブラシを ON にする。すでにどちらかのツールを
                // 使っている最中なら触らない(AI 提案中に押しただけでブラシへ切り替わると、
                // ウィンドウを前面に出したかっただけのユーザーがモードを失う)。
                if (!maskPaintActive && !AiSuggestArmed) ActivateBrush(erase: false);
                MaskBrushWindow.Open(_host);
            }
            GUI.backgroundColor = prevBg;

            // AI 提案は「一度きりの有効化」だけをここに置く(Sentis 導入・モデル取得)。
            // 素のプロジェクトでも機能の存在に気づけるようにするため。
            MaskSuggestSection.DrawSetup(_host);

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        /// <summary>いま編集対象になっているマスクのゾーン名(共通マスクなら共通の表示名)。</summary>
        public string ActiveTargetName()
        {
            var zones = _host.Session?.zones;
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
                return Localization.MaskTargetCommon;
            string raw = zones[activeMaskTarget].name;
            return string.IsNullOrEmpty(raw) ? Localization.UnnamedZone : raw;
        }

        /// <summary>いま編集対象になっているマスクの種類の表示名(除外 / 含める)。</summary>
        public string ActiveLayerName()
            => editIncludeLayer ? Localization.Include : Localization.Exclude;

        /// <summary>ブラシペイントモードを ON にする（AI 提案とは排他）。</summary>
        public void ActivateBrush(bool erase)
        {
            maskPaintActive = true;
            brushEraseMode = erase;
            _suggestController?.SetActive(false);
        }

        /// <summary>ブラシペイントモードを OFF にする。</summary>
        public void DeactivateBrush()
        {
            maskPaintActive = false;
        }

        /// <summary>
        /// レイヤー選択の整合を保つ: 含めるレイヤーはゾーン単位のみなので、編集対象が
        /// 共通マスクのときは除外レイヤーへ戻す(ゾーン削除・対象切替の過渡で不整合になり得る)。
        /// </summary>
        private void EnforceLayerConsistency()
        {
            if (!editIncludeLayer) return;
            var zones = _host.Session?.zones;
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
            {
                editIncludeLayer = false;
                maskDirty = true;
            }
        }

        /// <summary>
        /// マスク編集ウィンドウ（MaskBrushWindow）の中身。**マスク編集の操作と状態はここに全部ある**
        /// のが設計上の約束で、メインウィンドウ側には入口と読み取り専用サマリしか置かない。
        ///
        /// 上から「対象(どのゾーンの) → 種類(除外/含める) → ツール(塗る/消す/AI 提案) →
        /// ツール別の設定 → 取り消し/クリア」の順。ユーザーが決める順序どおりに並べてある。
        /// 状態はすべて本クラスに集約されたままなので、メインウィンドウ側のハイライトや
        /// AI 提案との排他は従来ロジックがそのまま機能する。
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

            // 3. ツール選択: 塗る / 消す / AI 提案(いずれも上で選んだ対象・種類に対して作用する)。
            //    AI 提案は Sentis 統合が載っているときだけ出す(導入・モデル取得は一度きりの
            //    セットアップなのでメインウィンドウのマスク欄 = MaskSuggestSection.DrawSetup)。
            var ctl = SuggestController;
            bool aiActive = ctl != null && ctl.Active;
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
                    ctl != null ? EditorStyles.miniButtonMid : EditorStyles.miniButtonRight))
            {
                if (eraseActive) DeactivateBrush();
                else ActivateBrush(erase: true);
                stateChanged = true;
            }
            if (ctl != null)
            {
                // モデル未取得のうちは押しても何も起きないので、押せない理由ごと見せる
                // (取得ボタンをここへ複製すると、集約した意味が無くなる)。
                bool ready = MaskSuggestSection.ToolReady;
                GUI.backgroundColor = aiActive ? IrocaColors.IncludeButton : Color.white;
                using (new EditorGUI.DisabledScope(!ready))
                {
                    if (GUILayout.Button(new GUIContent(Localization.AiSuggestTool,
                            ready ? Localization.AiSuggestToolTooltip
                                  : Localization.AiSuggestToolNotReadyTooltip),
                            EditorStyles.miniButtonRight))
                    {
                        if (aiActive) ctl.SetActive(false);
                        else
                        {
                            DeactivateBrush();
                            ctl.SetActive(true);
                        }
                        stateChanged = true;
                    }
                }
            }
            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            // 4. ツール別の設定。塗る/消すならブラシサイズ、AI 提案なら粒度と推論の状態。
            //    使わない側を無効表示で残すより、選んだツールのものだけを出すほうが
            //    「いま何を調整できるのか」が一目で分かる。
            if (aiActive)
            {
                MaskSuggestSection.DrawToolControls(_host, this);
            }
            else
            {
                brushSize = EditorGUILayout.IntSlider(
                    new GUIContent(Localization.BrushSize, Localization.BrushSizeTooltip),
                    brushSize, 1, 64);
            }

            EditorGUILayout.Space(2);

            // 5. 取り消し / クリア。
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
                aiActive ? Localization.MaskHintAi
                : maskPaintActive ? Localization.MaskHint
                : Localization.MaskHintPaintOff,
                MessageType.Info);

            if (stateChanged)
                _host.RequestRepaint();
        }

        /// <summary>
        /// 「マスクの種類」(除外/含める)の切り替え行。ブラシパレットと AI 提案セクションの
        /// 両方から呼ぶ(種類はツールに依らない共通の軸なので、どちらの UI からも同じ状態を
        /// 切り替える)。含めるはゾーン単位のみなので、共通マスクが編集対象のときは無効化する。
        /// </summary>
        public void DrawLayerKindSelector()
        {
            var zones = _host.Session?.zones;
            bool commonTarget = activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count;
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
            int w = sourceTexture.width;
            int h = sourceTexture.height;

            if (maskWidth != w || maskHeight != h)
            {
                int oldW = maskWidth, oldH = maskHeight;
                bool hadData = exclusionMask != null || zoneMasks.Count > 0 || zoneIncludeMasks.Count > 0;
                if (hadData && oldW > 0 && oldH > 0)
                {
                    exclusionMask = RescaleMask(exclusionMask, oldW, oldH, w, h);
                    var keys = new List<string>(zoneMasks.Keys);
                    foreach (var key in keys)
                    {
                        var scaled = RescaleMask(zoneMasks[key], oldW, oldH, w, h);
                        if (scaled != null) zoneMasks[key] = scaled;
                        else zoneMasks.Remove(key); // 不変条件: null 値のエントリは持たない
                    }
                    var incKeys = new List<string>(zoneIncludeMasks.Keys);
                    foreach (var key in incKeys)
                    {
                        var scaled = RescaleMask(zoneIncludeMasks[key], oldW, oldH, w, h);
                        if (scaled != null) zoneIncludeMasks[key] = scaled;
                        else zoneIncludeMasks.Remove(key);
                    }
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
        /// 指定ゾーンのマスクを確保（存在しなければ新規作成）して返す。
        /// </summary>
        private bool[] EnsureZoneMask(string zoneId)
        {
            EnsureMasks();
            if (string.IsNullOrEmpty(zoneId)) return null;
            if (maskWidth <= 0 || maskHeight <= 0) return null;
            int len = maskWidth * maskHeight;
            if (!zoneMasks.TryGetValue(zoneId, out var m) || m == null || m.Length != len)
            {
                m = new bool[len];
                zoneMasks[zoneId] = m;
            }
            return m;
        }

        /// <summary>
        /// 指定ゾーンの「含める」マスクを確保（存在しなければ新規作成）して返す。
        /// </summary>
        private bool[] EnsureZoneIncludeMask(string zoneId)
        {
            EnsureMasks();
            if (string.IsNullOrEmpty(zoneId)) return null;
            if (maskWidth <= 0 || maskHeight <= 0) return null;
            int len = maskWidth * maskHeight;
            if (!zoneIncludeMasks.TryGetValue(zoneId, out var m) || m == null || m.Length != len)
            {
                m = new bool[len];
                zoneIncludeMasks[zoneId] = m;
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
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
                return editIncludeLayer ? null : EnsureCommonMask();

            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            return editIncludeLayer ? EnsureZoneIncludeMask(zone.id) : EnsureZoneMask(zone.id);
        }

        /// <summary>
        /// 現在アクティブなターゲットのキー（"__common__" or zone.id）を返す。
        /// </summary>
        private string GetActiveTargetKey()
        {
            var zones = _host.Session.zones;
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
                return CommonMaskKey;
            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            return zone.id;
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

            if (activeMaskTarget == index) activeMaskTarget = -1;
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
        /// ブラシ 1 スタンプ分を塗る。gridW/gridH は表示プレビューの画素格子
        /// (previewTexture の実寸)で、brushSize はこの格子セル単位の半径。
        ///
        /// 塗りは格子セル単位で行い、セルに対応するマスクブロック
        /// [gx*maskW/gridW, (gx+1)*maskW/gridW) を丸ごと塗る。オーバーレイ表示
        /// (ComputeOverlayPixels)とプロキシ処理(IsExcludedCombined)は各セルにつき
        /// ブロック先頭の 1 画素だけを最近傍で読むため、セル内部に塗り残しがあると
        /// 「縮小表示では塗れて見えるのにフル解像度適用(詳細プレビュー/エクスポート)
        /// では穴」という不一致が起きていた。ブロック単位で塗ることでマスクが常に
        /// セル内一様になり、この不一致を構造的に排除する(WYSIWYG)。
        /// </summary>
        public void PaintMask(Vector2 uvPos, int gridW, int gridH)
        {
            bool[] target = GetActiveMaskArray();
            if (target == null) return;
            if (maskWidth <= 0 || maskHeight <= 0) return;
            if (gridW <= 0) gridW = maskWidth;
            if (gridH <= 0) gridH = maskHeight;

            int cx = Mathf.Min(gridW - 1, Mathf.FloorToInt(uvPos.x * gridW));
            int cy = Mathf.Min(gridH - 1, Mathf.FloorToInt(uvPos.y * gridH));
            int r = Mathf.Max(1, brushSize);
            bool value = !brushEraseMode;

            // オーバーレイ直接書き込み: テクスチャが塗り格子と同寸のときは、ブロック塗りと
            // 同時に対応セルを更新して即時フィードバックする(非同期再構築のスロットル待ちと
            // フル解像度 bool[] clone をストローク中に発生させない)。使えないとき
            // (未生成・寸法違い)は従来どおり maskDirty を立てて非同期再構築に任せる。
            var overlayTex = ActiveOverlayTexture(out Color32 overlayColor);
            bool direct = overlayTex != null && overlayTex.width == gridW && overlayTex.height == gridH;

            // 円ブラシをセル行ごとの span で決め、対応するマスクブロック矩形を塗る。
            // 各行の最大 dx は floor(sqrt(r²-dy²))。Mathf.Sqrt の丸めで境界セルを
            // 取りこぼさないよう整数で補正する。
            int rr = r * r;
            for (int dy = -r; dy <= r; dy++)
            {
                int gy = cy + dy;
                if (gy < 0 || gy >= gridH) continue;
                int rowRemain = rr - dy * dy;
                int dxMax = (int)Mathf.Sqrt(rowRemain);
                while ((dxMax + 1) * (dxMax + 1) <= rowRemain) dxMax++;
                while (dxMax > 0 && dxMax * dxMax > rowRemain) dxMax--;
                int gxLo = cx - dxMax; if (gxLo < 0) gxLo = 0;
                int gxHi = cx + dxMax; if (gxHi >= gridW) gxHi = gridW - 1;

                // セル範囲 → マスクブロック矩形 [mx0, mx1) × [my0, my1)。
                // 除算は処理側(IsExcludedCombined の x*maskW/texW)と同じ整数切り捨て。
                // grid が mask より細かい方向ではブロックが空になり得るため 1 画素を保証する。
                int my0 = (int)((long)gy * maskHeight / gridH);
                int my1 = (int)((long)(gy + 1) * maskHeight / gridH);
                if (my1 <= my0) my1 = Mathf.Min(maskHeight, my0 + 1);
                int mx0 = (int)((long)gxLo * maskWidth / gridW);
                int mx1 = (int)((long)(gxHi + 1) * maskWidth / gridW);
                if (mx1 <= mx0) mx1 = Mathf.Min(maskWidth, mx0 + 1);

                for (int my = my0; my < my1; my++)
                {
                    int rowBase = my * maskWidth;
                    for (int px = mx0; px < mx1; px++)
                        target[rowBase + px] = value;
                }

                if (direct)
                {
                    // オーバーレイの 1 画素 = 1 セル。SetPixels32 の y は行 0 = 下端で、
                    // gy(v 上向き)とそのまま一致する。消去は default(0,0,0,0) = 透明。
                    int spanW = gxHi - gxLo + 1;
                    var row = new Color32[spanW];
                    if (value)
                        for (int k = 0; k < spanW; k++) row[k] = overlayColor;
                    overlayTex.SetPixels32(gxLo, gy, spanW, 1, row);
                }
            }

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
        /// 現在の編集対象マスク(共通=赤 / ゾーン=ゾーン色)のオーバーレイ色を返す。
        /// AI 提案のオーバーレイを実マスクと同色に揃えるために公開する
        /// (提案→確定でマスクの見た目が変わらず「同じマスクになる」ことを示すため)。
        /// </summary>
        public Color32 ActiveMaskOverlayColor()
        {
            var zones = _host.Session.zones;
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
                return ExcludedOverlayColor;
            return editIncludeLayer ? IncludedOverlayColor : OverlayColorForZone(activeMaskTarget);
        }

        /// <summary>
        /// 現在の編集対象マスクに対応するオーバーレイテクスチャと塗り色を返す。
        /// (共通=maskOverlayTexture/赤、ゾーン=zoneMaskOverlayTexture/ゾーン色)
        /// </summary>
        private Texture2D ActiveOverlayTexture(out Color32 paintColor)
        {
            var zones = _host.Session.zones;
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
            {
                paintColor = ExcludedOverlayColor;
                return maskOverlayTexture;
            }
            // 含めるレイヤーも表示スロットはゾーン用テクスチャを共用する(同時表示しないため)。
            paintColor = editIncludeLayer ? IncludedOverlayColor : OverlayColorForZone(activeMaskTarget);
            return zoneMaskOverlayTexture;
        }

        /// <summary>
        /// PaintMask のオーバーレイ直接書き込みを GPU へ反映する。スタンプごとではなく
        /// ドラッグイベント 1 回分につき 1 回だけ Apply するため、呼び出し側
        /// (PaintAtScreenPos)の末尾で呼ぶ。
        /// </summary>
        public void FlushOverlayDirect()
        {
            if (!_overlayDirectPendingApply) return;
            _overlayDirectPendingApply = false;
            var tex = ActiveOverlayTexture(out _);
            if (tex != null) tex.Apply();
        }

        // バックグラウンドで生成したオーバーレイ Color32[] をメインスレッドで Texture2D に
        // 書き戻すまでの中継。SetPixels32 / Apply は Unity API なので必ずメインスレッド。
        private struct OverlayResult
        {
            public bool hasCommon;
            public Color32[] commonPixels;
            public bool hasZone;
            public Color32[] zonePixels;
            public int width;
            public int height;
        }

        [System.NonSerialized] private readonly PreviewJob<OverlayResult> _overlayJob = new PreviewJob<OverlayResult>();
        [System.NonSerialized] private OverlayResult? _pendingOverlayResult;

        /// <summary>
        /// 共通マスクとゾーン別マスクのオーバーレイテクスチャの再構築をバックグラウンドに
        /// スケジュールする。bool[] バッファを Clone してワーカに渡すため、ペイント中の
        /// 変更とデータレースしない。SetPixels32 / Apply は次フレーム以降に
        /// <see cref="ApplyPendingOverlay"/> で適用される。
        /// </summary>
        public void RebuildMaskOverlay(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            int capW = width;
            int capH = height;
            int capMw = maskWidth;
            int capMh = maskHeight;

            // 適用中のマスクはすべて表示する(共通の除外=赤 / ゾーン別の除外=ゾーン色 /
            // 含める=緑)。編集対象×種類の 1 枚だけを出す旧仕様は「表示されていないのに
            // 効いている」マスクを生み、対象や種類を切り替えた直後・プリセット読込
            // (編集対象が共通へ戻る)直後に、原因の見えない除外・変換漏れに見えていた
            // (2026-08-24 のユーザー報告)。どれを編集中かは明暗差で示す: 編集対象は
            // 従来アルファ、他は減光(InactiveOverlayAlphaScale)。編集対象は最後に
            // 描いて最前面にする(RenderMaskCoverage は上書き合成のため)。
            var zones = _host.Session.zones;
            bool commonIsActive = activeMaskTarget < 0
                || zones == null || activeMaskTarget >= zones.Count;

            bool[] commonSnap = null;
            Color32 commonColor = ExcludedOverlayColor;
            if (exclusionMask != null && capMw > 0 && capMh > 0)
            {
                commonSnap = (bool[])exclusionMask.Clone();
                if (!commonIsActive) commonColor = DimOverlayColor(ExcludedOverlayColor);
            }

            var zoneInfos = new List<(Color32 color, bool[] mask)>();
            if (zones != null && capMw > 0 && capMh > 0)
            {
                (Color32 color, bool[] mask)? editingEntry = null;
                for (int i = 0; i < zones.Count; i++)
                {
                    var zone = zones[i];
                    if (zone == null || string.IsNullOrEmpty(zone.id)) continue;
                    bool zoneIsTarget = i == activeMaskTarget;

                    if (zoneMasks.TryGetValue(zone.id, out var ex) && ex != null)
                    {
                        bool isEditing = zoneIsTarget && !editIncludeLayer;
                        var color = OverlayColorForZone(i);
                        var entry = (isEditing ? color : DimOverlayColor(color), (bool[])ex.Clone());
                        if (isEditing) editingEntry = entry;
                        else zoneInfos.Add(entry);
                    }
                    if (zoneIncludeMasks.TryGetValue(zone.id, out var inc) && inc != null)
                    {
                        bool isEditing = zoneIsTarget && editIncludeLayer;
                        var entry = (isEditing ? IncludedOverlayColor
                                               : DimOverlayColor(IncludedOverlayColor),
                                     (bool[])inc.Clone());
                        if (isEditing) editingEntry = entry;
                        else zoneInfos.Add(entry);
                    }
                }
                if (editingEntry.HasValue) zoneInfos.Add(editingEntry.Value);
            }

            _overlayJob.Schedule(
                work: token => ComputeOverlayPixels(commonSnap, commonColor, zoneInfos, capW, capH, capMw, capMh, token),
                apply: result =>
                {
                    _pendingOverlayResult = result;
                    _host.RequestRepaint();
                });
        }

        private static OverlayResult ComputeOverlayPixels(
            bool[] common, Color32 commonColor, List<(Color32 color, bool[] mask)> zoneInfos,
            int w, int h, int mw, int mh, CancellationToken token)
        {
            var result = new OverlayResult { width = w, height = h };

            if (common != null && mw > 0 && mh > 0)
            {
                result.hasCommon = true;
                result.commonPixels = RenderMaskCoverage(common, commonColor, null, w, h, mw, mh);
                token.ThrowIfCancellationRequested();
            }

            if (zoneInfos != null && zoneInfos.Count > 0 && mw > 0 && mh > 0)
            {
                result.hasZone = true;
                Color32[] pixels = null;
                foreach (var (color, zm) in zoneInfos)
                {
                    pixels = RenderMaskCoverage(zm, color, pixels, w, h, mw, mh);
                    token.ThrowIfCancellationRequested();
                }
                result.zonePixels = pixels;
            }

            return result;
        }

        /// <summary>
        /// マスクをオーバーレイ解像度へ「被覆率比例アルファ」で描画する。
        /// マスクと表示が同解像度なら被覆率は 0/1 で従来の最近傍と同一出力。
        /// マスクの方が高解像度(例: 4096 マスク→2048 表示)のときは境界セルのアルファが
        /// 被覆率で階調化され、実体どおりの滑らかな縁に見える(二値ブロックの偽ギザギザを防ぐ)。
        /// accumulate 非 null 時はその配列に上書き合成して返す(ゾーン重ね描き用)。
        /// </summary>
        private static Color32[] RenderMaskCoverage(
            bool[] mask, Color32 color, Color32[] accumulate, int w, int h, int mw, int mh)
        {
            var pixels = accumulate ?? new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int my0 = Mathf.Clamp(y * mh / h, 0, mh - 1);
                int my1 = Mathf.Clamp((y + 1) * mh / h, my0 + 1, mh);
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int mx0 = Mathf.Clamp(x * mw / w, 0, mw - 1);
                    int mx1 = Mathf.Clamp((x + 1) * mw / w, mx0 + 1, mw);
                    int count = 0;
                    for (int my = my0; my < my1; my++)
                    {
                        int myBase = my * mw;
                        for (int mx = mx0; mx < mx1; mx++)
                            if (mask[myBase + mx]) count++;
                    }
                    if (count == 0) continue;
                    int total = (my1 - my0) * (mx1 - mx0);
                    if (count == total)
                    {
                        pixels[rowBase + x] = color;
                    }
                    else
                    {
                        var c = color;
                        c.a = (byte)Mathf.Clamp(Mathf.RoundToInt(color.a * count / (float)total), 1, color.a);
                        pixels[rowBase + x] = c;
                    }
                }
            }
            return pixels;
        }

        /// <summary>
        /// バックグラウンドで生成された Color32[] をオーバーレイテクスチャに適用する。
        /// PreviewView.Draw の冒頭から呼ばれる。
        /// </summary>
        public void ApplyPendingOverlay()
        {
            if (!_pendingOverlayResult.HasValue) return;
            // ペイント中に直接書き込みへ移行済みなら適用を保留する。スナップショット clone
            // 時点より新しいスタンプが古い結果に一瞬上書きされて見えるのを防ぐ。結果は保持し、
            // ストローク終了後(EndStroke が maskDirty を立てて再構築)に最新内容へ収束する。
            if (isPainting && _strokeHadDirectOverlayWrite) return;
            var r = _pendingOverlayResult.Value;
            _pendingOverlayResult = null;

            if (r.hasCommon)
            {
                // Bilinear: プレビュー本体テクスチャ(previewTexture)と同じ補間で拡大表示する。
                // Point だと拡大時にオーバーレイだけ格子状にカクつき、実際は正しい判定でも
                // マスクがはみ出しているように見えてしまう。
                TextureSlot.Resize(ref maskOverlayTexture, r.width, r.height, FilterMode.Bilinear);
                maskOverlayTexture.SetPixels32(r.commonPixels);
                maskOverlayTexture.Apply();
            }
            else
            {
                TextureSlot.Release(ref maskOverlayTexture);
            }

            if (r.hasZone)
            {
                TextureSlot.Resize(ref zoneMaskOverlayTexture, r.width, r.height, FilterMode.Bilinear);
                zoneMaskOverlayTexture.SetPixels32(r.zonePixels);
                zoneMaskOverlayTexture.Apply();
            }
            else
            {
                TextureSlot.Release(ref zoneMaskOverlayTexture);
            }
        }

        /// <summary>
        /// ゾーンインデックスから黄金比ベースのオーバーレイ色を決定する。
        /// </summary>
        public static Color32 OverlayColorForZone(int zoneIndex)
        {
            const float golden = 0.61803398875f;
            float h = (zoneIndex * golden) % 1f;
            if (h < 0) h += 1f;
            Color rgb = Color.HSVToRGB(h, 0.8f, 1f);
            return new Color32(
                (byte)Mathf.RoundToInt(rgb.r * 255f),
                (byte)Mathf.RoundToInt(rgb.g * 255f),
                (byte)Mathf.RoundToInt(rgb.b * 255f),
                100);
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
            var snap = new MaskSnapshot
            {
                width = maskWidth,
                height = maskHeight,
                zones = new Dictionary<string, ulong[]>()
            };
            snap.common = MaskSnapshot.Pack(exclusionMask);   // Pack(null) は null
            foreach (var kv in zoneMasks)
            {
                if (kv.Value == null) continue;
                snap.zones[kv.Key] = MaskSnapshot.Pack(kv.Value);
            }
            if (zoneIncludeMasks.Count > 0)
            {
                foreach (var kv in zoneIncludeMasks)
                {
                    // 全 false の含めるマスクはスナップショットに載せない。載せると処理側が
                    // 空の includedPx 展開(全画素ループ)を毎回行い、選択キャッシュキーも
                    // 「含めるなし」と別になってしまう(出力は同じなのにミスが増える)。
                    if (kv.Value == null || !AnyTrue(kv.Value)) continue;
                    if (snap.zoneIncludes == null)
                        snap.zoneIncludes = new Dictionary<string, ulong[]>();
                    snap.zoneIncludes[kv.Key] = MaskSnapshot.Pack(kv.Value);
                }
            }
            return snap;
        }

        /// <summary>
        /// アクティブなマスク編集対象を共通マスクへリセットする。
        /// </summary>
        public void ResetActiveTarget()
        {
            activeMaskTarget = -1;
            maskDirty = true;
        }

        /// <summary>
        /// オーバーレイテクスチャと進行中のオーバーレイ生成ジョブを破棄する。
        /// </summary>
        public void ReleaseOverlayTextures()
        {
            SuspendTransientState();
            _overlayJob.Dispose();
            // AI 提案の進行中推論を破棄し、**静的サービスへの購読も解除する**(ウィンドウ破棄時)。
            // 解除しないと閉じたウィンドウのコントローラがイベント経由で生き残り、再オープン後に
            // 新しいコントローラから提案を横取りする(MaskSuggestController.Shutdown 参照)。
            // 参照も落として、万一この後に触られても新しいインスタンスが作り直されるようにする。
            _suggestController?.Shutdown();
            _suggestController = null;
        }

        public void SuspendTransientState()
        {
            _overlayJob.Cancel();
            _pendingOverlayResult = null;
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
