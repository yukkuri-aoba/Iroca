// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;

namespace Iroca.EditorTests {
    /// <summary>
    /// 編集中のマスク(<see cref="MaskWorkspace"/>。ヒープの外の 1 画素 1 ビット)が、前の bool[] の経路
    /// (<see cref="MaskStateCodec"/>・<see cref="MaskSnapshot.FromBuffers"/>)と同じ保存形式・同じスナップショットを作ること、
    /// 中身が同じ層を使い回して残りを手放すこと、ほかに持ち主がいる層は書く前に写すこと。
    /// Application の層なのでハーネスの自己検査には入らない。ここで見る。
    /// </summary>
    public class MaskWorkspaceTests {
        private const int W = 37, H = 11;   // 語(64 画素)の境目をまたぐ寸法

        private static bool[] Random(int seed, double density) {
            var rng = new System.Random(seed);
            var m = new bool[W * H];
            for (int i = 0; i < m.Length; i++) {
                m[i] = rng.NextDouble() < density;
            }
            return m;
        }

        private static bool[] ToBools(NativeBitMask m) {
            var b = new bool[m.Length];
            for (int i = 0; i < b.Length; i++) {
                b[i] = m.Get(i);
            }
            return b;
        }

        // 共通 1・ゾーンの除外 2(1 つは全部 0)・含める 2(1 つは全部 0)を、両方の経路に同じく置く。
        private static MaskWorkspace Fill(out bool[] common, out Dictionary<string, bool[]> zones,
                                          out Dictionary<string, bool[]> includes) {
            common = Random(1, 0.3);
            zones = new Dictionary<string, bool[]> { ["za"] = Random(2, 0.5), ["zb"] = new bool[W * H] };
            includes = new Dictionary<string, bool[]> { ["za"] = new bool[W * H], ["zc"] = Random(3, 0.1) };
            var ws = new MaskWorkspace();
            ws.Reset(W, H);
            ws.SetCommon(NativeBitMask.FromBools(common, W, H));
            foreach (var kv in zones) {
                ws.SetLayer(false, kv.Key, NativeBitMask.FromBools(kv.Value, W, H));
            }
            foreach (var kv in includes) {
                ws.SetLayer(true, kv.Key, NativeBitMask.FromBools(kv.Value, W, H));
            }
            return ws;
        }

        private static string Json(MaskState ms) => UnityEngine.JsonUtility.ToJson(ms);

        [Test]
        public void EncodeTo_MatchesBoolCodec() {
            var ws = Fill(out var common, out var zones, out var includes);
            var a = new MaskState();
            ws.EncodeTo(a);
            var b = new MaskState();
            MaskStateCodec.Encode(b, W, H, common, zones, includes);
            // 版は編集中の文書の保存だけが刻む(レシピの中身を保存ごとにそろえるため)。
            b.schemaVersion = MaskState.CurrentSchemaVersion;
            Assert.AreEqual(Json(b), Json(a), "同じ文字列・同じ項目の並び(全部 0 の層は書かない)");
            ws.Clear();
        }

        [Test]
        public void DecodeFrom_MatchesBoolCodec_AndSkipsBrokenEntries() {
            var ws = Fill(out var common, out var zones, out var includes);
            var ms = new MaskState();
            MaskStateCodec.Encode(ms, W, H, common, zones, includes);
            // 壊れた項目・寸法の合わない項目・id の無い項目・同じ id の 2 度目(後が勝つ)を足す。
            ms.zones.Add(new MaskZoneEntry { zoneId = "broken", maskBase64 = "R:###" });
            ms.zones.Add(new MaskZoneEntry { zoneId = "small", maskBase64 = MaskRle.Encode(new bool[4] { true, false, true, false }, 2, 2) });
            ms.zones.Add(new MaskZoneEntry { zoneId = "", maskBase64 = ms.zones[0].maskBase64 });
            var later = Random(9, 0.4);
            ms.zones.Add(new MaskZoneEntry { zoneId = "za", maskBase64 = MaskRle.Encode(later, W, H) });

            var fresh = new MaskWorkspace();
            Assert.IsTrue(fresh.DecodeFrom(ms));
            var dz = new Dictionary<string, bool[]>();
            var di = new Dictionary<string, bool[]>();
            Assert.IsTrue(MaskStateCodec.Decode(ms, out int w, out int h, out bool[] dc, dz, di));
            Assert.AreEqual(w, fresh.Width);
            Assert.AreEqual(h, fresh.Height);
            CollectionAssert.AreEqual(dc, ToBools(fresh.Common));
            var gotZones = new List<string>();
            foreach (var kv in fresh.Layers(false)) { gotZones.Add(kv.Key); CollectionAssert.AreEqual(dz[kv.Key], ToBools(kv.Value), kv.Key); }
            CollectionAssert.AreEqual(new List<string>(dz.Keys), gotZones, "同じ層・同じ並び");
            CollectionAssert.AreEqual(later, ToBools(fresh.Layer(false, "za")), "同じ id は後の項目が勝つ");
            foreach (var kv in fresh.Layers(true)) {
                CollectionAssert.AreEqual(di[kv.Key], ToBools(kv.Value), kv.Key);
            }
            Assert.AreEqual(di.Count, new List<KeyValuePair<string, NativeBitMask>>(fresh.Layers(true)).Count);
            // 非破壊ビルド・自動化の読み方(MaskStateCodec.ToSnapshot)も、同じ項目を読み飛ばして同じ層になる(読み飛ばしの規則は
            // Core の MaskStateCodec.ReadLayers の 1 つで、編集中のマスクと共有)。
            var snap = MaskStateCodec.ToSnapshot(ms);
            try {
                CollectionAssert.AreEqual(dc, ToBools(snap.common));
                CollectionAssert.AreEquivalent(new List<string>(dz.Keys), new List<string>(snap.zones.Keys));
                foreach (var kv in snap.zones) {
                    CollectionAssert.AreEqual(dz[kv.Key], ToBools(kv.Value), kv.Key);
                }
            } finally { snap.Release(); }
            fresh.Clear();
            ws.Clear();
        }

        // 読み飛ばした項目の数を返す(読んだ側がファイルを退避するため)。文字列が空の項目は層が無いだけなので数えない。
        [Test]
        public void DecodeFrom_CountsTheEntriesItSkips() {
            var ws = Fill(out _, out _, out _);
            var ms = new MaskState();
            ws.EncodeTo(ms);
            var fresh = new MaskWorkspace();
            try {
                Assert.IsTrue(fresh.DecodeFrom(ms, out int dropped));
                Assert.AreEqual(0, dropped, "全部読めた");

                ms.zones.Add(new MaskZoneEntry { zoneId = "broken", maskBase64 = "R:###" });
                ms.zoneIncludes.Add(new MaskZoneEntry { zoneId = "small", maskBase64 = MaskRle.Encode(new bool[4] { true, false, true, false }, 2, 2) });
                ms.zones.Add(new MaskZoneEntry { zoneId = "", maskBase64 = ms.zones[0].maskBase64 });
                ms.zones.Add(new MaskZoneEntry { zoneId = "blank", maskBase64 = "" });
                Assert.IsTrue(fresh.DecodeFrom(ms, out dropped));
                Assert.AreEqual(3, dropped, "壊れた・寸法の合わない・id の無い項目");
                Assert.IsNotNull(fresh.Layer(false, "za"), "読める項目は読む");

                ms.commonMaskBase64 = "R:###";
                Assert.IsTrue(fresh.DecodeFrom(ms, out dropped));
                Assert.AreEqual(4, dropped, "共通の除外も数える");
                Assert.IsNull(fresh.Common);
            } finally {
                fresh.Clear();
                ws.Clear();
            }
        }

