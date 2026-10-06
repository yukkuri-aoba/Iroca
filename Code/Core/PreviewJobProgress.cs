// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;

namespace Iroca
{
    /// <summary>
    /// バックグラウンド計算中のジョブが、メインスレッドへ進捗値(0..1)を
    /// 中継するための軽量ヘルパー。
    ///
    /// PreviewJob 本体の API は変えず、PreviewJob の work デリゲートから
    /// Report を呼べば、OnGUI から Value を読み取れる。
    /// </summary>
    internal sealed class PreviewJobProgress
    {
        private float _value;

        public float Value => Volatile.Read(ref _value);

        public void Report(float value)
        {
            if (value < 0f) value = 0f;
            else if (value > 1f) value = 1f;
            Volatile.Write(ref _value, value);
        }

        public void Reset()
        {
            Volatile.Write(ref _value, 0f);
        }
    }
}
