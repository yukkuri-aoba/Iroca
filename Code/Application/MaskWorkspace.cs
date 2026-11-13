// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;

namespace Iroca {
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
    /// 保存形式(<see cref="MaskState"/>)との変換(EncodeTo・DecodeFrom)と、処理用のスナップショットの組み立て(ToSnapshot)は
    /// ここが正。テスト・ハーネス用の bool[] の見本(<see cref="MaskStateCodec"/> の Encode・Decode、
    /// <see cref="MaskSnapshot.FromBuffers"/>)とは、層の中身と項目の並びが同じになる。保存形式の版(schemaVersion)と
    /// 塗った範囲の記録(patches)を書くのはここだけで、見本は書かない。
    /// Undo・保存・重ね表示の予定は呼び出し側(マスクの編集)。メインスレッド専用。
    /// </para>
    /// <para>
    /// 層のほかに、範囲の操作(右クリックの「ここも塗る」など)が 1 回ごとに塗った画素の記録(<see cref="MaskPatch"/>)を持つ。
    /// 「ここのマスクを消す」が入れたときの範囲をそっくり消すのに使い(<see cref="ClearMaskAt"/>)、層と一緒に Undo の履歴と
    /// 保存形式へ載る。色替えの結果には関わらない。記録は、いまも層に立っている画素だけにしておく(<see cref="TrimPatches"/>。
    /// 範囲の操作・「消す」の中と、ブラシなどで層を直接書いた後の版の確定で呼ぶ)。
    /// </para>
    /// </summary>
    internal sealed unsafe class MaskWorkspace {
        private NativeBitMask _common;
        private readonly Dictionary<string, NativeBitMask> _excludes = new Dictionary<string, NativeBitMask>();
        private readonly Dictionary<string, NativeBitMask> _includes = new Dictionary<string, NativeBitMask>();
        // 範囲の操作が塗った画素の記録(古い順)。
        private readonly List<MaskPatch> _patches = new List<MaskPatch>();

        // 1 つの層について持つ記録の数の上限。超えたら古いものから手放す(手放した操作の範囲は、つながったかたまりで消すことになる)。
        // 記録 1 つは 4K の部分 1 つで 10〜40KB ほど。
        internal const int MaxPatchesPerLayer = 64;

        // 記録の RLE の合計の上限(バイト。全部の層の合計)。記録は git で管理するレシピ(.asset)にも入り(Base64 で 4/3 倍)、
        // 自動保存・非破壊ビルド・シーンのプレビューのたびに読み書きされるので、件数だけでなく量でも止める。超えたら、層を問わず
        // 古いものから手放す(いちばん新しい 1 件は大きさによらず残す)。
        // 記録は層に立っている画素だけを持つので、ふつうは層の RLE と同じ程度で止まる。細かい模様を何十回も塗り分けたときの
        // 守りで、値は「4K の部分 1 つで 10〜40KB」の 25〜100 回ぶん。
        internal const long MaxPatchBytes = 1L << 20;

        /// <summary>マスクの土台の寸法(0 = まだ決まっていない)。</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>層が 1 枚でもあるか(中身が全部 0 の層も数える)。</summary>
        public bool HasLayers => _common != null || _excludes.Count > 0 || _includes.Count > 0;

        /// <summary>全ゾーン共通の除外(無ければ null)。読むだけ。書くなら <see cref="WritableCommon"/>。</summary>
        public NativeBitMask Common => _common;

        /// <summary>ゾーンの層(含めるか除外か)。無ければ null。読むだけ。</summary>
        public NativeBitMask Layer(bool include, string zoneId) {
            if (string.IsNullOrEmpty(zoneId)) {
                return null;
            }
            return Map(include).TryGetValue(zoneId, out var m) ? m : null;
        }

        /// <summary>ゾーンの層の一覧(含めるか除外か)。読むだけ。並びは足した順(保存形式の項目の並び)。</summary>
        public IEnumerable<KeyValuePair<string, NativeBitMask>> Layers(bool include) => Map(include);

        private Dictionary<string, NativeBitMask> Map(bool include) => include ? _includes : _excludes;

        // ─────────────── 書く ───────────────

        /// <summary>共通の除外を、無ければ作って、自分だけのものにして返す。寸法が決まっていなければ null。</summary>
        public NativeBitMask WritableCommon() {
            if (Width <= 0 || Height <= 0) {
                return null;
            }
            if (_common == null) {
                _common = NativeBitMask.Allocate(Width, Height);
            } else {
                _common = _common.Writable();
            }
            return _common;
        }