        [Test]
        public void DecodeFrom_InvalidSizeDropsLayersAndKeepsSize() {
            var ws = Fill(out _, out _, out _);
            var kept = ws.Common.AddRef();   // 手放されたかを見るため、持ち主をもう 1 つ足しておく
            Assert.IsFalse(ws.DecodeFrom(new MaskState { width = 0, height = 0 }));
            Assert.IsFalse(ws.HasLayers, "層はなくなる");
            Assert.AreEqual(W, ws.Width, "寸法はそのまま");
            Assert.AreEqual(1, kept.RefCount, "前の層は手放した");
            kept.Release();
        }

        [Test]
        public void DecodeFrom_ReusesUnchangedLayersAndReleasesTheRest() {
            var ws = Fill(out _, out _, out _);
            var ms = new MaskState();
            ws.EncodeTo(ms);
            var common = ws.Common;
            var za = ws.Layer(false, "za");
            var zc = ws.Layer(true, "zc");
            var watchZa = za.AddRef();
            // 1 枚だけ変える(Undo の書き戻しと同じ形)。
            ms.zones[0].maskBase64 = MaskRle.Encode(Random(7, 0.2), W, H);
            Assert.IsTrue(ws.DecodeFrom(ms));
            Assert.AreSame(common, ws.Common, "変わっていない共通は使い回す");
            Assert.AreSame(zc, ws.Layer(true, "zc"), "変わっていない含めるは使い回す");
            Assert.AreNotSame(za, ws.Layer(false, "za"), "変わった層は展開し直す");
            Assert.AreEqual(1, watchZa.RefCount, "前の層は手放した");
            Assert.AreEqual(1, ws.Common.RefCount);
            watchZa.Release();
            ws.Clear();
        }

        [Test]
        public void ToSnapshot_MatchesFromBuffers() {
            var ws = Fill(out var common, out var zones, out var includes);
            var a = ws.ToSnapshot();
            var b = MaskSnapshot.FromBuffers(W, H, common, zones, includes);
            Assert.AreEqual(b.width, a.width);
            Assert.AreEqual(b.height, a.height);
            CollectionAssert.AreEqual(b.common.ToWords(), a.common.ToWords());
            CollectionAssert.AreEquivalent(b.zones.Keys, a.zones.Keys, "全部 0 の除外も載る");
            foreach (var kv in b.zones) {
                CollectionAssert.AreEqual(kv.Value.ToWords(), a.zones[kv.Key].ToWords(), kv.Key);
            }
            CollectionAssert.AreEquivalent(b.zoneIncludes.Keys, a.zoneIncludes.Keys, "全部 0 の含めるは載らない");
            foreach (var kv in b.zoneIncludes) {
                CollectionAssert.AreEqual(kv.Value.ToWords(), a.zoneIncludes[kv.Key].ToWords(), kv.Key);
            }
            a.Release();
            b.Release();
            ws.Clear();
        }

        [Test]
        public void ToSnapshot_OfTheWindow_AndOfTheSavedForm_PassTheSameMasks() {
            // 窓は編集中のマスクから(ToSnapshot)、非破壊ビルドは保存形式から(MaskStateCodec.ToSnapshot)スナップショットを作る。
            // 違いは 1 つだけ: 窓は全部 0 の除外の層も載せ、保存形式は全部 0 の層を書かないので載らない。全部 0 の層は何も
            // 除外しないので、色替えの結果は同じ(下で画素を比べる)。
            var ws = Fill(out _, out var zones, out _);
            var window = ws.ToSnapshot();
            var ms = new MaskState();
            ws.EncodeTo(ms);
            var saved = MaskStateCodec.ToSnapshot(ms);
            try {
                Assert.AreEqual(window.width, saved.width);
                Assert.AreEqual(window.height, saved.height);
                CollectionAssert.AreEqual(window.common.ToWords(), saved.common.ToWords());
                foreach (var kv in window.zones) {
                    if (saved.zones.TryGetValue(kv.Key, out var s)) {
                        CollectionAssert.AreEqual(kv.Value.ToWords(), s.ToWords(), kv.Key);
                    } else {
                        Assert.IsFalse(kv.Value.Any(), $"{kv.Key}: 保存形式に無い除外の層は、全部 0 のものだけ");
                    }
                }
                foreach (var key in saved.zones.Keys) {
                    Assert.IsTrue(window.zones.ContainsKey(key), $"{key}: 保存形式にだけある除外の層");
                }
                CollectionAssert.AreEquivalent(window.zoneIncludes.Keys, saved.zoneIncludes.Keys);
                foreach (var kv in window.zoneIncludes) {
                    CollectionAssert.AreEqual(kv.Value.ToWords(), saved.zoneIncludes[kv.Key].ToWords(), kv.Key);
                }

                // 同じ画像・同じゾーンを両方のスナップショットで色替えして、画素が同じ。ゾーンの id はマスクの層の id に合わせる。
                var src = new UnityEngine.Color32[W * H];
                for (int i = 0; i < src.Length; i++) {
                    src[i] = (i / W) % 2 == 0 ? new UnityEngine.Color32(200, 40, 40, 255) : new UnityEngine.Color32(40, 90, 200, 255);
                }
                var list = new List<ColorZone>();
                foreach (var id in new[] { "za", "zb", "zc" }) {
                    var z = new ColorZone {
                        name = id, id = id, sampleColorSet = true, targetColorSet = true, tolerance = 0.3f,
                        sampleColor = id == "zb" ? new UnityEngine.Color(40 / 255f, 90 / 255f, 200 / 255f) : new UnityEngine.Color(200 / 255f, 40 / 255f, 40 / 255f),
                        targetColor = id == "za" ? new UnityEngine.Color(0.1f, 0.7f, 0.2f) : new UnityEngine.Color(0.9f, 0.8f, 0.1f),
                    };
                    list.Add(z);
                }
                var a = (UnityEngine.Color32[])src.Clone();
                var b = (UnityEngine.Color32[])src.Clone();
                PixelProcessor.ProcessPixelsArray(a, W, H, window, list, RecolorSettings.Default, default, null, null, src);
                PixelProcessor.ProcessPixelsArray(b, W, H, saved, list, RecolorSettings.Default, default, null, null, src);
                CollectionAssert.AreEqual(a, b, "窓のマスクと保存形式から作り直したマスクで、色替えの結果が違う");
                CollectionAssert.AreNotEqual(src, a, "色替えが空振り(テストの場面が効いていない)");
            } finally {
                window.Release();
                saved.Release();
                ws.Clear();
            }
        }

