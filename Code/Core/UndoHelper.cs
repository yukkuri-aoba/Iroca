// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
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
        private const string DefaultUndoName = "Iroca Edit";

        public static float Slider(Object host, GUIContent content, float value, float min, float max, string undoName = DefaultUndoName)
        {
            // 数値欄には小数 3 桁へ丸めた値を見せる。自動調整が書く値は 0.80859375 のように
            // 桁が長く、既定幅(50px)の数値欄では末尾が欠けて読めなかった。丸めるのは表示だけで、
            // ユーザーが触らない限り保存値（＝処理結果）は 1 ビットも変えない。
            float shown = Mathf.Clamp(Mathf.Round(value * 1000f) / 1000f, min, max);
            float n = EditorGUILayout.Slider(content, shown, min, max);
            if (n == shown) return value;
            Undo.RecordObject(host, undoName);
            return n;
        }

        public static float Slider(Object host, string label, float value, float min, float max, string undoName = DefaultUndoName)
            => Slider(host, new GUIContent(label), value, min, max, undoName);

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

        public static Color ColorField(Object host, GUIContent content, Color value, params GUILayoutOption[] options)
        {
            // Unity 純正のスポイト（スクリーン色を吸う）は無効化する。色取得は実テクスチャ画素を
            // 読む独自の「プレビュー直接スポイト」に一本化しており、純正スポイトは機能重複かつ
            // ガンマ空間のスクリーン色を拾うため精度的に劣り、誤操作の元になる。
            Color n = EditorGUILayout.ColorField(content, value, false, true, false, options);
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
