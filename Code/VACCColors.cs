using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    // GUI で使うカラー定数を Light/Dark Skin で出し分けるユーティリティ。
    internal static class VACCColors
    {
        public static Color ActiveMaskTarget =>
            EditorGUIUtility.isProSkin
                ? new Color(0.45f, 0.65f, 0.95f)
                : new Color(0.30f, 0.55f, 0.85f);

        public static Color ExcludeButton =>
            EditorGUIUtility.isProSkin
                ? new Color(1f, 0.55f, 0.55f)
                : new Color(0.85f, 0.30f, 0.30f);

        public static Color IncludeButton =>
            EditorGUIUtility.isProSkin
                ? new Color(0.55f, 1f, 0.55f)
                : new Color(0.30f, 0.75f, 0.30f);

        // ブラシカーソルは Exclude/Include の意味区別を保つため 2 定数に分離する。
        public static Color BrushCursorExclude => new Color(1f, 0f, 0f, 0.5f); // 赤
        public static Color BrushCursorInclude => new Color(0f, 1f, 0f, 0.5f); // 緑
    }
}
