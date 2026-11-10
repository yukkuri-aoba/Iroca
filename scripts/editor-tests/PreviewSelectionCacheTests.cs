// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// プレビューの選択キャッシュ(<see cref="PreviewPipeline.Selection"/>)を、同じ原本の間は捨てないこと。
    /// 表示の上限(<see cref="IrocaConsts.Preview.MaxSize"/>)より大きいテクスチャは、表示用に縮めた控えを確定段の
    /// apply で確定する。以前は「控えがある」も原本の一致の条件に入っていて、控えが確定する前に次の操作が来ると
    /// (確定段が古くなって apply されない)、操作のたびに選択キャッシュを捨てていた(点検 2026-11-09 選-7)。
    /// </summary>
    public class PreviewSelectionCacheTests {
        // 表示の上限(384)より大きいので、表示用に縮める段を通る。
        private const int W = 512, H = 512;

        private TestAssets _assets;
        private string _guid;
        private Texture2D _texture;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaWindow _win;
        private string _zoneId;

        [SetUp]
        public void SetUp() {
            Assume.That(W, Is.GreaterThan(IrocaConsts.Preview.MaxSize), "テストの前提: 表示用に縮めるテクスチャ");
            _assets = TestAssets.Create();
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    px[y * W + x] = x < W / 2 ? new Color32(200, 40, 40, 255) : new Color32(40, 90, 200, 255);
                }
            }
            string path = _assets.WritePng("halves", px, W, H);
            _guid = AssetDatabase.AssetPathToGUID(path);
            _texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            // 窓は開いたときに「前回のテクスチャ」を読む。テスト用のテクスチャを指しておき、開発者の値は TearDown で戻す。
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_texture, _win.SourceTexture, "窓がテスト用のテクスチャを開いていない");

            var zone = new ColorZone {
                name = "red",
                sampleColor = new Color(200 / 255f, 40 / 255f, 40 / 255f),
                sampleColorSet = true,
                targetColor = new Color(0.10f, 0.60f, 0.60f),
                targetColorSet = true,
                tolerance = 0.25f,
            };
            zone.EnsureId();
            _zoneId = zone.id;
            _win.Session.zones.Clear();
            _win.Session.zones.Add(zone);
        }

        [TearDown]
        public void TearDown() {
            if (_win != null) {
                Undo.ClearUndo(_win);
                Object.DestroyImmediate(_win);
            }
            _win = null;
            if (_prefBefore == null) {
                EditorPrefs.DeleteKey(_prefKey);
            } else {
                EditorPrefs.SetString(_prefKey, _prefBefore);
            }
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                if (!Directory.Exists(dir)) {
                    continue;
                }
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) {
                    File.Delete(f);
                }
            }
            _assets.Dispose();
        }

        // 最初の確定段が選択を保存したが、apply の前に次の操作が来た(縮めた控えはまだ無い)。原本は同じなので、
        // 次の作り直しで選択を捨てない。
        [Test]
        public void NextEditBeforeTheFirstApply_KeepsTheSelection() {
            var preview = _win.Preview;
            preview.SkipProxyOnce();
            preview.GeneratePreviewAsync();
            var selection = preview.Pipeline.Selection;
            Assert.IsNotNull(selection, "選択キャッシュが無い");
            // 確定段の計算が選択を保存するまで待つ(メインスレッドへの戻りは捌かないので、apply はまだ)。
            var watch = Stopwatch.StartNew();
            while (!HasSelection(selection)) {
                Assert.Less(watch.Elapsed.TotalSeconds, 30, "確定段が選択を保存しない");
                Thread.Sleep(2);
            }

            // 目標の色だけを変えた次の操作(選択は変わらない)。
            _win.Session.zones[0].targetColor = new Color(0.90f, 0.80f, 0.10f);
            _win.Session.zones[0].UpdateCacheIfNeeded();
            preview.GeneratePreviewAsync();
            Assert.IsTrue(HasSelection(preview.Pipeline.Selection), "同じ原本なのに、縮めた控えが無いだけで選択キャッシュを捨てた");

            Drain(preview);
            Assert.IsNotNull(preview.FullOutput, "確定段の出力が無い");
        }

        private bool HasSelection(SelectionCache selection)
            => selection.TryReadLatest(_zoneId, W, H, new[] { 0 }, new float[1]);

        // 概要段・確定段が終わるまでメインスレッドへの戻りを捌く(WindowOutputParityTests と同じ待ち方)。
        private static void Drain(PreviewView preview) {
            var watch = Stopwatch.StartNew();
            int idle = 0;
            while (idle < 3) {
                Assert.Less(watch.Elapsed.TotalSeconds, 60, "プレビューが終わらない");
                PreviewJobMainThread.Drain();
                idle = preview.IsGenerating ? 0 : idle + 1;
                Thread.Sleep(2);
            }
        }
    }
}
