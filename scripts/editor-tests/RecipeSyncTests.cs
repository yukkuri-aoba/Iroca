// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// いろかの窓が、外(git の pull・バックアップからの復元など)で書き換わったレシピを古い内容で上書きしないこと。
    /// 外からの変更は、Unity が .asset を取り込み直してレシピの sessionJson が新しい文字列になるのと同じ形
    /// (RecipeStore を通さない代入)で再現する。窓自身の保存と、Undo でレシピが前の中身へ戻ったときは
    /// 外からの変更と取り違えない(編集が消えない)こと。
    /// </summary>
    public class RecipeSyncTests {
        private TestAssets _assets;
        private string _tex;
        private string _guid;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaRecipe _recipe;
        private IrocaWindow _win;

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            _tex = _assets.WritePng("tex", TestAssets.Solid(4, 4, new Color32(200, 50, 50, 255)), 4, 4);
            _guid = AssetDatabase.AssetPathToGUID(_tex);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_tex);
            _recipe = RecipeStore.Create(texture, State("登録した内容"), _assets.Folder);

            // 窓は開いたときに「前回のテクスチャ」を読む。テスト用のテクスチャを指しておき、開発者の値は TearDown で戻す。
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);

            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_recipe, _win.BoundRecipe, "テクスチャのレシピが 1 つなのに結び付いていない");
            Assert.AreEqual("登録した内容", ZoneName());
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

        // 再読み込みのときにマスクのファイルが読めなくても(ウイルス対策ソフトのロックなど)、レシピの同じマスクを使う。
        // 空のまま進むと、次の保存でレシピのマスクまで空になり、開き直すとファイルも消えて戻らなかった。
        [Test]
        public void MaskFileUnreadable_UsesTheRecipeMasks_AndKeepsThemInTheRecipe() {
            _win.Editing.Masks.Reset(4, 4);
            _win.Editing.Masks.WritableCommon().Set(3, true);
            Save();
            StringAssert.StartsWith("R:", RecipeStore.Load(_recipe).maskState.commonMaskBase64, "テストの前提: レシピにマスクがある");

            string file = Directory.GetFiles(MaskFileStore.CacheDir, _guid + "*")[0];
            File.WriteAllText(file, "{ broken");
            LogAssert.ignoreFailingMessages = true;
            try {
                _win._maskView.RestoreFromSession();
                Assert.IsNotNull(_win.Editing.Masks.Common, "レシピのマスクを使っていない");
                Assert.IsTrue(_win.Editing.Masks.Common.Get(3));
                Save();
            } finally { LogAssert.ignoreFailingMessages = false; }
            StringAssert.StartsWith("R:", RecipeStore.Load(_recipe).maskState.commonMaskBase64, "保存でレシピのマスクが消えた");
            Assert.AreEqual("{ broken", File.ReadAllText(file + ".bak"), "読めなかったファイルを退避していない");
        }

        // マスクのファイルに読めない項目(壊れた文字列・寸法違い)があっても、読めた分で開く。読めなかった項目は黙って捨てず、
        // 次の保存の前に元のファイルを .bak に残す(捨てたまま保存すると、ファイルからも消える)。
        [Test]
        public void MaskFileWithUnreadableEntry_IsBackedUpBeforeSaving() {
            _win.Editing.Masks.Reset(4, 4);
            _win.Editing.Masks.WritableCommon().Set(3, true);
            Save();
            string file = Directory.GetFiles(MaskFileStore.CacheDir, _guid + "*")[0];
            var ms = JsonUtility.FromJson<MaskState>(File.ReadAllText(file));
            ms.zones.Add(new MaskZoneEntry { zoneId = "broken", maskBase64 = "R:###" });
            string withBroken = JsonUtility.ToJson(ms);
            File.WriteAllText(file, withBroken);

            LogAssert.ignoreFailingMessages = true;
            try {
                _win._maskView.RestoreFromSession();
                Assert.IsTrue(_win.Editing.Masks.Common != null && _win.Editing.Masks.Common.Get(3), "読めた分を読んでいない");
                Assert.IsTrue(_win._maskView.MaskLoadFailed, "読めない項目を黙って捨てた");
                Save();
            } finally { LogAssert.ignoreFailingMessages = false; }
            Assert.AreEqual(withBroken, File.ReadAllText(file + ".bak"), "読めない項目のあったファイルを退避していない");
        }

        [Test]
        public void OwnSaves_AreWrittenToTheRecipe() {
            SetZoneName("窓で直した内容");
            Save();
            Assert.AreEqual("窓で直した内容", RecipeZoneName());
            SetZoneName("もう一度直した内容");
            Save();
            Assert.AreEqual("もう一度直した内容", RecipeZoneName(), "窓自身の保存を外からの変更と取り違えた");
        }

        [Test]
        public void ExternalChange_IsNotOverwritten_AndIsReloaded() {
            SetZoneName("窓の古い内容");
            ChangeOutside("Git で取り込んだ内容");

            Save();   // 閉じる・再コンパイル・ビルドの直前・自動保存と同じ保存
            Assert.AreEqual("Git で取り込んだ内容", RecipeZoneName(), "外で書き換わったレシピを窓の古い内容で上書きした");

            _win.ReloadChangedRecipe();
            Assert.AreEqual("Git で取り込んだ内容", ZoneName(), "窓がレシピから読み直していない");

            // 読み直した後は、窓の編集がまたレシピに保存される。
            SetZoneName("読み直してから直した内容");
            Save();
            Assert.AreEqual("読み直してから直した内容", RecipeZoneName());
        }

        [Test]
        public void ReimportWithTheSameContent_IsNotAnExternalChange() {
            // 同じ中身で取り込み直された(文字列の参照だけが変わった)なら、窓の編集をそのまま保存する。
            _recipe.sessionJson = new string(_recipe.sessionJson.ToCharArray());
            SetZoneName("窓で直した内容");
            Save();
            Assert.AreEqual("窓で直した内容", RecipeZoneName());
        }

        [Test]
        public void RecipeRestoredByUndo_IsAdopted() {
            // 前に同期したことのある中身へレシピが戻った(自動化 API の色替えを Ctrl+Z した)なら、外からの変更ではない。
            string registered = _recipe.sessionJson;
            SetZoneName("窓で直した内容");
            Save();
            _recipe.sessionJson = new string(registered.ToCharArray());

            _win.AdoptRecipeAfterUndo();
            SetZoneName("Undo の後に直した内容");
            Save();
            Assert.AreEqual("Undo の後に直した内容", RecipeZoneName(), "Undo で戻ったレシピを外からの変更と取り違えた");
        }

        private static IrocaSessionState State(string zoneName) {
            var s = IrocaSessionState.CreateDefault();
            s.zones.Add(new ColorZone { name = zoneName });
            return s;
        }

        // 自動保存とビルド直前の書き出しでレシピの中身が揺れない(揺れると .asset の書き換えとキャッシュの作り直しが増える)。
        [Test]
        public void RecipeJson_IsTheSameEveryTime() {
            Save();
            string saved = _recipe.sessionJson;
            var state = RecipeStore.Load(_recipe);
            Assert.AreEqual(0, state.maskState.width, "層が無ければ寸法を書かない");
            _win.Editing.Masks.Reset(4, 4);
            Save();
            Assert.AreEqual(saved, _recipe.sessionJson, "マスクの無いレシピが保存で変わった");

            _win.Editing.Masks.WritableCommon().Set(1, true);
            Save();
            string withMask = _recipe.sessionJson;
            Assert.AreEqual(MaskState.CurrentSchemaVersion, RecipeStore.Load(_recipe).maskState.schemaVersion);
            _win.Editing.ToSaveState();
            NonDestructiveApplier.RaiseBeforeApplyForTests();
            Assert.AreEqual(withMask, _recipe.sessionJson, "ビルド直前の書き出しで揺れた");
        }

        // 開いただけでは(編集しなければ)レシピを書かない。読んで書き直すと形だけが変わるレシピ(前の版で作った・id の無いゾーン・
        // 手で書いた JSON)は、開くと 2 秒後の自動保存で新しい形に書き直され、git に差分が出ていた。閉じる・再コンパイル・ビルドの
        // 直前の保存も同じ所を通る。編集すれば書き、編集して戻したら戻した中身を書く。
        [Test]
        public void OpeningOnly_DoesNotRewriteTheRecipe() {
            Object.DestroyImmediate(_win);
            // 前の版で作ったレシピ(ゾーンに id が無く、形式の版も古い)。
            _recipe.sessionJson = JsonUtility.ToJson(State("登録した内容"));
            _recipe.formatVersion = 1;
            string opened = _recipe.sessionJson;
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_recipe, _win.BoundRecipe, "テストの前提: レシピから開いている");
            Assert.IsFalse(string.IsNullOrEmpty(_win.Session.zones[0].id), "テストの前提: 開くとゾーンに id が付く");

            Save();   // 自動保存・閉じる・再コンパイルの直前と同じ保存
            NonDestructiveApplier.RaiseBeforeApplyForTests();
            Assert.AreEqual(opened, _recipe.sessionJson, "開いただけでレシピを書き直した");
            Assert.AreEqual(1, _recipe.formatVersion, "開いただけで形式の版を書き換えた");

            SetZoneName("窓で直した内容");
            Save();
            Assert.AreEqual("窓で直した内容", RecipeZoneName(), "編集がレシピに書かれない");
            Assert.AreEqual(IrocaRecipe.CurrentFormatVersion, _recipe.formatVersion);

            SetZoneName("登録した内容");
            Save();
            Assert.AreEqual("登録した内容", RecipeZoneName(), "戻した中身がレシピに書かれない");
        }

        // 前の版の仕上がりで確かめたレシピ(RecipeOutputVersion)は、窓で開いただけでは印を変えない(確かめていない)。
        // 中身を変えて保存すると今の版になり、登録は中身が同じでも今の版にする(窓のプレビュー = 今の版の仕上がりを見て登録した)。
        [Test]
        public void OpeningOnly_DoesNotMarkTheOutputVersion_EditingAndRegisteringDo() {
            Object.DestroyImmediate(_win);
            _recipe.outputVersion = 0;
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_recipe, _win.BoundRecipe, "テストの前提: レシピから開いている");

            Save();
            NonDestructiveApplier.RaiseBeforeApplyForTests();
            Assert.AreEqual(0, _recipe.outputVersion, "開いただけで、今の版で確かめたことにした");

            SetZoneName("窓で直した内容");
            Save();
            Assert.AreEqual(RecipeOutputVersion.Current, _recipe.outputVersion, "中身を変えて保存したのに印が上がらない");

            _recipe.outputVersion = 0;
            var avatar = new GameObject("avatar");
            try {
                var plan = RegistrationService.Describe(avatar, _win.SourceTexture, _recipe);
                _win.CommitRegistration(plan, _recipe, 0);
                Assert.AreEqual(RecipeOutputVersion.Current, _recipe.outputVersion, "登録したのに印が上がらない");
            } finally {
                foreach (var c in avatar.GetComponents<Component>()) {
                    Undo.ClearUndo(c);
                }
                Undo.ClearUndo(avatar);
                Object.DestroyImmediate(avatar);
            }
        }

        private void ChangeOutside(string zoneName) => _recipe.sessionJson = JsonUtility.ToJson(State(zoneName));

        private string ZoneName() => _win.Session.zones[0].name;

        private void SetZoneName(string name) => _win.Session.zones[0].name = name;

        private string RecipeZoneName() => RecipeStore.Load(_recipe).zones[0].name;

        private void Save() => _win.SavePersistedSessionForCurrentTexture();
    }
}
