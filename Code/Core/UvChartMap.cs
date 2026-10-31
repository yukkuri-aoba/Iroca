// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// テクスチャを使うメッシュ(のサブメッシュ)1 つ分の UV 情報。Unity の Mesh には依存しない
    /// (Editor 側が Mesh から詰める。headless ハーネスはダンプから詰める)。
    /// </summary>
    internal sealed class UvChartSource
    {
        /// <summary>表示用の名前(Renderer 名など)。</summary>
        public string name;
        /// <summary>UV0(頂点ごと)。</summary>
        public Vector2[] uv;
        /// <summary>このサブメッシュの三角形(頂点番号 ×3)。</summary>
        public int[] triangles;
        /// <summary>マテリアルのメインテクスチャの Tiling / Offset(uv * scale + offset)。</summary>
        public Vector2 scale = Vector2.one;
        public Vector2 offset = Vector2.zero;
    }

    /// <summary>
    /// テクスチャ空間の「UV チャート」地図。チャート = UV の辺を共有する三角形のまとまり
    /// (モデル上の 1 パーツの 1 片。1 パーツは普通いくつかのチャートからなる)。
    ///
    /// 色では分けられない同じ色の別パーツ(アトラスに並んだ同色のパンツとブーツ等)を、
    /// メッシュの形から分けるための情報源。選択そのものは色で行い、これは除外・含めるマスクを作るだけに使う。
    ///
    /// 画素は GetPixels32 順(行 0 = 画像下端、v = 0)。チャートの内側はラスタライズで、
    /// 島の外のにじみ代(テクスチャ作成時に島の色を外へ延ばした余白)は <see cref="AssignPadding"/> で
    /// 一番近いチャートに割り当てる。メッシュには乗らない画素だが、色の選択はそこまで及ぶので、
    /// 割り当てないと除外から漏れる(実測: パンツの除外で適合率 0.485 止まり → 割り当てて 1.000)。
    /// 引くにじみ代の半径が決まっているときは <see cref="AssignPaddingBaked"/> で、半径の外を落とした
    /// 持ち主の配列だけを持つ(4K で Raster・Owner・OwnerDistance の 192MB → 64MB)。
    /// 画素ごとの配列はヒープの外に置くので、使い終わったら <see cref="Dispose"/> する。
    /// </summary>
    internal sealed unsafe class UvChartMap : System.IDisposable
    {
        /// <summary>UV を辺の同一判定に使うときの量子化(1e-5)。</summary>
        private const float UvQuant = 100000f;
        /// <summary>
        /// 量子化した (x, y) を 1 つの long に詰めるときの x の係数(素数)。量子化後の |y| がこの半分
        /// (約 2.0e6。UvQuant = 1e5 で生の UV0 の |v| 約 20)未満なら (x, y) ごとに一意。それを超える UV0 では
        /// 別の点と同じキーになり得て、辺の同一判定が別のチャートを誤って結合する。
        /// </summary>
        private const long UvKeyStride = 4_000_037L;

        public int Width { get; }
        public int Height { get; }
        /// <summary>
        /// 画素ごとのチャート番号(ラスタライズした内側だけ。-1 = どのチャートにも乗らない)。
        /// <see cref="AssignPaddingBaked"/> の後は null(同じ配列を持ち主の配列に作り替えるので)。
        /// </summary>
        public int* Raster => _raster != null ? (int*)_raster.Ptr : null;
        /// <summary>
        /// 画素ごとの持ち主チャート(にじみ代を割り当てた後。-1 = 未割り当て)。AssignPadding 前は Raster と同じ。
        /// AssignPaddingBaked の後は、焼き込んだ半径より遠い画素も -1。
        /// </summary>
        public int* Owner => _owner != null ? (int*)_owner.Ptr : Raster;
        /// <summary>
        /// 画素から持ち主チャートの内側までの L1 距離(内側は 0)。AssignPadding 前は内側 0・外側 int.MaxValue
        /// (読んだときに作る。地図を引くだけなら作らない)。AssignPaddingBaked の後は null。
        /// </summary>
        public int* OwnerDistance
        {
            get
            {
                if (_distance == null && !_baked)
                {
                    int len = Width * Height;
                    _distance = new NativeBuffer((long)len * sizeof(int), NativeUsage.Other);
                    int* owner = Owner, dist = (int*)_distance.Ptr;
                    for (int i = 0; i < len; i++) dist[i] = owner[i] >= 0 ? 0 : int.MaxValue;
                }
                return _distance != null ? (int*)_distance.Ptr : null;
            }
        }
        /// <summary>チャート番号 → 元の <see cref="UvChartSource"/> の番号。</summary>
        public int[] ChartSource { get; }
        public int ChartCount => ChartSource.Length;

        // チャート番号の配列。焼き込むと持ち主の配列になり(_owner へ移す)、null になる。
        private NativeBuffer _raster;
        // 持ち主の配列。null = Raster と同じ(にじみ代を割り当てていない)。
        private NativeBuffer _owner;
        // null = にじみ代を割り当てていない(内側 0・外側 int.MaxValue と同じ。OwnerDistance を読むと作る)か、焼き込み済み。
        private NativeBuffer _distance;
        // AssignPaddingBaked 済みか。済みなら Owner は半径 _bakedRadius の外を -1 にしたもので、距離は持たない。
        private bool _baked;
        private int _bakedRadius;

        private UvChartMap(int w, int h, NativeBuffer raster, int[] chartSource)
        {
            Width = w;
            Height = h;
            _raster = raster;
            ChartSource = chartSource;
        }

        /// <summary>ヒープの外の配列を解放する。何度呼んでもよい。</summary>
        public void Dispose()
        {
            _raster?.Release();
            _owner?.Release();
            _distance?.Release();
            _raster = _owner = _distance = null;
        }

        /// <summary>
        /// sources の三角形を w×h のテクスチャ空間に描いてチャート地図を作る。
        /// UV が重なるチャート(左右で UV を共有するパーツ等)は後の source・後の三角形が勝つ。
        /// テクスチャ空間では区別できないので、マスクとしてはどちらでも同じ画素になる。
        /// UV が 0..1 の外にある三角形は、はみ出した部分を描かない(折り返さない)。
        /// </summary>
        public static UvChartMap Build(int w, int h, IReadOnlyList<UvChartSource> sources)
        {
            var mem = new NativeBuffer((long)w * h * sizeof(int), NativeUsage.Other);
            try
            {
                int* raster = (int*)mem.Ptr;
                new System.Span<int>(raster, w * h).Fill(-1);
                var chartSource = new List<int>();
                for (int s = 0; s < sources.Count; s++)
                {
                    var src = sources[s];
                    if (src?.uv == null || src.triangles == null || src.triangles.Length < 3) continue;
                    int[] local = ComputeCharts(src.uv, src.triangles, out int n);
                    int baseId = chartSource.Count;
                    for (int c = 0; c < n; c++) chartSource.Add(s);

                    int triCount = src.triangles.Length / 3;
                    for (int t = 0; t < triCount; t++)
                    {
                        int a = src.triangles[t * 3], b = src.triangles[t * 3 + 1], c = src.triangles[t * 3 + 2];
                        if ((uint)a >= (uint)src.uv.Length || (uint)b >= (uint)src.uv.Length
                            || (uint)c >= (uint)src.uv.Length) continue;
                        RasterizeTriangle(raster, w, h, Map(src, a, w, h), Map(src, b, w, h), Map(src, c, w, h),
                                          baseId + local[t]);
                    }
                }
                return new UvChartMap(w, h, mem, chartSource.ToArray());
            }
            catch
            {
                mem.Release();
                throw;
            }
        }

        private static Vector2 Map(UvChartSource s, int i, int w, int h)
        {
            Vector2 uv = s.uv[i];
            return new Vector2((uv.x * s.scale.x + s.offset.x) * w, (uv.y * s.scale.y + s.offset.y) * h);
        }

        /// <summary>
        /// 三角形 → チャート番号(0 始まり、チャートの最小三角形番号の昇順 = 決定的)。
        /// 頂点番号ではなく UV 座標(1e-5 で量子化)で辺を比べる。同じ頂点でも UV が別なら別チャート(縫い目)、
        /// 頂点が複製されていても UV が一致すれば同じチャートになる。
        /// </summary>
        internal static int[] ComputeCharts(Vector2[] uv, int[] triangles, out int chartCount)
        {
            int triCount = triangles.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;
            var firstTriOfEdge = new Dictionary<(long, long), int>(triCount * 2);
            for (int t = 0; t < triCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int ia = triangles[t * 3 + e], ib = triangles[t * 3 + (e + 1) % 3];
                    if ((uint)ia >= (uint)uv.Length || (uint)ib >= (uint)uv.Length) continue;
                    long ka = UvKey(uv[ia]), kb = UvKey(uv[ib]);
                    var edge = ka < kb ? (ka, kb) : (kb, ka);
                    if (firstTriOfEdge.TryGetValue(edge, out int other)) Union(parent, t, other);
                    else firstTriOfEdge.Add(edge, t);
                }
            }
            // 根は常に集合内の最小番号なので、根の出現順に番号を振ると「最小三角形番号の昇順」になる
            var ids = new int[triCount];
            var rootId = new Dictionary<int, int>();
            for (int t = 0; t < triCount; t++)
            {
                int r = Find(parent, t);
                if (!rootId.TryGetValue(r, out int id)) { id = rootId.Count; rootId.Add(r, id); }
                ids[t] = id;
            }
            chartCount = rootId.Count;
            return ids;
        }

        /// <summary>
        /// UV0(Tiling / Offset 前)を辺の同一判定に使うキー。チャート分け(<see cref="ComputeCharts"/>)と
        /// 島の輪郭(MeshPartController.ComputeIslandOutline)が共有する。
        /// </summary>
        internal static long UvKey(Vector2 p)
        {
            long x = (long)Mathf.Round(p.x * UvQuant), y = (long)Mathf.Round(p.y * UvQuant);
            return x * UvKeyStride + y;
        }

        private static int Find(int[] parent, int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a == b) return;
            if (a < b) parent[b] = a; else parent[a] = b;
        }

        /// <summary>画素中心 (x+0.5, y+0.5) が三角形の内側(辺上を含む)なら id を書く。向きは問わない。</summary>
        private static void RasterizeTriangle(int* raster, int w, int h, Vector2 p0, Vector2 p1, Vector2 p2, int id)
        {
            float area = (p1.x - p0.x) * (p2.y - p0.y) - (p1.y - p0.y) * (p2.x - p0.x);
            if (area == 0f || float.IsNaN(area)) return;
            float sign = area > 0f ? 1f : -1f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)) - 0.5f));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)) - 0.5f));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)) - 0.5f));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y)) - 0.5f));
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float e0 = ((p1.x - p0.x) * (py - p0.y) - (p1.y - p0.y) * (px - p0.x)) * sign;
                    float e1 = ((p2.x - p1.x) * (py - p1.y) - (p2.y - p1.y) * (px - p1.x)) * sign;
                    float e2 = ((p0.x - p2.x) * (py - p2.y) - (p0.y - p2.y) * (px - p2.x)) * sign;
                    if (e0 >= 0f && e1 >= 0f && e2 >= 0f) raster[row + x] = id;
                }
            }
        }

        /// <summary>
        /// にじみ代の割り当て: どのチャートにも乗らない画素を、L1 距離で一番近いチャートの持ち主にする
        /// (<see cref="Owner"/> / <see cref="OwnerDistance"/> を更新)。テクスチャのにじみ代は島の色を
        /// 外へ 1 画素ずつ延ばして作るので、「一番近い島のもの」がそのまま作り方の写しになる。
        ///
        /// 方式は SamMaskRefine.DistanceToOpposite と同じ分離型 city-block 距離変換
        /// (行パス → 列パスの前進/後退走査。正確な L1、行・列レンジ単位で並列化しても決定的)に、
        /// 距離と一緒に持ち主を運ばせたもの。同距離の持ち主は走査順で決まる(左 → 右、下 → 上が先)。
        /// </summary>
        public void AssignPadding(CancellationToken token = default)
        {
            if (_baked) throw new System.InvalidOperationException("padding is already baked");
            int len = Width * Height;
            var distMem = new NativeBuffer((long)len * sizeof(int), NativeUsage.Other);
            var ownerMem = new NativeBuffer((long)len * sizeof(int), NativeUsage.Other);
            try
            {
                int* dist = (int*)distMem.Ptr;
                ComputePadding(Raster, (int*)ownerMem.Ptr, dist, Width, Height, token);
                for (int i = 0; i < len; i++)
                    if (dist[i] >= PaddingInf) dist[i] = int.MaxValue;
            }
            catch
            {
                distMem.Release();
                ownerMem.Release();
                throw;
            }
            _owner?.Release();
            _distance?.Release();
            _owner = ownerMem;
            _distance = distMem;
        }

        /// <summary>
        /// <see cref="AssignPadding"/> と同じ割り当てをして、maxPadding より遠い画素の持ち主を -1 にした
        /// <see cref="Owner"/> だけを残す。持ち主の配列は Raster を作り替えて使い、距離も捨てるので、
        /// 以降 Raster と OwnerDistance は null で、引けるのはこの半径だけになる(<see cref="ChartAt"/> /
        /// <see cref="MaskOf"/> に別の半径を渡すと例外)。結果は AssignPadding してから同じ半径で引いたのと同じ。
        /// </summary>
        public void AssignPaddingBaked(int maxPadding)
        {
            if (_baked) throw new System.InvalidOperationException("padding is already baked");
            if (maxPadding < 0) throw new System.ArgumentOutOfRangeException(nameof(maxPadding));
            int* raster = Raster;
            int len = Width * Height;
            using (var distMem = new NativeBuffer((long)len * sizeof(int), NativeUsage.Work))
            {
                int* dist = (int*)distMem.Ptr;
                ComputePadding(raster, raster, dist, Width, Height, default);
                for (int i = 0; i < len; i++)
                    if (dist[i] > maxPadding) raster[i] = -1;
            }
            _owner?.Release();
            _distance?.Release();
            _owner = _raster;
            _raster = null;
            _distance = null;
            _baked = true;
            _bakedRadius = maxPadding;
        }

        private const int PaddingInf = int.MaxValue / 2;

        /// <summary>
        /// にじみ代の距離変換。dist に距離(届かない画素は <see cref="PaddingInf"/>)、owner に持ち主を書く。
        /// owner は raster と同じ配列でもよい(チャート画素の値は変わらず、読むのはチャート画素の値だけ)。
        /// </summary>
        private static void ComputePadding(int* raster, int* owner, int* dist, int w, int h, CancellationToken token)
        {
            const int Inf = PaddingInf;
            var po = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism(),
            };

            // 行パス: 同じ行の中で一番近いチャート画素(左からの走査 → 右からの走査で近い方)
            Parallel.For(0, h, po, y =>
            {
                int row = y * w;
                int lastX = -1;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (raster[i] >= 0) { lastX = x; dist[i] = 0; owner[i] = raster[i]; }
                    else if (lastX >= 0) { dist[i] = x - lastX; owner[i] = raster[row + lastX]; }
                    else { dist[i] = Inf; owner[i] = -1; }
                }
                // 右からの走査では、チャート画素を距離 0 で見分ける(owner が raster と同じ配列だと、
                // 外側の画素は左からの走査で持ち主の番号に書き換わっている)。
                lastX = -1;
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = row + x;
                    if (dist[i] == 0) { lastX = x; continue; }
                    if (lastX >= 0 && lastX - x < dist[i]) { dist[i] = lastX - x; owner[i] = raster[row + lastX]; }
                }
            });

            // 列パス: min over y' (行距離(y') + |y - y'|) を前進/後退走査で合成する
            int dop = po.MaxDegreeOfParallelism;
            int chunk = (w + dop - 1) / dop;
            Parallel.For(0, dop, po, p =>
            {
                int x0 = p * chunk, x1 = System.Math.Min(w, x0 + chunk);
                if (x0 >= x1) return;
                for (int y = 1; y < h; y++)
                {
                    int row = y * w, prev = row - w;
                    for (int x = x0; x < x1; x++)
                    {
                        if (dist[prev + x] >= Inf) continue;
                        int v = dist[prev + x] + 1;
                        if (v < dist[row + x]) { dist[row + x] = v; owner[row + x] = owner[prev + x]; }
                    }
                }
                for (int y = h - 2; y >= 0; y--)
                {
                    int row = y * w, next = row + w;
                    for (int x = x0; x < x1; x++)
                    {
                        if (dist[next + x] >= Inf) continue;
                        int v = dist[next + x] + 1;
                        if (v < dist[row + x]) { dist[row + x] = v; owner[row + x] = owner[next + x]; }
                    }
                }
            });
        }

        /// <summary>
        /// にじみ代として扱う最大距離(px)。これより遠い画素はどのチャートのものでもないとみなす。
        /// テクスチャのにじみ代は 2048 で 8〜16px 程度が多いので、長辺の 1/64(4096 で 64px)で余裕を持たせる。
        /// </summary>
        public int DefaultPaddingRadius => System.Math.Max(4, System.Math.Max(Width, Height) / 64);

        /// <summary>画素 (x, y)(行 0 = 下端)の持ち主チャート。maxPadding より遠いにじみ代・範囲外は -1。</summary>
        public int ChartAt(int x, int y, int maxPadding)
        {
            RequireRadius(maxPadding);
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return -1;
            int i = y * Width + x;
            int c = Owner[i];
            if (_baked) return c;   // 半径の外はもう -1
            // にじみ代を割り当てる前(距離を作っていない)は、持ち主のある画素 = チャートの内側(距離 0)。
            int d = _distance != null ? ((int*)_distance.Ptr)[i] : (c >= 0 ? 0 : int.MaxValue);
            return d <= maxPadding ? c : -1;
        }

        /// <summary>
        /// 持ち主チャートが select を満たす画素を true にしたマスク(行 0 = 下端)。
        /// maxPadding より遠いにじみ代は含めない。参照 1 で返す(呼び出し側が Release する)。
        /// </summary>
        public NativeBools MaskOf(System.Func<int, bool> select, int maxPadding)
        {
            RequireRadius(maxPadding);
            int* owner = Owner;
            int* dist = _distance != null ? (int*)_distance.Ptr : null;
            // 距離を持たないとき、持ち主のある画素は半径の内側(焼き込み済み)かチャートの内側(距離 0)。
            bool ownedWithin = _baked || maxPadding >= 0;
            var chosen = new bool[ChartCount];
            for (int c = 0; c < chosen.Length; c++) chosen[c] = select(c);
            var mask = NativeBools.AllocateUninitialized(Width, Height);
            bool* m = mask.Ptr;
            int len = Width * Height;
            for (int i = 0; i < len; i++)
            {
                int c = owner[i];
                m[i] = c >= 0 && (dist != null ? dist[i] <= maxPadding : ownedWithin) && chosen[c];
            }
            return mask;
        }

        private void RequireRadius(int maxPadding)
        {
            if (_baked && maxPadding != _bakedRadius)
                throw new System.InvalidOperationException(
                    $"padding is baked for radius {_bakedRadius}, but {maxPadding} was requested");
        }

        /// <summary>
        /// 「keepSources のメッシュだけ残す」除外マスク: 他のメッシュのチャート(とそのにじみ代)を true にする。
        /// 参照 1 で返す(呼び出し側が Release する)。
        /// </summary>
        public NativeBools ExcludeOtherSources(ICollection<int> keepSources, int maxPadding)
            => MaskOf(c => !keepSources.Contains(ChartSource[c]), maxPadding);
    }
}
