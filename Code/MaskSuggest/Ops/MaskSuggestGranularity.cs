// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    /// <summary>
    /// AI マスク提案の粒度。SAM はクリック 1 点に対し粒度違いの候補
    /// (サブパーツ/パーツ/全体)を同時出力するため、どれを採用するかをユーザーが選べる。
    /// (SamMaskPostprocess のチャンネル選択規則 = headless 検証対象のため Ops 配下に置く)
    /// </summary>
    internal enum MaskSuggestGranularity
    {
        /// <summary>モデルの予測スコア最大の候補(既定)。</summary>
        Auto,
        /// <summary>面積最小の候補(模様・小パーツ向け)。</summary>
        Fine,
        /// <summary>面積最大の候補(パーツ全体向け)。</summary>
        Coarse,
    }
}