        /// <summary>ゾーンの層を、無ければ作って、自分だけのものにして返す。id が空・寸法が決まっていなければ null。</summary>
        public NativeBitMask WritableLayer(bool include, string zoneId) {
            if (string.IsNullOrEmpty(zoneId) || Width <= 0 || Height <= 0) {
                return null;
            }
            var map = Map(include);
            map[zoneId] = map.TryGetValue(zoneId, out var m) ? m.Writable() : NativeBitMask.Allocate(Width, Height);
            return map[zoneId];
        }

        /// <summary>ゾーンの層があれば自分だけのものにして返す。無ければ null(作らない)。</summary>
        public NativeBitMask WritableExistingLayer(bool include, string zoneId) {
            if (string.IsNullOrEmpty(zoneId)) {
                return null;
            }
            var map = Map(include);
            if (!map.TryGetValue(zoneId, out var m)) {
                return null;
            }
            map[zoneId] = m = m.Writable();
            return m;
        }

        /// <summary>共通の除外を置き換える(<paramref name="mask"/> の参照を預かる。null で消す)。寸法が違えば受け取らずに返す。</summary>
        public void SetCommon(NativeBitMask mask) {
            if (mask != null && (mask.Width != Width || mask.Height != Height)) { mask.Release(); return; }
            _common?.Release();
            _common = mask;
            DropPatches(null, false);   // 層ごと置き換えたので、前の層に塗った記録は合わない
        }

        /// <summary>ゾーンの層を置き換える(<paramref name="mask"/> の参照を預かる)。寸法が違う・id が空なら受け取らずに返す。</summary>
        public void SetLayer(bool include, string zoneId, NativeBitMask mask) {
            if (mask == null) {
                return;
            }
            if (string.IsNullOrEmpty(zoneId) || mask.Width != Width || mask.Height != Height) { mask.Release(); return; }
            var map = Map(include);
            if (map.TryGetValue(zoneId, out var old)) {
                old.Release();
            }
            map[zoneId] = mask;
            DropPatches(zoneId, include);
        }

        /// <summary>ゾーンの層を消す。</summary>
        public void RemoveLayer(bool include, string zoneId) {
            if (string.IsNullOrEmpty(zoneId)) {
                return;
            }
            var map = Map(include);
            if (map.TryGetValue(zoneId, out var m)) { m.Release(); map.Remove(zoneId); }
            DropPatches(zoneId, include);
        }

        /// <summary>ゾーンの除外と含めるを両方消す(ゾーンを消したとき)。</summary>
        public void RemoveZone(string zoneId) {
            RemoveLayer(false, zoneId);
            RemoveLayer(true, zoneId);
        }

        /// <summary>
        /// <paramref name="zoneIds"/> に無いゾーンの層(除外・含める)を外す。全ゾーン共通の除外は残す。外したら true。
        /// 保存形式の側の同じ規則は <see cref="MaskState.DropZonesExcept"/>。
        /// 外すのはどのゾーンにも効いていない層だけなので、色替えの結果は変わらない(残すと保存とその後のプリセットへ運ばれ、同じ id の
        /// ゾーンが戻ると黙って効き直す)。
        /// </summary>
        public bool RetainZones(ICollection<string> zoneIds) {
            for (int i = _patches.Count - 1; i >= 0; i--) {
                if (_patches[i].ZoneId != null && (zoneIds == null || !zoneIds.Contains(_patches[i].ZoneId))) {
                    _patches[i].Release();
                    _patches.RemoveAt(i);
                }
            }
            return RetainZones(_excludes, zoneIds) | RetainZones(_includes, zoneIds);
        }

        private static bool RetainZones(Dictionary<string, NativeBitMask> map, ICollection<string> zoneIds) {
            List<string> gone = null;
            foreach (var kv in map) {
                if (zoneIds == null || !zoneIds.Contains(kv.Key)) {
                    (gone ??= new List<string>()).Add(kv.Key);
                }
            }
            if (gone == null) {
                return false;
            }
            foreach (var id in gone) {
                map[id].Release();
                map.Remove(id);
            }
            return true;
        }

        /// <summary>層を全部手放す(寸法はそのまま)。</summary>
        public void Clear() {
            _common?.Release();
            _common = null;
            ReleaseAll(_excludes);
            ReleaseAll(_includes);
            ReleasePatches();
        }

        // ─────────────── 範囲の操作が塗った画素の記録 ───────────────

        /// <summary>範囲の操作が塗った画素の記録(古い順)。読むだけ。</summary>
        public IReadOnlyList<MaskPatch> Patches => _patches;

        /// <summary>記録を置き換える(<paramref name="patches"/> の中身の参照を預かる。Undo の履歴から戻すとき)。</summary>
        public void SetPatches(IEnumerable<MaskPatch> patches) {
            ReleasePatches();
            if (patches != null) {
                _patches.AddRange(patches);
            }
        }

