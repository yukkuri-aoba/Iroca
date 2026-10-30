// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 編集中の文書(<see cref="EditingSession"/>)のゾーンの操作。1 回の操作が Undo 1 ステップで戻ること、ゾーンを
    /// 消すとそのマスクも消えて、戻すとゾーンとマスクがそろって戻ること、新しいゾーンの名前と初期値。
    /// Undo の書き戻しは Unity のシリアライザが行うので、実機で見る(文書の中身をそのまま Undo の対象にする)。
    /// </summary>
    public class EditingSessionZoneTests
    {
        private const int W = 5, H = 3;

        private IrocaEditingDocument _host;
        private EditingSession _editing;

        [SetUp]
        public void SetUp()
        {
            _host = ScriptableObject.CreateInstance<IrocaEditingDocument>();
            _editing = new EditingSession(_host);
            Undo.ClearUndo(_host);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearUndo(_host);
            _editing.Masks.Clear();
            _editing.MaskHistory.Clear();
            Object.DestroyImmediate(_host);
        }

        private System.Collections.Generic.List<ColorZone> Zones => _host.session.zones;

        // 操作ごとに Undo グループを分ける(同じグループだと 1 ステップに合流する)。
        private T Step<T>(System.Func<T> op)
        {
            Undo.IncrementCurrentGroup();
            T r = op();
            Undo.IncrementCurrentGroup();
            return r;
        }

        private static string NumberedName(int n) => string.Format(Localization.NewZoneNameFormat, n);

        private static bool[] Pattern(int seed)
        {
            var m = new bool[W * H];
            for (int i = 0; i < m.Length; i++) m[i] = (i * 7 + seed) % 3 == 0;
            return m;
        }

        private static bool[] ToBools(NativeBitMask m)
        {
            var b = new bool[m.Length];
            for (int i = 0; i < b.Length; i++) b[i] = m.Get(i);
            return b;
        }

        [Test]
        public void NewZone_TakesSmallestFreeNumberAndStartsTuned()
        {
            Zones.Add(new ColorZone { name = NumberedName(1) });
            Zones.Add(new ColorZone { name = NumberedName(3) });
            var z = EditingSession.NewZone(Zones);
            Assert.AreEqual(NumberedName(2), z.name, "空いている最小の番号");
            Assert.AreEqual(EditingSession.NewZoneInitialTolerance, z.tolerance);
            Assert.IsFalse(string.IsNullOrEmpty(z.id));
            Assert.AreEqual(2, Zones.Count, "リストには足さない");
            Assert.AreEqual("服", EditingSession.NewZone(Zones, "服").name, "名前を渡せばそれを使う");
        }

        [Test]
        public void AddZoneFromSample_IsOneUndoStep()
        {
            var c = new Color(0.8f, 0.2f, 0.2f);
            var uv = new Vector2(0.25f, 0.75f);
            int index = Step(() => _editing.AddZoneFromSample(c, uv));
            Assert.AreEqual(0, index);
            Assert.AreEqual(c, Zones[0].sampleColor);
            Assert.IsTrue(Zones[0].sampleColorSet);
            Assert.AreEqual(uv, Zones[0].sampleUV);

            Undo.PerformUndo();
            Assert.AreEqual(0, Zones.Count, "戻すとゾーンごと消える");
            Undo.PerformRedo();
            Assert.AreEqual(1, Zones.Count);
            Assert.AreEqual(uv, Zones[0].sampleUV, "やり直すと色と位置もそろって戻る");
        }

        [Test]
        public void RemoveZone_DropsItsMasksAndUndoBringsBothBack()
        {
            Step(() => _editing.AddZone());
            Step(() => _editing.AddZone());
            string gone = Zones[0].id, kept = Zones[1].id;
            bool[] common = Pattern(0), goneEx = Pattern(1), goneIn = Pattern(2), keptEx = Pattern(3);
            _editing.Masks.Reset(W, H);
            _editing.Masks.SetCommon(NativeBitMask.FromBools(common, W, H));
            _editing.Masks.SetLayer(false, gone, NativeBitMask.FromBools(goneEx, W, H));
            _editing.Masks.SetLayer(true, gone, NativeBitMask.FromBools(goneIn, W, H));
            _editing.Masks.SetLayer(false, kept, NativeBitMask.FromBools(keptEx, W, H));

            Assert.IsTrue(Step(() => _editing.RemoveZoneAt(0)));
            Assert.AreEqual(1, Zones.Count);
            Assert.AreEqual(kept, Zones[0].id);
            Assert.IsNull(_editing.Masks.Layer(false, gone), "消したゾーンの除外も消える");
            Assert.IsNull(_editing.Masks.Layer(true, gone), "含めるも消える");
            Assert.IsFalse(_editing.ToSaveState().maskState.zones.Exists(e => e.zoneId == gone), "保存形式にも残さない");
            CollectionAssert.AreEqual(keptEx, ToBools(_editing.Masks.Layer(false, kept)), "残るゾーンのマスクはそのまま");

            Undo.PerformUndo();
            Assert.AreEqual(2, Zones.Count);
            Assert.AreEqual(gone, Zones[0].id, "同じ位置へ戻る");
            // ウィンドウは Undo の後に履歴から戻す(IrocaWindow.OnUndoRedoPerformed)。
            Assert.IsTrue(_editing.RestoreMasksAfterUndo());
            CollectionAssert.AreEqual(goneEx, ToBools(_editing.Masks.Layer(false, gone)), "マスクもそろって戻る");
            CollectionAssert.AreEqual(goneIn, ToBools(_editing.Masks.Layer(true, gone)));
            CollectionAssert.AreEqual(common, ToBools(_editing.Masks.Common));
        }

        [Test]
        public void RemoveZone_OutOfRangeDoesNothing()
        {
            Assert.IsFalse(_editing.RemoveZoneAt(0));
            Step(() => _editing.AddZone());
            Assert.IsFalse(_editing.RemoveZoneAt(1));
            Assert.IsFalse(_editing.RemoveZoneAt(-1));
            Assert.AreEqual(1, Zones.Count);
        }

        [Test]
        public void MoveZone_ReordersAsOneUndoStep()
        {
            for (int i = 0; i < 3; i++) Step(() => _editing.AddZone());
            string a = Zones[0].id, b = Zones[1].id, c = Zones[2].id;
            Assert.IsFalse(_editing.MoveZone(1, 1), "同じ位置");
            Assert.IsFalse(_editing.MoveZone(0, 3), "範囲外");

            Assert.IsTrue(Step(() => _editing.MoveZone(0, 2)));
            CollectionAssert.AreEqual(new[] { b, c, a }, Zones.ConvertAll(z => z.id));
            Undo.PerformUndo();
            CollectionAssert.AreEqual(new[] { a, b, c }, Zones.ConvertAll(z => z.id));
        }

        [Test]
        public void FloodFillSeed_SetOnlyWhenChangedAndUndoable()
        {
            Step(() => _editing.AddZone());
            var seed = new Vector2(0.4f, 0.6f);
            Assert.IsTrue(Step(() => _editing.SetFloodFillSeed(Zones[0], seed)));
            Assert.IsFalse(_editing.SetFloodFillSeed(Zones[0], seed), "同じ位置なら何もしない");
            Step(() => { _editing.ClearFloodFillSeed(Zones[0]); return 0; });
            Assert.Less(Zones[0].seedUV.x, 0f);

            Undo.PerformUndo();
            Assert.AreEqual(seed, Zones[0].seedUV, "外したのを戻す");
            Undo.PerformUndo();
            Assert.Less(Zones[0].seedUV.x, 0f, "置く前へ戻る");
        }

        [Test]
        public void Operations_NotifyContentOnceAndOnlyWhenSomethingChanged()
        {
            var got = new System.Collections.Generic.List<EditChange>();
            _editing.Changed += got.Add;
            int Count() { int n = got.Count; got.Clear(); return n; }

            _editing.AddZone();
            _editing.AddZoneFromSample(Color.red, new Vector2(0.1f, 0.1f));
            CollectionAssert.AreEqual(new[] { EditChange.Content, EditChange.Content }, got);
            got.Clear();

            Assert.IsFalse(_editing.MoveZone(0, 0));
            Assert.IsFalse(_editing.RemoveZoneAt(5));
            Assert.IsFalse(_editing.ApplySample(Zones[1], Color.red, new Vector2(0.1f, 0.1f)), "同じ色・同じ位置");
            Assert.IsFalse(_editing.SetFloodFillSeed(Zones[0], Zones[0].seedUV));
            Assert.AreEqual(0, Count(), "何も変えなければ知らせない");

            Assert.IsTrue(_editing.MoveZone(0, 1));
            Assert.IsTrue(_editing.ApplySample(Zones[0], Color.blue, new Vector2(0.2f, 0.2f)));
            Assert.IsTrue(_editing.SetFloodFillSeed(Zones[0], new Vector2(0.3f, 0.3f)));
            _editing.ClearFloodFillSeed(Zones[0]);
            _editing.ResetZoneTuning(Zones[0]);
            Assert.IsTrue(_editing.RemoveZoneAt(0));
            Assert.AreEqual(6, got.Count, "1 回の操作で 1 回");
            CollectionAssert.AreEqual(new[] { EditChange.Content }, got.Distinct());
        }

        [Test]
        public void Autosave_IsScheduledByChangesUntilStopped()
        {
            var autosave = new AutosaveSchedule(() => false, () => { });
            autosave.Start(_editing);
            Assert.IsFalse(autosave.IsPending);
            _editing.NotifyChanged(EditChange.Masks);
            Assert.IsTrue(autosave.IsPending, "編集で予定が立つ");
            autosave.Stop();
            Assert.IsFalse(autosave.IsPending, "やめると予定も消える");
            _editing.NotifyChanged(EditChange.Content);
            Assert.IsFalse(autosave.IsPending, "やめた後の編集は見ない");
        }

        [Test]
        public void ResetZoneTuning_KeepsChoicesAndIsUndoable()
        {
            Step(() => _editing.AddZone());
            var z = Zones[0];
            z.tolerance = 0.35f;
            z.valueBlend = 0.3f;
            Step(() => { _editing.ResetZoneTuning(Zones[0]); return 0; });
            Assert.AreEqual(new ColorZone().valueBlend, Zones[0].valueBlend);
            Assert.AreEqual(0.35f, Zones[0].tolerance, "許容範囲は利用者の選択なので残す");

            Undo.PerformUndo();
            Assert.AreEqual(0.3f, Zones[0].valueBlend);
        }
    }
}
