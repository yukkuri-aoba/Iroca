namespace VRCAvatarColorChanger
{
    // VACC で散在するマジックナンバー・メニューパスを集約する。
    // 表示文字列の正は Localization に残し、ここでは多言語化しない値だけを持つ。
    internal static class VACCConsts
    {
        public const string MenuPath = "Tools/VRC AvatarColorChanger";

        public static class Layout
        {
            public const float SideBySideMinWidth = 600f;
            public const float LeftColumnRatio    = 0.4f;
            public const float LeftColumnMin      = 280f;
            public const float LeftColumnMax      = 450f;
            public const float RemoveButtonWidth  = 22f;
            public const float SmallButtonWidth   = 48f;
            // EditorWindow.position はタイトルバー/タブバー（ウィンドウクローム）の高さを含むが、
            // GUI 描画領域はそれより小さい。エクスポートを常にウィンドウ内に収めるための安全マージン。
            // Windows 11 のフローティングウィンドウではタイトルバーが ~30-32px あり、
            // 加えて下端のリサイズハンドルも考慮して余裕を持たせる。
            public const float WindowChromeMargin = 44f;
            // スクロール対象の中央領域に確保する最低高さ。
            // ウィンドウが極端に低い時もこの高さは確保され、内部スクロールで残りを閲覧する。
            public const float MiddleAreaMinHeight = 120f;
        }

        public static class Preview
        {
            // メインプレビューの最大寸法（長辺）。
            // ソーステクスチャはこのサイズへ等比縮小されてから表示・処理される。
            public const int MaxSize = 512;
        }

        public static class ExperimentalFeatures
        {
            // 連続領域モードは実装継続中のため、当面は UI/処理の両方で無効化する。
            public const bool EnableFloodFill = false;
        }
    }
}