        private void ReleasePatches() {
            foreach (var p in _patches) {
                p.Release();
            }
            _patches.Clear();
        }

        private void DropPatches(string zoneId, bool include) {
            for (int i = _patches.Count - 1; i >= 0; i--) {
                if (_patches[i].IsOf(zoneId, include)) {
                    _patches[i].Release();
                    _patches.RemoveAt(i);
                }
            }
        }

        // 範囲の操作が層 after に新しく立てた画素(before = 操作の前の層の写し。無かったら null)を記録する。
        private void RecordPatch(string zoneId, bool include, NativeBitMask after, NativeBitMask before) {
            var patch = MaskPatch.Painted(zoneId, include, after, before);
            if (patch == null) {
                return;
            }
            _patches.Add(patch);
            CapPatches();
        }

        // 記録を上限に収める(層ごとの件数と、全体のバイト数)。新しいものから数え、超えた分の古い記録を手放す。
        private void CapPatches() {
            if (_patches.Count <= 1) {
                return;
            }
            var perLayer = new Dictionary<string, int>();
            long bytes = 0;
            bool overBytes = false;
            for (int i = _patches.Count - 1; i >= 0; i--) {
                var p = _patches[i];
                string key = (p.Include ? "+" : "-") + p.ZoneId;
                perLayer.TryGetValue(key, out int count);
                bool newest = i == _patches.Count - 1;
                // バイト数は、超えた所から古い側を全部手放す(小さい古い記録だけが飛び飛びに残らないように)。
                overBytes |= !newest && bytes + p.Rle.Bytes > MaxPatchBytes;
                if (overBytes || count >= MaxPatchesPerLayer) {
                    p.Release();
                    _patches.RemoveAt(i);
                    continue;
                }
                perLayer[key] = count + 1;
                bytes += p.Rle.Bytes;
            }
        }

        /// <summary>
        /// 記録を、層にいまも立っている画素だけにする(1 つも残っていない記録は外す)。層を書き換えた後に呼ぶ。
        /// 消された画素を記録に残すと、同じ所を記録なしで塗り直した(ブラシなど)ときに、「ここのマスクを消す」が古い記録の形だけを
        /// 消す。記録どうしは重ならなくなる(新しい記録は、それまで立っていなかった画素だけを持つ)ので、量も層と同じ程度で止まる。
        /// </summary>
        public void TrimPatches() {
            for (int i = _patches.Count - 1; i >= 0; i--) {
                var p = _patches[i];
                var kept = p.TrimmedTo(LayerOf(p));
                if (ReferenceEquals(kept, p)) {
                    continue;
                }
                p.Release();
                if (kept == null) {
                    _patches.RemoveAt(i);
                } else {
                    _patches[i] = kept;
                }
            }
        }

        // 記録が指す層(無ければ null)。
        private NativeBitMask LayerOf(MaskPatch p)
            => p.ZoneId == null ? (p.Include ? null : _common) : Layer(p.Include, p.ZoneId);

        // 層の画素 pixel を塗った記録のうち、いちばん新しいもの(無ければ null)。
        private MaskPatch NewestPatchAt(string zoneId, bool include, int pixel) {
            for (int i = _patches.Count - 1; i >= 0; i--) {
                var p = _patches[i];
                if (p.IsOf(zoneId, include) && p.Width == Width && p.Height == Height && p.Contains(pixel)) {
                    return p;
                }
            }
            return null;
        }

        private static void ReleaseAll(Dictionary<string, NativeBitMask> map) {
            foreach (var kv in map) {
                kv.Value.Release();
            }
            map.Clear();
        }

        /// <summary>層を全部手放して、寸法を決め直す。</summary>
        public void Reset(int width, int height) {
            Clear();
            Width = width;
            Height = height;
        }

        /// <summary>
        /// 寸法を変え、今の層を新しい寸法へ最近傍で写す。サンプリング式は処理側(PixelProcessor の
        /// <c>mx = x * maskW / texW</c>)と同じ整数切り捨てで、適用結果の対応関係を保つ。
        /// </summary>
        public void Rescale(int width, int height) {
            if (width <= 0 || height <= 0 || Width <= 0 || Height <= 0) { Reset(width, height); return; }
            int oldW = Width, oldH = Height;
            Width = width;
            Height = height;
            if (_common != null) { var s = RescaleMask(_common, oldW, oldH, width, height); _common.Release(); _common = s; }
            RescaleMap(_excludes, oldW, oldH);
            RescaleMap(_includes, oldW, oldH);
            // 記録も同じ写し方で新しい寸法へ(層と同じ画素を指し続ける)。
            for (int i = _patches.Count - 1; i >= 0; i--) {
                var old = _patches[i];
                var was = old.Decode();
                var now = RescaleMask(was, oldW, oldH, width, height);
                was.Release();
                var scaled = MaskPatch.Painted(old.ZoneId, old.Include, now, null);
                now.Release();
                old.Release();
                if (scaled != null) {
                    _patches[i] = scaled;
                } else {
                    _patches.RemoveAt(i);
                }
            }
        }

