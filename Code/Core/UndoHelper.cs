using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// EditorGUILayout の各コントロールに「変更前の状態を Unity の Undo に登録する」
    /// 振る舞いを足したラッパ群。新しい値が古い値と異なる場合のみ Undo.RecordObject を呼ぶ。
    /// Undo 名は同一フレーム内で同じ名前なら 1 ステップに合流するため、連続スライダー操作も
    /// 1 ステップで戻せる。
    /// </summary>
    internal static class UndoHelper
    {
        private const string DefaultUndoName = "VACC Edit";

        public static float Slider(Object host, GUIContent content, float value, float min, float max, string undoName = DefaultUndoName)
        {
            float n = EditorGUILayout.Slider(content, value, min, max);
            if (!Mathf.Approximately(n, value)) Undo.RecordObject(host, undoName);
            return n;
        }

        public static int IntSlider(Object host, GUIContent content, int value, int min, int max, string undoName = DefaultUndoName)
        {
            int n = EditorGUILayout.IntSlider(content, value, min, max);
            if (n != value) Undo.RecordObject(host, undoName);
            return n;
        }

        public static int IntField(Object host, int value, params GUILayoutOption[] options)
        {
            int n = EditorGUILayout.IntField(value, options);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static bool Toggle(Object host, GUIContent content, bool value)
        {
            bool n = EditorGUILayout.Toggle(content, value);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static bool ToggleLeft(Object host, GUIContent content, bool value)
        {
            bool n = EditorGUILayout.Toggle(value, GUILayout.Width(16));
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static Color ColorField(Object host, GUIContent content, Color value)
        {
            Color n = EditorGUILayout.ColorField(content, value);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static string TextField(Object host, GUIContent content, string value)
        {
            string n = EditorGUILayout.TextField(content, value);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static System.Enum EnumPopup(Object host, GUIContent content, System.Enum value)
        {
            var n = EditorGUILayout.EnumPopup(content, value);
            if (!System.Object.Equals(n, value)) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static T EnumPopup<T>(Object host, GUIContent content, T value) where T : System.Enum
        {
            return (T)EnumPopup(host, content, (System.Enum)value);
        }

        public static Rect RectField(Object host, Rect value, float minXY, float maxXY)
        {
            // Slider ベースで X/Y/W/H を出すパターンは直接呼び出し側で UndoHelper.Slider を 4 回使う想定。
            return value;
        }
    }
}
