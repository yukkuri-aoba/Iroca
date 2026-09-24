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
            // 置換先が他プロセスに開かれている（Unity が読み込み中など）と置換は失敗する。
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
    }
}
