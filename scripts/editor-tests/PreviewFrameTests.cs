// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// プレビューの計算(<see cref="PreviewPipeline"/>)が表示へ渡す画素(<see cref="PreviewFrame"/>)。0.4.0 の計画の 4-4 で、
    /// 表示寸法の変更後・変更前と概要段の画素を Mono のヒープの外に置き、テクスチャへは管理配列を通さずに上げるようにした。
    /// <list type="bullet">
    /// <item>確定段の表示用の 2 枚は、確定段の出力と原本をそれぞれ表示寸法へ面平均で縮めたものと同じ。</item>
    /// <item>縮小しないテクスチャでは、確定段の出力と原本そのもの(写さない)。</item>
    /// <item>テクスチャへ上げたもの(<see cref="PreviewRenderer.UploadPixels"/>)は、SetPixels32 で上げたものと同じ。</item>
    /// <item>受け取られなかった結果・閉じた後に、画素(<see cref="NativeUsage.Pixels"/>)が残らない。</item>
    /// </list>
    /// </summary>
    public class PreviewFrameTests
    {
        private TestAssets _assets;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaWindow _win;
        private string _guid;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
        }

        [TearDown]
        public void TearDown()
        {
            CloseWindow();
            if (_prefBefore == null) EditorPrefs.DeleteKey(_prefKey);
            else EditorPrefs.SetString(_prefKey, _prefBefore);
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (_guid == null || !Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) File.Delete(f);
            }
            _assets.Dispose();
        }

        [Test]
        public void FinalFrame_IsTheOutputAndSourceShrunkToTheDisplaySize()
        {
            // 表示の上限(IrocaConsts.Preview.MaxSize)より大きいので、表示用は縮める(概要段も走る)。
            const int w = 640, h = 320;
            var preview = Open("wide", w, h);
            Run(preview, dragOnly: false);

            var pipeline = preview.Pipeline;
            Assert.IsTrue(pipeline.TakePendingFrame(out var frame), "表示へ渡す結果が無い");
            try
            {
                Assert.IsTrue(frame.IsFinal, "最後に届いたのは確定段のはず");
                PixelProcessor.ComputeFitSize(w, h, IrocaConsts.Preview.MaxSize, out int pw, out int ph, out float scale);
                Assert.AreEqual((pw, ph), (frame.Width, frame.Height));
                Assert.AreEqual((pw, ph), (frame.Processed.Width, frame.Processed.Height));
                Assert.AreEqual((pw, ph), (frame.Raw.Width, frame.Raw.Height));
                AssertSamePixels(PixelProcessor.BoxDownsample(preview.FullOutput.ToArray(), w, h, pw, ph, scale),
                    frame.Processed.ToArray(), pw, "変更後");
                Assert.IsTrue(preview.TryGetTrueSourcePixels(preview.Pipeline.Sources.Texture, out var src, out _, out _));
                AssertSamePixels(PixelProcessor.BoxDownsample(src.ToArray(), w, h, pw, ph, scale),
                    frame.Raw.ToArray(), pw, "変更前");
            }
            finally { frame.Release(); }
        }

        [Test]
        public void FinalFrame_OfASmallTexture_IsTheOutputAndSourceThemselves()
        {
            const int w = 64, h = 48;
            var preview = Open("small", w, h);
            Run(preview, dragOnly: false);

            Assert.IsTrue(preview.Pipeline.TakePendingFrame(out var frame), "表示へ渡す結果が無い");
            try
            {
                Assert.AreSame(preview.FullOutput, frame.Processed, "縮小しないなら確定段の出力そのものを上げる");
                Assert.AreSame(preview.Pipeline.TrueSource, frame.Raw, "縮小しないなら原本そのものを上げる");
            }
            finally { frame.Release(); }
        }

        [Test]
        public void UploadPixels_MatchesSetPixels32()
        {
            const int w = 37, h = 23;   // 4 で割り切れない寸法でも行がずれないこと
            var px = Pattern(w, h);
            var native = NativePixels.CopyOf(px, w, h);
            Texture2D byUpload = null, bySet = null;
            try
            {
                PreviewRenderer.UploadPixels(ref byUpload, native);
                TextureSlot.Resize(ref bySet, w, h);
                bySet.SetPixels32(px);
                bySet.Apply();
                AssertSamePixels(bySet.GetPixels32(), byUpload.GetPixels32(), w, "LoadRawTextureData と SetPixels32");
            }
            finally
            {
                native.Release();
                TextureSlot.Release(ref byUpload);
                TextureSlot.Release(ref bySet);
            }
        }

        [Test]
        public void Frames_AreReturned_WhenNotTakenAndAfterClosing()
        {
            long before = IrocaNativeMemory.LiveBytesOf(NativeUsage.Pixels);
            var preview = Open("wide", 640, 320);
            // ドラッグの追従(概要段だけ)→ 確定、を 2 回。どれも表示へは渡さない(前の結果は次の結果が置き換える)。
            for (int i = 0; i < 2; i++)
            {
                Run(preview, dragOnly: true);
                Run(preview, dragOnly: false);
            }
            CloseWindow();
            PreviewJobMainThread.Drain();
            Assert.AreEqual(before, IrocaNativeMemory.LiveBytesOf(NativeUsage.Pixels),
                "閉じた後に画素が残った(受け取られなかった表示用の画素・縮小した控え)");
        }

        // ───────────── 手伝い ─────────────

        private PreviewView Open(string name, int w, int h)
        {
            string path = _assets.WritePng(name, Pattern(w, h), w, h);
            _guid = AssetDatabase.AssetPathToGUID(path);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            EditorPrefs.SetString(_prefKey, _guid);
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(tex, _win.SourceTexture, "窓がテスト用のテクスチャを開いていない");
            var zone = new ColorZone
            {
                name = "red",
                sampleColor = new Color(0.78f, 0.16f, 0.16f),
                sampleColorSet = true,
                targetColor = new Color(0.10f, 0.60f, 0.60f),
                targetColorSet = true,
                tolerance = 0.25f,
            };
            zone.EnsureId();
            _win.Session.zones.Clear();
            _win.Session.zones.Add(zone);
            return _win.Preview;
        }

        private void CloseWindow()
        {
            if (_win == null) return;
            Undo.ClearUndo(_win);
            Object.DestroyImmediate(_win);
            _win = null;
        }

        // 作り直して、概要段・確定段が終わるまでメインスレッドへの戻りを捌く(結果は表示へ渡さずに置いておく)。
        private static void Run(PreviewView preview, bool dragOnly)
        {
            preview.GeneratePreviewAsync(dragOnly);
            var watch = Stopwatch.StartNew();
            int idle = 0;
            while (idle < 3)
            {
                Assert.Less(watch.Elapsed.TotalSeconds, 60, "プレビューが終わらない");
                PreviewJobMainThread.Drain();
                idle = preview.IsGenerating ? 0 : idle + 1;
                Thread.Sleep(2);
            }
        }

        // 赤の帯(縦に陰影)と青の帯、境目の混色。行 0 = 画像の下端。
        private static Color32[] Pattern(int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float s = 0.6f + 0.4f * y / Mathf.Max(1, h - 1);
                for (int x = 0; x < w; x++)
                {
                    px[y * w + x] = x < w / 2
                        ? new Color32((byte)(200 * s), (byte)(40 * s), (byte)(40 * s), 255)
                        : new Color32((byte)(40 * s), (byte)(90 * s), (byte)(200 * s), 255);
                    if (x == w / 2) px[y * w + x] = new Color32((byte)(120 * s), (byte)(65 * s), (byte)(120 * s), 255);
                }
            }
            return px;
        }

        private static void AssertSamePixels(Color32[] expected, Color32[] actual, int w, string what)
        {
            Assert.AreEqual(expected.Length, actual.Length, what + ": 画素数");
            for (int i = 0; i < expected.Length; i++)
            {
                var e = expected[i];
                var a = actual[i];
                if (e.r != a.r || e.g != a.g || e.b != a.b || e.a != a.a)
                    Assert.Fail($"{what}: ({i % w}, {i / w}) が {e} ではなく {a}");
            }
        }
    }
}
