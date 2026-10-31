// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// テクスチャ削除に追従して、対応する MaskCache / SessionCache のファイルを退避する(<c>.orphan</c>。30 日残す)。
    /// 消してしまうと、Project での誤削除を Git やごみ箱で戻しても編集内容が戻らない。同じ GUID で戻れば読み込みのときに復元する。
    /// rename / move は GUID ベースで自動追従するため、ここでは扱わない。
    /// </summary>
    internal class IrocaAssetWatcher : AssetModificationProcessor
    {
        // 削除確定前に GUID を解決する（OnPostprocessAllAssets まで待つと
        // GUIDToAssetPath が空を返す場合があり、特定が難しくなるため）。
        private static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!string.IsNullOrEmpty(guid))
            {
                MaskFileStore.RetireMaskByGuid(guid);
                SessionFileStore.RetireSessionByGuid(guid);
            }
            // アセット本体の削除は Unity に続行させる。
            return AssetDeleteResult.DidNotDelete;
        }
    }
}
