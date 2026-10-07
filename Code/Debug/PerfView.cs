// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace Iroca.DebugTools
{
    /// <summary>
    /// IrocaWindow に組み込まれる「スレッド数調整 + パフォーマンス表示」セクション。
    /// デバッグモード OFF は体感とコア処理の要約 2 行だけ、ON は区間・フェーズ・ゾーン別の内訳とメモリも出す。
    /// Debug asmdef ごと削除すれば <see cref="DebugBootstrap"/> の登録も消え、本体に影響なし。
    /// </summary>
    internal static class PerfView
    {
        private const string PrefKeyThreads = "Iroca.Perf.ThreadOverride";

        private static bool s_prefsLoaded;
        // 体感速度(操作 → 画面)と、同じ操作のコア処理の計測。メインスレッドで届くので同期は要らない。
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
            PreviewLatency.OnReport += HandleLatency;
        }

        private static void HandleLatency(PreviewLatencyReport report) => s_lastLatency = report;

        internal static void Draw(IrocaWindow host)
        {
            EnsurePrefsLoaded();
            // 表示するレポートは Layout のときに固定する。レポートはプレビューの転送(同じ OnGUI の中)で
            // 差し替わるので、Layout と Repaint で別のものを描くと行数が食い違って IMGUI が例外を出す。
            // メモリも同じく Layout で読んだ値を Repaint でも描く。
            bool debug = DebugMode.IsEnabled;
            if (Event.current.type == EventType.Layout)
            {
                s_shownLatency = s_lastLatency;
                if (debug) SampleMemory();
            }
            var shown = s_shownLatency;

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // トグルは見出しの行に置く(オン/オフで中身の行数が変わっても位置が動かない)。
                DrawHeader(host);

                // 簡略表示(デバッグモード OFF)は体感とコア処理の要約を 1 行ずつだけ。
                if (!debug)
                {
                    DrawSummary(shown);
                    return;
                }

                // 体感(操作 → 画面)。コア処理の時間とは別の見方で、待ち・受け渡し・転送・段の直列を含む。
                DrawLatency(shown);

                // 同じ操作のコア処理: 段ごとの合計と、フェーズ別/ゾーン別の内訳。
                DrawPerfReport(shown);

                EditorGUILayout.Space(4);
                DrawMemory();

                EditorGUILayout.Space(4);
                DrawThreadControl(host);
                EditorGUILayout.Space(4);
                DebugView.DrawCaptureControls(host);
            }
        }

        private static void DrawHeader(IrocaWindow host)
        {
            var title = new GUIContent("パフォーマンス",
                "プレビューの体感時間(操作から画面に出るまで)と、コア処理の実行時間を表示します。\n" +
                "デバッグモードをオンにすると、体感時間の区間別タイムライン・フェーズ別/ゾーン別の詳細内訳・" +
                "メモリ・スレッド数調整・段階ごとのキャプチャが使えます。\n" +
                "このセクションは Debug asmdef ごと削除することで本体から切り離せます。");
            using (new EditorGUILayout.HorizontalScope())
            {
                // 見出しもトグルも中身の幅だけを取り、左に寄せる。既定の最小幅(ラベル幅＋フィールド幅)を
                // 要求させると設定列より広い行になり、列全体の右端が切れる(DrawCoreRow を参照)。右寄せにすると、
                // 列の右端が切れているときにトグルの文字が欠ける。
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel,
                    GUILayout.Width(EditorStyles.boldLabel.CalcSize(title).x));
                GUILayout.Space(12f);
                DrawDebugModeToggle(host);
                GUILayout.FlexibleSpace();
            }
        }

        private static void DrawDebugModeToggle(IrocaWindow host)
        {
            var content = new GUIContent(
                "デバッグモード",
                "オフ: 体感時間とコア処理の合計だけを 2 行で表示します。\n" +
                "オン: 体感時間の区間別タイムライン・フェーズ別/ゾーン別の詳細内訳・メモリ・スレッド数調整・" +
                "段階ごとのキャプチャ（パイプライン透明化）を表示します。");
            // チェックボックスの分(~16px)と余白を足した幅だけを取る。
            float width = EditorStyles.label.CalcSize(content).x + 20f;
            EditorGUI.BeginChangeCheck();
            bool prev = DebugMode.IsEnabled;
            bool now = EditorGUILayout.ToggleLeft(content, prev, GUILayout.Width(width));
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

        // 簡略表示: 体感(操作 → 画面)とコア処理を 1 行ずつ。寸法や用語の説明はツールチップへ寄せる。
        private static void DrawSummary(PreviewLatencyReport r)
        {
            if (r == null)
            {
                EditorGUILayout.LabelField(
                    "(プレビューを生成すると実行時間が表示されます)",
                    EditorStyles.miniLabel);
                return;
            }

            string latency;
            if (r.ViewChange)
            {
                latency = $"体感  スクロール・ズーム → 拡大 {r.DetailShownMs:F0} ms";
            }
            else
            {
                latency = r.IsFinal
                    ? $"体感  初回 {r.FirstShownMs:F0} ・ 確定 {r.FinalShownMs:F0}"
                    : $"体感  初回 {r.FirstShownMs:F0} ・ 確定 待ち";
                if (!double.IsNaN(r.DetailShownMs)) latency += $" ・ 拡大 {r.DetailShownMs:F0}";
                latency += " ms";
            }
            if (!r.HasInput) latency += " (操作なし)";
            // 折り返す(狭い設定列で末尾が切れて読めなくならないように)。
            EditorGUILayout.LabelField(
                new GUIContent(latency,
                    "操作(スライダー・クリックなど)から、結果が画面に出るまでの時間。\n" +
                    "初回: 縮小プロキシの概要が出るまで(プロキシを使わないときは確定と同じ)。\n" +
                    "確定: 表示解像度の確定結果が出るまで。ドラッグの追従中や確定前は「待ち」。\n" +
                    "拡大: 拡大表示中だけ。フル解像度の詳細クロップが出るまで。\n" +
                    "スクロール・ズーム: 拡大表示中に表示位置・倍率を最後に変えてから、拡大表示が出るまで" +
                    $"(手を止めて {DebounceText} 待ってから作り直すので、その待ちを含みます)。\n" +
                    "(操作なし): 操作を起点にできない再生成(初回表示など)で、起点は生成開始。\n" +
                    $"元画像 {r.SourceW}×{r.SourceH}。区間ごとの内訳はデバッグモードで表示します。"),
                EditorStyles.wordWrappedMiniLabel);

            string core;
            if (r.ProxyCore == null && r.FullCore == null)
            {
                core = r.ViewChange ? "コア  なし(拡大表示は確定結果の切り出しだけ)" : "コア  なし";
            }
            else
            {
                core = "コア  ";
                if (r.ProxyCore != null) core += $"プロキシ {r.ProxyCore.TotalMs:F0}";
                if (r.ProxyCore != null && r.FullCore != null) core += " ・ ";
                if (r.FullCore != null) core += $"フル {r.FullCore.TotalMs:F0}";
                core += " ms";
            }
            string dims = string.Empty;
            if (r.ProxyCore != null) dims += $"\nプロキシ: {r.ProxyCore.Width}×{r.ProxyCore.Height}";
            if (r.FullCore != null) dims += $"\nフル: {r.FullCore.Width}×{r.FullCore.Height}";
            EditorGUILayout.LabelField(
                new GUIContent(core,
                    "同じ操作で走った ProcessPixelsArray の実行時間。待ち・受け渡し・転送は含みません。\n" +
                    "プロキシ: 縮小した概要の段。フル: 確定の段(書き出しと同じ解像度)。\n" +
                    "拡大表示はフル段の出力を切り出すだけなので走りません。" + dims),
                EditorStyles.wordWrappedMiniLabel);
        }

        // コア処理(ProcessPixelsArray)の実行時間。上の「体感」と同じ操作の、画面に出た段のものを出すので、
        // 操作のたびに体感と一緒に差し替わる(エクスポートなどプレビュー以外の処理のものは混ぜない)。
        private static void DrawPerfReport(PreviewLatencyReport r)
        {
            if (r == null)
            {
                EditorGUILayout.LabelField(
                    "(プレビューを生成すると実行時間が表示されます)",
                    EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField(
                new GUIContent("コア処理",
                    "上の「体感」と同じ操作で走った ProcessPixelsArray の実行時間と、処理した寸法。\n" +
                    "プロキシ: 縮小した概要の段。フル: 確定の段(書き出しと同じ解像度)。\n" +
                    "拡大表示はフル段の出力を切り出すだけなので走りません(スクロール・ズームでは「なし」)。\n" +
                    "ゾーン数が多いほど比例して増加します。\n" +
                    "操作から画面に出るまでの待ち・受け渡し・転送は含みません(上の「体感」を参照)。"),
                EditorStyles.boldLabel);

            var rep = r.FullCore ?? r.ProxyCore;
            if (rep == null)
            {
                EditorGUILayout.LabelField(
                    r.ViewChange ? "なし(拡大表示は確定結果の切り出しだけ)" : "なし",
                    EditorStyles.miniLabel);
                return;
            }
            if (r.ProxyCore != null) DrawCoreRow("プロキシ", r.ProxyCore);
            if (r.FullCore != null) DrawCoreRow("フル", r.FullCore);

            // 内訳は最後に画面に出た段のもの(確定前のドラッグの追従ならプロキシ、確定したらフル)。
            string stage = r.FullCore != null ? "フル" : "プロキシ";

            // フェーズ別内訳(全ゾーン合算)。どの段が重いかを把握して最適化対象を絞るための表示。
            if (rep.Phases != null && rep.Phases.Length > 0)
            {
                EditorGUILayout.LabelField(
                    new GUIContent($"フェーズ別内訳({stage})",
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
                new GUIContent($"ゾーン別内訳({stage})", "各ゾーンの処理時間。ゾーン数に比例して総時間が増えます。"),
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

        private static void DrawLatency(PreviewLatencyReport r)
        {
            if (r == null) return;

            EditorGUILayout.LabelField(
                new GUIContent("体感(操作 → 画面)",
                    "プレビューの再生成を起こした最後の操作(スライダー・クリックなど)から、結果が画面に出るまでの時間。\n" +
                    "拡大表示中のスクロール・ズームは、拡大表示が作り直されて画面に出るまでを測ります。\n" +
                    "下の「コア処理」は同じ操作で走った ProcessPixelsArray の時間だけで、待ち・受け渡し・転送は含みません。"),
                EditorStyles.boldLabel);

            string line;
            if (r.ViewChange)
            {
                line = $"スクロール・ズーム → 拡大 {r.DetailShownMs:F0} ms";
            }
            else
            {
                line = r.IsFinal
                    ? $"初回 {r.FirstShownMs:F0} ms ・ 確定 {r.FinalShownMs:F0} ms"
                    : $"初回 {r.FirstShownMs:F0} ms ・ 確定 待ち";
                if (!double.IsNaN(r.DetailShownMs)) line += $" ・ 拡大 {r.DetailShownMs:F0} ms";
            }
            if (!r.HasInput) line += "  (操作なし: 起点は生成開始)";
            // 折り返す(既定幅の設定列では 1 行に収まらず、末尾が切れて読めなかった)。
            EditorGUILayout.LabelField(
                new GUIContent(line,
                    "初回: 縮小プロキシの概要が出るまで(プロキシを使わないときは確定と同じ)。\n" +
                    "確定: 表示解像度の確定結果が出るまで。ドラッグの追従中や確定前は「待ち」。\n" +
                    "拡大: 拡大表示中だけ。フル解像度の詳細クロップが出るまで。\n" +
                    "スクロール・ズーム: 拡大表示中に表示位置・倍率を最後に変えてから、拡大表示が出るまで" +
                    $"(手を止めて {DebounceText} 待ってから作り直すので、その待ちを含みます)。\n" +
                    $"元画像 {r.SourceW}×{r.SourceH}"),
                EditorStyles.wordWrappedLabel);

            DrawLatencyTimeline(r);

            // スクロール・ズームの間は縮小表示の引き伸ばしがすぐ追従するので、止まっていた時間は出さない。
            if (!r.ViewChange)
                DrawLatencyValueRow("操作中に止まっていた時間", r.StaleMs,
                    "前回画面が更新されてから最初の操作 → 初回表示。\n" +
                    "ドラッグ中はプロキシだけを回して追従するので、追従 1 回ぶん(数十 ms)程度に収まるのが正常です。");
            DrawLatencyValueRow("UI が止まった時間", r.MainThreadMs,
                "メインスレッドの処理(入力のスナップショット・テクスチャ転送)の合計。\n" +
                "この間は Editor の操作・再描画が止まります。");
            EditorGUILayout.Space(4);
        }

        private static void DrawCoreRow(string label, PerfReport rep)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(150));
                // MinWidth(0): 値の LabelField は既定で「ラベル幅＋フィールド幅」(~200px)を最小幅に
                // 要求し、150px の段名と並ぶとこの行だけで ~360px になる。設定列(下限 320px)より広い行が
                // 1 つでもあると、列全体がその幅で並べられて全行の右端(数値欄・ボタン)が切れる。
                EditorGUILayout.LabelField($"{rep.TotalMs:F1} ms  ({rep.Width}×{rep.Height})", EditorStyles.miniLabel,
                    GUILayout.MinWidth(0f));
            }
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
                    return "最後の操作から再生成を始めるまでの待ちと、入力(マスク・ゾーン設定)のスナップショット。\n" +
                           $"スクロール・ズームでは、手を止めて拡大表示の作り直しを始めるまで({DebounceText}の間引き)の待ち。";
                case PreviewLatencyReport.LaneProxy:
                    return "縮小プロキシで概要を先に出す段(大きいテクスチャのときだけ)。";
                case PreviewLatencyReport.LaneFull:
                    return "フル解像度で処理して表示解像度の確定結果を出す段。プロキシがあるときはその後に始まります。\n" +
                           "ドラッグ中はプロキシだけで追従し、手を止めて 0.2 秒経つか離してからこの段を始めます" +
                           "(その間は「確定の開始待ち」)。";
                default:
                    return "拡大表示中だけ。確定表示の直後と、スクロール・ズームのあとに、見えている範囲を\n" +
                           $"フル段の出力から切り出して作り直す段(スクロール・ズームの直後は {DebounceText} 待ってから)。";
            }
        }

        // スクロール・ズームのあと拡大表示を作り直すまでの待ち(ツールチップ用。値は DetailPreviewView が正)。
        private static string DebounceText => $"{DetailPreviewView.DetailDebounceSeconds:0.0#} 秒";

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

        private static void DrawLatencyValueRow(string label, double ms, string tooltip) =>
            DrawValueRow(label, $"{ms:F0} ms", tooltip, 150f);

        private static void DrawValueRow(string label, string value, string tooltip, float labelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(labelWidth));
                // MinWidth(0) の理由は DrawCoreRow と同じ(設定列を押し広げない)。
                EditorGUILayout.LabelField(value, EditorStyles.miniLabel, GUILayout.MinWidth(0f));
            }
        }

        // ---- メモリ(デバッグモード時のみ) ----

        private struct MemorySample
        {
            internal long MonoUsed, MonoHeap;             // Mono(マネージド)ヒープ
            internal long NativeUsed, NativeReserved;     // Unity の内部アロケータ
            internal bool HasProcess;                     // false = プロセスの値が取れない(Windows 以外など)
            internal long Private, PeakPrivate, WorkingSet;
            internal int GcCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr p, out ProcessMemoryCounters c, uint cb);

        // 描き直しのたびに読み直すと数字が揺れて読めないので間引く(秒)。
        private const double MemorySampleInterval = 0.5;
        private const float MemoryLabelWidth = 110f;

        private static MemorySample s_memory;
        private static double s_memorySampledAt = double.NegativeInfinity;
        private static bool s_processMemoryUnavailable;

        private static void SampleMemory()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - s_memorySampledAt < MemorySampleInterval) return;
            s_memorySampledAt = now;

            var m = new MemorySample
            {
                MonoUsed = Profiler.GetMonoUsedSizeLong(),
                MonoHeap = Profiler.GetMonoHeapSizeLong(),
                NativeUsed = Profiler.GetTotalAllocatedMemoryLong(),
                NativeReserved = Profiler.GetTotalReservedMemoryLong(),
                GcCount = GC.CollectionCount(0),
            };
            m.HasProcess = TryGetProcessMemory(out m.Private, out m.PeakPrivate, out m.WorkingSet);
            s_memory = m;
        }

        // プロセスのメモリ(Windows の GetProcessMemoryInfo)。MemoryBenchTests と同じ値を読む。
        private static bool TryGetProcessMemory(out long privateBytes, out long peakPrivate, out long workingSet)
        {
            privateBytes = peakPrivate = workingSet = 0;
            if (s_processMemoryUnavailable || Application.platform != RuntimePlatform.WindowsEditor) return false;
            try
            {
                if (!GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
                    return false;
                privateBytes = (long)c.PagefileUsage.ToUInt64();
                peakPrivate = (long)c.PeakPagefileUsage.ToUInt64();
                workingSet = (long)c.WorkingSetSize.ToUInt64();
                return true;
            }
            catch (Exception)
            {
                // DLL・関数が無い環境では以後読まない(行ごと出さない)。
                s_processMemoryUnavailable = true;
                return false;
            }
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N0");

        private static void DrawMemory()
        {
            EditorGUILayout.LabelField(
                new GUIContent("メモリ",
                    "Unity エディタのプロセス全体の値です(いろか以外のウィンドウやアセットも含みます)。\n" +
                    $"このウィンドウを描き直したときに読み直します({MemorySampleInterval:0.0#} 秒おき。放置中は止まります)。"),
                EditorStyles.boldLabel);

            var m = s_memory;
            DrawValueRow("Mono ヒープ", $"使用 {Mb(m.MonoUsed)} / 確保 {Mb(m.MonoHeap)} MB",
                "C# のマネージドメモリ。いろかの画素配列・作業配列・選択キャッシュなどはここに入ります。\n" +
                "使用: 生きているオブジェクトと、まだ回収されていないゴミの合計。\n" +
                "確保: ヒープ全体の大きさ。Unity の Mono は一度広げたヒープを OS に返さないので、" +
                "処理中のピークに引きずられて増え、編集をやめても減りません。",
                MemoryLabelWidth);
            DrawValueRow("Unity ネイティブ", $"使用 {Mb(m.NativeUsed)} / 確保 {Mb(m.NativeReserved)} MB",
                "Unity エンジン側のメモリ(テクスチャ・メッシュなどのアセットとエディタ本体)。\n" +
                "プレビューやシーンに映すテクスチャはここに入ります。\n" +
                "使用: Unity の内部アロケータが使っている量。確保: 予約している量。",
                MemoryLabelWidth);
            if (m.HasProcess)
            {
                DrawValueRow("プロセス", $"{Mb(m.Private)} MB (最大 {Mb(m.PeakPrivate)} MB)",
                    "Unity エディタのプロセスがコミットしているメモリ(Private Bytes)。Mono ヒープとネイティブの両方を含みます。\n" +
                    "最大: プロセスを起動してからの最大値。",
                    MemoryLabelWidth);
                DrawValueRow("物理メモリ", $"{Mb(m.WorkingSet)} MB",
                    "そのうち実際に物理メモリ(RAM)に載っている量(ワーキングセット。共有メモリを含みます)。",
                    MemoryLabelWidth);
            }
            DrawValueRow("GC 回数", $"{m.GcCount} 回",
                "起動してからのガベージコレクションの回数。操作のあとに増えていれば、その操作で回収が走っています" +
                "(回収の間はエディタが止まります)。",
                MemoryLabelWidth);
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
