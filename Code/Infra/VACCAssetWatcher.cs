using UnityEditor;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// テクスチャ削除に追従して、対応する MaskCache のゴミファイルを掃除する。
    /// rename / move は GUID ベースで自動追従するため、ここでは扱わない。
    /// </summary>
    internal class VACCAssetWatcher : AssetModificationProcessor
    {
        // 削除確定前に GUID を解決する（OnPostprocessAllAssets まで待つと
        // GUIDToAssetPath が空を返す場合があり、特定が難しくなるため）。
        private static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!string.IsNullOrEmpty(guid))
                MaskFileStore.DeleteMaskByGuid(guid);
            // 実際の削除は Unity 側に任せる。
            return AssetDeleteResult.DidNotDelete;
        }
    }
}
