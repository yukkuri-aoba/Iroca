using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // 除外マスク（フル解像度、true = 除外）
        private bool[] exclusionMask;
        private int maskWidth, maskHeight;
        private int brushSize = 8;
        private bool brushEraseMode; // false = 除外ペイント、true = 除外消去
        private bool isPainting;
        private Vector2 lastPaintUV = -Vector2.one;

        // マスクオーバーレイ
        private Texture2D maskOverlayTexture;
        private bool maskDirty = true;

        private bool maskFoldout = true;

        // 除外マスク元に戻す履歴（最大30ステップ）
        private readonly List<bool[]> _undoMaskHistory = new List<bool[]>();
        private bool _maskStrokeStarted;
        private const int UndoMaskLimit = 30;

        // マスクペイントモード: ブラシストロークが機能する前に明示的にアクティベートされる必要があります
        private bool maskPaintActive;

        // ─────────────────────── 除外マスク ─────────────────────────

        private void DrawMaskSection()
        {
            maskFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(maskFoldout, Localization.ExclusionMask);
            if (!maskFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

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
                exclusionMask = null;
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

        private void EnsureMask()
        {
            if (sourceTexture == null) return;
            if (exclusionMask == null || exclusionMask.Length != sourceTexture.width * sourceTexture.height)
            {
                maskWidth = sourceTexture.width;
                maskHeight = sourceTexture.height;
                exclusionMask = new bool[maskWidth * maskHeight];
            }
        }

        private void PaintMask(Vector2 uvPos)
        {
            EnsureMask();
            int cx = Mathf.RoundToInt(uvPos.x * maskWidth);
            int cy = Mathf.RoundToInt(uvPos.y * maskHeight);
            // ブラシサイズをプレビューピクセルからマスクピクセルにスケール
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
                    exclusionMask[py * maskWidth + px] = value;
                }
            }

            maskDirty = true;
        }

        private bool IsExcluded(int x, int y, int texWidth, int texHeight)
        {
            if (exclusionMask == null) return false;
            int mx = Mathf.Clamp(x * maskWidth / texWidth, 0, maskWidth - 1);
            int my = Mathf.Clamp(y * maskHeight / texHeight, 0, maskHeight - 1);
            return exclusionMask[my * maskWidth + mx];
        }

        // ─────────────────────── マスクオーバーレイ ─────────────────────────

        private void RebuildMaskOverlay(int width, int height)
        {
            if (exclusionMask == null)
            {
                if (maskOverlayTexture != null)
                {
                    DestroyImmediate(maskOverlayTexture);
                    maskOverlayTexture = null;
                }
                return;
            }

            if (maskOverlayTexture == null || maskOverlayTexture.width != width || maskOverlayTexture.height != height)
            {
                if (maskOverlayTexture != null)
                    DestroyImmediate(maskOverlayTexture);
                maskOverlayTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                maskOverlayTexture.filterMode = FilterMode.Point;
            }

            var overlayPixels = new Color32[width * height];
            var excluded = new Color32(255, 60, 60, 80);  // semi-transparent red
            var clear = new Color32(0, 0, 0, 0);

            for (int i = 0; i < overlayPixels.Length; i++)
            {
                int x = i % width;
                int y = i / width;
                overlayPixels[i] = IsExcluded(x, y, width, height) ? excluded : clear;
            }

            maskOverlayTexture.SetPixels32(overlayPixels);
            maskOverlayTexture.Apply();
        }

        // ───────────────────────── Mask Undo ────────────────────────────

        private void PushMaskUndo()
        {
            bool[] snapshot = exclusionMask != null ? (bool[])exclusionMask.Clone() : null;
            _undoMaskHistory.Add(snapshot);
            if (_undoMaskHistory.Count > UndoMaskLimit)
                _undoMaskHistory.RemoveAt(0);
        }

        private void UndoMaskStep()
        {
            if (_undoMaskHistory.Count == 0) return;
            exclusionMask = _undoMaskHistory[_undoMaskHistory.Count - 1];
            _undoMaskHistory.RemoveAt(_undoMaskHistory.Count - 1);
            maskDirty = true;
            previewDirty = true;
            Repaint();
        }

        // ───────────────────────── Mask Persistence ────────────────────

        private string MaskSessionKey()
        {
            if (sourceTexture == null) return null;
            string path = AssetDatabase.GetAssetPath(sourceTexture);
            return string.IsNullOrEmpty(path) ? null : "VACC_Mask_" + path;
        }

        private void SaveMaskToSession()
        {
            string key = MaskSessionKey();
            if (key == null) return;

            if (exclusionMask == null || exclusionMask.Length == 0)
            {
                SessionState.EraseString(key);
                return;
            }

            // Store dimensions + bitpacked mask
            int len = exclusionMask.Length;
            int byteLen = (len + 7) / 8;
            byte[] packed = new byte[byteLen + 8]; // 4bytes W + 4bytes H + data

            System.Buffer.BlockCopy(System.BitConverter.GetBytes(maskWidth),  0, packed, 0, 4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(maskHeight), 0, packed, 4, 4);

            for (int i = 0; i < len; i++)
            {
                if (exclusionMask[i])
                    packed[8 + i / 8] |= (byte)(1 << (i % 8));
            }

            SessionState.SetString(key, System.Convert.ToBase64String(packed));
        }

        private void RestoreMaskFromSession()
        {
            string key = MaskSessionKey();
            if (key == null) return;

            string encoded = SessionState.GetString(key, null);
            if (string.IsNullOrEmpty(encoded)) return;

            try
            {
                byte[] packed = System.Convert.FromBase64String(encoded);
                if (packed.Length < 9) return;

                int w = System.BitConverter.ToInt32(packed, 0);
                int h = System.BitConverter.ToInt32(packed, 4);
                if (w <= 0 || h <= 0) return;

                int len = w * h;
                bool[] mask = new bool[len];
                for (int i = 0; i < len; i++)
                    mask[i] = (packed[8 + i / 8] & (1 << (i % 8))) != 0;

                maskWidth  = w;
                maskHeight = h;
                exclusionMask = mask;
                maskDirty = true;
            }
            catch
            {
                // 破損データは無視
                SessionState.EraseString(key);
            }
        }
    }
}
