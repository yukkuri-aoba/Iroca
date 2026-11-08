// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// <see cref="IrocaRecolor.shadowMatchMaterials"/> の実体。マテリアルの影の色を読み、複製へ塗り先に合わせた色を書く。
    /// 元のマテリアルには書き込まない。非破壊ビルド(<see cref="NonDestructiveApplier"/>)・シーンのプレビュー・
    /// いろかウィンドウの案内・インスペクタが同じ判断を使う。今は lilToon だけ(影の色の欄の名前がシェーダーごとに違う)。
    /// </summary>
    internal static class MaterialShadowColors {
        // lilToon の 1〜3 影の色。掛かる先はメインテクスチャか影色テクスチャ。
        private static readonly string[] Properties = { "_ShadowColor", "_Shadow2ndColor", "_Shadow3rdColor" };

        /// <summary>影の色を合わせられるマテリアルか(lilToon で、影を使っている)。</summary>
        public static bool IsEligible(Material material) {
            if (material == null || material.shader == null) {
                return false;
            }
            if (MaterialColorSettings.FamilyOf(material) != MaterialColorSettings.Family.LilToon) {
                return false;
            }
            return material.HasProperty("_UseShadow") && material.GetFloat("_UseShadow") > 0.5f;
        }

        /// <summary>
        /// 合わせる色を決めるゾーン: 有効で、サンプルと変更先の色が決まっている最初のもの。
        /// 影の色はマテリアルに 1 つなので、複数の色へ塗り分けていてもこのゾーンの色に合わせる。
        /// </summary>
        public static bool TryGetColors(IList<ColorZone> zones, out Color sample, out Color target) {
            sample = target = default;
            if (zones == null) {
                return false;
            }
            for (int i = 0; i < zones.Count; i++) {
                var z = zones[i];
                if (z == null || !z.enabled || !z.sampleColorSet || !z.HasTargetColor) {
                    continue;
                }
                sample = z.sampleColor;
                target = z.targetColor;
                return true;
            }
            return false;
        }

        /// <summary><see cref="TryGetColors(IList{ColorZone}, out Color, out Color)"/> のレシピ版(読めないレシピは false)。</summary>
        public static bool TryGetColors(IrocaRecipe recipe, out Color sample, out Color target) {
            sample = target = default;
            var state = recipe != null ? RecipeStore.Load(recipe) : null;
            return state != null && TryGetColors(state.zones, out sample, out target);
        }

        /// <summary><paramref name="texture"/> がメインテクスチャか(影の色が掛かるのはメインの色なので、ほかの欄の色替えでは合わせない)。</summary>
        public static bool MainTextureIs(Material material, Texture texture) {
            if (material == null || texture == null) {
                return false;
            }
            string main = MaterialColorSettings.MainTextureProperty(material);
            return main != null && material.GetTexture(main) == texture;
        }

        /// <summary>今の色替えで、影の色を合わせると見た目が変わるか(いろかウィンドウが案内を出す条件)。</summary>
        public static bool NeedsMatch(Material material, Color sample, Color target) {
            if (!IsEligible(material)) {
                return false;
            }
            foreach (var name in Properties) {
                if (!material.HasProperty(name)) {
                    continue;
                }
                var shadow = material.GetColor(name);
                if (shadow.a > 0f && ShadowColorMatch.Differs(shadow, ShadowColorMatch.Compute(shadow, sample, target))) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// <paramref name="original"/> の影の色を塗り先に合わせた色を <paramref name="clone"/> へ書く。元の色から毎回求めるので、
        /// 塗り先の色を変えながら何度呼んでもよい。
        /// </summary>
        public static void Apply(Material clone, Material original, Color sample, Color target) {
            if (clone == null || original == null) {
                return;
            }
            foreach (var name in Properties) {
                if (original.HasProperty(name) && clone.HasProperty(name)) {
                    clone.SetColor(name, ShadowColorMatch.Compute(original.GetColor(name), sample, target));
                }
            }
        }

        /// <summary><paramref name="clone"/> の影の色を <paramref name="original"/> の色へ戻す(塗り先の色が決まっていないとき)。</summary>
        public static void Restore(Material clone, Material original) {
            if (clone == null || original == null) {
                return;
            }
            foreach (var name in Properties) {
                if (original.HasProperty(name) && clone.HasProperty(name)) {
                    clone.SetColor(name, original.GetColor(name));
                }
            }
        }

        /// <summary>インスペクタの見本用: 1 影の元の色と合わせた後の色。</summary>
        public static bool TryGetPreview(Material material, Color sample, Color target, out Color before, out Color after) {
            before = after = default;
            if (material == null || !material.HasProperty(Properties[0])) {
                return false;
            }
            before = material.GetColor(Properties[0]);
            after = ShadowColorMatch.Compute(before, sample, target);
            return true;
        }
    }
}
