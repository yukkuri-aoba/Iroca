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
            var buttonStyle = GUI.skin.button;
            var m = buttonStyle.margin;
            float buttonH = buttonStyle.CalcSize(buttons[0]).y;
            float gap = Mathf.Max(m.top, m.bottom);
            float stackH = buttons.Length * buttonH + (buttons.Length - 1) * gap;

            // 行の高さは本文の枠とボタンの積みの高い方。枠は少なくともボタンの積みの高さを取り、ボタンは枠の上下の中央に置く
            // (ボタンを 2 つ積んだ案内で、枠の下端よりボタンがはみ出していた)。ボタンは自動の並べ方に任せず、取っておいた列に
            // 枠の矩形から位置を決めて描く。FlexibleSpace で中央に寄せると、行が縦に伸びる扱いになってウィンドウの残りの
            // 高さを取った(ExpandHeight(false) を付けても止まらない)。
            EditorGUILayout.BeginHorizontal();
            // EditorGUILayout.HelpBox は幅の指定を受けない。日本語は語の切れ目が無く、折り返し文の最小幅が本文の長さになって
            // ボタンを右へ押し出すので、MinWidth(0) で領域を取ってから描く。高さは取った幅で折り返した行数になる。
            // MinHeight は付けない: 折り返す文で最小の高さを指定すると、Unity は折り返した高さを捨ててその値にする
            // (2 行の本文が 1 行の高さに潰れた)。ボタンの積みの高さは列のほうで取り、枠は描くときだけ伸ばす。
            var sizing = new GUIContent(text, Icon(type));
            Rect box = GUILayoutUtility.GetRect(sizing, EditorStyles.helpBox, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            // ボタンの列。左右の余白はボタンの余白と同じだけ空ける(自動で並べていた頃と同じ間隔)。高さは、枠を積みの高さまで
            // 伸ばしたときの枠の外側(枠の上下の余白を含む)。
            var boxMargin = EditorStyles.helpBox.margin;
            GUILayoutUtility.GetRect(columnW + m.left + m.right, stackH + boxMargin.top + boxMargin.bottom, GUIStyle.none,
                GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();

            box.height = Mathf.Max(box.height, stackH);
            EditorGUI.HelpBox(box, text, type);
            // Layout のときの矩形は仮のものだが、ボタンは数と順番だけそろっていればよい(押した判定は Layout 以外で行われる)。
            float x = box.xMax + boxMargin.right + m.left;
            float y = box.y + (box.height - stackH) * 0.5f;
            int pressed = -1;
            for (int i = 0; i < buttons.Length; i++) {
                var r = new Rect(x, y + i * (buttonH + gap), columnW, buttonH);
                if (GUI.Button(r, buttons[i])) {
                    pressed = i;
                }
            }
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
