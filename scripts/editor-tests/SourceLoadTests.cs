// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 原本の PNG を別スレッドで読む経路(<see cref="SourcePixelCache.LoadInBackground"/>・<see cref="PreviewPipeline.PollSource"/>)。
    /// 待たずに始まり、結果は待って読むのと同じ画素で、途中で要らなくなったらメモリがその場で戻ること。
    /// </summary>
    public class SourceLoadTests {
        // デコードに数十 ms かかる大きさ(取り消しが読んでいる途中に届くように)。
        private const int BigSide = 2048;
        private const int SmallSide = 16;

        private TestAssets _assets;

        [SetUp]
        public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown() => _assets.Dispose();

        private static Color32[] Noise(int side, int seed) {
            var rnd = new System.Random(seed);
            var px = new Color32[side * side];
            var bytes = new byte[px.Length * 3];
            rnd.NextBytes(bytes);
            for (int i = 0; i < px.Length; i++) {
                px[i] = new Color32(bytes[i * 3], bytes[i * 3 + 1], bytes[i * 3 + 2], 255);
            }
            return px;
        }

        private Texture2D Png(string name, int side, int seed, out Color32[] expected) {
            expected = Noise(side, seed);
            string path = _assets.WritePng(name, expected, side, side);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // 読み終わるまで(メインスレッドへの知らせも含めて)回す。
        private static SourceState PollUntilSettled(SourcePixelCache sources, Texture2D tex, Action finished) {
            var watch = Stopwatch.StartNew();
            while (true) {
                var state = sources.LoadInBackground(tex, finished);
                if (state != SourceState.Loading) {
                    return state;
                }
                Assert.Less(watch.Elapsed.TotalSeconds, 60, "読み終わらない");
                PreviewJobMainThread.Drain();
                Thread.Sleep(1);
            }
        }

        private static long LivePixelBytes => IrocaNativeMemory.LiveBytesOf(NativeUsage.Pixels);

        [Test]
        public void Background_StartsWithoutWaiting_AndReadsTheSamePixels() {
            var tex = Png("big", BigSide, 1, out var expected);
            var sources = new SourcePixelCache();
            try {
                Assert.AreEqual(SourceState.Loading, sources.LoadInBackground(tex, () => { }), "呼んだその場で読み終えている");
                Assert.IsNull(sources.Pixels, "読み終える前に画素がある");
                Assert.AreEqual(SourceState.Ready, PollUntilSettled(sources, tex, () => { }));
                Assert.IsTrue(sources.FromFile);
                Assert.AreEqual(BigSide, sources.Width);
                CollectionAssert.AreEqual(expected, sources.Pixels.ToArray());
            } finally { sources.Clear(); }
        }

        [Test]
        public void Clear_WhileLoading_GivesTheMemoryBackAtOnce() {
            var tex = Png("big", BigSide, 2, out _);
            long before = LivePixelBytes;
            var sources = new SourcePixelCache();
            bool told = false;
            Assert.AreEqual(SourceState.Loading, sources.LoadInBackground(tex, () => told = true));
            sources.Clear();
            sources.WaitForCancelledLoads(5000);
            Assert.AreEqual(before, LivePixelBytes, "読みかけの画素が残っている");
            Assert.IsNull(sources.Pixels);
            PreviewJobMainThread.Drain();
            Assert.IsFalse(told, "取り消した読み込みの知らせが届いた");
        }

        [Test]
        public void ClearAfterTheReadEnded_BeforeAdopting_GivesTheMemoryBack() {
            var tex = Png("small", SmallSide, 3, out _);
            long before = LivePixelBytes;
            var sources = new SourcePixelCache();
            sources.LoadInBackground(tex, () => { });
            // 読み終わっているが受け取っていない。
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 200) {
                Thread.Sleep(5);
            }
            sources.Clear();
            sources.WaitForCancelledLoads(5000);
            Assert.AreEqual(before, LivePixelBytes);
        }

        [Test]
        public void SwitchingWhileLoading_EndsWithTheLastTexture_WithoutLeftovers() {
            var a = Png("a", BigSide, 4, out _);
            var b = Png("b", BigSide, 5, out _);
            var c = Png("c", SmallSide, 6, out var expected);
            long before = LivePixelBytes;
            var sources = new SourcePixelCache();
            try {
                sources.LoadInBackground(a, () => { });
                sources.LoadInBackground(b, () => { });
                Assert.AreEqual(SourceState.Ready, PollUntilSettled(sources, c, () => { }));
                Assert.AreSame(c, sources.Texture);
                CollectionAssert.AreEqual(expected, sources.Pixels.ToArray());
                sources.WaitForCancelledLoads(5000);
                Assert.AreEqual(before + SmallSide * SmallSide * 4, LivePixelBytes, "前のテクスチャの画素が残っている");
            } finally { sources.Clear(); }
        }

        // 待つ口(自動調整など)は、読んでいる途中のものを受け取る(読み直さない)。
        [Test]
        public void BlockingLoad_WhileLoading_TakesTheSameRead() {
            var tex = Png("big", BigSide, 7, out var expected);
            var sources = new SourcePixelCache();
            try {
                sources.LoadInBackground(tex, () => { });
                Assert.IsTrue(sources.Load(tex));
                CollectionAssert.AreEqual(expected, sources.Pixels.ToArray());
            } finally { sources.Clear(); }
        }

        // 別スレッドで読めないもの(JPG)は、次の呼び出しでメインスレッドで読む。
        [Test]
        public void NotPng_FallsBackOnTheMainThread() {
            var png = Noise(SmallSide, 8);
            var encoder = new Texture2D(SmallSide, SmallSide, TextureFormat.RGBA32, false);
            string path = _assets.Folder + "/photo.jpg";
            try {
                encoder.SetPixels32(png);
                File.WriteAllBytes(path, encoder.EncodeToJPG());
            } finally { UnityEngine.Object.DestroyImmediate(encoder); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            var sources = new SourcePixelCache();
            try {
                Assert.AreEqual(SourceState.Loading, sources.LoadInBackground(tex, () => { }));
                Assert.AreEqual(SourceState.Ready, PollUntilSettled(sources, tex, () => { }));
                Assert.IsTrue(sources.FromFile, "JPG はファイルから読む");
                Assert.AreEqual(SmallSide, sources.Width);
            } finally { sources.Clear(); }
        }

        // 書き出し・自動化が読み直さずに使えるのは、同じテクスチャの元ファイルを読んだ画素で、ファイルが読んだ後に
        // 変わっていないときだけ。変わったら(更新時刻が違う)使わせない。
        [Test]
        public void ShareIfCurrent_OnlyWhileTheFileIsUnchanged() {
            var tex = Png("share", SmallSide, 11, out var expected);
            var other = Png("other", SmallSide, 12, out _);
            string path = AssetDatabase.GetAssetPath(tex);
            var sources = new SourcePixelCache();
            try {
                Assert.IsNull(sources.ShareIfCurrent(tex, path), "読む前に画素を渡した");
                Assert.AreEqual(SourceState.Ready, PollUntilSettled(sources, tex, () => { }));
                var shared = sources.ShareIfCurrent(tex, path);
                Assert.IsNotNull(shared, "読んだばかりの画素を渡さない");
                try {
                    Assert.AreSame(sources.Pixels, shared);
                    CollectionAssert.AreEqual(expected, shared.ToArray());
                } finally { shared.Release(); }
                Assert.IsNull(sources.ShareIfCurrent(other, AssetDatabase.GetAssetPath(other)), "別のテクスチャに渡した");

                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
                Assert.IsNull(sources.ShareIfCurrent(tex, path), "読んだ後に変わったファイルの画素を渡した");
            } finally { sources.Clear(); }
        }

        // ファイルを開けないときは、読み直しを繰り返さず 1 回で取り込み済みテクスチャへ落ちる(Read/Write が無ければ読めないと覚える)。
        [Test]
        public void LockedFile_DoesNotRetryForever() {
            var tex = Png("locked", SmallSide, 9, out _);
            string path = AssetDatabase.GetAssetPath(tex);
            var sources = new SourcePixelCache();
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                    Assert.AreEqual(SourceState.Unavailable, PollUntilSettled(sources, tex, () => { }));
                    Assert.IsTrue(sources.IsKnownUnreadable(tex));
                }
            } finally {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
                sources.Clear();
            }
        }

        [Test]
        public void Decoder_Cancelled_ReleasesAndThrows() {
            var tex = Png("small", SmallSide, 10, out _);
            string path = AssetDatabase.GetAssetPath(tex);
            long before = LivePixelBytes;
            using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel();
                Assert.Throws<OperationCanceledException>(() => PngFileDecoder.TryDecode(path, cancel.Token));
            }
            Assert.AreEqual(before, LivePixelBytes);
        }

        [Test]
        public void CanDecode_AnswersFromTheHeader() {
            var tex = Png("small", SmallSide, 11, out _);
            Assert.IsTrue(PngFileDecoder.CanDecode(AssetDatabase.GetAssetPath(tex)));
            string notPng = _assets.Folder + "/not.bin";
            File.WriteAllBytes(notPng, new byte[64]);
            Assert.IsFalse(PngFileDecoder.CanDecode(notPng));
            string empty = _assets.Folder + "/empty.bin";
            File.WriteAllBytes(empty, new byte[0]);
            Assert.IsFalse(PngFileDecoder.CanDecode(empty));
        }

        // 別スレッドの寸法の決め方が、テクスチャを渡す口と同じ(0 は元の解像度)。
        [Test]
        public void FitToWorkingSize_ByValue_MatchesTheRule() {
            var src = NativePixels.Allocate(9000, 2);
            var same = WorkingResolution.FitToWorkingSize(src, 0, 0);
            Assert.AreSame(src, same, "0 は縮めない");
            var shrunk = WorkingResolution.FitToWorkingSize(same, 4500, 1);
            try {
                Assert.AreEqual(4500, shrunk.Width);
                Assert.AreEqual(1, shrunk.Height);
            } finally { shrunk.Release(); }
        }

        // ───────────── ウィンドウ ─────────────

        private string _prefKey;
        private string _prefBefore;

        private IrocaWindow OpenWindow(Texture2D tex) {
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(tex)));
            var win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(tex, win.SourceTexture);
            return win;
        }

        private void CloseWindow(IrocaWindow win, params Texture2D[] opened) {
            Undo.ClearUndo(win);
            UnityEngine.Object.DestroyImmediate(win);
            if (_prefBefore == null) {
                EditorPrefs.DeleteKey(_prefKey);
            } else {
                EditorPrefs.SetString(_prefKey, _prefBefore);
            }
            foreach (var tex in opened) {
                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(tex));
                foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                    if (!Directory.Exists(dir)) {
                        continue;
                    }
                    foreach (var f in Directory.GetFiles(dir, guid + "*")) {
                        File.Delete(f);
                    }
                }
            }
        }

        // 描かれていなくても、読み終えたら受け取る(受け取り手のいない画素を残さない)。
        [Test]
        public void Window_AdoptsTheReadOnTheTick_WithoutDrawing() {
            var tex = Png("big", BigSide, 12, out var expected);
            var win = OpenWindow(tex);
            try {
                var pipe = win.Preview.Pipeline;
                Assert.AreEqual(SourceState.Loading, pipe.PollSource(tex));
                var watch = Stopwatch.StartNew();
                while (pipe.TrueSource == null) {
                    Assert.Less(watch.Elapsed.TotalSeconds, 60, "受け取らない");
                    PreviewJobMainThread.Drain();
                    Thread.Sleep(1);
                }
                Assert.AreEqual(SourceState.Ready, pipe.PollSource(tex));
                CollectionAssert.AreEqual(expected, pipe.TrueSource.ToArray());
            } finally { CloseWindow(win, tex); }
        }

        // 読んでいる途中でウィンドウを閉じても、画素は残らない。
        [Test]
        public void Window_ClosedWhileLoading_LeavesNothing() {
            var tex = Png("big", BigSide, 13, out _);
            long before = LivePixelBytes;
            var win = OpenWindow(tex);
            bool closed = false;
            try {
                Assert.AreEqual(SourceState.Loading, win.Preview.Pipeline.PollSource(tex));
                CloseWindow(win, tex);
                closed = true;
                Assert.AreEqual(before, LivePixelBytes, "閉じた後に読みかけの画素が残っている");
                PreviewJobMainThread.Drain();
                Assert.AreEqual(before, LivePixelBytes);
            } finally {
                if (!closed) {
                    CloseWindow(win, tex);
                }
            }
        }

        // テクスチャを切り替えたら、読んでいた分は捨てて新しいほうを読む。
        [Test]
        public void Window_SwitchedWhileLoading_ShowsTheNewTexture() {
            var a = Png("a", BigSide, 14, out _);
            var b = Png("b", SmallSide, 15, out var expected);
            var win = OpenWindow(a);
            try {
                var pipe = win.Preview.Pipeline;
                pipe.PollSource(a);
                win.SwitchSourceTexture(b);
                var watch = Stopwatch.StartNew();
                while (pipe.PollSource(b) != SourceState.Ready) {
                    Assert.Less(watch.Elapsed.TotalSeconds, 60, "読み終わらない");
                    PreviewJobMainThread.Drain();
                    Thread.Sleep(1);
                }
                Assert.AreEqual(SmallSide, pipe.TrueSourceW);
                CollectionAssert.AreEqual(expected, pipe.TrueSource.ToArray());
            } finally { CloseWindow(win, a, b); }
        }
    }
}
