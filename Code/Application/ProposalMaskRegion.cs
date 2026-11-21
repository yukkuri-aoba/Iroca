// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// AI の提案の範囲を、マスクの寸法の範囲にする(右クリックの提案と自動化の共通の部品)。規則(被覆を保って写す・AA 包含と
    /// 細い描き込みの地はがしのやり直し)は Ops の <see cref="ProposalMaskTransfer"/> にあり、ここはテクスチャから画素を読む所・
    /// 計算の区間の数え・時間の記録を持つ。
    /// </summary>
    internal static class ProposalMaskRegion {
        /// <summary>段ごとの時間(計測の記録用)。</summary>
        internal struct Timing {
            public double transferMs, getPixelsMs, aaMs;
        }

        /// <summary>
        /// 提案(<paramref name="src"/>)をマスクの寸法 mw×mh の範囲にする。同じ寸法なら提案をそのまま返す(owned = false)。
        /// 違えば自分で作って返す(owned = true。呼び出し側が Release する)。AA 包含をやり直すのは、テクスチャがマスクの寸法の
        /// ときだけ(画素は <see cref="WorkingResolution.NativePixelsOf"/> で、要るときにだけ読む)。規則は
        /// <see cref="ProposalMaskTransfer.ToMaskSize"/>。
        /// </summary>
        public static NativeBools ToMaskSize(NativeBools src, int mw, int mh, Texture2D texture, out bool owned, out Timing timing) {
            timing = default;
            owned = false;
            if (mw == src.Width && mh == src.Height) {
                return src;
            }

            // 作業配列を借りる計算の区間として数える(手を止めて 10 秒で空きを返す予約は、区間が終わったときに入る)。AI の後処理の
            // ほかの段は背景のジョブ(PreviewJob)が数えていて、メインスレッドで走るこの段だけが外にあった。
            NativeMemoryLifecycle.WorkStarted();
            try {
                long t = MaskSuggestPerf.Now;
                var tm = default(Timing);
                var region = ProposalMaskTransfer.ToMaskSize(src, mw, mh,
                    texture != null ? texture.width : 0, texture != null ? texture.height : 0,
                    texture != null ? () => WorkingResolution.NativePixelsOf(texture, null) : null,
                    out owned,
                    stage => {
                        switch (stage) {
                            case ProposalMaskTransfer.Stage.Transferred:
                                tm.transferMs = MaskSuggestPerf.MsSince(t);
                                break;
                            case ProposalMaskTransfer.Stage.PixelsRead:
                                tm.getPixelsMs = MaskSuggestPerf.MsSince(t);
                                break;
                            case ProposalMaskTransfer.Stage.AaDone:
                                tm.aaMs = MaskSuggestPerf.MsSince(t);
                                break;
                        }
                        t = MaskSuggestPerf.Now;
                    });
                timing = tm;
                return region;
            } finally {
                NativeMemoryLifecycle.WorkEnded();
            }
        }
    }
}
