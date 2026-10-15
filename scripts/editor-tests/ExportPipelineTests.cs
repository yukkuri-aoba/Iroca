// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 書き出しの手順（<see cref="ExportPipeline"/>）。ユーザーのファイルを書き換える唯一の出口なので、
    /// 「原本を import 設定に左右されず読む」「読めない形式は取り込み済みで代替する」「書いた画素が
    /// そのまま戻る」「import 設定を引き継ぐ」「上書きで GUID（マテリアルからの参照）を保つ」を実機で見る。
    /// </summary>
    public class ExportPipelineTests
    {
        private TestAssets _assets;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();
        [TearDown] public void TearDown() => _assets.Dispose();

        private static Color32[] Pattern(int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32((byte)(x * 13), (byte)(y * 7), (byte)((x + y) * 3), (byte)(x == 0 ? 0 : 255 - y));
            return px;
        }

        // ─── 出力先 ───

        [Test]
        public void SingleOutputPath_OverwriteKeepsFolderAndSwapsExtension()
        {
            Assert.AreEqual("Assets/Tex/a.png", Norm(ExportPipeline.SingleOutputPath("Assets/Tex/a.png", false, null)));
            // 元が PNG 以外なら隣に .png を作る（元ファイルは上書きしない）。
            Assert.AreEqual("Assets/Tex/a.png", Norm(ExportPipeline.SingleOutputPath("Assets/Tex/a.jpg", false, null)));
        }

        [Test]
        public void SingleOutputPath_NewFileNameIsSanitized()
        {
            Assert.AreEqual("Assets/Tex/_CON.png", Norm(ExportPipeline.SingleOutputPath("Assets/Tex/a.png", true, "CON")));
            Assert.AreEqual("Assets/Tex/.._evil.png", Norm(ExportPipeline.SingleOutputPath("Assets/Tex/a.png", true, "../evil")));
            Assert.AreEqual("Assets/Tex/recolored.png", Norm(ExportPipeline.SingleOutputPath("Assets/Tex/a.png", true, "  ")));
        }

        [Test]
        public void BatchOutputPath_AddsSuffix()
            => Assert.AreEqual("Assets/Tex/a_recolored.png", Norm(ExportPipeline.BatchOutputPath("Assets/Tex/a.tga")));

        private static string Norm(string p) => p.Replace('\\', '/');

        // ─── 原本の読み込み ───

        [Test]
        public void ReadSource_PngIsDecodedFromFileIgnoringImportSettings()
        {
            // import 側を縮小（maxTextureSize=32）しても、書き出しは原本の 64x64 を使うこと。
            var px = Pattern(64, 64);
            string path = _assets.WritePng("src", px, 64, 64);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.maxTextureSize = 32;
            imp.SaveAndReimport();
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.AreEqual(32, imported.width, "前提: 取り込み側は縮小されている");

            var kind = ExportPipeline.ReadSourcePixels(path, imported, out var got, out int w, out int h);
            Assert.AreEqual(ExportPipeline.SourceKind.File, kind);
            Assert.AreEqual((64, 64), (w, h));
            CollectionAssert.AreEqual(px, got);
        }

        [Test]
        public void ReadSource_TgaFallsBackToImportedTextureWhenReadable()
        {
            string path = _assets.WriteBytes("src.tga", Tga(8, 4, new Color32(10, 200, 30, 255)), readable: true);
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var kind = ExportPipeline.ReadSourcePixels(path, imported, out var got, out int w, out int h);
            Assert.AreEqual(ExportPipeline.SourceKind.ImportedTexture, kind, "TGA は LoadImage で読めないので取り込み済みを使う");
            Assert.AreEqual((8, 4), (w, h));
            Assert.AreEqual(new Color32(10, 200, 30, 255), got[0]);
        }

        [Test]
        public void ReadSource_TgaWithoutReadWriteIsUnavailable()
        {
            string path = _assets.WriteBytes("src.tga", Tga(8, 4, new Color32(10, 200, 30, 255)), readable: false);
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.AreEqual(ExportPipeline.SourceKind.Unavailable,
                ExportPipeline.ReadSourcePixels(path, imported, out _, out _, out _));
        }

        // 自動化（IrocaAutomation）も原本を同じ手順で読むが、取り込み済みテクスチャは渡さない（PNG/JPG のみ）。
        // 読めない原本では取り込み済みへ逃げず、デコード失敗のエラーを返すこと。
        private const string AutomationZones = "{\"zones\":[{\"name\":\"z\"}]}";

        [Test]
        public void ReadSource_AutomationReportsUndecodableSource()
        {
            // 取り込むと Unity が読み込みエラーを出すので、AssetDatabase に載らないプロジェクト内（Temp/）に置く。
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp",
                                      "iroca_export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string src = Path.Combine(dir, "broken.png");
                File.WriteAllBytes(src, System.Text.Encoding.ASCII.GetBytes("this is not a png"));
                string outAsset = _assets.Folder + "/out.png";
                var result = JsonUtility.FromJson<IrocaAutomation.RecolorResult>(
                    IrocaAutomation.RecolorWithZones(src, AutomationZones, outAsset));
                Assert.IsFalse(result.ok);
                Assert.AreEqual("failed to decode source image.", result.error);
                Assert.IsFalse(File.Exists(TestAssets.Abs(outAsset)), "出力を書かないこと");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void ReadSource_AutomationDoesNotFallBackToImportedTexture()
        {
            // 書き出しなら取り込み済みで代替できる（Read/Write 有効の）TGA でも、自動化は受け付けない。
            string src = _assets.WriteBytes("src.tga", Tga(8, 4, new Color32(10, 200, 30, 255)), readable: true);
            var result = JsonUtility.FromJson<IrocaAutomation.RecolorResult>(
                IrocaAutomation.RecolorWithZones(src, AutomationZones, _assets.Folder + "/out.png"));
            Assert.IsFalse(result.ok);
            Assert.AreEqual("failed to decode source image.", result.error);
        }

        /// <summary>無圧縮 32bit TGA（左下原点）。</summary>
        private static byte[] Tga(int w, int h, Color32 c)
        {
            var b = new byte[18 + w * h * 4];
            b[2] = 2;                                  // 無圧縮トゥルーカラー
            b[12] = (byte)w; b[13] = (byte)(w >> 8);
            b[14] = (byte)h; b[15] = (byte)(h >> 8);
            b[16] = 32;                                // bpp
            b[17] = 8;                                 // α 8bit
            for (int i = 0; i < w * h; i++)
            {
                b[18 + i * 4] = c.b; b[19 + i * 4] = c.g; b[20 + i * 4] = c.r; b[21 + i * 4] = c.a;
            }
            return b;
        }

        // ─── PNG 化 ───

        [Test]
        public void EncodePng_RoundTripsVisiblePixelsExactly()
        {
            var px = Pattern(16, 9);
            byte[] png = ExportPipeline.EncodePng(px, 16, 9);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(tex.LoadImage(png));
                Assert.AreEqual((16, 9), (tex.width, tex.height));
                var got = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    Assert.AreEqual(px[i].a, got[i].a, $"α @{i}");
                    if (px[i].a > 0) Assert.AreEqual(px[i], got[i], $"画素 @{i}");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Test]
        public void EncodePng_RunsOnBackgroundThreadAndMatchesMainThread()
        {
            // 書き出しは PNG 化をバックグラウンドで行う（ExportView）。その前提が実機で成り立つこと。
            var px = Pattern(64, 48);
            byte[] onMain = ExportPipeline.EncodePng(px, 64, 48);
            byte[] onWorker = null;
            Exception error = null;
            var worker = new System.Threading.Thread(() =>
            {
                try { onWorker = ExportPipeline.EncodePng(px, 64, 48); }
                catch (Exception e) { error = e; }
            });
            worker.Start();
            Assert.IsTrue(worker.Join(30000), "バックグラウンドの PNG 化が終わらない");
            Assert.IsNull(error, error?.ToString());
            CollectionAssert.AreEqual(onMain, onWorker);
        }

        // ─── 書き込みと取り込み ───

        [Test]
        public void WriteAndImport_InheritsImportSettingsWithNewGuid()
        {
            string src = _assets.WritePng("src", Pattern(8, 8), 8, 8);
            var srcImp = (TextureImporter)AssetImporter.GetAtPath(src);
            srcImp.sRGBTexture = false;
            srcImp.filterMode = FilterMode.Point;
            srcImp.maxTextureSize = 256;
            srcImp.SaveAndReimport();

            string outAsset = _assets.Folder + "/out.png";
            string rel = ExportPipeline.WriteAndImport(TestAssets.Abs(outAsset), ExportPipeline.EncodePng(Pattern(8, 8), 8, 8), src, true);
            Assert.AreEqual(outAsset, rel);
            var outImp = (TextureImporter)AssetImporter.GetAtPath(outAsset);
            Assert.IsNotNull(outImp, "取り込まれていること");
            Assert.IsFalse(outImp.sRGBTexture);
            Assert.AreEqual(FilterMode.Point, outImp.filterMode);
            Assert.AreEqual(256, outImp.maxTextureSize);
            Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(src), AssetDatabase.AssetPathToGUID(outAsset), "GUID は複製しない");
        }

        [Test]
        public void WriteAndImport_OverwriteKeepsGuid()
        {
            // 上書き保存で GUID が変わると、そのテクスチャを参照するマテリアルが外れる。
            string src = _assets.WritePng("src", Pattern(8, 8), 8, 8);
            string guid = AssetDatabase.AssetPathToGUID(src);
            ExportPipeline.WriteAndImport(TestAssets.Abs(src), ExportPipeline.EncodePng(Pattern(8, 8), 8, 8), src, true);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(src));
        }

        [Test]
        public void WriteAndImport_WithoutInheritUsesDefaults()
        {
            string src = _assets.WritePng("src", Pattern(8, 8), 8, 8);
            var srcImp = (TextureImporter)AssetImporter.GetAtPath(src);
            srcImp.sRGBTexture = false;
            srcImp.SaveAndReimport();
            string outAsset = _assets.Folder + "/out.png";
            ExportPipeline.WriteAndImport(TestAssets.Abs(outAsset), ExportPipeline.EncodePng(Pattern(8, 8), 8, 8), src, false);
            Assert.IsTrue(((TextureImporter)AssetImporter.GetAtPath(outAsset)).sRGBTexture);
        }

        [Test]
        public void WriteAndImport_OutsideAssetsWritesButDoesNotImport()
        {
            string dir = Path.Combine(Path.GetTempPath(), "iroca_export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "x.png");
                Assert.IsNull(ExportPipeline.WriteAndImport(p, ExportPipeline.EncodePng(Pattern(4, 4), 4, 4), "Assets/none.png", true));
                Assert.IsTrue(File.Exists(p));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void IsSameFile_NormalizesSeparators()
        {
            Assert.IsTrue(ExportPipeline.IsSameFile("Assets/Tex/a.png", Path.Combine("Assets", "Tex", "a.png")));
            Assert.IsFalse(ExportPipeline.IsSameFile("Assets/Tex/a.png", "Assets/Tex/b.png"));
        }
    }
}
