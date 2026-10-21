// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// AI モデルのファイルの扱い。モデルと変換済みのキャッシュは全プロジェクト共有のフォルダ
    /// (<see cref="MaskSuggestBridge.ModelsDirectory"/>)にあるので、版の違うプロジェクトや同時に開いた別の Unity と
    /// ぶつからないこと。本物のモデルと名前がぶつからないよう、置くファイルはテスト用の名前にして必ず片付ける。
    /// AI 統合アセンブリ(Sentis / Inference Engine)が無ければ、その部分は Ignore。
    /// </summary>
    public class MaskSuggestModelFilesTests
    {
        static string CacheDir => Path.Combine(MaskSuggestBridge.ModelsDirectory, "cache");

        // AI 統合アセンブリの内部なので名前で呼ぶ(名前を変えたらこのテストも直す)。
        static Type Repository()
        {
            var repo = Type.GetType("Iroca.SentisIntegration.SentisModelRepository, Iroca.SentisIntegration");
            if (repo == null) Assert.Ignore("AI 統合アセンブリが無い（Sentis / Inference Engine 未導入）");
            return repo;
        }

        static MethodInfo Method(Type t, string name)
        {
            var m = t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(m, $"{t.Name}.{name} が見つからない（名前を変えたらこのテストも直す）");
            return m;
        }

        static string TestStem() => "iroca_test_" + Guid.NewGuid().ToString("N");

        [Test]
        public void OtherVersionCaches_OnlyLongUnusedOnesAreRemoved()
        {
            // 版の違うプロジェクト(Unity 2022 の Sentis と Unity 6 の Inference Engine など)は、それぞれの版の
            // キャッシュを使う。行き来するたびに消し合わないよう、ほかの版は長く使われていないものだけ消す。
            var sweep = Method(Repository(), "DeleteStaleCaches");
            Directory.CreateDirectory(CacheDir);
            string stem = TestStem();
            string F(string version) => Path.Combine(CacheDir, stem + "." + version + ".sentis");
            string mine = F("2.1.3"), recent = F("2.4.1"), unused = F("2.0.0");
            var all = new[] { mine, recent, unused };
            try
            {
                foreach (var f in all) File.WriteAllText(f, "x");
                File.SetLastWriteTimeUtc(mine, DateTime.UtcNow.AddDays(-60));   // 自分の版は古くても消さない
                File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-3));
                File.SetLastWriteTimeUtc(unused, DateTime.UtcNow.AddDays(-40));

                sweep.Invoke(null, new object[] { Path.Combine(MaskSuggestBridge.ModelsDirectory, stem + ".onnx"), mine });

                Assert.IsTrue(File.Exists(mine), "今の版のキャッシュを消した");
                Assert.IsTrue(File.Exists(recent), "最近ほかのプロジェクトが使った版のキャッシュを消した");
                Assert.IsFalse(File.Exists(unused), "長く使われていない版のキャッシュを消していない");
            }
            finally
            {
                foreach (var f in all) if (File.Exists(f)) File.Delete(f);
            }
        }

        [Test]
        public void LeftoverCacheTempFiles_OnlyOldOnesAreRemoved()
        {
            // キャッシュは一時ファイルに書いてから置き換える。保存中に落ちて残った一時ファイルだけを消し、
            // 別の Unity が今書いている一時ファイルは消さない。
            var sweep = Method(Repository(), "DeleteLeftoverTempCaches");
            Directory.CreateDirectory(CacheDir);
            string stem = TestStem();
            string writing = Path.Combine(CacheDir, stem + ".2.1.3.sentis.tmp.12345");
            string leftover = Path.Combine(CacheDir, stem + ".2.1.3.sentis.tmp.23456");
            var all = new[] { writing, leftover };
            try
            {
                foreach (var f in all) File.WriteAllText(f, "x");
                File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddDays(-2));

                sweep.Invoke(null, new object[] { Path.Combine(MaskSuggestBridge.ModelsDirectory, stem + ".onnx") });

                Assert.IsTrue(File.Exists(writing), "書いている最中かもしれない一時ファイルを消した");
                Assert.IsFalse(File.Exists(leftover), "残った一時ファイルを消していない");
            }
            finally
            {
                foreach (var f in all) if (File.Exists(f)) File.Delete(f);
            }
        }

        // いま動いていないプロセス ID(Windows の ID は 4 の倍数)。
        static int UnusedProcessId()
        {
            for (int pid = 4_000_000; pid > 0; pid -= 4)
            {
                try { using (System.Diagnostics.Process.GetProcessById(pid)) { } }
                catch (ArgumentException) { return pid; }
            }
            Assert.Inconclusive("使われていないプロセス ID が見つからない");
            return 0;
        }

        // 自分以外の、いま動いているプロセスの ID。
        static int OtherLiveProcessId()
        {
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    try { if (p.Id != self && !p.HasExited) return p.Id; }
                    catch { /* 権限で見られないプロセスは飛ばす */ }
                }
            }
            Assert.Inconclusive("ほかに動いているプロセスが見つからない");
            return 0;
        }

        [Test]
        public void LeftoverDownloads_OnlyThoseOfGoneUnitiesAreRemoved()
        {
            // ダウンロードの途中で Unity が落ちると、途中までのファイル(<モデル>.download.<プロセス ID>)が残る。
            // 次に始めるときに、持ち主の Unity がもう動いていないもの(と 1 日以上前のもの)だけを消す。
            string dir = MaskSuggestBridge.ModelsDirectory;
            Directory.CreateDirectory(dir);
            string F(int pid) => Path.Combine(dir, MaskSuggestBridge.EncoderFileName + ".download." + pid);
            int live = OtherLiveProcessId();
            string ofGone = F(UnusedProcessId());
            string ofSelf = F(System.Diagnostics.Process.GetCurrentProcess().Id);
            string ofLive = F(live);
            var all = new[] { ofGone, ofSelf, ofLive };
            try
            {
                foreach (var f in all) File.WriteAllText(f, "x");

                MaskSuggestModelDownload.DeleteLeftoverDownloads();

                Assert.IsFalse(File.Exists(ofGone), "落ちた Unity の途中のファイルを消していない");
                Assert.IsTrue(File.Exists(ofSelf), "自分のダウンロード中のファイルを消した");
                Assert.IsTrue(File.Exists(ofLive), "別の Unity がダウンロード中のファイルを消した");

                // プロセス ID は使い回されるので、1 日以上前のものは持ち主が動いていても消す。
                File.SetLastWriteTimeUtc(ofLive, DateTime.UtcNow.AddDays(-2));
                MaskSuggestModelDownload.DeleteLeftoverDownloads();
                Assert.IsFalse(File.Exists(ofLive), "1 日以上前の途中のファイルを消していない");
            }
            finally
            {
                foreach (var f in all) if (File.Exists(f)) File.Delete(f);
            }
        }

        [Test]
        public void CacheOpenedByAnotherReader_IsLoadedWithoutConverting()
        {
            // 別の Unity が同じキャッシュを読んでいる最中でも、こちらも読める(以前は開けずに
            // 「壊れたキャッシュ」として消し、ONNX から変換し直していた)。小さいデコーダーで見る。
            var repo = Repository();
            if (!MaskSuggestBridge.ModelFilesPresent) Assert.Ignore("AI モデルが未配置");
            string onnx = (string)repo.GetProperty("DecoderOnnxPath", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            string cache = (string)Method(repo, "CachePathFor").Invoke(null, new object[] { onnx });
            if (!File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < File.GetLastWriteTimeUtc(onnx))
                Assert.Ignore("この版の変換済みキャッシュがまだ無い（AI を一度使ってから回す）");

            var warnings = new List<string>();
            Application.LogCallback onLog = (condition, _, type) =>
            {
                if (type == LogType.Warning && condition.StartsWith("[Iroca]", StringComparison.Ordinal))
                    warnings.Add(condition);
            };
            Application.logMessageReceived += onLog;
            try
            {
                using (new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var args = new object[] { onnx, null };
                    var model = Method(repo, "LoadOrConvert").Invoke(null, args);
                    Assert.IsNotNull(model, $"読み込めなかった: {args[1]}");
                }
            }
            finally
            {
                Application.logMessageReceived -= onLog;
            }
            CollectionAssert.IsEmpty(warnings, "キャッシュを使えずに変換し直した");
            Assert.IsTrue(File.Exists(cache), "キャッシュを消した");
        }
    }
}
