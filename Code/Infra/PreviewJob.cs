// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Iroca
{
    /// <summary>
    /// PreviewJob<T> から投入されたメインスレッド復帰アクションを EditorApplication.update で捌くポンプ。
    /// バックグラウンドスレッドが直接 Texture2D / EditorWindow に触らず、ここに Action をキュー
    /// した上で次の Editor tick でドレインする。
    /// </summary>
    internal static class PreviewJobMainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        // 1 回の tick で捌く時間の目安。超えたら残りは次の tick へ回す。
        private const long TickBudgetMs = 20;
        private static readonly long TickBudgetTicks = TickBudgetMs * Stopwatch.Frequency / 1000;

        [InitializeOnLoadMethod]
        private static void Install()
        {
            // ドメインリロード直後に呼ばれる。通常 Unity はリロードで静的 Queue をリセットするため
            // 空のはずだが、万一前セッションのアクション（破棄済み EditorWindow を掴んだクロージャ）が
            // 残っていた場合に備え、防御的に空にしてから再購読する。Drain 自体も世代ガード＋
            // try/catch で死参照を弾くため二重の安全策。
            while (Queue.TryDequeue(out _)) { }
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        // テスト(EditMode の同期テストでは EditorApplication.update が回らない)からも呼ぶ。
        // 回し始めた時点で積まれていた分だけを、目安の時間まで捌く。残りと、捌いている間に積まれた分は
        // 次の tick へ回すので、重いメインスレッドの仕事(シーンのプレビュー用のテクスチャ作りなど)を
        // 分けて積めば、1 フレームにまとめて止めずに少しずつ進む。
        // エディタの毎フレームの更新で呼ばれるので、積まれていなければ何も確保せずに戻る。
        // 時間は Stopwatch のインスタンスを作らずに時刻の差で測る。
        internal static void Drain()
        {
            if (Queue.IsEmpty) return;
            long start = Stopwatch.GetTimestamp();
            for (int n = Queue.Count; n > 0 && Queue.TryDequeue(out var action); n--)
            {
                try { action(); }
                catch (Exception ex) { Debug.LogException(ex); }
                if (Stopwatch.GetTimestamp() - start > TickBudgetTicks) break;
            }
        }

        public static void Post(Action action)
        {
            if (action == null) return;
            Queue.Enqueue(action);
        }
    }

    /// <summary>
    /// バックグラウンド計算 → メインスレッド適用の共通パターンを世代管理付きでラップする。
    /// 新しい Schedule が来たら前のジョブを CancellationToken でキャンセルし、
    /// 古いジョブの結果は世代不一致で破棄する。Dispose でも同様にキャンセルし、
    /// 以降の apply / onError は呼ばれない。
    /// 新しいジョブは、キャンセルした前のジョブが抜け終わってから始める(1 つの PreviewJob で計算が同時に
    /// 走るのは 1 本だけ)。重ねて走らせると、前のジョブがまだ借りている作業配列とは別にもう 1 組を借りることになり、
    /// プールはその組をずっと持ち続ける(Mono のヒープも縮まない。8K で 1 組 2〜3GB)。
    /// </summary>
    internal sealed class PreviewJob<T> : IDisposable
    {
        public bool IsRunning => _isRunning;

        private volatile bool _isRunning;
        private volatile bool _disposed;
        private CancellationTokenSource _cts;
        private int _generation;
        // 最後に投げた計算。次の計算はこれの後ろにつなぐ(メインスレッドからしか触らない)。
        private Task _last = Task.CompletedTask;

        /// <param name="cleanup">
        /// 計算の後(取り消されて計算しなかったときも)、計算したスレッドで必ず 1 回呼ぶ。ジョブが読むために足した
        /// 参照(<see cref="NativePixels.AddRef"/>)を返す所。
        /// </param>
        /// <param name="discard">
        /// 計算は済んだが apply しなかった結果(取り消し・新しい世代・破棄)を、メインスレッドで渡す。結果が
        /// ネイティブメモリを持っているとき、ここで手放す(しないと GC のファイナライザまで残る)。
        /// </param>
        public void Schedule(Func<CancellationToken, T> work, Action<T> apply, Action<Exception> onError = null,
            Action cleanup = null, Action<T> discard = null)
        {
            if (_disposed)
            {
                // 予約しないので、呼び出し側が足した参照はここで返す。
                cleanup?.Invoke();
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int myGen = ++_generation;
            _isRunning = true;

            // 予約から抜けるまでを計算中として数える(閉じたときの解放が、走り終わるのを待って作業配列を返すため)。
            NativeMemoryLifecycle.WorkStarted();
            // 前の計算(キャンセル済み)が抜けてから始める。途中で次の Schedule が来てキャンセルされたら、
            // 計算せずにすぐ抜ける(下の ThrowIfCancellationRequested)。前の計算の成否は見ない。
            _last = _last.ContinueWith(_ =>
            {
                T result = default;
                bool haveResult = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    result = work(token);
                    haveResult = true;
                    token.ThrowIfCancellationRequested();
                    var r = result;
                    haveResult = false;   // ここから先は Post した側が始末する
                    PreviewJobMainThread.Post(() =>
                    {
                        if (TryComplete(myGen)) apply(r);
                        else discard?.Invoke(r);
                    });
                }
                catch (OperationCanceledException)
                {
                    var r = result;
                    bool drop = haveResult;
                    PreviewJobMainThread.Post(() =>
                    {
                        TryComplete(myGen);
                        if (drop) discard?.Invoke(r);
                    });
                }
                catch (Exception ex)
                {
                    PreviewJobMainThread.Post(() =>
                    {
                        if (TryComplete(myGen)) onError?.Invoke(ex);
                    });
                }
                finally
                {
                    try { cleanup?.Invoke(); }
                    catch (Exception ex) { PreviewJobMainThread.Post(() => Debug.LogException(ex)); }
                    NativeMemoryLifecycle.WorkEnded();
                }
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        /// <summary>
        /// 現世代の完了なら実行中フラグを下ろして true。古い世代・破棄後は何もしない
        /// （古い世代の完了通知で、現世代の実行状態を上書きしない）。
        /// </summary>
        private bool TryComplete(int gen)
        {
            if (_disposed || gen != _generation) return false;
            _isRunning = false;
            return true;
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            // in-flight タスクが結果を適用できないよう世代を進める。
            _generation++;
            _isRunning = false;
        }

        public void Dispose()
        {
            _disposed = true;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _cts?.Dispose(); } catch { /* ignore */ }
            _cts = null;
            _isRunning = false;
        }
    }
}