        private void RescaleMap(Dictionary<string, NativeBitMask> map, int oldW, int oldH) {
            var keys = new List<string>(map.Keys);
            foreach (var key in keys) {
                var old = map[key];
                map[key] = RescaleMask(old, oldW, oldH, Width, Height);
                old.Release();
            }
        }

        private static NativeBitMask RescaleMask(NativeBitMask src, int oldW, int oldH, int newW, int newH) {
            var dst = NativeBitMask.Allocate(newW, newH);
            ulong* s = src.Bits, d = dst.Bits;
            for (int y = 0; y < newH; y++) {
                int oy = (int)((long)y * oldH / newH);
                int rowOld = oy * oldW;
                int rowNew = y * newW;
                for (int x = 0; x < newW; x++) {
                    int i = rowOld + (int)((long)x * oldW / newW);
                    if ((s[i >> 6] & (1UL << (i & 63))) == 0UL) {
                        continue;
                    }
                    int j = rowNew + x;
                    d[j >> 6] |= 1UL << (j & 63);
                }
            }
            return dst;
        }

        // ─────────────── 範囲に操作を当てる ───────────────

        /// <summary>
        /// 範囲(<see cref="Width"/> × <see cref="Height"/> 個。下原点)に操作を当てる(規則は <see cref="MaskRegionEdit"/>)。
        /// 右クリック(ウィンドウ)と自動化の共通の入口。zoneId = null は全ゾーン共通で、共通にできるのは「塗らない」と「消す」だけ。
        /// 書く直前に <paramref name="beforeWrite"/> を呼ぶ(ウィンドウの Undo の記録。書かないときは呼ばない)。
        /// 「消す」は消す物が無ければ層を写さず、何も書かない(重なりだけ返す)。範囲の縁に残る細い切れ端も一緒に消す
        /// (<see cref="MaskRegionEdit.EraseCrumbs"/>)。ゾーンが今もあるかは呼び出し側が確かめる。
        /// 当てられない組み合わせなら false。
        /// </summary>
        public bool ApplyRegion(bool* region, string zoneId, MaskRegionOp op, System.Action beforeWrite,
            out MaskRegionEditResult result) {
            result = default;
            int len = Width * Height;
            if (region == null || len <= 0) {
                return false;
            }
            if (op == MaskRegionOp.ClearHere) {
                return ClearRegion(region, len, zoneId, beforeWrite, out result);
            }

            // 書く層は自分だけのものにしてから渡す(重ね表示のワーカーが読んでいれば写す)。共通は重なりを数えるだけ。
            NativeBitMask exclude, include = null, common = null;
            if (zoneId == null) {
                if (op != MaskRegionOp.DontPaintHere) {
                    return false;
                }
                exclude = WritableCommon();
            } else {
                common = _common;
                if (op == MaskRegionOp.PaintHere) {
                    include = WritableLayer(true, zoneId);
                    exclude = WritableExistingLayer(false, zoneId);
                } else {
                    exclude = WritableLayer(false, zoneId);
                    include = WritableExistingLayer(true, zoneId);
                }
            }
            if (op == MaskRegionOp.PaintHere ? include == null : exclude == null) {
                return false;
            }

            // この操作が塗る層(「ここも塗る」は含める、ほかは除外)の、操作の前の中身。後で差を取って、塗った画素を記録する。
            bool paintsInclude = op == MaskRegionOp.PaintHere;
            NativeBitMask target = paintsInclude ? include : exclude;
            NativeBitMask before = target.Any() ? target.Clone() : null;
            try {
                beforeWrite?.Invoke();
                result = MaskRegionEdit.Apply(op, region, len, BitsOf(exclude), BitsOf(include), BitsOf(common));
                if (result.changed > 0) {
                    RecordPatch(zoneId, paintsInclude, target, before);
                    TrimPatches();   // 「ここも塗る」は除外を、「塗らない」は含めるを消す。消えた画素を前の記録から外す
                }
            } finally {
                before?.Release();
            }
            return true;
        }

