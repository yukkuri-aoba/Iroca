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
        // ─── シリアライズ対象（UI 状態） ─────────────────────────────
        // どのマスクを編集対象にするか。-1 = 共通マスク、0 以上 = zones[index]。
        public int activeMaskTarget = -1;
        public int brushSize = 8;
        public bool brushEraseMode; // false = 除外ペイント、true = 除外消去
        public bool maskFoldout = true;

        // ─── 実行時バッファ（NonSerialized） ──────────────────────────
        // 共通マスク（フル解像度、true = 除外）。全ゾーンに適用される。
        [System.NonSerialized] public bool[] exclusionMask;
        [System.NonSerialized] public int maskWidth, maskHeight;

        // ゾーン別マスク: key = ColorZone.id。配列サイズは maskWidth * maskHeight。
        // 値が null のエントリは持たない（存在しない = 全ピクセル非除外扱い）。
        [System.NonSerialized] public Dictionary<string, bool[]> zoneMasks = new Dictionary<string, bool[]>();

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

        // 共通マスク用のターゲットキー（SessionState キーにも使う）
        public const string CommonMaskKey = "__common__";

        // マスクペイントモード: ブラシストロークが機能する前に明示的にアクティベートされる必要があります
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

        /// <summary>AI 提案モードがプレビュークリックを受け取るべきか。</summary>
        public bool AiSuggestArmed => _suggestController != null && _suggestController.Active;

        /// <summary>プレビューへ重ねる AI 提案オーバーレイ(なければ null)。</summary>
        public Texture2D AiSuggestOverlay => _suggestController?.OverlayTexture;

        // ─────────────────────── 除外マスク UI ───────────────────────

        public void Draw()
        {
            maskFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(maskFoldout, Localization.ExclusionMask);
            if (!maskFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            DrawMaskTargetSelector();

            brushSize = EditorGUILayout.IntSlider(
                new GUIContent(Localization.BrushSize, Localization.BrushSizeTooltip),
                brushSize, 1, 64);

            // Exclude / Include ボタン: 押すとペイントモードON+モード選択、同じボタン再押しでOFF
            bool excludeActive = maskPaintActive && !brushEraseMode;
            bool includeActive = maskPaintActive && brushEraseMode;

            EditorGUILayout.BeginHorizontal();
            var prevBg = GUI.backgroundColor;

            GUI.backgroundColor = excludeActive ? IrocaColors.ExcludeButton : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.Exclude, Localization.ExcludeTooltip), EditorStyles.miniButtonLeft))
            {
                if (excludeActive)
                    maskPaintActive = false;
                else
                {
                    maskPaintActive = true; brushEraseMode = false;
                    _suggestController?.SetActive(false); // AI 提案とは排他
                }
            }

            GUI.backgroundColor = includeActive ? IrocaColors.IncludeButton : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.Include, Localization.IncludeTooltip), EditorStyles.miniButtonRight))
            {
                if (includeActive)
                    maskPaintActive = false;
                else
                {
                    maskPaintActive = true; brushEraseMode = true;
                    _suggestController?.SetActive(false); // AI 提案とは排他
                }
            }

            GUI.backgroundColor = prevBg;
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button(new GUIContent(Localization.ClearMask, Localization.ClearMaskTooltip)))
            {
                // bool[] バッファを _session.maskState に同期してから Undo 登録、クリア後に再同期。
                SyncBuffersToState();
                Undo.RegisterCompleteObjectUndo(_host, "Clear Mask");
                ClearActiveMask();
                SyncBuffersToState();
                maskDirty = true;
                _host.MarkPreviewDirty();
            }

            // Unity 標準 Undo に統合済みのため、専用ボタンは PerformUndo の薄いショートカットとして残す。
            if (GUILayout.Button(new GUIContent(Localization.UndoMask, Localization.UndoMaskTooltip)))
            {
                Undo.PerformUndo();
            }

            EditorGUILayout.HelpBox(
                maskPaintActive ? Localization.MaskHint : Localization.MaskHintPaintOff,
                MessageType.Info);

            // AI マスク提案(Sentis 統合が存在するときのみ描画される)
            MaskSuggestSection.Draw(_host, this);

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
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

        // ─────────────────────── マスク確保 ─────────────────────────

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
                bool hadData = exclusionMask != null || zoneMasks.Count > 0;
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
                    Debug.Log($"[Iroca] テクスチャ解像度の変更 ({oldW}x{oldH} → {w}x{h}) に合わせてマスクをリスケールしました。");
                }
                else
                {
                    exclusionMask = null;
                    zoneMasks.Clear();
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
        /// 現在のアクティブターゲット（共通 or ゾーン）のマスク配列を返す。必要なら確保する。
        /// 共通マスクは実際にペイント先になった時だけ確保される（zone-only mask の保全のため）。
        /// </summary>
        public bool[] GetActiveMaskArray()
        {
            var zones = _host.Session.zones;
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
                return EnsureCommonMask();

            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            return EnsureZoneMask(zone.id);
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
                exclusionMask = null;
            }
            else if (zones != null && activeMaskTarget < zones.Count)
            {
                var zone = zones[activeMaskTarget];
                zone.EnsureId();
                zoneMasks.Remove(zone.id);
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
                zoneMasks.Remove(id);

            // アクティブターゲットの調整
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

        // ─────────────────────── ペイント ─────────────────────────

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
            paintColor = OverlayColorForZone(activeMaskTarget);
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

        // ─────────────────────── マスクオーバーレイ（非同期） ─────────────────────────

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

            // 編集対象のマスクだけをオーバーレイ表示する。
            // 全ゾーンのマスクを重ねて出すと、重なり順で「最後のゾーン」が見え続け、
            // どのマスクを編集しているのか分からなくなるため、対象を切り替えたら表示も切り替える。
            var zones = _host.Session.zones;
            bool commonIsActive = activeMaskTarget < 0
                || zones == null || activeMaskTarget >= zones.Count;

            // 共通マスク bool[] のスナップショット（共通マスクが編集対象のときのみ）
            bool[] commonSnap = (commonIsActive && exclusionMask != null)
                ? (bool[])exclusionMask.Clone() : null;

            // ゾーン別マスクのスナップショット（編集対象ゾーンのみ）
            var zoneInfos = new List<(Color32 color, bool[] mask)>();
            if (!commonIsActive && capMw > 0 && capMh > 0)
            {
                var zone = zones[activeMaskTarget];
                if (zone != null && !string.IsNullOrEmpty(zone.id)
                    && zoneMasks.TryGetValue(zone.id, out var zm) && zm != null)
                {
                    zoneInfos.Add((OverlayColorForZone(activeMaskTarget), (bool[])zm.Clone()));
                }
            }

            _overlayJob.Schedule(
                work: token => ComputeOverlayPixels(commonSnap, zoneInfos, capW, capH, capMw, capMh, token),
                apply: result =>
                {
                    _pendingOverlayResult = result;
                    _host.RequestRepaint();
                });
        }

        private static OverlayResult ComputeOverlayPixels(
            bool[] common, List<(Color32 color, bool[] mask)> zoneInfos,
            int w, int h, int mw, int mh, CancellationToken token)
        {
            var result = new OverlayResult { width = w, height = h };

            if (common != null && mw > 0 && mh > 0)
            {
                result.hasCommon = true;
                var pixels = new Color32[w * h];
                var excluded = ExcludedOverlayColor;
                // 行ループ化で i%w / i/w の除算を排除(my は行ごとに一定)。出力は不変。
                for (int y = 0; y < h; y++)
                {
                    int my = Mathf.Clamp(y * mh / h, 0, mh - 1);
                    int rowBase = y * w;
                    int myBase = my * mw;
                    for (int x = 0; x < w; x++)
                    {
                        int mx = Mathf.Clamp(x * mw / w, 0, mw - 1);
                        if (common[myBase + mx]) pixels[rowBase + x] = excluded;
                    }
                }
                token.ThrowIfCancellationRequested();
                result.commonPixels = pixels;
            }

            if (zoneInfos != null && zoneInfos.Count > 0 && mw > 0 && mh > 0)
            {
                result.hasZone = true;
                var pixels = new Color32[w * h];
                foreach (var (color, zm) in zoneInfos)
                {
                    for (int y = 0; y < h; y++)
                    {
                        int my = Mathf.Clamp(y * mh / h, 0, mh - 1);
                        int rowBase = y * w;
                        int myBase = my * mw;
                        for (int x = 0; x < w; x++)
                        {
                            int mx = Mathf.Clamp(x * mw / w, 0, mw - 1);
                            if (zm[myBase + mx]) pixels[rowBase + x] = color;
                        }
                    }
                    token.ThrowIfCancellationRequested();
                }
                result.zonePixels = pixels;
            }

            return result;
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
                TextureSlot.Resize(ref maskOverlayTexture, r.width, r.height, FilterMode.Point);
                maskOverlayTexture.SetPixels32(r.commonPixels);
                maskOverlayTexture.Apply();
            }
            else
            {
                TextureSlot.Release(ref maskOverlayTexture);
            }

            if (r.hasZone)
            {
                TextureSlot.Resize(ref zoneMaskOverlayTexture, r.width, r.height, FilterMode.Point);
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

        // ───────────────────────── Mask Stroke Undo ────────────────────────────

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

        // ───────────────────────── Processing 用スナップショット ────────────────────

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
            // AI 提案の積み上げ・オーバーレイ・進行中推論も破棄(ウィンドウ破棄時)
            _suggestController?.OnSourceChangedOrClosing();
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
