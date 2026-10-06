// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
//
// Unity 不要の部品を検査する最小のテストランナー（NuGet に依存しない）。
//
// テストの足し方: Tests クラスに `public static void 名前()` を書くだけ。リフレクションで全件拾う。
// 失敗は例外（Check.* が投げる）で表す。出力は 1 行 1 件:
//   PASS <名前>
//   FAIL <名前>: <理由>
// 終了コードは失敗件数。pytest(test_unit_run.py) はこの出力を件ごとに読む。
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Iroca.UnitRun
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var tests = typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetParameters().Length == 0 && m.ReturnType == typeof(void))
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList();
            if (args.Length > 0 && args[0] == "--list")
            {
                foreach (var t in tests) Console.WriteLine(t.Name);
                return 0;
            }
            int failed = 0;
            foreach (var t in tests)
            {
                try
                {
                    t.Invoke(null, null);
                    Console.WriteLine($"PASS {t.Name}");
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    string msg = (ex.InnerException?.Message ?? ex.Message).Replace('\n', ' ').Replace('\r', ' ');
                    Console.WriteLine($"FAIL {t.Name}: {msg}");
                }
            }
            return failed;
        }
    }

    internal static class Check
    {
        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!Equals(expected, actual))
                throw new Exception($"{what}: expected <{expected}> but was <{actual}>");
        }

        public static void True(bool cond, string what)
        {
            if (!cond) throw new Exception(what);
        }

        public static TEx Throws<TEx>(Action a, string what) where TEx : Exception
        {
            try { a(); }
            catch (TEx ex) { return ex; }
            catch (Exception ex) { throw new Exception($"{what}: expected {typeof(TEx).Name} but got {ex.GetType().Name}"); }
            throw new Exception($"{what}: expected {typeof(TEx).Name} but nothing was thrown");
        }
    }

    /// <summary>テスト本体。1 メソッド = 1 件。</summary>
    public static class Tests
    {
        // ─── PathUtils.SanitizeFileName（プリセット保存・エクスポートの新規ファイル名） ───

        private static string San(string s) => PathUtils.SanitizeFileName(s, "fallback");

        public static void Sanitize_KeepsOrdinaryName() => Check.Equal("my_preset 01", San("my_preset 01"), "ordinary");
        public static void Sanitize_KeepsJapanese() => Check.Equal("青い服", San("青い服"), "japanese");
        public static void Sanitize_EmptyUsesFallback()
        {
            Check.Equal("fallback", San(null), "null");
            Check.Equal("fallback", San(""), "empty");
            Check.Equal("fallback", San("   "), "spaces");
        }

        public static void Sanitize_CannotEscapeFolder()
        {
            // 区切りを置換するので、どう書いても同じフォルダの 1 ファイル名にしかならない。
            foreach (var s in new[] { "../evil", "..\\evil", "a/b", "a\\b", "/abs", "C:\\x\\y" })
            {
                string r = San(s);
                Check.True(r.IndexOf('/') < 0 && r.IndexOf('\\') < 0, $"separator left in <{r}> from <{s}>");
                Check.True(r != ".." && r != ".", $"dot name <{r}> from <{s}>");
            }
            Check.Equal("fallback", San(".."), "dotdot alone");
        }

        public static void Sanitize_TrimsTrailingDotsAndSpaces()
        {
            // Windows は末尾の '.' / ' ' を無言で落とすので、残すと保存名と参照名がずれる。
            Check.Equal("name", San("name. . "), "trailing");
            Check.Equal("a.b", San("a.b"), "inner dot kept");
        }

        public static void Sanitize_EscapesWindowsReservedNames()
        {
            Check.Equal("_CON", San("CON"), "CON");
            Check.Equal("_con", San("con"), "lowercase");
            Check.Equal("_LPT1.backup", San("LPT1.backup"), "with extension");
            Check.Equal("CONSOLE", San("CONSOLE"), "prefix only is not reserved");
        }

        public static void Sanitize_ReplacesInvalidChars()
        {
            string r = San("a:b*c?d\"e<f>g|h");
            foreach (char c in Path.GetInvalidFileNameChars())
                Check.True(r.IndexOf(c) < 0, $"invalid char U+{(int)c:X4} left in <{r}>");
        }

        // ─── PathUtils.ToAssetsRelativeOrNull（出力先が Assets 配下かの判定） ───

        private static readonly string ProjectRoot =
            Path.Combine(Path.GetTempPath(), "iroca_unitrun_project").Replace('\\', '/');
        private static readonly string AssetsFolder = ProjectRoot + "/Assets";

        private static string Rel(string p) => PathUtils.ToAssetsRelativeOrNull(p, AssetsFolder);

        public static void AssetsRel_RelativeInside() => Check.Equal("Assets/Tex/a.png", Rel("Assets/Tex/a.png"), "relative");
        public static void AssetsRel_AbsoluteInside() => Check.Equal("Assets/Tex/a.png", Rel(AssetsFolder + "/Tex/a.png"), "absolute");
        public static void AssetsRel_AssetsFolderItself() => Check.Equal("Assets", Rel(AssetsFolder), "folder");
        public static void AssetsRel_ParentTraversalRejected() => Check.Equal(null, Rel("Assets/../evil.png"), "traversal");
        public static void AssetsRel_SiblingPrefixRejected() => Check.Equal(null, Rel(ProjectRoot + "/AssetsExtra/a.png"), "prefix trap");
        public static void AssetsRel_OutsideRejected() => Check.Equal(null, Rel(ProjectRoot + "/Packages/a.png"), "outside");
        public static void AssetsRel_EmptyIsNull()
        {
            Check.Equal(null, Rel(null), "null");
            Check.Equal(null, Rel(""), "empty");
        }

        public static void AssetsRel_BackslashesNormalized()
            => Check.Equal("Assets/Tex/a.png", Rel((AssetsFolder + "/Tex/a.png").Replace('/', Path.DirectorySeparatorChar)), "native separators");

        public static void AssetsRel_DriveLetterCaseInsensitive()
        {
            // Windows ではドライブレターの大小が揺れる（C:/ と c:/）。他 OS では該当しない。
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            string flipped = char.IsUpper(AssetsFolder[0])
                ? char.ToLowerInvariant(AssetsFolder[0]) + AssetsFolder.Substring(1)
                : char.ToUpperInvariant(AssetsFolder[0]) + AssetsFolder.Substring(1);
            Check.Equal("Assets/a.png", Rel(flipped + "/a.png"), "drive letter case");
        }

        // ─── AtomicFile（エクスポート・セッション・マスク保存の書き込み） ───

        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "iroca_unitrun_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void Atomic_CreatesNewFile()
        {
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "new.txt");
                AtomicFile.WriteAllText(p, "こんにちは");
                Check.Equal("こんにちは", File.ReadAllText(p), "content");
                var bytes = File.ReadAllBytes(p);
                Check.True(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "no BOM");
                Check.True(!File.Exists(p + ".tmp"), "tmp removed");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void Atomic_OverwritesExisting()
        {
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "a.bin");
                File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
                AtomicFile.WriteAllBytes(p, new byte[] { 9, 8 });
                Check.Equal("9,8", string.Join(",", File.ReadAllBytes(p)), "content");
                Check.True(!File.Exists(p + ".tmp"), "tmp removed");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void Atomic_LockedTargetKeepsOriginal()
        {
            // 置換先が他プロセスに開かれたまま（Unity が読み込み中など）だと、しばらく繰り返したあと失敗する。
            // そのとき原本は元のまま残り、書きかけの一時ファイルも残らないこと。
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // Linux は開いたままでも置換できる
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "locked.png");
                File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
                using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Check.Throws<IOException>(() => AtomicFile.WriteAllBytes(p, new byte[] { 7 }), "locked");
                }
                Check.Equal("1,2,3", string.Join(",", File.ReadAllBytes(p)), "original intact");
                Check.True(!File.Exists(p + ".tmp"), "tmp removed");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void Atomic_RetriesWhileTargetIsBrieflyLocked()
        {
            // ウイルス対策・検索インデクサなどが置換先を一瞬つかんでいる間は、待って繰り返して書き切る
            // （以前はここで「置換されるファイルを削除できません」になり、セッション保存が失敗していた）。
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "busy.json");
                File.WriteAllBytes(p, new byte[] { 1 });
                var holder = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
                var release = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(100);
                    holder.Dispose();
                });
                AtomicFile.WriteAllBytes(p, new byte[] { 2, 3 });
                release.Wait();
                Check.Equal("2,3", string.Join(",", File.ReadAllBytes(p)), "content after the lock was released");
                Check.True(!File.Exists(p + ".tmp"), "tmp removed");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void Atomic_MissingFolderThrowsWithoutLeftovers()
        {
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "no_such_dir", "x.txt");
                Check.Throws<DirectoryNotFoundException>(() => AtomicFile.WriteAllText(p, "x"), "missing dir");
                Check.True(Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length == 0, "nothing written");
            }
            finally { Directory.Delete(d, true); }
        }

        // ─── PreviewLatency（プレビューの体感時間: 操作 → 画面） ───
        // 周波数 1000 = 1 タイムスタンプ 1 ms として時刻を直接書く。

        private const long Hz = 1000;

        private static void SetStage(LatencyStageMarks m, long scheduled, long workStart, long coreStart,
            long coreEnd, long workEnd, long applied, long uploadStart, long uploadEnd)
        {
            m.Scheduled = scheduled; m.WorkStart = workStart; m.CoreStart = coreStart; m.CoreEnd = coreEnd;
            m.WorkEnd = workEnd; m.Applied = applied; m.UploadStart = uploadStart; m.UploadEnd = uploadEnd;
        }

        // 操作 100 → 準備 110..112 → プロキシ(コア 20ms) が 152 に表示 → フル(コア 200ms) が 363 に表示。
        private static PreviewLatencyCycle ProxyAndFullCycle()
        {
            var c = new PreviewLatencyCycle { Input = 100, FirstInput = 100, PrepStart = 110, PrepEnd = 112 };
            SetStage(c.Proxy, 112, 113, 115, 135, 136, 140, 150, 152);
            SetStage(c.Full, 140, 141, 145, 345, 350, 355, 360, 363);
            return c;
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

        public static void Latency_ProxyAndFull_MilestonesAndSums()
        {
            var r = PreviewLatencyReport.Build(ProxyAndFullCycle(), Hz);
            Check.True(r.HasInput, "has input");
            Check.True(Near(52, r.FirstShownMs), $"first shown = proxy upload end (52), was {r.FirstShownMs}");
            Check.True(Near(263, r.FinalShownMs), $"final shown = full upload end (263), was {r.FinalShownMs}");
            Check.True(double.IsNaN(r.DetailShownMs), "no detail");
            Check.True(Near(263, r.LastShownMs), "last shown = final without detail");
            Check.True(Near(52, r.StaleMs), "stale = first shown when the first input is the last");
            // メインスレッド = 準備 2 + プロキシ転送 2 + フル転送 3。コア = 20 + 200。
            Check.True(Near(7, r.MainThreadMs), $"main thread 7, was {r.MainThreadMs}");
            Check.True(Near(220, r.CoreMs), $"core 220, was {r.CoreMs}");
            Check.Equal(16, r.Segments.Length, "2 request + 7 proxy + 7 full segments");
            Check.True(r.Segments.All(s => s.EndMs >= s.StartMs), "segments are not reversed");
        }

        public static void Latency_WaitIsInputToGenerationStart()
        {
            var r = PreviewLatencyReport.Build(ProxyAndFullCycle(), Hz);
            var wait = r.Segments.Single(s => s.Lane == PreviewLatencyReport.LaneRequest && s.Kind == LatencyKind.Wait);
            Check.True(Near(0, wait.StartMs) && Near(10, wait.EndMs), $"wait 0..10, was {wait.StartMs}..{wait.EndMs}");
        }

        public static void Latency_NoProxy_FirstEqualsFinal()
        {
            var c = new PreviewLatencyCycle { Input = 100, FirstInput = 100, PrepStart = 101, PrepEnd = 102 };
            SetStage(c.Full, 102, 103, 104, 204, 205, 210, 220, 222);
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(Near(122, r.FinalShownMs), "final");
            Check.True(Near(r.FinalShownMs, r.FirstShownMs), "first == final without proxy");
            Check.True(r.Segments.All(s => s.Lane != PreviewLatencyReport.LaneProxy), "no proxy lane");
        }

        public static void Latency_DragFinish_ShowsWaitBeforeFull()
        {
            // ドラッグの追従: プロキシが 152 に出たあと、手を止めて 0.2 秒後にフル段を投入した。
            var c = new PreviewLatencyCycle { Input = 100, FirstInput = 100, PrepStart = 110, PrepEnd = 112 };
            SetStage(c.Proxy, 112, 113, 115, 135, 136, 140, 150, 152);
            SetStage(c.Full, 340, 341, 345, 545, 550, 555, 560, 563);
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(Near(52, r.FirstShownMs), "first shown is the drag proxy");
            Check.True(Near(463, r.FinalShownMs), "final shown");
            var wait = r.Segments.Single(s => s.Lane == PreviewLatencyReport.LaneFull && s.Kind == LatencyKind.Wait
                                              && s.Name.StartsWith("待ち"));
            Check.True(Near(40, wait.StartMs) && Near(240, wait.EndMs), $"wait 40..240, was {wait.StartMs}..{wait.EndMs}");
        }

        public static void Latency_ChainedFull_HasNoStartWait()
        {
            // 通常の直列ではプロキシの受け取りと同時にフル段を投入するので、待ちの区間は出さない。
            var r = PreviewLatencyReport.Build(ProxyAndFullCycle(), Hz);
            Check.True(!r.Segments.Any(s => s.Lane == PreviewLatencyReport.LaneFull && s.Name.StartsWith("待ち")),
                "no start wait in the chained case");
        }

        public static void Latency_Detail_WaitStartsAtFinalShown()
        {
            var c = ProxyAndFullCycle();
            c.Detail.PrepStart = 663;
            SetStage(c.Detail, 665, 666, 667, 707, 710, 712, 720, 722);
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(Near(622, r.DetailShownMs), $"detail shown 622, was {r.DetailShownMs}");
            Check.True(Near(622, r.LastShownMs), "last shown = detail");
            var wait = r.Segments.First(s => s.Lane == PreviewLatencyReport.LaneDetail);
            Check.True(wait.Kind == LatencyKind.Wait && Near(263, wait.StartMs) && Near(563, wait.EndMs),
                $"detail wait 263..563 (the debounce after the final), was {wait.StartMs}..{wait.EndMs}");
            Check.True(Near(7 + 2 + 2, r.MainThreadMs), $"main thread adds detail prep/upload, was {r.MainThreadMs}");
            Check.True(Near(260, r.CoreMs), $"core adds detail 40, was {r.CoreMs}");
        }

        public static void Latency_DetailNotShown_IsIgnored()
        {
            var c = ProxyAndFullCycle();
            c.Detail.PrepStart = 663;
            c.Detail.Scheduled = 665;
            c.Detail.WorkStart = 666; // 取り消されて画面に出なかった
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(double.IsNaN(r.DetailShownMs), "no detail");
            Check.True(r.Segments.All(s => s.Lane != PreviewLatencyReport.LaneDetail), "no detail lane");
        }

        public static void Latency_Stale_CountsFromFirstUnshownInput()
        {
            var c = ProxyAndFullCycle();
            c.FirstInput = 40; // ドラッグの開始。最後の操作(100)の 60ms 前
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(Near(112, r.StaleMs), $"stale = 60 + first shown 52, was {r.StaleMs}");
            Check.True(Near(52, r.FirstShownMs), "milestones stay relative to the last input");
        }

        public static void Latency_ProxyOnly_ReportsFirstWithoutFinal()
        {
            // ドラッグの追従・確定前: プロキシだけ画面に出た段階でもレポートを出し、確定は未(NaN)。
            var c = ProxyAndFullCycle();
            c.Full.Applied = c.Full.UploadStart = c.Full.UploadEnd = 0;
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(r != null, "proxy-only cycle has a report");
            Check.True(!r.IsFinal && double.IsNaN(r.FinalShownMs), "final is pending");
            Check.True(Near(52, r.FirstShownMs), $"first shown = proxy (52), was {r.FirstShownMs}");
            Check.True(Near(52, r.LastShownMs), "last shown = proxy");
            Check.True(r.Segments.All(s => s.Lane != PreviewLatencyReport.LaneFull), "no full lane yet");
        }

        public static void Latency_NothingShown_ReturnsNull()
        {
            var c = ProxyAndFullCycle();
            c.Proxy.UploadEnd = 0;
            c.Full.UploadEnd = 0;
            Check.True(PreviewLatencyReport.Build(c, Hz) == null, "nothing on screen yet -> no report");
        }

        public static void Latency_NoInput_OriginIsGenerationStart()
        {
            var c = ProxyAndFullCycle();
            c.Input = 0;
            c.FirstInput = 0;
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(!r.HasInput, "no input");
            Check.True(Near(253, r.FinalShownMs), $"final from prep start (110), was {r.FinalShownMs}");
            Check.True(r.Segments.All(s => s.Kind != LatencyKind.Wait || s.Lane != PreviewLatencyReport.LaneRequest),
                "no input wait segment");
        }

        // スクロール・ズーム: 最後の操作 100 → 手を止めて間引きの待ち(ここでは 300)のあと拡大表示の準備 400..402 →
        // 切り出し(コア無し) → 430 に表示。プロキシ・フル段は無い。
        private static PreviewLatencyCycle ViewChangeCycle()
        {
            var c = new PreviewLatencyCycle { ViewChange = true, Input = 100 };
            c.Detail.PrepStart = 400;
            SetStage(c.Detail, 402, 403, 0, 0, 415, 420, 426, 430);
            return c;
        }

        public static void Latency_ViewChange_MeasuresDetailFromLastViewChange()
        {
            var r = PreviewLatencyReport.Build(ViewChangeCycle(), Hz);
            Check.True(r != null && r.ViewChange && r.HasInput, "view-change cycle has a report");
            Check.True(Near(330, r.DetailShownMs), $"detail shown 330, was {r.DetailShownMs}");
            Check.True(Near(330, r.LastShownMs), "last shown = detail");
            Check.True(double.IsNaN(r.FirstShownMs) && double.IsNaN(r.FinalShownMs) && double.IsNaN(r.StaleMs),
                "no first / final / stale for a view change");
            var wait = r.Segments.Single(s => s.Lane == PreviewLatencyReport.LaneRequest);
            Check.True(wait.Kind == LatencyKind.Wait && Near(0, wait.StartMs) && Near(300, wait.EndMs),
                $"request wait 0..300 (the debounce), was {wait.StartMs}..{wait.EndMs}");
            Check.True(r.Segments.All(s => s.Lane == PreviewLatencyReport.LaneRequest
                                           || s.Lane == PreviewLatencyReport.LaneDetail),
                "only the request and detail lanes");
            // メインスレッド = 切り出し範囲の計算 2 + 転送 4。コア処理は無い(フル段の出力の切り出しだけ)。
            Check.True(Near(6, r.MainThreadMs), $"main thread 6, was {r.MainThreadMs}");
            Check.True(Near(0, r.CoreMs), $"no core, was {r.CoreMs}");
        }

        public static void Latency_ViewChange_DetailNotShown_ReturnsNull()
        {
            var c = ViewChangeCycle();
            c.Detail.UploadStart = c.Detail.UploadEnd = 0; // 次のスクロールで取り消された
            Check.True(PreviewLatencyReport.Build(c, Hz) == null, "view change with nothing on screen -> no report");
        }

        public static void Latency_CoreReports_FollowShownStages()
        {
            var c = ProxyAndFullCycle();
            c.Proxy.CoreReport = new PerfReport(20, 512, 512, new ZonePerfEntry[0]);
            c.Full.CoreReport = new PerfReport(200, 4096, 4096, new ZonePerfEntry[0]);
            var r = PreviewLatencyReport.Build(c, Hz);
            Check.True(ReferenceEquals(c.Proxy.CoreReport, r.ProxyCore), "proxy core of this operation");
            Check.True(ReferenceEquals(c.Full.CoreReport, r.FullCore), "full core of this operation");

            // 確定前(ドラッグの追従): フル段の計測はワーカーが書き終えていても、画面に出るまで載せない。
            c.Full.Applied = c.Full.UploadStart = c.Full.UploadEnd = 0;
            r = PreviewLatencyReport.Build(c, Hz);
            Check.True(ReferenceEquals(c.Proxy.CoreReport, r.ProxyCore) && r.FullCore == null,
                "full core is not shown before the final");
        }

        public static void Latency_ViewChange_HasNoCoreReport()
        {
            var r = PreviewLatencyReport.Build(ViewChangeCycle(), Hz);
            Check.True(r.ProxyCore == null && r.FullCore == null, "scroll / zoom runs no core");
        }

        public static void InputClock_CarriesUnshownInputsAcrossCancelledCycles()
        {
            var clock = new PreviewInputClock();
            clock.NoteInput(10);
            clock.NoteInput(20);
            var c1 = new PreviewLatencyCycle();
            clock.Snapshot(c1);
            Check.Equal(20L, c1.Input, "c1 last input");
            Check.Equal(10L, c1.FirstInput, "c1 first input");

            // c1 は新しい操作で取り消された。画面はまだ止まったまま。
            clock.NoteInput(30);
            var c2 = new PreviewLatencyCycle();
            clock.Snapshot(c2);
            Check.Equal(30L, c2.Input, "c2 last input");
            Check.Equal(10L, c2.FirstInput, "c2 keeps the first unshown input");

            // c2 のスナップショット後の操作は、c2 が画面に出てもまだ反映されていない。
            clock.NoteInput(35);
            clock.NoteShown();
            var c3 = new PreviewLatencyCycle();
            clock.Snapshot(c3);
            Check.Equal(35L, c3.FirstInput, "input after the shown snapshot carries over");

            clock.NoteShown();
            clock.NoteInput(50);
            var c4 = new PreviewLatencyCycle();
            clock.Snapshot(c4);
            Check.Equal(50L, c4.FirstInput, "fresh start after everything was shown");
        }

        // ─── MeshRaycast（Scene でモデルをクリックした場所をプレビューで示す） ───

        // z = 0 の平面に置いた 1×1 の四角(2 枚の三角形)。UV は位置の xy と同じ。
        private static readonly UnityEngine.Vector3[] QuadVerts =
        {
            new UnityEngine.Vector3(0, 0, 0), new UnityEngine.Vector3(1, 0, 0),
            new UnityEngine.Vector3(1, 1, 0), new UnityEngine.Vector3(0, 1, 0),
        };
        private static readonly UnityEngine.Vector2[] QuadUv =
        {
            new UnityEngine.Vector2(0, 0), new UnityEngine.Vector2(1, 0),
            new UnityEngine.Vector2(1, 1), new UnityEngine.Vector2(0, 1),
        };
        private static readonly int[] QuadTris = { 0, 1, 2, 0, 2, 3 };

        private static void Near(float expected, float actual, string what)
        {
            if (Math.Abs(expected - actual) > 1e-4f)
                throw new Exception($"{what}: expected <{expected}> but was <{actual}>");
        }

        public static void Raycast_HitsQuadAndInterpolatesUv()
        {
            bool hit = MeshRaycast.Intersect(new UnityEngine.Vector3(0.25f, 0.75f, -1f), new UnityEngine.Vector3(0, 0, 1),
                                             QuadVerts, QuadTris, float.PositiveInfinity, MeshRaycast.Faces.Both, out var h);
            Check.True(hit, "hit");
            Check.Equal(1, h.triangle, "upper-left triangle");
            Near(1f, h.t, "distance");
            var uv = MeshRaycast.InterpolateUv(QuadUv, QuadTris, h);
            Near(0.25f, uv.x, "u");
            Near(0.75f, uv.y, "v");
        }

        public static void Raycast_HitsBackFace()
        {
            // 両面を描くマテリアル(Faces.Both)では、服の内側の裏面もクリックで拾う。
            bool hit = MeshRaycast.Intersect(new UnityEngine.Vector3(0.6f, 0.2f, 2f), new UnityEngine.Vector3(0, 0, -1),
                                             QuadVerts, QuadTris, float.PositiveInfinity, MeshRaycast.Faces.Both, out var h);
            Check.True(hit, "hit from behind");
            Near(2f, h.t, "distance");
            var uv = MeshRaycast.InterpolateUv(QuadUv, QuadTris, h);
            Near(0.6f, uv.x, "u");
            Near(0.2f, uv.y, "v");
        }

        public static void Raycast_RespectsCulledFaces()
        {
            // QuadTris の並びは cross(b − a, c − a) が +z を向く = 表は +z 側。
            var fromFront = new UnityEngine.Vector3(0.5f, 0.4f, 1f);
            var fromBack = new UnityEngine.Vector3(0.5f, 0.4f, -1f);
            var toMinusZ = new UnityEngine.Vector3(0, 0, -1);
            var toPlusZ = new UnityEngine.Vector3(0, 0, 1);
            Check.True(MeshRaycast.Intersect(fromFront, toMinusZ, QuadVerts, QuadTris, float.PositiveInfinity,
                                             MeshRaycast.Faces.Front, out _), "front face, front only");
            Check.True(!MeshRaycast.Intersect(fromBack, toPlusZ, QuadVerts, QuadTris, float.PositiveInfinity,
                                              MeshRaycast.Faces.Front, out _), "back face is culled");
            Check.True(MeshRaycast.Intersect(fromBack, toPlusZ, QuadVerts, QuadTris, float.PositiveInfinity,
                                             MeshRaycast.Faces.Back, out _), "back face, back only");
            Check.True(!MeshRaycast.Intersect(fromFront, toMinusZ, QuadVerts, QuadTris, float.PositiveInfinity,
                                              MeshRaycast.Faces.Back, out _), "front face is culled");

            // 手前の面が消える向きなら、奥で表を向いている面に当たる(カメラが服の内側にあるとき)。
            // 手前 z = 0 は表が +z(光線から見て裏)、奥 z = 1 は並びを逆にして表が −z(光線から見て表)。
            var verts = new UnityEngine.Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                verts[i] = QuadVerts[i];
                verts[i + 4] = QuadVerts[i] + new UnityEngine.Vector3(0, 0, 1);
            }
            int[] tris = { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
            Check.True(MeshRaycast.Intersect(fromBack, toPlusZ, verts, tris, float.PositiveInfinity,
                                             MeshRaycast.Faces.Front, out var h), "far front face");
            Check.True(h.triangle >= 2, $"far quad expected, got triangle {h.triangle}");
            Near(2f, h.t, "distance to the far quad");
            Check.True(MeshRaycast.Intersect(fromBack, toPlusZ, verts, tris, float.PositiveInfinity,
                                             MeshRaycast.Faces.Both, out h) && h.triangle < 2, "both faces: the near quad");
        }

        public static void Raycast_PicksNearestTriangle()
        {
            // 奥(z = 1)の四角を先に並べても、手前(z = 0)が選ばれる。
            var verts = new UnityEngine.Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                verts[i] = QuadVerts[i] + new UnityEngine.Vector3(0, 0, 1);
                verts[i + 4] = QuadVerts[i];
            }
            int[] tris = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            bool hit = MeshRaycast.Intersect(new UnityEngine.Vector3(0.5f, 0.4f, -1f), new UnityEngine.Vector3(0, 0, 1),
                                             verts, tris, float.PositiveInfinity, MeshRaycast.Faces.Both, out var h);
            Check.True(hit, "hit");
            Check.True(h.triangle >= 2, $"front quad expected, got triangle {h.triangle}");
            Near(1f, h.t, "distance to the front quad");
        }

        public static void Raycast_Misses()
        {
            var origin = new UnityEngine.Vector3(0.5f, 0.5f, -1f);
            Check.True(!MeshRaycast.Intersect(new UnityEngine.Vector3(1.5f, 0.5f, -1f), new UnityEngine.Vector3(0, 0, 1),
                                              QuadVerts, QuadTris, float.PositiveInfinity, MeshRaycast.Faces.Both, out _), "outside the quad");
            Check.True(!MeshRaycast.Intersect(origin, new UnityEngine.Vector3(0, 0, -1),
                                              QuadVerts, QuadTris, float.PositiveInfinity, MeshRaycast.Faces.Both, out _), "pointing away");
            Check.True(!MeshRaycast.Intersect(origin, new UnityEngine.Vector3(1, 0, 0),
                                              QuadVerts, QuadTris, float.PositiveInfinity, MeshRaycast.Faces.Both, out _), "parallel");
            Check.True(!MeshRaycast.Intersect(origin, new UnityEngine.Vector3(0, 0, 1),
                                              QuadVerts, QuadTris, 0.5f, MeshRaycast.Faces.Both, out _), "farther than maxT");
            Check.True(!MeshRaycast.Intersect(origin, new UnityEngine.Vector3(0, 0, 1),
                                              QuadVerts, new[] { 0, 1, 9 }, float.PositiveInfinity, MeshRaycast.Faces.Both, out _), "bad index is skipped");
        }

        public static void Raycast_WrapToTexture()
        {
            var w = MeshRaycast.WrapToTexture(new UnityEngine.Vector2(1.25f, -0.5f), out var shift);
            Near(0.25f, w.x, "u wrapped");
            Near(0.5f, w.y, "v wrapped");
            Near(1f, shift.x, "u shift");
            Near(-1f, shift.y, "v shift");
            // 0..1 の内側(端の 1 を含む)はそのまま。1.0 を 0 へ畳むと端の画素が反対側へ飛ぶ。
            w = MeshRaycast.WrapToTexture(new UnityEngine.Vector2(1f, 0f), out shift);
            Near(1f, w.x, "u = 1 kept");
            Near(0f, w.y, "v = 0 kept");
            Near(0f, shift.x + shift.y, "no shift");
        }

        // ─── MaskRegionEdit(右クリックの「ここも塗る / ここは塗らない / この部分だけ塗る」) ───
        // 画素は 6 つ: 範囲 = [0..2]、範囲の外 = [3..5]。

        private static bool[] Mask(params int[] on)
        {
            var m = new bool[6];
            foreach (int i in on) m[i] = true;
            return m;
        }

        private static string Bits(bool[] m)
        {
            var c = new char[m.Length];
            for (int i = 0; i < m.Length; i++) c[i] = m[i] ? '1' : '0';
            return new string(c);
        }

        private static readonly bool[] Region = Mask(0, 1, 2);

        public static void MaskEdit_PaintHere_IncludesAndClearsZoneExclusion()
        {
            var ex = Mask(1, 4);           // 範囲の中 1 と外 4 を外していた
            var inc = Mask(5);
            var common = Mask(2, 3);
            var r = MaskRegionEdit.Apply(MaskRegionOp.PaintHere, Region, ex, inc, common);
            Check.Equal("111001", Bits(inc), "include = 範囲 + 既存");
            Check.Equal("000010", Bits(ex), "範囲の中の除外だけ消える");
            Check.Equal("001100", Bits(common), "共通の除外は触らない");
            Check.Equal(1, r.commonOverlap, "範囲の中の共通の重なり");
            Check.Equal(4, r.changed, "含める 3 + 除外を消す 1");
        }

        public static void MaskEdit_PaintHere_AllowsMissingExclusion()
        {
            var inc = Mask();
            var r = MaskRegionEdit.Apply(MaskRegionOp.PaintHere, Region, null, inc, null);
            Check.Equal("111000", Bits(inc), "include");
            Check.Equal(0, r.commonOverlap, "no common");
        }

        public static void MaskEdit_DontPaintHere_ExcludesAndClearsInclude()
        {
            var ex = Mask(4);
            var inc = Mask(0, 5);
            var r = MaskRegionEdit.Apply(MaskRegionOp.DontPaintHere, Region, ex, inc, null);
            Check.Equal("111010", Bits(ex), "exclude = 範囲 + 既存");
            Check.Equal("000001", Bits(inc), "範囲の中の含めるだけ消える");
            Check.Equal(4, r.changed, "除外 3 + 含めるを消す 1");
        }

        public static void MaskEdit_DontPaintHere_CommonWithoutInclude()
        {
            var common = Mask(5);
            MaskRegionEdit.Apply(MaskRegionOp.DontPaintHere, Region, common, null, null);
            Check.Equal("111001", Bits(common), "共通の除外へ足す");
        }

        public static void MaskEdit_OnlyThisPart_ExcludesOutsideOnly()
        {
            var ex = Mask(1);              // 範囲の中で外していた所は、この部分を塗るので消す
            var inc = Mask(2, 4);          // 範囲の外の含めるは消し、中は残す
            var common = Mask(0);
            var r = MaskRegionEdit.Apply(MaskRegionOp.OnlyThisPart, Region, ex, inc, common);
            Check.Equal("000111", Bits(ex), "範囲の外だけ除外");
            Check.Equal("001000", Bits(inc), "範囲の中の含めるは残る(足しもしない)");
            Check.Equal(1, r.commonOverlap, "範囲の中の共通の重なり");
            Check.Equal(5, r.changed, "外 3 + 中の除外 1 + 外の含める 1");
        }

        public static void MaskEdit_LaterInstructionWins()
        {
            // 塗る → 塗らない: 除外だけが残る(含めるが残っていても除外が勝つが、見た目と食い違わないよう消す)。
            var ex = Mask();
            var inc = Mask();
            MaskRegionEdit.Apply(MaskRegionOp.PaintHere, Region, ex, inc, null);
            MaskRegionEdit.Apply(MaskRegionOp.DontPaintHere, Region, ex, inc, null);
            Check.Equal("111000", Bits(ex), "paint→dont: excluded");
            Check.Equal("000000", Bits(inc), "paint→dont: not included");
            // 塗らない → 塗る: 含めるだけが残る(除外が残ると合成で含めるが無効になる)。
            MaskRegionEdit.Apply(MaskRegionOp.PaintHere, Region, ex, inc, null);
            Check.Equal("000000", Bits(ex), "dont→paint: not excluded");
            Check.Equal("111000", Bits(inc), "dont→paint: included");
        }

        public static void MaskEdit_BrushPaintClearsOppositeLayerInSpanOnly()
        {
            // 含めるを [1, 4) に塗る: 同じゾーンの除外はその区間だけ消え、区間の外(0, 4, 5)は残る。
            var inc = Mask();
            var ex = Mask(0, 1, 2, 3, 4, 5);
            MaskRegionEdit.BrushSpan(inc, ex, 1, 4, paint: true);
            Check.Equal("011100", Bits(inc), "painted span");
            Check.Equal("100011", Bits(ex), "opposite cleared in span only");
        }

        public static void MaskEdit_BrushEraseKeepsOppositeLayer()
        {
            // 消しゴムは塗っている層だけを消す(反対側の層まで消すと、消しただけで別の指定が消える)。
            var inc = Mask(0, 1, 2);
            var ex = Mask(1, 2, 3);
            MaskRegionEdit.BrushSpan(inc, ex, 0, 6, paint: false);
            Check.Equal("000000", Bits(inc), "erased");
            Check.Equal("011100", Bits(ex), "opposite untouched");
            // 反対側の層が無い(null)ときも塗れる。
            MaskRegionEdit.BrushSpan(inc, null, 2, 3, paint: true);
            Check.Equal("001000", Bits(inc), "no opposite layer");
        }

        public static void MaskEdit_NoChangeReportsZero()
        {
            var ex = Mask(3, 4, 5);
            var r = MaskRegionEdit.Apply(MaskRegionOp.OnlyThisPart, Region, ex, null, null);
            Check.Equal(0, r.changed, "already only this part");
        }
    }
}
