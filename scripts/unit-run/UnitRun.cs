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

        // ─── PathUtils.ExportFolderFor（書き出しの置き場所。パッケージの中には書かない） ───

        public static void ExportFolder_InsideAssetsIsTheSameFolder()
        {
            Check.Equal("Assets/Tex", PathUtils.ExportFolderFor("Assets/Tex/a.png"), "Assets の中");
            Check.Equal("Assets", PathUtils.ExportFolderFor("Assets/a.png"), "Assets 直下");
        }

        public static void ExportFolder_PackageMirrorsUnderExports()
        {
            // VCC / ALCOM の更新でパッケージのフォルダは丸ごと入れ替わるので、Assets の下へ写す。
            Check.Equal("Assets/Iroca/Exports/Packages/com.example.outfit/Textures",
                PathUtils.ExportFolderFor("Packages/com.example.outfit/Textures/a.png"), "Packages");
            Check.Equal("Assets/Iroca/Exports/Packages/com.example.outfit/Textures",
                PathUtils.ExportFolderFor("Packages\\com.example.outfit\\Textures\\a.png"), "backslash");
        }

        public static void ExportFolder_RoundTripsToTheSourceFolder()
        {
            string src = "Packages/com.example.outfit/Textures";
            Check.Equal(src, PathUtils.SourceFolderOfExport(PathUtils.ExportFolderFor(src + "/a.png")), "round trip");
            Check.Equal("Assets/Tex", PathUtils.SourceFolderOfExport("Assets/Tex"), "ordinary folder");
            Check.Equal("Assets/Iroca/Exports", PathUtils.SourceFolderOfExport("Assets/Iroca/Exports"), "exports root");
            Check.Equal("Assets/Iroca/Exports/Assets/x", PathUtils.SourceFolderOfExport("Assets/Iroca/Exports/Assets/x"),
                "Assets の中の元は写さない");
        }

        public static void ExportFolder_ParentTraversalStaysInExports()
            => Check.Equal("Assets/Iroca/Exports", PathUtils.ExportFolderFor("Packages/../../evil/a.png"), "traversal");

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

        public static void Atomic_IfChanged_SkipsSameContent()
        {
            // 自動保存: 中身が同じなら書かない(更新時刻も変えない)。
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "same.json");
                Check.True(AtomicFile.WriteAllTextIfChanged(p, "{\"a\":\"マスク\"}"), "first write creates the file");
                var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(p, old);
                Check.True(!AtomicFile.WriteAllTextIfChanged(p, "{\"a\":\"マスク\"}"), "same content is not written");
                Check.Equal(old, File.GetLastWriteTimeUtc(p), "file untouched");
                Check.True(!File.Exists(p + ".tmp"), "no tmp");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void Atomic_IfChanged_WritesDifferentContent()
        {
            // 長さが同じでも中身が違えば書く。長さが違っても書く。
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "diff.json");
                File.WriteAllText(p, "abc");
                Check.True(AtomicFile.WriteAllTextIfChanged(p, "abd"), "same length, different bytes");
                Check.Equal("abd", File.ReadAllText(p), "content after same-length change");
                Check.True(AtomicFile.WriteAllTextIfChanged(p, "abde"), "different length");
                Check.Equal("abde", File.ReadAllText(p), "content after length change");
                var bytes = File.ReadAllBytes(p);
                Check.True(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "no BOM");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void Atomic_IfChanged_BomFileIsRewritten()
        {
            // 比べるのはバイト列。BOM 付きの同じ文字列は別物として BOM なしで書き直す(WriteAllText と同じ結果にそろえる)。
            string d = TempDir();
            try
            {
                string p = Path.Combine(d, "bom.json");
                File.WriteAllText(p, "x", new System.Text.UTF8Encoding(true));
                Check.True(AtomicFile.WriteAllTextIfChanged(p, "x"), "BOM file differs");
                Check.Equal("x", string.Join(",", File.ReadAllBytes(p).Select(b => ((char)b).ToString())), "no BOM after rewrite");
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

        // ─── MetaFile（書き出しでインポート設定を引き継ぐときの .meta の GUID の差し替え） ───

        private const string NewGuid = "0123456789abcdef0123456789abcdef";

        private static string Meta(string nl) =>
            "fileFormatVersion: 2" + nl
            + "guid: 5f1e2d3c4b5a69788796a5b4c3d2e1f0" + nl
            + "TextureImporter:" + nl
            + "  externalObjects: {}" + nl
            + "  spriteSheet:" + nl
            + "    sprites: []" + nl
            + "  - first: {fileID: 2800000, guid: 5f1e2d3c4b5a69788796a5b4c3d2e1f0, type: 3}" + nl;

        public static void Meta_ReplacesGuidLf()
        {
            Check.True(MetaFile.TryReplaceGuid(Meta("\n"), NewGuid, out var r), "LF の .meta で GUID の行が見つからない");
            Check.Equal(Meta("\n").Replace("guid: 5f1e2d3c4b5a69788796a5b4c3d2e1f0\n", "guid: " + NewGuid + "\n"), r, "LF");
        }

        public static void Meta_ReplacesGuidCrlfAndKeepsLineEnds()
        {
            // Windows の Git(autocrlf)で取り出した .meta。以前は CRLF の行末で一致せず、元の GUID のまま写していた。
            Check.True(MetaFile.TryReplaceGuid(Meta("\r\n"), NewGuid, out var r), "CRLF の .meta で GUID の行が見つからない");
            Check.True(r.Contains("guid: " + NewGuid + "\r\n"), "CRLF の行末が保たれていない");
            Check.Equal(Meta("\r\n").Split('\n').Length, r.Split('\n').Length, "行数");
            Check.True(!r.Replace("\r\n", "").Contains("\n"), "LF だけの行ができた");
        }

        public static void Meta_LeavesNestedReferencesAlone()
        {
            // 行頭でない guid:(入れ子の参照)は差し替えない。
            MetaFile.TryReplaceGuid(Meta("\n"), NewGuid, out var r);
            Check.True(r.Contains("{fileID: 2800000, guid: 5f1e2d3c4b5a69788796a5b4c3d2e1f0, type: 3}"), "入れ子の参照まで書き換えた");
        }

        public static void Meta_LastLineWithoutNewline()
        {
            Check.True(MetaFile.TryReplaceGuid("fileFormatVersion: 2\r\nguid: abc123", NewGuid, out var r), "末尾の改行が無い");
            Check.Equal("fileFormatVersion: 2\r\nguid: " + NewGuid, r, "content");
        }

        public static void Meta_NoGuidLineIsRefused()
        {
            Check.True(!MetaFile.TryReplaceGuid("fileFormatVersion: 2\nTextureImporter:\n", NewGuid, out var r), "GUID の行が無いのに成功した");
            Check.Equal(null, r, "replaced");
            Check.True(!MetaFile.TryReplaceGuid("guid: XYZ\n", NewGuid, out _), "16 進でない GUID");
            Check.True(!MetaFile.TryReplaceGuid("", NewGuid, out _), "empty");
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
        // 画素は 6 つ: 範囲 = [0..2]、範囲の外 = [3..5]。範囲は bool[]、マスクの層は 1 画素 1 ビットの語の並び。

        private static bool[] Mask(params int[] on)
        {
            var m = new bool[6];
            foreach (int i in on) m[i] = true;
            return m;
        }

        private static ulong[] Layer(params int[] on) => PackBits(6, on);

        private static ulong[] PackBits(int n, params int[] on)
        {
            var b = new ulong[(n + 63) >> 6];
            foreach (int i in on) b[i >> 6] |= 1UL << (i & 63);
            return b;
        }

        private static bool Bit(ulong[] b, int i) => (b[i >> 6] & (1UL << (i & 63))) != 0UL;

        private static string Bits(ulong[] b)
        {
            var c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = Bit(b, i) ? '1' : '0';
            return new string(c);
        }

        private static int CountBits(ulong[] b, int n)
        {
            int k = 0;
            for (int i = 0; i < n; i++) if (Bit(b, i)) k++;
            return k;
        }

        private static unsafe MaskRegionEditResult Apply(MaskRegionOp op, ulong[] ex, ulong[] inc, ulong[] common)
        {
            fixed (ulong* e = ex, i = inc, c = common) return MaskRegionEdit.Apply(op, Region, e, i, c);
        }

        private static unsafe void Span(ulong[] target, ulong[] opposite, int start, int end, bool paint)
        {
            fixed (ulong* t = target, o = opposite) MaskRegionEdit.BrushSpan(t, o, start, end, paint);
        }

        private static unsafe void Stamp(ulong[] target, ulong[] opposite, int w, int h, float cx, float cy, float r,
                                         bool paint, out int x0, out int y0, out int x1, out int y1)
        {
            fixed (ulong* t = target, o = opposite)
                MaskRegionEdit.StampDisk(t, o, w, h, cx, cy, r, paint, out x0, out y0, out x1, out y1);
        }

        private static readonly bool[] Region = Mask(0, 1, 2);

        public static void MaskEdit_PaintHere_IncludesAndClearsZoneExclusion()
        {
            var ex = Layer(1, 4);          // 範囲の中 1 と外 4 を外していた
            var inc = Layer(5);
            var common = Layer(2, 3);
            var r = Apply(MaskRegionOp.PaintHere, ex, inc, common);
            Check.Equal("111001", Bits(inc), "include = 範囲 + 既存");
            Check.Equal("000010", Bits(ex), "範囲の中の除外だけ消える");
            Check.Equal("001100", Bits(common), "共通の除外は触らない");
            Check.Equal(1, r.commonOverlap, "範囲の中の共通の重なり");
            Check.Equal(4, r.changed, "含める 3 + 除外を消す 1");
        }

        public static void MaskEdit_PaintHere_AllowsMissingExclusion()
        {
            var inc = Layer();
            var r = Apply(MaskRegionOp.PaintHere, null, inc, null);
            Check.Equal("111000", Bits(inc), "include");
            Check.Equal(0, r.commonOverlap, "no common");
        }

        public static void MaskEdit_DontPaintHere_ExcludesAndClearsInclude()
        {
            var ex = Layer(4);
            var inc = Layer(0, 5);
            var r = Apply(MaskRegionOp.DontPaintHere, ex, inc, null);
            Check.Equal("111010", Bits(ex), "exclude = 範囲 + 既存");
            Check.Equal("000001", Bits(inc), "範囲の中の含めるだけ消える");
            Check.Equal(4, r.changed, "除外 3 + 含めるを消す 1");
        }

        public static void MaskEdit_DontPaintHere_CommonWithoutInclude()
        {
            var common = Layer(5);
            Apply(MaskRegionOp.DontPaintHere, common, null, null);
            Check.Equal("111001", Bits(common), "共通の除外へ足す");
        }

        public static void MaskEdit_OnlyThisPart_ExcludesOutsideOnly()
        {
            var ex = Layer(1);             // 範囲の中で外していた所は、この部分を塗るので消す
            var inc = Layer(2, 4);         // 範囲の外の含めるは消し、中は残す
            var common = Layer(0);
            var r = Apply(MaskRegionOp.OnlyThisPart, ex, inc, common);
            Check.Equal("000111", Bits(ex), "範囲の外だけ除外");
            Check.Equal("001000", Bits(inc), "範囲の中の含めるは残る(足しもしない)");
            Check.Equal(1, r.commonOverlap, "範囲の中の共通の重なり");
            Check.Equal(5, r.changed, "外 3 + 中の除外 1 + 外の含める 1");
        }

        public static void MaskEdit_LaterInstructionWins()
        {
            // 塗る → 塗らない: 除外だけが残る(含めるが残っていても除外が勝つが、見た目と食い違わないよう消す)。
            var ex = Layer();
            var inc = Layer();
            Apply(MaskRegionOp.PaintHere, ex, inc, null);
            Apply(MaskRegionOp.DontPaintHere, ex, inc, null);
            Check.Equal("111000", Bits(ex), "paint→dont: excluded");
            Check.Equal("000000", Bits(inc), "paint→dont: not included");
            // 塗らない → 塗る: 含めるだけが残る(除外が残ると合成で含めるが無効になる)。
            Apply(MaskRegionOp.PaintHere, ex, inc, null);
            Check.Equal("000000", Bits(ex), "dont→paint: not excluded");
            Check.Equal("111000", Bits(inc), "dont→paint: included");
        }

        // 右クリックの宛先: クリックした所(ここでは 4 画素)をいま塗っているゾーン。
        private static float[] Strength(params float[] s) => s;

        public static void PaintingZone_LowerZoneWhenTopDoesNotPaint()
        {
            // 上のゾーンは塗っていない所を、下のゾーンが塗っている(上を宛先にしても何も変わらない場面)。
            var s = new[] { Strength(0f, 0f, 0f, 0f), Strength(1f, 1f, 1f, 1f) };
            Check.Equal(1, MaskRegionEdit.PaintingZoneAt(s, 4), "下のゾーン");
        }

        public static void PaintingZone_TopZoneWinsWhereBothPaint()
        {
            // 両方が選んでいる所は、合成で上のゾーンだけが効く。
            var s = new[] { Strength(1f, 1f, 1f, 1f), Strength(1f, 1f, 1f, 1f) };
            Check.Equal(0, MaskRegionEdit.PaintingZoneAt(s, 4), "上のゾーン");
        }

        public static void PaintingZone_WeakTopLeavesRoomForLower()
        {
            // 上が 0.3 しか取らなければ、残り 0.7 を下が塗る(見えている色は下のゾーンが大半)。
            var s = new[] { Strength(0.3f, 0.3f, 0.3f, 0.3f), Strength(1f, 1f, 1f, 1f) };
            Check.Equal(1, MaskRegionEdit.PaintingZoneAt(s, 4), "取り分の大きい下のゾーン");
        }

        public static void PaintingZone_NoneWhenMostlyUnpainted()
        {
            // 4 画素のうち 1 画素だけ(縁など)では、塗られている所とみなさない。
            var s = new[] { Strength(1f, 0f, 0f, 0f), Strength(0.2f, 0.2f, 0.2f, 0.2f) };
            Check.Equal(-1, MaskRegionEdit.PaintingZoneAt(s, 4), "誰も塗っていない");
        }

        public static void PaintingZone_SkipsZonesWithoutValues()
        {
            // 無効のゾーン・選択の値が取れないゾーン(null)は飛ばす。
            var s = new[] { null, Strength(1f, 1f, 0f, 1f) };
            Check.Equal(1, MaskRegionEdit.PaintingZoneAt(s, 4), "値のあるゾーン");
            Check.Equal(-1, MaskRegionEdit.PaintingZoneAt(new float[][] { null, null }, 4), "どれも値なし");
        }

        public static void MaskEdit_CanvasSizeFollowsSourceFile()
        {
            // 4K の元画像を 2048 で取り込んでも、マスクは元画像(書き出しの解像度)に合わせる。
            MaskRegionEdit.CanvasSize(4096, 4096, 2048, 2048, out int w, out int h);
            Check.Equal(4096, w, "file width"); Check.Equal(4096, h, "file height");
            // 元画像の寸法が分からない(生成テクスチャ等)なら取り込み後の寸法。
            MaskRegionEdit.CanvasSize(0, 0, 1024, 512, out w, out h);
            Check.Equal(1024, w, "import width"); Check.Equal(512, h, "import height");
            // 長辺 8192 を超える元画像は縦横比を保って縮める。
            MaskRegionEdit.CanvasSize(16384, 8192, 2048, 1024, out w, out h);
            Check.Equal(8192, w, "capped width"); Check.Equal(4096, h, "capped height");
            // 取り込み後の寸法より小さくはしない。
            MaskRegionEdit.CanvasSize(16384, 16384, 16384, 16384, out w, out h);
            Check.Equal(16384, w, "not below import");
        }

        public static void MaskEdit_StampDiskPaintsPixelsInsideCircle()
        {
            const int W = 11, H = 11;
            var target = PackBits(W * H);
            var opposite = PackBits(W * H, Enumerable.Range(0, W * H).ToArray());
            // 中心が画素 (5,5) の中心、半径 1.5: 中心からの距離が 1.5 以下の画素(3x3 の 9 画素)。
            Stamp(target, opposite, W, H, 5.5f, 5.5f, 1.5f, true, out int x0, out int y0, out int x1, out int y1);
            Check.Equal(9, CountBits(target, W * H), "painted pixels");
            Check.True(Bit(target, 5 * W + 5) && Bit(target, 4 * W + 4) && Bit(target, 6 * W + 6), "3x3 painted");
            Check.True(!Bit(opposite, 5 * W + 5) && Bit(opposite, 5 * W + 8), "opposite cleared under the brush only");
            Check.Equal(4, x0, "bbox x0"); Check.Equal(4, y0, "bbox y0");
            Check.Equal(7, x1, "bbox x1"); Check.Equal(7, y1, "bbox y1");
            // 消す: 塗った円の中だけ消え、反対側の層には触らない。
            Stamp(target, opposite, W, H, 5.5f, 5.5f, 0.5f, false, out _, out _, out _, out _);
            Check.True(!Bit(target, 5 * W + 5) && Bit(target, 4 * W + 4), "erase center only");
        }

        public static void MaskEdit_StampDiskTinyBrushPaintsCenterPixel()
        {
            const int W = 8, H = 8;
            var target = PackBits(W * H);
            // 半径 0(拡大して最小のブラシ): 中心が画素の中心から外れていても、その画素は塗る。
            Stamp(target, null, W, H, 3.9f, 2.1f, 0f, true, out int x0, out int y0, out int x1, out int y1);
            Check.Equal(1, CountBits(target, W * H), "one pixel");
            Check.True(Bit(target, 2 * W + 3), "the pixel under the center");
            Check.Equal(3, x0, "bbox x0"); Check.Equal(4, x1, "bbox x1");
            // 画像の外にかかる円は切る(落ちない)。
            Stamp(target, null, W, H, -1f, -1f, 2.2f, true, out x0, out y0, out x1, out y1);
            Check.True(Bit(target, 0) && x0 == 0 && y0 == 0, "clipped at the corner");
        }

        public static void MaskEdit_BrushPaintClearsOppositeLayerInSpanOnly()
        {
            // 含めるを [1, 4) に塗る: 同じゾーンの除外はその区間だけ消え、区間の外(0, 4, 5)は残る。
            var inc = Layer();
            var ex = Layer(0, 1, 2, 3, 4, 5);
            Span(inc, ex, 1, 4, paint: true);
            Check.Equal("011100", Bits(inc), "painted span");
            Check.Equal("100011", Bits(ex), "opposite cleared in span only");
        }

        public static void MaskEdit_BrushEraseKeepsOppositeLayer()
        {
            // 消しゴムは塗っている層だけを消す(反対側の層まで消すと、消しただけで別の指定が消える)。
            var inc = Layer(0, 1, 2);
            var ex = Layer(1, 2, 3);
            Span(inc, ex, 0, 6, paint: false);
            Check.Equal("000000", Bits(inc), "erased");
            Check.Equal("011100", Bits(ex), "opposite untouched");
            // 反対側の層が無い(null)ときも塗れる。
            Span(inc, null, 2, 3, paint: true);
            Check.Equal("001000", Bits(inc), "no opposite layer");
        }

        public static void MaskEdit_NoChangeReportsZero()
        {
            var ex = Layer(3, 4, 5);
            var r = Apply(MaskRegionOp.OnlyThisPart, ex, null, null);
            Check.Equal(0, r.changed, "already only this part");
        }

        // ─── UpdateCheck（新しい版のお知らせ: 公開リストから知らせる版を選ぶ） ───

        private const string UpdatePackageId = "com.yukkuri-aoba.iroca";

        // VPM のリポジトリ形式。ほかのパッケージ(NDMF の写し)も同じリストに載る。
        private static string Listing(params (string version, string unity)[] versions)
        {
            var entries = versions.Select(v =>
                $"\"{v.version}\": {{ \"name\": \"{UpdatePackageId}\", \"version\": \"{v.version}\", \"displayName\": \"\\u3044\\u308d\\u304b\""
                + (v.unity == null ? "" : $", \"unity\": \"{v.unity}\"") + " }");
            return "{ \"name\": \"repo\", \"id\": \"com.yukkuri-aoba.repo\", \"packages\": {"
                   + " \"nadena.dev.ndmf\": { \"versions\": { \"9.0.0\": { \"name\": \"nadena.dev.ndmf\", \"version\": \"9.0.0\" } } },"
                   + $" \"{UpdatePackageId}\": {{ \"versions\": {{ {string.Join(", ", entries)} }} }} }} }}";
        }

        private static string Latest(string json, bool includePrerelease = false, string unity = "2022.3.22f1")
        {
            Check.True(UpdateCheck.TryFindLatest(json, UpdatePackageId, includePrerelease, unity, out string latest),
                "listing readable");
            return latest;
        }

        private static int SemVerCompare(string a, string b)
        {
            Check.True(UpdateCheck.SemVer.TryParse(a, out var va), a);
            Check.True(UpdateCheck.SemVer.TryParse(b, out var vb), b);
            return Math.Sign(va.CompareTo(vb));
        }

        public static void Update_SemVerComparesLikeVpm()
        {
            Check.Equal(1, SemVerCompare("0.2.1", "0.2.0"), "patch");
            Check.Equal(1, SemVerCompare("0.10.0", "0.9.9"), "numeric, not text");
            Check.Equal(1, SemVerCompare("1.0.0", "1.0.0-beta.2"), "prerelease comes before release");
            Check.Equal(1, SemVerCompare("1.0.0-beta.10", "1.0.0-beta.2"), "numeric prerelease ids");
            Check.Equal(1, SemVerCompare("1.0.0-alpha.1", "1.0.0-alpha"), "more ids is later");
            Check.Equal(1, SemVerCompare("1.0.0-alpha", "1.0.0-1"), "numeric ids come before text");
            Check.Equal(0, SemVerCompare("1.2.3+build.5", "1.2.3"), "build metadata ignored");
        }

        public static void Update_SemVerRejectsMalformed()
        {
            foreach (string s in new[] { "", null, "0.2", "v0.2.0", "0.2.0.1", "1.2.3-", "1.2.3-beta..1", "1.x.3", "1.2.3 " })
                Check.True(!UpdateCheck.SemVer.TryParse(s, out _), $"<{s}> rejected");
        }

        public static void Update_PicksNewestInstallable()
        {
            string json = Listing(("0.2.0", "2022.3"), ("0.2.1", "2022.3"), ("0.3.0-beta.1", "2022.3"), ("0.4.0", "6000.0"));
            Check.Equal("0.2.1", Latest(json), "skips prerelease and versions needing Unity 6");
            Check.Equal("0.3.0-beta.1", Latest(json, includePrerelease: true), "prerelease channel");
            Check.Equal("0.4.0", Latest(json, unity: "6000.0.58f2"), "Unity 6");
            Check.Equal("0.2.1", Latest(Listing(("0.2.1", null), ("0.2.0", null))), "no unity field");
        }

        public static void Update_NoEntryIsNotAnError()
        {
            Check.Equal(null, Latest("{ \"packages\": {} }"), "package not listed");
            Check.Equal(null, Latest(Listing(("0.3.0-beta.1", "2022.3"))), "only prereleases");
        }

        public static void Update_RejectsUnexpectedShapes()
        {
            foreach (string json in new[]
                     {
                         "", null, "<html>404</html>", "{ \"name\": \"repo\" }", "{ \"packages\": {} } trailing",
                         "{ \"packages\": { \"" + UpdatePackageId + "\": { \"versions\": [] } } }",
                     })
                Check.True(!UpdateCheck.TryFindLatest(json, UpdatePackageId, false, "2022.3.22f1", out _), $"<{json}> rejected");
        }

        public static void Update_ReadsThePublishedListing()
        {
            // リポジトリの docs/index.json(Pages に載るリストの元)をそのまま読めるか。
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "index.json"))) dir = dir.Parent;
            Check.True(dir != null, "docs/index.json not found above the test binary");
            string latest = Latest(File.ReadAllText(Path.Combine(dir.FullName, "docs", "index.json")));
            Check.True(latest != null, "docs/index.json lists an installable version");
        }

        public static void Update_UnitySatisfiesComparesMajorMinor()
        {
            Check.True(UpdateCheck.UnitySatisfies("2022.3", "2022.3.22f1"), "same");
            Check.True(UpdateCheck.UnitySatisfies("2022.3", "6000.0.58f2"), "newer major");
            Check.True(!UpdateCheck.UnitySatisfies("2022.3", "2021.3.45f1"), "older major");
            Check.True(!UpdateCheck.UnitySatisfies("6000.0", "2022.3.22f1"), "needs Unity 6");
            Check.True(!UpdateCheck.UnitySatisfies("6000.1", "6000.0.58f2"), "older minor");
            Check.True(UpdateCheck.UnitySatisfies("someday", "2022.3.22f1"), "unreadable field is not a reason to skip");
        }

        public static void Update_IsDueOncePerDay()
        {
            var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
            string Ticks(DateTime t) => t.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Check.True(UpdateCheck.IsDue("", now), "never checked");
            Check.True(UpdateCheck.IsDue("garbage", now), "unreadable");
            Check.True(!UpdateCheck.IsDue(Ticks(now.AddHours(-23)), now), "23 hours ago");
            Check.True(UpdateCheck.IsDue(Ticks(now.AddHours(-24)), now), "a day ago");
            Check.True(UpdateCheck.IsDue(Ticks(now.AddHours(1)), now), "clock went back");
        }

        public static void Update_MiniJsonReadsValues()
        {
            var root = UpdateCheck.MiniJson.Parse("\uFEFF{ \"a\": [1, -2.5e1, true, false, null, \"x\\\"\\n\\u3044\"], \"b\": {} }")
                as System.Collections.Generic.Dictionary<string, object>;
            Check.True(root != null, "parsed");
            var a = (System.Collections.Generic.List<object>)root["a"];
            Check.Equal<object>(1.0, a[0], "int");
            Check.Equal<object>(-25.0, a[1], "exponent");
            Check.Equal<object>(true, a[2], "true");
            Check.Equal<object>(false, a[3], "false");
            Check.Equal<object>(null, a[4], "null");
            Check.Equal<object>("x\"\nい", a[5], "escapes");
            Check.Equal(0, ((System.Collections.Generic.Dictionary<string, object>)root["b"]).Count, "empty object");
        }

        public static void Update_MiniJsonRejectsMalformed()
        {
            foreach (string json in new[] { "{ \"a\": 1, }", "{ \"a\" 1 }", "[1 2]", "\"unterminated", "tru", "\"\\u12\"" })
                Check.True(UpdateCheck.MiniJson.Parse(json) == null, $"<{json}> rejected");
            // 深い入れ子でスタックを使い切らない。
            Check.True(UpdateCheck.MiniJson.Parse(new string('[', 10000) + new string(']', 10000)) == null, "deep nesting");
        }

        // ─── ColorTintLut（「アバターの色」表示: マテリアルのメインカラーの掛け算） ───

        public static void TintLut_WhiteIsIdentity()
        {
            Check.True(ColorTintLut.IsIdentity(ColorTintLut.Build(1f, true)), "linear");
            Check.True(ColorTintLut.IsIdentity(ColorTintLut.Build(1f, false)), "gamma");
        }

        public static void TintLut_BlackGivesZero()
        {
            var lut = ColorTintLut.Build(0f, true);
            for (int v = 0; v < 256; v++) Check.Equal((byte)0, lut[v], $"v={v}");
        }

        // Gamma のプロジェクトでは保存された値どうしをそのまま掛ける。
        public static void TintLut_GammaMultipliesStoredValues()
        {
            var lut = ColorTintLut.Build(0.5f, false);
            Check.Equal((byte)100, lut[200], "200 * 0.5");
            Check.Equal((byte)128, lut[255], "255 * 0.5");
        }

        // Linear のプロジェクトでは線形で掛けて sRGB へ戻す。白(255)に 0.5 を掛けると、保存値の 0.5 に戻る。
        public static void TintLut_LinearMultipliesInLinearSpace()
        {
            var lut = ColorTintLut.Build(0.5f, true);
            Check.Equal((byte)128, lut[255], "white * 0.5");
            // 中間の値は線形の積を sRGB に戻したもの(保存値どうしの積 64 とは違う)。
            float expect = ColorTintLut.LinearToSrgb(ColorTintLut.SrgbToLinear(128 / 255f) * ColorTintLut.SrgbToLinear(0.5f));
            Check.Equal((byte)Math.Round(expect * 255f), lut[128], "128 * 0.5");
            Check.True(lut[128] != 64, "not the gamma product");
        }

        // HDR の色(1 を超える)は明るくし、表示先で飽和するのと同じく 255 で止める。
        public static void TintLut_HdrBrightensAndClamps()
        {
            var lut = ColorTintLut.Build(2f, true);
            Check.Equal((byte)255, lut[255], "clamped");
            Check.True(lut[100] > 100, "brighter");
            for (int v = 1; v < 256; v++) Check.True(lut[v] >= lut[v - 1], $"monotonic at {v}");
        }

        public static void TintLut_SrgbRoundTrips()
        {
            for (int v = 0; v < 256; v++)
            {
                float back = ColorTintLut.LinearToSrgb(ColorTintLut.SrgbToLinear(v / 255f));
                Check.Equal(v, (int)Math.Round(back * 255f), $"v={v}");
            }
        }

        public static void TintLut_ApplyMapsChannelsAndKeepsAlpha()
        {
            var r = ColorTintLut.Build(1f, true);
            var g = ColorTintLut.Build(0.5f, false);
            var b = ColorTintLut.Build(0f, true);
            var src = new[] { new UnityEngine.Color32(10, 200, 30, 77), new UnityEngine.Color32(255, 255, 255, 0) };
            var dst = new UnityEngine.Color32[2];
            ColorTintLut.Apply(src, dst, r, g, b);
            Check.Equal((byte)10, dst[0].r, "r identity");
            Check.Equal((byte)100, dst[0].g, "g halved");
            Check.Equal((byte)0, dst[0].b, "b zero");
            Check.Equal((byte)77, dst[0].a, "alpha kept");
            Check.Equal((byte)0, dst[1].a, "alpha kept (0)");
            // 同じ配列へ書いてもよい。
            ColorTintLut.Apply(src, src, r, g, b);
            Check.Equal((byte)100, src[0].g, "in place");
        }
    }
}
