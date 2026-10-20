// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 編集の作業解像度(<see cref="WorkingResolution"/>)。既定は取り込み後の寸法へ縮め、「元の解像度で編集」
    /// なら元ファイルのまま。縮小はテクスチャの生データから帯ごとに読む経路と、Color32[] の経路が同じ画素になること。
    /// </summary>
    public class WorkingResolutionTests
    {
        private TestAssets _assets;
        private bool _savedAtSource;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            // この PC の設定(EditorPrefs)を書き換えるので、終わったら戻す。
            _savedAtSource = WorkingResolution.AtSourceResolution;
            WorkingResolution.AtSourceResolution = false;
        }

        [TearDown]
        public void TearDown()
        {
            WorkingResolution.AtSourceResolution = _savedAtSource;
            _assets.Dispose();
        }

        private static (int, int) Size(int fw, int fh, int iw, int ih, bool atSource)
        {
            WorkingResolution.Size(fw, fh, iw, ih, atSource, out int w, out int h);
            return (w, h);
        }

        [Test]
        public void Size_IsTheImportSizeWhenImportShrinks()
        {
            Assert.AreEqual((2048, 2048), Size(8192, 8192, 2048, 2048, false));
            Assert.AreEqual((2048, 1024), Size(4096, 2048, 2048, 1024, false));
            Assert.AreEqual((8192, 8192), Size(8192, 8192, 2048, 2048, true), "元の解像度で編集");
            // 2 のべき乗へ丸める取り込みで縦横比が変わっても、取り込み後の寸法に従う(ゲームに出る寸法)。
            Assert.AreEqual((2048, 2048), Size(3000, 2000, 2048, 2048, false));
            // 取り込みで大きくなる(2 のべき乗へ切り上げ)なら引き伸ばさない。
            Assert.AreEqual((1000, 500), Size(1000, 500, 1024, 512, false));
            Assert.AreEqual((64, 64), Size(64, 64, 0, 0, false), "取り込み後の寸法が分からない");
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
        public void ReadSource_WorkingSizeFollowsImportMaxSizeUnlessAtSource()
        {
            var px = Pattern(64, 48);
            string path = _assets.WritePng("src", px, 64, 48);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.maxTextureSize = 32;
            imp.SaveAndReimport();
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            int iw = imported.width, ih = imported.height;
            Assert.Less(iw * ih, 64 * 48, "前提: 取り込み側は縮小されている");

            // 既定: 元ファイルを取り込み後の寸法へ面積平均で縮める(取り込み済みの圧縮された画素は使わない)。
            // 64x48 は 2 のべき乗へ丸めてから Max Size に収めるので、取り込み後は縦横比が変わり得る(それに従う)。
            var kind = ExportPipeline.ReadSourcePixels(path, imported, out var got, out int w, out int h, workingSize: true);
            Assert.AreEqual(ExportPipeline.SourceKind.File, kind);
            Assert.AreEqual((iw, ih), (w, h));
            CollectionAssert.AreEqual(WorkingResolution.ResizeArea(px, 64, 48, iw, ih), got);

            // 元の解像度で編集: 元ファイルのまま。
            WorkingResolution.AtSourceResolution = true;
            ExportPipeline.ReadSourcePixels(path, imported, out got, out w, out h, workingSize: true);
            Assert.AreEqual((64, 48), (w, h));
            CollectionAssert.AreEqual(px, got);

            // 書き出し(workingSize なし)は設定に関係なく元ファイルのまま。
            WorkingResolution.AtSourceResolution = false;
            ExportPipeline.ReadSourcePixels(path, imported, out got, out w, out h);
            Assert.AreEqual((64, 48), (w, h));
            CollectionAssert.AreEqual(px, got);
        }
    }
}
