// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
using Unity.Sentis;
using UnityEditor;

namespace Iroca.SentisIntegration
{
    /// <summary>
    /// GPU→CPU のテンソル読出を EditorApplication.update のポーリングで待つ。
    /// 同期 ReadbackAndClone は GPU 実行完了までメインスレッドを停止させる
    /// (4K ズームで毎回 129-194ms、初回エンコードは数百 ms)ため、ReadbackRequest を
    /// 発行して IsReadbackRequestDone を毎 tick 確認し、完了後の非ブロッキングな
    /// ReadbackAndClone で回収する。CPU バックエンドでも ReadbackRequest は no-op /
    /// IsReadbackRequestDone は Burst fence 完了を見るため同一コードで動く。
    ///
    /// 不変条件: ポーリング中に同一 Worker へ新規 Schedule しないこと(PeekOutput の
    /// テンソルが無効化される)。サービスの Encoding/Decoding フェーズの単一実行が
    /// これを保証する。
    /// </summary>
    internal sealed class EditorReadbackPoller
    {
        // GPU ハング等での永久待ちを避ける安全弁。通常は数十〜数百 ms で完了する。
        const double TimeoutMs = 15000;

        Tensor<float> _tensor;
        Action<float[]> _onDone;
        Action<Exception> _onError;
        long _startedAt;
        bool _running;

        public bool IsRunning => _running;

        /// <summary>読出リクエストを発行しポーリングを開始する。tensor は Worker 所有の
        /// PeekOutput 返しを想定(こちらでは Dispose しない)。</summary>
        public void Start(Tensor<float> tensor, Action<float[]> onDone, Action<Exception> onError)
        {
            Stop();
            try
            {
                tensor.ReadbackRequest();
            }
            catch (Exception e)
            {
                onError?.Invoke(e);
                return;
            }
            _tensor = tensor;
            _onDone = onDone;
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
            _tensor = null;
            _onDone = null;
            _onError = null;
        }

        void Tick()
        {
            try
            {
                if (!_tensor.IsReadbackRequestDone())
                {
                    if (MaskSuggestPerf.MsSince(_startedAt) > TimeoutMs)
                        throw new TimeoutException("GPU からの読出がタイムアウトしました");
                    return;
                }
                float[] data;
                using (var t = _tensor.ReadbackAndClone()) // 完了済みなので非ブロッキング
                    data = t.DownloadToArray();
                var done = _onDone;
                Stop();
                done?.Invoke(data);
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
