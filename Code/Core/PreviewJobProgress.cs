// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;

namespace Iroca
{
    /// <summary>
    /// バックグラウンド計算中のジョブが、メインスレッドへ進捗値(0..1)と
    /// 任意のフェーズ文字列を中継するための軽量ヘルパー。
    ///
    /// PreviewJob 本体の API は変えず、PreviewJob の work デリゲートから
    /// Report を呼べば、OnGUI から Value / Phase を読み取れる。
    /// </summary>
    internal sealed class PreviewJobProgress
    {
        private float _value;
        private string _phase;

        public float Value => Volatile.Read(ref _value);
        public string Phase => Volatile.Read(ref _phase);

        public void Report(float value, string phase = null)
        {
            if (value < 0f) value = 0f;
            else if (value > 1f) value = 1f;
            Volatile.Write(ref _value, value);
            if (phase != null) Volatile.Write(ref _phase, phase);
        }

        public void Reset()
        {
            Volatile.Write(ref _value, 0f);
            Volatile.Write(ref _phase, null);
        }
    }
}
