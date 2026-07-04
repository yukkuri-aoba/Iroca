// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca.DebugTools
{
    /// <summary>
    /// 「段階ごとのキャプチャ」トグルとキャプチャ状態を担当する部品。
    /// UI は <see cref="PerfView"/> のデバッグモードブロック内から
    /// <see cref="DrawCaptureControls"/> 経由で描画され、詳細可視化は独立 EditorWindow
    /// (<see cref="DebugWindow"/>) に分離されている。
    ///
    /// キャプチャは <see cref="DebugMode"/>（マスタースイッチ）と本トグルの
    /// 両方が ON のときだけ走る。片方でも OFF なら <see cref="CurrentCaptureOrNull"/> が
    /// null を返し、本体パイプラインの <c>debug?.</c> が全て skip される
    /// ＝ キャプチャのオーバーヘッドなしで実パフォーマンスを計測できる。
    ///
    /// EditorWindow 一個前提のプロセス内グローバル状態。
    /// </summary>
    internal static class DebugView
    {
        // ── UI 状態（EditorPrefs で永続化） ─────────────────
        private const string PrefKeyEnabled = "Iroca.Debug.EnableCapture";

        private static bool s_enableCapture;
        private static bool s_loadedPrefs;

        // ── キャプチャ状態 ────────────────────────────
        private static DebugCaptureContext s_activeContext;

        /// <summary>
        /// キャプチャが実際に走る状態か。デバッグモードとキャプチャトグルの両方が ON のときだけ true。
        /// </summary>
        internal static bool IsCaptureEnabled
        {
            get { EnsurePrefsLoaded(); return DebugMode.IsEnabled && s_enableCapture; }
        }

        /// <summary>最新のキャプチャ（DebugWindow から読み出される）。null 可。</summary>
        internal static DebugCaptureContext LatestContext => s_activeContext;

        /// <summary>
        /// <see cref="DebugCaptureHooks.Factory"/> から呼ばれる。
        /// デバッグモード OFF or キャプチャトグル OFF なら null を返してパイプラインを完全 no-op にする。
        /// </summary>
        internal static IDebugCapture CurrentCaptureOrNull()
        {
            EnsurePrefsLoaded();
            if (!DebugMode.IsEnabled || !s_enableCapture) return null;
            // 毎プレビュー/エクスポートで新しいインスタンスを作って渡す。
            var ctx = new DebugCaptureContext();
            s_activeContext = ctx;
            return ctx;
        }

        /// <summary>
        /// PerfView のデバッグモードブロック内から呼ばれ、
        /// 「段階ごとのキャプチャ」サブトグルと詳細ウィンドウを開くボタンを描画する。
        /// 外側の helpBox は PerfView 側が持つ（デバッグ UI を 1 セクションに集約するため）。
        /// </summary>
        internal static void DrawCaptureControls(IrocaWindow host)
        {
            EnsurePrefsLoaded();

            EditorGUI.BeginChangeCheck();
            bool prevEnabled = s_enableCapture;
            s_enableCapture = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "段階ごとのキャプチャを有効化 (パイプライン透明化)",
                    "オンにすると、次回プレビュー/エクスポート時に各パイプライン段階の strength マップ・差分・Recolor サブブランチを採取します。\n" +
                    "採取はピクセル配列の量子化・コピーを伴うため処理が重くなります。\n" +
                    "実際のパフォーマンスを計測したいときはオフにしてください（デバッグモードの詳細内訳表示は維持されます）。"),
                s_enableCapture);
            if (EditorGUI.EndChangeCheck() && s_enableCapture != prevEnabled)
            {
                EditorPrefs.SetBool(PrefKeyEnabled, s_enableCapture);
                host.MarkPreviewDirty();
                if (!s_enableCapture) ClearCapture(host); // OFF にしたら現状のキャプチャを破棄
            }

            if (!s_enableCapture)
            {
                EditorGUILayout.LabelField(
                    "キャプチャ OFF: 実パイプラインのみ実行（オーバーヘッドなしで計測できます）。",
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

        /// <summary>
        /// キャプチャ結果を破棄し DebugWindow に通知する。
        /// デバッグモード OFF もしくはキャプチャトグル OFF に切り替えたときに呼ぶ。
        /// </summary>
        internal static void ClearCapture(IrocaWindow host)
        {
            s_activeContext = null;
            if (host != null) host.LatestDebugCapture = null;
            DebugWindow.NotifyCaptureCleared();
        }

        // ──────────────────────────────────────────────
        private static void EnsurePrefsLoaded()
        {
            if (s_loadedPrefs) return;
            s_loadedPrefs = true;
            // 既定 true: デバッグモードをオンにした時点でキャプチャも有効になる。
            // （実パフォーマンスを測りたいときはユーザーが個別にオフにする）
            s_enableCapture = EditorPrefs.GetBool(PrefKeyEnabled, true);
        }
    }
}
