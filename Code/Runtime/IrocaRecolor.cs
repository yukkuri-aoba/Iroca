// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// アバターや衣装に置く「いろか 色替え」コンポーネント(非破壊)。
    /// 置いた GameObject とその子の Renderer が使うマテリアルのうち、レシピの元テクスチャを
    /// 参照しているものだけを、ビルド時(NDMF: 再生・アップロード)に色替え済みテクスチャへ差し替える。
    /// 元のテクスチャ・マテリアルは書き換えないので、このコンポーネントを外せば元に戻る。
    /// 処理は NDMF 連携(Iroca.NdmfIntegration)が行い、ビルド後のアバターからは取り除かれる。
    /// <para>
    /// スクリプトの GUID は .meta を追跡して固定している(シーン・prefab が GUID で参照するため。
    /// 変えると既存のアバターで Missing Script になる)。
    /// </para>
    /// </summary>
    [AddComponentMenu("Iroca/Iroca Recolor")]
    [HelpURL("https://github.com/yukkuri-aoba/Iroca/blob/main/MANUAL.md")]
    public sealed class IrocaRecolor : MonoBehaviour
#if IROCA_NDMF_PRESENT
        // VRChat SDK があれば IEditorOnly になり、SDK の「使えないコンポーネント」扱いを受けない。
        , nadena.dev.ndmf.INDMFEditorOnly
#endif
    {
        [Tooltip("色替えの内容(元テクスチャ・色・マスク)。いろかウィンドウの「アバターに非破壊で登録」で作られる。")]
        public IrocaRecipe recipe;
    }
}
