// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using UnityEditor;

namespace Iroca.SentisIntegration
{
    /// <summary>
    /// Sentis 導入環境でのみコンパイルされ、AI マスク提案サービスを本体へ登録する。
    /// Sentis 不在時はこのアセンブリ自体が存在しないため、Bridge.Service は null のまま
    /// (= UI 非表示・出力バイト不変)。
    /// </summary>
    [InitializeOnLoad]
    internal static class SentisMaskSuggestInstaller
    {
        static SentisMaskSuggestInstaller()
        {
            MaskSuggestBridge.Service = new SentisMaskSuggestService();
        }
    }
}
#endif
