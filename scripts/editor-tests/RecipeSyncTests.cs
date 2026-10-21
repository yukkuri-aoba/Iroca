// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// いろかの窓が、外(git の pull・バックアップからの復元など)で書き換わったレシピを古い内容で上書きしないこと。
    /// 外からの変更は、Unity が .asset を取り込み直してレシピの sessionJson が新しい文字列になるのと同じ形
    /// (RecipeStore を通さない代入)で再現する。窓自身の保存と、Undo でレシピが前の中身へ戻ったときは
    /// 外からの変更と取り違えない(編集が消えない)こと。
    /// </summary>
    public class RecipeSyncTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        private TestAssets _assets;
        private string _tex;
        private string _guid;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaRecipe _recipe;
        private IrocaWindow _win;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _tex = _assets.WritePng("tex", TestAssets.Solid(4, 4, new Color32(200, 50, 50, 255)), 4, 4);
            _guid = AssetDatabase.AssetPathToGUID(_tex);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_tex);
            _recipe = RecipeStore.Create(texture, State("登録した内容"), _assets.Folder);

            // 窓は開いたときに「前回のテクスチャ」を読む。テスト用のテクスチャを指しておき、開発者の値は TearDown で戻す。
            _prefKey = (string)typeof(IrocaWindow)
                .GetProperty("LastTextureGuidPrefKey", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);

            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_recipe, _win.BoundRecipe, "テクスチャのレシピが 1 つなのに結び付いていない");
            Assert.AreEqual("登録した内容", ZoneName());
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
        public void OwnSaves_AreWrittenToTheRecipe()
        {
            SetZoneName("窓で直した内容");
            Save();
            Assert.AreEqual("窓で直した内容", RecipeZoneName());
            SetZoneName("もう一度直した内容");
            Save();
            Assert.AreEqual("もう一度直した内容", RecipeZoneName(), "窓自身の保存を外からの変更と取り違えた");
        }

        [Test]
        public void ExternalChange_IsNotOverwritten_AndIsReloaded()
        {
            SetZoneName("窓の古い内容");
            ChangeOutside("Git で取り込んだ内容");

            Save();   // 閉じる・再コンパイル・ビルドの直前・自動保存と同じ保存
            Assert.AreEqual("Git で取り込んだ内容", RecipeZoneName(), "外で書き換わったレシピを窓の古い内容で上書きした");

            Invoke("ReloadChangedRecipe");
            Assert.AreEqual("Git で取り込んだ内容", ZoneName(), "窓がレシピから読み直していない");

            // 読み直した後は、窓の編集がまたレシピに保存される。
            SetZoneName("読み直してから直した内容");
            Save();
            Assert.AreEqual("読み直してから直した内容", RecipeZoneName());
        }

        [Test]
        public void ReimportWithTheSameContent_IsNotAnExternalChange()
        {
            // 同じ中身で取り込み直された(文字列の参照だけが変わった)なら、窓の編集をそのまま保存する。
            _recipe.sessionJson = new string(_recipe.sessionJson.ToCharArray());
            SetZoneName("窓で直した内容");
            Save();
            Assert.AreEqual("窓で直した内容", RecipeZoneName());
        }

        [Test]
        public void RecipeRestoredByUndo_IsAdopted()
        {
            // 前に同期したことのある中身へレシピが戻った(自動化 API の色替えを Ctrl+Z した)なら、外からの変更ではない。
            string registered = _recipe.sessionJson;
            SetZoneName("窓で直した内容");
            Save();
            _recipe.sessionJson = new string(registered.ToCharArray());

            Invoke("AdoptRecipeAfterUndo");
            SetZoneName("Undo の後に直した内容");
            Save();
            Assert.AreEqual("Undo の後に直した内容", RecipeZoneName(), "Undo で戻ったレシピを外からの変更と取り違えた");
        }

        private static IrocaSessionState State(string zoneName)
        {
            var s = IrocaSessionState.CreateDefault();
            s.zones.Add(new ColorZone { name = zoneName });
            return s;
        }

        private void ChangeOutside(string zoneName) => _recipe.sessionJson = JsonUtility.ToJson(State(zoneName));

        private string ZoneName() => _win.Session.zones[0].name;

        private void SetZoneName(string name) => _win.Session.zones[0].name = name;

        private string RecipeZoneName() => RecipeStore.Load(_recipe).zones[0].name;

        private void Save() => Invoke("SavePersistedSessionForCurrentTexture");

        private void Invoke(string method) => typeof(IrocaWindow).GetMethod(method, Instance).Invoke(_win, null);
    }
}
