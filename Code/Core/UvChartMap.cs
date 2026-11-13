// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// テクスチャを使うメッシュ(のサブメッシュ)1 つ分の UV 情報。Unity の Mesh には依存しない
    /// (Editor 側が Mesh から詰める。headless ハーネスはダンプから詰める)。
    /// </summary>
    internal sealed class UvChartSource {
        /// <summary>表示用の名前(Renderer 名など)。</summary>
        public string name;
        /// <summary>UV0(頂点ごと)。</summary>
        public Vector2[] uv;
        /// <summary>このサブメッシュの三角形(頂点番号 ×3)。</summary>
        public int[] triangles;
        /// <summary>マテリアルのメインテクスチャの Tiling / Offset(uv * scale + offset)。</summary>
        public Vector2 scale = Vector2.one;
        public Vector2 offset = Vector2.zero;

        /// <summary>
        /// UV に Tiling / Offset を掛けた、テクスチャ上の UV(0..1 が画像の端から端)。地図づくり・輪郭・Scene のクリックの
        /// 強調が同じ式を使う(式はここだけ)。
        /// </summary>
        public static Vector2 Tile(Vector2 uv, Vector2 scale, Vector2 offset) =>
            new Vector2(uv.x * scale.x + offset.x, uv.y * scale.y + offset.y);

        /// <summary>このメッシュの Tiling / Offset を掛けた、テクスチャ上の UV。</summary>
        public Vector2 Tile(Vector2 uv) => new Vector2(uv.x * scale.x + offset.x, uv.y * scale.y + offset.y);

        /// <summary>このメッシュの Tiling / Offset を掛けた、w×h の画像の画素の座標(下原点)。</summary>
        public Vector2 TileToPixel(Vector2 uv, int w, int h) =>
            new Vector2((uv.x * scale.x + offset.x) * w, (uv.y * scale.y + offset.y) * h);
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
    /// 持ち主の配列だけを持つ(4K で Raster・Owner・OwnerDistance の 192MB → 64MB)。島の数が 65535 未満なら、
    /// その配列を 1 画素 2 バイトに詰め直す(4K で 32MB・8K で 128MB。窓が開いている間ずっと持つので効く)。
    /// 画素ごとの配列はヒープの外に置くので、使い終わったら <see cref="Dispose"/> する。
    /// </summary>
    internal sealed unsafe class UvChartMap : System.IDisposable {
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
        /// AssignPaddingBaked の後は null のことがある(2 バイトに詰め直した持ち主は、この形では読めない)。焼き込んだ後の
        /// 持ち主は <see cref="ChartAt"/> / <see cref="MaskOf"/> で引く。
        /// </summary>
        public int* Owner => _owner != null ? (int*)_owner.Ptr : Raster;
        /// <summary>
        /// 画素から持ち主チャートの内側までの L1 距離(内側は 0)。AssignPadding 前は内側 0・外側 int.MaxValue
        /// (読んだときに作る。地図を引くだけなら作らない)。AssignPaddingBaked の後は null。
        /// </summary>
        public int* OwnerDistance {
            get {
                if (_distance == null && !_baked) {
                    int len = Width * Height;
                    _distance = new NativeBuffer((long)len * sizeof(int), NativeUsage.Other);
                    int* owner = Owner, dist = (int*)_distance.Ptr;
                    ForRanges(len, (i0, i1) => {
                        for (int i = i0; i < i1; i++) {
                            dist[i] = owner[i] >= 0 ? 0 : int.MaxValue;
                        }
                    });
                }
                return _distance != null ? (int*)_distance.Ptr : null;
            }
        }
        /// <summary>チャート番号 → 元の <see cref="UvChartSource"/> の番号。</summary>
        public int[] ChartSource { get; }
        public int ChartCount => ChartSource.Length;

        // チャート番号の配列。焼き込むと持ち主の配列になり(_owner へ移す)、null になる。
        private NativeBuffer _raster;
        // 持ち主の配列。null = Raster と同じ(にじみ代を割り当てていない)か、2 バイトに詰め直した(_owner16)。
        private NativeBuffer _owner;
        // 焼き込んだ持ち主を 1 画素 2 バイト(ushort。NoChart16 = どのチャートでもない)に詰め直したもの。島の数が
        // NoChart16 未満のときだけ。あるときは _raster・_owner・_distance は null。
        private NativeBuffer _owner16;
        private const ushort NoChart16 = ushort.MaxValue;
        // null = にじみ代を割り当てていない(内側 0・外側 int.MaxValue と同じ。OwnerDistance を読むと作る)か、焼き込み済み。
        private NativeBuffer _distance;
        // AssignPaddingBaked 済みか。済みなら Owner は半径 _bakedRadius の外を -1 にしたもので、距離は持たない。
        private bool _baked;
        private int _bakedRadius;

        private UvChartMap(int w, int h, NativeBuffer raster, int[] chartSource) {
            Width = w;
            Height = h;
            _raster = raster;
            ChartSource = chartSource;
        }

        /// <summary>ヒープの外の配列を解放する。何度呼んでもよい。</summary>
        public void Dispose() {
            _raster?.Release();
            _owner?.Release();
            _owner16?.Release();
            _distance?.Release();
            _raster = _owner = _owner16 = _distance = null;
        }

        /// <summary>
        /// sources の三角形を w×h のテクスチャ空間に描いてチャート地図を作る。
        /// UV が重なるチャート(左右で UV を共有するパーツ等)は後の source・後の三角形が勝つ。
        /// テクスチャ空間では区別できないので、マスクとしてはどちらでも同じ画素になる。
        /// UV が 0..1 の外にある三角形は、はみ出した部分を描かない(折り返さない)。
        /// </summary>
        public static UvChartMap Build(int w, int h, IReadOnlyList<UvChartSource> sources) {
            var mem = new NativeBuffer((long)w * h * sizeof(int), NativeUsage.Other);
            try {
                int* raster = (int*)mem.Ptr;
                ForRanges(w * h, (i0, i1) => new System.Span<int>(raster + i0, i1 - i0).Fill(-1));
                // 描くのは元の順のまま(後の source・後の三角形が勝つ)。
                var chartSource = new List<int>();
                ForEachTriangle(sources, w, h, chartSource, null,
                    (chart, a, b, c) => RasterizeTriangle(raster, w, h, a, b, c, chart));
                return new UvChartMap(w, h, mem, chartSource.ToArray());
            } catch {
                mem.Release();
                throw;
            }
        }

        /// <summary>三角形 1 つ: 島の通し番号(全メッシュを通した番号)と、3 頂点の画素の座標(下原点)。</summary>
        internal delegate void TriangleVisitor(int chart, Vector2 a, Vector2 b, Vector2 c);

        /// <summary>
        /// sources の三角形を、地図を描く順(メッシュの順 → その中の三角形の順)で 1 つずつ <paramref name="visit"/> へ渡す。
        /// 島の通し番号の振り方(メッシュごとの <see cref="ComputeCharts"/> の番号に、それまでの島の数を足す)と、頂点の画素の
        /// 座標(Tiling / Offset を掛けた UV × 寸法)を、地図(<see cref="Build"/>)と開発者向けの重ね表示(UvDetailOverlay)で
        /// 同じにするための下ごしらえ(以前は両方が同じ手順を別々に書いていた)。
        /// UV か三角形の無いメッシュと、頂点番号が範囲の外の三角形は飛ばす。chartSource に「島の通し番号 → メッシュの番号」を、
        /// chartLocal(null でよい)に「島の通し番号 → そのメッシュの中での島の番号」を足す。
        /// </summary>
        internal static void ForEachTriangle(IReadOnlyList<UvChartSource> sources, int w, int h,
            List<int> chartSource, List<int> chartLocal, TriangleVisitor visit, CancellationToken token = default) {
            // チャート分けはメッシュごとに独立(UV と三角形だけで決まる)なので、先に全メッシュの分を並列に求める。
            var charts = new int[sources.Count][];
            var chartCounts = new int[sources.Count];
            Parallel.For(0, sources.Count,
                new ParallelOptions { MaxDegreeOfParallelism = CoreParallelism.Max(), CancellationToken = token }, s => {
                    var src = sources[s];
                    if (src?.uv == null || src.triangles == null || src.triangles.Length < 3) {
                        return;
                    }
                    charts[s] = ComputeCharts(src.uv, src.triangles, out chartCounts[s]);
                });
            for (int s = 0; s < sources.Count; s++) {
                var src = sources[s];
                int[] local = charts[s];
                if (local == null) {
                    continue;
                }
                int baseId = chartSource.Count;
                for (int c = 0; c < chartCounts[s]; c++) {
                    chartSource.Add(s);
                    chartLocal?.Add(c);
                }

                var uv = src.uv;
                int triCount = src.triangles.Length / 3;
                for (int t = 0; t < triCount; t++) {
                    if ((t & 1023) == 0) {
                        token.ThrowIfCancellationRequested();
                    }
                    int a = src.triangles[t * 3], b = src.triangles[t * 3 + 1], c = src.triangles[t * 3 + 2];
                    if ((uint)a >= (uint)uv.Length || (uint)b >= (uint)uv.Length || (uint)c >= (uint)uv.Length) {
                        continue;
                    }
                    visit(baseId + local[t], src.TileToPixel(uv[a], w, h), src.TileToPixel(uv[b], w, h),
                        src.TileToPixel(uv[c], w, h));
                }
            }
        }

        /// <summary>
        /// 三角形 → チャート番号(0 始まり、チャートの最小三角形番号の昇順 = 決定的)。
        /// 頂点番号ではなく UV 座標(1e-5 で量子化)で辺を比べる。同じ頂点でも UV が別なら別チャート(縫い目)、
        /// 頂点が複製されていても UV が一致すれば同じチャートになる。
        /// </summary>
        internal static int[] ComputeCharts(Vector2[] uv, int[] triangles, out int chartCount) {
            int triCount = triangles.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) {
                parent[i] = i;
            }
            var firstTriOfEdge = new Dictionary<(long, long), int>(triCount * 2);
            for (int t = 0; t < triCount; t++) {
                for (int e = 0; e < 3; e++) {
                    int ia = triangles[t * 3 + e], ib = triangles[t * 3 + (e + 1) % 3];
                    if ((uint)ia >= (uint)uv.Length || (uint)ib >= (uint)uv.Length) {
                        continue;
                    }
                    long ka = UvKey(uv[ia]), kb = UvKey(uv[ib]);
                    var edge = ka < kb ? (ka, kb) : (kb, ka);
                    if (firstTriOfEdge.TryGetValue(edge, out int other)) {
                        Union(parent, t, other);
                    } else {
                        firstTriOfEdge.Add(edge, t);
                    }
                }
            }
            // 根は常に集合内の最小番号なので、根の出現順に番号を振ると「最小三角形番号の昇順」になる
            var ids = new int[triCount];
            var rootId = new Dictionary<int, int>();
            for (int t = 0; t < triCount; t++) {
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
        internal static long UvKey(Vector2 p) {
            long x = (long)Mathf.Round(p.x * UvQuant), y = (long)Mathf.Round(p.y * UvQuant);
            return x * UvKeyStride + y;
        }

        private static int Find(int[] parent, int a) {
            while (parent[a] != a) {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        private static void Union(int[] parent, int a, int b) {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a == b) {
                return;
            }
            if (a < b) {
                parent[b] = a;
            } else {
                parent[a] = b;
            }
        }

        /// <summary>画素中心 (x+0.5, y+0.5) が三角形の内側(辺上を含む)なら id を書く。向きは問わない。</summary>
        private static void RasterizeTriangle(int* raster, int w, int h, Vector2 p0, Vector2 p1, Vector2 p2, int id) {
            float area = (p1.x - p0.x) * (p2.y - p0.y) - (p1.y - p0.y) * (p2.x - p0.x);
            if (area == 0f || float.IsNaN(area)) {
                return;
            }
            float sign = area > 0f ? 1f : -1f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)) - 0.5f));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)) - 0.5f));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)) - 0.5f));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y)) - 0.5f));
            for (int y = y0; y <= y1; y++) {
                float py = y + 0.5f;
                int row = y * w;
                for (int x = x0; x <= x1; x++) {
                    float px = x + 0.5f;
                    float e0 = ((p1.x - p0.x) * (py - p0.y) - (p1.y - p0.y) * (px - p0.x)) * sign;
                    float e1 = ((p2.x - p1.x) * (py - p1.y) - (p2.y - p1.y) * (px - p1.x)) * sign;
                    float e2 = ((p0.x - p2.x) * (py - p2.y) - (p0.y - p2.y) * (px - p2.x)) * sign;
                    if (e0 >= 0f && e1 >= 0f && e2 >= 0f) {
                        raster[row + x] = id;
                    }
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
        public void AssignPadding(CancellationToken token = default) {
            if (_baked) {
                throw new System.InvalidOperationException("padding is already baked");
            }
            int len = Width * Height;
            var distMem = new NativeBuffer((long)len * sizeof(int), NativeUsage.Other);
            var ownerMem = new NativeBuffer((long)len * sizeof(int), NativeUsage.Other);
            try {
                int* dist = (int*)distMem.Ptr;
                ComputePadding(Raster, (int*)ownerMem.Ptr, dist, Width, Height, token);
                ForRanges(len, (i0, i1) => {
                    for (int i = i0; i < i1; i++) {
                        if (dist[i] >= PaddingInf) {
                            dist[i] = int.MaxValue;
                        }
                    }
                }, token);
            } catch {
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
        /// 島の数が 65535 未満なら、残した持ち主を 1 画素 2 バイトに詰め直す(そのとき <see cref="Owner"/> も null)。
        /// </summary>
        public void AssignPaddingBaked(int maxPadding) {
            if (_baked) {
                throw new System.InvalidOperationException("padding is already baked");
            }
            if (maxPadding < 0) {
                throw new System.ArgumentOutOfRangeException(nameof(maxPadding));
            }
            int* raster = Raster;
            int len = Width * Height;
            using (var distMem = new NativeBuffer((long)len * sizeof(int), NativeUsage.Work)) {
                int* dist = (int*)distMem.Ptr;
                ComputePadding(raster, raster, dist, Width, Height, default);
                ForRanges(len, (i0, i1) => {
                    for (int i = i0; i < i1; i++) {
                        if (dist[i] > maxPadding) {
                            raster[i] = -1;
                        }
                    }
                });
            }
            _owner?.Release();
            _distance?.Release();
            _owner = null;
            _distance = null;
            if (ChartCount < NoChart16) {
                // 島の番号が 2 バイトに収まる: 詰め直して、4 バイトの配列を返す(常駐が半分になる)。
                var packed = new NativeBuffer((long)len * sizeof(ushort), NativeUsage.Other);
                ushort* dst = (ushort*)packed.Ptr;
                ForRanges(len, (i0, i1) => {
                    for (int i = i0; i < i1; i++) {
                        int c = raster[i];
                        dst[i] = c < 0 ? NoChart16 : (ushort)c;
                    }
                });
                _owner16 = packed;
                _raster.Release();
            } else {
                _owner = _raster;
            }
            _raster = null;
            _baked = true;
            _bakedRadius = maxPadding;
        }

        private const int PaddingInf = int.MaxValue / 2;

        /// <summary>
        /// にじみ代の距離変換。dist に距離(届かない画素は <see cref="PaddingInf"/>)、owner に持ち主を書く。
        /// owner は raster と同じ配列でもよい(チャート画素の値は変わらず、読むのはチャート画素の値だけ)。
        /// </summary>
        private static void ComputePadding(int* raster, int* owner, int* dist, int w, int h, CancellationToken token) {
            const int Inf = PaddingInf;
            var po = new ParallelOptions {
                CancellationToken = token,
                MaxDegreeOfParallelism = CoreParallelism.Max(),
            };

            // 行パス: 同じ行の中で一番近いチャート画素(左からの走査 → 右からの走査で近い方)
            Parallel.For(0, h, po, y => {
                int row = y * w;
                int lastX = -1;
                for (int x = 0; x < w; x++) {
                    int i = row + x;
                    if (raster[i] >= 0) { lastX = x; dist[i] = 0; owner[i] = raster[i]; } else if (lastX >= 0) { dist[i] = x - lastX; owner[i] = raster[row + lastX]; } else { dist[i] = Inf; owner[i] = -1; }
                }
                // 右からの走査では、チャート画素を距離 0 で見分ける(owner が raster と同じ配列だと、
                // 外側の画素は左からの走査で持ち主の番号に書き換わっている)。
                lastX = -1;
                for (int x = w - 1; x >= 0; x--) {
                    int i = row + x;
                    if (dist[i] == 0) { lastX = x; continue; }
                    if (lastX >= 0 && lastX - x < dist[i]) { dist[i] = lastX - x; owner[i] = raster[row + lastX]; }
                }
            });

            // 列パス: min over y' (行距離(y') + |y - y'|) を前進/後退走査で合成する
            int dop = po.MaxDegreeOfParallelism;
            int chunk = (w + dop - 1) / dop;
            Parallel.For(0, dop, po, p => {
                int x0 = p * chunk, x1 = System.Math.Min(w, x0 + chunk);
                if (x0 >= x1) {
                    return;
                }
                for (int y = 1; y < h; y++) {
                    int row = y * w, prev = row - w;
                    for (int x = x0; x < x1; x++) {
                        if (dist[prev + x] >= Inf) {
                            continue;
                        }
                        int v = dist[prev + x] + 1;
                        if (v < dist[row + x]) { dist[row + x] = v; owner[row + x] = owner[prev + x]; }
                    }
                }
                for (int y = h - 2; y >= 0; y--) {
                    int row = y * w, next = row + w;
                    for (int x = x0; x < x1; x++) {
                        if (dist[next + x] >= Inf) {
                            continue;
                        }
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
        public int ChartAt(int x, int y, int maxPadding) {
            RequireRadius(maxPadding);
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) {
                return -1;
            }
            int i = y * Width + x;
            if (_owner16 != null) {
                ushort packed = ((ushort*)_owner16.Ptr)[i];
                return packed == NoChart16 ? -1 : packed;   // 焼き込み済み。半径の外はもう「どのチャートでもない」
            }
            int c = Owner[i];
            if (_baked) {
                return c;   // 半径の外はもう -1
            }
            // にじみ代を割り当てる前(距離を作っていない)は、持ち主のある画素 = チャートの内側(距離 0)。
            int d = _distance != null ? ((int*)_distance.Ptr)[i] : (c >= 0 ? 0 : int.MaxValue);
            return d <= maxPadding ? c : -1;
        }

        /// <summary>
        /// 持ち主チャートが select を満たす画素を true にしたマスク(行 0 = 下端)。
        /// maxPadding より遠いにじみ代は含めない。参照 1 で返す(呼び出し側が Release する)。
        /// </summary>
        public NativeBools MaskOf(System.Func<int, bool> select, int maxPadding) {
            RequireRadius(maxPadding);
            if (_owner16 != null) {
                // 焼き込んで詰め直した持ち主: 持ち主のある画素は半径の内側。
                var chosen16 = new bool[ChartCount];
                for (int c = 0; c < chosen16.Length; c++) {
                    chosen16[c] = select(c);
                }
                var mask16 = NativeBools.AllocateUninitialized(Width, Height);
                bool* m16 = mask16.Ptr;
                ushort* owner16 = (ushort*)_owner16.Ptr;
                ForRanges(Width * Height, (i0, i1) => {
                    for (int i = i0; i < i1; i++) {
                        ushort c = owner16[i];
                        m16[i] = c != NoChart16 && chosen16[c];
                    }
                });
                return mask16;
            }
            int* owner = Owner;
            int* dist = _distance != null ? (int*)_distance.Ptr : null;
            // 距離を持たないとき、持ち主のある画素は半径の内側(焼き込み済み)かチャートの内側(距離 0)。
            bool ownedWithin = _baked || maxPadding >= 0;
            var chosen = new bool[ChartCount];
            for (int c = 0; c < chosen.Length; c++) {
                chosen[c] = select(c);
            }
            var mask = NativeBools.AllocateUninitialized(Width, Height);
            bool* m = mask.Ptr;
            int len = Width * Height;
            ForRanges(len, (i0, i1) => {
                for (int i = i0; i < i1; i++) {
                    int c = owner[i];
                    m[i] = c >= 0 && (dist != null ? dist[i] <= maxPadding : ownedWithin) && chosen[c];
                }
            });
            return mask;
        }

        // [0, len) を区間に分けて並列に body(i0, i1) を呼ぶ(画素ごとに独立なループ用。どう分けても結果は同じ)。
        // 区間は 256K 画素以上にする(小さい地図は 1 本のまま)。地図は背景でも右クリックの待ちの中でも作るので、
        // 全画素の逐次ループ(4K で 1,677 万・8K で 6,700 万画素)を 1 本で回さない。
        private static void ForRanges(int len, System.Action<int, int> body, CancellationToken token = default) {
            const int Chunk = 1 << 18;
            int chunks = (len + Chunk - 1) / Chunk;
            if (chunks <= 1) {
                if (len > 0) {
                    body(0, len);
                }
                return;
            }
            var po = new ParallelOptions {
                CancellationToken = token,
                MaxDegreeOfParallelism = CoreParallelism.Max(),
            };
            Parallel.For(0, chunks, po, c => {
                int i0 = c * Chunk;
                body(i0, System.Math.Min(len, i0 + Chunk));
            });
        }

        private void RequireRadius(int maxPadding) {
            if (_baked && maxPadding != _bakedRadius) {
                throw new System.InvalidOperationException(
                    $"padding is baked for radius {_bakedRadius}, but {maxPadding} was requested");
            }
        }

        /// <summary>
        /// 「keepSources のメッシュだけ残す」除外マスク: 他のメッシュのチャート(とそのにじみ代)を true にする。
        /// 参照 1 で返す(呼び出し側が Release する)。
        /// </summary>
        public NativeBools ExcludeOtherSources(ICollection<int> keepSources, int maxPadding)
            => MaskOf(c => !keepSources.Contains(ChartSource[c]), maxPadding);
    }
}
