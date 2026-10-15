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
        internal static void Drain()
        {
            var sw = Stopwatch.StartNew();
            for (int n = Queue.Count; n > 0 && Queue.TryDequeue(out var action); n--)
            {
                try { action(); }
                catch (Exception ex) { Debug.LogException(ex); }
                if (sw.ElapsedMilliseconds > TickBudgetMs) break;
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
    /// </summary>
    internal sealed class PreviewJob<T> : IDisposable
    {
        public bool IsRunning => _isRunning;

        private volatile bool _isRunning;
        private volatile bool _disposed;
        private CancellationTokenSource _cts;
        private int _generation;

        public void Schedule(Func<CancellationToken, T> work, Action<T> apply, Action<Exception> onError = null)
        {
            if (_disposed) return;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int myGen = ++_generation;
            _isRunning = true;

            Task.Run(() =>
            {
                try
                {
                    var result = work(token);
                    token.ThrowIfCancellationRequested();
                    PreviewJobMainThread.Post(() =>
                    {
                        if (TryComplete(myGen)) apply(result);
                    });
                }
                catch (OperationCanceledException)
                {
                    PreviewJobMainThread.Post(() => TryComplete(myGen));
                }
                catch (Exception ex)
                {
                    PreviewJobMainThread.Post(() =>
                    {
                        if (TryComplete(myGen)) onError?.Invoke(ex);
                    });
                }
            }, token);
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
