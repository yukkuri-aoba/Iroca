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

        private static bool s_prefsLoaded;
        private static volatile bool s_hasReport;
        private static PerfReport s_lastReport;
        // 体感速度(操作 → 画面)。メインスレッドで届くので同期は要らない。
        private static PreviewLatencyReport s_lastLatency;
        private static PreviewLatencyReport s_shownLatency;

        private static readonly Color CoreColor = new Color(0.30f, 0.50f, 0.75f);
        private static readonly Color JobColor  = new Color(0.50f, 0.68f, 0.86f);
        private static readonly Color WaitColor = new Color(0.42f, 0.42f, 0.42f);
        private static readonly Color MainColor = new Color(0.88f, 0.56f, 0.24f);

        private static readonly string[] LaneNames = { "操作→準備", "プロキシ", "フル", "拡大表示" };

        internal static void Register()
        {
            EnsurePrefsLoaded();
            DebugCaptureHooks.OnPerfReport += HandleReport;
            PreviewLatency.OnReport += HandleLatency;
        }

        private static void HandleReport(PerfReport report)
        {
            s_lastReport = report;
            s_hasReport = true;
        }

        private static void HandleLatency(PreviewLatencyReport report) => s_lastLatency = report;

        internal static void Draw(IrocaWindow host)
        {
            EnsurePrefsLoaded();
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool debug = DebugMode.IsEnabled;

                EditorGUILayout.LabelField(
                    new GUIContent("パフォーマンス",
                        "プレビューの体感時間(操作から画面に出るまで)と、コア処理の実行時間を表示します。\n" +
                        "デバッグモードをオンにすると、体感時間の区間別タイムライン・フェーズ別/ゾーン別の詳細内訳・" +
                        "スレッド数調整・段階ごとのキャプチャが使えます。\n" +
                        "このセクションは Debug asmdef ごと削除することで本体から切り離せます。"),
                    EditorStyles.boldLabel);

                // 体感(操作 → 画面)。コア処理の時間とは別の見方で、待ち・受け渡し・転送・段の直列を含む。
                DrawLatency(debug);

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
                    $"コア処理(直近 1 回): {rep.TotalMs:F1} ms  ({rep.Width}×{rep.Height})",
                    "ProcessPixelsArray 1 回分の実行時間と、処理した寸法。\n" +
                    "プロキシ・フル・拡大表示のうち最後に走ったものです(寸法で見分けられます)。\n" +
                    "ゾーン数が多いほど比例して増加します。\n" +
                    "操作から画面に出るまでの待ち・受け渡し・転送は含みません(上の「体感」を参照)。"),
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

        private static void DrawLatency(bool detailed)
        {
            // 表示するレポートは Layout のときに固定する。レポートはプレビューの転送(同じ OnGUI の中)で
            // 差し替わるので、Layout と Repaint で別のものを描くと行数が食い違って IMGUI が例外を出す。
            if (Event.current.type == EventType.Layout) s_shownLatency = s_lastLatency;
            var r = s_shownLatency;
            if (r == null) return;

            EditorGUILayout.LabelField(
                new GUIContent("体感(操作 → 画面)",
                    "プレビューの再生成を起こした最後の操作(スライダー・クリックなど)から、結果が画面に出るまでの時間。\n" +
                    "下の「コア処理」は ProcessPixelsArray 1 回分で、待ち・受け渡し・転送や、" +
                    "プロキシ → フル → 拡大表示の段が順に走ることは含みません。"),
                EditorStyles.boldLabel);

            string line = $"初回 {r.FirstShownMs:F0} ms ・ 確定 {r.FinalShownMs:F0} ms";
            if (!double.IsNaN(r.DetailShownMs)) line += $" ・ 拡大 {r.DetailShownMs:F0} ms";
            if (!r.HasInput) line += "  (操作なし: 起点は生成開始)";
            EditorGUILayout.LabelField(
                new GUIContent(line,
                    "初回: 縮小プロキシの概要が出るまで(プロキシを使わないときは確定と同じ)。\n" +
                    "確定: 表示解像度の確定結果が出るまで。\n" +
                    "拡大: 拡大表示中だけ。フル解像度の詳細クロップが出るまで。\n" +
                    $"元画像 {r.SourceW}×{r.SourceH}"));

            // 簡略表示(デバッグモード OFF)は要約の 1 行だけ。
            if (!detailed) return;

            DrawLatencyTimeline(r);

            DrawLatencyValueRow("操作中に止まっていた時間", r.StaleMs,
                "前回画面が更新されてから最初の操作 → 初回表示。\n" +
                "ドラッグ中はプロキシだけを回して追従するので、追従 1 回ぶん(数十 ms)程度に収まるのが正常です。");
            DrawLatencyValueRow("UI が止まった時間", r.MainThreadMs,
                "メインスレッドの処理(入力のスナップショット・テクスチャ転送)の合計。\n" +
                "この間は Editor の操作・再描画が止まります。");
            DrawLatencyValueRow("コア処理の合計", r.CoreMs,
                "この 1 回の操作で走った ProcessPixelsArray(プロキシ・フル・拡大表示)の合計。");
            EditorGUILayout.Space(4);
        }

        // 起点(最後の操作)からの時間軸に、段ごとの区間を 1 行ずつ並べる。区間にカーソルを
        // 合わせると名前と長さが出る。行末の数値はその段が画面に出た時刻。
        private static void DrawLatencyTimeline(PreviewLatencyReport r)
        {
            double span = r.LastShownMs;
            if (span <= 0.0 || r.Segments == null) return;

            for (int lane = 0; lane < PreviewLatencyReport.LaneCount; lane++)
            {
                double laneEnd = -1.0;
                foreach (var s in r.Segments)
                    if (s.Lane == lane && s.EndMs > laneEnd) laneEnd = s.EndMs;
                if (laneEnd < 0.0) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        new GUIContent(LaneNames[lane], LaneTooltip(lane)), GUILayout.Width(150));

                    var barRect = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
                    var track = new Rect(barRect.x, barRect.y + 2, barRect.width, barRect.height - 4);
                    EditorGUI.DrawRect(track, new Color(0.18f, 0.18f, 0.18f));
                    foreach (var s in r.Segments)
                    {
                        if (s.Lane != lane) continue;
                        float x0 = track.x + track.width * (float)(Math.Max(0.0, s.StartMs) / span);
                        float x1 = track.x + track.width * (float)(Math.Min(span, s.EndMs) / span);
                        var segRect = new Rect(x0, track.y, Mathf.Max(1f, x1 - x0), track.height);
                        EditorGUI.DrawRect(segRect, KindColor(s.Kind));
                        GUI.Label(segRect, new GUIContent(string.Empty,
                            $"{s.Name}: {s.Ms:F1} ms  ({s.StartMs:F0} → {s.EndMs:F0} ms)"));
                    }

                    EditorGUILayout.LabelField($"{laneEnd:F0} ms", EditorStyles.miniLabel, GUILayout.Width(58));
                }
            }

            // 凡例は行頭から並べる(段名の列に合わせて字下げすると、狭い設定列で末尾が切れる)。
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawLegend(CoreColor, "コア処理", "ProcessPixelsArray。上の「コア処理」が測っている部分。");
                DrawLegend(JobColor, "複製・縮小", "ジョブ内のコア以外(入力の複製、表示寸法への縮小)。");
                DrawLegend(WaitColor, "待ち", "誰も計算していない時間(デバウンス・スレッドプールの起動・update/再描画待ち)。");
                DrawLegend(MainColor, "UI 停止", "メインスレッドの処理(入力のスナップショット・テクスチャ転送)。");
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.Space(2);
        }

        private static string LaneTooltip(int lane)
        {
            switch (lane)
            {
                case PreviewLatencyReport.LaneRequest:
                    return "最後の操作から再生成を始めるまでの待ちと、入力(マスク・ゾーン設定)のスナップショット。";
                case PreviewLatencyReport.LaneProxy:
                    return "縮小プロキシで概要を先に出す段(大きいテクスチャのときだけ)。";
                case PreviewLatencyReport.LaneFull:
                    return "フル解像度で処理して表示解像度の確定結果を出す段。プロキシがあるときはその後に始まります。\n" +
                           "ドラッグ中はプロキシだけで追従し、手を止めて 0.2 秒経つか離してからこの段を始めます" +
                           "(その間は「確定の開始待ち」)。";
                default:
                    return "拡大表示中だけ。確定表示の後、0.3 秒待ってから見えている範囲をフル解像度で作り直す段。";
            }
        }

        private static Color KindColor(LatencyKind kind)
        {
            switch (kind)
            {
                case LatencyKind.Core: return CoreColor;
                case LatencyKind.Job:  return JobColor;
                case LatencyKind.Main: return MainColor;
                default:               return WaitColor;
            }
        }

        private static void DrawLegend(Color color, string label, string tooltip)
        {
            var swatch = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(14));
            EditorGUI.DrawRect(new Rect(swatch.x, swatch.y + 3, 10, 8), color);
            var content = new GUIContent(label, tooltip);
            GUILayout.Label(content, EditorStyles.miniLabel,
                GUILayout.Width(EditorStyles.miniLabel.CalcSize(content).x + 4));
        }

        private static void DrawLatencyValueRow(string label, double ms, string tooltip)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(150));
                EditorGUILayout.LabelField($"{ms:F0} ms", EditorStyles.miniLabel);
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
        }
    }
}
