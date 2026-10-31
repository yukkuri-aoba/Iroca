// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;

namespace Iroca
{
    /// <summary>
    /// 編集中のマスク(0.4.0 の計画の 4-2 の 6)。全ゾーン共通の除外と、ゾーンごとの除外・含めるの層を、1 画素 1 ビットの
    /// ヒープの外のマスク(<see cref="NativeBitMask"/>)で持つ。以前は層ごとに bool[](8K で 1 枚 64MB、Mono のヒープ)で、
    /// 共通 1 + ゾーン 5 × 2 層で 8K なら 704MB だった。
    /// <para>
    /// 層は参照の数で共有できる(重ね表示のワーカーは参照を足して読む)。書く前は必ず Writable* で自分だけのものにする
    /// (ほかに持ち主がいるときだけ写す)。層の寸法はどれも <see cref="Width"/> × <see cref="Height"/>。値が null の
    /// 項目は持たない(無い = 全画素が立っていない)。
    /// </para>
    /// <para>
    /// 保存形式(<see cref="MaskState"/>)との変換・処理用のスナップショットの組み立ては、bool[] の経路
    /// (<see cref="MaskStateCodec"/>・<see cref="MaskSnapshot.FromBuffers"/>)と同じ結果になる(項目の並びも同じ)。
    /// Undo・保存・重ね表示の予定は呼び出し側(マスクの編集)。メインスレッド専用。
    /// </para>
    /// </summary>
    internal sealed unsafe class MaskWorkspace
    {
        private NativeBitMask _common;
        private readonly Dictionary<string, NativeBitMask> _excludes = new Dictionary<string, NativeBitMask>();
        private readonly Dictionary<string, NativeBitMask> _includes = new Dictionary<string, NativeBitMask>();

        /// <summary>マスクの土台の寸法(0 = まだ決まっていない)。</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>層が 1 枚でもあるか(中身が全部 0 の層も数える)。</summary>
        public bool HasLayers => _common != null || _excludes.Count > 0 || _includes.Count > 0;

        /// <summary>全ゾーン共通の除外(無ければ null)。読むだけ。書くなら <see cref="WritableCommon"/>。</summary>
        public NativeBitMask Common => _common;

        /// <summary>ゾーンの層(含めるか除外か)。無ければ null。読むだけ。</summary>
        public NativeBitMask Layer(bool include, string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return null;
            return Map(include).TryGetValue(zoneId, out var m) ? m : null;
        }

        /// <summary>ゾーンの層の一覧(含めるか除外か)。読むだけ。並びは足した順(保存形式の項目の並び)。</summary>
        public IEnumerable<KeyValuePair<string, NativeBitMask>> Layers(bool include) => Map(include);

        private Dictionary<string, NativeBitMask> Map(bool include) => include ? _includes : _excludes;

        // ─────────────── 書く ───────────────

        /// <summary>共通の除外を、無ければ作って、自分だけのものにして返す。寸法が決まっていなければ null。</summary>
        public NativeBitMask WritableCommon()
        {
            if (Width <= 0 || Height <= 0) return null;
            if (_common == null) _common = NativeBitMask.Allocate(Width, Height);
            else _common = _common.Writable();
            return _common;
        }

