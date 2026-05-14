using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// PreviewJob<T> から投入されたメインスレッド復帰アクションを EditorApplication.update で捌くポンプ。
    /// バックグラウンドスレッドが直接 Texture2D / EditorWindow に触らず、ここに Action をキュー
    /// した上で次の Editor tick でドレインする。
    /// </summary>
    internal static class PreviewJobMainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        [InitializeOnLoadMethod]
        private static void Install()
        {
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        private static void Drain()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogException(ex); }
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
                        if (_disposed || myGen != _generation) return;
                        _isRunning = false;
                        apply(result);
                    });
                }
                catch (OperationCanceledException)
                {
                    PreviewJobMainThread.Post(() =>
                    {
                        // 古い世代のキャンセルでも、現世代でなければ _isRunning は触らない
                        if (_disposed || myGen != _generation) return;
                        _isRunning = false;
                    });
                }
                catch (Exception ex)
                {
                    PreviewJobMainThread.Post(() =>
                    {
                        if (_disposed || myGen != _generation) return;
                        _isRunning = false;
                        onError?.Invoke(ex);
                    });
                }
            }, token);
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            // 世代をぶつけて in-flight タスクの apply を抑止
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
