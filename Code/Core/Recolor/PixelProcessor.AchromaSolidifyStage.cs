// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 再着色の段の 1 つ目「無彩の固め」(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から中身を書き換えずに
        /// 切り出した)。無彩サンプル/極端無彩ターゲットの重みを求め(<see cref="ZoneState.AchromaWeight"/>)、
        /// 極端な無彩ターゲットでは選択の内部を full strength に固める。
        /// </summary>
        private static class AchromaSolidifyStage
        {
            internal static void Run(RunContext run, ZoneState z)
            {
                // 無彩サンプル/極端無彩ターゲットの重み(無彩パスと AA フィデリティ修正で共用)。
                z.AchromaWeight = ComputeAchromaWeight(z.Zone.sampleColor, z.Zone.targetColor);

                // 無彩パスの内部固め: 極端な無彩ターゲット(白↔黒)では、マッチ強度が色のばらつきで内部まで
                // フルにならず、明るい画素ほど弱く塗られて元色が残り「中央の段差」になる。陰影は塗り
                // 強度でなく recolor の achroma レンジリマップ(gain≤1)で表現すべきなので、マッチ領域の
                // 内部を full strength に固め、AA 縁(侵食で除いた帯)の taper だけ残す。有彩ターゲット
                // (achromaWeight≈0)では no-op = byte 不変。
                if (z.AchromaWeight > AchromaWeightActiveMin)
                    SolidifyAchromaInterior(z.Strength, run.W, run.H, z.AchromaWeight, run.Ct);

                run.Sub.Mark(SpSolidify);
            }
        }
    }
}
