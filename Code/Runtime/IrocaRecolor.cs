// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// アバターや衣装に置く「いろか 色替え」コンポーネント(非破壊)。
    /// 置いた GameObject とその子の Renderer が使うマテリアルのうち、各レシピの元テクスチャを
    /// 参照しているものだけを、ビルド時(NDMF: 再生・アップロード)に色替え済みテクスチャへ差し替える。
    /// 元のテクスチャ・マテリアルは書き換えないので、このコンポーネントを外せば元に戻る。
    /// 処理は NDMF 連携(Iroca.NdmfIntegration)が行い、ビルド後のアバターからは取り除かれる。
    /// <para>
    /// 1 つのオブジェクトで複数のテクスチャを色替えするときは、レシピを 1 つのコンポーネントへまとめる
    /// (登録のたびにコンポーネントが増えないように)。並び順は同じテクスチャのレシピが重なったときの
    /// 優先順(先が勝つ)。
    /// </para>
    /// <para>
    /// スクリプトの GUID は .meta を追跡して固定している(シーン・prefab が GUID で参照するため。
    /// 変えると既存のアバターで Missing Script になる)。
    /// </para>
    /// </summary>
    [AddComponentMenu("Iroca/Iroca Recolor")]
    [HelpURL("https://github.com/yukkuri-aoba/Iroca/blob/main/MANUAL.md")]
    public sealed class IrocaRecolor : MonoBehaviour, ISerializationCallbackReceiver
#if IROCA_NDMF_PRESENT
        // VRChat SDK があれば IEditorOnly になり、SDK の「使えないコンポーネント」扱いを受けない。
        , nadena.dev.ndmf.INDMFEditorOnly
#endif
    {
        [Tooltip("色替えの内容(元テクスチャ・色・マスク)。テクスチャごとに 1 つ。いろかウィンドウの「アバターに非破壊で登録」で足される。")]
        public List<IrocaRecipe> recipes = new List<IrocaRecipe>();

        // 旧版(1 コンポーネント = 1 レシピ)で保存された値。読み込んだら recipes の先頭へ移して空にする
        // (次に保存したとき新しい形で書かれる)。
        [SerializeField, HideInInspector] private IrocaRecipe recipe;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            // 読み込みスレッドから呼ばれることがあるので、Unity の API(== を含む)を使わず参照で比べる。
            if (ReferenceEquals(recipe, null)) return;
            if (recipes == null) recipes = new List<IrocaRecipe>();
            bool listed = false;
            foreach (var r in recipes)
                if (ReferenceEquals(r, recipe)) { listed = true; break; }
            if (!listed) recipes.Insert(0, recipe);
            recipe = null;
        }
    }
}
