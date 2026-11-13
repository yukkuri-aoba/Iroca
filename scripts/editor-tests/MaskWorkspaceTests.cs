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
                    z.UpdateCacheIfNeeded();
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
}
