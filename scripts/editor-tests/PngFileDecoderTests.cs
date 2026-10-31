// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Iroca.EditorTests
{
    /// <summary>
    /// PNG を Mono のヒープの外へ読む口(<see cref="PngFileDecoder"/>)が LoadImage と同じ画素になること。扱わない形式と壊れたファイルは
    /// null を返して LoadImage に任せること。元ファイルの読み口(<see cref="TextureIO.DecodeSourceFile"/>)の結果が前の経路と同じこと。
    /// 圧縮テクスチャを GPU で展開した値が GetPixels32 に近いこと。
    /// </summary>
    public class PngFileDecoderTests
    {
        private const int W = 37, H = 23;
        private string _dir;
        private TestAssets _assets;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "iroca-png-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _assets = TestAssets.Create();
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_dir, true);
            _assets.Dispose();
        }

        // 各行に PNG の 5 種類のフィルタを順に当てる(row は元の画素。prev は前の行)。
        private static byte[] Filtered(byte[][] rows, int bpp)
        {
            int rowBytes = rows[0].Length;
            var result = new byte[rows.Length * (rowBytes + 1)];
            var zero = new byte[rowBytes];
            for (int y = 0; y < rows.Length; y++)
            {
                int filter = y % 5;
                var row = rows[y];
                var prev = y > 0 ? rows[y - 1] : zero;
                int o = y * (rowBytes + 1);
                result[o++] = (byte)filter;
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int predict;
                    switch (filter)
                    {
                        case 1: predict = a; break;
                        case 2: predict = b; break;
                        case 3: predict = (a + b) >> 1; break;
                        case 4:
                            int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                            predict = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            break;
                        default: predict = 0; break;
                    }
                    result[o + i] = (byte)(row[i] - predict);
                }
            }
            return result;
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var name = Encoding.ASCII.GetBytes(type);
            WriteBigEndian(s, (uint)data.Length);
            s.Write(name, 0, 4);
            s.Write(data, 0, data.Length);
            WriteBigEndian(s, 0);   // CRC は読まない
        }

        private static void WriteBigEndian(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        private static uint Adler(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var d in data) { a = (a + d) % 65521; b = (b + a) % 65521; }
            return b << 16 | a;
        }

        private static byte[] Idat(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                using (var z = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true)) z.Write(raw, 0, raw.Length);
                WriteBigEndian(ms, Adler(raw));
                return ms.ToArray();
            }
        }

        // CRC を計算しない PNG は LoadImage が読めないので、LoadImage と比べるものは CRC も入れる。
        private static uint Crc(byte[] name, byte[] data)
        {
            uint c = 0xFFFFFFFF;
            void Add(byte[] bytes)
            {
                foreach (var d in bytes)
                {
                    c ^= d;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
            }
            Add(name);
            Add(data);
            return c ^ 0xFFFFFFFF;
        }

        private static void CrcChunk(Stream s, string type, byte[] data)
        {
            var name = Encoding.ASCII.GetBytes(type);
            WriteBigEndian(s, (uint)data.Length);
            s.Write(name, 0, 4);
            s.Write(data, 0, data.Length);
            WriteBigEndian(s, Crc(name, data));
        }

        private string WritePng(string name, int depth, int colorType, byte[][] rows, int bpp, byte[] plte = null, byte[] trns = null,
            int interlace = 0, bool splitIdat = false)
        {
            string path = Path.Combine(_dir, name + ".png");
            using (var f = File.Create(path))
            {
                f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                using (var ihdr = new MemoryStream())
                {
                    WriteBigEndian(ihdr, W);
                    WriteBigEndian(ihdr, (uint)rows.Length);
                    ihdr.WriteByte((byte)depth); ihdr.WriteByte((byte)colorType);
                    ihdr.WriteByte(0); ihdr.WriteByte(0); ihdr.WriteByte((byte)interlace);
                    CrcChunk(f, "IHDR", ihdr.ToArray());
                }
                CrcChunk(f, "tEXt", Encoding.ASCII.GetBytes("Comment\0ancillary chunk"));
                if (plte != null) CrcChunk(f, "PLTE", plte);
                if (trns != null) CrcChunk(f, "tRNS", trns);
                var idat = Idat(Filtered(rows, bpp));
                if (splitIdat)
                {
                    int half = idat.Length / 2;
                    var a = new byte[half]; var b = new byte[idat.Length - half];
                    Buffer.BlockCopy(idat, 0, a, 0, half);
                    Buffer.BlockCopy(idat, half, b, 0, b.Length);
                    CrcChunk(f, "IDAT", a);
                    CrcChunk(f, "IDAT", b);
                }
                else CrcChunk(f, "IDAT", idat);
                CrcChunk(f, "IEND", new byte[0]);
            }
            return path;
        }

        private static byte[][] Rows(int rowBytes)
        {
            var rows = new byte[H][];
            var r = new System.Random(7);
            for (int y = 0; y < H; y++)
            {
                rows[y] = new byte[rowBytes];
                r.NextBytes(rows[y]);
            }
            return rows;
        }

        private static byte[][] Masked(byte[][] rows, byte mask)
        {
            foreach (var row in rows)
                for (int i = 0; i < row.Length; i++) row[i] &= mask;
            return rows;
        }

        private static byte[] Palette()
        {
            var plte = new byte[256 * 3];
            for (int i = 0; i < 256; i++) { plte[i * 3] = (byte)i; plte[i * 3 + 1] = (byte)(255 - i); plte[i * 3 + 2] = (byte)(i * 7); }
            return plte;
        }

        private static void AssertSameAsLoadImage(string path)
        {
            var mine = PngFileDecoder.TryDecode(path);
            Assert.IsNotNull(mine, "読めなかった");
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(path)), "テストの前提: LoadImage が読める");
                var theirs = WorkingResolution.NativePixelsOf(tex, null);
                try
                {
                    Assert.AreEqual((theirs.Width, theirs.Height), (mine.Width, mine.Height));
                    CollectionAssert.AreEqual(theirs.ToArray(), mine.ToArray());
                }
                finally { theirs.Release(); }
            }
            finally
            {
                Object.DestroyImmediate(tex);
                mine.Release();
            }
        }

        [Test] public void Rgba8() => AssertSameAsLoadImage(WritePng("rgba8", 8, 6, Rows(W * 4), 4));
        [Test] public void Rgb8() => AssertSameAsLoadImage(WritePng("rgb8", 8, 2, Rows(W * 3), 3));
        [Test] public void Gray8() => AssertSameAsLoadImage(WritePng("gray8", 8, 0, Rows(W), 1));
        [Test] public void GrayAlpha8() => AssertSameAsLoadImage(WritePng("ga8", 8, 4, Rows(W * 2), 2));
        [Test] public void Rgba16() => AssertSameAsLoadImage(WritePng("rgba16", 16, 6, Rows(W * 8), 8));
        [Test] public void Rgb16() => AssertSameAsLoadImage(WritePng("rgb16", 16, 2, Rows(W * 6), 6));
        [Test] public void Gray16() => AssertSameAsLoadImage(WritePng("gray16", 16, 0, Rows(W * 2), 2));
        [Test] public void GrayAlpha16() => AssertSameAsLoadImage(WritePng("ga16", 16, 4, Rows(W * 4), 4));
        [Test] public void Gray4() => AssertSameAsLoadImage(WritePng("gray4", 4, 0, Rows((W * 4 + 7) / 8), 1));
        [Test] public void Gray2() => AssertSameAsLoadImage(WritePng("gray2", 2, 0, Rows((W * 2 + 7) / 8), 1));
        [Test] public void Gray1() => AssertSameAsLoadImage(WritePng("gray1", 1, 0, Rows((W + 7) / 8), 1));
        [Test] public void Palette8() => AssertSameAsLoadImage(WritePng("p8", 8, 3, Rows(W), 1, Palette()));
        [Test] public void Palette4() => AssertSameAsLoadImage(WritePng("p4", 4, 3, Rows((W * 4 + 7) / 8), 1, Palette()));
        [Test] public void Palette1() => AssertSameAsLoadImage(WritePng("p1", 1, 3, Rows((W + 7) / 8), 1, Palette()));
        [Test]
        public void PaletteWithAlpha() =>
            AssertSameAsLoadImage(WritePng("p8a", 8, 3, Rows(W), 1, Palette(), new byte[] { 0, 64, 128, 200 }));
        [Test] public void SeveralDataChunks() => AssertSameAsLoadImage(WritePng("split", 8, 6, Rows(W * 4), 4, splitIdat: true));

        // 扱わないものは null(LoadImage へ回る)。
        [Test]
        public void Unsupported_IsLeftToLoadImage()
        {
            Assert.IsNull(PngFileDecoder.TryDecode(WritePng("key", 8, 2, Rows(W * 3), 3, trns: new byte[6])), "RGB の透明色");
            Assert.IsNull(PngFileDecoder.TryDecode(WritePng("adam7", 8, 2, Rows(W * 3), 3, interlace: 1)), "インターレース");
        }

        [Test]
        public void BrokenFiles_AreNotDecoded()
        {
            string good = WritePng("good", 8, 6, Rows(W * 4), 4);
            byte[] bytes = File.ReadAllBytes(good);

            string truncated = Path.Combine(_dir, "truncated.png");
            var part = new byte[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, part, 0, part.Length);
            File.WriteAllBytes(truncated, part);
            Assert.IsNull(PngFileDecoder.TryDecode(truncated), "途中で切れている");

            string notPng = Path.Combine(_dir, "not.png");
            File.WriteAllText(notPng, "this is not a png at all");
            Assert.IsNull(PngFileDecoder.TryDecode(notPng), "PNG でない");

            string headerOnly = Path.Combine(_dir, "sig.png");
            var sig = new byte[8];
            Buffer.BlockCopy(bytes, 0, sig, 0, 8);
            File.WriteAllBytes(headerOnly, sig);
            Assert.IsNull(PngFileDecoder.TryDecode(headerOnly), "署名だけ");
        }

        [Test]
        public void LockedFile_ThrowsLikeTheOtherPath()
        {
            string path = WritePng("locked", 8, 6, Rows(W * 4), 4);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => TextureIO.DecodeSourceFile(path, null));
        }

        // PNG・JPG でないファイルは LoadImage へ渡す前に分かる(読めないのに全部を管理配列へ読まない)。
        [Test]
        public void OtherFormats_AreNotReadAtAll()
        {
            string tga = Path.Combine(_dir, "a.tga");
            File.WriteAllBytes(tga, new byte[64]);
            Assert.IsNull(TextureIO.DecodeSourceFile(tga, null));
            string tiny = Path.Combine(_dir, "tiny.png");
            File.WriteAllBytes(tiny, new byte[] { 1, 2, 3 });
            Assert.IsNull(TextureIO.DecodeSourceFile(tiny, null));
        }

        // 辺が 8192 を超える原本は取り込み後の寸法へ縮める。LoadImage の経路と同じ画素。
        [Test]
        public void ShrinkingToTheImportedSize_MatchesTheTexturePath()
        {
            const int sw = 8200, sh = 4;
            var px = new Color32[sw * sh];
            var r = new System.Random(3);
            for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));
            string path = _assets.WritePng("wide", px, sw, sh);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.Less(imported.width, sw, "テストの前提: 取り込みで縮む");

            var got = TextureIO.DecodeSourceFile(path, imported);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(path)));
                var expected = WorkingResolution.NativePixelsOf(tex, imported);
                try
                {
                    Assert.AreEqual((imported.width, imported.height), (got.Width, got.Height));
                    CollectionAssert.AreEqual(expected.ToArray(), got.ToArray());
                }
                finally { expected.Release(); }
            }
            finally
            {
                got.Release();
                Object.DestroyImmediate(tex);
            }
        }

        // 同じ寸法なら受け取った画素をそのまま返す(写さない)。
        [Test]
        public void FitToWorkingSize_KeepsTheSameSize()
        {
            var source = NativePixels.Allocate(4, 4);
            try { Assert.AreSame(source, WorkingResolution.FitToWorkingSize(source, null)); }
            finally { source.Release(); }
        }

        // 圧縮テクスチャを GPU で展開した値。DXT は GPU の丸めで最大 1 段、BC7 は同じ。GPU が無ければ GetPixels32 の経路なので同じ。
        [TestCase(TextureImporterFormat.DXT1, 1)]
        [TestCase(TextureImporterFormat.DXT5, 1)]
        [TestCase(TextureImporterFormat.DXT1Crunched, 1)]
        [TestCase(TextureImporterFormat.DXT5Crunched, 1)]
        [TestCase(TextureImporterFormat.BC7, 0)]
        [TestCase(TextureImporterFormat.RGBAHalf, 0)]
        [TestCase(TextureImporterFormat.BC6H, 3)]
        [TestCase(TextureImporterFormat.BC5, 0)]
        public void CompressedTexture_ReadsLikeGetPixels32(TextureImporterFormat format, int allowed)
        {
            const int size = 128;
            var px = new Color32[size * size];
            var r = new System.Random(5);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = new Color32((byte)(x * 2), (byte)(y * 2), (byte)(x ^ y), (byte)(r.Next(4) == 0 ? 128 : 255));
            string path = _assets.WritePng("compressed", px, size, size);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = true;
            var ps = importer.GetPlatformTextureSettings("Standalone");
            ps.overridden = true;
            ps.format = format;
            importer.SetPlatformTextureSettings(ps);
            importer.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var expected = tex.GetPixels32();
            var got = WorkingResolution.NativePixelsOf(tex, null);
            try
            {
                int max = 0;
                var a = got.ToArray();
                Assert.AreEqual(expected.Length, a.Length);
                for (int i = 0; i < a.Length; i++)
                {
                    max = Math.Max(max, Math.Abs(a[i].r - expected[i].r));
                    max = Math.Max(max, Math.Abs(a[i].g - expected[i].g));
                    max = Math.Max(max, Math.Abs(a[i].b - expected[i].b));
                    max = Math.Max(max, Math.Abs(a[i].a - expected[i].a));
                }
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.AreEqual(0, max, "GPU が無ければ GetPixels32 と同じ");
                else Assert.LessOrEqual(max, allowed, $"GPU で展開した値が離れている: {tex.format}");
            }
            finally { got.Release(); }
        }
    }
}
