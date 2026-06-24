// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Camereo
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEditor;
using UnityEngine;

namespace Camereo.DebugTools
{
    /// <summary>
    /// CamereoWindow に組み込まれる「スレッド数調整 + パフォーマンス表示」セクション。
    /// Debug asmdef ごと削除すれば <see cref="DebugBootstrap"/> の登録も消え、本体に影響なし。
    /// </summary>
    internal static class PerfView
    {
        private const string PrefKeyThreads = "Camereo.Perf.ThreadOverride";

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

        internal static void Draw(CamereoWindow host)
        {
            EnsurePrefsLoaded();
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    new GUIContent("パフォーマンス / スレッド設定",
                        "プレビュー処理の実行時間計測と Parallel.For スレッド数の手動調整ができます。\n" +
                        "このセクションは Debug asmdef ごと削除することで本体から切り離せます。"),
                    EditorStyles.boldLabel);

                DrawThreadControl(host);
                EditorGUILayout.Space(4);
                DrawPerfReport();
            }
        }

        private static void DrawThreadControl(CamereoWindow host)
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

        private static void DrawPerfReport()
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

            if (rep.Zones == null || rep.Zones.Length == 0) return;

            float maxMs = 0f;
            foreach (var z in rep.Zones)
                if ((float)z.TotalMs > maxMs) maxMs = (float)z.TotalMs;

            foreach (var z in rep.Zones)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = string.IsNullOrEmpty(z.ZoneId) ? "(unnamed)" : z.ZoneId;
                    EditorGUILayout.LabelField(
                        new GUIContent(label, $"ゾーン \"{label}\" の処理時間"),
                        GUILayout.Width(150));

                    var barRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawRect(
                        new Rect(barRect.x, barRect.y + 2, barRect.width, barRect.height - 4),
                        new Color(0.18f, 0.18f, 0.18f));
                    float ratio = maxMs > 0f ? (float)(z.TotalMs / maxMs) : 0f;
                    if (ratio > 0f)
                        EditorGUI.DrawRect(
                            new Rect(barRect.x, barRect.y + 2, barRect.width * ratio, barRect.height - 4),
                            new Color(0.25f, 0.65f, 0.35f));

                    EditorGUILayout.LabelField(
                        $"{z.TotalMs:F1} ms",
                        EditorStyles.miniLabel,
                        GUILayout.Width(58));
                }
            }
        }

        private static void EnsurePrefsLoaded()
        {
            if (s_prefsLoaded) return;
            s_prefsLoaded = true;
            DebugCaptureHooks.ParallelismOverride = EditorPrefs.GetInt(PrefKeyThreads, 0);
        }
    }
}
