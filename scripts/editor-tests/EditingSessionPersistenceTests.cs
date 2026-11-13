// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 編集中の文書の保存と復元(<see cref="EditingSession"/> の Persistence の部分)。ウィンドウ無しで、ファイルへ書いて読み戻せること、
    /// 読めなかったファイルを空の保存で消さないこと(読めなかった印)、マスクを読み込む前の保存がマスクのファイルに触らないこと。
    /// ウィンドウを通した流れ(Undo・再読み込み・レシピ)は PersistenceTests・RecipeSyncTests・WindowUndoTests が見る。
    /// </summary>
    public class EditingSessionPersistenceTests {
        private const int W = 4, H = 2;

        private TestAssets _assets;
        private Texture2D _tex;
        private string _guid;
        private readonly System.Collections.Generic.List<EditingSession> _sessions = new System.Collections.Generic.List<EditingSession>();

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            string path = _assets.WritePng("tex", TestAssets.Solid(4, 4, new Color32(200, 50, 50, 255)), 4, 4);
            _tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            _guid = AssetDatabase.AssetPathToGUID(path);
        }

        [TearDown]
        public void TearDown() {
            foreach (var e in _sessions) {
                e.Masks.Clear();
                e.MaskHistory.Clear();
                Object.DestroyImmediate(e.Document);
            }
            _sessions.Clear();
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

        private string SessionPath => Path.Combine(SessionFileStore.CacheDir, _guid + ".iroca-session.json");
        private string MaskPath => Path.Combine(MaskFileStore.CacheDir, _guid + ".iroca-mask.json");

        // 同じテクスチャを開いた、新しい編集(ウィンドウを開き直したのと同じ)。
        private EditingSession Open() {
            var e = new EditingSession(IrocaEditingDocument.Create(IrocaSessionState.CreateDefault(), _tex, null));
            _sessions.Add(e);
            return e;
        }

        private static bool[] Pattern() => new[] { true, false, false, true, false, true, false, false };

        // ゾーン 1 つと、そのゾーンの除外マスクを持つ編集を保存する。ゾーンの id を返す。
        private string SaveSample() {
            var e = Open();
            var zone = new ColorZone { name = "服", tolerance = 0.3f, sampleColor = new Color(0.8f, 0.2f, 0.2f), sampleColorSet = true };
            zone.EnsureId();
            e.State.zones.Add(zone);
            e.Masks.Reset(W, H);
            e.Masks.SetLayer(false, zone.id, NativeBitMask.FromBools(Pattern(), W, H));
            Assert.IsTrue(e.Save(saveMasks: true, out bool recipeChangedOutside));
            Assert.IsFalse(recipeChangedOutside, "レシピは結び付いていない");
            Assert.IsTrue(File.Exists(SessionPath), "セッションのファイルが無い");
            Assert.IsTrue(File.Exists(MaskPath), "マスクのファイルが無い");
            return zone.id;
        }

        [Test]
        public void Save_ThenLoad_BringsBackZonesAndMasks_WithoutAWindow() {
            string id = SaveSample();

            var e = Open();
            e.LoadSessionFromFile();
            Assert.IsTrue(e.RestoreMasksFromFile(), "ファイルから層を読めていない");
            Assert.IsFalse(e.SessionLoadFailed);
            Assert.IsFalse(e.MaskLoadFailed);
            Assert.AreEqual(1, e.State.zones.Count);
            Assert.AreEqual("服", e.State.zones[0].name);
            Assert.AreEqual(id, e.State.zones[0].id);
            var layer = e.Masks.Layer(false, id);
            Assert.IsNotNull(layer);
            var expected = Pattern();
            for (int i = 0; i < expected.Length; i++) {
                Assert.AreEqual(expected[i], layer.Get(i), $"画素 {i}");
            }
        }

        // 読めなかったファイルは、空の編集を保存しても消さない(一時的に読めなかっただけで、中身を失わないように)。
        [Test]
        public void UnreadableFiles_AreNotDeletedByAnEmptySave() {
            SaveSample();
            File.WriteAllText(SessionPath, "{ broken");
            File.WriteAllText(MaskPath, "{ broken");

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try {
                var e = Open();
                e.LoadSessionFromFile();
                Assert.IsTrue(e.SessionLoadFailed, "テストの前提: セッションのファイルを読めない");
                Assert.IsFalse(e.RestoreMasksFromFile());
                Assert.IsTrue(e.MaskLoadFailed, "テストの前提: マスクのファイルを読めない");
                Assert.AreEqual(0, e.State.zones.Count, "読めなかったので既定(空)で開く");

                e.Save(saveMasks: true, out _);
                Assert.IsTrue(File.Exists(SessionPath), "読めなかったセッションのファイルを、空の保存で消した");
                Assert.IsTrue(File.Exists(MaskPath), "読めなかったマスクのファイルを、空の保存で消した");
                Assert.IsTrue(e.SessionLoadFailed, "空の保存で印を下ろした(次の空の保存が消してしまう)");
                Assert.IsTrue(e.MaskLoadFailed);

                // 確かめ直しても、読めないうちは印を立てたまま。
                e.RecheckSessionFile(adoptMissed: true);
                e.RecheckMaskFile();
                Assert.IsTrue(e.SessionLoadFailed);
                Assert.IsTrue(e.MaskLoadFailed);
            } finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
        }

        // マスクをまだ読み込んでいない編集の保存は、マスクのファイルに触らない(空のマスクを書くとファイルを消す)。
        [Test]
        public void Save_BeforeTheMasksWereLoaded_LeavesTheMaskFile() {
            SaveSample();
            string before = File.ReadAllText(MaskPath);

            var e = Open();
            e.LoadSessionFromFile();
            Assert.IsTrue(e.Save(saveMasks: false, out _));
            Assert.IsTrue(File.Exists(MaskPath), "マスクを読み込む前の保存が、マスクのファイルを消した");
            Assert.AreEqual(before, File.ReadAllText(MaskPath));
        }

        // 編集中が空なのにファイルに中身がある(読めないまま空で開いた後に、読めるようになった)とき: 確かめ直しで中身を読む。
        [Test]
        public void RecheckSessionFile_AdoptsAFileThatBecameReadable() {
            SaveSample();
            var e = Open();
            Assert.AreEqual(0, e.State.zones.Count);
            e.RecheckSessionFile(adoptMissed: false);
            Assert.IsTrue(e.SessionLoadFailed, "編集中が空でファイルに中身があるのに、印を立てない(空の保存がファイルを消す)");
            Assert.AreEqual(0, e.State.zones.Count);
            e.RecheckSessionFile(adoptMissed: true);
            Assert.IsFalse(e.SessionLoadFailed);
            Assert.AreEqual(1, e.State.zones.Count, "ファイルの中身を読んでいない");
        }
    }
}
