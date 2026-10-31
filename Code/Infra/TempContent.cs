// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// OnGUI の中で毎回 new GUIContent しないための使い回し。コントロールへ渡してその場で読ませるときだけ使う。
    /// 持ち続ける相手(GenericMenu・ShowNotification・titleContent・配列)には new GUIContent を渡す。少し後で書き換わる。
    /// </summary>
    internal static class TempContent
    {
        // 1 つのコントロールが読むまでに別の Of が挟まっても壊れない数(ローカル変数に受けて数行後に使う所がある)。
        private const int RingSize = 16;

        private static readonly GUIContent[] s_ring = CreateRing();
        private static int s_next;

        private static GUIContent[] CreateRing()
        {
            var ring = new GUIContent[RingSize];
            for (int i = 0; i < ring.Length; i++) ring[i] = new GUIContent();
            return ring;
        }

        public static GUIContent Of(string text, string tooltip = "")
        {
            GUIContent content = s_ring[s_next];
            s_next = (s_next + 1) % RingSize;
            content.text = text;
            content.tooltip = tooltip;
            return content;
        }
    }
}