        private bool ClearRegion(bool* region, int len, string zoneId, System.Action beforeWrite, out MaskRegionEditResult result) {
            bool toCommon = zoneId == null;
            NativeBitMask common = toCommon ? null : _common;
            result = MaskRegionEdit.CountClear(region, len,
                BitsOf(toCommon ? _common : Layer(false, zoneId)),
                BitsOf(toCommon ? null : Layer(true, zoneId)), BitsOf(common));
            if (result.changed == 0) {
                return true;
            }

            NativeBitMask exclude = toCommon ? WritableCommon() : WritableExistingLayer(false, zoneId);
            NativeBitMask include = toCommon ? null : WritableExistingLayer(true, zoneId);
            beforeWrite?.Invoke();
            // 縁の切れ端は、範囲の中を消す前に消す(切れ端かどうかを、消す前のつながりで決める)。
            int crumbs = EraseCrumbs(region, BitsOf(exclude), BitsOf(include));
            result = MaskRegionEdit.Apply(MaskRegionOp.ClearHere, region, len, BitsOf(exclude), BitsOf(include), BitsOf(common));
            result.changed += crumbs;
            TrimPatches();
            return true;
        }

        // 範囲で消すと縁に残る細い切れ端を、2 つの層から消す(規則は MaskRegionEdit.EraseCrumbs)。消した画素数を返す。
        private int EraseCrumbs(bool* region, ulong* first, ulong* second) {
            if (!MaskRegionEdit.RegionBounds(region, Width, Height, out int x0, out int y0, out int x1, out int y1)) {
                return 0;
            }
            int reach = MaskRegionEdit.CrumbReach(Width, Height);
            MaskRegionEdit.CrumbWindow(Width, Height, x0, y0, x1, y1, reach, out int wx0, out int wy0, out int wx1, out int wy1);
            // 印の置き場(1 画素 1 バイト)は、範囲の周りの窓の大きさだけ。MarkNear が全部を書くので、中身は初期化しない。
            var near = NativeBools.AllocateUninitialized(wx1 - wx0, wy1 - wy0);
            try {
                MaskRegionEdit.MarkNear(region, Width, reach, (byte*)near.Ptr, wx0, wy0, wx1, wy1);
                var work = new List<int>();
                return MaskRegionEdit.EraseCrumbs(region, first, Width, Height, (byte*)near.Ptr, wx0, wy0, wx1, wy1, 0, work)
                     + MaskRegionEdit.EraseCrumbs(region, second, Width, Height, (byte*)near.Ptr, wx0, wy0, wx1, wy1, 1, work);
            } finally {
                near.Release();
            }
        }

        /// <summary>
        /// 画素 (cx, cy)(下原点)にあるマスクを消す(右クリックの「ここのマスクを消す」)。AI もメッシュも使わない。
        /// <para>
        /// 消す範囲は、その画素を塗った範囲の操作が塗った画素(記録のうちいちばん新しいもの。規則は
        /// <see cref="MaskRegionEdit.ErasePatch"/>)。使った記録は外す。記録が無ければ(ブラシで塗った・記録より前からあった)、
        /// つながっているひとかたまり(<see cref="MaskRegionEdit.ErasePieceAt"/>)。
        /// </para>
        /// zoneId = null は全ゾーン共通の除外。radius は、クリックの狙いの外れを許す幅(マスクの画素)。含めると除外の
        /// 両方が近くにあれば、近いほうだけを消す(同じ距離なら両方)。
        /// 書く直前に <paramref name="beforeWrite"/> を呼ぶ(消す物が無ければ呼ばず、何も書かない。そのとき、全ゾーン共通の
        /// 除外がそこにあれば result.commonOverlap を 1 にする)。寸法が決まっていなければ false。
        /// </summary>
        public bool ClearMaskAt(string zoneId, int cx, int cy, int radius, System.Action beforeWrite,
            out MaskRegionEditResult result) {
            result = default;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) {
                return false;
            }
            bool toCommon = zoneId == null;
            int pe = MaskRegionEdit.NearestBit(BitsOf(toCommon ? _common : Layer(false, zoneId)), w, h, cx, cy, radius, out int de);
            int pi = MaskRegionEdit.NearestBit(BitsOf(toCommon ? null : Layer(true, zoneId)), w, h, cx, cy, radius, out int di);
            if (de < 0 && di < 0) {
                if (!toCommon && MaskRegionEdit.AnyBitNear(BitsOf(_common), w, h, cx, cy, radius)) {
                    result.commonOverlap = 1;
                }
                return true;
            }
            int nearest = de < 0 ? di : di < 0 ? de : System.Math.Min(de, di);

