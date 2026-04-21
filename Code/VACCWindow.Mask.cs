using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // ─── 除外マスク ────────────────────────────────────────────────
        // マスクは「共通マスク」と「ゾーン別マスク」のハイブリッド構成。
        // 処理時はピクセルごとに「共通マスクで除外」OR「そのゾーンの個別マスクで除外」と
        // なった場合に当該ゾーンの再色付けをスキップする。
        //
        // サイズが大きいため、マスクそのものは SerializeField せず
        // SessionState 経由で bitpack + Base64 文字列として永続化する。
        // ─────────────────────────────────────────────────────────────

        // 共通マスク（フル解像度、true = 除外）。全ゾーンに適用される。
        private bool[] exclusionMask;
        private int maskWidth, maskHeight;

        // ゾーン別マスク: key = ColorZone.id。配列サイズは maskWidth * maskHeight。
        // 値が null のエントリは持たない（存在しない = 全ピクセル非除外扱い）。
        private Dictionary<string, bool[]> zoneMasks = new Dictionary<string, bool[]>();

        // どのマスクを編集対象にするか。-1 = 共通マスク、0 以上 = zones[index]。
        [SerializeField] private int activeMaskTarget = -1;

        [SerializeField] private int brushSize = 8;
        [SerializeField] private bool brushEraseMode; // false = 除外ペイント、true = 除外消去
        private bool isPainting;
        private Vector2 lastPaintUV = -Vector2.one;

        // マスクオーバーレイ（テクスチャは都度再構築するのでシリアライズ不要）
        // 共通マスク用（赤）とゾーン合成用（ゾーン色分け）の 2 枚。
        private Texture2D maskOverlayTexture;
        private Texture2D zoneMaskOverlayTexture;
        private bool maskDirty = true;

        [SerializeField] private bool maskFoldout = true;

        // 除外マスク元に戻す履歴（最大30ステップ）
        // ターゲットキー: "__common__" または zone.id
        private readonly List<MaskUndoEntry> _undoMaskHistory = new List<MaskUndoEntry>();
        private bool _maskStrokeStarted;
        private const int UndoMaskLimit = 30;

        // 共通マスク用のターゲットキー（SessionState キーにも使う）
        private const string CommonMaskKey = "__common__";

        private struct MaskUndoEntry
        {
            public string targetKey; // CommonMaskKey or zone.id
            public bool[] snapshot;  // null = マスク未確保状態
        }

        // マスクペイントモード: ブラシストロークが機能する前に明示的にアクティベートされる必要があります
        private bool maskPaintActive;

        // ─────────────────────── 除外マスク UI ───────────────────────

        private void DrawMaskSection()
        {
            maskFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(maskFoldout, Localization.ExclusionMask);
            if (!maskFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            // 編集対象セレクタ（共通 / 各ゾーン）
            DrawMaskTargetSelector();

            brushSize = EditorGUILayout.IntSlider(
                new GUIContent(Localization.BrushSize, Localization.BrushSizeTooltip),
                brushSize, 1, 64);

            // Exclude / Include ボタン: 押すとペイントモードON+モード選択、同じボタン再押しでOFF
            bool excludeActive = maskPaintActive && !brushEraseMode;
            bool includeActive = maskPaintActive && brushEraseMode;

            EditorGUILayout.BeginHorizontal();
            var prevBg = GUI.backgroundColor;

            GUI.backgroundColor = excludeActive ? new Color(1f, 0.55f, 0.55f) : Color.white;
            if (GUILayout.Button(new GUIContent(Localization.Exclude, Localization.ExcludeTooltip), EditorStyles.miniButtonLeft))
            {
                if (excludeActive)
                    maskPaintActive = false;
                else
                { maskPaintActive = true; brushEraseMode = false; }
            }

            GUI.backgroundColor = includeActive ? new Color(0.55f, 1f, 0.55f) : Color.white;
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
                PushMaskUndo();
                ClearActiveMask();
                maskDirty = true;
                previewDirty = true;
            }

            EditorGUI.BeginDisabledGroup(_undoMaskHistory.Count == 0);
            if (GUILayout.Button(new GUIContent(Localization.UndoMask, Localization.UndoMaskTooltip)))
                UndoMaskStep();
            EditorGUI.EndDisabledGroup();

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
            EnsureAllZoneIds();

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
                Repaint();
            }
        }

        /// <summary>
        /// 現在の zones に対して id 未設定のものへ GUID を振る。
        /// </summary>
        private void EnsureAllZoneIds()
        {
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
                zones[i]?.EnsureId();
        }

        // ─────────────────────── マスク確保 ─────────────────────────

        /// <summary>
        /// 共通マスクおよび、必要ならアクティブゾーンのマスクを確保する。
        /// sourceTexture のサイズが変わった場合は両方再確保。
        /// </summary>
        private void EnsureMasks()
        {
            if (sourceTexture == null) return;
            int w = sourceTexture.width;
            int h = sourceTexture.height;

            if (exclusionMask == null || exclusionMask.Length != w * h)
            {
                maskWidth = w;
                maskHeight = h;
                exclusionMask = new bool[w * h];
                // 解像度が変わったのでゾーン別マスクは破棄する（齟齬防止）
                zoneMasks.Clear();
            }
        }

        /// <summary>
        /// 指定ゾーンのマスクを確保（存在しなければ新規作成）して返す。
        /// </summary>
        private bool[] EnsureZoneMask(string zoneId)
        {
            EnsureMasks();
            if (string.IsNullOrEmpty(zoneId)) return null;
            if (!zoneMasks.TryGetValue(zoneId, out var m) || m == null || m.Length != maskWidth * maskHeight)
            {
                m = new bool[maskWidth * maskHeight];
                zoneMasks[zoneId] = m;
            }
            return m;
        }

        /// <summary>
        /// 現在のアクティブターゲット（共通 or ゾーン）のマスク配列を返す。必要なら確保する。
        /// ターゲットがゾーンでも zones が範囲外になっている場合は共通にフォールバック。
        /// </summary>
        private bool[] GetActiveMaskArray()
        {
            EnsureMasks();
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
                return exclusionMask;

            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            return EnsureZoneMask(zone.id);
        }

        /// <summary>
        /// 現在アクティブなターゲットのキー（"__common__" or zone.id）を返す。
        /// </summary>
        private string GetActiveTargetKey()
        {
            if (activeMaskTarget < 0 || zones == null || activeMaskTarget >= zones.Count)
                return CommonMaskKey;
            var zone = zones[activeMaskTarget];
            zone.EnsureId();
            return zone.id;
        }

        /// <summary>
        /// アクティブターゲットのマスクをクリア（配列を null 化相当に戻す）。
        /// </summary>
        private void ClearActiveMask()
        {
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
        /// 呼び出し側は zones.RemoveAt(index) の直前に呼ぶこと。
        /// </summary>
        internal void OnZoneAboutToBeRemoved(int index)
        {
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

        private void PaintMask(Vector2 uvPos)
        {
            bool[] target = GetActiveMaskArray();
            if (target == null) return;

            int cx = Mathf.RoundToInt(uvPos.x * maskWidth);
            int cy = Mathf.RoundToInt(uvPos.y * maskHeight);

            // ブラシサイズは「プレビュー画像上のピクセル数」で設定されるため、
            // マスク座標系（フル解像度）に合わせてスケーリングする必要がある。
            float maskScale = maskWidth / (float)Mathf.Min(maskWidth, PreviewMaxSize);
            int r = Mathf.Max(1, Mathf.RoundToInt(brushSize * maskScale));

            bool value = !brushEraseMode; // true = 除外

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

        // ─────────────────────── マスクオーバーレイ ─────────────────────────

        /// <summary>
        /// 共通マスクとゾーン別マスクのオーバーレイテクスチャを再構築する。
        /// </summary>
        private void RebuildMaskOverlay(int width, int height)
        {
            // 共通マスクオーバーレイ（赤）
            if (exclusionMask == null)
            {
                if (maskOverlayTexture != null)
                {
                    DestroyImmediate(maskOverlayTexture);
                    maskOverlayTexture = null;
                }
            }
            else
            {
                if (maskOverlayTexture == null || maskOverlayTexture.width != width || maskOverlayTexture.height != height)
                {
                    if (maskOverlayTexture != null) DestroyImmediate(maskOverlayTexture);
                    maskOverlayTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    maskOverlayTexture.filterMode = FilterMode.Point;
                }

                var overlayPixels = new Color32[width * height];
                var excluded = new Color32(255, 60, 60, 80);
                var clear = new Color32(0, 0, 0, 0);
                for (int i = 0; i < overlayPixels.Length; i++)
                {
                    int x = i % width;
                    int y = i / width;
                    int mx = Mathf.Clamp(x * maskWidth / width, 0, maskWidth - 1);
                    int my = Mathf.Clamp(y * maskHeight / height, 0, maskHeight - 1);
                    overlayPixels[i] = exclusionMask[my * maskWidth + mx] ? excluded : clear;
                }
                maskOverlayTexture.SetPixels32(overlayPixels);
                maskOverlayTexture.Apply();
            }

            RebuildZoneMaskOverlay(width, height);
        }

        /// <summary>
        /// ゾーン別マスクを1枚のテクスチャに合成して描画する（ゾーンごとに色分け）。
        /// </summary>
        private void RebuildZoneMaskOverlay(int width, int height)
        {
            bool hasAny = false;
            if (zones != null)
            {
                foreach (var z in zones)
                {
                    if (z == null || string.IsNullOrEmpty(z.id)) continue;
                    if (zoneMasks.TryGetValue(z.id, out var zm) && zm != null) { hasAny = true; break; }
                }
            }

            if (!hasAny || maskWidth == 0 || maskHeight == 0)
            {
                if (zoneMaskOverlayTexture != null)
                {
                    DestroyImmediate(zoneMaskOverlayTexture);
                    zoneMaskOverlayTexture = null;
                }
                return;
            }

            if (zoneMaskOverlayTexture == null
                || zoneMaskOverlayTexture.width != width
                || zoneMaskOverlayTexture.height != height)
            {
                if (zoneMaskOverlayTexture != null) DestroyImmediate(zoneMaskOverlayTexture);
                zoneMaskOverlayTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                zoneMaskOverlayTexture.filterMode = FilterMode.Point;
            }

            var pixels = new Color32[width * height];
            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            for (int zi = 0; zi < zones.Count; zi++)
            {
                var zone = zones[zi];
                if (zone == null || string.IsNullOrEmpty(zone.id)) continue;
                if (!zoneMasks.TryGetValue(zone.id, out var zm) || zm == null) continue;
                Color32 c = OverlayColorForZone(zi);

                for (int i = 0; i < pixels.Length; i++)
                {
                    int x = i % width;
                    int y = i / width;
                    int mx = Mathf.Clamp(x * maskWidth / width, 0, maskWidth - 1);
                    int my = Mathf.Clamp(y * maskHeight / height, 0, maskHeight - 1);
                    if (zm[my * maskWidth + mx]) pixels[i] = c;
                }
            }

            zoneMaskOverlayTexture.SetPixels32(pixels);
            zoneMaskOverlayTexture.Apply();
        }

        /// <summary>
        /// ゾーンインデックスから黄金比ベースのオーバーレイ色を決定する。
        /// 彩度高め・明度高め・半透明。
        /// </summary>
        internal static Color32 OverlayColorForZone(int zoneIndex)
        {
            // 黄金比で色相を散らす
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

        // ───────────────────────── Mask Undo ────────────────────────────

        private void PushMaskUndo()
        {
            string key = GetActiveTargetKey();
            bool[] current = GetCurrentArrayForKey(key);
            bool[] snapshot = current != null ? (bool[])current.Clone() : null;
            _undoMaskHistory.Add(new MaskUndoEntry { targetKey = key, snapshot = snapshot });
            if (_undoMaskHistory.Count > UndoMaskLimit)
                _undoMaskHistory.RemoveAt(0);
        }

        private bool[] GetCurrentArrayForKey(string key)
        {
            if (key == CommonMaskKey) return exclusionMask;
            if (!string.IsNullOrEmpty(key) && zoneMasks.TryGetValue(key, out var zm)) return zm;
            return null;
        }

        private void SetArrayForKey(string key, bool[] arr)
        {
            if (key == CommonMaskKey)
            {
                exclusionMask = arr;
            }
            else if (!string.IsNullOrEmpty(key))
            {
                if (arr == null) zoneMasks.Remove(key);
                else zoneMasks[key] = arr;
            }
        }

        private void UndoMaskStep()
        {
            if (_undoMaskHistory.Count == 0) return;
            var entry = _undoMaskHistory[_undoMaskHistory.Count - 1];
            _undoMaskHistory.RemoveAt(_undoMaskHistory.Count - 1);

            SetArrayForKey(entry.targetKey, entry.snapshot);

            // アクティブターゲットも巻き戻した対象に合わせる
            if (entry.targetKey == CommonMaskKey)
            {
                activeMaskTarget = -1;
            }
            else if (zones != null)
            {
                for (int i = 0; i < zones.Count; i++)
                {
                    if (zones[i] != null && zones[i].id == entry.targetKey)
                    {
                        activeMaskTarget = i;
                        break;
                    }
                }
            }

            maskDirty = true;
            previewDirty = true;
            Repaint();
        }

        // ───────────────────────── Mask Persistence ────────────────────

        private string MaskSessionPathKey()
        {
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
        /// 現在の全マスク（共通 + 各ゾーン）を SessionState に保存する。
        /// 全 false のゾーンマスクは書き出さない。
        /// </summary>
        private void SaveMaskToSession()
        {
            string path = MaskSessionPathKey();
            if (path == null) return;

            // 旧キーは常にクリア（新フォーマットに移行）
            SessionState.EraseString(LegacyMaskSessionKey(path));

            var idx = new MaskIndex { width = maskWidth, height = maskHeight };

            // 共通マスク
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

            // ゾーン別マスク
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
        /// SessionState から全マスクを復元する。旧フォーマット（単一マスク）しか無い場合は
        /// 共通マスクへ移行してから新フォーマットで保存し直す。
        /// </summary>
        private void RestoreMaskFromSession()
        {
            string path = MaskSessionPathKey();
            if (path == null) return;

            exclusionMask = null;
            zoneMasks.Clear();

            string indexJson = SessionState.GetString(MaskIndexSessionKey(path), null);
            if (!string.IsNullOrEmpty(indexJson))
            {
                MaskIndex idx = null;
                try { idx = JsonUtility.FromJson<MaskIndex>(indexJson); } catch { idx = null; }
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

            // 新フォーマットで保存し直し、旧キーは消す
            SessionState.EraseString(LegacyMaskSessionKey(path));
            SaveMaskToSession();
            maskDirty = true;
        }

        private static bool AnyTrue(bool[] arr)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++) if (arr[i]) return true;
            return false;
        }

        /// <summary>
        /// bool 配列を「4byte W + 4byte H + bitpacked body」の Base64 文字列にエンコード。
        /// </summary>
        internal static string EncodeMask(bool[] mask, int w, int h)
        {
            if (mask == null || mask.Length == 0) return "";
            int len = mask.Length;
            int byteLen = (len + 7) / 8;
            byte[] packed = new byte[byteLen + 8];
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(w), 0, packed, 0, 4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(h), 0, packed, 4, 4);
            for (int i = 0; i < len; i++)
            {
                if (mask[i])
                    packed[8 + i / 8] |= (byte)(1 << (i % 8));
            }
            return System.Convert.ToBase64String(packed);
        }

        /// <summary>
        /// EncodeMask の逆。デコード失敗時は null を返す。
        /// </summary>
        internal static bool[] DecodeMask(string encoded, out int w, out int h)
        {
            w = 0; h = 0;
            if (string.IsNullOrEmpty(encoded)) return null;
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
            catch
            {
                return null;
            }
        }

        // ───────────────────────── Processing 用スナップショット ────────────────────

        /// <summary>
        /// バックグラウンド処理に渡すためのマスク一式のイミュータブルスナップショット。
        /// 配列は deep clone 済み（呼び出し側の書き換えと競合しない）。
        /// </summary>
        internal class MaskSnapshot
        {
            public bool[] common;
            public int width;
            public int height;
            public Dictionary<string, bool[]> zones;
        }

        /// <summary>
        /// 現在のマスク状態をスナップショット化する（deep clone）。
        /// </summary>
        private MaskSnapshot BuildMaskSnapshot()
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
    }
}
