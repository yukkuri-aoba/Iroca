// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// <see cref="IrocaRecolor.shadowMatchMaterials"/> の実体。マテリアルの影の色を読み、複製へ塗り先に合わせた色を書く。
    /// 元のマテリアルには書き込まない。非破壊ビルド(<see cref="NonDestructiveApplier"/>)・シーンのプレビュー・
    /// いろかウィンドウの案内・インスペクタが同じ判断を使う。今は lilToon だけ(影の色の欄の名前がシェーダーごとに違う)。
    /// 合わせる色はマテリアルごとに選ぶ(<see cref="TryChooseColors"/>。そのマテリアルが映す所を一番多く塗ったゾーンの色)。
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
        /// マテリアル 1 つの影の色を合わせる色(サンプル色 → 変更先カラー)を選ぶ。影の色はマテリアルに 1 つなので、
        /// マテリアルが映す所(<paramref name="area"/>)を一番多く塗っているゾーンの色に合わせる(<see cref="ZoneShareGrid.Choose"/>)。
        /// 1 枚のテクスチャをパーツごとのマテリアルで共有していても、パーツごとに、そのパーツを塗った色になる。
        /// どのゾーンにも塗られていない所の方が多い(色替えしていないパーツ)・そのゾーンの色が決まっていない・
        /// <paramref name="grid"/> が無いときは false(影の色は変えない)。<paramref name="area"/> が null なら、テクスチャ全体で選ぶ。
        /// </summary>
        public static bool TryChooseColors(ZoneShareGrid grid, ShadowArea area, ZoneColors colors,
            out Color sample, out Color target) {
            sample = target = default;
            if (grid == null || colors == null) {
                return false;
            }
            string zone = grid.Choose(area != null ? area.On(grid) : null);
            return colors.TryGet(zone, out sample, out target);
        }

        /// <summary>
        /// レシピのゾーンの色(<see cref="ZoneColors"/>。読めないレシピは空)。非破壊ビルドは影を合わせる複製ごとに、シーンの
        /// プレビューは差し替えの枠ごとに呼ぶ。レシピの中身(マスク込みの JSON。4K で数 MB)は、中身が変わったときだけ読む
        /// (ゾーンだけ)。
        /// </summary>
        public static ZoneColors ColorsOf(IrocaRecipe recipe) {
            string json = recipe != null ? recipe.sessionJson : null;
            if (string.IsNullOrEmpty(json)) {
                return ZoneColors.Empty;
            }
            if (!s_recipeColors.TryGetValue(json, out var known) || known.formatVersion != recipe.formatVersion) {
                var zones = RecipeStore.LoadZones(recipe);
                var fresh = new RecipeColors {
                    formatVersion = recipe.formatVersion,
                    colors = zones != null ? ZoneColors.From(zones) : ZoneColors.Empty,
                };
                s_recipeColors.Remove(json);
                s_recipeColors.Add(json, fresh);
                known = fresh;
            }
            return known.colors;
        }

        // レシピの中身(sessionJson の文字列の参照)→ ゾーンの色。同じ参照なら中身も同じ。弱い参照の表なので、レシピが文字列を
        // 手放せば覚えた分も消える(RecipeTextureBuilder の鍵の覚えと同じ作り)。読めるかは形式の版でも変わるので、版も覚える。
        private sealed class RecipeColors {
            public int formatVersion;
            public ZoneColors colors;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, RecipeColors> s_recipeColors =
            new System.Runtime.CompilerServices.ConditionalWeakTable<string, RecipeColors>();

        // 1〜3 影の影色テクスチャ(Properties と同じ並び)。lilToon の影の色は lerp(メインの色, 影色テクスチャ, その α) × 影の色。
        private static readonly string[] TextureProperties = { "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex" };

        /// <summary>
        /// 影の色が掛かる先として、<paramref name="source"/>(色替えする元テクスチャ)と別の影色テクスチャが設定されているもの
        /// (重複なし)。そのテクスチャは色替えされないので、影はその色のまま残る(影の色を合わせても、掛かる先が古い色のまま)。
        /// 同じテクスチャなら一緒に色替えされるので入れない。使っていない影(影の色の α が 0)の欄も入れない。
        /// </summary>
        public static List<Texture> SeparateShadowTextures(Material material, Texture source) {
            var found = new List<Texture>();
            if (!IsEligible(material)) {
                return found;
            }
            for (int i = 0; i < TextureProperties.Length; i++) {
                if (!material.HasProperty(TextureProperties[i])) {
                    continue;
                }
                if (material.HasProperty(Properties[i]) && material.GetColor(Properties[i]).a <= 0f) {
                    continue;
                }
                var texture = material.GetTexture(TextureProperties[i]);
                if (texture != null && texture != source && !found.Contains(texture)) {
                    found.Add(texture);
                }
            }
            return found;
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
