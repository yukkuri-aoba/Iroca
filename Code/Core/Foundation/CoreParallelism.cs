// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca
{
    /// <summary>
    /// Core の並列度の規則(0.4.0 の計画の 3-4 で PixelProcessor から切り出した)。PixelProcessor の
    /// <c>ComputeDefaultParallelism</c>・<c>DefaultParallelism</c>・<c>GetMaxParallelism</c> と
    /// <c>DebugCaptureHooks.ParallelismOverride</c> は、ここへの転送役として残している。新しく書くコードはここを使う。
    /// </summary>
    internal static class CoreParallelism
    {
        // Parallel.For で使用する既定の CPU コア数制限。
        // 計算はバックグラウンドで走り、その間もメインスレッドが UI を描くので、全コア並列にせず
        // コアをあけておく。あける数は割合でなく固定: 要るのは Unity のメインスレッドと描画などの
        // 分で、コア数に比例しない。8 論理コア以上は 2、それ未満は 1(4 コア 4 スレッドで 2 本しか
        // 使わず半分遊ばせていた)。最低 1 はあける(全コアにすると操作中の UI が引っかかる)。
        private static readonly int s_default = ComputeDefault(Environment.ProcessorCount);

        internal static int ComputeDefault(int processorCount)
        {
            int reserve = processorCount >= 8 ? 2 : 1;
            return Math.Max(1, processorCount - reserve);
        }

        // デバッグ画面の「自動」の表示が実際の既定値と食い違わないよう、計算式はここだけに置く。
        internal static int Default => s_default;

        /// <summary>
        /// Parallel.For の MaxDegreeOfParallelism を手動設定する。0 以下のとき自動(<see cref="Default"/>)。
        /// Debug モジュールの PerfView が EditorPrefs に永続化して管理する。ハーネスの --threads も設定する。
        /// </summary>
        internal static int Override;

        // ZoneAutoTuner・AI 提案の後処理など Core の他クラスも同じ規則で並列度を決める。
        // 各所で Environment.ProcessorCount から直に計算すると、デバッグ時のスレッド数指定が
        // 一部の処理にだけ効かず、計測が食い違う。
        internal static int Max()
        {
            int ov = Override;
            return ov > 0 ? Math.Min(ov, Environment.ProcessorCount) : s_default;
        }
    }
}