            MaskPatch paintedExclude = de == nearest ? NewestPatchAt(zoneId, false, pe) : null;
            MaskPatch paintedInclude = di == nearest ? NewestPatchAt(zoneId, true, pi) : null;
            NativeBitMask exclude = de != nearest ? null : toCommon ? WritableCommon() : WritableExistingLayer(false, zoneId);
            NativeBitMask include = di != nearest ? null : WritableExistingLayer(true, zoneId);
            ulong* common = toCommon ? null : BitsOf(_common);
            beforeWrite?.Invoke();
            var work = new List<int>();
            ErasePaintedOrPiece(exclude, paintedExclude, cx, cy, nearest, common, work, ref result);
            ErasePaintedOrPiece(include, paintedInclude, cx, cy, nearest, common, work, ref result);
            TrimPatches();   // つながったかたまりで消したときは、ほかの記録の画素も消えていることがある
            return true;
        }

        private void ErasePaintedOrPiece(NativeBitMask layer, MaskPatch painted, int cx, int cy, int distance,
            ulong* common, List<int> work, ref MaskRegionEditResult result) {
            if (layer == null) {
                return;
            }
            if (painted == null) {
                MaskRegionEdit.ErasePieceAt(layer.Bits, Width, Height, cx, cy, distance, common, work, ref result);
                return;
            }
            var pixels = painted.Decode();
            try {
                MaskRegionEdit.ErasePatch(layer.Bits, pixels.Bits, layer.Length, common, ref result);
            } finally {
                pixels.Release();
            }
            // 塗った画素はもう残っていないので、記録も外す。
            _patches.Remove(painted);
            painted.Release();
        }

        private static ulong* BitsOf(NativeBitMask m) => m != null ? m.Bits : null;

        // ─────────────── 保存形式との変換 ───────────────

        /// <summary>
        /// 保存形式へ書き込む(<see cref="MaskStateCodec.Encode"/> と同じ文字列・同じ項目の並び)。全部 0 の層は書かない。
        /// </summary>
        public void EncodeTo(MaskState ms) {
            ms.width = Width;
            ms.height = Height;
            ms.commonMaskBase64 = MaskRle.EncodeBitsIfAny(_common) ?? "";

            if (ms.zones == null) {
                ms.zones = new List<MaskZoneEntry>();
            }
            ms.zones.Clear();
            AppendEntries(ms.zones, _excludes);

            if (ms.zoneIncludes == null) {
                ms.zoneIncludes = new List<MaskZoneEntry>();
            }
            ms.zoneIncludes.Clear();
            AppendEntries(ms.zoneIncludes, _includes);

            // 範囲の操作が塗った画素の記録。塗った画素がもう 1 つも残っていない記録は書かない(ふだんは TrimPatches が外している。
            // 層を直接書いた後、版の確定より前に保存が走ったときの守り)。
            if (ms.patches == null) {
                ms.patches = new List<MaskPatchEntry>();
            }
            ms.patches.Clear();
            foreach (var p in _patches) {
                if (p.AnyIn(LayerOf(p))) {
                    ms.patches.Add(new MaskPatchEntry { zoneId = p.ZoneId ?? "", include = p.Include, maskBase64 = p.Text });
                }
            }

            // 保存のたびに同じ文字列になるようにする。レシピの中身が揺れると .asset が書き換わり、シーンのプレビューとビルドのキャッシュの
            // 鍵も変わって作り直しになる(版はファイルへ書くときだけ刻んでいた。層が無いのに寸法だけ書くと、開いて保存するだけで変わる)。
            ms.schemaVersion = MaskState.CurrentSchemaVersion;
            if (ms.commonMaskBase64.Length == 0 && ms.zones.Count == 0 && ms.zoneIncludes.Count == 0) {
                ms.width = 0;
                ms.height = 0;
                ms.schemaVersion = 0;
            }
        }

        private static void AppendEntries(List<MaskZoneEntry> dst, Dictionary<string, NativeBitMask> src) {
            foreach (var kv in src) {
                if (string.IsNullOrEmpty(kv.Key)) {
                    continue;
                }
                string encoded = MaskRle.EncodeBitsIfAny(kv.Value);
                if (encoded == null) {
                    continue;
                }
                dst.Add(new MaskZoneEntry { zoneId = kv.Key, maskBase64 = encoded });
            }
        }

        /// <summary>
        /// 保存形式で置き換える(<see cref="MaskStateCodec.Decode"/> と同じ規則: 壊れた項目・寸法の合わない項目・ゾーン id の
        /// 無い項目は読み飛ばし、同じゾーン id は後の項目が勝つ)。層はまず全部外す。寸法が無効なら false(層なし・寸法は
        /// そのまま)。同じ寸法で中身が保存形式の文字列と同じ層は、展開し直さずにそのまま使う(Undo/Redo の書き戻しで
        /// 変わるのはふつう 1 枚だけ)。
        /// </summary>
        public bool DecodeFrom(MaskState ms) => DecodeFrom(ms, out _);

