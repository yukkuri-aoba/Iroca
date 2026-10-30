// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// プリセットを編集状態へ写す規則(<see cref="PresetApplier"/>)と、UI の読み込み(<see cref="EditingSession.ApplyPreset"/>)。
    /// 古い優先順位の移行・id・マスクの写し方、読み込みが Undo 1 ステップでゾーン・マスクごと戻ること、
    /// マスクを読み込まないときは今のマスクを残すこと。古い版のファイルそのものは OldFormatLoadTests が見る。
    /// </summary>
    public class PresetApplierTests
    {
        private const int W = 6, H = 2;

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
            Object.DestroyImmediate(_host);
        }

        private static bool[] Bits(params int[] on)
        {
            var m = new bool[W * H];
            foreach (int i in on) m[i] = true;
            return m;
        }

        private static string Rle(bool[] m) => MaskRle.Encode(m, W, H);

        private static bool[] ToBools(NativeBitMask m)
        {
            var b = new bool[m.Length];
            for (int i = 0; i < b.Length; i++) b[i] = m.Get(i);
            return b;
        }

        private static ColorZone Zone(string name, int layer, string id = "") =>
            new ColorZone { name = name, layerIndex = layer, id = id };

        [Test]
        public void LegacyLayerPriority_SortsDescendingOnlyWhenTheyDiffer()
        {
            var zones = new List<ColorZone> { Zone("a", 0), Zone("b", 2), Zone("c", 1), Zone("d", 2) };
            PresetApplier.MigrateLegacyLayerPriority(zones);
            CollectionAssert.AreEqual(new[] { "b", "d", "c", "a" }, zones.ConvertAll(z => z.name), "大きい順・同値は元の並び");

            var same = new List<ColorZone> { Zone("x", 3), Zone("y", 3) };
            PresetApplier.MigrateLegacyLayerPriority(same);
            CollectionAssert.AreEqual(new[] { "x", "y" }, same.ConvertAll(z => z.name), "全部同じなら今の並び");
        }

        [Test]
        public void ApplyZonesAndSettings_GivesIdsAndKeepsEditMode()
        {
            var state = IrocaSessionState.CreateDefault();
            state.editMode = EditMode.Advanced;
            var preset = new IrocaPresetData
            {
                zones = { Zone("a", 0, "keep"), Zone("b", 0) },
                antiAliasCleanup = 0,
                holeFillPasses = 3,
            };
            PresetApplier.ApplyZonesAndSettings(preset, state);
            Assert.AreEqual("keep", state.zones[0].id, "書かれた id は残す");
            Assert.IsFalse(string.IsNullOrEmpty(state.zones[1].id), "無ければ振る");
            Assert.AreEqual(0, state.antiAliasCleanup, "明示した 0 も写す");
            Assert.AreEqual(3, state.holeFillPasses);
            Assert.AreEqual(EditMode.Advanced, state.editMode, "表示の好みは写さない");
        }

        [Test]
        public void MaskStateOf_CopiesEntriesAndSkipsBlankIds()
        {
            Assert.AreEqual(0, PresetApplier.MaskStateOf(new IrocaPresetData()).width, "寸法が無ければマスクなし");

            var preset = new IrocaPresetData
            {
                maskWidth = W, maskHeight = H,
                commonMaskBase64 = Rle(Bits(0)),
                zoneMasks = { new ZoneMaskEntry { zoneId = "z", maskBase64 = Rle(Bits(1)) }, new ZoneMaskEntry { zoneId = "" } },
                zoneIncludeMasks = { new ZoneMaskEntry { zoneId = "z", maskBase64 = null } },
            };
            var ms = PresetApplier.MaskStateOf(preset);
            Assert.AreEqual(W, ms.width);
            Assert.AreEqual(preset.commonMaskBase64, ms.commonMaskBase64);
            Assert.AreEqual(1, ms.zones.Count, "id の無い項目は捨てる");
            Assert.AreEqual("z", ms.zones[0].zoneId);
            Assert.AreEqual("", ms.zoneIncludes[0].maskBase64, "null は空にする(展開で読み飛ばされる)");
        }

        private IrocaPresetData PresetWithMask(out string zoneId, out bool[] mask)
        {
            var z = Zone("p", 0);
            z.EnsureId();
            zoneId = z.id;
            mask = Bits(2, 3);
            return new IrocaPresetData
            {
                zones = { z },
                maskWidth = W, maskHeight = H,
                zoneMasks = { new ZoneMaskEntry { zoneId = z.id, maskBase64 = Rle(mask) } },
            };
        }

        [Test]
        public void ApplyPreset_ReplacesZonesAndMasksAsOneUndoStep()
        {
            _editing.AddZone();
            string before = _host.session.zones[0].id;
            var common = Bits(5);
            _editing.Masks.Reset(W, H);
            _editing.Masks.SetCommon(NativeBitMask.FromBools(common, W, H));
            var preset = PresetWithMask(out string zoneId, out bool[] mask);

            var got = new List<EditChange>();
            _editing.Changed += got.Add;
            Undo.IncrementCurrentGroup();
            Assert.IsTrue(_editing.ApplyPreset(preset, applyMasks: true));
            Undo.IncrementCurrentGroup();
            CollectionAssert.AreEqual(new[] { EditChange.Replaced }, got);
            Assert.AreEqual(zoneId, _host.session.zones[0].id);
            CollectionAssert.AreEqual(mask, ToBools(_editing.Masks.Layer(false, zoneId)));
            Assert.IsNull(_editing.Masks.Common, "マスクごと入れ替わる");

            Undo.PerformUndo();
            Assert.AreEqual(before, _host.session.zones[0].id, "ゾーンが戻る");
            Assert.IsTrue(_editing.Masks.DecodeFrom(_host.session.maskState));
            CollectionAssert.AreEqual(common, ToBools(_editing.Masks.Common), "マスクも戻る");
            Assert.IsNull(_editing.Masks.Layer(false, zoneId));
        }

        [Test]
        public void ApplyPreset_WithoutMasksKeepsCurrentMasks()
        {
            var common = Bits(1, 4);
            _editing.Masks.Reset(W, H);
            _editing.Masks.SetCommon(NativeBitMask.FromBools(common, W, H));
            var preset = PresetWithMask(out string zoneId, out _);

            Assert.IsFalse(_editing.ApplyPreset(preset, applyMasks: false));
            Assert.AreEqual(zoneId, _host.session.zones[0].id, "ゾーンは読み込む");
            CollectionAssert.AreEqual(common, ToBools(_editing.Masks.Common), "マスクは今のまま");
            Assert.IsNull(_editing.Masks.Layer(false, zoneId));
        }
    }
}
