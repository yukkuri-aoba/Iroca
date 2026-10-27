// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// Unity との境目でヒープの外のまま画素を読み書きする口(<see cref="TextureIO"/>。0.4.0 の計画の 2-4)が、今の管理配列の
    /// 経路(<see cref="ExportPipeline"/>)と同じ画素・同じバイト列になること。PNG 化(<c>EncodeNativeArrayToPNG</c>)を
    /// 別スレッドから呼べるかもここで確かめる(計画の「確かめること」)。
    /// </summary>
    public class TextureIOTests
    {
        private TestAssets _assets;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();
        [TearDown] public void TearDown() => _assets.Dispose();

        [TestCase(false, TestName = "ReadSourceFile_EqualsTheManagedPath(OriginalSize)")]
        [TestCase(true, TestName = "ReadSourceFile_EqualsTheManagedPath(WorkingSize)")]
        public void ReadSourceFile_EqualsTheManagedPath(bool workingSize)
        {
            const int W = 37, H = 29;
            string path = _assets.WritePng("src", Pattern(W, H), W, H);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 32;   // 取り込みでは縮む(作業解像度は元ファイルのまま。辺が 8192 以下)
            importer.SaveAndReimport();
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            Assert.AreEqual(ExportPipeline.SourceKind.File,
                ExportPipeline.ReadSourcePixels(path, imported, out var expected, out int ew, out int eh, workingSize));
            var native = TextureIO.ReadSourceFile(path, imported, workingSize);
            Assert.IsNotNull(native, "元ファイルを読めない");
            try
            {
                Assert.AreEqual((ew, eh), (native.Width, native.Height));
                AssertSame(expected, native.ToArray());
            }
            finally { native.Release(); }
        }

        [Test]
        public void EncodePng_EqualsTheManagedEncode()
        {
            const int W = 64, H = 48;
            var px = Pattern(W, H);
            var native = NativePixels.CopyOf(px, W, H);
            try
            {
                var png = TextureIO.EncodePng(native);
                try { CollectionAssert.AreEqual(ExportPipeline.EncodePng(px, W, H), png.ToArray()); }
                finally { png.Dispose(); }
            }
            finally { native.Release(); }
        }

        [Test]
        public void EncodePng_RunsOnABackgroundThreadAndMatchesTheMainThread()
        {
            const int W = 64, H = 48;
            var native = NativePixels.CopyOf(Pattern(W, H), W, H);
            try
            {
                byte[] onMain;
                var png = TextureIO.EncodePng(native);
                try { onMain = png.ToArray(); }
                finally { png.Dispose(); }

                byte[] onWorker = null;
                Exception error = null;
                var worker = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var p = TextureIO.EncodePng(native);
                        try { onWorker = p.ToArray(); }
                        finally { p.Dispose(); }
                    }
                    catch (Exception e) { error = e; }
                });
                worker.Start();
                Assert.IsTrue(worker.Join(30000), "バックグラウンドの PNG 化が終わらない");
                Assert.IsNull(error, error?.ToString());
                CollectionAssert.AreEqual(onMain, onWorker);
            }
            finally { native.Release(); }
        }

        [Test]
        public void WriteAllBytes_StreamsThroughASmallBufferAndReplacesAtomically()
        {
            // 置き場(1MB)より大きく、端数のある長さ。
            const int N = (5 << 20) / 2 + 123;
            var rng = new System.Random(20261028);
            var data = new byte[N];
            rng.NextBytes(data);
            string dir = Path.Combine(Path.GetTempPath(), "iroca-textureio-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "sub", "out.bin");
            var bytes = new NativeArray<byte>(data, Allocator.Persistent);
            try
            {
                TextureIO.WriteAllBytes(path, bytes);   // フォルダが無ければ作る
                CollectionAssert.AreEqual(data, File.ReadAllBytes(path));
                Assert.IsFalse(File.Exists(path + ".tmp"), "一時ファイルが残った");
                // 置き換え(短いものへ)
                var shorter = new NativeArray<byte>(new byte[] { 1, 2, 3 }, Allocator.Persistent);
                try { TextureIO.WriteAllBytes(path, shorter); }
                finally { shorter.Dispose(); }
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            }
            finally
            {
                bytes.Dispose();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // 色・α がまちまちな絵(行 0 = 下端)。
        private static Color32[] Pattern(int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32((byte)(x * 7 + y), (byte)(y * 9 + 3 * x), (byte)((x ^ y) * 5), (byte)(x % 5 == 0 ? 128 : 255));
            return px;
        }

        private static void AssertSame(Color32[] expected, Color32[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, "寸法が違う");
            for (int i = 0; i < expected.Length; i++)
            {
                var a = expected[i];
                var b = actual[i];
                if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a)
                    Assert.Fail($"画素 {i} が違う: {a} と {b}");
            }
        }
    }
}
