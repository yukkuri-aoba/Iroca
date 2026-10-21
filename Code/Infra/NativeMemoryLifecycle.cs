// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// いろかがネイティブメモリに持つ作業配列を OS へ返す時期の管理。
    /// <list type="bullet">
    /// <item>スクリプトのリロードの前に返す。static の参照はリロードで消えるので、返さないと塊のファイナライザが
    ///   走るまで残る。計算中の分は計算が返した後にプールへ戻り、リロードのアンロードでファイナライザが解放する。</item>
    /// <item>ウィンドウを閉じたときなどは <see cref="ReleaseWhenIdle"/> で返す。その時点で計算中の処理(取り消した
    ///   プレビュー・書き出し・AI 提案など)の作業配列は、解放の後でプールへ戻ってくるので、今の空きを返すだけでは
    ///   残り続ける(8K の書き出し中に閉じると約 3GB)。そこで、走っている計算(<see cref="WorkStarted"/> 〜
    ///   <see cref="WorkEnded"/>)が全部終わった時点でもう一度返す。</item>
    /// </list>
    /// </summary>
    internal static class NativeMemoryLifecycle
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Release;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
        }

        private static void Release() => PixelProcessor.ReleaseWorkBuffers();

        // 走っているバックグラウンドの計算の数と、全部終わったら返す予約(0/1)。どのスレッドからも触る。
        private static int s_running;
        private static int s_releaseWhenIdle;

        /// <summary>走っている計算の数(テスト用)。</summary>
        internal static int RunningWork => Volatile.Read(ref s_running);

        /// <summary>
        /// 作業配列を借りうる計算を始める(どのスレッドからでも)。終えたら必ず <see cref="WorkEnded"/> を呼ぶ。
        /// <see cref="PreviewJob{T}"/> の計算と、非破壊の色替え(RecipeTextureBuilder)が呼ぶ。
        /// </summary>
        internal static void WorkStarted() => Interlocked.Increment(ref s_running);

        /// <summary>
        /// 計算を終える。走っている計算が無くなり、<see cref="ReleaseWhenIdle"/> の予約があれば、作業配列と
        /// 色の表をスレッドプールで OS へ返す。予約が無ければ何もしない(編集中は使い回す)。
        /// </summary>
        internal static void WorkEnded()
        {
            if (Interlocked.Decrement(ref s_running) > 0) return;
            if (Interlocked.Exchange(ref s_releaseWhenIdle, 0) == 1) PixelProcessor.ReleaseWorkBuffersInBackground();
        }

        /// <summary>
        /// 作業配列と色の表を OS へ返す(スレッドプールで)。今の空きはすぐ返し、計算中のものがあれば、
        /// それが全部終わったときにもう一度返す。
        /// </summary>
        internal static void ReleaseWhenIdle()
        {
            Volatile.Write(ref s_releaseWhenIdle, 1);
            PixelProcessor.ReleaseWorkBuffersInBackground();
            // 計算が無ければ予約は要らない(上で返した)。この間に終わった計算が予約を使っていても、返すのが 1 回増えるだけ。
            if (Volatile.Read(ref s_running) == 0) Interlocked.Exchange(ref s_releaseWhenIdle, 0);
        }
    }
}
