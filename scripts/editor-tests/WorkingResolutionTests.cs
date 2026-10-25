// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 編集の作業解像度(<see cref="WorkingResolution"/>)。元ファイルの辺が 8192 以下なら元ファイルのまま、超えるときだけ
    /// 取り込み後の寸法へ縮める。拡大表示に映す寸法は取り込み後の寸法。縮小はテクスチャの生データから帯ごとに読む
    /// 経路と、Color32[] の経路が同じ画素になること。
    /// </summary>
    public class WorkingResolutionTests
    {
        private TestAssets _assets;

        [SetUp]
        public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown() => _assets.Dispose();

        private static (int, int) Size(int fw, int fh, int iw, int ih)
        {
            WorkingResolution.Size(fw, fh, iw, ih, out int w, out int h);
            return (w, h);
        }

        private static (int, int) DisplaySize(int ww, int wh, int iw, int ih)
        {
            WorkingResolution.DisplaySize(ww, wh, iw, ih, out int w, out int h);
            return (w, h);
        }

        [Test]
        public void Size_IsTheSourceUpTo8KAndTheImportSizeBeyond()
        {
            // 辺が 8192 以下は、取り込みで小さくなっても元ファイルのまま(書き出しと同じ解像度で処理する)。
            Assert.AreEqual((8192, 8192), Size(8192, 8192, 2048, 2048));
            Assert.AreEqual((4096, 2048), Size(4096, 2048, 2048, 1024));
            // 辺が 8192 を超えるときは取り込み後の寸法。2 のべき乗へ丸める取り込みで縦横比が変わっても従う。
            Assert.AreEqual((4096, 4096), Size(16384, 16384, 4096, 4096));
            Assert.AreEqual((4096, 2048), Size(8193, 4000, 4096, 2048));
            // 超えても取り込みで小さくならないなら元のまま(引き伸ばさない)。
            Assert.AreEqual((16384, 8192), Size(16384, 8192, 16384, 8192));
            Assert.AreEqual((9000, 64), Size(9000, 64, 0, 0), "取り込み後の寸法が分からない");
            Assert.IsFalse(WorkingResolution.ExceedsLimit(8192, 8192));
            Assert.IsTrue(WorkingResolution.ExceedsLimit(8193, 16));
        }

        [Test]
        public void DisplaySize_IsTheImportSizeWhenImportShrinks()
        {
            Assert.AreEqual((2048, 2048), DisplaySize(8192, 8192, 2048, 2048));
            Assert.AreEqual((2048, 2048), DisplaySize(3000, 2000, 2048, 2048));
            Assert.AreEqual((1000, 500), DisplaySize(1000, 500, 1024, 512), "引き伸ばさない");
            Assert.AreEqual((64, 64), DisplaySize(64, 64, 0, 0), "取り込み後の寸法が分からない");
        }

        private static Color32[] Random(int w, int h, int seed)
        {
            var rnd = new System.Random(seed);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            return px;
        }

        // px を format の生データの並びにする(α の無い形式は α を落とす)。
        private static byte[] Raw(Color32[] px, WorkingResolution.Layout l)
        {
            var bytes = new byte[px.Length * l.bpp];
            for (int i = 0; i < px.Length; i++)
            {
                int o = i * l.bpp;
                bytes[o + l.r] = px[i].r;
                bytes[o + l.g] = px[i].g;
                bytes[o + l.b] = px[i].b;
                if (l.a >= 0) bytes[o + l.a] = px[i].a;
            }
            return bytes;
        }

        // 大きいケースは帯が複数になる(1 帯 16MB)。
        [TestCase(37, 23, 10, 7)]
        [TestCase(64, 64, 32, 32)]
        [TestCase(30, 30, 30, 11)]
        [TestCase(4096, 3000, 1000, 701)]
        public void ResizeArea_FromRawDataMatchesColor32Path(int sw, int sh, int dw, int dh)
        {
            var px = Random(sw, sh, sw * 31 + dw);
            foreach (var format in new[] { TextureFormat.RGBA32, TextureFormat.ARGB32, TextureFormat.BGRA32, TextureFormat.RGB24 })
            {
                Assert.IsTrue(WorkingResolution.TryLayout(format, out var layout), format.ToString());
                var src = px;
                if (layout.a < 0)
                {
                    // GetPixels32 は α の無い形式を α=255 で返す。
                    src = (Color32[])px.Clone();
                    for (int i = 0; i < src.Length; i++) src[i].a = 255;
                }
                var expected = WorkingResolution.ResizeArea(src, sw, sh, dw, dh);
                using (var native = new NativeArray<byte>(Raw(px, layout), Allocator.Persistent))
                {
                    var got = WorkingResolution.ResizeArea(native, layout, sw, sh, dw, dh);
                    Assert.AreEqual(expected.Length, got.Length, format.ToString());
                    for (int i = 0; i < expected.Length; i++)
                        if (!expected[i].Equals(got[i]))
                            Assert.Fail($"{format}: 画素 {i} が違います {expected[i]} vs {got[i]}");
                }
            }
        }

        private static Color32[] Pattern(int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32((byte)(x * 13), (byte)(y * 7), (byte)((x + y) * 3), (byte)(x == 0 ? 0 : 255 - y));
            return px;
        }

        [Test]
        public void ReadSource_WorkingSizeIsTheSourceEvenWhenImportShrinks()
        {
            var px = Pattern(64, 48);
            string path = _assets.WritePng("src", px, 64, 48);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.maxTextureSize = 32;
            imp.SaveAndReimport();
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            int iw = imported.width, ih = imported.height;
            Assert.Less(iw * ih, 64 * 48, "前提: 取り込み側は縮小されている");

            // 作業解像度: 辺が 8192 以下なので、取り込みで小さくなっても元ファイルのまま(取り込み済みの圧縮された
            // 画素は使わない)。
            var kind = ExportPipeline.ReadSourcePixels(path, imported, out var got, out int w, out int h, workingSize: true);
            Assert.AreEqual(ExportPipeline.SourceKind.File, kind);
            Assert.AreEqual((64, 48), (w, h));
            CollectionAssert.AreEqual(px, got);

            // 書き出し(workingSize なし)も元ファイルのまま。
            ExportPipeline.ReadSourcePixels(path, imported, out got, out w, out h);
            Assert.AreEqual((64, 48), (w, h));
            CollectionAssert.AreEqual(px, got);
        }
    }
}
