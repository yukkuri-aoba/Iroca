// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Camereo
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Camereo.DebugTools
{
    /// <summary>
    /// パイプライン透明化の詳細可視化を担う独立 EditorWindow。
    /// Camereo 本体ウィンドウとは別にサイズ変更・ドッキングできる。
    /// 表示元のキャプチャは <see cref="DebugView.LatestContext"/> から読み出す。
    /// </summary>
    internal sealed class DebugWindow : EditorWindow
    {
        internal enum Mode
        {
            Strength,
            Delta,
            Ownership,
            RecolorBranch,
        }

        // ── EditorPrefs キー ───────────────────────────
        private const string PrefKeyStage = "Camereo.Debug.SelectedStage";
        private const string PrefKeyZone = "Camereo.Debug.SelectedZone";
        private const string PrefKeyMode = "Camereo.Debug.Mode";

        // ── ウィンドウ自身が保持する選択状態 ──────────
        [SerializeField] private int _selectedStageIndex;
        [SerializeField] private string _selectedZoneId = "";
        [SerializeField] private Mode _mode;
        [SerializeField] private Vector2 _scrollPos;

        // ── 表示用テクスチャ ─────────────────────────
        [System.NonSerialized] private Texture2D _overlayTexture;
        // overlay 再生成判定用に「最後にどのキャプチャを描いたか」を覚えておく。
        [System.NonSerialized] private DebugCaptureContext _overlayBuiltFrom;
        [System.NonSerialized] private int _overlayBuiltSnapshotCount;
        [System.NonSerialized] private string _overlayBuiltKey = "";

        [MenuItem("Window/Camereo/Debug Visualization", priority = 200)]
        public static void OpenOrFocus()
        {
            var win = GetWindow<DebugWindow>(utility: false, title: "Camereo Debug", focus: true);
            win.minSize = new Vector2(400, 320);
            win.Show();
        }

        /// <summary>
        /// <see cref="DebugView"/> がキャプチャを破棄した際の通知。
        /// 既存ウィンドウがあればオーバーレイをリセットして再描画する。
        /// </summary>
        public static void NotifyCaptureCleared()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<DebugWindow>())
            {
                w.DisposeOverlay();
                w.Repaint();
            }
        }

        private void OnEnable()
        {
            _selectedStageIndex = EditorPrefs.GetInt(PrefKeyStage, 0);
            _selectedZoneId = EditorPrefs.GetString(PrefKeyZone, "");
            _mode = (Mode)EditorPrefs.GetInt(PrefKeyMode, (int)Mode.Strength);
            titleContent = new GUIContent("Camereo Debug",
                EditorGUIUtility.IconContent("d_Profiler.UIDetails").image);
        }

        private void OnDisable()
        {
            DisposeOverlay();
        }

        private void OnGUI()
        {
            if (!DebugView.IsCaptureEnabled)
            {
                EditorGUILayout.HelpBox(
                    "デバッグキャプチャが無効化されています。\nCamereo ウィンドウで「デバッグキャプチャを有効化」をオンにしてからプレビューを再生成してください。",
                    MessageType.Info);
                return;
            }

            var ctx = DebugView.LatestContext;
            if (ctx == null || ctx.Snapshots.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "キャプチャがまだありません。Camereo ウィンドウで設定を変えるかプレビューを再生成してください。",
                    MessageType.Info);
                return;
            }

            DrawToolbar(ctx);
            DrawSelectors(ctx);
            DrawOverlay(ctx);
            DrawDumpButton(ctx);
        }

        // ──────────────────────────────────────────────
        private void DrawToolbar(DebugCaptureContext ctx)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"capture: {ctx.Snapshots.Count} snapshots × {ctx.Width}x{ctx.Height}",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("再描画", "オーバーレイを最新キャプチャから再構築します。"),
                        EditorStyles.toolbarButton))
                {
                    DisposeOverlay();
                    Repaint();
                }
            }
        }

        private void DrawSelectors(DebugCaptureContext ctx)
        {
            var zoneIds = ctx.CollectZoneIds();
            if (zoneIds.Count == 0)
            {
                EditorGUILayout.HelpBox("有効な zone がありません。", MessageType.Info);
                return;
            }
            int zoneIdx = Mathf.Max(0, zoneIds.IndexOf(_selectedZoneId));

            EditorGUI.BeginChangeCheck();
            zoneIdx = EditorGUILayout.Popup(
                new GUIContent("Zone", "どの zone のキャプチャを表示するか選択します。"),
                zoneIdx, zoneIds.ConvertAll(s => new GUIContent(s)).ToArray());
            if (EditorGUI.EndChangeCheck())
            {
                _selectedZoneId = zoneIds[zoneIdx];
                EditorPrefs.SetString(PrefKeyZone, _selectedZoneId);
                DisposeOverlay();
            }
            _selectedZoneId = zoneIds[zoneIdx];

            EditorGUI.BeginChangeCheck();
            _mode = (Mode)EditorGUILayout.EnumPopup(
                new GUIContent("Mode",
                    "Strength: 各段階の strength マップ（グレースケール）。\n" +
                    "Delta: 直前段階との差分（緑=追加 / 赤=削除、振幅は ×4 増幅）。\n" +
                    "Ownership: ピクセルごとに最初に strength≥0.5 に達した段階を色分け。\n" +
                    "RecolorBranch: Recolor 段で適用されたサブブランチ（base / highlight / shadow / decontam）。"),
                _mode);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PrefKeyMode, (int)_mode);
                DisposeOverlay();
            }

            bool needStageSelector = _mode == Mode.Strength || _mode == Mode.Delta;
            if (needStageSelector)
            {
                var stageNames = BuildAvailableStageNames(ctx, _selectedZoneId);
                if (stageNames.Count == 0)
                {
                    EditorGUILayout.HelpBox("この zone にはキャプチャがありません。", MessageType.Info);
                    return;
                }
                _selectedStageIndex = Mathf.Clamp(_selectedStageIndex, 0, stageNames.Count - 1);
                EditorGUI.BeginChangeCheck();
                _selectedStageIndex = EditorGUILayout.Popup(
                    new GUIContent("Stage", "どのパイプライン段階のキャプチャを表示するか選択します。"),
                    _selectedStageIndex, stageNames.ToArray());
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetInt(PrefKeyStage, _selectedStageIndex);
                    DisposeOverlay();
                }
            }
        }

        private void DrawOverlay(DebugCaptureContext ctx)
        {
            EnsureOverlayUpToDate(ctx);

            if (_overlayTexture == null)
            {
                EditorGUILayout.HelpBox(
                    _mode == Mode.Delta
                        ? "この段階には差分情報がありません（最初の段階 or 差分計算に必要な直前段階がない）。"
                        : "この zone × 段階のキャプチャが見つかりません。",
                    MessageType.Info);
                return;
            }

            // ウィンドウ幅・高さに合わせて最大サイズで表示。アスペクト比は保持。
            EditorGUILayout.LabelField(BuildLegend(_mode), EditorStyles.miniLabel);

            float availableW = position.width - 20f;
            float availableH = position.height - 200f; // toolbar/popups/legend/dump 分の概算
            if (availableH < 200f) availableH = 200f;

            float aspect = (float)_overlayTexture.height / _overlayTexture.width;
            float drawW = availableW;
            float drawH = drawW * aspect;
            if (drawH > availableH) { drawH = availableH; drawW = drawH / aspect; }

            // スクロール領域内に置くことで、巨大テクスチャでも全体を覗ける。
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));
            var rect = GUILayoutUtility.GetRect(drawW, drawH);
            EditorGUI.DrawPreviewTexture(rect, _overlayTexture, null, ScaleMode.ScaleToFit);
            EditorGUILayout.EndScrollView();
        }

        private void DrawDumpButton(DebugCaptureContext ctx)
        {
            EditorGUILayout.Space(4);
            if (GUILayout.Button(new GUIContent(
                    "Dump all stages to PNG",
                    "全 zone × 全段階のキャプチャを Library/Camereo/Debug/<source>/<timestamp>/ 配下に PNG として書き出します。manifest.json も併せて生成されます。\nProject ビューには表示されません（Assets/ 外に保存）。書き出し後にフォルダをエクスプローラーで開きます。")))
            {
                var vaccWin = Resources.FindObjectsOfTypeAll<CamereoWindow>().Length > 0
                    ? Resources.FindObjectsOfTypeAll<CamereoWindow>()[0]
                    : null;
                string srcName = vaccWin != null && vaccWin.SourceTexture != null
                    ? vaccWin.SourceTexture.name : "unknown";
                string dumpPath = DebugDumpStore.DumpAll(ctx, srcName);
                if (!string.IsNullOrEmpty(dumpPath))
                {
                    ShowNotification(new GUIContent("Dumped to Library/Camereo/Debug/\n(エクスプローラーで開きます)"));
                    EditorUtility.RevealInFinder(dumpPath);
                }
                else
                {
                    ShowNotification(new GUIContent("Dump failed (see Console)."));
                }
            }
        }

        // ─── オーバーレイ構築 ─────────────────────────
        private void EnsureOverlayUpToDate(DebugCaptureContext ctx)
        {
            string key = _selectedZoneId + "|" + (int)_mode + "|" + _selectedStageIndex;
            if (_overlayTexture != null
                && _overlayBuiltFrom == ctx
                && _overlayBuiltSnapshotCount == ctx.Snapshots.Count
                && _overlayBuiltKey == key)
                return;

            DisposeOverlay();
            _overlayTexture = BuildOverlay(ctx, _selectedZoneId, _mode, _selectedStageIndex);
            _overlayBuiltFrom = ctx;
            _overlayBuiltSnapshotCount = ctx.Snapshots.Count;
            _overlayBuiltKey = key;
        }

        private void DisposeOverlay()
        {
            if (_overlayTexture != null)
            {
                DestroyImmediate(_overlayTexture);
                _overlayTexture = null;
            }
            _overlayBuiltFrom = null;
            _overlayBuiltSnapshotCount = 0;
            _overlayBuiltKey = "";
        }

        private static List<string> BuildAvailableStageNames(DebugCaptureContext ctx, string zoneId)
        {
            var result = new List<string>();
            foreach (var snap in ctx.Snapshots)
                if (snap.zoneId == zoneId) result.Add(snap.stageName);
            return result;
        }

        private static Texture2D BuildOverlay(DebugCaptureContext ctx, string zoneId, Mode mode, int stageIndex)
        {
            switch (mode)
            {
                case Mode.Strength:    return BuildStrengthTexture(ctx, zoneId, stageIndex, useDelta: false);
                case Mode.Delta:       return BuildStrengthTexture(ctx, zoneId, stageIndex, useDelta: true);
                case Mode.Ownership:   return BuildOwnershipTexture(ctx, zoneId);
                case Mode.RecolorBranch: return BuildRecolorBranchTexture(ctx, zoneId);
            }
            return null;
        }

        private static Texture2D BuildStrengthTexture(DebugCaptureContext ctx, string zoneId, int stageIndex, bool useDelta)
        {
            int idx = 0;
            StageSnapshot picked = null;
            foreach (var snap in ctx.Snapshots)
            {
                if (snap.zoneId != zoneId) continue;
                if (idx == stageIndex) { picked = snap; break; }
                idx++;
            }
            if (picked == null) return null;

            byte[] src = useDelta ? picked.deltaQuantized : picked.strengthQuantized;
            if (src == null) return null; // delta が無い → 上位で「差分なし」HelpBox を表示

            int w = picked.width, h = picked.height;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false) { filterMode = FilterMode.Point };
            var pixels = new Color32[w * h];

            if (useDelta)
            {
                // 振幅 ×4 で増幅。背景（差分 0）は純黒にして変化を強調。
                for (int i = 0; i < pixels.Length; i++)
                {
                    int signed = src[i] - 128;
                    if (signed > 0)
                    {
                        byte mag = (byte)Mathf.Min(255, signed * 4);
                        pixels[i] = new Color32(0, mag, 0, 255);
                    }
                    else if (signed < 0)
                    {
                        byte mag = (byte)Mathf.Min(255, -signed * 4);
                        pixels[i] = new Color32(mag, 0, 0, 255);
                    }
                    else
                    {
                        pixels[i] = new Color32(0, 0, 0, 255);
                    }
                }
            }
            else
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte v = src[i];
                    pixels[i] = new Color32(v, v, v, 255);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static Texture2D BuildOwnershipTexture(DebugCaptureContext ctx, string zoneId)
        {
            var snaps = new List<StageSnapshot>();
            foreach (var snap in ctx.Snapshots)
                if (snap.zoneId == zoneId) snaps.Add(snap);
            if (snaps.Count == 0) return null;

            int w = snaps[0].width, h = snaps[0].height;
            int len = w * h;
            byte[] owner = new byte[len];
            for (int i = 0; i < len; i++) owner[i] = 255;
            for (int sIdx = 0; sIdx < snaps.Count; sIdx++)
            {
                var s = snaps[sIdx];
                if (s.strengthQuantized == null || s.strengthQuantized.Length != len) continue;
                for (int i = 0; i < len; i++)
                {
                    if (owner[i] == 255 && s.strengthQuantized[i] >= 128) owner[i] = (byte)sIdx;
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false) { filterMode = FilterMode.Point };
            var pixels = new Color32[len];
            for (int i = 0; i < len; i++)
            {
                if (owner[i] == 255) { pixels[i] = new Color32(0, 0, 0, 0); continue; }
                pixels[i] = StageColor(owner[i], snaps.Count);
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static Texture2D BuildRecolorBranchTexture(DebugCaptureContext ctx, string zoneId)
        {
            if (!ctx.BranchMaps.TryGetValue(zoneId, out var branchMap)) return null;
            var snap = ctx.FindSnapshot(zoneId, DebugStages.Recolor);
            if (snap == null) return null;
            int w = snap.width, h = snap.height;
            int len = w * h;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false) { filterMode = FilterMode.Point };
            var pixels = new Color32[len];
            for (int i = 0; i < len; i++)
            {
                switch ((DebugBranch)branchMap[i])
                {
                    case DebugBranch.Base:          pixels[i] = new Color32(0, 200, 200, 255); break;
                    case DebugBranch.Highlight:     pixels[i] = new Color32(255, 160, 0, 255); break;
                    case DebugBranch.Shadow:        pixels[i] = new Color32(160, 60, 200, 255); break;
                    case DebugBranch.Decontaminate: pixels[i] = new Color32(255, 230, 0, 255); break;
                    default:                        pixels[i] = new Color32(0, 0, 0, 0); break;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static Color32 StageColor(int stageIdx, int totalStages)
        {
            float hue = (stageIdx / (float)Mathf.Max(1, totalStages)) % 1f;
            Color c = Color.HSVToRGB(hue, 0.8f, 0.95f);
            return new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
        }

        private static string BuildLegend(Mode mode)
        {
            switch (mode)
            {
                case Mode.Strength:      return "凡例: 白 = 強度 1.0 / 黒 = 0.0";
                case Mode.Delta:         return "凡例: 緑 = 強度上昇 / 赤 = 強度低下 / 黒 = 変化なし（振幅 ×4 増幅）";
                case Mode.Ownership:     return "凡例: ピクセルごとに最初に strength≥0.5 を達成した段階を hue 円で色分け（透明 = 未到達）";
                case Mode.RecolorBranch: return "凡例: シアン=Base / オレンジ=Highlight / 紫=Shadow / 黄=Decontaminate";
            }
            return "";
        }
    }
}
