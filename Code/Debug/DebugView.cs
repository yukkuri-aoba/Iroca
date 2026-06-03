// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger.DebugTools
{
    /// <summary>
    /// VACCWindow に組み込まれる「デバッグキャプチャ有効化トグル」と
    /// 「別ウィンドウで詳細を開く」ボタンだけを担当する小さなフット プリント部品。
    /// 実際の可視化 UI は <see cref="DebugWindow"/> (独立 EditorWindow) に分離されている。
    ///
    /// EditorWindow 一個前提のプロセス内グローバル状態。
    /// </summary>
    internal static class DebugView
    {
        // ── UI 状態（EditorPrefs で永続化） ─────────────────
        private const string PrefKeyEnabled = "VACC.Debug.EnableCapture";

        private static bool s_enableCapture;
        private static bool s_loadedPrefs;

        // ── キャプチャ状態 ────────────────────────────
        private static DebugCaptureContext s_activeContext;

        internal static bool IsCaptureEnabled
        {
            get { EnsurePrefsLoaded(); return s_enableCapture; }
        }

        /// <summary>最新のキャプチャ（DebugWindow から読み出される）。null 可。</summary>
        internal static DebugCaptureContext LatestContext => s_activeContext;

        /// <summary>
        /// <see cref="DebugCaptureHooks.Factory"/> から呼ばれる。
        /// トグル OFF なら null を返してパイプラインを完全 no-op にする。
        /// </summary>
        internal static IDebugCapture CurrentCaptureOrNull()
        {
            EnsurePrefsLoaded();
            if (!s_enableCapture) return null;
            // 毎プレビュー/エクスポートで新しいインスタンスを作って渡す。
            var ctx = new DebugCaptureContext();
            s_activeContext = ctx;
            return ctx;
        }

        /// <summary>
        /// VACCWindow.OnGUI のスクロール領域内から発火される。
        /// 最小限のヘッダー（チェックボックス + 別ウィンドウを開くボタン）だけ描画する。
        /// </summary>
        internal static void Draw(VACCWindow host)
        {
            EnsurePrefsLoaded();

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
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
                        host.MarkPreviewDirty();
                        if (!s_enableCapture)
                        {
                            // OFF にしたら現状のキャプチャを破棄
                            s_activeContext = null;
                            host.LatestDebugCapture = null;
                            DebugWindow.NotifyCaptureCleared();
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

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(
                            "詳細を別ウィンドウで開く",
                            "可視化（ステージごとのヒートマップ・差分・オーナーシップ・Recolor ブランチ）と PNG ダンプを別ウィンドウで開きます。\nウィンドウを自由にサイズ変更してデバッグできます。")))
                    {
                        DebugWindow.OpenOrFocus();
                    }
                    if (s_activeContext != null && s_activeContext.Snapshots.Count > 0)
                    {
                        GUILayout.Label(
                            $"capture: {s_activeContext.Snapshots.Count} snapshots × {s_activeContext.Width}x{s_activeContext.Height}",
                            EditorStyles.miniLabel);
                    }
                    else
                    {
                        GUILayout.Label("(プレビューを再生成するとキャプチャされます)", EditorStyles.miniLabel);
                    }
                }
            }
        }

        // ──────────────────────────────────────────────
        private static void EnsurePrefsLoaded()
        {
            if (s_loadedPrefs) return;
            s_loadedPrefs = true;
            s_enableCapture = EditorPrefs.GetBool(PrefKeyEnabled, false);
        }
    }
}
