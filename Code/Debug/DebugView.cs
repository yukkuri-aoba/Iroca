using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger.DebugTools
{
    /// <summary>
    /// VACCWindow に追加されるパイプライン透明化用 Foldout。
    /// この型は <see cref="DebugBootstrap"/> から
    /// <see cref="DebugCaptureHooks.OnDrawFoldout"/> に subscribe され、
    /// プロセス内グローバルな状態として振る舞う（VACCWindow は EditorWindow 1 個前提）。
    /// </summary>
    internal static class DebugView
    {
        internal enum Mode
        {
            Strength,
            Delta,
            Ownership,
            RecolorBranch,
        }

        // ── UI 状態（EditorPrefs で永続化） ───────────────────────────
        private const string PrefKeyEnabled = "VACC.Debug.EnableCapture";
        private const string PrefKeyFoldout = "VACC.Debug.Foldout";
        private const string PrefKeyStage = "VACC.Debug.SelectedStage";
        private const string PrefKeyZone = "VACC.Debug.SelectedZone";
        private const string PrefKeyMode = "VACC.Debug.Mode";

        private static bool s_enableCapture;
        private static bool s_foldout;
        private static int s_selectedStageIndex;
        private static string s_selectedZoneId = "";
        private static Mode s_mode;
        private static bool s_loadedPrefs;

        // ── キャプチャ状態 ────────────────────────────────────────
        private static DebugCaptureContext s_activeContext;
        // 表示用テクスチャ（オーバーレイ）。
        private static Texture2D s_overlayTexture;

        /// <summary>
        /// <see cref="DebugCaptureHooks.Factory"/> から呼ばれる。
        /// トグル OFF なら null を返してパイプラインを完全 no-op にする。
        /// </summary>
        internal static IDebugCapture CurrentCaptureOrNull()
        {
            EnsurePrefsLoaded();
            if (!s_enableCapture) return null;
            // 毎プレビュー / エクスポートで新しいインスタンスを作って渡す。
            // 古いコンテキストは GC に任せる（snapshot byte[] が破棄されるまで）。
            var ctx = new DebugCaptureContext();
            s_activeContext = ctx;
            return ctx;
        }

        /// <summary>
        /// VACCWindow.OnGUI 末尾から発火される。本体 UI に影響しない位置で foldout を描画する。
        /// </summary>
        internal static void Draw(VACCWindow host)
        {
            EnsurePrefsLoaded();

            EditorGUILayout.Space(6);

            // セクション枠（背景つき）でデバッグ機能をひと目で見分けられるようにする。
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // ── キャプチャ有効化トグル（foldout の外、常時表示） ──
                // デバッグ asmdef がインストールされていることを示す目印として、
                // ヘッダー兼チェックボックスを常に出す。
                EditorGUI.BeginChangeCheck();
                bool prevEnabled = s_enableCapture;
                s_enableCapture = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "デバッグキャプチャを有効化 (パイプライン透明化)",
                        "オンにすると、次回プレビュー/エクスポート時に各パイプライン段階の strength マップ・差分・Recolor サブブランチを採取します。\nオフでは何もキャプチャされず、本体パイプラインに一切のオーバーヘッドはありません。"),
                    s_enableCapture, EditorStyles.boldLabel);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetBool(PrefKeyEnabled, s_enableCapture);
                    if (s_enableCapture != prevEnabled)
                    {
                        // 有効化 / 無効化が切り替わったタイミングで次回プレビューを再キャプチャするため
                        // プレビューをダーティ化する。
                        host.MarkPreviewDirty();
                        // OFF にしたら現状のキャプチャをすぐ破棄して foldout 内の表示も消す。
                        if (!s_enableCapture)
                        {
                            s_activeContext = null;
                            host.LatestDebugCapture = null;
                            DisposeOverlay();
                        }
                    }
                }

                if (!s_enableCapture)
                {
                    EditorGUILayout.LabelField(
                        "オフ: 本体パイプラインに何もフックされていません。",
                        EditorStyles.miniLabel);
                    return;
                }

                EditorGUI.BeginChangeCheck();
                s_foldout = EditorGUILayout.Foldout(s_foldout, "可視化と PNG ダンプ", true);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetBool(PrefKeyFoldout, s_foldout);
                }
                if (!s_foldout) return;

                // ── キャプチャがまだない場合のヘルプ ───────────────
                // host.LatestDebugCapture はインターフェース型なので、Debug 側では具体型に再キャストして読む。
                var ctx = host.LatestDebugCapture as DebugCaptureContext ?? s_activeContext;
                if (ctx == null || ctx.Snapshots.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "プレビューを再生成するとキャプチャされます（テクスチャやゾーン設定を変更してください）。",
                        MessageType.Info);
                    return;
                }

                // ── Zone セレクタ ─────────────────────────────────
                var zoneIds = ctx.CollectZoneIds();
                if (zoneIds.Count == 0)
                {
                    EditorGUILayout.HelpBox("有効な zone がありません。", MessageType.Info);
                    return;
                }
                int zoneIdx = Mathf.Max(0, zoneIds.IndexOf(s_selectedZoneId));
                EditorGUI.BeginChangeCheck();
                zoneIdx = EditorGUILayout.Popup(
                    new GUIContent("Zone", "どの zone のキャプチャを表示するか選択します。"),
                    zoneIdx, zoneIds.ConvertAll(s => new GUIContent(s)).ToArray());
                if (EditorGUI.EndChangeCheck())
                {
                    s_selectedZoneId = zoneIds[zoneIdx];
                    EditorPrefs.SetString(PrefKeyZone, s_selectedZoneId);
                    DisposeOverlay();
                }
                s_selectedZoneId = zoneIds[zoneIdx];

                // ── Mode セレクタ ─────────────────────────────────
                EditorGUI.BeginChangeCheck();
                s_mode = (Mode)EditorGUILayout.EnumPopup(
                    new GUIContent("Mode",
                        "Strength: 各段階の strength マップ（グレースケール）。\n" +
                        "Delta: 直前段階との差分（緑=追加 / 赤=削除）。\n" +
                        "Ownership: ピクセルごとに最後に変更を加えた段階を色分け。\n" +
                        "RecolorBranch: Recolor 段で適用されたサブブランチ（base / highlight / shadow / decontam）。"),
                    s_mode);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetInt(PrefKeyMode, (int)s_mode);
                    DisposeOverlay();
                }

                // ── Stage セレクタ（Strength / Delta のみ使用） ──
                bool needStageSelector = s_mode == Mode.Strength || s_mode == Mode.Delta;
                if (needStageSelector)
                {
                    var stageNames = BuildAvailableStageNames(ctx, s_selectedZoneId);
                    if (stageNames.Count == 0)
                    {
                        EditorGUILayout.HelpBox("この zone にはキャプチャがありません。", MessageType.Info);
                        return;
                    }
                    s_selectedStageIndex = Mathf.Clamp(s_selectedStageIndex, 0, stageNames.Count - 1);
                    EditorGUI.BeginChangeCheck();
                    s_selectedStageIndex = EditorGUILayout.Popup(
                        new GUIContent("Stage", "どのパイプライン段階のキャプチャを表示するか選択します。"),
                        s_selectedStageIndex, stageNames.ToArray());
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorPrefs.SetInt(PrefKeyStage, s_selectedStageIndex);
                        DisposeOverlay();
                    }
                }

                // ── オーバーレイテクスチャを構築/再利用 ────────────
                if (s_overlayTexture == null)
                {
                    s_overlayTexture = BuildOverlay(ctx, s_selectedZoneId, s_mode, s_selectedStageIndex);
                }
                if (s_overlayTexture != null)
                {
                    // アスペクト比を保ったまま foldout 幅にフィットさせる。
                    float maxW = EditorGUIUtility.currentViewWidth - 40f;
                    float aspect = (float)s_overlayTexture.height / s_overlayTexture.width;
                    float drawW = Mathf.Min(maxW, 480f);
                    float drawH = drawW * aspect;
                    var rect = GUILayoutUtility.GetRect(drawW, drawH);
                    EditorGUI.DrawPreviewTexture(rect, s_overlayTexture, null, ScaleMode.ScaleToFit);

                    // 凡例
                    EditorGUILayout.LabelField(BuildLegend(s_mode), EditorStyles.miniLabel);
                }

                EditorGUILayout.Space(4);

                if (GUILayout.Button(new GUIContent(
                        "Dump all stages to PNG",
                        "全 zone × 全段階のキャプチャを Assets/VACC/Debug/<source>/<timestamp>/ 配下に PNG として書き出します。manifest.json も併せて生成され、Unity のプロジェクトビューから直接参照できます。")))
                {
                    string srcName = host.SourceTexture != null ? host.SourceTexture.name : "unknown";
                    string assetsRel = DebugDumpStore.DumpAll(ctx, srcName);
                    if (!string.IsNullOrEmpty(assetsRel))
                    {
                        host.ShowNotification(new GUIContent($"Dumped to:\n{assetsRel}"));
                        // 書き出し先フォルダを Project ビューでハイライト＆フォーカス
                        var folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetsRel);
                        if (folder != null)
                        {
                            EditorGUIUtility.PingObject(folder);
                            Selection.activeObject = folder;
                        }
                    }
                    else
                    {
                        host.ShowNotification(new GUIContent("Dump failed (see Console)."));
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────
        private static void EnsurePrefsLoaded()
        {
            if (s_loadedPrefs) return;
            s_loadedPrefs = true;
            s_enableCapture = EditorPrefs.GetBool(PrefKeyEnabled, false);
            s_foldout = EditorPrefs.GetBool(PrefKeyFoldout, false);
            s_selectedStageIndex = EditorPrefs.GetInt(PrefKeyStage, 0);
            s_selectedZoneId = EditorPrefs.GetString(PrefKeyZone, "");
            s_mode = (Mode)EditorPrefs.GetInt(PrefKeyMode, (int)Mode.Strength);
        }

        private static void DisposeOverlay()
        {
            if (s_overlayTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(s_overlayTexture);
                s_overlayTexture = null;
            }
        }

        private static List<string> BuildAvailableStageNames(DebugCaptureContext ctx, string zoneId)
        {
            var result = new List<string>();
            foreach (var snap in ctx.Snapshots)
            {
                if (snap.zoneId == zoneId) result.Add(snap.stageName);
            }
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

            int w = picked.width, h = picked.height;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false) { filterMode = FilterMode.Point };
            var pixels = new Color32[w * h];

            byte[] src = useDelta ? picked.deltaQuantized : picked.strengthQuantized;
            if (src == null)
            {
                // delta が無い（最初のステージ）→ 一様グレーで埋める
                var fill = useDelta ? new Color32(64, 64, 64, 255) : new Color32(0, 0, 0, 255);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            }
            else if (useDelta)
            {
                // 緑=増加、赤=減少
                for (int i = 0; i < pixels.Length; i++)
                {
                    int signed = src[i] - 128;
                    if (signed > 0)
                    {
                        byte mag = (byte)Mathf.Min(255, signed * 2);
                        pixels[i] = new Color32(0, mag, 0, 255);
                    }
                    else if (signed < 0)
                    {
                        byte mag = (byte)Mathf.Min(255, -signed * 2);
                        pixels[i] = new Color32(mag, 0, 0, 255);
                    }
                    else
                    {
                        pixels[i] = new Color32(32, 32, 32, 255);
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

            // Unity の Texture2D は左下原点だが、キャプチャは行優先 (y=0 が上)。
            // 縦反転して描画時に正しい向きになるようにする。
            FlipVertical(pixels, w, h);
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static Texture2D BuildOwnershipTexture(DebugCaptureContext ctx, string zoneId)
        {
            // 各ピクセルで「最後に strength を 0.5 以上に押し上げた段階」を色分けする。
            // 段階を巡回して、しきい値を初めて超えたタイミングで stage index を記録する方式。
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
            FlipVertical(pixels, w, h);
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
                    case DebugBranch.Base:          pixels[i] = new Color32(0, 200, 200, 255); break; // シアン
                    case DebugBranch.Highlight:     pixels[i] = new Color32(255, 160, 0, 255); break; // オレンジ
                    case DebugBranch.Shadow:        pixels[i] = new Color32(160, 60, 200, 255); break; // 紫
                    case DebugBranch.Decontaminate: pixels[i] = new Color32(255, 230, 0, 255); break; // 黄
                    default:                        pixels[i] = new Color32(0, 0, 0, 0); break;
                }
            }
            FlipVertical(pixels, w, h);
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private static Color32 StageColor(int stageIdx, int totalStages)
        {
            // 段階数に応じて HSV ホイール上の固定 hue を割り当てる。
            float hue = (stageIdx / (float)Mathf.Max(1, totalStages)) % 1f;
            Color c = Color.HSVToRGB(hue, 0.8f, 0.95f);
            return new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
        }

        private static void FlipVertical(Color32[] pixels, int w, int h)
        {
            for (int y = 0; y < h / 2; y++)
            {
                int top = y * w;
                int bot = (h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                {
                    var t = pixels[top + x];
                    pixels[top + x] = pixels[bot + x];
                    pixels[bot + x] = t;
                }
            }
        }

        private static string BuildLegend(Mode mode)
        {
            switch (mode)
            {
                case Mode.Strength:      return "凡例: 白 = 強度 1.0 / 黒 = 0.0";
                case Mode.Delta:         return "凡例: 緑 = 強度上昇 / 赤 = 強度低下 / 暗灰 = 変化なし";
                case Mode.Ownership:     return "凡例: ピクセルごとに最初に strength≥0.5 を達成した段階を hue 円で色分け（透明 = 未到達）";
                case Mode.RecolorBranch: return "凡例: シアン=Base / オレンジ=Highlight / 紫=Shadow / 黄=Decontaminate";
            }
            return "";
        }
    }
}
