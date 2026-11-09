// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// ウィンドウの Undo/Redo が編集内容だけを戻すこと(0.4.0 のフェーズ 5)。Undo の対象は文書の中身で、ウィンドウではない。
    /// ズーム・前後比較・ブラシの大きさ・開閉などは巻き戻らず、いま直しているゾーンはゾーンの並びが戻っても同じゾーンを指す。
    /// 保存の控え(古い版のレイアウト・ドメインリロードの読み直し)から文書を作り直せること。
    /// </summary>
    public class WindowUndoTests {
        private const int W = 4, H = 4;

        private TestAssets _assets;
        private string _tex;
        private string _guid;
        private string _prefKey;
        private string _prefBefore;
        private IrocaWindow _win;
        // 登録先にしたオブジェクト(TearDown で消す)。
        private readonly List<GameObject> _avatars = new List<GameObject>();

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            _tex = _assets.WritePng("tex", TestAssets.Solid(W, H, new Color32(200, 50, 50, 255)), W, H);
            _guid = AssetDatabase.AssetPathToGUID(_tex);
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreEqual(_tex, AssetDatabase.GetAssetPath(_win.SourceTexture), "窓がテスト用のテクスチャを開いていない");
            _win.Editing.Masks.Reset(W, H);
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown() {
            Undo.ClearAll();
            if (_win != null) {
                Object.DestroyImmediate(_win);
            }
            foreach (var go in _avatars) {
                if (go != null) {
                    Object.DestroyImmediate(go);
                }
            }
            _avatars.Clear();
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

        private void Stroke(int pixel) {
            Undo.IncrementCurrentGroup();
            _win._maskView.BeginStroke();
            _win.Editing.Masks.WritableCommon().Set(pixel, true);
            _win._maskView.EndStroke();
            _win.Editing.NotifyChanged(EditChange.Masks);
            Undo.IncrementCurrentGroup();
        }

        private void AddZones(params string[] names) {
            foreach (var name in names) {
                Undo.IncrementCurrentGroup();
                int i = _win.Editing.AddZone();
                _win.Session.zones[i].name = name;
                Undo.IncrementCurrentGroup();
            }
        }

        [Test]
        public void Undo_RestoresMasksButNotTheView() {
            var view = _win.Preview;
            var masks = _win._maskView;
            view.previewZoom = 1.5f;
            view.comparisonMode = false;
            masks.brushSize = 3;
            Stroke(5);

            view.previewZoom = 4f;
            view.comparisonMode = true;
            masks.brushSize = 9;
            Undo.PerformUndo();

            Assert.IsFalse(_win.Editing.Masks.Common != null && _win.Editing.Masks.Common.Get(5), "マスクは戻る");
            Assert.AreEqual(4f, view.previewZoom, "ズームは戻らない");
            Assert.IsTrue(view.comparisonMode, "前後比較は戻らない");
            Assert.AreEqual(9, masks.brushSize, "ブラシの大きさは戻らない");

            Undo.PerformRedo();
            Assert.IsTrue(_win.Editing.Masks.Common.Get(5), "やり直すとマスクも戻る");
            Assert.AreEqual(4f, view.previewZoom, "やり直しでもズームは動かない");
        }

        // 塗っている途中に Esc でブラシが切れると、マウスを離す処理へ来なくなる。ストロークはそこで閉じる
        // (閉じないと、塗っている途中のままで自動保存が止まり、Undo が次のストロークとまとまる)。
        [Test]
        public void EscapeDuringStroke_FinishesTheStroke() {
            var masks = _win._maskView;
            masks.maskPaintActive = true;
            Undo.IncrementCurrentGroup();
            masks.isPainting = true;
            masks.BeginStroke();
            _win.Editing.Masks.WritableCommon().Set(1, true);

            Assert.IsTrue(_win.ClearPreviewInteractionModes());

            Assert.IsFalse(masks.maskPaintActive);
            Assert.IsFalse(masks.isPainting, "塗っている途中のまま");
            Undo.IncrementCurrentGroup();
            Stroke(2);

            Undo.PerformUndo();
            Assert.IsTrue(_win.Editing.Masks.Common.Get(1), "1 回目の Undo で Esc の前のストロークまで戻った");
            Assert.IsFalse(_win.Editing.Masks.Common.Get(2));
            Undo.PerformUndo();
            Assert.IsFalse(_win.Editing.Masks.Common != null && _win.Editing.Masks.Common.Get(1), "Esc で閉じたストロークが戻らない");
        }

        // 読めなかったセッションのファイルは、新しい中身を書く前に .bak へ退避する。2 回目の保存で .bak を上書きしない。
        [Test]
        public void UnreadableSessionFile_BackupSurvivesTheNextSaves() {
            string other = _assets.WritePng("other", TestAssets.Solid(W, H, new Color32(50, 200, 50, 255)), W, H);
            var otherTex = AssetDatabase.LoadAssetAtPath<Texture2D>(other);
            var tex = _win.SourceTexture;
            string file = Path.Combine(SessionFileStore.CacheDir, _guid + ".iroca-session.json");
            _win.SwitchSourceTexture(otherTex);
            Directory.CreateDirectory(SessionFileStore.CacheDir);
            File.WriteAllText(file, "{ broken");
            LogAssert.ignoreFailingMessages = true;
            try {
                _win.SwitchSourceTexture(tex);
                AddZones("a");
                _win.SavePersistedSessionForCurrentTexture();
                AddZones("b");
                _win.SavePersistedSessionForCurrentTexture();
            } finally { LogAssert.ignoreFailingMessages = false; }
            Assert.AreEqual("{ broken", File.ReadAllText(file + ".bak"), "読めなかった元のファイルが残っていない");
            StringAssert.Contains("\"b\"", File.ReadAllText(file), "新しい中身は書く");
            CleanOther(other);
        }

        // Undo で読めないファイルのテクスチャへ戻ったら、読めなかった印もそのテクスチャのものになる。残っていないと、空の保存で
        // 読めなかったファイルを削除する。
        [Test]
        public void UndoBackToUnreadableTexture_DoesNotDeleteItsFile() {
            string other = _assets.WritePng("other", TestAssets.Solid(W, H, new Color32(50, 200, 50, 255)), W, H);
            var otherTex = AssetDatabase.LoadAssetAtPath<Texture2D>(other);
            string file = Path.Combine(SessionFileStore.CacheDir, _guid + ".iroca-session.json");
            Directory.CreateDirectory(SessionFileStore.CacheDir);
            LogAssert.ignoreFailingMessages = true;
            try {
                // 読めない状態で A を開き直してから B へ切り替える。
                _win.SwitchSourceTexture(otherTex);
                File.WriteAllText(file, "{ broken");
                _win.SwitchSourceTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(_tex));
                Undo.IncrementCurrentGroup();
                _win.SwitchSourceTexture(otherTex);
                Undo.IncrementCurrentGroup();

                Undo.PerformUndo();
                Assert.AreEqual(_tex, AssetDatabase.GetAssetPath(_win.SourceTexture), "テクスチャが戻っていない");
                _win.SavePersistedSessionForCurrentTexture();
            } finally { LogAssert.ignoreFailingMessages = false; }
            Assert.AreEqual("{ broken", File.ReadAllText(file), "読めなかったファイルを消した");
            CleanOther(other);
        }

        // 履歴に無い版へテクスチャごと戻ったとき、前のテクスチャのマスクを残さない(次の保存で別のテクスチャへ書き込む)。
        [Test]
        public void UndoToMissingMaskVersion_WithTextureChange_ReadsTheSavedMasks() {
            string other = _assets.WritePng("other", TestAssets.Solid(W, H, new Color32(50, 200, 50, 255)), W, H);
            var otherTex = AssetDatabase.LoadAssetAtPath<Texture2D>(other);
            Stroke(1);
            _win.SwitchSourceTexture(otherTex);   // ここで A のマスクを保存する
            Undo.IncrementCurrentGroup();
            _win.Editing.Masks.Reset(W, H);
            Stroke(2);
            _win.Editing.MaskHistory.Clear();     // 上限で手放した(Unity 6 で退避を読めなかった)のと同じ

            LogAssert.ignoreFailingMessages = true;
            try {
                Undo.PerformUndo();   // B のストローク
                Undo.PerformUndo();   // テクスチャの切り替え
            } finally { LogAssert.ignoreFailingMessages = false; }

            Assert.AreEqual(_tex, AssetDatabase.GetAssetPath(_win.SourceTexture), "テクスチャが戻っていない");
            var common = _win.Editing.Masks.Common;
            Assert.IsNotNull(common, "A のマスクを読んでいない");
            Assert.IsTrue(common.Get(1), "A のマスクを読んでいない");
            Assert.IsFalse(common.Get(2), "B のマスクが残っている");
            CleanOther(other);
        }

        // テクスチャの切り替えを Undo したら、書き出しの既定の名前も戻す。
        [Test]
        public void UndoOfTextureChange_RestoresTheExportName() {
            string other = _assets.WritePng("other", TestAssets.Solid(W, H, new Color32(50, 200, 50, 255)), W, H);
            _win.SwitchSourceTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(other));
            StringAssert.StartsWith("other", _win._exportView.newFileName);
            Undo.PerformUndo();
            StringAssert.StartsWith("tex", _win._exportView.newFileName);
            CleanOther(other);
        }

        // レシピとの結び付きは編集内容ではない。登録より前の記録まで戻っても外さない(外れると、以後の編集がレシピに入らず、開き直すと
        // 古いレシピが正として読まれて消える)。
        [Test]
        public void UndoBeforeRegistration_KeepsTheRecipeBinding() {
            AddZones("服");
            var recipe = RecipeStore.Create(_win.SourceTexture, _win.Editing.ToSaveState(), _assets.Folder);
            Undo.IncrementCurrentGroup();
            IrocaWindow.ReloadFromRecipe(recipe, "bind");
            Assert.AreSame(recipe, _win.BoundRecipe, "テストの前提: 結び付いている");
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreSame(recipe, _win.BoundRecipe, "Undo で結び付きが外れた");
        }

        // 「いろかで開く」は読み込みと一緒に Undo で戻る。結び付きだけ残すと、開く前の内容でレシピを上書きする。
        [Test]
        public void UndoOfOpeningRecipe_DoesNotOverwriteTheRecipe() {
            AddZones("開く前");
            var texture = _win.SourceTexture;
            var content = new IrocaSessionState();
            content.zones.Add(new ColorZone { name = "レシピの内容" });
            var a = RecipeStore.Create(texture, content, _assets.Folder);
            var b = RecipeStore.Create(texture, content, _assets.Folder);
            Assert.AreNotSame(a, b);
            Undo.IncrementCurrentGroup();
            _win.SwitchSourceTexture(texture, b);
            Assert.AreSame(b, _win.BoundRecipe, "テストの前提: 結び付いている");
            Assert.AreEqual("レシピの内容", _win.Session.zones[0].name);
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreEqual("開く前", _win.Session.zones[0].name, "テストの前提: 内容が戻っている");
            Assert.IsNull(_win.BoundRecipe, "読み込みを戻したのに結び付きが残っている");
            _win.SavePersistedSessionForCurrentTexture();
            Assert.AreEqual("レシピの内容", RecipeStore.Load(b).zones[0].name, "開く前の内容でレシピを上書きした");
        }

        private GameObject Avatar() {
            var go = new GameObject("avatar");
            _avatars.Add(go);
            return go;
        }

        // 「いろかで開く」で結び付けたレシピ b に、同じ窓から登録する(レシピ 2 つ・結び付きは開いたときのもの)。
        private IrocaRecipe OpenSecondRecipe(out Texture2D texture) {
            AddZones("開く前");
            texture = _win.SourceTexture;
            var content = new IrocaSessionState();
            content.zones.Add(new ColorZone { name = "レシピの内容" });
            RecipeStore.Create(texture, content, _assets.Folder);
            var b = RecipeStore.Create(texture, content, _assets.Folder);
            Undo.IncrementCurrentGroup();
            _win.SwitchSourceTexture(texture, b);
            Undo.IncrementCurrentGroup();
            Assert.AreSame(b, _win.BoundRecipe, "テストの前提: 結び付いている");
            return b;
        }

        // 「いろかで開く」の後に登録しても、結び付きは開いたときのもの。開く前まで Undo したら結び付きも戻る(残すと、開く前の内容で
        // レシピを上書きする)。
        [Test]
        public void UndoOfOpeningRecipe_AfterRegistering_DoesNotOverwriteTheRecipe() {
            var b = OpenSecondRecipe(out var texture);
            _win.CommitRegistration(RegistrationService.Describe(Avatar(), texture, b), b, 0);
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();   // 登録
            Undo.PerformUndo();   // 開く
            Assert.AreEqual("開く前", _win.Session.zones[0].name, "テストの前提: 内容が戻っている");
            Assert.IsNull(_win.BoundRecipe, "開く前まで戻したのに結び付きが残っている");
            _win.SavePersistedSessionForCurrentTexture();
            Assert.AreEqual("レシピの内容", RecipeStore.Load(b).zones[0].name, "開く前の内容でレシピを上書きした");
        }

        // 自動化(MCP の色替え)が結び付いたレシピを書き換えて窓を読み直させた後も同じ。
        [Test]
        public void UndoOfOpeningRecipe_AfterAutomationReload_DoesNotOverwriteTheRecipe() {
            var b = OpenSecondRecipe(out _);
            IrocaWindow.ReloadFromRecipe(b, "automation");
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();   // 読み直し
            Undo.PerformUndo();   // 開く
            Assert.AreEqual("開く前", _win.Session.zones[0].name, "テストの前提: 内容が戻っている");
            Assert.IsNull(_win.BoundRecipe, "開く前まで戻したのに結び付きが残っている");
            _win.SavePersistedSessionForCurrentTexture();
            Assert.AreEqual("レシピの内容", RecipeStore.Load(b).zones[0].name, "開く前の内容でレシピを上書きした");
        }

        // 結び付いていない窓から、登録先などのレシピを流用して登録すると、レシピの中身を窓の内容で置き換える。Ctrl+Z 1 回で
        // コンポーネント・レシピの中身・結び付きがそろって戻る(結び付きだけ残すと、戻ったレシピを外での書き換えと取り違えて
        // 読み直し、窓の内容が消える)。
        [Test]
        public void RegisteringFromUnboundWindow_UndoBringsTheRecipeBack() {
            AddZones("窓の内容");
            var texture = _win.SourceTexture;
            var content = new IrocaSessionState();
            content.zones.Add(new ColorZone { name = "レシピの内容" });
            var recipe = RecipeStore.Create(texture, content, _assets.Folder);
            string before = recipe.sessionJson;
            Assert.IsNull(_win.BoundRecipe, "テストの前提: 結び付いていない");

            // 確認画面で、中身を置き換えることを知らせる(中身が窓と同じなら知らせない)。
            var plan = RegistrationService.Describe(Avatar(), texture, recipe);
            string label = AssetDatabase.GetAssetPath(recipe);
            string overwrite = string.Format(Localization.RegisterRecipeOverwriteFormat, label);
            Assert.IsTrue(_win.RegistrationOverwritesRecipe(recipe));
            StringAssert.Contains(overwrite, IrocaWindow.RegisterMessage(plan, label, overwritesRecipe: true));
            StringAssert.DoesNotContain(overwrite, IrocaWindow.RegisterMessage(plan, label, overwritesRecipe: false));
            var same = RecipeStore.Create(texture, _win.Editing.ToSaveState(), _assets.Folder);
            Assert.IsFalse(_win.RegistrationOverwritesRecipe(same), "中身が同じでも置き換えを知らせた");

            Undo.IncrementCurrentGroup();
            var component = _win.CommitRegistration(plan, recipe, 0);
            Undo.IncrementCurrentGroup();
            Assert.IsFalse(_win.RegistrationOverwritesRecipe(recipe), "結び付いたレシピは置き換えではない");
            Assert.AreEqual("窓の内容", RecipeStore.Load(recipe).zones[0].name, "窓の内容で置き換える");
            Assert.AreSame(recipe, _win.BoundRecipe);

            Undo.PerformUndo();
            Assert.IsTrue(component == null, "コンポーネントが戻っていない");
            Assert.AreEqual(before, recipe.sessionJson, "レシピの中身が戻っていない");
            Assert.IsNull(_win.BoundRecipe, "結び付きが戻っていない");
            Assert.AreEqual("窓の内容", _win.Session.zones[0].name, "窓の内容は変わらない");
            _win.SavePersistedSessionForCurrentTexture();
            Assert.AreEqual(before, recipe.sessionJson, "戻したレシピを保存で書き換えた");
        }

        // 複数のレシピがあるテクスチャは、最後に結び付いたレシピへ開き直す。Undo で外したら開き直しても結び付けない。
        [Test]
        public void ReopeningTextureWithSeveralRecipes_BindsTheLastOne() {
            var texture = _win.SourceTexture;
            var content = new IrocaSessionState();
            content.zones.Add(new ColorZone { name = "レシピの内容" });
            var a = RecipeStore.Create(texture, content, _assets.Folder);
            var b = RecipeStore.Create(texture, content, _assets.Folder);
            _win.SwitchSourceTexture(texture);
            Assert.IsNull(_win.BoundRecipe, "テストの前提: 選ばれていなければ結び付かない");
            Assert.AreEqual(2, _win.UnboundRecipeCount, "窓が案内を出す数");
            Undo.IncrementCurrentGroup();
            _win.SwitchSourceTexture(texture, b);
            Assert.AreEqual(0, _win.UnboundRecipeCount);
            Undo.IncrementCurrentGroup();

            Assert.AreSame(b, SessionPersistence.RecipeFor(texture, null), "最後に結び付いたレシピを覚えていない");

            Undo.PerformUndo();
            Assert.IsNull(_win.BoundRecipe);
            Assert.IsNull(SessionPersistence.RecipeFor(texture, null), "外したレシピへ開き直してしまう");
            Assert.AreEqual(2, _win.UnboundRecipeCount);
        }

        // 読めないまま空で開いた後、再読み込みの間に読めるようになっても、空の保存でファイルを消さない。
        [Test]
        public void SessionFileThatBecameReadable_IsNotDeletedByReload() {
            string file = Path.Combine(SessionFileStore.CacheDir, _guid + ".iroca-session.json");
            var state = new IrocaSessionState();
            state.zones.Add(new ColorZone { name = "前の編集" });
            string json = JsonUtility.ToJson(state);
            Directory.CreateDirectory(SessionFileStore.CacheDir);
            _win.SwitchSourceTexture(null);
            File.WriteAllText(file, "{ broken");
            LogAssert.ignoreFailingMessages = true;
            _win.SwitchSourceTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(_tex));
            Assert.IsTrue(_win.SessionLoadFailed, "テストの前提: 読めない");
            Assert.AreEqual(0, _win.Session.zones.Count);
            LogAssert.ignoreFailingMessages = false;

            File.WriteAllText(file, json);
            _win.OnEnable();   // 再読み込み

            Assert.AreEqual("前の編集", _win.Session.zones[0].name, "読めるようになった内容を読まなかった");
            Assert.IsFalse(_win.SessionLoadFailed);
            _win.SavePersistedSessionForCurrentTexture();
            StringAssert.Contains("前の編集", File.ReadAllText(file));
        }

        // マスクのファイルも同じ。読めないまま空で開いた後、再読み込みの間に読めるようになったら、履歴の空の版でなくファイルを読む
        // (空の版で上書きすると、次の保存でファイルを消す)。
        [Test]
        public void MaskFileThatBecameReadable_IsNotDeletedByReload() {
            string file = Path.Combine(MaskFileStore.CacheDir, _guid + ".iroca-mask.json");
            string json;
            var painted = new MaskWorkspace();
            try {
                painted.Reset(W, H);
                painted.WritableCommon().Set(3, true);
                var ms = new MaskState();
                painted.EncodeTo(ms);
                json = JsonUtility.ToJson(ms);
            } finally { painted.Clear(); }
            Directory.CreateDirectory(MaskFileStore.CacheDir);
            _win.SwitchSourceTexture(null);
            File.WriteAllText(file, "{ broken");
            LogAssert.ignoreFailingMessages = true;
            try {
                _win.SwitchSourceTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(_tex));
                Assert.IsTrue(_win._maskView.MaskLoadFailed, "テストの前提: 読めない");
                Assert.IsFalse(_win.Editing.Masks.HasLayers);
            } finally { LogAssert.ignoreFailingMessages = false; }

            File.WriteAllText(file, json);
            _win.OnEnable();   // 再読み込み

            var common = _win.Editing.Masks.Common;
            Assert.IsTrue(common != null && common.Get(3), "読めるようになったマスクを読まなかった");
            Assert.IsTrue(_win.SavePersistedSessionForCurrentTexture());
            Assert.IsTrue(File.Exists(file), "空の保存でマスクのファイルを消した");
            var fromFile = new MaskWorkspace();
            try {
                Assert.IsTrue(fromFile.DecodeFrom(MaskFileStore.LoadMask(_tex, out _)));
                Assert.IsTrue(fromFile.Common.Get(3));
            } finally { fromFile.Clear(); }
        }

        // 再読み込みの前にマスクをファイルへ書けなくても、塗った分は履歴から戻る。
        [Test]
        public void MaskStrokeNotWrittenToFile_ComesBackFromHistoryAfterReload() {
            Stroke(3);
            Assert.IsTrue(_win.SavePersistedSessionForCurrentTexture());
            Stroke(5);   // ファイルにはまだ書いていない = 書けなかったときと同じ
            _win.OnEnable();   // 再読み込み(ファイルのマスクを読み直す)

            var common = _win.Editing.Masks.Common;
            Assert.IsTrue(common.Get(3));
            Assert.IsTrue(common.Get(5), "ファイルより新しい塗りが消えた");
        }

        // 保存に失敗したことを窓に出す印。直れば下りる。
        [Test]
        public void SaveFailure_IsKeptUntilASaveSucceeds() {
            AddZones("服");
            string file = Path.Combine(SessionFileStore.CacheDir, _guid + ".iroca-session.json");
            Directory.CreateDirectory(file);   // 同じ名前のフォルダがあるとファイルを書けない
            try {
                LogAssert.ignoreFailingMessages = true;
                Assert.IsFalse(_win.SavePersistedSessionForCurrentTexture());
                Assert.IsTrue(_win.LastSaveFailed);
            } finally {
                LogAssert.ignoreFailingMessages = false;
                Directory.Delete(file);
            }
            Assert.IsTrue(_win.SavePersistedSessionForCurrentTexture());
            Assert.IsFalse(_win.LastSaveFailed);
        }

        // 開き直した窓の宛先は先頭のゾーン。全ゾーン共通のままだと、右クリックにゾーン宛の項目が出ず、ブラシも共通へ塗る。
        [Test]
        public void ReopenedWindow_TargetsTheFirstZone() {
            AddZones("服", "靴");
            Assert.IsTrue(_win.SavePersistedSessionForCurrentTexture());
            Object.DestroyImmediate(_win);
            _win = ScriptableObject.CreateInstance<IrocaWindow>();

            Assert.AreEqual(2, _win.Session.zones.Count, "テストの前提: 読み直している");
            Assert.AreEqual(0, _win._maskView.activeMaskTarget, "全ゾーン共通になった");
        }

        // スポイト・シード指定を武装したらブラシを切る(残すと、カードは武装の色なのにクリックは塗りになる)。
        [Test]
        public void ArmingEyedropperOrSeed_TurnsTheBrushOff() {
            AddZones("服");
            var zone = _win.Session.zones[0];
            _win._maskView.ActivateBrush(erase: false);
            _win.ArmEyedropper(zone.id);
            Assert.IsFalse(_win._maskView.maskPaintActive);

            _win._maskView.ActivateBrush(erase: false);
            _win.ArmSeedPick(zone.id);
            Assert.IsFalse(_win._maskView.maskPaintActive);
            Assert.IsNull(_win.EyedropperZoneId, "スポイトと排他でない");
        }

        // 別の窓にフォーカスがあっても Esc で解除できる。
        [Test]
        public void GlobalEscape_ClearsTheModes() {
            AddZones("服");
            _win.ArmEyedropper(_win.Session.zones[0].id);
            _win.ClearModesOnEscape(EventType.KeyUp, KeyCode.Escape);
            Assert.IsNotNull(_win.EyedropperZoneId, "押したときだけ解除する");
            _win.ClearModesOnEscape(EventType.KeyDown, KeyCode.Escape);
            Assert.IsNull(_win.EyedropperZoneId);
        }

        // テクスチャを選ぶ前に作ったゾーンは、そのテクスチャに編集内容が無ければ引き継ぐ。
        [Test]
        public void ZonesMadeWithoutTexture_AreCarriedToTheTexture() {
            var texture = _win.SourceTexture;
            _win.SwitchSourceTexture(null);
            AddZones("先に作った");
            _win.SwitchSourceTexture(texture);
            Assert.AreEqual(1, _win.Session.zones.Count, "作ったゾーンが消えた");
            Assert.AreEqual("先に作った", _win.Session.zones[0].name);
        }

        // テクスチャの切り替えを Undo したら、そのテクスチャで直していたゾーンへ戻る。
        [Test]
        public void UndoOfTextureSwitch_RestoresTheTargetZone() {
            AddZones("服", "靴");
            _win._maskView.activeMaskTarget = 1;
            string other = _assets.WritePng("target-other", TestAssets.Solid(W, H, Color.white), W, H);
            Undo.IncrementCurrentGroup();
            _win.SwitchSourceTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(other));
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreEqual(_tex, AssetDatabase.GetAssetPath(_win.SourceTexture), "テストの前提: 戻っている");
            Assert.AreEqual(1, _win._maskView.activeMaskTarget, "直していたゾーンへ戻らなかった");
            CleanOther(other);
        }

        // マスクの重ね表示の画素(ヒープの外で作る)。塗った所だけ色が付き、ほかは透明。
        [Test]
        public void MaskOverlay_ShowsOnlyThePaintedPixels() {
            var view = _win._maskView;
            _win.Editing.Masks.WritableCommon().Set(5, true);
            view.RebuildMaskOverlay(W, H);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (view.maskOverlayTexture == null && watch.Elapsed.TotalSeconds < 10) {
                PreviewJobMainThread.Drain();
                view.ApplyPendingOverlay();
                System.Threading.Thread.Sleep(1);
            }
            Assert.IsNotNull(view.maskOverlayTexture, "重ね表示ができない");
            var pixels = view.maskOverlayTexture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++) {
                if (i == 5) {
                    Assert.Greater(pixels[i].a, 0, "塗った所に色が無い");
                } else {
                    Assert.AreEqual(0, pixels[i].a, $"塗っていない所 {i} に色がある");
                }
            }
        }

        // 自動調整を押した後に Ctrl+Z やテクスチャの切り替えをしたら、結果を当てない(押したときの状態はもう無い)。
        [Test]
        public void AutoTuneRunning_IsStoppedByUndo() {
            AddZones("服");
            AddZones("靴");
            var zone = _win.Session.zones[1];
            _win.RunAutoTune(zone);
            Assert.IsTrue(_win.IsJobBlockingUI, "テストの前提: 自動調整が走っている");

            Undo.PerformUndo();
            Assert.IsFalse(_win.IsJobBlockingUI, "Undo で止まっていない");
        }

        [Test]
        public void AutoTuneRunning_IsStoppedByTextureChange() {
            string other = _assets.WritePng("other", TestAssets.Solid(W, H, new Color32(50, 200, 50, 255)), W, H);
            AddZones("服");
            _win.RunAutoTune(_win.Session.zones[0]);
            Assert.IsTrue(_win.IsJobBlockingUI, "テストの前提: 自動調整が走っている");

            _win.SwitchSourceTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(other));
            Assert.IsFalse(_win.IsJobBlockingUI, "テクスチャの切り替えで止まっていない");
            CleanOther(other);
        }

        private static void CleanOther(string otherTexturePath) {
            string guid = AssetDatabase.AssetPathToGUID(otherTexturePath);
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                if (!Directory.Exists(dir)) {
                    continue;
                }
                foreach (var f in Directory.GetFiles(dir, guid + "*")) {
                    File.Delete(f);
                }
            }
        }

        [Test]
        public void Undo_OfSliderEdit_RestoresTheValueAndAsksForAPreview() {
            AddZones("服");
            float before = _win.Session.zones[0].tolerance;
            int replaced = 0;
            _win.Editing.Changed += c => {
                if (c == EditChange.Replaced) {
                    replaced++;
                }
            };

            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_win.UndoTarget, "Iroca Edit");
            _win.Session.zones[0].tolerance = before + 0.25f;
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreEqual(before, _win.Session.zones[0].tolerance, "ウィンドウから読む値も戻る");
            Assert.AreEqual(1, replaced, "編集が戻ったことを知らせる(プレビューの作り直し・重ね表示・自動保存)");
        }

        private bool[] Pixels(params int[] on) {
            var region = new bool[W * H];
            foreach (var i in on) {
                region[i] = true;
            }
            return region;
        }

        private void ApplyToMask(string zoneId, MaskRegionOp op, params int[] on) {
            Undo.IncrementCurrentGroup();
            Assert.IsTrue(_win._maskView.ApplyRegion(Pixels(on), new MaskPaintView.MaskDestination(zoneId, op), out _), op.ToString());
            Undo.IncrementCurrentGroup();
        }

        [Test]
        public void ClearHere_RemovesOnlyTheRegion_AndUndoBringsItBack() {
            AddZones("服");
            string id = _win.Session.zones[0].id;
            ApplyToMask(id, MaskRegionOp.PaintHere, 0, 1);
            ApplyToMask(id, MaskRegionOp.DontPaintHere, 4, 5);

            ApplyToMask(id, MaskRegionOp.ClearHere, 1, 5);
            Assert.IsTrue(_win.Editing.Masks.Layer(true, id).Get(0), "範囲の外の含めるは残る");
            Assert.IsFalse(_win.Editing.Masks.Layer(true, id).Get(1), "範囲の中の含めるは消える");
            Assert.IsTrue(_win.Editing.Masks.Layer(false, id).Get(4), "範囲の外の除外は残る");
            Assert.IsFalse(_win.Editing.Masks.Layer(false, id).Get(5), "範囲の中の除外は消える");

            Undo.PerformUndo();
            Assert.IsTrue(_win.Editing.Masks.Layer(true, id).Get(1), "Undo で含めるが戻る");
            Assert.IsTrue(_win.Editing.Masks.Layer(false, id).Get(5), "Undo で除外が戻る");

            Undo.PerformRedo();
            Assert.IsFalse(_win.Editing.Masks.Layer(true, id).Get(1), "やり直すと含めるがまた消える");
            Assert.IsFalse(_win.Editing.Masks.Layer(false, id).Get(5), "やり直すと除外がまた消える");
        }

        [Test]
        public void ClearHere_WithNothingToClear_LeavesNoUndoStep() {
            AddZones("服");
            string id = _win.Session.zones[0].id;
            ApplyToMask(id, MaskRegionOp.DontPaintHere, 0);
            long revision = _win.Editing.Document.maskRevision;

            Assert.IsTrue(_win._maskView.ApplyRegion(Pixels(15),
                new MaskPaintView.MaskDestination(id, MaskRegionOp.ClearHere), out var result));
            Assert.AreEqual(0, result.changed, "消す物が無い");
            Assert.AreEqual(revision, _win.Editing.Document.maskRevision, "マスクの版が進まない");

            Undo.PerformUndo();
            var exclude = _win.Editing.Masks.Layer(false, id);
            Assert.IsFalse(exclude != null && exclude.Get(0), "次の Ctrl+Z は、その前の操作を戻す");
        }

        [Test]
        public void ClearHere_WhereOnlyTheCommonMaskIs_SaysSo() {
            AddZones("服");
            string id = _win.Session.zones[0].id;
            ApplyToMask(null, MaskRegionOp.DontPaintHere, 3);
            var dest = new MaskPaintView.MaskDestination(id, MaskRegionOp.ClearHere);

            Assert.IsTrue(_win._maskView.ApplyRegion(Pixels(3), dest, out var result));
            Assert.AreEqual(0, result.changed, "ゾーンには消す物が無い");
            Assert.AreEqual(1, result.commonOverlap, "共通の除外の重なりを数える");
            Assert.IsTrue(_win.Editing.Masks.Common.Get(3), "共通の除外は消さない");
            string message = _win._maskView.DescribeRegionEdit(dest, true, result);
            StringAssert.Contains(Localization.NotifyClearHereNothing, message);
            StringAssert.Contains(Localization.NotifyCommonOverlap, message);
        }

        [Test]
        public void ClearHere_ToCommon_ClearsOnlyTheCommonMask() {
            AddZones("服");
            string id = _win.Session.zones[0].id;
            ApplyToMask(null, MaskRegionOp.DontPaintHere, 3, 7);
            ApplyToMask(id, MaskRegionOp.DontPaintHere, 3);

            ApplyToMask(null, MaskRegionOp.ClearHere, 3);
            Assert.IsFalse(_win.Editing.Masks.Common.Get(3), "範囲の中の共通の除外は消える");
            Assert.IsTrue(_win.Editing.Masks.Common.Get(7), "範囲の外の共通の除外は残る");
            Assert.IsTrue(_win.Editing.Masks.Layer(false, id).Get(3), "ゾーンの除外は触らない");
        }

        [Test]
        public void ClearTarget_IsTheZoneWithAMaskHere_NotThePaintingZone() {
            AddZones("上", "下");
            string upper = _win.Session.zones[0].id, lower = _win.Session.zones[1].id;
            var view = _win._maskView;
            const int Pixel = 3 * W + 3;   // (3, 3)
            var near = new Vector2(3.5f / W, 3.5f / H);
            var far = new Vector2(0.5f / W, 0.5f / H);   // (3, 3) から 3 画素離れている(見る範囲は 2 画素)

            ApplyToMask(upper, MaskRegionOp.DontPaintHere, Pixel);
            Assert.AreEqual(upper, view.ClearTargetZoneIdAt(near, upper, lower), "塗っている下にマスクが無ければ、除外を持つ上");
            Assert.AreEqual(lower, view.ClearTargetZoneIdAt(far, upper, lower), "マスクが無い所は、ほかの操作と同じ宛先");

            ApplyToMask(lower, MaskRegionOp.PaintHere, Pixel);
            Assert.AreEqual(lower, view.ClearTargetZoneIdAt(near, upper, lower), "どちらにもあれば塗っている下");

            _win.Session.zones[1].enabled = false;
            Assert.AreEqual(upper, view.ClearTargetZoneIdAt(near, upper, lower), "無効のゾーンは見ない");
        }

        [Test]
        public void UnrelatedUndo_DoesNotRebuildThePreview() {
            var other = ScriptableObject.CreateInstance<SampleUvUndoHost>();
            try {
                Stroke(1);
                Undo.IncrementCurrentGroup();
                Undo.RegisterCompleteObjectUndo(other, "Unrelated");
                other.session.zones.Add(new ColorZone());
                Undo.IncrementCurrentGroup();
                int changes = 0;
                _win.Editing.Changed += _ => changes++;

                Undo.PerformUndo();
                Assert.AreEqual(1, other.session.zones.Count == 0 ? 1 : 0, "相手の Undo は効いている");
                Assert.AreEqual(0, changes, "関係ない Undo では編集として知らせない(プレビューを作り直さない)");
            } finally { Object.DestroyImmediate(other); }
        }

        [Test]
        public void ActiveTarget_FollowsItsZoneWhenUndoChangesTheOrder() {
            AddZones("a", "b", "c");
            string a = _win.Session.zones[0].id;
            var masks = _win._maskView;

            Undo.IncrementCurrentGroup();
            Assert.IsTrue(_win.Editing.RemoveZoneAt(0));
            Undo.IncrementCurrentGroup();
            masks.activeMaskTarget = 1;   // 消した後の c
            masks.RememberActiveTarget();

            Undo.PerformUndo();
            Assert.AreEqual(a, _win.Session.zones[0].id, "消したゾーンが戻る");
            Assert.AreEqual(2, masks.activeMaskTarget, "c を指したまま");
        }

        [Test]
        public void ActiveTarget_FallsBackWhenUndoRemovesItsZone() {
            AddZones("a", "b");
            var masks = _win._maskView;
            masks.activeMaskTarget = 1;
            masks.RememberActiveTarget();

            Undo.PerformUndo();
            Assert.AreEqual(1, _win.Session.zones.Count);
            Assert.AreEqual(0, masks.activeMaskTarget, "残ったゾーンへ移る");

            masks.RememberActiveTarget();
            Undo.PerformUndo();
            Assert.AreEqual(0, _win.Session.zones.Count);
            Assert.AreEqual(-1, masks.activeMaskTarget, "ゾーンが無ければ全ゾーン共通");
        }

        [Test]
        public void ActiveTarget_CommonStaysCommon() {
            AddZones("a");
            var masks = _win._maskView;
            masks.activeMaskTarget = -1;
            masks.RememberActiveTarget();
            Undo.PerformUndo();
            Assert.AreEqual(-1, masks.activeMaskTarget);
        }

        [Test]
        public void ChangeSourceTexture_UndoBringsBackTextureAndZones() {
            AddZones("服");
            var first = _win.SourceTexture;
            string otherPath = _assets.WritePng("other", TestAssets.Solid(W, H, new Color32(10, 20, 200, 255)), W, H);
            var other = AssetDatabase.LoadAssetAtPath<Texture2D>(otherPath);

            Undo.IncrementCurrentGroup();
            _win.SwitchSourceTexture(other);
            Undo.IncrementCurrentGroup();
            Assert.AreSame(other, _win.SourceTexture);
            Assert.AreEqual(0, _win.Session.zones.Count, "別のテクスチャの編集内容になる");

            Undo.PerformUndo();
            Assert.AreSame(first, _win.SourceTexture, "テクスチャが戻る");
            Assert.AreEqual(1, _win.Session.zones.Count, "ゾーンも戻る");

            string otherGuid = AssetDatabase.AssetPathToGUID(otherPath);
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                if (!Directory.Exists(dir)) {
                    continue;
                }
                foreach (var f in Directory.GetFiles(dir, otherGuid + "*")) {
                    File.Delete(f);
                }
            }
        }

        [Test]
        public void SavedForm_KeepsTheOldFieldNames() {
            AddZones("控えに残る");
            // 0.3.x に戻したとき・文書を失ったときに読む控えは、今の中身を指している。
            string json = EditorJsonUtility.ToJson(_win);
            StringAssert.Contains("\"_session\"", json);
            StringAssert.Contains("控えに残る", json);
            StringAssert.Contains("\"sourceTexture\"", json);
            StringAssert.Contains("\"_boundRecipe\"", json);
        }

        [Test]
        public void MissingDocument_IsRebuiltFromTheSavedForm() {
            AddZones("控えから戻す");
            string json = EditorJsonUtility.ToJson(_win);
            var texture = _win.SourceTexture;

            // 0.3.x のレイアウト(文書が無く、控えだけがある)を読んだ状態を作る。
            var old = ScriptableObject.CreateInstance<IrocaWindow>();
            try {
                Object.DestroyImmediate(old.UndoTarget);
                EditorJsonUtility.FromJsonOverwrite(json.Replace("\"_document\"", "\"_documentGone\""), old);
                old.OnEnable();
                Assert.AreSame(texture, old.SourceTexture, "テクスチャは控えから");
                Assert.AreEqual(1, old.Session.zones.Count);
                Assert.AreEqual("控えから戻す", old.Session.zones[0].name, "ゾーンは控えから");
                Assert.AreEqual(HideFlags.HideAndDontSave, old.UndoTarget.hideFlags, "新しい文書を作る");
                Assert.AreNotSame(_win.UndoTarget, old.UndoTarget);
            } finally { Object.DestroyImmediate(old); }
        }

        [Test]
        public void Document_NeverHoldsMaskStrings() {
            Stroke(5);
            AddZones("服");
            Stroke(6);
            _win.SavePersistedSessionForCurrentTexture();

            string json = EditorJsonUtility.ToJson(_win.UndoTarget);
            StringAssert.DoesNotContain("R:", json, "Undo に載る文書にマスクの文字列が入った");
            Assert.IsEmpty(_win.Session.maskState.commonMaskBase64 ?? "");
        }

        [Test]
        public void Saving_WritesTheEditingMasks() {
            Stroke(5);
            Assert.IsTrue(_win.SavePersistedSessionForCurrentTexture());

            var saved = MaskFileStore.LoadMask(_tex, out bool unreadable);
            Assert.IsFalse(unreadable);
            Assert.IsNotNull(saved, "マスクがファイルに書かれていない");
            var fromFile = new MaskWorkspace();
            try {
                Assert.IsTrue(fromFile.DecodeFrom(saved));
                Assert.IsTrue(fromFile.Common.Get(5));
            } finally { fromFile.Clear(); }
        }

        [Test]
        public void Undo_OfAnEditWithoutMasks_DoesNotTouchTheMasks() {
            AddZones("服");
            Stroke(5);
            var layer = _win.Editing.Masks.Common;
            float before = _win.Session.zones[0].tolerance;

            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_win.UndoTarget, "Iroca Edit");
            _win.Session.zones[0].tolerance = before + 0.25f;
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreEqual(before, _win.Session.zones[0].tolerance);
            Assert.AreSame(layer, _win.Editing.Masks.Common, "マスクの版は変わらないので触らない");
        }

        [Test]
        public void Undo_OfAVersionNoLongerInHistory_KeepsMasksAndEditingContinues() {
            Stroke(1);
            Stroke(2);
            _win.Editing.MaskHistory.Clear();   // 上限で手放した状態

            Undo.PerformUndo();
            Assert.IsTrue(_win.Editing.Masks.Common.Get(1) && _win.Editing.Masks.Common.Get(2), "戻せなければマスクはそのまま");

            Stroke(3);
            Assert.IsTrue(_win.Editing.Masks.Common.Get(3));
            Undo.PerformUndo();
            Assert.IsFalse(_win.Editing.Masks.Common.Get(3), "その後の編集は取り消せる");
            Assert.IsTrue(_win.Editing.Masks.Common.Get(2));
        }

        [Test]
        public void ClosingTheWindow_ReturnsTheMaskHistory() {
            long before = IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask);
            var win = ScriptableObject.CreateInstance<IrocaWindow>();
            win.Editing.Masks.Reset(W, H);
            Undo.IncrementCurrentGroup();
            win._maskView.BeginStroke();
            win.Editing.Masks.WritableCommon().Set(1, true);
            win._maskView.EndStroke();
            Assert.Greater(IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask), before);
            Object.DestroyImmediate(win);
            Assert.AreEqual(before, IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask), "閉じたら履歴も返す");
        }

        [Test]
        public void ClosingTheWindow_DestroysItsDocument() {
            var win = ScriptableObject.CreateInstance<IrocaWindow>();
            var doc = win.UndoTarget;
            Assert.IsTrue(doc != null);
            Object.DestroyImmediate(win);
            Assert.IsTrue(doc == null, "閉じたら文書も手放す");
        }
    }
}
