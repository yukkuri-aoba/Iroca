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

        private bool[] Pixels(params int[] on)
        {
            var region = new bool[W * H];
            foreach (var i in on) region[i] = true;
            return region;
        }

        private void ApplyToMask(string zoneId, MaskRegionOp op, params int[] on)
        {
            Undo.IncrementCurrentGroup();
            Assert.IsTrue(_win._maskView.ApplyRegion(Pixels(on), new MaskPaintView.MaskDestination(zoneId, op), out _), op.ToString());
            Undo.IncrementCurrentGroup();
        }

        [Test]
        public void ClearHere_RemovesOnlyTheRegion_AndUndoBringsItBack()
        {
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
        public void ClearHere_WithNothingToClear_LeavesNoUndoStep()
        {
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
        public void ClearHere_WhereOnlyTheCommonMaskIs_SaysSo()
        {
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
        public void ClearHere_ToCommon_ClearsOnlyTheCommonMask()
        {
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
        public void ClearTarget_IsTheZoneWithAMaskHere_NotThePaintingZone()
        {
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
        public void Document_NeverHoldsMaskStrings()
        {
            Stroke(5);
            AddZones("服");
            Stroke(6);
            _win.SavePersistedSessionForCurrentTexture();

            string json = EditorJsonUtility.ToJson(_win.UndoTarget);
            StringAssert.DoesNotContain("R:", json, "Undo に載る文書にマスクの文字列が入った");
            Assert.IsEmpty(_win.Session.maskState.commonMaskBase64 ?? "");
        }

        [Test]
        public void Saving_WritesTheEditingMasks()
        {
            Stroke(5);
            Assert.IsTrue(_win.SavePersistedSessionForCurrentTexture());

            var saved = MaskFileStore.LoadMask(_tex, out bool unreadable);
            Assert.IsFalse(unreadable);
            Assert.IsNotNull(saved, "マスクがファイルに書かれていない");
            var fromFile = new MaskWorkspace();
            try
            {
                Assert.IsTrue(fromFile.DecodeFrom(saved));
                Assert.IsTrue(fromFile.Common.Get(5));
            }
            finally { fromFile.Clear(); }
        }

        [Test]
        public void Undo_OfAnEditWithoutMasks_DoesNotTouchTheMasks()
        {
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
        public void Undo_OfAVersionNoLongerInHistory_KeepsMasksAndEditingContinues()
        {
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
        public void ClosingTheWindow_ReturnsTheMaskHistory()
        {
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