        [Test]
        public void MaskState_DropZonesExcept_CoversEveryListKeyedByZone() {
            // MaskState の「zoneId を持つ項目のリスト」を反射で全部拾い、消したゾーンの項目がどの欄にも残らないことを見る。
            // 欄を足して DropZonesExcept に足し忘れると、ここで落ちる(消したゾーンのマスクや記録がレシピに残り、同じ id の
            // ゾーンが戻ると黙って効き直す)。
            var ms = new MaskState();
            int lists = 0;
            foreach (var field in typeof(MaskState).GetFields()) {
                if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(List<>)) {
                    continue;
                }
                var entryType = field.FieldType.GetGenericArguments()[0];
                var zoneId = entryType.GetField("zoneId");
                if (zoneId == null) {
                    continue;
                }
                lists++;
                var list = (System.Collections.IList)field.GetValue(ms);
                foreach (var id in new[] { "keep", "drop", "keep", "gone" }) {
                    var entry = System.Activator.CreateInstance(entryType);
                    zoneId.SetValue(entry, id);
                    list.Add(entry);
                }
                list.Add(null);
            }
            Assert.GreaterOrEqual(lists, 3, "zoneId を持つ項目のリストが見つからない(欄の形が変わった?)");
            // 全ゾーン共通の除外の記録(zoneId が空)は残す。
            ms.patches.Add(new MaskPatchEntry { zoneId = "" });

            ms.DropZonesExcept(new HashSet<string> { "keep" });

            foreach (var field in typeof(MaskState).GetFields()) {
                if (!(field.GetValue(ms) is System.Collections.IList list) || !field.FieldType.IsGenericType) {
                    continue;
                }
                var zoneId = field.FieldType.GetGenericArguments()[0].GetField("zoneId");
                if (zoneId == null) {
                    continue;
                }
                int kept = 0;
                foreach (var entry in list) {
                    Assert.IsNotNull(entry, $"{field.Name}: null の項目が残った");
                    string id = (string)zoneId.GetValue(entry);
                    Assert.IsTrue(id == "keep" || (id == "" && field.Name == nameof(MaskState.patches)),
                        $"{field.Name}: 消したゾーン '{id}' の項目が残った");
                    if (id == "keep") {
                        kept++;
                    }
                }
                Assert.AreEqual(2, kept, $"{field.Name}: 残すゾーンの項目が減った");
            }
            Assert.AreEqual(3, ms.patches.Count, "共通の除外の記録(zoneId が空)が消えた");
        }

        [Test]
        public void ToSnapshot_SharesLayers_AndWritersCopyFirst() {
            var ws = Fill(out _, out var zones, out _);
            string id = null;
            foreach (var kv in zones) { id = kv.Key; break; }
            var layer = ws.Layer(false, id);
            int refs = layer.RefCount;
            var snap = ws.ToSnapshot();
            Assert.AreSame(layer, snap.zones[id], "写さずに同じ層を指す");
            Assert.AreEqual(refs + 1, layer.RefCount);
            bool before = layer.Get(0);

            var writable = ws.WritableLayer(false, id);
            Assert.AreNotSame(snap.zones[id], writable, "読み手がいれば、書く前に写す");
            writable.Set(0, !before);
            Assert.AreEqual(before, snap.zones[id].Get(0), "スナップショットの中身は変わらない");

            snap.Release();
            Assert.AreEqual(1, ws.Layer(false, id).RefCount);
            Assert.AreSame(ws.Layer(false, id), ws.WritableLayer(false, id), "読み手がいなくなれば写さない");
            ws.Clear();
        }

        [Test]
        public void Rescale_IsNearestWithTruncation() {
            var ws = Fill(out var common, out _, out _);
            const int nw = 50, nh = 7;
            ws.Rescale(nw, nh);
            var got = ToBools(ws.Common);
            for (int y = 0; y < nh; y++) {
                for (int x = 0; x < nw; x++) {
                    Assert.AreEqual(common[(int)((long)y * H / nh) * W + (int)((long)x * W / nw)], got[y * nw + x], $"({x},{y})");
                }
            }
            Assert.AreEqual(nw, ws.Layer(false, "za").Width);
            ws.Clear();
        }

        [Test]
        public void WritableLayer_CopiesWhenShared() {
            var ws = Fill(out _, out var zones, out _);
            var shared = ws.Layer(false, "za").AddRef();   // 重ね表示のワーカーが読んでいる
            var writable = ws.WritableLayer(false, "za");
            Assert.AreNotSame(shared, writable, "ほかに持ち主がいれば写す");
            writable.Set(0, !writable.Get(0));
            CollectionAssert.AreEqual(zones["za"], ToBools(shared), "読んでいる側は変わらない");
            Assert.AreEqual(1, shared.RefCount, "編集側の参照は写しへ移った");
            shared.Release();
            Assert.AreSame(writable, ws.WritableLayer(false, "za"), "持ち主が 1 つなら写さない");
            ws.Clear();
        }

        // ─── 範囲の操作が塗った画素の記録(MaskPatch)と「ここのマスクを消す」 ───

        private const int PW = 8, PH = 4;

        private static MaskWorkspace Empty() {
            var ws = new MaskWorkspace();
            ws.Reset(PW, PH);
            return ws;
        }

        private static unsafe MaskRegionEditResult Apply(MaskWorkspace ws, string zoneId, MaskRegionOp op, params int[] on) {
            var region = new bool[ws.Width * ws.Height];
            foreach (int i in on) {
                region[i] = true;
            }
            fixed (bool* p = region) {
                Assert.IsTrue(ws.ApplyRegion(p, zoneId, op, null, out var result), op.ToString());
                return result;
            }
        }

        private static List<int> On(NativeBitMask m) {
            var got = new List<int>();
            for (int i = 0; m != null && i < m.Length; i++) {
                if (m.Get(i)) {
                    got.Add(i);
                }
            }
            return got;
        }

        private static List<int> On(MaskPatch p) {
            var m = p.Decode();
            try { return On(m); } finally { m.Release(); }
        }

        private static int Clear(MaskWorkspace ws, string zoneId, int pixel) {
            Assert.IsTrue(ws.ClearMaskAt(zoneId, pixel % ws.Width, pixel / ws.Width, 0, null, out var result));
            return result.changed;
        }

