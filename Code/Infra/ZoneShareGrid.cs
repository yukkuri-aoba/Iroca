// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 色替えで、どのゾーンがテクスチャのどこを塗るかを、粗い格子で控えたもの。影の色を合わせるときに、マテリアルごとに
    /// どのゾーンの色に合わせるかを決める(<see cref="Choose"/>。<see cref="MaterialShadowColors"/>)。
    /// 1 枚のテクスチャ(アトラス)をパーツごとのマテリアルで共有していると、マテリアルごとに映る部分が違い、塗ったゾーンも違う。
    /// <para>
    /// 格子の各点の取り分は、合成と同じく上のゾーンから順に取り合った強さ(自動化の調査の割合と同じ考え方。
    /// IrocaAutomation.FillCoverage・MaskRegionEdit.PaintingZoneAt)。含めるマスクは全強度、除外は 0。透明な点は数えない。
    /// 選択の強さは、色替えの計算が控えたもの(<see cref="SelectionCache"/>)を読む。非破壊ビルドは標本だけを控える作り
    /// (<see cref="SelectionCache.ForSamples"/>)で同じ計算の中で取り(<see cref="RecipeZoneShares"/>)、編集中は
    /// プレビューの確定段の控えから取る(PreviewPipeline)。
    /// </para>
    /// 作った後は書き換えない(複数のスレッドから読んでよい)。
    /// </summary>
    internal sealed class ZoneShareGrid {
        /// <summary>格子の点の数の目安(自動化の調査の粗い格子と同じ数。4K でも 1 ゾーン 64KB で済み、細い部品も数点は乗る)。</summary>
        internal const int TargetCells = 65536;

        /// <summary>色替えの作業解像度(格子の点を画素へ写すのに使う)。</summary>
        public readonly int Width, Height;
        /// <summary>格子の寸法(テクスチャの縦横比を保つ。行 0 = 画像の下端。画素と同じ向き)。</summary>
        public readonly int GridW, GridH;
        // ゾーン(並び順。上ほど先に取る)の id と、点ごとの取り分(0〜255 = 0〜1)。
        private readonly string[] _zoneIds;
        private readonly byte[][] _shares;
        // 数える点か(不透明)。
        private readonly bool[] _counted;

        private ZoneShareGrid(int width, int height, int gridW, int gridH, string[] zoneIds, byte[][] shares, bool[] counted) {
            Width = width;
            Height = height;
            GridW = gridW;
            GridH = gridH;
            _zoneIds = zoneIds;
            _shares = shares;
            _counted = counted;
        }

        /// <summary>ゾーンの数。</summary>
        public int ZoneCount => _zoneIds.Length;

        /// <summary>w×h の画像に置く格子の寸法(点の数が <see cref="TargetCells"/> 以下。画像より細かくはしない)。</summary>
        internal static void GridSize(int w, int h, out int gridW, out int gridH) {
            double cells = (double)w * h;
            if (cells <= TargetCells) {
                gridW = w;
                gridH = h;
                return;
            }
            double scale = Math.Sqrt(TargetCells / cells);
            gridW = Math.Max(1, Math.Min(w, (int)(w * scale)));
            gridH = Math.Max(1, Math.Min(h, (int)(h * scale)));
        }

        /// <summary>格子の各点に当たる画素(y*w+x)。点の中心に一番近い画素。</summary>
        internal static int[] SampleIndices(int w, int h, int gridW, int gridH) {
            var indices = new int[gridW * gridH];
            for (int gy = 0; gy < gridH; gy++) {
                int y = Math.Min(h - 1, (int)((gy + 0.5) * h / gridH));
                for (int gx = 0; gx < gridW; gx++) {
                    int x = Math.Min(w - 1, (int)((gx + 0.5) * w / gridW));
                    indices[gy * gridW + gx] = y * w + x;
                }
            }
            return indices;
        }

        // 最後に作った格子の点の画素(同じ寸法なら使い回す。編集中は確定段のプレビューごとに作るので)。
        private static readonly object s_samplesGate = new object();
        private static int[] s_samples;
        private static int s_samplesW, s_samplesH;

        /// <summary>w×h の画像の格子(<see cref="GridSize"/>)と、その点の画素(<see cref="SampleIndices"/>。読むだけにする)。</summary>
        internal static int[] Samples(int w, int h, out int gridW, out int gridH) {
            GridSize(w, h, out gridW, out gridH);
            lock (s_samplesGate) {
                if (s_samples == null || s_samplesW != w || s_samplesH != h) {
                    s_samples = SampleIndices(w, h, gridW, gridH);
                    s_samplesW = w;
                    s_samplesH = h;
                }
                return s_samples;
            }
        }

        /// <summary>格子の点のうち数える点(不透明)。色替えの前の画素で見る。</summary>
        internal static unsafe bool[] Opaque(Color32* pixels, int[] samples) {
            var counted = new bool[samples.Length];
            for (int i = 0; i < samples.Length; i++) {
                counted[i] = pixels == null || pixels[samples[i]].a >= PixelProcessor.OpaqueAlphaMin;
            }
            return counted;
        }

        /// <summary>
        /// 色替えの計算が控えた選択(<paramref name="selection"/>)から作る。<paramref name="samples"/> は w×h の画像の格子の点の画素
        /// (<see cref="Samples"/>)。<paramref name="sampled"/> なら selection は samples だけを控える作り
        /// (<see cref="SelectionCache.ForSamples"/>)で、そうでなければ全画素を控える作り。<paramref name="zones"/> は計算に渡した
        /// ゾーン(並び順)、<paramref name="masks"/> は計算に渡したマスク(含めるマスクを読む。null 可)、<paramref name="counted"/> は
        /// 数える点(<see cref="Opaque"/>)。選択を控えていないゾーンは、どこも塗らないものとして扱う。
        /// 計算が終わった後に、計算と同じスレッドか、計算を待った後に呼ぶ。
        /// </summary>
        internal static ZoneShareGrid Capture(SelectionCache selection, bool sampled, int[] samples, int w, int h,
            IList<ColorZone> zones, MaskSnapshot masks, bool[] counted) {
            GridSize(w, h, out int gridW, out int gridH);
            int n = samples.Length;
            var ids = new string[zones.Count];
            var shares = new byte[zones.Count][];
            var buffer = new float[n];
            // 上のゾーンから順に取り合う(FromStrengths と同じ。ゾーンごとの強さの配列を並べて持たない)。
            var room = new float[n];
            for (int i = 0; i < n; i++) {
                room[i] = 1f;
            }
            for (int k = 0; k < zones.Count; k++) {
                string id = zones[k].id;
                ids[k] = id ?? "";
                shares[k] = new byte[n];
                if (string.IsNullOrEmpty(id) || selection == null) {
                    continue;
                }
                bool read = sampled
                    ? selection.TryReadSamples(id, w, h, buffer)
                    : selection.TryReadLatest(id, w, h, samples, buffer);
                if (!read) {
                    continue;
                }
                ApplyInclude(buffer, samples, w, h, masks, id);
                var share = shares[k];
                for (int i = 0; i < n; i++) {
                    float take = Math.Min(Math.Max(buffer[i], 0f), room[i]);
                    room[i] -= take;
                    share[i] = (byte)Math.Round(take * 255f);
                }
            }
            return new ZoneShareGrid(w, h, gridW, gridH, ids, shares, counted);
        }

        // 含めるマスク(除外されていない所)の点を全強度にする(合成と同じく、除外が勝つ。控えの強さは除外を掛けたもの)。
        // マスクの座標の対応は PixelProcessor.BuildMaskIndexMap・SelectionCache と同じ。
        private static unsafe void ApplyInclude(float[] s, int[] samples, int w, int h, MaskSnapshot masks, string zoneId) {
            if (masks == null || masks.zoneIncludes == null || !masks.zoneIncludes.TryGetValue(zoneId, out var include)
                || include == null || masks.width <= 0 || masks.height <= 0) {
                return;
            }
            ulong* inc = NativeBitMask.BitsOf(include);
            ulong* common = NativeBitMask.BitsOf(masks.common);
            ulong* exclude = null;
            if (masks.zones != null && masks.zones.TryGetValue(zoneId, out var ex)) {
                exclude = NativeBitMask.BitsOf(ex);
            }
            int mw = masks.width, mh = masks.height;
            for (int i = 0; i < samples.Length; i++) {
                int x = samples[i] % w, y = samples[i] / w;
                int mi = Math.Min(Math.Max(y * mh / h, 0), mh - 1) * mw + Math.Min(Math.Max(x * mw / w, 0), mw - 1);
                if (MaskSnapshot.GetBit(inc, mi) && !MaskSnapshot.GetBit(exclude, mi) && !MaskSnapshot.GetBit(common, mi)) {
                    s[i] = 1f;
                }
            }
        }

        /// <summary>
        /// ゾーンごとの点の強さ(<paramref name="strengths"/>[ゾーン][点]。null はどこも塗らない)から作る。上のゾーンから順に
        /// 取り合う(合成と同じ)。<paramref name="counted"/> は数える点(不透明)。
        /// </summary>
        internal static ZoneShareGrid FromStrengths(int w, int h, int gridW, int gridH, string[] zoneIds, float[][] strengths,
            bool[] counted) {
            int n = gridW * gridH;
            var shares = new byte[zoneIds.Length][];
            for (int k = 0; k < zoneIds.Length; k++) {
                shares[k] = new byte[n];
            }
            for (int i = 0; i < n; i++) {
                float room = 1f;
                for (int k = 0; k < zoneIds.Length && room > 0f; k++) {
                    var s = strengths[k];
                    if (s == null) {
                        continue;
                    }
                    float take = Math.Min(Math.Max(s[i], 0f), room);
                    room -= take;
                    shares[k][i] = (byte)Math.Round(take * 255f);
                }
            }
            return new ZoneShareGrid(w, h, gridW, gridH, zoneIds, shares, counted);
        }

        /// <summary>
        /// <paramref name="area"/>(格子の点ごと。この格子の寸法の、マテリアルが映す所。null か 1 点も無ければテクスチャ全体)を
        /// 一番多く塗っているゾーンの id。どのゾーンにも塗られていない所の方が多い(か同じ)なら null(影の色は元の色に
        /// 合っているので変えない)。不透明な点が無いときも null。
        /// </summary>
        public string Choose(bool[] area) {
            bool whole = area == null || area.Length != _counted.Length || !Any(area);
            long counted = 0;
            var sums = new long[_zoneIds.Length];
            for (int i = 0; i < _counted.Length; i++) {
                if (!_counted[i] || (!whole && !area[i])) {
                    continue;
                }
                counted++;
                for (int k = 0; k < sums.Length; k++) {
                    sums[k] += _shares[k][i];
                }
            }
            if (counted == 0) {
                return null;
            }
            long painted = 0;
            int best = -1;
            for (int k = 0; k < sums.Length; k++) {
                painted += sums[k];
                if (best < 0 || sums[k] > sums[best]) {
                    best = k;
                }
            }
            long unpainted = counted * 255 - painted;
            if (best < 0 || sums[best] <= unpainted) {
                return null;
            }
            return _zoneIds[best];
        }

        private static bool Any(bool[] a) {
            foreach (bool b in a) {
                if (b) {
                    return true;
                }
            }
            return false;
        }

        // ───────────── ファイルへの保存(非破壊ビルドのキャッシュ。RecipeZoneShares) ─────────────

        private const int FileMagic = 0x315A5349;   // "ISZ1"

        internal void Write(Stream stream) {
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) {
                w.Write(FileMagic);
                w.Write(Width);
                w.Write(Height);
                w.Write(GridW);
                w.Write(GridH);
                w.Write(_zoneIds.Length);
                for (int k = 0; k < _zoneIds.Length; k++) {
                    w.Write(_zoneIds[k]);
                    w.Write(_shares[k]);
                }
                var packed = new byte[(_counted.Length + 7) / 8];
                for (int i = 0; i < _counted.Length; i++) {
                    if (_counted[i]) {
                        packed[i >> 3] |= (byte)(1 << (i & 7));
                    }
                }
                w.Write(packed);
            }
        }

        /// <summary>読めなければ(形式が違う・壊れている)null。</summary>
        internal static ZoneShareGrid Read(Stream stream) {
            try {
                using (var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true)) {
                    if (r.ReadInt32() != FileMagic) {
                        return null;
                    }
                    int width = r.ReadInt32(), height = r.ReadInt32(), gridW = r.ReadInt32(), gridH = r.ReadInt32();
                    int zoneCount = r.ReadInt32();
                    if (width <= 0 || height <= 0 || gridW <= 0 || gridH <= 0 || (long)gridW * gridH > TargetCells * 4L
                        || zoneCount < 0 || zoneCount > 1024) {
                        return null;
                    }
                    int n = gridW * gridH;
                    var ids = new string[zoneCount];
                    var shares = new byte[zoneCount][];
                    for (int k = 0; k < zoneCount; k++) {
                        ids[k] = r.ReadString();
                        shares[k] = r.ReadBytes(n);
                        if (shares[k].Length != n) {
                            return null;
                        }
                    }
                    var packed = r.ReadBytes((n + 7) / 8);
                    if (packed.Length != (n + 7) / 8) {
                        return null;
                    }
                    var counted = new bool[n];
                    for (int i = 0; i < n; i++) {
                        counted[i] = (packed[i >> 3] & (1 << (i & 7))) != 0;
                    }
                    return new ZoneShareGrid(width, height, gridW, gridH, ids, shares, counted);
                }
            } catch (EndOfStreamException) {
                return null;
            } catch (IOException) {
                return null;
            }
        }
    }

    /// <summary>
    /// ゾーンの id → 影の色を合わせる色の組(サンプル色 → 変更先カラー)。有効で、サンプル色と変更先カラーが決まっているゾーンだけ。
    /// 作った後は書き換えない。
    /// </summary>
    internal sealed class ZoneColors {
        private readonly Dictionary<string, (Color sample, Color target)> _byId = new Dictionary<string, (Color, Color)>();

        public static readonly ZoneColors Empty = new ZoneColors();

        public static ZoneColors From(IList<ColorZone> zones) {
            var c = new ZoneColors();
            if (zones == null) {
                return c;
            }
            foreach (var z in zones) {
                if (Usable(z) && !c._byId.ContainsKey(z.id)) {
                    c._byId.Add(z.id, (z.sampleColor, z.targetColor));
                }
            }
            return c;
        }

        private static bool Usable(ColorZone z)
            => z != null && z.enabled && z.sampleColorSet && z.HasTargetColor && !string.IsNullOrEmpty(z.id);

        public bool IsEmpty => _byId.Count == 0;

        public bool TryGet(string zoneId, out Color sample, out Color target) {
            sample = target = default;
            if (zoneId == null || !_byId.TryGetValue(zoneId, out var pair)) {
                return false;
            }
            sample = pair.sample;
            target = pair.target;
            return true;
        }

        /// <summary><see cref="From"/>(<paramref name="zones"/>) と同じ中身か(作らずに比べる。毎フレーム呼ぶ所で使う)。</summary>
        public bool SameAs(IList<ColorZone> zones) {
            int count = 0;
            if (zones != null) {
                foreach (var z in zones) {
                    if (!Usable(z)) {
                        continue;
                    }
                    if (!_byId.TryGetValue(z.id, out var pair)) {
                        return false;
                    }
                    if (pair.sample != z.sampleColor || pair.target != z.targetColor) {
                        // 同じ id が 2 つあると、先の方だけが入っている(From と同じ)。
                        return false;
                    }
                    count++;
                }
            }
            return count == _byId.Count;
        }
    }
}
