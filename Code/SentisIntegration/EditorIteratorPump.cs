// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
using System.Collections;
using System.Diagnostics;
using UnityEditor;

namespace Iroca.SentisIntegration
{
    /// <summary>
    /// IEnumerator(Worker.ScheduleIterable 等)を EditorApplication.update から
    /// 時間予算内で小刻みに進める。GPU ディスパッチはメインスレッド必須のため、
    /// Task.Run でなくこの方式でエディタの応答性を保つ。
    /// </summary>
    internal sealed class EditorIteratorPump
    {
        const int BudgetMs = 8;

        IEnumerator _iterator;
        Action _onDone;
        Action<Exception> _onError;
        Action<float> _onProgress;
        int _totalSteps;
        int _steps;
        bool _running;

        public bool IsRunning => _running;

        /// <summary>実行開始。totalSteps は進捗計算用(0 = 不定)。</summary>
        public void Start(IEnumerator iterator, int totalSteps,
                          Action onDone, Action<Exception> onError, Action<float> onProgress)
        {
            Stop();
            _iterator = iterator;
            _totalSteps = totalSteps;
            _onDone = onDone;
            _onError = onError;
            _onProgress = onProgress;
            _steps = 0;
            _running = true;
            EditorApplication.update += Tick;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            EditorApplication.update -= Tick;
            _iterator = null;
        }

        void Tick()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < BudgetMs)
                {
                    if (!_iterator.MoveNext())
                    {
                        var done = _onDone;
                        Stop();
                        done?.Invoke();
                        return;
                    }
                    _steps++;
                }
                if (_totalSteps > 0)
                    _onProgress?.Invoke(_steps / (float)_totalSteps);
            }
            catch (Exception e)
            {
                var onError = _onError;
                Stop();
                onError?.Invoke(e);
            }
        }
    }
}
#endif
