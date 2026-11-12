// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
using System.Collections;
using System.Diagnostics;
using UnityEditor;
using UnityEditorInternal;

namespace Iroca.SentisIntegration {
    /// <summary>
    /// IEnumerator(Worker.ScheduleIterable 等)を EditorApplication.update から
    /// 時間予算内で小刻みに進める。GPU ディスパッチはメインスレッド必須のため、
    /// Task.Run でなくこの方式でエディタの応答性を保つ。
    /// </summary>
    internal sealed class EditorIteratorPump {
        const int BudgetMs = 8;
        // Unity が前面に無いとき。背面では update の回る間隔が延びるので、前面と同じ予算では埋め込みの計算が
        // 何倍も遅れる(MCP から頼んだ自動調整など、利用者が別のアプリにいる間に走る)。背面では誰もエディタを
        // 操作していないので、1 回に長く進める。推論の中身・順は同じで、1 回にいくつ進めるかだけが変わる。
        const int BackgroundBudgetMs = 50;

        IEnumerator _iterator;
        Action _onDone;
        Action<Exception> _onError;
        Action<float> _onProgress;
        int _totalSteps;
        int _steps;
        int _ticks;
        // 最後に知らせた進みの百分率(-1 = まだ知らせていない)。
        int _reportedPercent;
        bool _running;

        // ─── 計測用(直近の実行分。Stop では消さないので onDone 内から読める) ───
        /// <summary>消化したイテレータステップ数。</summary>
        public int Steps => _steps;
        /// <summary>要した EditorApplication.update tick 数。スループットが tick 頻度で
        /// 頭打ちかを見る(Steps/Ticks が予算に届かず小さい = tick 側がボトルネック)。</summary>
        public int Ticks => _ticks;

        /// <summary>実行開始。totalSteps は進捗計算用(0 = 不定)。</summary>
        public void Start(IEnumerator iterator, int totalSteps,
                          Action onDone, Action<Exception> onError, Action<float> onProgress) {
            Stop();
            _iterator = iterator;
            _totalSteps = totalSteps;
            _onDone = onDone;
            _onError = onError;
            _onProgress = onProgress;
            _steps = 0;
            _ticks = 0;
            _reportedPercent = -1;
            _running = true;
            EditorApplication.update += Tick;
        }

        public void Stop() {
            if (!_running) {
                return;
            }
            _running = false;
            EditorApplication.update -= Tick;
            _iterator = null;
            // コールバックは推論の入出力テンソル（CHW で 12MB 級）を捕捉したクロージャであり得る。
            // 参照を残すと次の Start まで解放されない。停止時にまとめて手放す。
            _onDone = null;
            _onError = null;
            _onProgress = null;
        }

        void Tick() {
            _ticks++;
            var sw = Stopwatch.StartNew();
            int budgetMs = InternalEditorUtility.isApplicationActive ? BudgetMs : BackgroundBudgetMs;
            Action done = null;
            try {
                while (sw.ElapsedMilliseconds < budgetMs) {
                    if (!_iterator.MoveNext()) {
                        done = _onDone ?? (() => { });
                        Stop();
                        break;
                    }
                    _steps++;
                }
                if (done == null && _totalSteps > 0 && PercentChanged(_steps, _totalSteps, ref _reportedPercent)) {
                    _onProgress?.Invoke(_steps / (float)_totalSteps);
                }
            } catch (Exception e) {
                var onError = _onError;
                Stop();
                CallGuarded(() => onError?.Invoke(e));
                return;
            }
            // 完了の通知は推論の外で呼ぶ(中で投げたものを推論の失敗と取り違えない。以前は Stop が
            // onError を外した後だったので、黙って捨てられていた)。
            if (done != null) {
                CallGuarded(done);
            }
        }

        /// <summary>
        /// 進みを知らせるか。整数の百分率が、前に知らせたときから変わったときだけ true(lastPercent を進める)。
        /// 受け取る側は状態が変わったとして窓の描き直しを頼むので、更新のたびに知らせると、計算の間ずっと(CPU だと数十秒)
        /// 開いている窓を全面で描き直し続ける。進みの棒は 1% より細かく動いても見分けられない。
        /// </summary>
        internal static bool PercentChanged(int steps, int totalSteps, ref int lastPercent) {
            int percent = (int)(steps * 100L / totalSteps);
            if (percent == lastPercent) {
                return false;
            }
            lastPercent = percent;
            return true;
        }

        // update から呼ばれる入口なので、通知先の例外はここで止める。漏らすと、同じフレームに並んだ
        // 他ツールの update が飛ばされる(Unity は購読者を順に呼ぶだけで例外を止めない)。
        static void CallGuarded(Action a) {
            try { a(); } catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
#endif
