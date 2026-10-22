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
        int _ticks;
        bool _running;

        public bool IsRunning => _running;

        // ─── 計測用(直近の実行分。Stop では消さないので onDone 内から読める) ───
        /// <summary>消化したイテレータステップ数。</summary>
        public int Steps => _steps;
        /// <summary>要した EditorApplication.update tick 数。スループットが tick 頻度で
        /// 頭打ちかを見る(Steps/Ticks が予算に届かず小さい = tick 側がボトルネック)。</summary>
        public int Ticks => _ticks;

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
            _ticks = 0;
            _running = true;
            EditorApplication.update += Tick;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            EditorApplication.update -= Tick;
            _iterator = null;
            // コールバックは推論の入出力テンソル（CHW で 12MB 級）を捕捉したクロージャであり得る。
            // 参照を残すと次の Start まで解放されない。停止時にまとめて手放す。
            _onDone = null;
            _onError = null;
            _onProgress = null;
        }

        void Tick()
        {
            _ticks++;
            var sw = Stopwatch.StartNew();
            Action done = null;
            try
            {
                while (sw.ElapsedMilliseconds < BudgetMs)
                {
                    if (!_iterator.MoveNext())
                    {
                        done = _onDone ?? (() => { });
                        Stop();
                        break;
                    }
                    _steps++;
                }
                if (done == null && _totalSteps > 0)
                    _onProgress?.Invoke(_steps / (float)_totalSteps);
            }
            catch (Exception e)
            {
                var onError = _onError;
                Stop();
                CallGuarded(() => onError?.Invoke(e));
                return;
            }
            // 完了の通知は推論の外で呼ぶ(中で投げたものを推論の失敗と取り違えない。以前は Stop が
            // onError を外した後だったので、黙って捨てられていた)。
            if (done != null) CallGuarded(done);
        }

        // update から呼ばれる入口なので、通知先の例外はここで止める。漏らすと、同じフレームに並んだ
        // 他ツールの update が飛ばされる(Unity は購読者を順に呼ぶだけで例外を止めない)。
        static void CallGuarded(Action a)
        {
            try { a(); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
#endif
