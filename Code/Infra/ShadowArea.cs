// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// マテリアルがテクスチャのどこを映すか(そのマテリアルで描くサブメッシュの UV)。影の色をマテリアルごとに合わせるとき、
    /// そこを一番多く塗っているゾーンを選ぶのに使う(<see cref="MaterialShadowColors.TryChooseColors"/>)。
    /// UV は <see cref="Of"/> で読み(同じ顔ぶれなら読み直さない)、格子(<see cref="ZoneShareGrid"/>)の寸法で塗りつぶしたものを
    /// 覚える(<see cref="On"/>)。メインスレッドで使う。
    /// </summary>
    internal sealed class ShadowArea {
        private readonly List<UvChartSource> _sources;
        private bool[] _area;
        private int _areaW, _areaH;

        private ShadowArea(List<UvChartSource> sources) {
            _sources = sources;
        }

        /// <summary>UV を読めたサブメッシュが 1 つも無い(どこを映すか分からない。<see cref="On"/> は null)。</summary>
        public bool IsEmpty => _sources.Count == 0;

        // マテリアル → 前に読んだ顔ぶれの指紋と結果(同じ顔ぶれなら読み直さない)。数が増えすぎたら全部捨てる。
        private static readonly Dictionary<Material, (long signature, ShadowArea area)> s_known =
            new Dictionary<Material, (long, ShadowArea)>();
        private const int MaxKnown = 256;

        /// <summary>
        /// <paramref name="renderers"/> のうち <paramref name="material"/> で描くサブメッシュから作る(<see cref="MeshUvLocator.SourcesUsing"/>。
        /// <paramref name="rootOf"/> も同じ)。同じマテリアル・同じ顔ぶれ(メッシュ・サブメッシュ・Tiling / Offset)なら前のものを返す。
        /// </summary>
        public static ShadowArea Of(Material material, IEnumerable<Renderer> renderers,
            System.Func<Material, Material> rootOf = null) {
            if (material == null) {
                return new ShadowArea(new List<UvChartSource>());
            }
            long signature = MeshUvLocator.SourcesSignature(material, renderers, rootOf);
            if (s_known.TryGetValue(material, out var known) && known.signature == signature) {
                return known.area;
            }
            var area = new ShadowArea(MeshUvLocator.SourcesUsing(material, renderers, rootOf));
            if (s_known.Count >= MaxKnown) {
                s_known.Clear();
            }
            s_known[material] = (signature, area);
            return area;
        }

        /// <summary>
        /// <paramref name="grid"/> の格子の点ごとに、このマテリアルが映すか。UV が無ければ null(呼ぶ側はテクスチャ全体で選ぶ)。
        /// 同じ寸法の格子なら前の結果を返す。
        /// </summary>
        public unsafe bool[] On(ZoneShareGrid grid) {
            if (grid == null || _sources.Count == 0) {
                return null;
            }
            if (_area != null && _areaW == grid.GridW && _areaH == grid.GridH) {
                return _area;
            }
            var area = new bool[grid.GridW * grid.GridH];
            // 格子の寸法で UV を塗る(画素の向きと同じく行 0 = 下端。格子の点の位置は ZoneShareGrid.SampleIndices と同じ)。
            using (var map = UvChartMap.Build(grid.GridW, grid.GridH, _sources)) {
                int* raster = map.Raster;
                for (int i = 0; i < area.Length; i++) {
                    area[i] = raster[i] >= 0;
                }
            }
            _area = area;
            _areaW = grid.GridW;
            _areaH = grid.GridH;
            return area;
        }
    }
}
