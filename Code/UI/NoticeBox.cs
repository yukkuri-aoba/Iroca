// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// いろかウィンドウの上部(テクスチャ欄の下)に出す案内 1 つ。ボタン(閉じる など)は本文の右の列に縦に積む。
    /// 本文の下の行に置いていた頃は、案内 1 つにつき 1 行ぶん上部の高さを使い、横並びでは設定列とプレビューの
    /// 両方が縮んでいた(2026-11-12)。
    /// ボタンの列の幅は、この場所に出る案内のボタンすべてで同じにする(<see cref="ColumnWidth"/>)。案内ごとに
    /// ボタンの幅で決めると、並んだ案内の枠の右端とボタンの幅がそろわなかった。
    /// </summary>
    internal static class NoticeBox {
        // 列の幅の覚え(言語が替わったら測り直す。ボタンの文言は言語でしか変わらない)。
        static float s_columnWidth;
        static LanguageMode s_columnLang;

        /// <summary>
        /// ボタンの列の幅。この場所の案内に出るボタンの文言のうち、いちばん広いものに合わせる。
        /// 出ている案内のボタンだけで決めると、案内が出たり消えたりするたびに列の幅が変わる。
        /// 案内にボタンを足したら、その文言もここに足す。
        /// </summary>
        public static float ColumnWidth() {
            if (s_columnWidth > 0f && s_columnLang == Localization.CurrentLanguage) {
                return s_columnWidth;
            }
            var style = GUI.skin.button;
            float w = 0f;
            foreach (string label in new[] {
                Localization.LargeSourceDismiss,
                Localization.ShadowColorMatchButton,
                Localization.EnableReadWrite,
                Localization.UpdateNoticeChanges,
                Localization.UpdateNoticeHowTo,
                Localization.UpdateNoticeDismiss,
                Localization.AiBannerDismiss,
                Localization.AiSuggestRestartNow,
                Localization.AiSuggestRestartLater,
            }) {
                w = Mathf.Max(w, style.CalcSize(TempContent.Of(label)).x);
            }
            s_columnWidth = Mathf.Ceil(w);
            s_columnLang = Localization.CurrentLanguage;
            return s_columnWidth;
        }

        /// <summary>
        /// 案内を描く。戻り値は押されたボタンの番号(-1 = 押されていない)。案内が出ているときだけ通るので、
        /// 配列の確保は気にしない。ボタンが無ければ全幅で描く。
        /// </summary>
        public static int Draw(string text, MessageType type, params GUIContent[] buttons) {
            if (buttons == null || buttons.Length == 0) {
                EditorGUILayout.HelpBox(text, type);
                return -1;
            }
            float columnW = ColumnWidth();
            int pressed = -1;
            EditorGUILayout.BeginHorizontal();
            // EditorGUILayout.HelpBox は幅の指定を受けない。日本語は語の切れ目が無く、折り返し文の最小幅が本文の長さになって
            // ボタンを右へ押し出すので、MinWidth(0) で領域を取ってから描く。高さは取った幅で折り返した行数になる。
            var sizing = new GUIContent(text, Icon(type));
            Rect box = GUILayoutUtility.GetRect(sizing, EditorStyles.helpBox, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            EditorGUI.HelpBox(box, text, type);
            // ボタンは上に寄せる。FlexibleSpace で上下中央にすると、行が縦に伸びる扱いになってウィンドウの残りの高さを取った。
            EditorGUILayout.BeginVertical(GUILayout.Width(columnW));
            for (int i = 0; i < buttons.Length; i++) {
                if (GUILayout.Button(buttons[i], GUILayout.Width(columnW))) {
                    pressed = i;
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            return pressed;
        }

        // 案内の絵(EditorGUI.HelpBox が描くものと同じ。高さの見積もりに使う)。
        static Texture Icon(MessageType type) {
            switch (type) {
                case MessageType.Info:
                    return EditorGUIUtility.IconContent("console.infoicon").image;
                case MessageType.Warning:
                    return EditorGUIUtility.IconContent("console.warnicon").image;
                case MessageType.Error:
                    return EditorGUIUtility.IconContent("console.erroricon").image;
                default:
                    return null;
            }
        }
    }
}