        /// <summary>ゾーンの層を、無ければ作って、自分だけのものにして返す。id が空・寸法が決まっていなければ null。</summary>
        public NativeBitMask WritableLayer(bool include, string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId) || Width <= 0 || Height <= 0) return null;
            var map = Map(include);
            map[zoneId] = map.TryGetValue(zoneId, out var m) ? m.Writable() : NativeBitMask.Allocate(Width, Height);
            return map[zoneId];
        }

        /// <summary>ゾーンの層があれば自分だけのものにして返す。無ければ null(作らない)。</summary>
        public NativeBitMask WritableExistingLayer(bool include, string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return null;
            var map = Map(include);
            if (!map.TryGetValue(zoneId, out var m)) return null;
            map[zoneId] = m = m.Writable();
            return m;
        }

        /// <summary>共通の除外を置き換える(<paramref name="mask"/> の参照を預かる。null で消す)。寸法が違えば受け取らずに返す。</summary>
        public void SetCommon(NativeBitMask mask)
        {
            if (mask != null && (mask.Width != Width || mask.Height != Height)) { mask.Release(); return; }
            _common?.Release();
            _common = mask;
        }

        /// <summary>ゾーンの層を置き換える(<paramref name="mask"/> の参照を預かる)。寸法が違う・id が空なら受け取らずに返す。</summary>
        public void SetLayer(bool include, string zoneId, NativeBitMask mask)
        {
            if (mask == null) return;
            if (string.IsNullOrEmpty(zoneId) || mask.Width != Width || mask.Height != Height) { mask.Release(); return; }
            var map = Map(include);
            if (map.TryGetValue(zoneId, out var old)) old.Release();
            map[zoneId] = mask;
        }

        /// <summary>ゾーンの層を消す。</summary>
        public void RemoveLayer(bool include, string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            var map = Map(include);
            if (map.TryGetValue(zoneId, out var m)) { m.Release(); map.Remove(zoneId); }
        }

        /// <summary>ゾーンの除外と含めるを両方消す(ゾーンを消したとき)。</summary>
        public void RemoveZone(string zoneId)
        {
            RemoveLayer(false, zoneId);
            RemoveLayer(true, zoneId);
        }

        /// <summary>層を全部手放す(寸法はそのまま)。</summary>
        public void Clear()
        {
            _common?.Release();
            _common = null;
            ReleaseAll(_excludes);
            ReleaseAll(_includes);
        }

        private static void ReleaseAll(Dictionary<string, NativeBitMask> map)
        {
            foreach (var kv in map) kv.Value.Release();
            map.Clear();
        }

        /// <summary>層を全部手放して、寸法を決め直す。</summary>
        public void Reset(int width, int height)
        {
            Clear();
            Width = width;
            Height = height;
        }

        /// <summary>
        /// 寸法を変え、今の層を新しい寸法へ最近傍で写す。サンプリング式は処理側(PixelProcessor の
        /// <c>mx = x * maskW / texW</c>)と同じ整数切り捨てで、適用結果の対応関係を保つ。
        /// </summary>
        public void Rescale(int width, int height)
        {
            if (width <= 0 || height <= 0 || Width <= 0 || Height <= 0) { Reset(width, height); return; }
            int oldW = Width, oldH = Height;
            Width = width;
            Height = height;
            if (_common != null) { var s = RescaleMask(_common, oldW, oldH, width, height); _common.Release(); _common = s; }
            RescaleMap(_excludes, oldW, oldH);
            RescaleMap(_includes, oldW, oldH);
        }

        private void RescaleMap(Dictionary<string, NativeBitMask> map, int oldW, int oldH)
        {
            var keys = new List<string>(map.Keys);
            foreach (var key in keys)
            {
                var old = map[key];
                map[key] = RescaleMask(old, oldW, oldH, Width, Height);
                old.Release();
            }
        }

        private static NativeBitMask RescaleMask(NativeBitMask src, int oldW, int oldH, int newW, int newH)
        {
            var dst = NativeBitMask.Allocate(newW, newH);
            ulong* s = src.Bits, d = dst.Bits;
            for (int y = 0; y < newH; y++)
            {
                int oy = (int)((long)y * oldH / newH);
                int rowOld = oy * oldW;
                int rowNew = y * newW;
                for (int x = 0; x < newW; x++)
                {
                    int i = rowOld + (int)((long)x * oldW / newW);
                    if ((s[i >> 6] & (1UL << (i & 63))) == 0UL) continue;
                    int j = rowNew + x;
                    d[j >> 6] |= 1UL << (j & 63);
                }
            }
            return dst;
        }

        // ─────────────── 保存形式との変換 ───────────────

        /// <summary>
        /// 保存形式へ書き込む(<see cref="MaskStateCodec.Encode"/> と同じ文字列・同じ項目の並び)。全部 0 の層は書かない。
        /// </summary>
        public void EncodeTo(MaskState ms)
        {
            ms.width = Width;
            ms.height = Height;
            ms.commonMaskBase64 = MaskRle.EncodeBitsIfAny(_common) ?? "";

            if (ms.zones == null) ms.zones = new List<MaskZoneEntry>();
            ms.zones.Clear();
            AppendEntries(ms.zones, _excludes);

            if (ms.zoneIncludes == null) ms.zoneIncludes = new List<MaskZoneEntry>();
            ms.zoneIncludes.Clear();
            AppendEntries(ms.zoneIncludes, _includes);
        }

        private static void AppendEntries(List<MaskZoneEntry> dst, Dictionary<string, NativeBitMask> src)
        {
            foreach (var kv in src)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                string encoded = MaskRle.EncodeBitsIfAny(kv.Value);
                if (encoded == null) continue;
                dst.Add(new MaskZoneEntry { zoneId = kv.Key, maskBase64 = encoded });
            }
        }

        /// <summary>
        /// 保存形式で置き換える(<see cref="MaskStateCodec.Decode"/> と同じ規則: 壊れた項目・寸法の合わない項目・ゾーン id の
        /// 無い項目は読み飛ばし、同じゾーン id は後の項目が勝つ)。層はまず全部外す。寸法が無効なら false(層なし・寸法は
        /// そのまま)。同じ寸法で中身が保存形式の文字列と同じ層は、展開し直さずにそのまま使う(Undo/Redo の書き戻しで
        /// 変わるのはふつう 1 枚だけ)。
        /// </summary>
        public bool DecodeFrom(MaskState ms)
        {
            var oldCommon = _common;
            var oldExcludes = new Dictionary<string, NativeBitMask>(_excludes);
            var oldIncludes = new Dictionary<string, NativeBitMask>(_includes);
            bool sameSize = ms != null && ms.width == Width && ms.height == Height;
            _common = null;
            _excludes.Clear();
            _includes.Clear();
            try
            {
                if (ms == null || ms.width <= 0 || ms.height <= 0) return false;
                Width = ms.width;
                Height = ms.height;
                if (!string.IsNullOrEmpty(ms.commonMaskBase64))
                    _common = DecodeSized(ms.commonMaskBase64, sameSize ? oldCommon : null);
                ReadEntries(ms.zones, _excludes, sameSize ? oldExcludes : null);
                ReadEntries(ms.zoneIncludes, _includes, sameSize ? oldIncludes : null);
                return true;
            }
            finally
            {
                // 使い回さなかった前の層を手放す(使い回した層は新しい持ち主へ移っている)。
                if (oldCommon != null && !Holds(oldCommon)) oldCommon.Release();
                foreach (var kv in oldExcludes) if (!Holds(kv.Value)) kv.Value.Release();
                foreach (var kv in oldIncludes) if (!Holds(kv.Value)) kv.Value.Release();
            }
        }

        private bool Holds(NativeBitMask m)
        {
            if (ReferenceEquals(_common, m)) return true;
            foreach (var kv in _excludes) if (ReferenceEquals(kv.Value, m)) return true;
            foreach (var kv in _includes) if (ReferenceEquals(kv.Value, m)) return true;
            return false;
        }

        private void ReadEntries(List<MaskZoneEntry> src, Dictionary<string, NativeBitMask> dst,
            Dictionary<string, NativeBitMask> previous)
        {
            if (src == null) return;
            foreach (var entry in src)
            {
                if (entry == null || string.IsNullOrEmpty(entry.zoneId)) continue;
                NativeBitMask candidate = null;
                previous?.TryGetValue(entry.zoneId, out candidate);
                var m = DecodeSized(entry.maskBase64, candidate);
                if (m == null) continue;
                // 同じ id が 2 度出たら後のものが勝つ。前に置いたものが使い回しでなければ手放す。
                if (dst.TryGetValue(entry.zoneId, out var replaced) && !ReferenceEquals(replaced, m)
                    && !ReferenceEquals(replaced, candidate))
                    replaced.Release();
                dst[entry.zoneId] = m;
            }
        }

        // 寸法がいまの Width/Height と同じときだけ採る。candidate(前に持っていた同じ層)を符号化すると encoded になるなら、
        // それをそのまま返す。
        private NativeBitMask DecodeSized(string encoded, NativeBitMask candidate)
        {
            if (candidate != null && candidate.Width == Width && candidate.Height == Height
                && MaskRle.EncodeBitsIfAny(candidate) == encoded)
                return candidate;
            var m = MaskRle.DecodeBits(encoded);
            if (m == null) return null;
            if (m.Width == Width && m.Height == Height) return m;
            m.Release();
            return null;
        }

        // ─────────────── 処理へ渡す ───────────────

        /// <summary>
        /// 処理用のスナップショット(<see cref="MaskSnapshot.FromBuffers"/> と同じ組み立て: 共通は無ければ null、ゾーンの除外は
        /// 全部、含めるは立っている画素がある層だけ)。層は参照で共有し(写さない)、書く側が書く前に写すので、この後に層を書き換えても変わらない。
        /// </summary>
        public MaskSnapshot ToSnapshot()
        {
            var snap = new MaskSnapshot
            {
                width = Width,
                height = Height,
                zones = new Dictionary<string, NativeBitMask>(),
            };
            snap.common = _common?.AddRef();
            foreach (var kv in _excludes) snap.zones[kv.Key] = kv.Value.AddRef();
            foreach (var kv in _includes)
            {
                if (!kv.Value.Any()) continue;
                if (snap.zoneIncludes == null) snap.zoneIncludes = new Dictionary<string, NativeBitMask>();
                snap.zoneIncludes[kv.Key] = kv.Value.AddRef();
            }
            return snap;
        }

        /// <summary>
        /// 共通の除外とゾーンの除外を合わせた bool[](自動調整に「ユーザーが外した所」として渡す)。どちらも無ければ null。
        /// </summary>
        public bool[] CombinedExclusion(string zoneId)
        {
            var zone = Layer(false, zoneId);
            if (_common == null && zone == null) return null;
            int len = Width * Height;
            var combined = new bool[len];
            ulong* c = _common != null ? _common.Bits : null;
            ulong* z = zone != null ? zone.Bits : null;
            for (int i = 0; i < len; i++)
            {
                ulong bit = 1UL << (i & 63);
                combined[i] = (c != null && (c[i >> 6] & bit) != 0UL) || (z != null && (z[i >> 6] & bit) != 0UL);
            }
            return combined;
        }
    }
}
