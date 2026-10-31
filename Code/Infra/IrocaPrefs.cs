// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca
{
    /// <summary>
    /// EditorPrefs(PC ごとの設定)と SessionState(エディタを閉じるまでの状態)のキーの一覧(0.4.0 の計画の 4-1)。
    /// 以前は 20 余りのファイルがそれぞれ定数を持っていた(同じキーを 2 か所で定義していたものもある)。
    /// キーの文字列は利用者の設定そのものなので**変えない**。変えると設定が黙って既定に戻り、閉じた案内がまた出る。
    /// 変えるときは古いキーから読んで移す処理を書き、scripts/source_checks/test_editor_prefs_keys.py の一覧も直す。
    /// 名前が Prefix で終わるものは前置きで、後ろにパス・GUID・ID などを足して使う。
    /// </summary>
    internal static class IrocaPrefs
    {
        // ── EditorPrefs(PC ごと) ──
        internal const string Language = "Iroca.Language";
        internal const string EnglishTranslationNoticeShown = "Iroca.EnglishTranslationNoticeShown";
        internal const string ContextMenuUsed = "Iroca.ContextMenuUsed";
        internal const string SceneClickUsed = "Iroca.SceneClickUsed";
        internal const string ShowDeveloperSettings = "Iroca.ShowDeveloperSettings";
        internal const string ShowMeshIslands = "Iroca.ShowMeshIslands";
        internal const string PreviewSmoothMagnify = "Iroca.PreviewSmoothMagnify";
        internal const string PreviewDetailFullResolution = "Iroca.PreviewDetailFullResolution";
        internal const string LastTextureGuidPrefix = "Iroca.LastTextureGuid.";          // + プロジェクトのパスのハッシュ
        internal const string MaskBrushWindowPlacedPrefix = "Iroca.MaskBrushWindowPlaced:";  // + プロジェクトのパス
        internal const string AiSuggestPerfLog = "Iroca.AiSuggestPerfLog";
        internal const string UpdateCheckEnabled = "Iroca.UpdateCheck.Enabled";
        internal const string UpdateCheckDismissed = "Iroca.UpdateCheck.Dismissed";
        internal const string UpdateCheckLatestPrefix = "Iroca.UpdateCheck.Latest:";           // + Unity の版(+ ":pre")
        internal const string UpdateCheckCheckedAtUtcPrefix = "Iroca.UpdateCheck.CheckedAtUtc:"; // + Unity の版(+ ":pre")
        // 開発者向け(Code/Debug)
        internal const string DebugEnableCapture = "Iroca.Debug.EnableCapture";
        internal const string DebugMaterialSelection = "Iroca.Debug.MaterialSelection";
        internal const string DebugMasterEnabled = "Iroca.Debug.MasterEnabled";
        internal const string DebugWindowMode = "Iroca.Debug.Mode";
        internal const string DebugSelectedStage = "Iroca.Debug.SelectedStage";
        internal const string DebugSelectedZone = "Iroca.Debug.SelectedZone";
        internal const string PerfThreadOverride = "Iroca.Perf.ThreadOverride";
        internal const string ReproDumpLastDir = "Iroca.ReproDump.LastDir";

        // ── SessionState(エディタを閉じるまで) ──
        internal const string UpdateCheckTried = "Iroca.UpdateCheck.Tried";
        internal const string WindowEnabledPrefix = "Iroca.WindowEnabled.";               // + ウィンドウの InstanceID
        internal const string RecipeSyncedPrefix = "Iroca.RecipeSynced.";                 // + レシピの GUID
        internal const string MaterialColorDismissedPrefix = "Iroca.MaterialColor.Dismissed:"; // + テクスチャのパス
        internal const string LargeSourceDismissedPrefix = "Iroca.LargeSource.Dismissed:";     // + テクスチャのパス
        internal const string AiSetupBannerDismissed = "Iroca.AiSetup.BannerDismissed";
        internal const string BurstCompileFailed = "Iroca.Burst.CompileFailed";
        internal const string SentisRestartRecommended = "Iroca.Sentis.RestartRecommended";
        // 古い版のマスクの置き場(+ テクスチャのパス)。読んで移したら消す。
        internal const string LegacyMaskIndexPrefix = "Iroca_MaskIndex_";
        internal const string LegacyMaskPrefix = "Iroca_Mask_";
    }
}
