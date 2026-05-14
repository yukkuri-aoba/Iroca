using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// EditorGUILayout の各コントロールに「変更前の状態を Unity の Undo に登録する」
    /// 振る舞いを足したラッパ群。
    /// 内側で EditorGUILayout を呼んで戻り値 n を取得し、入力 value と異なれば
    /// その時点（呼び出し側の代入前）で Undo.RecordObject(host, ...) を呼ぶ。
    /// 呼び出し側でまだ代入が行われていないため、host の状態は「変更前」が記録される。
    /// 同一フレーム内・同一 Undo 名なら 1 ステップに合流するため、連続スライダー操作も
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

        public static float Slider(Object host, string label, float value, float min, float max, string undoName = DefaultUndoName)
        {
            float n = EditorGUILayout.Slider(label, value, min, max);
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

        public static bool ToggleLeft(Object host, GUIContent content, bool value, params GUILayoutOption[] options)
        {
            bool n = EditorGUILayout.ToggleLeft(content, value, options);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static Color ColorField(Object host, GUIContent content, Color value)
        {
            Color n = EditorGUILayout.ColorField(content, value);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static string TextField(Object host, GUIContent content, string value, params GUILayoutOption[] options)
        {
            string n = EditorGUILayout.TextField(content, value, options);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static T EnumPopup<T>(Object host, GUIContent content, T value) where T : System.Enum
        {
            var n = (T)EditorGUILayout.EnumPopup(content, value);
            if (!System.Object.Equals(n, value)) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }

        public static T ObjectField<T>(Object host, GUIContent content, T value, bool allowSceneObjects) where T : Object
        {
            T n = (T)EditorGUILayout.ObjectField(content, value, typeof(T), allowSceneObjects);
            if (n != value) Undo.RecordObject(host, DefaultUndoName);
            return n;
        }
    }
}
