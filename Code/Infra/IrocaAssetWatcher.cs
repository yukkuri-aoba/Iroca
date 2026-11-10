// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;

namespace Iroca {
    /// <summary>
    /// テクスチャ削除に追従して、対応する MaskCache / SessionCache のファイルを退避する(<c>.orphan</c>。30 日残す)。
    /// 消してしまうと、Project での誤削除を Git やごみ箱で戻しても編集内容が戻らない。同じ GUID で戻れば読み込みのときに復元する。
    /// フォルダごと消したときは、中のアセットの分も退避する(Unity はフォルダの分しか知らせない)。
    /// rename / move は GUID ベースで自動追従するため、ここでは扱わない。
    /// </summary>
    internal class IrocaAssetWatcher : AssetModificationProcessor {
        // 削除確定前に GUID を解決する（OnPostprocessAllAssets まで待つと
        // GUIDToAssetPath が空を返す場合があり、特定が難しくなるため）。
        private static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options) {
            // フォルダなら中のアセット(下のフォルダも含む)の分も。窓を開くたびの掃除(CleanupOrphans)も GUID の引けない
            // ファイルを退避するが、窓を開かない間は残り続けていた。
            // フォルダのときは GUID をまとめて渡し、キャッシュのフォルダの列挙を 1 回で済ませる(中のアセットごとに
            // 3 つのキャッシュのフォルダを列挙していた)。
            if (AssetDatabase.IsValidFolder(assetPath)) {
                var guids = new List<string> { AssetDatabase.AssetPathToGUID(assetPath) };
                guids.AddRange(AssetDatabase.FindAssets("", new[] { assetPath }));
                MaskFileStore.RetireMasksByGuids(guids);
                SessionFileStore.RetireSessionsByGuids(guids);
            } else {
                Retire(AssetDatabase.AssetPathToGUID(assetPath));
            }
            // アセット本体の削除は Unity に続行させる。
            return AssetDeleteResult.DidNotDelete;
        }

        private static void Retire(string guid) {
            if (string.IsNullOrEmpty(guid)) {
                return;
            }
            MaskFileStore.RetireMaskByGuid(guid);
            SessionFileStore.RetireSessionByGuid(guid);
        }
    }
}
