// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
#if IROCA_INFERENCE_ENGINE
using Unity.InferenceEngine;
#else
using Unity.Sentis;
#endif
using UnityEditor;

namespace Iroca.SentisIntegration
{
    /// <summary>
    /// GPU→CPU のテンソル読出を EditorApplication.update のポーリングで待つ。
    /// 同期 ReadbackAndClone は GPU 実行完了までメインスレッドを停止させる
    /// (4K ズームで毎回 129-194ms、初回エンコードは数百 ms、デコーダは 14〜50ms)ため、ReadbackRequest を
    /// 発行して IsReadbackRequestDone を毎 tick 確認し、完了後の非ブロッキングな
    /// ReadbackAndClone で回収する。CPU バックエンドでも ReadbackRequest は no-op /
    /// IsReadbackRequestDone は Burst fence 完了を見るため同一コードで動く。
    ///
    /// 不変条件: ポーリング中に同一 Worker へ新規 Schedule しないこと(PeekOutput の
    /// テンソルが無効化される)。サービスの Encoding/Decoding フェーズの単一実行が
    /// これを保証する。
    /// 1 本読みの <see cref="Start(Tensor{float}, Action{NativeBuffer}, Action{Exception})"/> は、読み出した中身を
    /// ヒープの外の float の並び(<see cref="NativeBuffer"/>。参照 1)で渡し、受け取った側が持つ
    /// (0.4.0 の計画の B4。以前は 4.2MB の float[] を毎回作っていた)。
    /// </summary>
    internal sealed class EditorReadbackPoller
    {
        // GPU ハング等での永久待ちを避ける安全弁。通常は数十〜数百 ms で完了する。
        const double TimeoutMs = 15000;

        Tensor[] _tensors;
        Func<Action> _read;
        Action<Exception> _onError;
        long _startedAt;
        bool _running;

        public bool IsRunning => _running;

        /// <summary>読出リクエストを発行しポーリングを開始する。tensor は Worker 所有の
        /// PeekOutput 返しを想定(こちらでは Dispose しない)。</summary>
        public void Start(Tensor<float> tensor, Action<NativeBuffer> onDone, Action<Exception> onError)
        {
            Start(new Tensor[] { tensor }, () =>
            {
                NativeBuffer data;
                using (var t = tensor.ReadbackAndClone()) // 完了済みなので非ブロッキング
                    data = SamNativeOps.CopyFloats(t.DownloadToNativeArray());
                if (onDone == null)
                {
                    data?.Release();
                    return null;
                }
                return () => onDone(data);
            }, onError);
        }

        /// <summary>
        /// tensors すべての読出リクエストを発行し、すべて済んだら read をメインスレッドで呼ぶ(中の
        /// ReadbackAndClone は待たない)。read は読んだ中身を受け取った通知を返し(null = 通知しない)、それを読み出しの
        /// 外で呼ぶ。read の例外と時間切れは onError へ。tensors は Worker 所有の PeekOutput 返しを想定(Dispose しない)。
        /// </summary>
        public void Start(Tensor[] tensors, Func<Action> read, Action<Exception> onError)
        {
            Stop();
            try
            {
                foreach (var t in tensors) t.ReadbackRequest();
            }
            catch (Exception e)
            {
                onError?.Invoke(e);
                return;
            }
            _tensors = tensors;
            _read = read;
            _onError = onError;
            _startedAt = MaskSuggestPerf.Now;
            _running = true;
            EditorApplication.update += Tick;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            EditorApplication.update -= Tick;
            // コールバックは埋め込み(4.2MB)や後続処理を捕捉したクロージャであり得る。
            // 参照を残すと次の Start まで解放されない(EditorIteratorPump と同じ規律)。
            _tensors = null;
            _read = null;
            _onError = null;
        }

        void Tick()
        {
            Action done;
            try
            {
                foreach (var t in _tensors)
                {
                    if (t.IsReadbackRequestDone()) continue;
                    if (MaskSuggestPerf.MsSince(_startedAt) > TimeoutMs)
                        throw new TimeoutException(Localization.AiErrReadbackTimeout);
                    return;
                }
                done = _read();
                Stop();
            }
            catch (Exception e)
            {
                var onError = _onError;
                Stop();
                CallGuarded(() => onError?.Invoke(e));
                return;
            }
            // 完了の通知は読み出しの外で呼ぶ(中で投げたものを読み出しの失敗と取り違えない。以前は Stop が
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