        /// <summary>
        /// <see cref="DecodeFrom(MaskState)"/> と同じで、読み飛ばした項目の数を <paramref name="dropped"/> に返す(壊れた文字列・
        /// 寸法の合わない項目・ゾーン id の無い項目。文字列が空の項目は層が無いだけなので数えない)。読み飛ばした項目はゾーンの id つきで
        /// 警告に出す。黙って捨てると、次の保存で保存先からも消えるので、読んだ側が数を見て元のファイルを退避する。
        /// </summary>
        public bool DecodeFrom(MaskState ms, out int dropped) {
            dropped = 0;
            var oldCommon = _common;
            var oldExcludes = new Dictionary<string, NativeBitMask>(_excludes);
            var oldIncludes = new Dictionary<string, NativeBitMask>(_includes);
            bool sameSize = ms != null && ms.width == Width && ms.height == Height;
            _common = null;
            _excludes.Clear();
            _includes.Clear();
            ReleasePatches();
            try {
                if (ms == null || ms.width <= 0 || ms.height <= 0) {
                    return false;
                }
                Width = ms.width;
                Height = ms.height;
                if (!string.IsNullOrEmpty(ms.commonMaskBase64)) {
                    _common = DecodeSized(ms.commonMaskBase64, sameSize ? oldCommon : null);
                    if (_common == null) {
                        dropped++;
                        WarnDropped(null, include: false);
                    }
                }
                dropped += ReadEntries(ms.zones, _excludes, sameSize ? oldExcludes : null, include: false);
                dropped += ReadEntries(ms.zoneIncludes, _includes, sameSize ? oldIncludes : null, include: true);
                ReadPatches(ms.patches);
                TrimPatches();   // 削る前の版(0.4.0 の開発中)や手で書いた保存の記録を、層に合わせる
                CapPatches();    // 手で書いた保存は、上限を超えて並べられる
                return true;
            } finally {
                // 使い回さなかった前の層を手放す(使い回した層は新しい持ち主へ移っている)。
                if (oldCommon != null && !Holds(oldCommon)) {
                    oldCommon.Release();
                }
                foreach (var kv in oldExcludes) {
                    if (!Holds(kv.Value)) {
                        kv.Value.Release();
                    }
                }
                foreach (var kv in oldIncludes) {
                    if (!Holds(kv.Value)) {
                        kv.Value.Release();
                    }
                }
            }
        }

        private bool Holds(NativeBitMask m) {
            if (ReferenceEquals(_common, m)) {
                return true;
            }
            foreach (var kv in _excludes) {
                if (ReferenceEquals(kv.Value, m)) {
                    return true;
                }
            }
            foreach (var kv in _includes) {
                if (ReferenceEquals(kv.Value, m)) {
                    return true;
                }
            }
            return false;
        }

        // 範囲の操作が塗った画素の記録を読む。読めない・層が無い記録は黙って捨てる(マスクそのものは変わらない。その場所の
        // 「ここのマスクを消す」が、つながったかたまりを消すだけになる)。古い版の保存には項目が無い(= 記録なし)。
        private void ReadPatches(List<MaskPatchEntry> src) {
            if (src == null) {
                return;
            }
            foreach (var entry in src) {
                if (entry == null) {
                    continue;
                }
                string zoneId = string.IsNullOrEmpty(entry.zoneId) ? null : entry.zoneId;
                var layer = zoneId == null ? (entry.include ? null : _common) : Layer(entry.include, zoneId);
                if (layer == null) {
                    continue;
                }
                var patch = MaskPatch.FromText(zoneId, entry.include, entry.maskBase64, Width, Height);
                if (patch != null) {
                    _patches.Add(patch);
                }
            }
        }

        // 読み飛ばした項目の数を返す。
        private int ReadEntries(List<MaskZoneEntry> src, Dictionary<string, NativeBitMask> dst,
            Dictionary<string, NativeBitMask> previous, bool include) {
            if (src == null) {
                return 0;
            }
            int dropped = 0;
            foreach (var entry in src) {
                if (entry == null) {
                    continue;
                }
                bool hasData = !string.IsNullOrEmpty(entry.maskBase64);
                if (string.IsNullOrEmpty(entry.zoneId)) {
                    if (hasData) {
                        dropped++;
                        WarnDropped("", include);
                    }
                    continue;
                }
                NativeBitMask candidate = null;
                previous?.TryGetValue(entry.zoneId, out candidate);
                var m = DecodeSized(entry.maskBase64, candidate);
                if (m == null) {
                    if (hasData) {
                        dropped++;
                        WarnDropped(entry.zoneId, include);
                    }
                    continue;
                }
                // 同じ id が 2 度出たら後のものが勝つ。前に置いたものが使い回しでなければ手放す。
                if (dst.TryGetValue(entry.zoneId, out var replaced) && !ReferenceEquals(replaced, m)
                    && !ReferenceEquals(replaced, candidate)) {
                    replaced.Release();
                }
                dst[entry.zoneId] = m;
            }
            return dropped;
        }

