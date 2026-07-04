// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEditor;
using UnityEngine;

namespace Iroca.DebugTools
{
    /// <summary>
    /// IrocaWindow に組み込まれる「スレッド数調整 + パフォーマンス表示」セクション。
    /// Debug asmdef ごと削除すれば <see cref="DebugBootstrap"/> の登録も消え、本体に影響なし。
    /// </summary>
    internal static class PerfView
    {
        private const string PrefKeyThreads = "Iroca.Perf.ThreadOverride";
        private const string PrefKeyMatchOklab = "Iroca.Debug.MatchDistanceOklab";

        private static bool s_prefsLoaded;
        private static volatile bool s_hasReport;
        private static PerfReport s_lastReport;

        internal static void Register()
        {
            EnsurePrefsLoaded();
            DebugCaptureHooks.OnPerfReport += HandleReport;
        }

        private static void HandleReport(PerfReport report)
        {
            s_lastReport = report;
            s_hasReport = true;
        }

        internal static void Draw(IrocaWindow host)
        {
            EnsurePrefsLoaded();
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool debug = DebugMode.IsEnabled;

                EditorGUILayout.LabelField(
                    new GUIContent("パフォーマンス",
                        "プレビュー処理の実行時間を表示します。\n" +
                        "デバッグモードをオンにすると、フェーズ別/ゾーン別の詳細内訳・スレッド数調整・段階ごとのキャプチャが使えます。\n" +
                        "このセクションは Debug asmdef ごと削除することで本体から切り離せます。"),
                    EditorStyles.boldLabel);

                // 計測結果: 簡略時は合計だけ、デバッグモード時はフェーズ別/ゾーン別も表示。
                DrawPerfReport(debug);

                EditorGUILayout.Space(4);
                DrawDebugModeToggle(host);

                // デバッグモード時のみ: スレッド調整と段階キャプチャの制御を展開。
                if (debug)
                {
                    EditorGUILayout.Space(4);
                    DrawThreadControl(host);
                    EditorGUILayout.Space(4);
                    DrawMatchDistanceControl(host);
                    EditorGUILayout.Space(4);
                    DebugView.DrawCaptureControls(host);
                }
            }
        }

