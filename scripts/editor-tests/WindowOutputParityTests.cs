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
    /// 色替えの経路どうしが同じ画素を出すことを固定する（0.4.0 の計画の 0-2）。フェーズ 4 で色替えの入力の組み立てを
    /// 1 本（RecolorInput）にまとめる前に、今の振る舞いを押さえておく。
    /// <list type="bullet">
    /// <item>ウィンドウの確定段（プレビューのフル段）の出力 = <see cref="SessionRecolor.Apply(Color32[], int, int, IrocaSessionState, CancellationToken)"/>
    /// （非破壊ビルド・シーンのプレビューの入口）に同じセッションを当てたもの。選択キャッシュに当たる 2 回目も同じ。</item>
    /// <item>ウィンドウの書き出し（「適用して保存」） = 自動化 API の PNG（iroca_recolor の png。土台は開いている窓の編集内容）。</item>
    /// </list>
    /// マスクは、共通の「塗らない」・ゾーンの「塗らない」・ゾーンの「ここも塗る」を、右クリックと AI 提案と同じ口
    /// （<see cref="MaskPaintView.ApplyRegion"/>）で塗る。ウィンドウは画素を編集用の配列から、ほかの経路は保存形式から
    /// 組み立てるので、その 2 つが同じ規則であることもここで確かめる。
    /// </summary>
    public class WindowOutputParityTests
    {
        private const int W = 48, H = 32;

        private TestAssets _assets;
        private string _path;
        private string _guid;
        private Texture2D _texture;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaWindow _win;
        private string _redId, _blueId;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _path = _assets.WritePng("scene", Scene(), W, H);
            _guid = AssetDatabase.AssetPathToGUID(_path);
            _texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_path);

            // 窓は開いたときに「前回のテクスチャ」を読む。テスト用のテクスチャを指しておき、開発者の値は TearDown で戻す。
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_texture, _win.SourceTexture, "窓がテスト用のテクスチャを開いていない");

            var red = Zone("red", new Color(0.78f, 0.16f, 0.16f), new Color(0.10f, 0.60f, 0.60f), 0.20f);
            var blue = Zone("blue", new Color(0.16f, 0.35f, 0.78f), new Color(0.90f, 0.80f, 0.10f), 0.25f);
            var green = Zone("green (off)", new Color(0.16f, 0.67f, 0.24f), new Color(0.60f, 0.20f, 0.70f), 0.25f);
            green.enabled = false;   // 無効なゾーンはどの経路でも当てない
            _redId = red.id;
            _blueId = blue.id;
            var zones = _win.Session.zones;
            zones.Clear();
            zones.Add(red);
            zones.Add(blue);
            zones.Add(green);

            var masks = _win._maskView;
            Assert.IsTrue(masks.ApplyRegion(Rect(0, 0, 8, 8),
                new MaskPaintView.MaskDestination(null, MaskRegionOp.DontPaintHere), out _), "共通の塗らない");
            Assert.IsTrue(masks.ApplyRegion(Rect(8, 24, 8, 8),
                new MaskPaintView.MaskDestination(_redId, MaskRegionOp.DontPaintHere), out _), "赤のゾーンの塗らない");
            Assert.IsTrue(masks.ApplyRegion(Rect(20, 4, 8, 8),
                new MaskPaintView.MaskDestination(_blueId, MaskRegionOp.PaintHere), out _), "青のゾーンのここも塗る");
        }

        [TearDown]
        public void TearDown()
        {
            IrocaAutomation.CancelRecolorJob();
            if (_win != null)
            {
                Undo.ClearUndo(_win);
                Object.DestroyImmediate(_win);
            }
            _win = null;
            if (_prefBefore == null) EditorPrefs.DeleteKey(_prefKey);
            else EditorPrefs.SetString(_prefKey, _prefBefore);
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) File.Delete(f);
            }
            _assets.Dispose();
        }

        [Test]
        public void WindowFullStage_EqualsSessionRecolorApply()
        {
            var expected = BySessionRecolor(withMasks: true);
            AssertPremises(expected);
            for (int round = 1; round <= 2; round++)
                AssertSamePixels(expected, RunFullStage(), round == 1 ? "確定段(1 回目)" : "確定段(2 回目。選択キャッシュに当たる)");
        }

        [Test]
        public void WindowExport_EqualsAutomationPng()
        {
            var expected = BySessionRecolor(withMasks: true);
            AssertPremises(expected);
            var byWindow = ExportFromWindow("scene_window");
            var byAutomation = ExportByAutomation(_assets.Folder + "/scene_automation.png");
            AssertSamePixels(expected, byWindow, "ウィンドウの書き出し");
            AssertSamePixels(byWindow, byAutomation, "自動化 API の PNG");
        }

        // ───────────── 経路 ─────────────

        // 非破壊ビルドと同じ入口。窓が保存するのと同じく、編集用のマスクを保存形式(maskState)へ書いてから当てる。
        private Color32[] BySessionRecolor(bool withMasks)
        {
            _win._maskView.SyncBuffersToState();
            var state = JsonUtility.FromJson<IrocaSessionState>(JsonUtility.ToJson(_win.Session));
            if (!withMasks) state.maskState = new MaskState();
            Assert.IsTrue(_win.Preview.TryGetTrueSourcePixels(_texture, out var src, out int w, out int h), "原本を読めない");
            Assert.AreEqual((W, H), (w, h));
            var px = src.ToArray();
            Assert.IsTrue(SessionRecolor.Apply(px, w, h, state), "有効なゾーンが無い");
            return px;
        }

        // ウィンドウのプレビューを作り直し、概要段・確定段が終わるまでメインスレッドへの戻りを捌いて、確定段の出力を読む。
        private Color32[] RunFullStage()
        {
            var preview = _win.Preview;
            preview.GeneratePreviewAsync();
            var watch = Stopwatch.StartNew();
            int idle = 0;
            while (idle < 3)
            {
                Assert.Less(watch.Elapsed.TotalSeconds, 60, "プレビューが終わらない");
                PreviewJobMainThread.Drain();
                idle = preview.IsGenerating ? 0 : idle + 1;
                Thread.Sleep(2);
            }
            var output = preview.FullOutput;
            Assert.IsNotNull(output, "確定段の出力が無い");
            Assert.AreEqual(W * H, output.Length);
            return output.ToArray();
        }

        // 「適用して保存」(新しいファイルに保存)。確認の画面は出ない(出力先がまだ無い)。
        private Color32[] ExportFromWindow(string fileName)
        {
            var export = _win._exportView;
            export.saveAsNewFile = true;
            export.newFileName = fileName;
            string output = ExportPipeline.SingleOutputPath(_path, true, fileName);
            Assert.IsFalse(File.Exists(output), "出力先が既にある(テストの前提が崩れている)");
            export.ApplyRecolor();
            var watch = Stopwatch.StartNew();
            while (export.IsExporting)
            {
                Assert.Less(watch.Elapsed.TotalSeconds, 60, "書き出しが終わらない");
                PreviewJobMainThread.Drain();
                Thread.Sleep(2);
            }
            Assert.IsTrue(File.Exists(output), "書き出していない: " + output);
            return DecodePng(output);
        }

        // 変更なしの依頼。土台は開いている窓の編集内容(ゾーン・設定・マスク)。png の仕上げは背景のスレッドで進むので、
        // エディタのコマを挟まずに問い合わせ直せば終わる(AutomationTests と同じ待ち方)。
        private Color32[] ExportByAutomation(string output)
        {
            string json = IrocaAutomation.Recolor("{\"texture\":\"" + _path + "\",\"apply\":\"png\",\"changes\":[],"
                + "\"output\":\"" + output + "\",\"autoTune\":false}");
            var watch = Stopwatch.StartNew();
            while (json.Contains("\"pending\":true"))
            {
                Assert.Less(watch.Elapsed.TotalSeconds, 60, "自動化 API の書き出しが終わらない: " + json);
                Thread.Sleep(5);
                json = IrocaAutomation.RecolorStatus();
            }
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            return DecodePng(r.output);
        }

        // ───────────── 入力と比べ方 ─────────────

        // 3 色の帯(縦に陰影)と、帯の境目の混色の列、赤の帯の中の明るい点。行 0 = 画像の下端。
        private static Color32[] Scene()
        {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float s = 0.6f + 0.4f * y / (H - 1);
                for (int x = 0; x < W; x++)
                {
                    Color32 c = x < 16 ? Shade(200, 40, 40, s) : x < 32 ? Shade(40, 170, 60, s) : Shade(40, 90, 200, s);
                    if (x == 16) c = Mix(Shade(200, 40, 40, s), Shade(40, 170, 60, s));
                    if (x == 32) c = Mix(Shade(40, 170, 60, s), Shade(40, 90, 200, s));
                    if (x >= 4 && x < 7 && y >= 20 && y < 23) c = new Color32(235, 130, 130, 255);
                    px[y * W + x] = c;
                }
            }
            return px;
        }

        private static Color32 Shade(int r, int g, int b, float s)
            => new Color32((byte)(r * s), (byte)(g * s), (byte)(b * s), 255);

        private static Color32 Mix(Color32 a, Color32 b)
            => new Color32((byte)((a.r + b.r) / 2), (byte)((a.g + b.g) / 2), (byte)((a.b + b.b) / 2), 255);

        private static ColorZone Zone(string name, Color sample, Color target, float tolerance)
        {
            var z = new ColorZone
            {
                name = name,
                sampleColor = sample,
                sampleColorSet = true,
                targetColor = target,
                targetColorSet = true,
                tolerance = tolerance,
            };
            z.EnsureId();
            return z;
        }

        // マスクと同じ寸法・下原点の範囲。
        private static bool[] Rect(int x0, int y0, int w, int h)
        {
            var r = new bool[W * H];
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    r[y * W + x] = true;
            return r;
        }

        // 比べる意味があること: 色替えが起きていて、マスクが出力を変えている。
        private void AssertPremises(Color32[] expected)
        {
            var source = Scene();
            int red = 12 * W + 10, blue = 16 * W + 40, commonExcluded = 2 * W + 2, redExcluded = 28 * W + 12;
            Assert.IsFalse(Same(source[red], expected[red]), "赤の帯が色替えされていない(テストの前提が崩れている)");
            Assert.IsFalse(Same(source[blue], expected[blue]), "青の帯が色替えされていない(テストの前提が崩れている)");
            Assert.IsTrue(Same(source[commonExcluded], expected[commonExcluded]), "共通の塗らないの画素が変わった");
            Assert.IsTrue(Same(source[redExcluded], expected[redExcluded]), "赤のゾーンの塗らないの画素が変わった");
            Assert.IsFalse(SameAll(BySessionRecolor(withMasks: false), expected), "マスクが出力を変えていない(テストの前提が崩れている)");
        }

        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        private static bool SameAll(Color32[] a, Color32[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!Same(a[i], b[i])) return false;
            return true;
        }

        private static void AssertSamePixels(Color32[] expected, Color32[] actual, string what)
        {
            Assert.AreEqual(expected.Length, actual.Length, what + ": 寸法が違う");
            int diff = 0, first = -1;
            for (int i = 0; i < expected.Length; i++)
                if (!Same(expected[i], actual[i])) { diff++; if (first < 0) first = i; }
            if (diff > 0)
                Assert.Fail($"{what}: {diff} 画素が違う。最初は (x={first % W}, y={first / W}) で {expected[first]} と {actual[first]}");
        }

        private static Color32[] DecodePng(string path)
        {
            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(path)), "PNG を読めない: " + path);
                Assert.AreEqual((W, H), (tex.width, tex.height), path);
                return tex.GetPixels32();
            }
            finally { Object.DestroyImmediate(tex); }
        }
    }
}
