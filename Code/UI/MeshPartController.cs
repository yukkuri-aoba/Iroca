// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 右クリックメニューの「パーツ」操作(メッシュの UV の島・メッシュ単位でマスクへ足す)の状態。
    ///
    /// 編集中のテクスチャを使うメッシュを <see cref="MeshUvLocator"/> で探し、見つかれば
    /// マスクと同じ寸法の <see cref="UvChartMap"/> を作って持つ。見つからなければ何もしない
    /// (メニューは項目を無効表示にし、手動指定の導線だけを出す。色の処理は今まで通り)。
    /// 探索とチャート地図は重いので、テクスチャ単位でキャッシュする(マスク欄の「メッシュ」行か
    /// 初回の右クリックで作る)。
    /// </summary>
    internal sealed class MeshPartController
    {
        IrocaWindow _host;
        MaskPaintView _maskView;

        Texture2D _searchedFor;
        bool _searched;
        MeshUvLocator.Result _found;
        UvChartMap _map;
        // 手動で指定した物(マスク欄の「メッシュ」行や右クリックの「選択中の…を使う」)。自動なら null。
        Object _manualObject;
        // UV の島の輪郭(プレビューの重ね表示用)。_found と同じ寿命。
        Vector2[] _outline;

        public void Initialize(IrocaWindow host, MaskPaintView maskView)
        {
            _host = host;
            _maskView = maskView;
        }

        /// <summary>メッシュの探索結果(未探索なら探す)。使っているメッシュが無ければ null。</summary>
        public MeshUvLocator.Result Found
        {
            get
            {
                var tex = _host != null ? _host.SourceTexture : null;
                if (tex != _searchedFor)
                {
                    Reset();
                    _searchedFor = tex;
                }
                if (!_searched && tex != null)
                {
                    _found = MeshUvLocator.FindForTexture(tex);
                    _searched = true;
                }
                return _found;
            }
        }

        /// <summary>UV を読めるメッシュがあるか。</summary>
        public bool HasMesh => Found != null && Found.found.Count > 0;

        /// <summary>手動で指定したメッシュを使っているか。</summary>
        public bool IsManual => HasMesh && _manualObject != null;

        /// <summary>
        /// メッシュを見つけた元(表示用)。手動指定ならその物、自動なら最初のメッシュの一番上の親
        /// (シーンならアバターのルート、プロジェクトなら Prefab)。見つかっていなければ null。
        /// </summary>
        public Object SourceObject
        {
            get
            {
                if (!HasMesh) return null;
                if (_manualObject != null) return _manualObject;
                var renderer = _found.found[0].renderer;
                return renderer != null ? renderer.transform.root.gameObject : null;
            }
        }

        /// <summary>探し直す(シーンを開き直した・FBX の Read/Write を変えたあと)。手動の指定も外す。</summary>
        public void Research()
        {
            Reset();
            _searchedFor = _host != null ? _host.SourceTexture : null;
        }

        /// <summary>
        /// 手動指定(FBX・Prefab・シーンの GameObject)。使えるメッシュがあれば true。
        /// テクスチャを切り替えるまで有効(別のテクスチャを開いたら自動の探索に戻る)。
        /// </summary>
        public bool UseObject(Object obj)
        {
            var tex = _host != null ? _host.SourceTexture : null;
            var r = MeshUvLocator.FromObject(obj, tex);
            if (r == null || r.found.Count == 0) return false;
            Reset();
            _searchedFor = tex;
            _found = r;
            _searched = true;
            _manualObject = obj;
            return true;
        }

        void Reset()
        {
            _searched = false;
            _found = null;
            _map = null;
            _manualObject = null;
            _outline = null;
        }

        /// <summary>
        /// UV の島の輪郭(線分の端点の組。2 点で 1 本、UV 下原点)。メッシュが無ければ null。
        /// プレビューに重ねて、右クリックの「この島」がどこまでかを見せるのに使う。
        /// </summary>
        public Vector2[] IslandOutline()
        {
            if (!HasMesh) return null;
            return _outline ??= ComputeIslandOutline(_found.Sources());
        }

        /// <summary>
        /// UV の島の輪郭 = ほかの三角形と共有していない UV の辺(チャート分けと同じく、頂点番号でなく
        /// UV 座標で比べる)。左右対称のパーツが UV を重ねて使っていると同じ三角形が 2 回現れ、輪郭の辺まで
        /// 「2 つの三角形が共有する内側の辺」に数えられて消えるので、UV が完全に同じ三角形は 1 つにまとめてから数える。
        /// 戻り値は線分の端点の組(2 点で 1 本)で、マテリアルの Tiling / Offset を掛けた UV。
        /// </summary>
        internal static Vector2[] ComputeIslandOutline(IReadOnlyList<UvChartSource> sources)
        {
            var segments = new List<Vector2>();
            foreach (var src in sources)
            {
                if (src?.uv == null || src.triangles == null) continue;
                var uv = src.uv;
                var seenTriangles = new HashSet<(long, long, long)>();
                // 辺(量子化した端点の組) → 使っている三角形の数と、描くときの端点(頂点番号)
                var edges = new Dictionary<(long, long), (int count, int a, int b)>();
                int triCount = src.triangles.Length / 3;
                for (int t = 0; t < triCount; t++)
                {
                    int a = src.triangles[t * 3], b = src.triangles[t * 3 + 1], c = src.triangles[t * 3 + 2];
                    if ((uint)a >= (uint)uv.Length || (uint)b >= (uint)uv.Length || (uint)c >= (uint)uv.Length) continue;
                    long ka = UvChartMap.UvKey(uv[a]), kb = UvChartMap.UvKey(uv[b]), kc = UvChartMap.UvKey(uv[c]);
                    if (ka == kb || kb == kc || kc == ka) continue; // UV 上でつぶれた三角形
                    if (!seenTriangles.Add(Sorted(ka, kb, kc))) continue;
                    CountEdge(edges, ka, kb, a, b);
                    CountEdge(edges, kb, kc, b, c);
                    CountEdge(edges, kc, ka, c, a);
                }
                foreach (var e in edges.Values)
                {
                    if (e.count != 1) continue;
                    segments.Add(Tiled(src, uv[e.a]));
                    segments.Add(Tiled(src, uv[e.b]));
                }
            }
            return segments.ToArray();
        }

        static void CountEdge(Dictionary<(long, long), (int count, int a, int b)> edges, long ka, long kb, int a, int b)
        {
            var key = ka < kb ? (ka, kb) : (kb, ka);
            edges[key] = edges.TryGetValue(key, out var e) ? (e.count + 1, e.a, e.b) : (1, a, b);
        }

        static (long, long, long) Sorted(long a, long b, long c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        static Vector2 Tiled(UvChartSource s, Vector2 uv)
            => new Vector2(uv.x * s.scale.x + s.offset.x, uv.y * s.scale.y + s.offset.y);

        /// <summary>マスクと同じ寸法のチャート地図(にじみ代の割り当て済み)。メッシュが無ければ null。</summary>
        UvChartMap Map()
        {
            if (!HasMesh || _maskView == null) return null;
            _maskView.EnsureMasks();
            int w = _maskView.maskWidth, h = _maskView.maskHeight;
            if (w <= 0 || h <= 0) return null;
            if (_map == null || _map.Width != w || _map.Height != h)
            {
                _map = UvChartMap.Build(w, h, _found.Sources());
                _map.AssignPadding();
            }
            return _map;
        }

        /// <summary>右クリックで足す範囲。</summary>
        public enum Region
        {
            /// <summary>クリックした UV の島(とそのにじみ代)。</summary>
            Island,
            /// <summary>クリックしたメッシュ以外のすべての島(このメッシュだけを残す除外用)。</summary>
            OtherMeshes,
        }

        /// <summary>UV(下原点)の位置にあるメッシュの名前。島が無い場所なら null。</summary>
        public string MeshNameAt(float u, float v)
        {
            var map = Map();
            if (map == null) return null;
            int c = ChartAt(map, u, v);
            return c < 0 ? null : _found.found[map.ChartSource[c]].source.name;
        }

        /// <summary>
        /// UV(下原点)の位置から、マスクと同じ寸法・下原点の領域を作る。島が無い場所なら null。
        /// </summary>
        public bool[] RegionAt(float u, float v, Region kind)
        {
            var map = Map();
            if (map == null) return null;
            int c = ChartAt(map, u, v);
            if (c < 0) return null;
            int radius = map.DefaultPaddingRadius;
            return kind == Region.Island
                ? map.MaskOf(k => k == c, radius)
                : map.ExcludeOtherSources(new[] { map.ChartSource[c] }, radius);
        }

        static int ChartAt(UvChartMap map, float u, float v)
        {
            PreviewCoords.UvToPixel(u, v, map.Width, map.Height, out int x, out int y);
            return map.ChartAt(x, y, map.DefaultPaddingRadius);
        }
    }
}
