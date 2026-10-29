// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// 開いている編集(いろかのウィンドウ)の一覧(0.4.0 の計画の 4-2 の 3)。ウィンドウが OnEnable で登録し、
    /// OnDisable で外す。以前はウィンドウを型で探していた(<c>Resources.FindObjectsOfTypeAll&lt;IrocaWindow&gt;</c>)ので、
    /// ウィンドウより下の層(非破壊ビルド・更新の確認・自動化)は UI の型を知るしかなかった。
    /// 計画では Application の層だったが、Infra(非破壊ビルドの作業配列の手放し・更新の確認の再描画)からも使うので
    /// Infra に置いた。持つのはウィンドウそのもの(UI の型は知らないので <see cref="EditorWindow"/> として持つ)。
    /// 編集中の文書(EditingSession)は Application の層にあり、Infra からは見えないので持たない(ウィンドウから取る)。
    /// 並びは登録した順(先に開いたものが先頭)。ドメインリロードで空になり、ウィンドウの OnEnable がまた登録する。
    /// メインスレッド専用。
    /// </summary>
    internal static class EditingSessionRegistry
    {
        private static readonly List<EditorWindow> s_open = new List<EditorWindow>();

        /// <summary>開いた編集を足す(同じものを 2 度足しても 1 つ)。</summary>
        internal static void Register(EditorWindow window)
        {
            if (window != null && !s_open.Contains(window)) s_open.Add(window);
        }

        /// <summary>閉じた編集を外す。</summary>
        internal static void Unregister(EditorWindow window) => s_open.Remove(window);

        /// <summary>開いている編集があるか。</summary>
        internal static bool Any
        {
            get
            {
                Prune();
                return s_open.Count > 0;
            }
        }

        /// <summary>開いている編集を 1 つ(無ければ null)。複数あるときは先に開いたもの。</summary>
        internal static EditorWindow First
        {
            get
            {
                Prune();
                return s_open.Count > 0 ? s_open[0] : null;
            }
        }

        /// <summary>
        /// 開いている編集の写し。呼び出し側が回している間に開け閉めがあっても(読み直しで窓を閉じるなど)
        /// 壊れないよう、配列に写して返す。
        /// </summary>
        internal static EditorWindow[] Snapshot()
        {
            Prune();
            return s_open.ToArray();
        }

        /// <summary>開いている編集を全部描き直す。</summary>
        internal static void RepaintAll()
        {
            foreach (var w in Snapshot()) w.Repaint();
        }

        // OnDisable を通らずに消えたもの(Unity の null)を外す。普通は起きないが、型で探していたときと同じく
        // 消えたウィンドウを返さないようにする。
        private static void Prune() => s_open.RemoveAll(w => w == null);
    }
}
