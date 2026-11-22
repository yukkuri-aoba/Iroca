// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca {
    /// <summary>
    /// 1 クリック分の提案マスク。範囲は Mono のヒープの外に置く(元の解像度。4K で 16MB。0.4.0 の計画の B4)。
    /// 受け取った側(AiSuggestBroker の受け取り先)が使えるのは受け取りの間だけで、AiSuggestBroker が
    /// 受け取り先を呼んだ後に <see cref="Release"/> する。持ち続けるなら写すか、範囲の参照を足す。
    /// サービスから直接受け取った側は、自分で Release する。
    /// (提案を作る後処理の段 <see cref="SamProposalSteps"/> と同じく、ハーネスが取り込む Ops 配下に置く)
    /// </summary>
    internal sealed class MaskSuggestProposal {
        /// <summary>下原点(GetPixels32 順) width*height の提案領域(この提案が参照を 1 つ持つ)。</summary>
        public NativeBools mask;
        public int width;
        public int height;
        /// <summary>モデルの予測 IoU スコア(参考表示用)。</summary>
        public float score;
        /// <summary>キャンバス面積比。</summary>
        public float areaFrac;
        /// <summary>背景に流れた可能性(面積が棄却しきい以上のチャンネルしか無かった)。</summary>
        public bool floodWarning;
        /// <summary>
        /// 「ここも塗る」(MaskSuggestUse.RegionPaintHere・粒度 自動)で、右クリックした所の色から遠いとして範囲から外した画素数
        /// (色で絞らない用途は 0)。窓は、色の近い所が全部もう塗れていたときの知らせに使う。
        /// </summary>
        public int colorDropped;

        /// <summary>範囲の参照を返す(2 回目からは何もしない)。</summary>
        public void Release() {
            var m = mask;
            mask = null;
            m?.Release();
        }
    }
}
