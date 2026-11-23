// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca {
    /// <summary>
    /// AI 提案を何に使うか。後処理の段が用途で変わる(IMaskSuggestService.RequestProposal・<see cref="SamProposalSteps"/>)。
    /// (後処理の段の分かれ目なので、ハーネスが取り込む Ops 配下に置く)
    /// </summary>
    internal enum MaskSuggestUse {
        /// <summary>
        /// 部分をマスクに当てる(右クリックの「ここも塗る」など・自動化のマスク操作)。粒度が自動・大きいなら、クリックした塊と
        /// 色の違う離れた塊を外す(SamMaskRefine.DropForeignComponents)。
        /// </summary>
        Region,
        /// <summary>自動調整の証拠。精密化 4 段までで、塊は外さない。</summary>
        Evidence,
    }
}
