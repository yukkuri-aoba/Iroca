// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 製品の実行環境（Unity 2022.3 の Mono）とテストの実行環境（headless ハーネスの net8 JIT）で
    /// 再着色の出力が一致するかを測る。回帰テスト・golden・視覚レビューはすべてハーネスの出力を
    /// 見ているので、ここがずれていると「テストが測っているものと出荷物が違う」ことになる。
    ///
    /// 入力と期待出力は scripts/golden の cases/ と expected/（ハーネスが書いたもの）を使う。
    /// 製品側の経路は <see cref="IrocaAutomation.RecolorWithZones"/>（ディスクの PNG を読み、
    /// ProcessPixelsArray を通して PNG を書く。ExportView と同じ手順）。
    ///
    /// 判定は golden の CI と同じ許容（最大差 2・差のある画素 1% まで）。完全一致かどうかは
    /// 結果レポート（IROCA_PARITY_REPORT が指すファイル）に件ごとに残す。
    /// </summary>
    public class RuntimeParityTests
    {
        private const int MaxAbsDiff = 2;
        private const double MaxDiffFrac = 0.01;

        private static string Repo => Environment.GetEnvironmentVariable("IROCA_REPO");
        private static string GoldenDir => Path.Combine(Repo ?? "", "scripts", "golden");

        public static IEnumerable<string> Labels()
        {
            string dir = Path.Combine(GoldenDir, "cases");
            if (string.IsNullOrEmpty(Repo) || !Directory.Exists(dir))
                return new[] { "(IROCA_REPO 未設定)" };
            return Directory.GetFiles(dir, "*.zones.json")
                .Select(p => Path.GetFileName(p).Replace(".zones.json", ""))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
        }

        private TestAssets _assets;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();
        [TearDown] public void TearDown() => _assets.Dispose();

        [TestCaseSource(nameof(Labels))]
        public void GoldenCase_MatchesHarness(string label)
        {
            if (string.IsNullOrEmpty(Repo))
                Assert.Ignore("IROCA_REPO が未設定です。scripts/Run-EditorTests.ps1 から実行してください");

            string input = Path.Combine(GoldenDir, "cases", label + ".png");
            string zones = File.ReadAllText(Path.Combine(GoldenDir, "cases", label + ".zones.json"));
            string expectedPng = Path.Combine(GoldenDir, "expected", label + ".png");

            string src = _assets.WriteBytes(label + ".png", File.ReadAllBytes(input));
            string outPath = _assets.Folder + "/" + label + "_out.png";
            string json = IrocaAutomation.RecolorWithZones(src, zones, outPath);
            var result = JsonUtility.FromJson<IrocaAutomation.RecolorResult>(json);
            Assert.IsTrue(result.ok, $"{label}: 製品経路が失敗しました: {result.error}");

            var actual = Decode(File.ReadAllBytes(TestAssets.Abs(outPath)), out int w, out int h);
            var expected = Decode(File.ReadAllBytes(expectedPng), out int ew, out int eh);
            Assert.AreEqual((ew, eh), (w, h), $"{label}: 寸法");

            int maxDiff = 0, diffPx = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                int d = Math.Max(Math.Max(Math.Abs(actual[i].r - expected[i].r), Math.Abs(actual[i].g - expected[i].g)),
                                 Math.Max(Math.Abs(actual[i].b - expected[i].b), Math.Abs(actual[i].a - expected[i].a)));
                if (d > 0) diffPx++;
                if (d > maxDiff) maxDiff = d;
            }
            double frac = diffPx / (double)actual.Length;
            Report(label, maxDiff, diffPx, actual.Length);
            Assert.IsTrue(maxDiff <= MaxAbsDiff && frac <= MaxDiffFrac,
                $"{label}: Unity(Mono) とハーネス(net8) の出力が食い違います: 最大差 {maxDiff}、差のある画素 {diffPx}/{actual.Length} ({frac:P2})");
        }

        private static Color32[] Decode(byte[] png, out int w, out int h)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(png), "PNG を読めません");
                w = tex.width; h = tex.height;
                return tex.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static void Report(string label, int maxDiff, int diffPx, int total)
        {
            string line = $"{label}\t{(diffPx == 0 ? "exact" : "differs")}\tmax_diff={maxDiff}\tdiff_px={diffPx}/{total}";
            TestContext.WriteLine(line);
            string report = Environment.GetEnvironmentVariable("IROCA_PARITY_REPORT");
            if (!string.IsNullOrEmpty(report)) File.AppendAllText(report, line + "\n");
        }
    }
}
