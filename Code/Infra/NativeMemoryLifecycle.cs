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
    /// <item>編集中も、計算が <see cref="IdleTrimMilliseconds"/>(10 秒)のあいだ無ければ、作業配列のプールの空きを返す
    ///   (<see cref="WorkPools.ReleaseFree"/>。色の表と選択キャッシュは残す。作り直しが重く、次の編集ですぐ使うため)。</item>
    /// <item>返したあとに、持ち主が返し忘れてファイナライザが解放した塊があれば警告する(不具合。用途つき)。
    ///   開発者向けの設定を表示しているときは、閉じた後に残っている量も用途ごとにログへ出す。</item>
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

        // リロードの前に呼ばれる。Unity はこの通知の購読者を順に呼ぶだけで、1 つが例外を投げると残りの購読者
        // (他ツールのリロード前の後始末)を呼ばないので、ここで止める。
        private static void Release()
        {
            try
            {
                DisposeIdleTimer();
                PixelProcessor.ReleaseWorkBuffers();
                WarnFinalized();
            }
            catch (System.Exception e) { UnityEngine.Debug.LogException(e); }
        }

        /// <summary>計算が無いまま、作業配列のプールの空きを返すまでの時間(ミリ秒。テストで短くする)。</summary>
        internal static int IdleTrimMilliseconds = 10_000;

        // 手を止めたときの縮小の予約。計算が始まる・終わるたびに番号を進め、予約した番号のままなら返す。
        // タイマーは 1 本を使い回す(プレビューのジョブは操作中に何度も終わる)。
        private static int s_idleGen, s_armedGen;
        private static Timer s_idleTimer;
        private static readonly object s_idleLock = new object();

        private static void ArmIdleTrim()
        {
            int gen = Interlocked.Increment(ref s_idleGen);
            Volatile.Write(ref s_armedGen, gen);
            lock (s_idleLock)
            {
                s_idleTimer ??= new Timer(_ => TrimIfIdle());
                s_idleTimer.Change(IdleTrimMilliseconds, Timeout.Infinite);
            }
        }

        private static void TrimIfIdle()
        {
            if (Volatile.Read(ref s_running) != 0 || Volatile.Read(ref s_idleGen) != Volatile.Read(ref s_armedGen)) return;
            WorkPools.ReleaseFree();
        }

        private static void DisposeIdleTimer()
        {
            lock (s_idleLock)
            {
                s_idleTimer?.Dispose();
                s_idleTimer = null;
            }
        }

        // これまでに警告した返し忘れの数(用途ごと)。
        private static readonly int[] s_finalizedWarned = new int[(int)NativeUsage.Other + 1];

        /// <summary>
        /// 持ち主が返し忘れてファイナライザが解放した塊が、前に見たときより増えていれば警告する(不具合を見つけるため)。
        /// </summary>
        internal static void WarnFinalized()
        {
            System.Text.StringBuilder sb = null;
            for (int u = 0; u < s_finalizedWarned.Length; u++)
            {
                int n = IrocaNativeMemory.FinalizedCount((NativeUsage)u);
                int seen = Interlocked.Exchange(ref s_finalizedWarned[u], n);
                if (n <= seen) continue;
                (sb ??= new System.Text.StringBuilder()).Append($" {(NativeUsage)u}={n - seen}");
            }
            if (sb != null)
                UnityEngine.Debug.LogWarning("[Iroca] Native memory blocks were not released by their owner and were freed by the finalizer:" + sb);
        }

        /// <summary>用途ごとの今の量(MB。ログ用)。</summary>
        internal static string DescribeLive()
        {
            var sb = new System.Text.StringBuilder();
            for (int u = 0; u <= (int)NativeUsage.Other; u++)
            {
                long b = IrocaNativeMemory.LiveBytesOf((NativeUsage)u);
                if (b > 0) sb.Append($" {(NativeUsage)u}={b / (1024.0 * 1024.0):0.#}MB");
            }
            return sb.Length == 0 ? " (none)" : sb.ToString();
        }

        // 走っているバックグラウンドの計算の数と、全部終わったら返す予約(0/1)。どのスレッドからも触る。
        private static int s_running;
        private static int s_releaseWhenIdle;

        /// <summary>走っている計算の数(テスト用)。</summary>
        internal static int RunningWork => Volatile.Read(ref s_running);

        /// <summary>
        /// 作業配列を借りうる計算を始める(どのスレッドからでも)。終えたら必ず <see cref="WorkEnded"/> を呼ぶ。
        /// <see cref="PreviewJob{T}"/> の計算と、非破壊の色替え(RecipeTextureBuilder)が呼ぶ。
        /// </summary>
        internal static void WorkStarted()
        {
            Interlocked.Increment(ref s_running);
            Interlocked.Increment(ref s_idleGen);   // 予約していた縮小を取り消す
        }

        /// <summary>
        /// 計算を終える。走っている計算が無くなり、<see cref="ReleaseWhenIdle"/> の予約があれば、作業配列と
        /// 色の表をスレッドプールで OS へ返す。予約が無ければ何もしない(編集中は使い回す)。
        /// </summary>
        internal static void WorkEnded()
        {
            if (Interlocked.Decrement(ref s_running) > 0) return;
            if (Interlocked.Exchange(ref s_releaseWhenIdle, 0) == 1) ReleaseAndReport();
            else ArmIdleTrim();
        }

        /// <summary>
        /// 作業配列と色の表を OS へ返す(スレッドプールで)。今の空きはすぐ返し、計算中のものがあれば、
        /// それが全部終わったときにもう一度返す。
        /// </summary>
        internal static void ReleaseWhenIdle()
        {
            // 開発者向けの設定はここ(メインスレッド)で読んで控える(WorkEnded は計算のスレッドから来る)。
            Volatile.Write(ref s_reportLive, EditorPrefs.GetBool(DeveloperSettingsPrefKey, false));
            Volatile.Write(ref s_releaseWhenIdle, 1);
            ReleaseAndReport();
            // 計算が無ければ予約は要らない(上で返した)。この間に終わった計算が予約を使っていても、返すのが 1 回増えるだけ。
            if (Volatile.Read(ref s_running) == 0) Interlocked.Exchange(ref s_releaseWhenIdle, 0);
        }

        // 作業配列と色の表をスレッドプールで返し、返し忘れを警告する。開発者向けの設定を表示しているときは、
        // 返した後に残っている量(開いているほかのウィンドウ・シーンのプレビューが持つ分を含む)も出す。
        // どのスレッドからでも呼ぶ(EditorPrefs は読まない)。
        private static void ReleaseAndReport()
        {
            bool verbose = Volatile.Read(ref s_reportLive);
            PixelProcessor.ReleaseWorkBuffersInBackground().ContinueWith(_ =>
            {
                WarnFinalized();
                if (verbose) UnityEngine.Debug.Log("[Iroca] Native memory after release:" + DescribeLive());
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        // 返した後に残っている量をログへ出すか(最後に ReleaseWhenIdle を呼んだときの開発者向けの設定)。
        private static bool s_reportLive;

        // IrocaWindow の「開発者向けの設定を表示」と同じキー(Infra から UI を参照しないので、文字列を写して読む)。
        private const string DeveloperSettingsPrefKey = "Iroca.ShowDeveloperSettings";
    }
}
