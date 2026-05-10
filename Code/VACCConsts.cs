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
