// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca {
    /// <summary>
    /// アセットの取り込みを、開いているいろかの窓へ知らせる。取り込みの結果は変えない。
    /// .asset(git の pull・checkout・バックアップからの復元の後など)は <see cref="IrocaWindow"/> の「外でのレシピの書き換え」、
    /// 開いているテクスチャは原本の読み直し(外のツールでの上書き・取り込み設定の変更)。
    /// 窓(<see cref="IrocaWindow"/>)を呼ぶので UI の層に置く。
    /// </summary>
    internal sealed class RecipeImportWatcher : AssetPostprocessor {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths) {
            // テクスチャが消えていたら、シーンのプレビューが持つ画素も手放す。
            if (deletedAssets.Length > 0) {
                LivePreview.PurgeDestroyedSources();
            }
            // 知らせる先(開いている窓)が無ければ、取り込まれたものを調べない。この入口はいろかを開いていない人の取り込みでも
            // 毎回呼ばれ、SDK の更新や Library の作り直しでは .asset が数千件まとめて来る。
            if (!EditingSessionRegistry.Any) {
                return;
            }
            bool anyAsset = ContainsRecipe(importedAssets);
            bool anyDeletedAsset = ContainsAssetFile(deletedAssets);
            foreach (var e in EditingSessionRegistry.Snapshot()) {
                if (!(e is IrocaWindow w)) {
                    continue;
                }
                if (anyAsset) {
                    w.OnRecipeAssetsImported();
                }
                if (anyDeletedAsset) {
                    w.OnAssetFilesDeleted();
                }
                w.OnSourceAssetImported(importedAssets);
            }
        }

        // 消えたものに .asset があるか(消えた後は型を引けないので、拡張子だけで見る)。
        private static bool ContainsAssetFile(string[] paths) {
            foreach (var path in paths) {
                if (path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            return false;
        }

        // レシピの .asset が取り込まれたときだけ、窓のレシピを確かめて数え直す(数え直しはプロジェクトのレシピ探し)。
        // .asset はメッシュ・アニメーションの設定などほかにも多く、ビルドや git の pull のたびに探していた。
        internal static bool ContainsRecipe(string[] importedAssets) {
            foreach (var path in importedAssets) {
                if (path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)
                    && AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(IrocaRecipe)) {
                    return true;
                }
            }
            return false;
        }
    }
}
