// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// セッション・マスク・プリセットの保存と復元。どれもユーザーの手作業（ゾーン設定・手描きマスク）を
    /// 預かるので、「保存したものが戻る」「読めないファイルを空保存で消さない」「一時的に GUID が
    /// 引けないだけのファイルを消さない」を実機（AssetDatabase 込み）で検査する。
    /// </summary>
    public class PersistenceTests {
        // 1 つのファイルに複数のテクスチャがあっても、編集内容のファイルは分ける。
        [Test]
        public void TexturesInOneAsset_DoNotShareTheirEdits() {
            using (var assets = TestAssets.Create()) {
                string path = assets.Folder + "/two-textures.asset";
                var main = new Texture2D(2, 2);
                var sub = new Texture2D(2, 2) { name = "sub" };
                AssetDatabase.CreateAsset(main, path);
                AssetDatabase.AddObjectToAsset(sub, path);
                AssetDatabase.SaveAssets();
                string guid = AssetDatabase.AssetPathToGUID(path);
                try {
                    var a = new IrocaSessionState();
                    a.zones.Add(new ColorZone { name = "本体" });
                    var b = new IrocaSessionState();
                    b.zones.Add(new ColorZone { name = "サブ" });
                    Assert.IsTrue(SessionPersistence.SaveToFile(main, a, false, out _));
                    Assert.IsTrue(SessionPersistence.SaveToFile(sub, b, false, out _));

                    Assert.AreEqual("本体", SessionPersistence.LoadFromFile(main, out _).zones[0].name);
                    Assert.AreEqual("サブ", SessionPersistence.LoadFromFile(sub, out _).zones[0].name);

                    // 消したら両方退避する。
                    SessionFileStore.RetireSessionByGuid(guid);
                    Assert.AreEqual(2, Directory.GetFiles(SessionFileStore.CacheDir, guid + "*.orphan").Length);
                } finally {
                    foreach (var f in Directory.GetFiles(SessionFileStore.CacheDir, guid + "*")) {
                        File.Delete(f);
                    }
                }
            }
        }

        private TestAssets _assets;
        private string _tex;
        private string _guid;

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            _tex = _assets.WritePng("tex", TestAssets.Solid(4, 4, new Color32(200, 50, 50, 255)), 4, 4);
            _guid = AssetDatabase.AssetPathToGUID(_tex);
            Assert.IsNotEmpty(_guid);
        }

        [TearDown]
        public void TearDown() {
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

        private static IrocaSessionState SampleSession() {
            var s = IrocaSessionState.CreateDefault();
            var z = new ColorZone { name = "服", tolerance = 0.3f, sampleColor = new Color(0.8f, 0.2f, 0.2f), targetColor = Color.blue };
            z.EnsureId();
            s.zones.Add(z);
            new RecolorSettings(0.5f, 2, 3, 5, 0.03f, 0.09f, false, 7).CopyTo(s);
            return s;
        }

        // ─── レシピの仕上がりの版の印(RecipeOutputVersion) ───

        // 作ったときと中身を変えて保存したときに今の版になり、同じ中身の保存(開いただけの自動保存など)では変わらない。
        // 「確かめた」(登録・インスペクタ・アップロード)は中身が同じでも今の版にし、Undo で戻る。
        [Test]
        public void Recipe_OutputVersion_IsCurrentWhenCreatedOrChanged_NotWhenSavedUnchanged() {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_tex);
            var state = SampleSession();
            var recipe = RecipeStore.Create(texture, state, _assets.Folder);
            Assert.AreEqual(RecipeOutputVersion.Current, recipe.outputVersion, "作ったレシピが今の版になっていない");

            recipe.outputVersion = 0;   // 前の版のいろかで作ったもの
            Assert.IsFalse(RecipeStore.Save(recipe, state));
            Assert.AreEqual(0, recipe.outputVersion, "同じ中身の保存で印を上げた(開いただけで確かめたことになる)");
            state.zones[0].tolerance = 0.4f;
            Assert.IsTrue(RecipeStore.Save(recipe, state));
            Assert.AreEqual(RecipeOutputVersion.Current, recipe.outputVersion, "中身を変えて保存したのに印が上がらない");

            recipe.outputVersion = 0;
            Undo.IncrementCurrentGroup();
            try {
                Assert.IsTrue(RecipeStore.MarkOutputChecked(recipe, "Iroca: Checked"));
                Assert.AreEqual(RecipeOutputVersion.Current, recipe.outputVersion);
                Assert.IsFalse(RecipeStore.MarkOutputChecked(recipe), "今の版なのに書き直した");
                Undo.PerformUndo();
                Assert.AreEqual(0, recipe.outputVersion, "「確かめた」が Undo で戻らない");
            } finally {
                Undo.ClearUndo(recipe);
            }
        }

        // ─── セッション ───

        [Test]
        public void Session_RoundTrip() {
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
        public void Session_DoesNotDuplicateMask() {
            // マスクは MaskFileStore が唯一の正。セッション側に巨大な base64 を二重に書かない。
            var s = SampleSession();
            s.maskState.width = 4; s.maskState.height = 4; s.maskState.commonMaskBase64 = "AAAA";
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, s));
            StringAssert.DoesNotContain("AAAA", File.ReadAllText(SessionPath));
            Assert.AreEqual("AAAA", s.maskState.commonMaskBase64, "保存後に呼び出し側の maskState が戻ること");
        }

        [Test]
        public void Session_EmptySaveDeletesFile() {
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, SampleSession()));
            Assert.IsTrue(File.Exists(SessionPath));
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, IrocaSessionState.CreateDefault()));
            Assert.IsFalse(File.Exists(SessionPath));
        }

        [Test]
        public void Session_UnreadableFileSurvivesEmptySave() {
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
        public void Session_OrphanLifecycle() {
            Directory.CreateDirectory(SessionFileStore.CacheDir);
            string unknown = Guid.NewGuid().ToString("N");
            string unknownPath = Path.Combine(SessionFileStore.CacheDir, unknown + ".iroca-session.json");
            string expiredOrphan = Path.Combine(SessionFileStore.CacheDir, Guid.NewGuid().ToString("N") + ".iroca-session.json.orphan");
            string ownOrphan = SessionPath + ".orphan";
            try {
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
            } finally {
                foreach (var f in new[] { unknownPath, unknownPath + ".orphan", expiredOrphan }) {
                    if (File.Exists(f)) {
                        File.Delete(f);
                    }
                }
            }
        }

        // ─── マスク ───

        private static MaskState SampleMask() {
            var m = new MaskState { width = 4, height = 4, commonMaskBase64 = "AQID" };
            m.zones.Add(new MaskZoneEntry { zoneId = "z1", maskBase64 = "BAUG" });
            m.zoneIncludes.Add(new MaskZoneEntry { zoneId = "z1", maskBase64 = "BwgJ" });
            return m;
        }

        [Test]
        public void Mask_RoundTripWithSchemaVersion() {
            Assert.IsTrue(MaskFileStore.SaveMask(_tex, SampleMask()));
            var loaded = MaskFileStore.LoadMask(_tex, out bool unreadable);
            Assert.IsFalse(unreadable);
            Assert.AreEqual(MaskState.CurrentSchemaVersion, loaded.schemaVersion);
            Assert.AreEqual("AQID", loaded.commonMaskBase64);
            Assert.AreEqual("BAUG", loaded.zones[0].maskBase64);
            Assert.AreEqual("BwgJ", loaded.zoneIncludes[0].maskBase64, "含めるマスク(v2)も戻ること");
        }

        [Test]
        public void Mask_EmptyJsonIsUnreadableNotMissing() {
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

        [Test]
        public void Mask_OrphanLifecycle() {
            // Session_OrphanLifecycle と同じ規則。処理は GuidFileCache を共有するが、フォルダと拡張子の配線はストアごとなので別に固定する。
            Directory.CreateDirectory(MaskFileStore.CacheDir);
            string unknown = Guid.NewGuid().ToString("N");
            string unknownPath = Path.Combine(MaskFileStore.CacheDir, unknown + ".iroca-mask.json");
            string expiredOrphan = Path.Combine(MaskFileStore.CacheDir, Guid.NewGuid().ToString("N") + ".iroca-mask.json.orphan");
            string ownOrphan = MaskPath + ".orphan";
            try {
                File.WriteAllText(unknownPath, "{}");
                File.WriteAllText(expiredOrphan, "{}");
                File.SetLastWriteTimeUtc(expiredOrphan, DateTime.UtcNow.AddDays(-31));
                File.WriteAllText(ownOrphan, "{\"zones\":[]}");

                MaskFileStore.CleanupOrphans();

                Assert.IsFalse(File.Exists(unknownPath), "GUID が引けない現用ファイルは退避される");
                Assert.IsTrue(File.Exists(unknownPath + ".orphan"), "即削除ではなく .orphan へ退避");
                Assert.IsFalse(File.Exists(expiredOrphan), "猶予を過ぎた退避ファイルは削除");
                Assert.IsTrue(File.Exists(MaskPath), "GUID が再び引けた退避ファイルは元名へ復元");
                Assert.IsFalse(File.Exists(ownOrphan));

                // 退避直後（猶予内）はもう一度掃除しても消えない。
                MaskFileStore.CleanupOrphans();
                Assert.IsTrue(File.Exists(unknownPath + ".orphan"));
            } finally {
                foreach (var f in new[] { unknownPath, unknownPath + ".orphan", expiredOrphan }) {
                    if (File.Exists(f)) {
                        File.Delete(f);
                    }
                }
            }
        }

        // 窓を開いたときの掃除の後で GUID が引けるようになったテクスチャでも、退避されたファイルを読む
        // (読まないと空で始まり、次の掃除で退避が消えて以前の編集を失っていた)。
        [Test]
        public void OrphanedFiles_AreRestoredWhenTheTextureIsLoaded() {
            string tex = _assets.WritePng("orphan", TestAssets.Solid(4, 4, new Color32(1, 2, 3, 255)), 4, 4);
            string guid = AssetDatabase.AssetPathToGUID(tex);
            var state = IrocaSessionState.CreateDefault();
            state.zones.Add(new ColorZone { name = "退避された編集" });
            Assert.IsTrue(SessionFileStore.SaveSession(tex, state));
            var masks = new MaskState { width = 4, height = 4, commonMaskBase64 = MaskRle.Encode(new bool[16], 4, 4) };
            Assert.IsTrue(MaskFileStore.SaveMask(tex, masks));
            try {
                foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                    foreach (var f in Directory.GetFiles(dir, guid + "*")) {
                        File.Move(f, f + ".orphan");
                    }
                }

                var loaded = SessionFileStore.LoadSession(tex, out bool unreadable);
                Assert.IsFalse(unreadable);
                Assert.IsNotNull(loaded, "退避されたセッションを読んでいない");
                Assert.AreEqual("退避された編集", loaded.zones[0].name);
                Assert.IsNotNull(MaskFileStore.LoadMask(tex, out _), "退避されたマスクを読んでいない");
            } finally {
                foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                    foreach (var f in Directory.GetFiles(dir, guid + "*")) {
                        File.Delete(f);
                    }
                }
            }
        }

        // テクスチャの入ったフォルダごと消しても、中のテクスチャの編集内容(セッション・マスク)を退避する(窓を開かない間、
        // GUID の引けないファイルとして残り続けない。同じ GUID で戻せば読み込みのときに復元する)。
        [Test]
        public void DeletingTheFolder_RetiresTheEditsOfTexturesInside() {
            AssetDatabase.CreateFolder(_assets.Folder, "outfit");
            AssetDatabase.CreateFolder(_assets.Folder + "/outfit", "tex");
            string tex = _assets.WritePng("outfit/tex/inner", TestAssets.Solid(4, 4, new Color32(1, 2, 3, 255)), 4, 4);
            string guid = AssetDatabase.AssetPathToGUID(tex);
            var state = IrocaSessionState.CreateDefault();
            state.zones.Add(new ColorZone { name = "フォルダの中" });
            Assert.IsTrue(SessionFileStore.SaveSession(tex, state));
            var masks = new MaskState { width = 4, height = 4, commonMaskBase64 = MaskRle.Encode(new bool[16], 4, 4) };
            Assert.IsTrue(MaskFileStore.SaveMask(tex, masks));
            string session = Path.Combine(SessionFileStore.CacheDir, guid + ".iroca-session.json");
            string mask = Path.Combine(MaskFileStore.CacheDir, guid + ".iroca-mask.json");
            try {
                Assert.IsTrue(File.Exists(session) && File.Exists(mask), "テストの前提: 編集内容のファイルがある");
                Assert.IsTrue(AssetDatabase.DeleteAsset(_assets.Folder + "/outfit"));
                Assert.IsFalse(File.Exists(session), "セッションが残った");
                Assert.IsFalse(File.Exists(mask), "マスクが残った");
                Assert.IsTrue(File.Exists(session + ".orphan"), "セッションを退避していない");
                Assert.IsTrue(File.Exists(mask + ".orphan"), "マスクを退避していない");
            } finally {
                foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                    foreach (var f in Directory.GetFiles(dir, guid + "*")) {
                        File.Delete(f);
                    }
                }
            }
        }

        // ─── プリセット ───

        [Test]
        public void Preset_RoundTripStampsSchema() {
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
        public void Preset_OutOfRangeRadiusIsClampedOnLoad() {
            // プリセットは手で書かれ得る。UI と MCP のどちらから読んでもスライダー範囲へ丸まること。
            var data = new IrocaPresetData { decontaminationRadius = 50 };
            Assert.AreEqual(RecolorSettings.MaxDecontaminationRadius, RecolorSettings.From(data).decontaminationRadius);
            data.decontaminationRadius = 0;
            Assert.AreEqual(RecolorSettings.MinDecontaminationRadius, RecolorSettings.From(data).decontaminationRadius);
        }

        [Test]
        public void Preset_FileNameIsSanitized() {
            string dir = TestAssets.Abs(_assets.Folder);
            Assert.AreEqual(Path.Combine(dir, "_CON.json"), PresetStore.PresetFilePath(dir, "CON"));
            Assert.AreEqual(Path.Combine(dir, ".._evil.json"), PresetStore.PresetFilePath(dir, "../evil"));
        }

        [Test]
        public void Preset_ListMissingFolderIsEmpty()
            => Assert.IsEmpty(PresetStore.ListJson(Path.Combine(TestAssets.Abs(_assets.Folder), "none")));

        [Test]
        public void Preset_BrokenFileLoadsAsNull() {
            string path = Path.Combine(TestAssets.Abs(_assets.Folder), "broken.json");
            File.WriteAllText(path, "{ broken");
            LogAssertIgnore();
            Assert.IsNull(PresetStore.Load(path));
        }

        // プリセットでない JSON を読むと、JsonUtility は既定値のまま返すので、全ゾーンが空で置き換わっていた。
        [Test]
        public void Preset_OtherJsonLoadsAsNull_ButAPresetWithoutZonesLoads() {
            string path = Path.Combine(TestAssets.Abs(_assets.Folder), "other.json");
            File.WriteAllText(path, "{\"version\":1,\"dependencies\":{}}");
            LogAssertIgnore();
            Assert.IsNull(PresetStore.Load(path));

            File.WriteAllText(path, "{\"name\":\"empty\",\"zones\":[]}");
            var empty = PresetStore.Load(path);
            Assert.IsNotNull(empty, "ゾーンが 0 個のプリセットは読める");
            Assert.AreEqual(0, empty.zones.Count);
        }

        // マスク画面に層を置く(寸法 w×h。各層は (ゾーン id, 含めるか, 画素))。使い終えたら Workspace.Clear() で返す。
        private static MaskPaintView MaskViewWith(int w, int h, params (string zoneId, bool include, bool[] mask)[] layers) {
            var view = new MaskPaintView();
            view.Workspace.Reset(w, h);
            foreach (var (zoneId, include, mask) in layers) {
                view.Workspace.SetLayer(include, zoneId, NativeBitMask.FromBools(mask, w, h));
            }
            return view;
        }

        [Test]
        public void Preset_MaskViewWritesExcludeAndIncludeEntries() {
            // マスク画面からプリセットへの書き出し。除外（zoneMasks）と含める（zoneIncludeMasks）を
            // 取り違えないこと、全 false のマスクは書かないこと。_host は使わない経路なのでホスト無しで呼べる。
            var exclude = new bool[8]; exclude[1] = true;
            var include = new bool[8]; include[6] = true; include[7] = true;
            var view = MaskViewWith(4, 2, ("z1", false, exclude), ("z2", false, new bool[8]), ("z3", true, include));

            var data = new IrocaPresetData();
            view.WriteToPreset(data);
            view.Workspace.Clear();

            Assert.AreEqual("", data.commonMaskBase64, "共通マスクが無ければ書かない");
            Assert.AreEqual(1, data.zoneMasks.Count, "全 false の z2 は書かない");
            Assert.AreEqual("z1", data.zoneMasks[0].zoneId);
            Assert.AreEqual(MaskPaintView.EncodeMask(exclude, 4, 2), data.zoneMasks[0].maskBase64);
            Assert.AreEqual(1, data.zoneIncludeMasks.Count);
            Assert.AreEqual("z3", data.zoneIncludeMasks[0].zoneId);
            Assert.AreEqual(MaskPaintView.EncodeMask(include, 4, 2), data.zoneIncludeMasks[0].maskBase64);
            Assert.AreEqual(4, data.maskWidth);
            Assert.AreEqual(2, data.maskHeight);

            // 書いた文字列が元のマスクと寸法に戻ること。
            CollectionAssert.AreEqual(exclude, MaskPaintView.DecodeMask(data.zoneMasks[0].maskBase64, out int w, out int h));
            Assert.AreEqual(4, w);
            Assert.AreEqual(2, h);
            CollectionAssert.AreEqual(include, MaskPaintView.DecodeMask(data.zoneIncludeMasks[0].maskBase64, out w, out h));
            Assert.AreEqual(4, w);
            Assert.AreEqual(2, h);
        }

        [Test]
        public void Preset_MaskViewWritesSizeOnlyWithSomeMask() {
            // 含めるマスクだけでも寸法は載る。何も書かなければ寸法も書かない（0 のまま）。
            var include = new bool[8]; include[0] = true;
            var includeOnly = MaskViewWith(4, 2, ("z1", true, include));
            var data = new IrocaPresetData();
            includeOnly.WriteToPreset(data);
            includeOnly.Workspace.Clear();
            Assert.AreEqual(0, data.zoneMasks.Count);
            Assert.AreEqual(1, data.zoneIncludeMasks.Count);
            Assert.AreEqual(4, data.maskWidth);
            Assert.AreEqual(2, data.maskHeight);

            var allFalse = MaskViewWith(4, 2, ("z1", false, new bool[8]), ("z1", true, new bool[8]));
            var none = new IrocaPresetData();
            allFalse.WriteToPreset(none);
            allFalse.Workspace.Clear();
            Assert.AreEqual("", none.commonMaskBase64);
            Assert.AreEqual(0, none.zoneMasks.Count);
            Assert.AreEqual(0, none.zoneIncludeMasks.Count);
            Assert.AreEqual(0, none.maskWidth);
            Assert.AreEqual(0, none.maskHeight);
        }

        // 失敗経路は Debug.LogWarning を出す（Unity Test Framework は Warning では落とさないが、
        // 将来 Error に変えたときにテストの意図が「ログを出すこと」ではないと分かるよう明示する）。
        private static void LogAssertIgnore() => UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
    }
}