        private static void DrawDebugModeToggle(IrocaWindow host)
        {
            EditorGUI.BeginChangeCheck();
            bool prev = DebugMode.IsEnabled;
            bool now = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "デバッグモード",
                    "オフ: 実行時間の合計のみを簡潔に表示します。\n" +
                    "オン: フェーズ別/ゾーン別の詳細内訳・スレッド数調整・段階ごとのキャプチャ（パイプライン透明化）を表示します。"),
                prev, EditorStyles.boldLabel);
            if (EditorGUI.EndChangeCheck() && now != prev)
            {
                DebugMode.IsEnabled = now;
                host.MarkPreviewDirty();
                // デバッグモードを切ったらキャプチャ結果も破棄する（次回プレビューは非キャプチャで走る）。
                if (!now) DebugView.ClearCapture(host);
            }
        }

        private static void DrawMatchDistanceControl(IrocaWindow host)
        {
            EditorGUI.BeginChangeCheck();
            bool prev = DebugCaptureHooks.MatchDistanceOklab;
            bool now = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "実験: OKLab マッチング距離",
                    "選択（色替え対象にする画素の判定）距離を、従来の HSV/RGB ハイブリッドから OKLab へ切替える実験機能です。\n" +
                    "プレビュー・適用・エクスポートの選択結果（どの画素が変わるか）が変化します。発色（色の計算）そのものは変わりません。\n" +
                    "自動調整・穴埋め/境界回復は HSV のままのため、この実験距離と整合しない場合があります。\n" +
                    "通常は OFF（既定）を推奨します。"),
                prev);
            // PerfView は BeginChangeCheck の外なので自動 dirty 化されない。明示的に MarkPreviewDirty する。
            if (EditorGUI.EndChangeCheck() && now != prev)
            {
                DebugCaptureHooks.MatchDistanceOklab = now;
                EditorPrefs.SetBool(PrefKeyMatchOklab, now);
                host.MarkPreviewDirty();
            }
            // ON のまま忘れる対策として、有効中は常に警告を出す。
            if (DebugCaptureHooks.MatchDistanceOklab)
                EditorGUILayout.HelpBox(
                    "OKLab マッチング距離（実験）が有効です。エクスポート結果も変わります。",
                    MessageType.Warning);
        }

        private static void DrawThreadControl(IrocaWindow host)
        {
            int cpuCount = Environment.ProcessorCount;
            int defaultCount = Math.Max(1, cpuCount - 2);
            bool isAuto = DebugCaptureHooks.ParallelismOverride <= 0;
            int displayCount = isAuto ? defaultCount : DebugCaptureHooks.ParallelismOverride;

            EditorGUI.BeginChangeCheck();
            bool newAuto = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    $"スレッド数を自動設定  (現在: {displayCount} / {cpuCount} コア使用)",
                    $"ON: CPU コア数 - 2 = {defaultCount} スレッドを自動使用。\n" +
                    "Unity Editor のスレッドプール圧迫を防ぐデフォルト設定です。\n" +
                    "OFF にするとスライダーで手動設定できます。"),
                isAuto);
            if (EditorGUI.EndChangeCheck() && newAuto != isAuto)
            {
                DebugCaptureHooks.ParallelismOverride = newAuto ? 0 : displayCount;
                EditorPrefs.SetInt(PrefKeyThreads, DebugCaptureHooks.ParallelismOverride);
                host.MarkPreviewDirty();
            }

            using (new EditorGUI.DisabledGroupScope(isAuto))
            {
                EditorGUI.BeginChangeCheck();
                int newCount = EditorGUILayout.IntSlider(
                    new GUIContent("スレッド数",
                        "Parallel.For の MaxDegreeOfParallelism を手動設定します。\n" +
                        "コア数を増やすと処理が速くなりますが、Unity Editor の UI が重くなる場合があります。\n" +
                        $"CPU コア数: {cpuCount}  /  推奨自動値: {defaultCount}"),
                    displayCount, 1, cpuCount);
                if (EditorGUI.EndChangeCheck() && !isAuto)
                {
                    DebugCaptureHooks.ParallelismOverride = newCount;
                    EditorPrefs.SetInt(PrefKeyThreads, newCount);
                    host.MarkPreviewDirty();
                }
            }
        }

        private static void DrawPerfReport(bool detailed)
        {
            if (!s_hasReport)
            {
                EditorGUILayout.LabelField(
                    "(プレビューを生成すると実行時間が表示されます)",
                    EditorStyles.miniLabel);
                return;
            }

            var rep = s_lastReport;
            if (rep == null) return;

            EditorGUILayout.LabelField(
                new GUIContent(
                    $"最終実行: {rep.TotalMs:F1} ms  ({rep.Width}×{rep.Height})",
                    "ProcessPixelsArray の総実行時間とテクスチャサイズ。\nゾーン数が多いほど比例して増加します。"),
                EditorStyles.boldLabel);

            // 簡略表示（デバッグモード OFF）は合計だけで打ち切り。
            if (!detailed) return;

            // フェーズ別内訳(全ゾーン合算)。どの段が重いかを把握して最適化対象を絞るための表示。
            if (rep.Phases != null && rep.Phases.Length > 0)
            {
                EditorGUILayout.LabelField(
                    new GUIContent("フェーズ別内訳",
                        "ProcessPixelsArray の各段(HSV/Match/FloodFill/穴埋め/境界/ブラー/デコンタミ/領域統計/再着色)\n" +
                        "の所要時間を全ゾーン合算で表示します。最も重い段が最適化の第一候補です。"),
                    EditorStyles.miniBoldLabel);

                double maxPhaseMs = 0.0;
                foreach (var p in rep.Phases)
                    if (p.TotalMs > maxPhaseMs) maxPhaseMs = p.TotalMs;

                foreach (var p in rep.Phases)
                    DrawBarRow(p.Name, p.TotalMs, maxPhaseMs, new Color(0.30f, 0.50f, 0.75f),
                        $"フェーズ \"{p.Name}\" の所要時間(全ゾーン合算)");
                EditorGUILayout.Space(2);
            }

            if (rep.Zones == null || rep.Zones.Length == 0) return;

            EditorGUILayout.LabelField(
                new GUIContent("ゾーン別内訳", "各ゾーンの処理時間。ゾーン数に比例して総時間が増えます。"),
                EditorStyles.miniBoldLabel);

            float maxMs = 0f;
            foreach (var z in rep.Zones)
                if ((float)z.TotalMs > maxMs) maxMs = (float)z.TotalMs;

            foreach (var z in rep.Zones)
            {
                string label = string.IsNullOrEmpty(z.ZoneId) ? "(unnamed)" : z.ZoneId;
                DrawBarRow(label, z.TotalMs, maxMs, new Color(0.25f, 0.65f, 0.35f),
                    $"ゾーン \"{label}\" の処理時間");
            }
        }

        private static void DrawBarRow(string label, double ms, double maxMs, Color barColor, string tooltip)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(150));

                var barRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(
                    new Rect(barRect.x, barRect.y + 2, barRect.width, barRect.height - 4),
                    new Color(0.18f, 0.18f, 0.18f));
                float ratio = maxMs > 0.0 ? (float)(ms / maxMs) : 0f;
                if (ratio > 0f)
                    EditorGUI.DrawRect(
                        new Rect(barRect.x, barRect.y + 2, barRect.width * ratio, barRect.height - 4),
                        barColor);

                EditorGUILayout.LabelField($"{ms:F1} ms", EditorStyles.miniLabel, GUILayout.Width(58));
            }
        }

        private static void EnsurePrefsLoaded()
        {
            if (s_prefsLoaded) return;
            s_prefsLoaded = true;
            DebugCaptureHooks.ParallelismOverride = EditorPrefs.GetInt(PrefKeyThreads, 0);
            // Debug asmdef を削除した環境ではこの復元コードごと消え、フラグは常に false=本番安全。
            DebugCaptureHooks.MatchDistanceOklab = EditorPrefs.GetBool(PrefKeyMatchOklab, false);
        }
    }
}
