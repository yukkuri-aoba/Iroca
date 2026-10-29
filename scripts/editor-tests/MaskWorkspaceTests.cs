// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 編集中のマスク(<see cref="MaskWorkspace"/>。ヒープの外の 1 画素 1 ビット)が、前の bool[] の経路
    /// (<see cref="MaskStateCodec"/>・<see cref="MaskSnapshot.FromBuffers"/>)と同じ保存形式・同じスナップショットを作ること、
    /// 中身が同じ層を使い回して残りを手放すこと、ほかに持ち主がいる層は書く前に写すこと。
    /// Application の層なのでハーネスの自己検査には入らない。ここで見る。
    /// </summary>
    public class MaskWorkspaceTests
    {
        private const int W = 37, H = 11;   // 語(64 画素)の境目をまたぐ寸法

        private static bool[] Random(int seed, double density)
        {
            var rng = new System.Random(seed);
            var m = new bool[W * H];
            for (int i = 0; i < m.Length; i++) m[i] = rng.NextDouble() < density;
            return m;
        }

        private static bool[] ToBools(NativeBitMask m)
        {
            var b = new bool[m.Length];
            for (int i = 0; i < b.Length; i++) b[i] = m.Get(i);
            return b;
        }

        // 共通 1・ゾーンの除外 2(1 つは全部 0)・含める 2(1 つは全部 0)を、両方の経路に同じく置く。
        private static MaskWorkspace Fill(out bool[] common, out Dictionary<string, bool[]> zones,
                                          out Dictionary<string, bool[]> includes)
        {
            common = Random(1, 0.3);
            zones = new Dictionary<string, bool[]> { ["za"] = Random(2, 0.5), ["zb"] = new bool[W * H] };
            includes = new Dictionary<string, bool[]> { ["za"] = new bool[W * H], ["zc"] = Random(3, 0.1) };
            var ws = new MaskWorkspace();
            ws.Reset(W, H);
            ws.SetCommon(NativeBitMask.FromBools(common, W, H));
            foreach (var kv in zones) ws.SetLayer(false, kv.Key, NativeBitMask.FromBools(kv.Value, W, H));
            foreach (var kv in includes) ws.SetLayer(true, kv.Key, NativeBitMask.FromBools(kv.Value, W, H));
            return ws;
        }

        private static string Json(MaskState ms) => UnityEngine.JsonUtility.ToJson(ms);

        [Test]
        public void EncodeTo_MatchesBoolCodec()
        {
            var ws = Fill(out var common, out var zones, out var includes);
            var a = new MaskState();
            ws.EncodeTo(a);
            var b = new MaskState();
            MaskStateCodec.Encode(b, W, H, common, zones, includes);
            Assert.AreEqual(Json(b), Json(a), "同じ文字列・同じ項目の並び(全部 0 の層は書かない)");
            ws.Clear();
        }

        [Test]
        public void DecodeFrom_MatchesBoolCodec_AndSkipsBrokenEntries()
        {
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
            foreach (var kv in fresh.Layers(true)) CollectionAssert.AreEqual(di[kv.Key], ToBools(kv.Value), kv.Key);
            Assert.AreEqual(di.Count, new List<KeyValuePair<string, NativeBitMask>>(fresh.Layers(true)).Count);
            fresh.Clear();
            ws.Clear();
        }

        [Test]
        public void DecodeFrom_InvalidSizeDropsLayersAndKeepsSize()
        {
            var ws = Fill(out _, out _, out _);
            var kept = ws.Common.AddRef();   // 手放されたかを見るため、持ち主をもう 1 つ足しておく
            Assert.IsFalse(ws.DecodeFrom(new MaskState { width = 0, height = 0 }));
            Assert.IsFalse(ws.HasLayers, "層はなくなる");
            Assert.AreEqual(W, ws.Width, "寸法はそのまま");
            Assert.AreEqual(1, kept.RefCount, "前の層は手放した");
            kept.Release();
        }

        [Test]
        public void DecodeFrom_ReusesUnchangedLayersAndReleasesTheRest()
        {
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
        public void ToSnapshot_MatchesFromBuffers()
        {
            var ws = Fill(out var common, out var zones, out var includes);
            var a = ws.ToSnapshot();
            var b = MaskSnapshot.FromBuffers(W, H, common, zones, includes);
            Assert.AreEqual(b.width, a.width);
            Assert.AreEqual(b.height, a.height);
            CollectionAssert.AreEqual(b.common, a.common);
            CollectionAssert.AreEquivalent(b.zones.Keys, a.zones.Keys, "全部 0 の除外も載る");
            foreach (var kv in b.zones) CollectionAssert.AreEqual(kv.Value, a.zones[kv.Key], kv.Key);
            CollectionAssert.AreEquivalent(b.zoneIncludes.Keys, a.zoneIncludes.Keys, "全部 0 の含めるは載らない");
            foreach (var kv in b.zoneIncludes) CollectionAssert.AreEqual(kv.Value, a.zoneIncludes[kv.Key], kv.Key);
            ws.Clear();
        }

        [Test]
        public void Rescale_IsNearestWithTruncation()
        {
            var ws = Fill(out var common, out _, out _);
            const int nw = 50, nh = 7;
            ws.Rescale(nw, nh);
            var got = ToBools(ws.Common);
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    Assert.AreEqual(common[(int)((long)y * H / nh) * W + (int)((long)x * W / nw)], got[y * nw + x], $"({x},{y})");
            Assert.AreEqual(nw, ws.Layer(false, "za").Width);
            ws.Clear();
        }

        [Test]
        public void WritableLayer_CopiesWhenShared()
        {
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

        [Test]
        public void CombinedExclusion_IsCommonOrZone()
        {
            var ws = Fill(out var common, out var zones, out _);
            var got = ws.CombinedExclusion("za");
            for (int i = 0; i < got.Length; i++) Assert.AreEqual(common[i] || zones["za"][i], got[i], i.ToString());
            CollectionAssert.AreEqual(common, ws.CombinedExclusion("none"), "ゾーンの除外が無ければ共通だけ");
            ws.SetCommon(null);
            Assert.IsNull(ws.CombinedExclusion("none"), "どちらも無ければ null");
            ws.Clear();
        }
    }
}
