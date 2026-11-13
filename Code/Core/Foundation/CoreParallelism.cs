// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iroca {
    /// <summary>
    /// Core の並列度の規則(0.4.0 の計画の 3-4 で PixelProcessor から切り出した)。並列度を決める所は、どこもここを呼ぶ。
    /// <c>DebugCaptureHooks.ParallelismOverride</c> だけは、デバッグの画面が使う名前として、ここへの転送役で残している。
    /// </summary>
    internal static class CoreParallelism {
        // Parallel.For で使用する既定の CPU コア数制限。
        // 計算はバックグラウンドで走り、その間もメインスレッドが UI を描くので、全コア並列にせず
        // コアをあけておく。あける数は割合でなく固定: 要るのは Unity のメインスレッドと描画などの
        // 分で、コア数に比例しない。8 論理コア以上は 2、それ未満は 1(4 コア 4 スレッドで 2 本しか
        // 使わず半分遊ばせていた)。最低 1 はあける(全コアにすると操作中の UI が引っかかる)。
        private static readonly int s_default = ComputeDefault(Environment.ProcessorCount);

        internal static int ComputeDefault(int processorCount) {
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
        internal static int Max() {
            int ov = Override;
            return ov > 0 ? Math.Min(ov, Environment.ProcessorCount) : s_default;
        }

        /// <summary>
        /// 並列のループ(Parallel.For など)へ渡す設定を作る。<b>作り口はここだけ</b>(ほかの所に <c>new ParallelOptions</c> を
        /// 書かない。<c>scripts/source_checks/test_parallel_options_factory.py</c> が見張る)。並列度の決め方を変えるときに、
        /// 直す所が 1 つで済むようにするため。
        /// </summary>
        /// <param name="ct">取り消し。渡すと、ループが区切りごとに取り消しを見る。</param>
        /// <param name="degree">並列度。0 以下なら <see cref="Max"/>。分け方で本数が決まる所(帯の数・区間の数を、
        /// <see cref="Max"/> 以下で自分で決めた所)だけが渡す。</param>
        /// <remarks>
        /// 重い計算(プレビュー・自動調整・AI の後処理・自動化)が重なると、それぞれが <see cref="Max"/> 本を求める。プロセス全体で
        /// 本数を頭打ちにする共有のスケジューラは、測って入れなかった(2026-11-13。Unity 2022.3 の Mono・16 論理コア・4096² の
        /// 色替えを同時に走らせた): 2 本が重なっても合計の時間は伸びず(1 本 101 ms・2 本 180 ms・3 本 264 ms。本数を分け合うと
        /// 193・309 ms で、かえって遅い)、メインスレッドが止まる長さは、重なっている間に最大 17〜27 ms(3 本で 31〜61 ms)が
        /// 数回。計算を待っている間の 1〜2 フレームで、頭打ちにすると、メインスレッドが回すループ(マスクの確定など)が計算の
        /// 列の後ろに並ぶ別の遅れを持ち込む。入れるなら、ここでスケジューラを設定に載せる。
        /// </remarks>
        internal static ParallelOptions Options(CancellationToken ct = default, int degree = 0) =>
            new ParallelOptions { MaxDegreeOfParallelism = degree > 0 ? degree : Max(), CancellationToken = ct };
    }
}