        // zoneId = null は全ゾーン共通の除外、空はゾーンの id が無い項目。
        private void WarnDropped(string zoneId, bool include) {
            string layer = zoneId == null ? "the all-zones exclusion mask" : include ? "an include mask" : "an exclude mask";
            string zone = string.IsNullOrEmpty(zoneId) ? "" : $" of zone {zoneId}";
            string reason = zoneId != null && zoneId.Length == 0 ? "it has no zone id"
                : $"its data is broken or its size does not match {Width}x{Height}";
            UnityEngine.Debug.LogWarning($"[Iroca] Skipped {layer}{zone}: {reason}.");
        }

        // 寸法がいまの Width/Height と同じときだけ採る。candidate(前に持っていた同じ層)を符号化すると encoded になるなら、
        // それをそのまま返す。
        private NativeBitMask DecodeSized(string encoded, NativeBitMask candidate) {
            if (candidate != null && candidate.Width == Width && candidate.Height == Height
                && MaskRle.EncodeBitsIfAny(candidate) == encoded) {
                return candidate;
            }
            var m = MaskRle.DecodeBits(encoded);
            if (m == null) {
                return null;
            }
            if (m.Width == Width && m.Height == Height) {
                return m;
            }
            m.Release();
            return null;
        }

        // ─────────────── 処理へ渡す ───────────────

        /// <summary>
        /// 処理用のスナップショット(<see cref="MaskSnapshot.FromBuffers"/> と同じ組み立て: 共通は無ければ null、ゾーンの除外は
        /// 全部、含めるは立っている画素がある層だけ)。層は参照で共有し(写さない)、書く側が書く前に写すので、この後に層を書き換えても変わらない。
        /// </summary>
        public MaskSnapshot ToSnapshot() {
            var snap = new MaskSnapshot {
                width = Width,
                height = Height,
                zones = new Dictionary<string, NativeBitMask>(),
            };
            snap.common = _common?.AddRef();
            foreach (var kv in _excludes) {
                snap.zones[kv.Key] = kv.Value.AddRef();
            }
            foreach (var kv in _includes) {
                if (!kv.Value.Any()) {
                    continue;
                }
                if (snap.zoneIncludes == null) {
                    snap.zoneIncludes = new Dictionary<string, NativeBitMask>();
                }
                snap.zoneIncludes[kv.Key] = kv.Value.AddRef();
            }
            return snap;
        }

        /// <summary>
        /// 共通の除外とゾーンの除外を合わせた bool[](自動調整に「ユーザーが外した所」として渡す)。どちらも無ければ null。
        /// </summary>
        public NativeBools CombinedExclusion(string zoneId) {
            var zone = Layer(false, zoneId);
            if (_common == null && zone == null) {
                return null;
            }
            int len = Width * Height;
            var result = NativeBools.AllocateUninitialized(Width, Height);
            bool* combined = result.Ptr;
            ulong* c = _common != null ? _common.Bits : null;
            ulong* z = zone != null ? zone.Bits : null;
            // 自動調整を押すたびにメインスレッドで全画素(4K 1,677 万・8K 6,700 万)を回すので、語(64 画素)の区間に
            // 分けて並列に展開する。画素ごとに独立なので、どう分けても中身は同じ。
            int words = (len + 63) >> 6;
            const int WordsPerChunk = 1 << 12;
            int chunks = (words + WordsPerChunk - 1) / WordsPerChunk;
            System.Action<int> expand = k => {
                int w0 = k * WordsPerChunk, w1 = System.Math.Min(words, w0 + WordsPerChunk);
                for (int w = w0; w < w1; w++) {
                    ulong v = (c != null ? c[w] : 0UL) | (z != null ? z[w] : 0UL);
                    int i0 = w << 6, n = System.Math.Min(64, len - i0);
                    for (int b = 0; b < n; b++) {
                        combined[i0 + b] = ((v >> b) & 1UL) != 0UL;
                    }
                }
            };
            if (chunks <= 1) {
                expand(0);
            } else {
                System.Threading.Tasks.Parallel.For(0, chunks,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = CoreParallelism.Max() }, expand);
            }
            return result;
        }
    }
}
