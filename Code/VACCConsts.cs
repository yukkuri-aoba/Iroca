namespace VRCAvatarColorChanger
{
    // VACC で散在するマジックナンバー・メニューパスを集約する。
    // 表示文字列の正は Localization に残し、ここでは多言語化しない値だけを持つ。
    internal static class VACCConsts
    {
        public const string MenuPath = "Tools/yukkuri-aoba/VRC AvatarColorChanger";

        public static class Layout
        {
            public const float SideBySideMinWidth = 600f;
            public const float LeftColumnRatio    = 0.4f;
            public const float LeftColumnMin      = 280f;
            public const float LeftColumnMax      = 450f;
            public const float RemoveButtonWidth  = 22f;
            public const float SmallButtonWidth   = 48f;
        }
    }
}
