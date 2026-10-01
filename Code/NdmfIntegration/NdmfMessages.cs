// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;

namespace Iroca.NdmfIntegration
{
    /// <summary>
    /// ビルド時の問題を NDMF のエラー画面へ出す(日本語・英語)。表示言語は NDMF 側の言語設定に従う。
    /// キーは NDMF の規約どおり、タイトル = key、説明 = key:description、対処 = key:hint。{0} はコンポーネント。
    /// </summary>
    internal static class NdmfMessages
    {
        private static readonly Dictionary<string, string> Ja = new Dictionary<string, string>
        {
            ["iroca.missing_recipe"] = "いろか: レシピが設定されていません",
            ["iroca.missing_recipe:description"] = "{0} の「レシピ」が空なので、何も色替えしませんでした。",
            ["iroca.missing_recipe:hint"] = "いろかウィンドウの「アバターに非破壊で登録」で作り直すか、レシピを設定してください。",

            ["iroca.texture_not_used"] = "いろか: 色替えするマテリアルが見つかりません",
            ["iroca.texture_not_used:description"] = "{0} とその子のマテリアルに、レシピの元テクスチャを使っているものがありません。何も変わりませんでした。",
            ["iroca.texture_not_used:hint"] = "コンポーネントを衣装やアバターの親に置いてください。マテリアルが書き出し済みの _recolored.png を指している場合は、元のテクスチャに戻すと非破壊で扱えます。",

            ["iroca.build_failed.no_source"] = "いろか: レシピに元テクスチャがありません",
            ["iroca.build_failed.no_source:description"] = "{0} のレシピに元テクスチャが設定されていないので、色替えできませんでした。",
            ["iroca.build_failed.no_source:hint"] = "レシピの「Source Texture」に色替え前のテクスチャを設定してください。",

            ["iroca.build_failed.unreadable_recipe"] = "いろか: レシピを読めません",
            ["iroca.build_failed.unreadable_recipe:description"] = "{0} のレシピの中身が空か壊れているか、この版のいろかより新しい形式です。",
            ["iroca.build_failed.unreadable_recipe:hint"] = "いろかを更新するか、いろかウィンドウで開き直して登録し直してください。",

            ["iroca.build_failed.no_zones"] = "いろか: 色替えするゾーンがありません",
            ["iroca.build_failed.no_zones:description"] = "{0} のレシピに有効なゾーンが無いので、何も変わりませんでした。",
            ["iroca.build_failed.no_zones:hint"] = "いろかウィンドウでゾーンを有効にしてください。",

            ["iroca.build_failed.source_unreadable"] = "いろか: 元テクスチャを読めません",
            ["iroca.build_failed.source_unreadable:description"] = "{0} のレシピの元テクスチャを読めなかったので、色替えできませんでした。",
            ["iroca.build_failed.source_unreadable:hint"] = "PNG/JPG 以外の形式なら、テクスチャの Import Settings で Read/Write を有効にしてください。",
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            ["iroca.missing_recipe"] = "Iroca: No recipe is set",
            ["iroca.missing_recipe:description"] = "The Recipe field of {0} is empty, so nothing was recolored.",
            ["iroca.missing_recipe:hint"] = "Register again with \"Register to avatar (non-destructive)\" in the Iroca window, or set a recipe.",

            ["iroca.texture_not_used"] = "Iroca: No material to recolor",
            ["iroca.texture_not_used:description"] = "No material under {0} uses the recipe's source texture. Nothing was changed.",
            ["iroca.texture_not_used:hint"] = "Place the component on the outfit or avatar root. If the material points to an exported _recolored.png, switch it back to the original texture.",

            ["iroca.build_failed.no_source"] = "Iroca: The recipe has no source texture",
            ["iroca.build_failed.no_source:description"] = "The recipe of {0} has no source texture, so it could not be recolored.",
            ["iroca.build_failed.no_source:hint"] = "Set the original texture in the recipe's Source Texture field.",

            ["iroca.build_failed.unreadable_recipe"] = "Iroca: Cannot read the recipe",
            ["iroca.build_failed.unreadable_recipe:description"] = "The recipe of {0} is empty, broken, or from a newer version of Iroca.",
            ["iroca.build_failed.unreadable_recipe:hint"] = "Update Iroca, or open it in the Iroca window and register again.",

            ["iroca.build_failed.no_zones"] = "Iroca: No zone to recolor",
            ["iroca.build_failed.no_zones:description"] = "The recipe of {0} has no enabled zone, so nothing was changed.",
            ["iroca.build_failed.no_zones:hint"] = "Enable a zone in the Iroca window.",

            ["iroca.build_failed.source_unreadable"] = "Iroca: Cannot read the source texture",
            ["iroca.build_failed.source_unreadable:description"] = "The source texture of {0}'s recipe could not be read, so it could not be recolored.",
            ["iroca.build_failed.source_unreadable:hint"] = "For formats other than PNG/JPG, enable Read/Write in the texture's import settings.",
        };

        private static readonly Localizer Localizer = new Localizer("en-US", () =>
            new List<(string, Func<string, string>)>
            {
                ("en-US", key => En.TryGetValue(key, out var s) ? s : null),
                ("ja-JP", key => Ja.TryGetValue(key, out var s) ? s : null),
            });

        public static void Report(NonDestructiveApplier.Problem problem, IrocaRecolor component,
            RecipeTextureBuilder.Failure failure)
        {
            var (severity, key) = Classify(problem, failure);
            ErrorReport.ReportError(Localizer, severity, key, component);
        }

        // 色替えしたかったのにできなかった(元テクスチャ・レシピ・原本の問題)はアップロードを止める。
        // 元の色のまま気づかず上がるのを防ぐため。何もしない設定(レシピ空・ゾーン無効・範囲に無い)は警告だけ。
        internal static (ErrorSeverity, string) Classify(NonDestructiveApplier.Problem problem,
            RecipeTextureBuilder.Failure failure)
        {
            switch (problem)
            {
                case NonDestructiveApplier.Problem.MissingRecipe:
                    return (ErrorSeverity.NonFatal, "iroca.missing_recipe");
                case NonDestructiveApplier.Problem.TextureNotUsedInScope:
                    return (ErrorSeverity.NonFatal, "iroca.texture_not_used");
            }
            switch (failure)
            {
                case RecipeTextureBuilder.Failure.NoEnabledZones:
                    return (ErrorSeverity.NonFatal, "iroca.build_failed.no_zones");
                case RecipeTextureBuilder.Failure.NoSourceTexture:
                    return (ErrorSeverity.Error, "iroca.build_failed.no_source");
                case RecipeTextureBuilder.Failure.UnreadableRecipe:
                    return (ErrorSeverity.Error, "iroca.build_failed.unreadable_recipe");
                default:
                    return (ErrorSeverity.Error, "iroca.build_failed.source_unreadable");
            }
        }
    }
}
