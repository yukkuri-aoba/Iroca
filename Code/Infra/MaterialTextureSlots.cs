// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// マテリアルのテクスチャの欄(メイン以外も全部)に、あるテクスチャが差してあるかを調べる。
    /// 非破壊ビルド(差し替える先を探す)と登録(元のテクスチャ・書き出した画像を使うマテリアルを探す)が同じ調べ方を
    /// 別々に持っていたので、ここに 1 つ置く。メインスレッドで呼ぶ(欄の ID の置き場を使い回すため)。
    /// </summary>
    internal static class MaterialTextureSlots {
        // 欄の ID の置き場。シーンの全マテリアルを見る所から呼ばれるので、呼ぶたびに配列を作らない。
        private static readonly List<int> s_ids = new List<int>();

        /// <summary>マテリアルがいずれかのテクスチャの欄で <paramref name="texture"/> を参照しているか。</summary>
        internal static bool References(Material material, Texture texture) {
            material.GetTexturePropertyNameIDs(s_ids);
            bool found = false;
            for (int i = 0; i < s_ids.Count && !found; i++) {
                found = material.GetTexture(s_ids[i]) == texture;
            }
            s_ids.Clear();
            return found;
        }

        /// <summary>マテリアルがいずれかのテクスチャの欄で <paramref name="textures"/> のどれかを参照しているか。</summary>
        internal static bool ReferencesAny(Material material, HashSet<Texture> textures) {
            if (textures == null || textures.Count == 0) {
                return false;
            }
            material.GetTexturePropertyNameIDs(s_ids);
            bool found = false;
            for (int i = 0; i < s_ids.Count && !found; i++) {
                var t = material.GetTexture(s_ids[i]);
                found = t != null && textures.Contains(t);
            }
            s_ids.Clear();
            return found;
        }
    }
}
