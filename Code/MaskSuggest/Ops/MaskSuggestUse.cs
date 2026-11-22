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
        /// <summary>
        /// 部分を「含める」へ足す(右クリック・自動化の「ここも塗る」を、もうそのゾーンで塗れている所で押したとき。
        /// 塗れていない所で押したときは <see cref="Region"/>。SamProposalSteps.RegionUse)。後処理は <see cref="Region"/> と同じで、粒度が自動なら
        /// 最後に、右クリックした所の色に近い画素だけに絞る(明るさの差は広く・色味の差は狭く見る。SamMaskRefine.KeepNearClickColor。2026-11-22 ユーザー判断:
        /// 黒い文字の上で押しても、色の違う下地は塗らない)。
        /// </summary>
        RegionPaintHere,
    }
}
