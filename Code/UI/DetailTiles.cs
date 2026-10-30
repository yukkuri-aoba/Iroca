// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 拡大表示のタイルの格子(0.4.0 の計画の 3.5 の案 A)。拡大表示の土台(DetailW×DetailH。行 0 = 画像の下端)を、
    /// 元の画素 <see cref="S"/> 四方のタイルに分け、1 枚を <see cref="T"/> 四方(画像の端のタイルは小さい)の画素にする。
    /// 縮めて見る倍率(画面の 1pt に元の 1 画素より多くが入る)では T &lt; S で、タイルごとに面平均で縮める。
    /// 格子は倍率と土台の寸法だけで決まり、見えている位置に依らないので、パンしても同じタイルは同じ画素になる
    /// (以前は見えている範囲の左下から縮めていたので、パンのたびに縮める区切りがずれていた)。
    /// </summary>
    internal readonly struct DetailTileGrid : IEquatable<DetailTileGrid>
    {
        // 縮めて見るときの、タイル 1 枚の画素数(一辺)。テストは小さい画像で格子を何枚にも分けるために変える。
        internal static int TileTexels = 256;

        public readonly int W, H;   // 土台の寸法
        public readonly int S;      // タイル 1 枚が覆う元の画素(一辺)
        public readonly int T;      // タイル 1 枚の画素(一辺)。縮めないなら S と同じ

        private DetailTileGrid(int w, int h, int s, int t)
        {
            W = w;
            H = h;
            S = s;
            T = t;
        }

        /// <summary>
        /// 土台 w×h を、元の 1 画素が画面の pxPerSrc pt になる倍率で見るときの格子。等倍以上は縮めない
        /// (元の画素をそのまま見せる。倍率が変わっても格子は同じ)。
        /// </summary>
        public static DetailTileGrid For(int w, int h, float pxPerSrc)
        {
            int t = Mathf.Max(1, TileTexels);
            if (pxPerSrc >= 1f) return new DetailTileGrid(w, h, t, t);
            int s = Mathf.Max(t, Mathf.RoundToInt(t / pxPerSrc));
            return new DetailTileGrid(w, h, s, Mathf.Clamp(Mathf.RoundToInt(s * pxPerSrc), 1, s));
        }

        public bool Shrinks => T < S;
        public int Columns => (W + S - 1) / S;
        public int Rows => (H + S - 1) / S;
        // 面平均の倍率(縮めた画素 ÷ 元の画素)。
        public float Scale => T / (float)S;

        /// <summary>元の画素の幅 span のタイル(画像の端のタイルは S より狭い)の画素数。</summary>
        public int TexelsFor(int span) => !Shrinks ? span : span == S ? T : Mathf.Clamp(Mathf.RoundToInt(span * Scale), 1, span);

        /// <summary>列 c の元の画素の範囲 [x0, x0 + span)。</summary>
        public void Column(int c, out int x0, out int span)
        {
            x0 = c * S;
            span = Mathf.Min(S, W - x0);
        }

        /// <summary>行 r の元の画素の範囲 [y0, y0 + span)。</summary>
        public void Row(int r, out int y0, out int span)
        {
            y0 = r * S;
            span = Mathf.Min(S, H - y0);
        }

        public bool Equals(DetailTileGrid o) => W == o.W && H == o.H && S == o.S && T == o.T;
        public override bool Equals(object obj) => obj is DetailTileGrid o && Equals(o);
        public override int GetHashCode() => ((W * 31 + H) * 31 + S) * 31 + T;
    }

    /// <summary>
    /// 拡大表示に並べるタイルの範囲(列 [C0, C1) × 行 [R0, R1))と、その画素を作る土台の版。同じ要求なら同じ画素になる
    /// (表示中と同じなら作り直さない)。
    /// </summary>
    internal readonly struct DetailTileRange : IEquatable<DetailTileRange>
    {
        public readonly DetailTileGrid Grid;
        public readonly int C0, R0, C1, R1;
        // 土台の中身の版(変更後は PreviewPipeline.DetailOutputVersion、変更前は DetailSourceVersion)。
        public readonly int Version;

        public DetailTileRange(DetailTileGrid grid, int c0, int r0, int c1, int r1, int version)
        {
            Grid = grid;
            C0 = c0;
            R0 = r0;
            C1 = c1;
            R1 = r1;
            Version = version;
        }

        /// <summary>
        /// 元の画素の範囲 [x0, x1) × [y0, y1) を覆うタイルの範囲。
        /// </summary>
        public static DetailTileRange Covering(DetailTileGrid grid, int x0, int y0, int x1, int y1, int version)
        {
            int c0 = Mathf.Clamp(x0 / grid.S, 0, grid.Columns - 1);
            int r0 = Mathf.Clamp(y0 / grid.S, 0, grid.Rows - 1);
            int c1 = Mathf.Clamp((x1 + grid.S - 1) / grid.S, c0 + 1, grid.Columns);
            int r1 = Mathf.Clamp((y1 + grid.S - 1) / grid.S, r0 + 1, grid.Rows);
            return new DetailTileRange(grid, c0, r0, c1, r1, version);
        }

        /// <summary>同じタイルの並びを、別の土台(版)で作る要求。</summary>
        public DetailTileRange WithVersion(int version) => new DetailTileRange(Grid, C0, R0, C1, R1, version);

        // 並べた 1 枚が覆う元の画素の範囲と、その画素数。
        public int OriginX => C0 * Grid.S;
        public int OriginY => R0 * Grid.S;
        public int SourceW => Mathf.Min(C1 * Grid.S, Grid.W) - OriginX;
        public int SourceH => Mathf.Min(R1 * Grid.S, Grid.H) - OriginY;
        // 端のタイルだけが小さいので、端を含むときだけ最後の 1 枚の分を数え直す。
        public int TexelsW => (C1 - C0 - 1) * Grid.T + Grid.TexelsFor(Mathf.Min(Grid.S, Grid.W - (C1 - 1) * Grid.S));
        public int TexelsH => (R1 - R0 - 1) * Grid.T + Grid.TexelsFor(Mathf.Min(Grid.S, Grid.H - (R1 - 1) * Grid.S));

        public bool Equals(DetailTileRange o)
            => Grid.Equals(o.Grid) && C0 == o.C0 && R0 == o.R0 && C1 == o.C1 && R1 == o.R1 && Version == o.Version;
        public override bool Equals(object obj) => obj is DetailTileRange o && Equals(o);
        public override int GetHashCode() => (((Grid.GetHashCode() * 31 + C0) * 31 + R0) * 31 + C1) * 31 + R1 ^ Version;
    }

    /// <summary>
    /// 縮めたタイルの控え(Mono のヒープの外)。持つのは 1 つの格子・1 つの版の分だけで、要求が替わったら(倍率・土台の
    /// 寸法・中身の版)前の分を捨てる。上限の枚数を超えたら、いちばん長く使っていないものから手放す。
    /// 縮めないタイル(等倍以上)は元の画素を写すだけなので控えない。
    /// どのスレッドからでも呼べる(切り出しのジョブ・先読みのジョブ・「元を表示」のメインスレッド)。
    /// </summary>
    internal sealed unsafe class DetailTileCache
    {
        // 4K の土台で、見えている範囲(枠いっぱい＋余白)と先読みの 1 周を足しても収まる枚数。1 枚 256KB なので 16MB まで。
        internal const int MaxTiles = 64;

        private readonly object _gate = new object();
        private readonly Dictionary<long, Entry> _tiles = new Dictionary<long, Entry>();
        private DetailTileGrid _grid;
        private int _version;
        private bool _targeted;
        private long _tick;

        private struct Entry
        {
            public NativePixels Pixels;
            public long Used;
        }

        /// <summary>縮めて作ったタイルの延べ枚数(テストが、控えを使ったかを見る)。</summary>
        internal int ComputedTiles;

        /// <summary>控えている枚数。</summary>
        internal int Count
        {
            get { lock (_gate) return _tiles.Count; }
        }

        /// <summary>
        /// これから作る要求(格子と土台の版)に合わせる。違えば前の分を捨てる。メインスレッドが要求を出すときに呼ぶ
        /// (前の要求で走っているジョブが後から作ったタイルは、控えに入らない)。
        /// </summary>
        internal void Retarget(DetailTileGrid grid, int version)
        {
            lock (_gate)
            {
                if (_targeted && _grid.Equals(grid) && _version == version) return;
                DropAllLocked();
                _grid = grid;
                _version = version;
                _targeted = true;
            }
        }

        /// <summary>控えを全部手放す(テクスチャを替えた・拡大表示をやめた・閉じた)。</summary>
        internal void Clear()
        {
            lock (_gate)
            {
                DropAllLocked();
                _targeted = false;
            }
        }

        private void DropAllLocked()
        {
            foreach (var e in _tiles.Values) e.Pixels.Release();
            _tiles.Clear();
        }

        /// <summary>控えに無ければ作る(縮めるタイルだけ)。控えにあるか、作って控えに入れたら true。</summary>
        internal bool Prefetch(DetailTileGrid grid, int version, int c, int r, NativePixels source)
        {
            var tile = Get(grid, version, c, r, source);
            if (tile == null) return false;
            tile.Release();
            return true;
        }

        /// <summary>
        /// タイル (c, r) の画素(参照を 1 つ足して返す。呼び出し側が手放す)。控えにあればそれ、無ければ土台 source から
        /// 縮めて作り、要求が今のものなら控えに入れる。
        /// </summary>
        internal NativePixels Get(DetailTileGrid grid, int version, int c, int r, NativePixels source)
        {
            long key = (long)r * grid.Columns + c;
            lock (_gate)
            {
                if (Current(grid, version) && _tiles.TryGetValue(key, out var hit))
                {
                    hit.Used = ++_tick;
                    _tiles[key] = hit;
                    return hit.Pixels.AddRef();
                }
            }

            var made = Make(grid, c, r, source);
            System.Threading.Interlocked.Increment(ref ComputedTiles);
            lock (_gate)
            {
                // 作っている間に要求が替わった(古い要求のジョブ)なら控えない。同じタイルを先に入れたものがあればそちらを使う。
                if (!Current(grid, version)) return made;
                if (_tiles.TryGetValue(key, out var other))
                {
                    made.Release();
                    other.Used = ++_tick;
                    _tiles[key] = other;
                    return other.Pixels.AddRef();
                }
                _tiles[key] = new Entry { Pixels = made.AddRef(), Used = ++_tick };
                if (_tiles.Count > MaxTiles) EvictOldestLocked();
                return made;
            }
        }

        private bool Current(DetailTileGrid grid, int version) => _targeted && _grid.Equals(grid) && _version == version;

        private void EvictOldestLocked()
        {
            long oldestKey = 0, oldestUsed = long.MaxValue;
            foreach (var kv in _tiles)
                if (kv.Value.Used < oldestUsed)
                {
                    oldestUsed = kv.Value.Used;
                    oldestKey = kv.Key;
                }
            if (_tiles.TryGetValue(oldestKey, out var e))
            {
                _tiles.Remove(oldestKey);
                e.Pixels.Release();
            }
        }

        /// <summary>タイル (c, r) を土台 source から縮めて作る(参照 1 で返す)。</summary>
        internal static NativePixels Make(DetailTileGrid grid, int c, int r, NativePixels source)
        {
            grid.Column(c, out int x0, out int sw);
            grid.Row(r, out int y0, out int sh);
            int tw = grid.TexelsFor(sw), th = grid.TexelsFor(sh);
            var tile = NativePixels.Allocate(tw, th);
            if (grid.Shrinks)
                PixelProcessor.BoxDownsampleRegionInto(source.Ptr, source.Length, grid.W, x0, y0, sw, sh,
                    tile.Ptr, tw, th, grid.Scale);
            else
                CopyRows(source.Ptr, grid.W, x0, y0, tile.Ptr, tw, 0, 0, sw, sh);
            return tile;
        }

        /// <summary>
        /// 範囲 range のタイルを並べた 1 枚(参照 1 で返す。寸法は range.TexelsW × TexelsH)。縮めるタイルは控え cache を
        /// 通し、縮めないタイルは土台から直接写す。取り消されたら途中で投げる。
        /// </summary>
        internal static NativePixels Assemble(DetailTileRange range, NativePixels source, DetailTileCache cache,
            System.Threading.CancellationToken token)
        {
            var grid = range.Grid;
            int outW = range.TexelsW, outH = range.TexelsH;
            var crop = NativePixels.Allocate(outW, outH);
            try
            {
                if (!grid.Shrinks)
                {
                    // 等倍以上: 範囲そのものを写す(見えている画素数ぶんなので小さい)。
                    CopyRows(source.Ptr, grid.W, range.OriginX, range.OriginY, crop.Ptr, outW, 0, 0, range.SourceW, range.SourceH);
                    return crop;
                }
                for (int r = range.R0; r < range.R1; r++)
                    for (int c = range.C0; c < range.C1; c++)
                    {
                        token.ThrowIfCancellationRequested();
                        var tile = cache.Get(grid, range.Version, c, r, source);
                        try
                        {
                            CopyRows(tile.Ptr, tile.Width, 0, 0, crop.Ptr, outW,
                                (c - range.C0) * grid.T, (r - range.R0) * grid.T, tile.Width, tile.Height);
                        }
                        finally { tile.Release(); }
                    }
                return crop;
            }
            catch
            {
                crop.Release();
                throw;
            }
        }

        // src(1 行 srcStride 画素)の (sx, sy) から w×h を、dst(1 行 dstStride 画素)の (dx, dy) へ行ごとに写す。
        private static void CopyRows(Color32* src, int srcStride, int sx, int sy,
            Color32* dst, int dstStride, int dx, int dy, int w, int h)
        {
            long rowBytes = (long)w * sizeof(Color32);
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)(sy + y) * srcStride + sx, dst + (long)(dy + y) * dstStride + dx, rowBytes, rowBytes);
        }
    }
}
