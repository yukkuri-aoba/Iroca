// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 右クリックメニューの「パーツ」操作(メッシュの UV の島・メッシュ単位でマスクへ足す)の状態。
    ///
    /// 編集中のテクスチャを使うメッシュを <see cref="MeshUvLocator"/> で探し、見つかれば
    /// マスクと同じ寸法の <see cref="UvChartMap"/> を作って持つ。見つからなければ何もしない
    /// (メニューは項目を無効表示にし、手動指定の導線だけを出す。色の処理は今まで通り)。
    /// 探索とチャート地図は重いので、テクスチャ単位でキャッシュする(初回の右クリック時に作る)。
    /// </summary>
    internal sealed class MeshPartController
    {
        IrocaWindow _host;
        MaskPaintView _maskView;

        Texture2D _searchedFor;
        bool _searched;
        MeshUvLocator.Result _found;
        UvChartMap _map;

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

        /// <summary>探し直す(シーンを開き直した・FBX の Read/Write を変えたあと)。</summary>
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
            return true;
        }

        void Reset()
        {
            _searched = false;
            _found = null;
            _map = null;
        }

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
