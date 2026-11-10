// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;

namespace Iroca.NdmfIntegration {
    /// <summary>
    /// ビルド時の問題を NDMF のエラー画面へ出す(日本語・英語)。表示言語は NDMF 側の言語設定に従う。
    /// キーは NDMF の規約どおり、タイトル = key、説明 = key:description、対処 = key:hint。{0} はコンポーネント。
    /// </summary>
    internal static class NdmfMessages {
        private static readonly Dictionary<string, string> Ja = new Dictionary<string, string> {
            ["iroca.texture_not_used"] = "いろか: 色替えするマテリアルが見つかりません",
            ["iroca.texture_not_used:description"] = "{0} とその子のマテリアルに、レシピ {1} の元テクスチャを使っているものがありません。このレシピでは何も変わりませんでした。",
            ["iroca.texture_not_used:hint"] = "コンポーネントを衣装やアバターの親に置いてください。マテリアルが書き出し済みの _recolored.png を指している場合は、元のテクスチャに戻すと非破壊で扱えます。",

            ["iroca.build_failed.lost_recipe"] = "いろか: レシピが見つかりません",
            ["iroca.build_failed.lost_recipe:description"] = "{0} が指しているレシピがもうありません。そのため色替えできませんでした。",
            ["iroca.build_failed.lost_recipe:hint"] = "レシピを元に戻す（Git など）か、いろかのウィンドウでテクスチャを色替えし直し、同じオブジェクトへ「アバターに非破壊で登録」してください（確認画面のとおり、消えた欄は外れます）。この色替えなしでアップロードするときは、コンポーネントの消えた欄を外してください。",

            ["iroca.build_failed.no_source"] = "いろか: レシピに元テクスチャがありません",
            ["iroca.build_failed.no_source:description"] = "{0} のレシピ {1} に元テクスチャが設定されていないので、色替えできませんでした。",
            ["iroca.build_failed.no_source:hint"] = "レシピの「元テクスチャ」に色替え前のテクスチャを設定してください。",

            ["iroca.build_failed.unreadable_recipe"] = "いろか: レシピを読めません",
            ["iroca.build_failed.unreadable_recipe:description"] = "{0} のレシピ {1} の中身が空か壊れているか、この版のいろかより新しい形式です。",
            ["iroca.build_failed.unreadable_recipe:hint"] = "いろかを更新するか、いろかウィンドウで開き直して登録し直してください。",

            ["iroca.build_failed.source_unreadable"] = "いろか: 元テクスチャを読めません",
            ["iroca.build_failed.source_unreadable:description"] = "{0} のレシピ {1} の元テクスチャを読めなかったので、色替えできませんでした。",
            ["iroca.build_failed.source_unreadable:hint"] = "ほかのアプリでファイルを開いているなら閉じ、同期サービスのオンライン専用のファイルならこの PC に保存してから、もう一度試してください。PNG/JPG 以外の形式なら、テクスチャのインポート設定で Read/Write を有効にしてください。",

            ["iroca.build_failed.source_undecodable"] = "いろか: 元テクスチャの画像ファイルを読めません",
            ["iroca.build_failed.source_undecodable:description"] = "{0} のレシピ {1} の元テクスチャのファイルが壊れているか、いろかの扱えない形式なので、色替えできませんでした。",
            ["iroca.build_failed.source_undecodable:hint"] = "画像を編集ソフトで開いて PNG で保存し直してください。Unity がそのテクスチャを取り込めているなら、インポート設定で Read/Write を有効にすると、取り込み済みの画素で色替えします。",
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string> {
            ["iroca.texture_not_used"] = "Iroca: No material to recolor",
            ["iroca.texture_not_used:description"] = "No material under {0} uses the source texture of recipe {1}. Nothing was changed by this recipe.",
            ["iroca.texture_not_used:hint"] = "Place the component on the outfit or avatar root. If the material points to an exported _recolored.png, switch it back to the original texture.",

            ["iroca.build_failed.lost_recipe"] = "Iroca: The recipe is missing",
            ["iroca.build_failed.lost_recipe:description"] = "{0} points to a recipe that no longer exists, so it could not be recolored.",
            ["iroca.build_failed.lost_recipe:hint"] = "Restore the recipe (for example with Git), or recolor the texture again in the Iroca window and use \"Register to avatar (non-destructive)\" on the same object (as the confirmation says, the missing entries are removed). To upload without this recolor, remove the missing entry from the component.",

            ["iroca.build_failed.no_source"] = "Iroca: The recipe has no source texture",
            ["iroca.build_failed.no_source:description"] = "Recipe {1} of {0} has no source texture, so it could not be recolored.",
            ["iroca.build_failed.no_source:hint"] = "Set the original texture in the recipe's Source Texture field.",

            ["iroca.build_failed.unreadable_recipe"] = "Iroca: Cannot read the recipe",
            ["iroca.build_failed.unreadable_recipe:description"] = "Recipe {1} of {0} is empty, broken, or from a newer version of Iroca.",
            ["iroca.build_failed.unreadable_recipe:hint"] = "Update Iroca, or open it in the Iroca window and register again.",

            ["iroca.build_failed.source_unreadable"] = "Iroca: Cannot read the source texture",
            ["iroca.build_failed.source_unreadable:description"] = "The source texture of recipe {1} of {0} could not be read, so it could not be recolored.",
            ["iroca.build_failed.source_unreadable:hint"] = "If another app has the file open, close it; if it is an online-only file of a sync service, keep it on this PC; then try again. For formats other than PNG/JPG, enable Read/Write in the texture's import settings.",

            ["iroca.build_failed.source_undecodable"] = "Iroca: Cannot read the source image file",
            ["iroca.build_failed.source_undecodable:description"] = "The source texture file of recipe {1} of {0} is broken or in a format Iroca cannot read, so it could not be recolored.",
            ["iroca.build_failed.source_undecodable:hint"] = "Open the image in an image editor and save it again as PNG. If Unity has imported the texture, turning on Read/Write in its import settings lets Iroca recolor the imported pixels.",
        };

        private static readonly Localizer Localizer = new Localizer("en-US", () =>
            new List<(string, Func<string, string>)> {
                ("en-US", key => En.TryGetValue(key, out var s) ? s : null),
                ("ja-JP", key => Ja.TryGetValue(key, out var s) ? s : null),
            });

        /// <summary>{0} = コンポーネント、{1} = どのレシピか(1 つのコンポーネントが複数のレシピを持つので)。</summary>
        public static void Report(NonDestructiveApplier.Problem problem, IrocaRecolor component, IrocaRecipe recipe,
            RecipeTextureBuilder.Failure failure) {
            var (severity, key) = Classify(problem, failure);
            if (key == null) {
                return;
            }
            if (recipe != null) {
                ErrorReport.ReportError(Localizer, severity, key, component, recipe);
            } else {
                ErrorReport.ReportError(Localizer, severity, key, component);
            }
        }

        // 色替えしたかったのにできなかった(元テクスチャ・レシピ・原本の問題)はアップロードを止める。
        // 元の色のまま気づかず上がるのを防ぐため。範囲に元テクスチャが無いのは設定ミスかもしれないので警告だけ。
        // レシピが空(空の欄)・ゾーンがすべて無効は、利用者が色替えを外している状態なので報告しない(key = null。
        // インスペクタには出ている)。NDMF は重大度に関係なく 1 件でもあれば NDMF Console を開いて前に出すので、
        // 報告すると、意図して外しているのに再生のたびに窓が出る。
        internal static (ErrorSeverity, string) Classify(NonDestructiveApplier.Problem problem,
            RecipeTextureBuilder.Failure failure) {
            switch (problem) {
                case NonDestructiveApplier.Problem.MissingRecipe:
                    return (default, null);
                case NonDestructiveApplier.Problem.LostRecipe:
                    return (ErrorSeverity.Error, "iroca.build_failed.lost_recipe");
                case NonDestructiveApplier.Problem.TextureNotUsedInScope:
                    return (ErrorSeverity.NonFatal, "iroca.texture_not_used");
            }
            switch (failure) {
                case RecipeTextureBuilder.Failure.NoEnabledZones:
                    return (default, null);
                case RecipeTextureBuilder.Failure.NoSourceTexture:
                    return (ErrorSeverity.Error, "iroca.build_failed.no_source");
                case RecipeTextureBuilder.Failure.UnreadableRecipe:
                    return (ErrorSeverity.Error, "iroca.build_failed.unreadable_recipe");
                case RecipeTextureBuilder.Failure.SourceUndecodable:
                    return (ErrorSeverity.Error, "iroca.build_failed.source_undecodable");
                default:
                    return (ErrorSeverity.Error, "iroca.build_failed.source_unreadable");
            }
        }
    }
}
