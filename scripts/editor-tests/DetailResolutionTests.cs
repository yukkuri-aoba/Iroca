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
    /// 拡大表示の解像度の設定（「元の解像度」。<see cref="PreviewView.DetailFullResolution"/>。0.4.0 の計画の 1-1）。
    /// 元が 64×64・取り込みの Max Size が 32 のテクスチャで、オフなら 32、オンなら 64 の画素で拡大表示を作る。
    /// どちらでも、変更後の切り出し・変更前の切り出し（「元を表示」）・マスクの重ね表示が同じ格子・同じ範囲になること、
    /// 切り替えても色替えをやり直さず、確定段の出力（書き出しと同じもの）は変わらないことを確かめる。
    /// 見えている範囲から切り出しを決める所は GUI の中でしか動かないので、切り出しは範囲を指定して予約する
    /// （<see cref="DetailPreviewView.ScheduleRange"/>。範囲はタイルの格子で広がるので、テストは格子を小さくして
    /// 範囲がちょうどタイルの境目になるようにする）。
    /// </summary>
    public class DetailResolutionTests
    {
        private const int W = 64, H = 64, Import = 32;

        private TestAssets _assets;
        private string _path;
        private string _guid;
        private Texture2D _texture;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private bool _fullResBefore;
        private bool _fullResHadKey;
        private int _tileTexelsBefore;
        private IrocaWindow _win;

        [SetUp]
        public void SetUp()
        {
            _fullResHadKey = EditorPrefs.HasKey(PreviewView.DetailFullResolutionPrefKey);
            _fullResBefore = PreviewView.DetailFullResolution;
            _tileTexelsBefore = DetailTileGrid.TileTexels;

            _assets = TestAssets.Create();
            _path = _assets.WritePng("detail", Scene(), W, H);
            var importer = (TextureImporter)AssetImporter.GetAtPath(_path);
            importer.maxTextureSize = Import;
            importer.SaveAndReimport();
            _guid = AssetDatabase.AssetPathToGUID(_path);
            _texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_path);
            Assert.AreEqual((Import, Import), (_texture.width, _texture.height), "取り込みで縮んでいない(テストの前提が崩れている)");

            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
        }

        [TearDown]
        public void TearDown()
        {
            if (_win != null)
            {
                Undo.ClearUndo(_win);
                Object.DestroyImmediate(_win);
            }
            _win = null;
            PreviewView.DetailFullResolution = _fullResBefore;
            DetailTileGrid.TileTexels = _tileTexelsBefore;
            if (!_fullResHadKey) EditorPrefs.DeleteKey(PreviewView.DetailFullResolutionPrefKey);
            if (_prefBefore == null) EditorPrefs.DeleteKey(_prefKey);
            else EditorPrefs.SetString(_prefKey, _prefBefore);
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) File.Delete(f);
            }
            _assets.Dispose();
        }

        [TestCase(false, TestName = "DetailLayersShareOneGrid(Off_ImportSize)")]
        [TestCase(true, TestName = "DetailLayersShareOneGrid(On_FullResolution)")]
        public void DetailLayersShareOneGrid(bool fullRes)
        {
            PreviewView.DetailFullResolution = fullRes;
            OpenWindow();
            var preview = _win.Preview;
            RunFullStage(preview);

            int det = fullRes ? W : Import;
            Assert.AreEqual((det, det), (preview.DetailW, preview.DetailH), "拡大表示の寸法");
            var source = TrueSource(preview);
            var full = preview.FullOutput.ToArray();
            var expectedSource = fullRes ? source : WorkingResolution.ResizeArea(source, W, H, det, det);
            var expectedOutput = fullRes ? full : WorkingResolution.ResizeArea(full, W, H, det, det);
            Assert.AreNotEqual(Changed(source, full), 0, "色替えが起きていない(テストの前提が崩れている)");
            AssertSamePixels(expectedSource, preview.DetailSource.ToArray(), det, "拡大表示の変更前の土台");
            AssertSamePixels(expectedOutput, preview.DetailOutput.ToArray(), det, "拡大表示の変更後の土台");

            // 共通の「塗らない」の境目(元の画像の x = 16。拡大表示では det/4)をまたぐ範囲を等倍で切り出す
            // (格子を det/8 にして、範囲をちょうどタイルの境目にする)。
            DetailTileGrid.TileTexels = det / 8;
            int x0 = det / 8, y0 = det / 8, cw = det / 2, ch = det / 2;
            var dv = preview.DetailView;
            Assert.IsTrue(dv.ScheduleRange(x0, y0, x0 + cw, y0 + ch, 2f), "切り出しを予約しない");
            Wait(() => dv.HasPendingResult, "拡大表示の切り出し");
            dv.ApplyPendingResult();
            Assert.AreEqual((x0, y0, cw, ch), (dv.detailOriginX, dv.detailOriginY, dv.detailCropW, dv.detailCropH));
            var after = dv.detailPreviewTexture;
            Assert.AreEqual((cw, ch), (after.width, after.height), "変更後の切り出しの寸法");
            AssertSamePixels(Crop(expectedOutput, det, x0, y0, cw, ch), after.GetPixels32(), cw, "変更後の切り出し");

            Assert.IsTrue(dv.EnsureDetailRaw(), "変更前の切り出しを作れない");
            var before = dv.detailRawTexture;
            Assert.AreEqual((cw, ch), (before.width, before.height), "変更前の切り出しの寸法");
            AssertSamePixels(Crop(expectedSource, det, x0, y0, cw, ch), before.GetPixels32(), cw, "変更前の切り出し");

            var masks = _win._maskView;
            preview.RebuildDetailMaskOverlay();
            Wait(() =>
            {
                masks.ApplyPendingDetailOverlay();
                return masks.DetailOverlayMatches(det, det, x0, y0, cw, ch, cw, ch);
            }, "マスクの重ね表示");
            var overlay = masks.detailMaskOverlayTexture;
            Assert.IsNotNull(overlay, "共通のマスクの重ね表示が無い");
            Assert.AreEqual((cw, ch), (overlay.width, overlay.height), "マスクの重ね表示の寸法");
            var ov = overlay.GetPixels32();
            int edge = det / 4 - x0;   // 切り出しの中で、塗らないの範囲が終わる列
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    bool covered = ov[y * cw + x].a > 0;
                    if (covered != x < edge)
                        Assert.Fail($"マスクの重ね表示が切り出しとずれている: (x={x}, y={y}) が {(covered ? "塗らない" : "空き")}"
                                    + $"(塗らないは切り出しの x < {edge})");
                }
        }

        [Test]
        public void Toggling_RebuildsTheDetailBaseWithoutRecoloring()
        {
            PreviewView.DetailFullResolution = false;
            OpenWindow();
            var preview = _win.Preview;
            RunFullStage(preview);
            var fullRef = preview.FullOutput;
            var full = fullRef.ToArray();
            var shrunkByJob = preview.DetailOutput.ToArray();
            Assert.AreEqual(Import * Import, shrunkByJob.Length);

            PreviewView.DetailFullResolution = true;
            preview.SyncDetailResolution();
            Assert.IsFalse(preview.IsGenerating, "切り替えで色替えをやり直した");
            Assert.AreEqual((W, H), (preview.DetailW, preview.DetailH), "オン: 作業解像度で映す");
            Assert.AreSame(fullRef, preview.FullOutput, "確定段の出力が替わった");
            Assert.AreSame(preview.FullOutput, preview.DetailOutput, "オン: 確定段の出力をそのまま映す");
            Assert.IsNull(preview.DetailView.detailPreviewTexture, "前の寸法の切り出しが残っている");

            PreviewView.DetailFullResolution = false;
            preview.SyncDetailResolution();
            Assert.IsFalse(preview.IsGenerating, "切り替えで色替えをやり直した");
            Assert.AreEqual((Import, Import), (preview.DetailW, preview.DetailH), "オフ: 取り込み後の寸法で映す");
            Assert.AreSame(fullRef, preview.FullOutput, "確定段の出力が替わった");
            AssertSamePixels(shrunkByJob, preview.DetailOutput.ToArray(), Import, "オフへ戻して縮めたものと、確定段のジョブが縮めたもの");
            AssertSamePixels(full, preview.FullOutput.ToArray(), W, "確定段の出力");
        }

        // 差分表示の間に作り始めた切り出しは、同じ範囲の変更前の切り出しを持ってくる(差分はこの 2 枚から GPU で描く。1-2)。
        // それは「元を表示」と同じ口(EnsureDetailRaw)がその場で作るものと同じ画素で、同じ範囲なら作り直さない。
        [Test]
        public void DiffMode_CropBringsTheSameBeforeCropAsEnsureDetailRaw()
        {
            PreviewView.DetailFullResolution = false;
            OpenWindow();
            var preview = _win.Preview;
            RunFullStage(preview);
            preview.diffMode = true;
            var dv = preview.DetailView;
            // 縮めて切り出す(表示の 1 画素に元の 2×2 画素。タイルは元の 8 画素四方を 4 画素四方に縮める)。
            DetailTileGrid.TileTexels = 4;
            Assert.IsTrue(dv.ScheduleRange(3, 5, 23, 23, 0.5f), "切り出しを予約しない");
            Wait(() => dv.HasPendingResult, "拡大表示の切り出し");
            dv.ApplyPendingResult();
            var raw = dv.detailRawTexture;
            var after = dv.detailPreviewTexture;
            Assert.IsNotNull(raw, "差分表示中の切り出しが、変更前の切り出しを持ってこない");
            Assert.AreEqual((0, 0, 24, 24), (dv.detailOriginX, dv.detailOriginY, dv.detailCropW, dv.detailCropH),
                "範囲を覆うタイルの並び(元の 8 画素四方 × 3)");
            Assert.AreEqual((12, 12), (after.width, after.height), "変更後の切り出しの寸法");
            Assert.AreEqual((after.width, after.height), (raw.width, raw.height), "変更前の切り出しの寸法");
            var fromJob = raw.GetPixels32();
            int version = dv.detailRawVersion;
            Assert.IsTrue(dv.EnsureDetailRaw());
            Assert.AreEqual(version, dv.detailRawVersion, "同じ範囲なのに作り直した");

            dv.ReleaseDetailRaw();
            Assert.IsTrue(dv.EnsureDetailRaw(), "変更前の切り出しを作れない");
            AssertSamePixels(dv.detailRawTexture.GetPixels32(), fromJob, after.width, "ジョブの変更前の切り出しと、その場で作ったもの");
        }

        // 拡大表示はタイルを並べて作る(0.4.0 の計画の 3.5 の案 A)。並べたものはタイルを 1 枚ずつ縮めたものと同じで、
        // パンでは控えにあるタイルを使う(見えている範囲の周り 1 枚分は先読みしてある)。タイルの並びが変わらなければ
        // 作り直さず、確定段の出力が新しくなったら作り直す。
        [Test]
        public void Tiles_AreReusedOnPan_AndRebuiltForANewOutput()
        {
            PreviewView.DetailFullResolution = true;   // 土台は 64×64
            OpenWindow();
            var preview = _win.Preview;
            RunFullStage(preview);
            DetailTileGrid.TileTexels = 4;   // 元の 8 画素四方を 4 画素四方に縮める(8×8 枚)
            var dv = preview.DetailView;
            var cache = dv.AfterTiles;

            // タイル 1〜3 列 × 1〜3 行を並べる。周りの 1 枚分(0〜4)も先読みする。
            Show(dv, 8, 8, 32, 32);
            Assert.AreEqual((8, 8, 24, 24), (dv.detailOriginX, dv.detailOriginY, dv.detailCropW, dv.detailCropH));
            Assert.AreEqual(25, cache.ComputedTiles, "並べた 9 枚と先読みの 16 枚");
            AssertSamePixels(TilesShrunkAlone(preview.DetailOutput.ToArray(), 1, 1, 4, 4),
                dv.detailPreviewTexture.GetPixels32(), 12, "並べたタイルと、1 枚ずつ縮めたもの");

            // 同じタイルの並びに収まる範囲なら作り直さない。
            Assert.IsFalse(dv.ScheduleRange(9, 9, 31, 31, 0.5f), "同じタイルの並びなのに作り直した");

            // 1 枚分パンする。並べる 9 枚は控えにあるので作らない(作るのは新しい周りの 9 枚だけ)。
            Show(dv, 16, 16, 40, 40);
            Assert.AreEqual(34, cache.ComputedTiles, "パンで、控えにあるタイルを作り直した");
            AssertSamePixels(TilesShrunkAlone(preview.DetailOutput.ToArray(), 2, 2, 5, 5),
                dv.detailPreviewTexture.GetPixels32(), 12, "パンした後の並び");

            // 確定段の出力が新しくなったら、同じ並びでも作り直す(前の出力のタイルは使わない)。
            _win.Session.zones[0].targetColor = new Color(0.9f, 0.8f, 0.1f);
            _win.Session.zones[0].UpdateCacheIfNeeded();
            RunFullStage(preview);
            Show(dv, 16, 16, 40, 40);
            Assert.AreEqual(34 + 9 + 16, cache.ComputedTiles, "新しい出力のタイルを並べる 9 枚と先読みの 16 枚");
            AssertSamePixels(TilesShrunkAlone(preview.DetailOutput.ToArray(), 2, 2, 5, 5),
                dv.detailPreviewTexture.GetPixels32(), 12, "新しい出力の並び");
        }

        // 範囲 [x0,x1)×[y0,y1) を、元の 1 画素が画面の 0.5pt の倍率で並べ、届くのと先読みが終わるのを待つ。
        private static void Show(DetailPreviewView dv, int x0, int y0, int x1, int y1)
        {
            Assert.IsTrue(dv.ScheduleRange(x0, y0, x1, y1, 0.5f), "切り出しを予約しない");
            Wait(() => dv.HasPendingResult, "拡大表示の切り出し");
            dv.ApplyPendingResult();
            Wait(() => !dv.IsPrefetching, "先読み");
        }

        // 64×64 の出力のタイル(元の 8 画素四方)を 1 枚ずつ 4 画素四方へ縮めて、列 [c0,c1) × 行 [r0,r1) を並べたもの。
        private static Color32[] TilesShrunkAlone(Color32[] output, int c0, int r0, int c1, int r1)
        {
            int ow = (c1 - c0) * 4, oh = (r1 - r0) * 4;
            var dst = new Color32[ow * oh];
            for (int r = r0; r < r1; r++)
                for (int c = c0; c < c1; c++)
                {
                    var tile = PixelProcessor.BoxDownsampleRegion(output, W, c * 8, r * 8, 8, 8, 4, 4, 0.5f);
                    for (int y = 0; y < 4; y++)
                        System.Array.Copy(tile, y * 4, dst, ((r - r0) * 4 + y) * ow + (c - c0) * 4, 4);
                }
            return dst;
        }

        // 確定段が走っている間に切り替えても、終わったときの拡大表示の土台は設定どおりで、確定段の出力と食い違わない。
        [TestCase(false, TestName = "TogglingWhileGenerating_EndsConsistent(ToOn)")]
        [TestCase(true, TestName = "TogglingWhileGenerating_EndsConsistent(ToOff)")]
        public void TogglingWhileGenerating_EndsConsistent(bool startFullRes)
        {
            PreviewView.DetailFullResolution = startFullRes;
            OpenWindow();
            var preview = _win.Preview;
            preview.GeneratePreviewAsync();
            PreviewView.DetailFullResolution = !startFullRes;
            preview.SyncDetailResolution();
            Drain(preview);

            Assert.IsNotNull(preview.FullOutput, "確定段の出力が無い");
            var full = preview.FullOutput.ToArray();
            int det = startFullRes ? Import : W;
            Assert.AreEqual((det, det), (preview.DetailW, preview.DetailH), "拡大表示の寸法");
            Assert.IsNotNull(preview.DetailOutput, "拡大表示の変更後の土台が無い");
            var expected = det == W ? full : WorkingResolution.ResizeArea(full, W, H, det, det);
            AssertSamePixels(expected, preview.DetailOutput.ToArray(), det, "拡大表示の変更後の土台");
        }

        // ───────────── 手順 ─────────────

        // 赤い帯の大部分を色替えし、共通の「塗らない」で左の 16 列を外す。
        private void OpenWindow()
        {
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_texture, _win.SourceTexture, "窓がテスト用のテクスチャを開いていない");
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
            var region = new bool[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < 16; x++)
                    region[y * W + x] = true;
            Assert.IsTrue(_win._maskView.ApplyRegion(region,
                new MaskPaintView.MaskDestination(null, MaskRegionOp.DontPaintHere), out _), "共通の塗らない");
        }

        private static void RunFullStage(PreviewView preview)
        {
            preview.GeneratePreviewAsync();
            Drain(preview);
            Assert.IsNotNull(preview.FullOutput, "確定段の出力が無い");
            Assert.AreEqual(W * H, preview.FullOutput.Length);
        }

        // 概要段・確定段が終わるまでメインスレッドへの戻りを捌く(WindowOutputParityTests と同じ待ち方)。
        private static void Drain(PreviewView preview)
        {
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

        private static void Wait(System.Func<bool> done, string what)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                PreviewJobMainThread.Drain();
                if (done()) return;
                Assert.Less(watch.Elapsed.TotalSeconds, 30, what + "が終わらない");
                Thread.Sleep(2);
            }
        }

        private Color32[] TrueSource(PreviewView preview)
        {
            Assert.IsTrue(preview.TryGetTrueSourcePixels(_texture, out var src, out int w, out int h), "原本を読めない");
            Assert.AreEqual((W, H), (w, h), "作業解像度は元の解像度");
            return src.ToArray();
        }

        // ───────────── 入力と比べ方 ─────────────

        // 左半分は赤、右半分は青。縦に陰影をつけ、1 画素おきに明るさを変える(縮めると平均の値になり、間引きとは違う)。
        // 行 0 = 画像の下端。
        private static Color32[] Scene()
        {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float s = 0.6f + 0.4f * y / (H - 1);
                for (int x = 0; x < W; x++)
                {
                    float k = ((x + y) & 1) == 0 ? s : s * 0.85f;
                    px[y * W + x] = x < W / 2 ? Shade(200, 40, 40, k) : Shade(40, 90, 200, k);
                }
            }
            return px;
        }

        private static Color32 Shade(int r, int g, int b, float s)
            => new Color32((byte)(r * s), (byte)(g * s), (byte)(b * s), 255);

        private static Color32[] Crop(Color32[] px, int w, int x0, int y0, int cw, int ch)
        {
            var dst = new Color32[cw * ch];
            for (int y = 0; y < ch; y++)
                System.Array.Copy(px, (y0 + y) * w + x0, dst, y * cw, cw);
            return dst;
        }

        private static int Changed(Color32[] a, Color32[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (!Same(a[i], b[i])) n++;
            return n;
        }

        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        private static void AssertSamePixels(Color32[] expected, Color32[] actual, int w, string what)
        {
            Assert.AreEqual(expected.Length, actual.Length, what + ": 寸法が違う");
            int diff = 0, first = -1;
            for (int i = 0; i < expected.Length; i++)
                if (!Same(expected[i], actual[i])) { diff++; if (first < 0) first = i; }
            if (diff > 0)
                Assert.Fail($"{what}: {diff} 画素が違う。最初は (x={first % w}, y={first / w}) で {expected[first]} と {actual[first]}");
        }
    }
}