        [Test]
        public void ApplyRegion_RecordsThePixelsItTurnedOn() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 1, 20);
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 1, 2);            // 1 はもう立っている
            Apply(ws, "z", MaskRegionOp.PaintHere, 20, 21);              // 含めるに塗る(除外の 20 は消える)
            Apply(ws, null, MaskRegionOp.DontPaintHere, 30);             // 全ゾーン共通
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 1);               // 何も変えない操作は記録しない
            Apply(ws, "z", MaskRegionOp.ClearHere, 31);                  // 消す操作も記録しない

            Assert.AreEqual(4, ws.Patches.Count);
            CollectionAssert.AreEqual(new[] { 0, 1 }, On(ws.Patches[0]), "含めるに塗って除外から消えた 20 は、記録からも外れる");
            CollectionAssert.AreEqual(new[] { 2 }, On(ws.Patches[1]), "新しく立てた画素だけ");
            Assert.IsTrue(ws.Patches[2].Include && ws.Patches[2].ZoneId == "z");
            CollectionAssert.AreEqual(new[] { 20, 21 }, On(ws.Patches[2]));
            Assert.IsNull(ws.Patches[3].ZoneId, "共通の記録はゾーンなし");
            CollectionAssert.AreEqual(new[] { 30 }, On(ws.Patches[3]));
            ws.Clear();
        }

        [Test]
        public void ApplyRegion_OnlyThisPart_RecordsTheOutside() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.OnlyThisPart, 0, 1);
            Assert.AreEqual(1, ws.Patches.Count);
            Assert.AreEqual(PW * PH - 2, On(ws.Patches[0]).Count, "範囲の外の全部");
            Assert.AreEqual(PW * PH - 2, Clear(ws, "z", 5), "外側で消すと、外側の「塗らない」がまとめて消える");
            Assert.IsFalse(ws.Layer(false, "z").Any());
            ws.Clear();
        }

        [Test]
        public void ClearMaskAt_RemovesExactlyWhatTheOperationPainted() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 1, 20, 31);    // 離れた島と点を含む 1 回の操作
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 2, 3);            // 隣の部分(1 とつながる)

            Assert.AreEqual(4, Clear(ws, "z", 20), "離れた所を押しても、その操作の全部");
            CollectionAssert.AreEqual(new[] { 2, 3 }, On(ws.Layer(false, "z")), "別の操作の分は、つながっていても残る");
            Assert.AreEqual(1, ws.Patches.Count, "使った記録は外す");

            Assert.AreEqual(2, Clear(ws, "z", 3));
            Assert.AreEqual(0, ws.Patches.Count);
            Assert.AreEqual(0, Clear(ws, "z", 3), "もう無い");
            ws.Clear();
        }

        [Test]
        public void ClearMaskAt_UsesTheNewestOperationThatPaintedThePixel() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 1, 2);
            Apply(ws, "z", MaskRegionOp.PaintHere, 1);                   // 除外の 1 を消す
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 1, 9);            // 1 を塗り直す(含めるの 1 は消える)

            Assert.AreEqual(2, Clear(ws, "z", 1), "塗り直した操作の 1・9");
            CollectionAssert.AreEqual(new[] { 0, 2 }, On(ws.Layer(false, "z")));
            Assert.AreEqual(2, Clear(ws, "z", 0), "最初の操作の残り(1 はもう無い)");
            Assert.IsFalse(ws.Layer(false, "z").Any());
            ws.Clear();
        }

        [Test]
        public void ClearMaskAt_WithoutARecord_RemovesTheConnectedPiece() {
            var ws = Empty();
            var layer = ws.WritableLayer(false, "z");                    // ブラシで塗ったのと同じ(記録なし)
            foreach (int i in new[] { 0, 1, PW + 2, 31 }) {
                layer.Set(i, true);
            }
            Assert.AreEqual(3, Clear(ws, "z", 0), "斜めにつながる 3 画素");
            CollectionAssert.AreEqual(new[] { 31 }, On(ws.Layer(false, "z")));
            ws.Clear();
        }

        [Test]
        public void Patches_SurviveSaveAndLoad_ButNotWhenNothingOfThemIsLeft() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 1, 20);
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 2, 3);
            Apply(ws, "z", MaskRegionOp.PaintHere, 2, 3);                // 2 回目の操作の画素を全部消す → その記録は書かない
            Apply(ws, null, MaskRegionOp.DontPaintHere, 30);
            var ms = new MaskState();
            ws.EncodeTo(ms);
            Assert.AreEqual(3, ms.patches.Count, "除外 1・含める 1・共通 1(消えた操作の分は書かない)");
            Assert.AreEqual(MaskState.CurrentSchemaVersion, ms.schemaVersion);
            string json = Json(ms);

            var loaded = new MaskWorkspace();
            Assert.IsTrue(loaded.DecodeFrom(UnityEngine.JsonUtility.FromJson<MaskState>(json)));
            Assert.AreEqual(3, loaded.Patches.Count);
            var again = new MaskState();
            loaded.EncodeTo(again);
            Assert.AreEqual(json, Json(again), "読んで書くと同じ文字列");

            Assert.AreEqual(3, Clear(loaded, "z", 20), "開き直した後も、入れたときの範囲で消える");
            Assert.IsFalse(loaded.Layer(false, "z").Any());
            Assert.AreEqual(1, Clear(loaded, null, 30), "共通の除外も");
            ws.Clear();
            loaded.Clear();
        }

        [Test]
        public void DecodeFrom_WithoutPatches_OrWithBrokenOnes_KeepsTheMasks() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 1, 20);
            var ms = new MaskState();
            ws.EncodeTo(ms);
            ms.patches.Add(new MaskPatchEntry { zoneId = "z", include = false, maskBase64 = "R:###" });
            ms.patches.Add(new MaskPatchEntry { zoneId = "gone", include = false, maskBase64 = ms.patches[0].maskBase64 });
            ms.patches.Add(new MaskPatchEntry { zoneId = "z", include = false, maskBase64 = MaskRle.Encode(new bool[4] { true, false, true, false }, 2, 2) });
            ms.patches.Add(null);

            var loaded = new MaskWorkspace();
            Assert.IsTrue(loaded.DecodeFrom(ms, out int dropped));
            Assert.AreEqual(0, dropped, "記録が読めなくても、マスクの読み飛ばしには数えない");
            Assert.AreEqual(1, loaded.Patches.Count, "壊れた・層の無い・寸法の違う記録は捨てる");
            CollectionAssert.AreEqual(new[] { 0, 1, 20 }, On(loaded.Layer(false, "z")));

            ms.patches = null;                                           // 記録を持たない版の保存
            Assert.IsTrue(loaded.DecodeFrom(ms));
            Assert.AreEqual(0, loaded.Patches.Count);
            Assert.AreEqual(2, Clear(loaded, "z", 0), "記録が無ければ、つながったかたまり");
            ws.Clear();
            loaded.Clear();
        }

        // 塗った画素 on(PW × PH の中)の記録。
        private static MaskPatch PatchOf(params int[] on) {
            var m = NativeBitMask.Allocate(PW, PH);
            foreach (int i in on) {
                m.Set(i, true);
            }
            try { return MaskPatch.Painted("z", false, m, null); } finally { m.Release(); }
        }

        // 保存形式の RLE の文字列を、ヘッダとラン列から組み立てる(わざと崩した形も作れる)。
        private static string RleText(int w, int h, bool first, params uint[] runs) {
            var bytes = new byte[9 + runs.Length * 4];
            System.BitConverter.GetBytes(w).CopyTo(bytes, 0);
            System.BitConverter.GetBytes(h).CopyTo(bytes, 4);
            bytes[8] = first ? (byte)1 : (byte)0;
            for (int i = 0; i < runs.Length; i++) {
                System.BitConverter.GetBytes(runs[i]).CopyTo(bytes, 9 + i * 4);
            }
            return "R:" + System.Convert.ToBase64String(bytes);
        }

        // 旧 bitpack(前置きなし。1 画素 1 ビット)の文字列。
        private static string BitpackText(int w, int h, params int[] on) {
            var bytes = new byte[8 + (w * h + 7) / 8];
            System.BitConverter.GetBytes(w).CopyTo(bytes, 0);
            System.BitConverter.GetBytes(h).CopyTo(bytes, 4);
            foreach (int i in on) {
                bytes[8 + i / 8] |= (byte)(1 << (i % 8));
            }
            return System.Convert.ToBase64String(bytes);
        }

        [Test]
        public void PatchText_IsTheStringOfEncodingItsPixels() {
            var all = new int[PW * PH];
            for (int i = 0; i < all.Length; i++) {
                all[i] = i;
            }
            foreach (var on in new[] { new[] { 0 }, new[] { 31 }, new[] { 0, 1, 20, 31 }, new[] { 5, 6, 7, 9 }, all }) {
                var patch = PatchOf(on);
                var pixels = patch.Decode();
                Assert.AreEqual(MaskRle.EncodeBits(pixels), patch.Text, string.Join(",", on));
                pixels.Release();
                patch.Release();
            }
        }

        [Test]
        public void PatchFromText_KeepsTheBytesOfAWellFormedString_AndRebuildsTheOthers() {
            var patch = PatchOf(0, 1, 20, 31);
            string text = patch.Text;
            Assert.AreEqual(RleText(PW, PH, true, 2, 18, 1, 10, 1), text, "保存形式: 寸法・先頭の値・ランの長さ");

            // Text が書いた形は、バイト列をそのまま持つ(全面へ展開しない)。読んだ文字列をそのまま返す。
            var read = MaskPatch.FromText("z", false, text, PW, PH);
            Assert.IsNotNull(read);
            Assert.IsTrue(MaskPatch.SameRle(patch.Rle, read.Rle));
            Assert.AreSame(text, read.Text);
            read.Release();

            // 同じ画素を表す別の書き方(末尾のランが画素数を超える・長さ 0 のランがある・旧 bitpack)は、展開して作り直す。
            foreach (string other in new[] {
                         RleText(PW, PH, true, 2, 18, 1, 10, 5),
                         RleText(PW, PH, false, 0, 2, 18, 1, 10, 1),
                         BitpackText(PW, PH, 0, 1, 20, 31) }) {
                read = MaskPatch.FromText("z", false, other, PW, PH);
                Assert.IsNotNull(read, other);
                Assert.IsTrue(MaskPatch.SameRle(patch.Rle, read.Rle), other);
                Assert.AreEqual(text, read.Text, other);
                read.Release();
            }

            // 採らないもの: 寸法が違う・立っている画素が無い・ランが足りない・Base64 として読めない・空。
            Assert.IsNull(MaskPatch.FromText("z", false, RleText(PW + 1, PH, true, (uint)((PW + 1) * PH)), PW, PH), "寸法が違う");
            Assert.IsNull(MaskPatch.FromText("z", false, RleText(PW, PH, false, (uint)(PW * PH)), PW, PH), "全部 0");
            Assert.IsNull(MaskPatch.FromText("z", false, RleText(PW, PH, true, 2, 18), PW, PH), "ランが足りない");
            Assert.IsNull(MaskPatch.FromText("z", false, "R:###", PW, PH), "Base64 として読めない");
            Assert.IsNull(MaskPatch.FromText("z", false, "", PW, PH));
            patch.Release();
        }

        [Test]
        public void Patches_KeepOnlyThePixelsStillOnTheirLayer() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 1, 20);         // 記録 A
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 30, 31);           // 記録 B(離れた所)
            var before = ws.Patches[1];

            // ブラシで消したのと同じ(層を直接書く)。版の確定で呼ばれる TrimPatches が記録を層に合わせる。
            var layer = ws.WritableLayer(false, "z");
            layer.Set(1, false);
            ws.TrimPatches();
            CollectionAssert.AreEqual(new[] { 0, 20 }, On(ws.Patches[0]), "消した画素は記録から外れる");
            Assert.AreSame(before, ws.Patches[1], "変わらない記録は作り直さない");

            // 記録 A を全部消してから、同じ所を記録なしで塗り直す(ブラシ)。
            layer = ws.WritableLayer(false, "z");
            layer.Set(0, false);
            layer.Set(20, false);
            ws.TrimPatches();
            Assert.AreEqual(1, ws.Patches.Count, "1 つも残っていない記録は外す");
            layer = ws.WritableLayer(false, "z");
            foreach (int i in new[] { 0, 1, 2, 20 }) {
                layer.Set(i, true);
            }
            ws.TrimPatches();
            Assert.AreEqual(3, Clear(ws, "z", 0), "古い記録の形(0・20)ではなく、つながったかたまり(0・1・2)が消える");
            CollectionAssert.AreEqual(new[] { 20, 30, 31 }, On(ws.Layer(false, "z")));
            Assert.AreEqual(2, Clear(ws, "z", 30), "残った記録は、入れたときの範囲で消える");
            ws.Clear();
        }

        [Test]
        public void Patches_AreTrimmedByRegionClear_AndWhenRead() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 5, 20);
            Apply(ws, "z", MaskRegionOp.ClearHere, 5, 20);                // 範囲で消す(0 は離れているので切れ端にならない)
            Assert.AreEqual(1, ws.Patches.Count);
            CollectionAssert.AreEqual(new[] { 0 }, On(ws.Patches[0]));

            // 一部が消された記録を保存すると、残っている画素だけが書かれる。
            var ms = new MaskState();
            ws.EncodeTo(ms);
            Assert.AreEqual(RleText(PW, PH, true, 1, (uint)(PW * PH - 1)), ms.patches[0].maskBase64);

            // 削る前の版が書いた記録(層に無い画素を含む)は、読むときに層へ合わせる。
            ms.patches[0].maskBase64 = RleText(PW, PH, true, 2, 18, 1, 11);
            var loaded = new MaskWorkspace();
            Assert.IsTrue(loaded.DecodeFrom(ms));
            Assert.AreEqual(1, loaded.Patches.Count);
            CollectionAssert.AreEqual(new[] { 0 }, On(loaded.Patches[0]));
            ws.Clear();
            loaded.Clear();
        }

        [Test]
        public void Patches_GoWithTheirLayer() {
            var ws = Empty();
            Apply(ws, "a", MaskRegionOp.DontPaintHere, 0);
            Apply(ws, "a", MaskRegionOp.PaintHere, 5);
            Apply(ws, "b", MaskRegionOp.DontPaintHere, 9);
            Apply(ws, null, MaskRegionOp.DontPaintHere, 30);
            Assert.AreEqual(4, ws.Patches.Count);

            ws.RemoveLayer(true, "a");
            Assert.AreEqual(3, ws.Patches.Count, "層を消すと、その層の記録も消える");
            ws.SetCommon(null);
            Assert.AreEqual(2, ws.Patches.Count);
            Assert.IsTrue(ws.RetainZones(new HashSet<string> { "b" }));
            Assert.AreEqual(1, ws.Patches.Count, "無くなったゾーンの記録も外す");
            Assert.AreEqual("b", ws.Patches[0].ZoneId);
            ws.Clear();
            Assert.AreEqual(0, ws.Patches.Count);
        }

        [Test]
        public void Patches_FollowARescale() {
            var ws = Empty();
            Apply(ws, "z", MaskRegionOp.DontPaintHere, 0, 1);
            ws.Rescale(PW * 2, PH * 2);
            Assert.AreEqual(8, On(ws.Layer(false, "z")).Count, "2 倍にすると 2 画素が 8 画素");
            Assert.AreEqual(8, Clear(ws, "z", 0), "記録も同じ画素を指す");
            ws.Clear();
        }

        [Test]
        public void Patches_AreCappedPerLayer() {
            var ws = new MaskWorkspace();
            ws.Reset(100, 2);
            for (int i = 0; i < 70; i++) {
                Apply(ws, "z", MaskRegionOp.DontPaintHere, i * 2);       // 1 画素おき(つながらない)
            }
            Apply(ws, "other", MaskRegionOp.DontPaintHere, 199);
            Assert.AreEqual(64 + 1, ws.Patches.Count, "1 つの層について 64 個まで。ほかの層の記録は数えない");
            CollectionAssert.AreEqual(new[] { 12 }, On(ws.Patches[0]), "古いものから手放す");

            // 読むときも同じ上限(手で書いた保存は、上限を超えて並べられる)。層には 70 画素とも立っている。
            var ms = new MaskState();
            ws.EncodeTo(ms);
            for (int i = 0; i < 6; i++) {
                int px = i * 2;
                ms.patches.Add(new MaskPatchEntry {
                    zoneId = "z", include = false,
                    maskBase64 = px == 0 ? RleText(100, 2, true, 1, 199) : RleText(100, 2, false, (uint)px, 1, (uint)(199 - px)),
                });
            }
            var loaded = new MaskWorkspace();
            Assert.IsTrue(loaded.DecodeFrom(ms));
            Assert.AreEqual(64 + 1, loaded.Patches.Count, "読んだ後も 1 つの層について 64 個まで");
            CollectionAssert.AreEqual(new[] { 24 }, On(loaded.Patches[0]), "並びの古い側(先頭)から手放す");
            ws.Clear();
            loaded.Clear();
        }

        [Test]
        public void Patches_AreCappedByTotalBytes() {
            // 1 回の操作 = 80 行ぶんの 1 画素おき(ランが約 4 万本、RLE で約 164KB)。7 回で上限(1MiB)を超える。
            const int w = 512, rows = 80, h = rows * 7;
            var ws = new MaskWorkspace();
            ws.Reset(w, h);
            long one = 0;
            for (int op = 0; op < 7; op++) {
                var region = new bool[w * h];
                for (int y = op * rows; y < (op + 1) * rows; y++) {
                    for (int x = 0; x < w; x += 2) {
                        region[y * w + x] = true;
                    }
                }
                unsafe {
                    fixed (bool* p = region) {
                        Assert.IsTrue(ws.ApplyRegion(p, op % 2 == 0 ? "a" : "b", MaskRegionOp.DontPaintHere, null, out _));
                    }
                }
                if (op == 0) {
                    one = ws.Patches[0].Rle.Bytes;
                    Assert.Greater(one * 7, MaskWorkspace.MaxPatchBytes, "この入力で上限を超えること");
                    Assert.LessOrEqual(one * 6, MaskWorkspace.MaxPatchBytes);
                }
            }
            Assert.AreEqual(6, ws.Patches.Count, "合計が上限を超えたら、層を問わず古いものから手放す");
            Assert.IsFalse(ws.Patches[0].Contains(0), "いちばん古い操作(0 行目から)の記録は無い");
            Assert.IsTrue(ws.Patches[0].Contains(rows * w), "2 回目の操作(80 行目から)の記録が先頭");
            Assert.IsTrue(ws.Layer(false, "a").Get(0), "層は変わらない(記録だけ手放す)");
            ws.Clear();
        }

        [Test]
        public void CombinedExclusion_IsCommonOrZone() {
            var ws = Fill(out var common, out var zones, out _);
            var native = ws.CombinedExclusion("za");
            var got = native.ToArray();
            native.Release();
            for (int i = 0; i < got.Length; i++) {
                Assert.AreEqual(common[i] || zones["za"][i], got[i], i.ToString());
            }
            native = ws.CombinedExclusion("none");
            CollectionAssert.AreEqual(common, native.ToArray(), "ゾーンの除外が無ければ共通だけ");
            native.Release();
            ws.SetCommon(null);
            Assert.IsNull(ws.CombinedExclusion("none"), "どちらも無ければ null");
            ws.Clear();
        }
    }

    /// <summary>
    /// 右クリックの「ここも塗る」の絞り込み(PaintHereNarrowing)の決め方の表。「ここも塗る」で粒度が自動のときだけ考え、
    /// 押した所(5×5)がもう塗れているか、押した色が「足すことになる画素」(範囲のうち、まだ塗れていない画素)の少数派なら、
    /// 範囲を押した所の色に近い画素へ絞る。それ以外(塗り残しの同じ素材・小さめ・大きめ・ほかの操作・強さを読めない)は、範囲を
    /// そのまま当てる(2026-11-22・11-23 ユーザー判断)。合成の画像(赤い面に白い小さな四角)で、返る範囲と理由だけを見る。
    /// </summary>
    public unsafe class PaintHereUseTests {
        private const int W = 40, H = 30;
        // 白い四角 [Sq0, Sq1)²(36 画素)。ほかは赤い面。
        private const int Sq0 = 10, Sq1 = 16;
        private static readonly UnityEngine.Color32 Red = new UnityEngine.Color32(200, 30, 30, 255);
        private static readonly UnityEngine.Color32 White = new UnityEngine.Color32(250, 250, 250, 255);

        private static bool InSquare(int i) {
            int x = i % W, y = i / W;
            return x >= Sq0 && x < Sq1 && y >= Sq0 && y < Sq1;
        }

        private static NativePixels Image() {
            var px = NativePixels.Allocate(W, H);
            for (int i = 0; i < W * H; i++) {
                px.Ptr[i] = InSquare(i) ? White : Red;
            }
            return px;
        }

        // 範囲は画像の全部(AI が面ごと取った)。
        private static NativeBools WholeRegion() {
            var m = NativeBools.Allocate(W, H);
            for (int i = 0; i < W * H; i++) {
                m.Ptr[i] = true;
            }
            return m;
        }

        // 画素ごとの強さ(painted(i) なら 1)を読む。
        private static PaintHereNarrowing.StrengthReader Reader(System.Func<int, bool> painted)
            => (indices, strength, count) => {
                for (int i = 0; i < count; i++) {
                    strength[i] = painted(indices[i]) ? 1f : 0f;
                }
                return true;
            };

        private static float U(int x) => (x + 0.5f) / W;
        private static float V(int y) => (y + 0.5f) / H;

        // 範囲を絞って、残った画素の数・白の数・理由を返す(範囲は書き換えないことも見る)。
        private static (int kept, int white, PaintHereNarrowing.Outcome o, bool owned) Run(int x, int y, MaskRegionOp op,
            bool sizeAuto, PaintHereNarrowing.StrengthReader read) {
            var px = Image();
            var region = WholeRegion();
            try {
                var r = PaintHereNarrowing.Narrow(region, px, U(x), V(y), op, sizeAuto, read, out bool owned, out var o);
                try {
                    Assert.AreEqual(W * H, region.CountTrue(), "渡した範囲は書き換えない");
                    Assert.AreEqual(owned, !ReferenceEquals(r, region), "絞ったときだけ新しい範囲");
                    int white = 0;
                    for (int i = 0; i < W * H; i++) {
                        if (r.Ptr[i] && InSquare(i)) {
                            white++;
                        }
                    }
                    return (r.CountTrue(), white, o, owned);
                } finally {
                    if (owned) {
                        r.Release();
                    }
                }
            } finally {
                region.Release();
                px.Release();
            }
        }

        [Test]
        public void OnlyPaintHereWithAutoSizeIsConsidered() {
            var ops = new[] { MaskRegionOp.PaintHere, MaskRegionOp.DontPaintHere, MaskRegionOp.OnlyThisPart, MaskRegionOp.ClearHere };
            foreach (var op in ops) {
                foreach (bool auto in new[] { true, false }) {
                    Assert.AreEqual(op == MaskRegionOp.PaintHere && auto, PaintHereNarrowing.Applies(op, auto), $"{op} 自動={auto}");
                    // 押した所が塗れていても、考えない操作・粒度なら範囲をそのまま返す
                    var (kept, _, o, owned) = Run(12, 12, op, auto, Reader(_ => true));
                    if (!(op == MaskRegionOp.PaintHere && auto)) {
                        Assert.IsFalse(owned || o.Narrowed, $"{op} 自動={auto} は絞らない");
                        Assert.AreEqual(W * H, kept);
                    }
                }
            }
        }

        [Test]
        public void SmallPatternOnUnpaintedSurface_AddsOnlyItsColor() {
            // どこも塗れていない。白い四角(全体の 3%)を押すと、足すことになる画素のうち白は少数派なので、白だけを足す。
            var (kept, white, o, owned) = Run(12, 12, MaskRegionOp.PaintHere, true, Reader(_ => false));
            Assert.AreEqual(PaintHereNarrowing.Reason.Minority, o.reason);
            Assert.IsFalse(o.clickPainted);
            Assert.IsTrue(owned);
            Assert.AreEqual(W * H, o.unpainted);
            Assert.AreEqual(36, o.unpaintedNear);
            Assert.AreEqual(36, kept, "白い四角だけが残る");
            Assert.AreEqual(36, white);
            Assert.AreEqual(W * H - 36, o.dropped);
            Assert.IsFalse(o.NothingLeftToAdd);
        }

        [Test]
        public void MajorityColorOnUnpaintedSurface_AddsTheWholeRegion() {
            // どこも塗れていない。赤い面を押すと、赤は多数派なので範囲を丸ごと足す(白い四角も)。
            var (kept, white, o, owned) = Run(30, 20, MaskRegionOp.PaintHere, true, Reader(_ => false));
            Assert.AreEqual(PaintHereNarrowing.Reason.None, o.reason);
            Assert.IsFalse(owned);
            Assert.AreEqual(W * H, kept);
            Assert.AreEqual(36, white);
        }

        [Test]
        public void FractionIsMeasuredOnlyOverUnpaintedPixels() {
            // 赤い面はもう塗れていて、白い四角だけが塗り残し。白は範囲の全部では 3% だが、足すことになる画素(白い四角)では全部なので、
            // 絞らない(大半が塗れている布の中の明るい雲を押した例。範囲の全部で測ると雲の芯が塗りむらになる)。
            var (kept, _, o, owned) = Run(12, 12, MaskRegionOp.PaintHere, true, Reader(i => !InSquare(i)));
            Assert.AreEqual(PaintHereNarrowing.Reason.None, o.reason);
            Assert.AreEqual(36, o.unpainted);
            Assert.AreEqual(36, o.unpaintedNear);
            Assert.IsFalse(owned);
            Assert.AreEqual(W * H, kept);
        }

        [Test]
        public void PaintedClick_AddsOnlyItsColor_AndSaysWhenNothingIsLeft() {
            // 赤い面はもう塗れている。赤を押すと(塗れている所)、色の違う白い四角は足さない。足す所(塗れていない赤)は無い。
            var (kept, white, o, owned) = Run(30, 20, MaskRegionOp.PaintHere, true, Reader(i => !InSquare(i)));
            Assert.AreEqual(PaintHereNarrowing.Reason.ClickPainted, o.reason);
            Assert.IsTrue(o.clickPainted);
            Assert.IsTrue(owned);
            Assert.AreEqual(W * H - 36, kept);
            Assert.AreEqual(0, white);
            Assert.AreEqual(36, o.dropped);
            Assert.IsTrue(o.NothingLeftToAdd, "近い色の所はもう全部塗れている");
        }

        [Test]
        public void UnreadableStrength_DoesNotNarrow() {
            var (kept, _, o, owned) = Run(12, 12, MaskRegionOp.PaintHere, true, (indices, strength, count) => false);
            Assert.IsFalse(owned || o.Narrowed);
            Assert.AreEqual(W * H, kept);
            var (kept2, _, o2, owned2) = Run(12, 12, MaskRegionOp.PaintHere, true, null);
            Assert.IsFalse(owned2 || o2.Narrowed);
            Assert.AreEqual(W * H, kept2);
        }

        [Test]
        public void PixelPaintedIsHalfOrMore() {
            Assert.IsFalse(MaskRegionEdit.PixelPainted(0.49f));
            Assert.IsTrue(MaskRegionEdit.PixelPainted(0.5f));
            Assert.IsTrue(MaskRegionEdit.PixelPainted(1f));
        }

        [Test]
        public void ZonePaintsAtNeedsHalfOfTheWindow() {
            Assert.IsFalse(MaskRegionEdit.ZonePaintsAt(null, 25), "強さが読めなければ塗っていない");
            var s = new float[25];
            for (int i = 0; i < 12; i++) {
                s[i] = 1f;
            }
            Assert.IsFalse(MaskRegionEdit.ZonePaintsAt(s, 25), "半分未満なら塗っていない");
            s[12] = 0.5f;
            Assert.IsTrue(MaskRegionEdit.ZonePaintsAt(s, 25), "強さの合計が半分以上なら塗っている");
            var idx = MaskRegionEdit.PaintedProbeIndices(0f, 0f, 10, 10);
            Assert.AreEqual(9, idx.Length, "画像の角では窓を端で切る(3×3)");
            Assert.AreEqual(25, MaskRegionEdit.PaintedProbeIndices(0.5f, 0.5f, 10, 10).Length, "中では 5×5");
        }
    }

    /// <summary>
    /// 「ここも塗る」の絞り込みで、窓の読み方(プレビューの選択の控え + 今のマスク。PaintHereNarrowing.WithMaskContract)と、
    /// 自動化・ハーネスの読み方(今のマスクでゾーンの選択を回す。PaintHereNarrowing.NarrowBySelection)が、同じ入力で同じ決め方
    /// (理由・押した所が塗れているか・塗れていない画素数・近い色の画素数・外した画素数・絞った範囲)になること。
    /// 控えは含めるマスクを足さずに保存しているので、重ねないと、先に「ここも塗る」で含めた所を窓だけが「塗れていない」と数えた。
    /// 場面: 左半分が赤(ゾーンが色で選ぶ)、右半分が青(選ばない)、青の中に白い四角。範囲は画像の全部(AI が面ごと取った)。
    /// </summary>
    public unsafe class PaintHereWindowParityTests {
        private const int W = 64, H = 48;
        // 白い四角 [Sq0, Sq1)²。
        private const int Sq0 = 40, Sq1 = 48;

        private static bool InSquare(int x, int y) => x >= Sq0 && x < Sq1 && y >= Sq0 - 20 && y < Sq1 - 20;

        private static UnityEngine.Color32[] Scene() {
            var px = new UnityEngine.Color32[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    byte v = (byte)(170 + (y * 60) / H);
                    px[y * W + x] = InSquare(x, y) ? new UnityEngine.Color32(250, 250, 250, 255)
                        : x < W / 2 ? new UnityEngine.Color32(v, 40, 40, 255)
                        : new UnityEngine.Color32(40, 80, v, 255);
                }
            }
            return px;
        }

        private static ColorZone RedZone() {
            var z = new ColorZone {
                name = "red",
                sampleColor = new UnityEngine.Color(200 / 255f, 40 / 255f, 40 / 255f),
                sampleColorSet = true,
                targetColor = new UnityEngine.Color(0.1f, 0.6f, 0.6f),
                targetColorSet = true,
                tolerance = 0.25f,
                edgeSoftness = 0.4f,
            };
            z.EnsureId();
            return z;
        }

        private static float U(int x) => (x + 0.5f) / W;
        private static float V(int y) => (y + 0.5f) / H;

        private static NativeBools WholeRegion() {
            var m = NativeBools.Allocate(W, H);
            for (int i = 0; i < W * H; i++) {
                m.Ptr[i] = true;
            }
            return m;
        }

        // 窓の読み方と自動化・ハーネスの読み方で絞り、決め方が同じことを確かめる。窓の絞った範囲(持ち主は呼び出し側)を返す。
        private static NativeBools AssertSameDecision(string label, UnityEngine.Color32[] src, ColorZone zone, MaskSnapshot masks,
            int cx, int cy, out PaintHereNarrowing.Outcome window) {
            var settings = RecolorSettings.Default;
            var px = NativePixels.CopyOf(src, W, H);
            var region = WholeRegion();
            using var cache = new SelectionCache();
            try {
                // プレビューと同じく、今のマスクで塗って選択の控えを作る。
                var outPx = (UnityEngine.Color32[])src.Clone();
                PixelProcessor.ProcessPixelsArray(outPx, W, H, masks, new List<ColorZone> { zone }, settings, default, null,
                    cache, src);
                PaintHereNarrowing.StrengthReader cached = (indices, strength, count)
                    => cache.TryReadLatest(zone.id, W, H, indices, strength);
                var read = PaintHereNarrowing.WithMaskContract(cached, masks, zone.id, W, H);
                var a = PaintHereNarrowing.Narrow(region, px, U(cx), V(cy), MaskRegionOp.PaintHere, true, read,
                    out bool ownA, out window);
                var b = PaintHereNarrowing.NarrowBySelection(region, px, U(cx), V(cy), MaskRegionOp.PaintHere, true,
                    zone.Clone(), settings, masks, out bool ownB, out var sim);
                try {
                    Assert.IsTrue(window.measured && sim.measured, $"{label}: 強さを読めなかった");
                    Assert.AreEqual(sim.reason, window.reason, $"{label}: 理由");
                    Assert.AreEqual(sim.clickPainted, window.clickPainted, $"{label}: 押した所が塗れているか");
                    Assert.AreEqual(sim.unpainted, window.unpainted, $"{label}: 塗れていない画素数");
                    Assert.AreEqual(sim.unpaintedNear, window.unpaintedNear, $"{label}: 近い色の画素数");
                    Assert.AreEqual(sim.dropped, window.dropped, $"{label}: 外した画素数");
                    Assert.AreEqual(ownB, ownA, $"{label}: 絞ったか");
                    for (int i = 0; i < W * H; i++) {
                        Assert.AreEqual(b.Ptr[i], a.Ptr[i], $"{label}: 絞った範囲が画素 {i} で違う");
                    }
                } finally {
                    if (ownB) {
                        b.Release();
                    }
                }
                if (ownA) {
                    return a;
                }
                return region.Clone();
            } finally {
                region.Release();
                px.Release();
            }
        }

        [Test]
        public void NoMasks_SameDecision() {
            var src = Scene();
            var zone = RedZone();
            var masks = MaskSnapshot.FromBuffers(W, H, null, null, null);
            try {
                // 白い四角(塗れていない・少数派)と、赤(塗れている)を押す。
                AssertSameDecision("マスクなし・白", src, zone, masks, 43, 23, out var o1).Release();
                Assert.AreEqual(PaintHereNarrowing.Reason.Minority, o1.reason, "場面の前提: 白は少数派");
                AssertSameDecision("マスクなし・赤", src, zone, masks, 10, 10, out var o2).Release();
                Assert.AreEqual(PaintHereNarrowing.Reason.ClickPainted, o2.reason, "場面の前提: 赤は塗れている");
            } finally {
                masks.Release();
            }
        }

        [Test]
        public void IncludeMask_CountsAsPainted() {
            var src = Scene();
            var zone = RedZone();
            // 白い四角と、その周りの青の一部を含める(色の照合では選ばれない所)。
            var include = new bool[W * H];
            for (int y = 15; y < 33; y++) {
                for (int x = 36; x < 52; x++) {
                    include[y * W + x] = true;
                }
            }
            var masks = MaskSnapshot.FromBuffers(W, H, null, null,
                new Dictionary<string, bool[]> { { zone.id, include } });
            try {
                AssertSameDecision("含める・白", src, zone, masks, 43, 23, out var o).Release();
                Assert.IsTrue(o.clickPainted, "含めた所は塗れている");
                AssertSameDecision("含める・青", src, zone, masks, 58, 40, out _).Release();
            } finally {
                masks.Release();
            }
        }

        [Test]
        public void ExcludeMasks_CountAsUnpainted() {
            var src = Scene();
            var zone = RedZone();
            var common = new bool[W * H];
            var zoneExclude = new bool[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    common[y * W + x] = x >= 8 && x < 14;
                    zoneExclude[y * W + x] = y >= 30 && y < 38;
                }
            }
            var masks = MaskSnapshot.FromBuffers(W, H, common,
                new Dictionary<string, bool[]> { { zone.id, zoneExclude } }, null);
            try {
                AssertSameDecision("除外・共通の帯の赤", src, zone, masks, 10, 10, out var o1).Release();
                Assert.IsFalse(o1.clickPainted, "共通の除外の上は塗れていない");
                AssertSameDecision("除外・ゾーンの帯の赤", src, zone, masks, 20, 33, out var o2).Release();
                Assert.IsFalse(o2.clickPainted, "ゾーンの除外の上は塗れていない");
                AssertSameDecision("除外・白", src, zone, masks, 43, 23, out _).Release();
            } finally {
                masks.Release();
            }
        }

        [Test]
        public void PaintHereTwiceAtTheSamePlace_SameDecision() {
            var src = Scene();
            var zone = RedZone();
            var ws = new MaskWorkspace();
            ws.Reset(W, H);
            try {
                // 1 回目: 白い四角を押す(少数派で白だけに絞って含める)。
                var snap = ws.ToSnapshot();
                NativeBools first;
                try {
                    first = AssertSameDecision("1 回目", src, zone, snap, 43, 23, out var o1);
                    Assert.AreEqual(PaintHereNarrowing.Reason.Minority, o1.reason);
                } finally {
                    snap.Release();
                }
                try {
                    Assert.IsTrue(ws.ApplyRegion(first.Ptr, zone.id, MaskRegionOp.PaintHere, null, out var r));
                    Assert.Greater(r.changed, 0);
                } finally {
                    first.Release();
                }
                // 2 回目: 同じ所。含めた白は塗れている(窓も、控えに含めるを重ねて同じに数える)。
                snap = ws.ToSnapshot();
                try {
                    AssertSameDecision("2 回目", src, zone, snap, 43, 23, out var o2).Release();
                    Assert.IsTrue(o2.clickPainted, "1 回目で含めた所は塗れている");
                    Assert.AreEqual(PaintHereNarrowing.Reason.ClickPainted, o2.reason);
                } finally {
                    snap.Release();
                }
            } finally {
                ws.Clear();
            }
        }
    }
}
