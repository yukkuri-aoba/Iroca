using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// マスク対象選択・ブラシ入力・オーバーレイ生成・bool[] バッファと
    /// _session.maskState の同期を担当する。
    /// ゾーン本体の編集 UI、プレビュー生成本体、プリセット一覧、エクスポートは扱わない。
    /// </summary>
    [System.Serializable]
    internal class MaskPaintView
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

        [System.NonSerialized] public bool isPainting;
        [System.NonSerialized] public Vector2 lastPaintUV = -Vector2.one;

        // ストローク中フラグ（同一ストロークで二重 Undo 登録しないため）
        [System.NonSerialized] public bool _maskStrokeStarted;

        // 共通マスク用のターゲットキー（SessionState キーにも使う）
        public const string CommonMaskKey = "__common__";

        // マスクペイントモード: ブラシストロークが機能する前に明示的にアクティベートされる必要があります
        [System.NonSerialized] public bool maskPaintActive;

        [System.NonSerialized] private VACCWindow _host;

        public void Initialize(VACCWindow host)
        {
            _host = host;
        }

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

            GUI.backgroundColor = excludeActive ? VACCColors.ExcludeButton : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.Exclude, Localization.ExcludeTooltip), EditorStyles.miniButtonLeft))
            {
                if (excludeActive)
                    maskPaintActive = false;
                else
                { maskPaintActive = true; brushEraseMode = false; }
            }

            GUI.backgroundColor = includeActive ? VACCColors.IncludeButton : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.Include, Localization.IncludeTooltip), EditorStyles.miniButtonRight))
            {
                if (includeActive)
                    maskPaintActive = false;
                else
                { maskPaintActive = true; brushEraseMode = true; }
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

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// 編集対象プルダウン。-1 = 共通マスク、0 以上 = zones[index] のマスク。
        /// </summary>
        private void DrawMaskTargetSelector()
        {
            _host.EnsureAllZoneIds();
            var zones = _host.Session.zones;

            int zoneCount = zones != null ? zones.Count : 0;
            var options = new GUIContent[zoneCount + 1];
            options[0] = new GUIContent(Localization.MaskTargetCommon);
            for (int i = 0; i < zoneCount; i++)
            {
                string label = string.IsNullOrEmpty(zones[i].name) ? "Zone" : zones[i].name;
                options[i + 1] = new GUIContent(label);
            }

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
        /// 解像度が変わったときにのみ既存マスクを破棄する。
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
                // 解像度が変わったのでマスク座標系が合わない。共通もゾーンも全破棄。
                maskWidth = w;
                maskHeight = h;
                exclusionMask = null;
                zoneMasks.Clear();
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

        // ─────────────────────── ペイント ─────────────────────────

        public void PaintMask(Vector2 uvPos)
        {
            bool[] target = GetActiveMaskArray();
            if (target == null) return;

            int cx = Mathf.RoundToInt(uvPos.x * maskWidth);
            int cy = Mathf.RoundToInt(uvPos.y * maskHeight);

            // ブラシサイズは「プレビュー画像上のピクセル数」で設定されるため、
            // マスク座標系（フル解像度）に合わせてスケーリングする必要がある。
            float maskScale = maskWidth / (float)Mathf.Min(maskWidth, VACCConsts.Preview.MaxSize);
            int r = Mathf.Max(1, Mathf.RoundToInt(brushSize * maskScale));

            bool value = !brushEraseMode;

            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    int px = cx + dx;
                    int py = cy + dy;
                    if (px < 0 || px >= maskWidth || py < 0 || py >= maskHeight) continue;
                    target[py * maskWidth + px] = value;
                }
            }

            maskDirty = true;
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

            // 共通マスク bool[] のスナップショット
            bool[] commonSnap = exclusionMask != null ? (bool[])exclusionMask.Clone() : null;

            // ゾーン別マスクのスナップショット（idx, color, mask[]）
            var zones = _host.Session.zones;
            var zoneInfos = new List<(Color32 color, bool[] mask)>();
            if (zones != null && capMw > 0 && capMh > 0)
            {
                for (int zi = 0; zi < zones.Count; zi++)
                {
                    var zone = zones[zi];
                    if (zone == null || string.IsNullOrEmpty(zone.id)) continue;
                    if (!zoneMasks.TryGetValue(zone.id, out var zm) || zm == null) continue;
                    zoneInfos.Add((OverlayColorForZone(zi), (bool[])zm.Clone()));
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
                var excluded = new Color32(255, 60, 60, 80);
                for (int i = 0; i < pixels.Length; i++)
                {
                    int x = i % w;
                    int y = i / w;
                    int mx = Mathf.Clamp(x * mw / w, 0, mw - 1);
                    int my = Mathf.Clamp(y * mh / h, 0, mh - 1);
                    if (common[my * mw + mx]) pixels[i] = excluded;
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
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        int x = i % w;
                        int y = i / w;
                        int mx = Mathf.Clamp(x * mw / w, 0, mw - 1);
                        int my = Mathf.Clamp(y * mh / h, 0, mh - 1);
                        if (zm[my * mw + mx]) pixels[i] = color;
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
        }

        // ───────────────────────── Mask Persistence ────────────────────

        private string MaskSessionPathKey()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null) return null;
            string path = AssetDatabase.GetAssetPath(sourceTexture);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        private string MaskIndexSessionKey(string path) => "VACC_MaskIndex_" + path;
        private string MaskArraySessionKey(string path, string targetKey) => "VACC_Mask_" + path + ":" + targetKey;
        private string LegacyMaskSessionKey(string path) => "VACC_Mask_" + path;

        [System.Serializable]
        private class MaskIndex
        {
            public bool hasCommon;
            public List<string> zoneIds = new List<string>();
            public int width;
            public int height;
        }

        /// <summary>
        /// 現在の全マスク（共通 + 各ゾーン）を SessionState に保存し、
        /// 同時に _session.maskState を bool[] バッファの内容で更新する。
        /// </summary>
        public void SaveToSession()
        {
            // bool[] バッファを _session.maskState（RLE 文字列）に書き戻す。
            SyncBuffersToState();

            string path = MaskSessionPathKey();
            if (path == null) return;

            // 旧キーは常にクリア（新フォーマットに移行）
            SessionState.EraseString(LegacyMaskSessionKey(path));

            var idx = new MaskIndex { width = maskWidth, height = maskHeight };

            if (exclusionMask != null && AnyTrue(exclusionMask))
            {
                SessionState.SetString(
                    MaskArraySessionKey(path, CommonMaskKey),
                    EncodeMask(exclusionMask, maskWidth, maskHeight));
                idx.hasCommon = true;
            }
            else
            {
                SessionState.EraseString(MaskArraySessionKey(path, CommonMaskKey));
            }

            foreach (var kv in zoneMasks)
            {
                if (kv.Value == null || !AnyTrue(kv.Value))
                {
                    SessionState.EraseString(MaskArraySessionKey(path, kv.Key));
                    continue;
                }
                SessionState.SetString(
                    MaskArraySessionKey(path, kv.Key),
                    EncodeMask(kv.Value, maskWidth, maskHeight));
                idx.zoneIds.Add(kv.Key);
            }

            SessionState.SetString(MaskIndexSessionKey(path), JsonUtility.ToJson(idx));
        }

        /// <summary>
        /// 現在の bool[] バッファの内容を RLE エンコードして _session.maskState に書き戻す。
        /// </summary>
        public void SyncBuffersToState()
        {
            var session = _host.Session;
            if (session == null) return;
            var ms = session.maskState ?? (session.maskState = new MaskState());
            ms.width = maskWidth;
            ms.height = maskHeight;

            ms.commonMaskBase64 = (exclusionMask != null && AnyTrue(exclusionMask))
                ? EncodeMask(exclusionMask, maskWidth, maskHeight)
                : "";

            ms.zones.Clear();
            foreach (var kv in zoneMasks)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || !AnyTrue(kv.Value)) continue;
                ms.zones.Add(new MaskZoneEntry
                {
                    zoneId = kv.Key,
                    maskBase64 = EncodeMask(kv.Value, maskWidth, maskHeight),
                });
            }
        }

        /// <summary>
        /// _session.maskState（RLE 文字列）を bool[] バッファに展開する。
        /// </summary>
        public void SyncBuffersFromState()
        {
            var session = _host.Session;
            if (session == null || session.maskState == null) return;
            var ms = session.maskState;

            exclusionMask = null;
            zoneMasks.Clear();

            if (ms.width <= 0 || ms.height <= 0) return;
            maskWidth = ms.width;
            maskHeight = ms.height;

            if (!string.IsNullOrEmpty(ms.commonMaskBase64))
            {
                var arr = DecodeMask(ms.commonMaskBase64, out int w, out int h);
                if (arr != null && w == maskWidth && h == maskHeight)
                    exclusionMask = arr;
            }

            if (ms.zones != null)
            {
                foreach (var entry in ms.zones)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.zoneId)) continue;
                    var arr = DecodeMask(entry.maskBase64, out int w, out int h);
                    if (arr != null && w == maskWidth && h == maskHeight)
                        zoneMasks[entry.zoneId] = arr;
                }
            }
        }

        /// <summary>
        /// SessionState から全マスクを復元する。旧フォーマット（単一マスク）しか無い場合は
        /// 共通マスクへ移行してから新フォーマットで保存し直す。
        /// </summary>
        public void RestoreFromSession()
        {
            string path = MaskSessionPathKey();
            if (path == null) return;

            exclusionMask = null;
            zoneMasks.Clear();

            string indexJson = SessionState.GetString(MaskIndexSessionKey(path), null);
            if (!string.IsNullOrEmpty(indexJson))
            {
                MaskIndex idx = null;
                try
                {
                    idx = JsonUtility.FromJson<MaskIndex>(indexJson);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[VACC] Mask index decode failed: {ex.Message}");
                    idx = null;
                }
                if (idx == null)
                {
                    SessionState.EraseString(MaskIndexSessionKey(path));
                    return;
                }

                maskWidth = idx.width;
                maskHeight = idx.height;

                if (idx.hasCommon)
                {
                    string enc = SessionState.GetString(MaskArraySessionKey(path, CommonMaskKey), null);
                    if (!string.IsNullOrEmpty(enc))
                        exclusionMask = DecodeMask(enc, out _, out _);
                }

                if (idx.zoneIds != null)
                {
                    foreach (var zid in idx.zoneIds)
                    {
                        if (string.IsNullOrEmpty(zid)) continue;
                        string enc = SessionState.GetString(MaskArraySessionKey(path, zid), null);
                        if (string.IsNullOrEmpty(enc)) continue;
                        var arr = DecodeMask(enc, out _, out _);
                        if (arr != null) zoneMasks[zid] = arr;
                    }
                }
                SyncBuffersToState();
                maskDirty = true;
                return;
            }

            // ─ 旧フォーマットフォールバック: 単一マスク → 共通へ移行 ─
            string legacyEncoded = SessionState.GetString(LegacyMaskSessionKey(path), null);
            if (string.IsNullOrEmpty(legacyEncoded)) return;

            var legacy = DecodeMask(legacyEncoded, out int lw, out int lh);
            if (legacy == null) return;

            maskWidth = lw;
            maskHeight = lh;
            exclusionMask = legacy;

            SessionState.EraseString(LegacyMaskSessionKey(path));
            SaveToSession();
            maskDirty = true;
        }

        /// <summary>
        /// テクスチャ切り替え時にバッファを破棄する。
        /// </summary>
        public void ClearBuffersOnTextureChange()
        {
            exclusionMask = null;
            zoneMasks.Clear();
        }

        // ─────────────────────── Encode / Decode ────────────────────

        private static bool AnyTrue(bool[] arr)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++) if (arr[i]) return true;
            return false;
        }

        /// <summary>
        /// bool 配列を RLE 圧縮 + Base64 文字列にエンコード。
        /// フォーマット: "R:" プレフィックス + Base64(4byte W + 4byte H + 1byte 開始値 + uint32[] ランレングス列)
        /// </summary>
        public static string EncodeMask(bool[] mask, int w, int h)
        {
            if (mask == null || mask.Length == 0) return "";
            int len = mask.Length;

            var runs = new List<uint>();
            bool curVal = mask[0];
            uint count = 0;
            for (int i = 0; i < len; i++)
            {
                if (mask[i] == curVal)
                {
                    count++;
                }
                else
                {
                    runs.Add(count);
                    curVal = mask[i];
                    count = 1;
                }
            }
            runs.Add(count);

            byte[] bytes = new byte[9 + runs.Count * 4];
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(w), 0, bytes, 0, 4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(h), 0, bytes, 4, 4);
            bytes[8] = mask[0] ? (byte)1 : (byte)0;
            for (int i = 0; i < runs.Count; i++)
                System.Buffer.BlockCopy(System.BitConverter.GetBytes(runs[i]), 0, bytes, 9 + i * 4, 4);

            return "R:" + System.Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// EncodeMask の逆。デコード失敗時は null を返す。
        /// "R:" プレフィックスがある場合は RLE フォーマット、ない場合は旧 bitpack フォーマットとして処理。
        /// </summary>
        public static bool[] DecodeMask(string encoded, out int w, out int h)
        {
            w = 0; h = 0;
            if (string.IsNullOrEmpty(encoded)) return null;

            if (encoded.StartsWith("R:", System.StringComparison.Ordinal))
            {
                try
                {
                    byte[] bytes = System.Convert.FromBase64String(encoded.Substring(2));
                    if (bytes.Length < 9) return null;
                    w = System.BitConverter.ToInt32(bytes, 0);
                    h = System.BitConverter.ToInt32(bytes, 4);
                    if (w <= 0 || h <= 0) return null;
                    int len = w * h;
                    bool curVal = bytes[8] != 0;
                    bool[] mask = new bool[len];
                    int pos = 0;
                    int byteIdx = 9;
                    while (pos < len && byteIdx + 4 <= bytes.Length)
                    {
                        uint run = System.BitConverter.ToUInt32(bytes, byteIdx);
                        byteIdx += 4;
                        bool fillVal = curVal;
                        uint end = (uint)System.Math.Min((long)pos + run, len);
                        while (pos < (int)end)
                            mask[pos++] = fillVal;
                        curVal = !curVal;
                    }
                    return mask;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[VACC] Mask decode failed: {ex.Message}");
                    return null;
                }
            }

            // 旧 bitpack フォーマット（後方互換）
            try
            {
                byte[] packed = System.Convert.FromBase64String(encoded);
                if (packed.Length < 9) return null;
                w = System.BitConverter.ToInt32(packed, 0);
                h = System.BitConverter.ToInt32(packed, 4);
                if (w <= 0 || h <= 0) return null;
                int len = w * h;
                if (packed.Length < 8 + (len + 7) / 8) return null;
                bool[] mask = new bool[len];
                for (int i = 0; i < len; i++)
                    mask[i] = (packed[8 + i / 8] & (1 << (i % 8))) != 0;
                return mask;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[VACC] Mask decode (legacy bitpack) failed: {ex.Message}");
                return null;
            }
        }

        // ───────────────────────── Processing 用スナップショット ────────────────────

        /// <summary>
        /// 現在のマスク状態をスナップショット化する（deep clone）。
        /// </summary>
        public MaskSnapshot BuildSnapshot()
        {
            var snap = new MaskSnapshot
            {
                width = maskWidth,
                height = maskHeight,
                zones = new Dictionary<string, bool[]>()
            };
            if (exclusionMask != null) snap.common = (bool[])exclusionMask.Clone();
            foreach (var kv in zoneMasks)
            {
                if (kv.Value == null) continue;
                snap.zones[kv.Key] = (bool[])kv.Value.Clone();
            }
            return snap;
        }

        // ───────────────────────── プリセット連携 ────────────────────

        /// <summary>
        /// プリセット内のマスクデータで現在のマスク状態を置き換える。
        /// </summary>
        public void ApplyFromPreset(VACCPresetData data)
        {
            if (data == null || data.maskWidth <= 0 || data.maskHeight <= 0) return;

            maskWidth = data.maskWidth;
            maskHeight = data.maskHeight;
            exclusionMask = null;
            zoneMasks.Clear();

            if (!string.IsNullOrEmpty(data.commonMaskBase64))
            {
                var m = DecodeMask(data.commonMaskBase64, out int w, out int h);
                if (m != null && w == maskWidth && h == maskHeight)
                    exclusionMask = m;
            }

            if (data.zoneMasks != null)
            {
                foreach (var e in data.zoneMasks)
                {
                    if (e == null || string.IsNullOrEmpty(e.zoneId)) continue;
                    var m = DecodeMask(e.maskBase64, out int w, out int h);
                    if (m == null || w != maskWidth || h != maskHeight) continue;
                    zoneMasks[e.zoneId] = m;
                }
            }

            SaveToSession();
        }

        /// <summary>
        /// 現在のマスク状態を VACCPresetData の commonMaskBase64 / zoneMasks フィールドに書き出す。
        /// </summary>
        public void WriteToPreset(VACCPresetData data)
        {
            if (data == null || maskWidth <= 0 || maskHeight <= 0) return;

            bool includedAnything = false;

            if (exclusionMask != null && AnyTrue(exclusionMask))
            {
                data.commonMaskBase64 = EncodeMask(exclusionMask, maskWidth, maskHeight);
                includedAnything = true;
            }

            foreach (var kv in zoneMasks)
            {
                if (kv.Value == null || !AnyTrue(kv.Value)) continue;
                data.zoneMasks.Add(new ZoneMaskEntry
                {
                    zoneId = kv.Key,
                    maskBase64 = EncodeMask(kv.Value, maskWidth, maskHeight),
                });
                includedAnything = true;
            }

            if (includedAnything)
            {
                data.maskWidth = maskWidth;
                data.maskHeight = maskHeight;
            }
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
            _overlayJob.Dispose();
            _pendingOverlayResult = null;
            TextureSlot.Release(ref maskOverlayTexture);
            TextureSlot.Release(ref zoneMaskOverlayTexture);
        }
    }
}
