// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// ウィンドウの Undo/Redo が編集内容だけを戻すこと(0.4.0 のフェーズ 5)。Undo の対象は文書の中身で、ウィンドウではない。
    /// ズーム・前後比較・ブラシの大きさ・開閉などは巻き戻らず、いま直しているゾーンはゾーンの並びが戻っても同じゾーンを指す。
    /// 保存の控え(古い版のレイアウト・ドメインリロードの読み直し)から文書を作り直せること。
    /// </summary>
    public class WindowUndoTests
    {
        private const int W = 4, H = 4;

        private TestAssets _assets;
        private string _tex;
        private string _guid;
        private string _prefKey;
        private string _prefBefore;
        private IrocaWindow _win;

        [SetUp]
        public void SetUp()
        {
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
        public void TearDown()
        {
            Undo.ClearAll();
            if (_win != null) Object.DestroyImmediate(_win);
            if (_prefBefore == null) EditorPrefs.DeleteKey(_prefKey);
            else EditorPrefs.SetString(_prefKey, _prefBefore);
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) File.Delete(f);
            }
            _assets.Dispose();
        }

        private void Stroke(int pixel)
        {
            Undo.IncrementCurrentGroup();
            _win._maskView.BeginStroke();
            _win.Editing.Masks.WritableCommon().Set(pixel, true);
            _win._maskView.EndStroke();
            _win.Editing.NotifyChanged(EditChange.Masks);
            Undo.IncrementCurrentGroup();
        }

        private void AddZones(params string[] names)
        {
            foreach (var name in names)
            {
                Undo.IncrementCurrentGroup();
                int i = _win.Editing.AddZone();
                _win.Session.zones[i].name = name;
                Undo.IncrementCurrentGroup();
            }
        }

        [Test]
        public void Undo_RestoresMasksButNotTheView()
        {
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

        [Test]
        public void Undo_OfSliderEdit_RestoresTheValueAndAsksForAPreview()
        {
            AddZones("服");
            float before = _win.Session.zones[0].tolerance;
            int replaced = 0;
            _win.Editing.Changed += c => { if (c == EditChange.Replaced) replaced++; };

            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_win.UndoTarget, "Iroca Edit");
            _win.Session.zones[0].tolerance = before + 0.25f;
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreEqual(before, _win.Session.zones[0].tolerance, "ウィンドウから読む値も戻る");
            Assert.AreEqual(1, replaced, "編集が戻ったことを知らせる(プレビューの作り直し・重ね表示・自動保存)");
        }

        [Test]
        public void UnrelatedUndo_DoesNotRebuildThePreview()
        {
            var other = ScriptableObject.CreateInstance<SampleUvUndoHost>();
            try
            {
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
            }
            finally { Object.DestroyImmediate(other); }
        }

        [Test]
        public void ActiveTarget_FollowsItsZoneWhenUndoChangesTheOrder()
        {
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
        public void ActiveTarget_FallsBackWhenUndoRemovesItsZone()
        {
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
        public void ActiveTarget_CommonStaysCommon()
        {
            AddZones("a");
            var masks = _win._maskView;
            masks.activeMaskTarget = -1;
            masks.RememberActiveTarget();
            Undo.PerformUndo();
            Assert.AreEqual(-1, masks.activeMaskTarget);
        }

        [Test]
        public void ChangeSourceTexture_UndoBringsBackTextureAndZones()
        {
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
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, otherGuid + "*")) File.Delete(f);
            }
        }

        [Test]
        public void SavedForm_KeepsTheOldFieldNames()
        {
            AddZones("控えに残る");
            // 0.3.x に戻したとき・文書を失ったときに読む控えは、今の中身を指している。
            string json = EditorJsonUtility.ToJson(_win);
            StringAssert.Contains("\"_session\"", json);
            StringAssert.Contains("控えに残る", json);
            StringAssert.Contains("\"sourceTexture\"", json);
            StringAssert.Contains("\"_boundRecipe\"", json);
        }

        [Test]
        public void MissingDocument_IsRebuiltFromTheSavedForm()
        {
            AddZones("控えから戻す");
            string json = EditorJsonUtility.ToJson(_win);
            var texture = _win.SourceTexture;

            // 0.3.x のレイアウト(文書が無く、控えだけがある)を読んだ状態を作る。
            var old = ScriptableObject.CreateInstance<IrocaWindow>();
            try
            {
                Object.DestroyImmediate(old.UndoTarget);
                EditorJsonUtility.FromJsonOverwrite(json.Replace("\"_document\"", "\"_documentGone\""), old);
                old.OnEnable();
                Assert.AreSame(texture, old.SourceTexture, "テクスチャは控えから");
                Assert.AreEqual(1, old.Session.zones.Count);
                Assert.AreEqual("控えから戻す", old.Session.zones[0].name, "ゾーンは控えから");
                Assert.AreEqual(HideFlags.HideAndDontSave, old.UndoTarget.hideFlags, "新しい文書を作る");
                Assert.AreNotSame(_win.UndoTarget, old.UndoTarget);
            }
            finally { Object.DestroyImmediate(old); }
        }

        [Test]
        public void ClosingTheWindow_DestroysItsDocument()
        {
            var win = ScriptableObject.CreateInstance<IrocaWindow>();
            var doc = win.UndoTarget;
            Assert.IsTrue(doc != null);
            Object.DestroyImmediate(win);
            Assert.IsTrue(doc == null, "閉じたら文書も手放す");
        }
    }
}
