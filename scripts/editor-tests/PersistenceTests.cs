// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// セッション・マスク・プリセットの保存と復元。どれもユーザーの手作業（ゾーン設定・手描きマスク）を
    /// 預かるので、「保存したものが戻る」「読めないファイルを空保存で消さない」「一時的に GUID が
    /// 引けないだけのファイルを消さない」を実機（AssetDatabase 込み）で検査する。
    /// </summary>
    public class PersistenceTests
    {
        private TestAssets _assets;
        private string _tex;
        private string _guid;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _tex = _assets.WritePng("tex", TestAssets.Solid(4, 4, new Color32(200, 50, 50, 255)), 4, 4);
            _guid = AssetDatabase.AssetPathToGUID(_tex);
            Assert.IsNotEmpty(_guid);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) File.Delete(f);
            }
            _assets.Dispose();
        }

        private string SessionPath => Path.Combine(SessionFileStore.CacheDir, _guid + ".iroca-session.json");
        private string MaskPath => Path.Combine(MaskFileStore.CacheDir, _guid + ".iroca-mask.json");

        private static IrocaSessionState SampleSession()
        {
            var s = IrocaSessionState.CreateDefault();
            var z = new ColorZone { name = "服", tolerance = 0.3f, sampleColor = new Color(0.8f, 0.2f, 0.2f), targetColor = Color.blue };
            z.EnsureId();
            s.zones.Add(z);
            new RecolorSettings(0.5f, 2, 3, 5, 0.03f, 0.09f, false, 7).CopyTo(s);
            return s;
        }

        // ─── セッション ───

        [Test]
        public void Session_RoundTrip()
        {
            var s = SampleSession();
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, s));
            var loaded = SessionFileStore.LoadSession(_tex, out bool unreadable);
            Assert.IsFalse(unreadable);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(1, loaded.zones.Count);
            Assert.AreEqual("服", loaded.zones[0].name);
            Assert.AreEqual(s.zones[0].id, loaded.zones[0].id);
            Assert.AreEqual(0.3f, loaded.zones[0].tolerance);
            Assert.AreEqual(RecolorSettings.From(s), RecolorSettings.From(loaded), "全体設定が戻ること");
        }

        [Test]
        public void Session_DoesNotDuplicateMask()
        {
            // マスクは MaskFileStore が唯一の正。セッション側に巨大な base64 を二重に書かない。
            var s = SampleSession();
            s.maskState.width = 4; s.maskState.height = 4; s.maskState.commonMaskBase64 = "AAAA";
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, s));
            StringAssert.DoesNotContain("AAAA", File.ReadAllText(SessionPath));
            Assert.AreEqual("AAAA", s.maskState.commonMaskBase64, "保存後に呼び出し側の maskState が戻ること");
        }

        [Test]
        public void Session_EmptySaveDeletesFile()
        {
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, SampleSession()));
            Assert.IsTrue(File.Exists(SessionPath));
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, IrocaSessionState.CreateDefault()));
            Assert.IsFalse(File.Exists(SessionPath));
        }

        [Test]
        public void Session_UnreadableFileSurvivesEmptySave()
        {
            Directory.CreateDirectory(SessionFileStore.CacheDir);
            File.WriteAllText(SessionPath, "{ this is not json");
            LogAssertIgnore();
            var loaded = SessionFileStore.LoadSession(_tex, out bool unreadable);
            Assert.IsNull(loaded);
            Assert.IsTrue(unreadable, "壊れたファイルは「無い」ではなく「読めない」と報告すること");

            // 読めなかったセッションの空保存は、既存ファイルを消さない。
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, IrocaSessionState.CreateDefault(), lastLoadFailed: true));
            Assert.AreEqual("{ this is not json", File.ReadAllText(SessionPath));

            // 中身のある保存は上書きするが、先に .bak へ退避する。
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, SampleSession(), lastLoadFailed: true));
            Assert.AreEqual("{ this is not json", File.ReadAllText(SessionPath + ".bak"));
            Assert.IsNotNull(SessionFileStore.LoadSession(_tex, out _));
        }

        [Test]
        public void Session_OrphanLifecycle()
        {
            Directory.CreateDirectory(SessionFileStore.CacheDir);
            string unknown = Guid.NewGuid().ToString("N");
            string unknownPath = Path.Combine(SessionFileStore.CacheDir, unknown + ".iroca-session.json");
            string expiredOrphan = Path.Combine(SessionFileStore.CacheDir, Guid.NewGuid().ToString("N") + ".iroca-session.json.orphan");
            string ownOrphan = SessionPath + ".orphan";
            try
            {
                File.WriteAllText(unknownPath, "{}");
                File.WriteAllText(expiredOrphan, "{}");
                File.SetLastWriteTimeUtc(expiredOrphan, DateTime.UtcNow.AddDays(-31));
                File.WriteAllText(ownOrphan, "{\"zones\":[]}");

                SessionFileStore.CleanupOrphans();

                Assert.IsFalse(File.Exists(unknownPath), "GUID が引けない現用ファイルは退避される");
                Assert.IsTrue(File.Exists(unknownPath + ".orphan"), "即削除ではなく .orphan へ退避");
                Assert.IsFalse(File.Exists(expiredOrphan), "猶予を過ぎた退避ファイルは削除");
                Assert.IsTrue(File.Exists(SessionPath), "GUID が再び引けた退避ファイルは元名へ復元");
                Assert.IsFalse(File.Exists(ownOrphan));

                // 退避直後（猶予内）はもう一度掃除しても消えない。
                SessionFileStore.CleanupOrphans();
                Assert.IsTrue(File.Exists(unknownPath + ".orphan"));
            }
            finally
            {
                foreach (var f in new[] { unknownPath, unknownPath + ".orphan", expiredOrphan })
                    if (File.Exists(f)) File.Delete(f);
            }
        }

        // ─── マスク ───

        private static MaskState SampleMask()
        {
            var m = new MaskState { width = 4, height = 4, commonMaskBase64 = "AQID" };
            m.zones.Add(new MaskZoneEntry { zoneId = "z1", maskBase64 = "BAUG" });
            m.zoneIncludes.Add(new MaskZoneEntry { zoneId = "z1", maskBase64 = "BwgJ" });
            return m;
        }

        [Test]
        public void Mask_RoundTripWithSchemaVersion()
        {
            Assert.IsTrue(MaskFileStore.SaveMask(_tex, SampleMask()));
            var loaded = MaskFileStore.LoadMask(_tex, out bool unreadable);
            Assert.IsFalse(unreadable);
            Assert.AreEqual(MaskState.CurrentSchemaVersion, loaded.schemaVersion);
            Assert.AreEqual("AQID", loaded.commonMaskBase64);
            Assert.AreEqual("BAUG", loaded.zones[0].maskBase64);
            Assert.AreEqual("BwgJ", loaded.zoneIncludes[0].maskBase64, "含めるマスク(v2)も戻ること");
        }

        [Test]
        public void Mask_EmptyJsonIsUnreadableNotMissing()
        {
            // JsonUtility は空文字や "null" を例外なしで null にする。「マスク無し」と誤認すると
            // 読込失敗時の保護が効かず、空マスクで上書き＝手描きマスクの恒久喪失になる。
            Directory.CreateDirectory(MaskFileStore.CacheDir);
            File.WriteAllText(MaskPath, "null");
            LogAssertIgnore();
            Assert.IsNull(MaskFileStore.LoadMask(_tex, out bool unreadable));
            Assert.IsTrue(unreadable);
            Assert.IsTrue(MaskFileStore.SaveMask(_tex, new MaskState(), lastLoadFailed: true));
            Assert.IsTrue(File.Exists(MaskPath), "読めなかったマスクを空保存で消さない");
        }

        // ─── プリセット ───

        [Test]
        public void Preset_RoundTripStampsSchema()
        {
            string dir = TestAssets.Abs(_assets.Folder);
            string path = PresetStore.PresetFilePath(dir, "テスト");
            var data = new IrocaPresetData { name = "テスト" };
            new RecolorSettings(0.25f, 1, 2, 3, 0.04f, 0.1f, false, 6).CopyTo(data);
            Assert.IsTrue(PresetStore.SaveToPath(path, data));
            var loaded = PresetStore.Load(path);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(IrocaPresetData.CurrentSchemaVersion, loaded.schemaVersion);
            Assert.AreEqual(RecolorSettings.From(data), RecolorSettings.From(loaded));
        }

        [Test]
        public void Preset_OutOfRangeRadiusIsClampedOnLoad()
        {
            // プリセットは手で書かれ得る。UI と MCP のどちらから読んでもスライダー範囲へ丸まること。
            var data = new IrocaPresetData { decontaminationRadius = 50 };
            Assert.AreEqual(RecolorSettings.MaxDecontaminationRadius, RecolorSettings.From(data).decontaminationRadius);
            data.decontaminationRadius = 0;
            Assert.AreEqual(RecolorSettings.MinDecontaminationRadius, RecolorSettings.From(data).decontaminationRadius);
        }

        [Test]
        public void Preset_FileNameIsSanitized()
        {
            string dir = TestAssets.Abs(_assets.Folder);
            Assert.AreEqual(Path.Combine(dir, "_CON.json"), PresetStore.PresetFilePath(dir, "CON"));
            Assert.AreEqual(Path.Combine(dir, ".._evil.json"), PresetStore.PresetFilePath(dir, "../evil"));
        }

        [Test]
        public void Preset_ListMissingFolderIsEmpty()
            => Assert.IsEmpty(PresetStore.ListJson(Path.Combine(TestAssets.Abs(_assets.Folder), "none")));

        [Test]
        public void Preset_BrokenFileLoadsAsNull()
        {
            string path = Path.Combine(TestAssets.Abs(_assets.Folder), "broken.json");
            File.WriteAllText(path, "{ broken");
            LogAssertIgnore();
            Assert.IsNull(PresetStore.Load(path));
        }

        // 失敗経路は Debug.LogWarning を出す（Unity Test Framework は Warning では落とさないが、
        // 将来 Error に変えたときにテストの意図が「ログを出すこと」ではないと分かるよう明示する）。
        private static void LogAssertIgnore() => UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
    }
}
