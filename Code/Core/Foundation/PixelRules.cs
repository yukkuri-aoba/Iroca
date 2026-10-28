// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    /// <summary>
    /// 画素の決まり(0.4.0 の計画の 3-4 で PixelProcessor から切り出した)。PixelProcessor の同名の定数は転送役。
    /// </summary>
    internal static class PixelRules
    {
        // 不透明とみなす α の下限。統計・連結成分・閉領域・自動調整のサンプリングの対象画素はこれ以上。
        // HighlightSampleCorrector と ZoneAutoTuner も同じ規約で数える。
        internal const int OpaqueAlphaMin = 128;
    }
}
