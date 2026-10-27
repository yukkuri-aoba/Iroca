// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// いろかのウィンドウが Unity の都合で状態を失ったり、古い状態に戻ったりしないこと。
    /// <list type="bullet">
    /// <item>プレビュー・オーバーレイの一時テクスチャがシーンを開いても破棄されない
    /// (破棄されると、次に編集するまでプレビューが空になっていた)。</item>
    /// <item>Unity が落ちた後の起動では、レイアウトから戻った古い編集内容ではなく、自動保存したディスクの
    /// 内容を読む(Unity はレイアウトを正常終了のときにしか書かないので、レイアウトには最後に正常終了した
    /// ときの内容が入っている)。ドメインリロードではメモリ上の内容をそのまま使う。</item>
    /// </list>
    /// Unity の再起動はテストの中ではできないので、「起動後の最初の有効化か」の印(SessionState)を消してから
    /// OnEnable を呼び直して、レイアウトからの復元を再現する。
    /// </summary>
    public class WindowRestoreTests
    {
        private TestAssets _assets;
        private string _tex;
        private string _guid;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaWindow _win;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _tex = _assets.WritePng("tex", TestAssets.Solid(4, 4, new Color32(200, 50, 50, 255)), 4, 4);
            _guid = AssetDatabase.AssetPathToGUID(_tex);
            Assert.IsNotEmpty(_guid);

            // ウィンドウは開いたときに「前回のテクスチャ」を読むので、テスト用のテクスチャを指しておく。
            // 開発者の実際の値は TearDown で戻す。
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
        }

        [TearDown]
        public void TearDown()
        {
            if (_win != null) Object.DestroyImmediate(_win);
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
        public void PreviewTexture_SurvivesOpeningAScene()
        {
            Texture2D tex = null;
            try
            {
                TextureSlot.Resize(ref tex, 4, 4);
                // 開いているシーンはテストランナーが実行後に開き直す。
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.IsTrue(tex != null, "シーンを開いたら一時テクスチャが破棄された");
            }
            finally { TextureSlot.Release(ref tex); }
            Assert.IsTrue(ReferenceEquals(tex, null), "Release で手放していない");
        }

        [Test]
        public void FirstEnableAfterEditorStart_ReadsSavedSessionNotLayout()
        {
            SaveSessionWithZoneName("自動保存した内容");
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreEqual("自動保存した内容", ZoneName(), "新しく開いたウィンドウが保存内容を読んでいない");

            // 落ちた後の起動: レイアウトから最後に正常終了したときの古い内容が戻り、起動後の印は無い。
            SetZoneName("最後に正常終了したときの内容");
            SessionState.EraseBool(EnabledKey());
            CallOnEnable();

            Assert.AreEqual("自動保存した内容", ZoneName(), "レイアウトの古い内容を使っている");
        }

        [Test]
        public void DomainReload_KeepsInMemorySession()
        {
            SaveSessionWithZoneName("ディスクの内容");
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            SetZoneName("メモリ上の内容");

            // ドメインリロード: 起動後の印は残っている。
            CallOnEnable();

            Assert.AreEqual("メモリ上の内容", ZoneName(), "ドメインリロードでディスクから読み直した");
        }

        private void SaveSessionWithZoneName(string name)
        {
            var s = IrocaSessionState.CreateDefault();
            s.zones.Add(new ColorZone { name = name });
            Assert.IsTrue(SessionFileStore.SaveSession(_tex, s));
        }

        private string ZoneName()
        {
            var zones = _win.Session.zones;
            Assert.AreEqual(1, zones.Count);
            return zones[0].name;
        }

        private void SetZoneName(string name) => _win.Session.zones[0].name = name;

        private string EnabledKey() => _win.EnabledSinceEditorStartKey;

        private void CallOnEnable() => _win.OnEnable();
    }
}
