// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca
{
    // UI 型 IrocaWindow を引数に取るので UI 側に置く（Core を headless でコンパイルできるように）。
    internal static partial class DebugCaptureHooks
    {
        /// <summary>
        /// IrocaWindow の OnGUI から発火される foldout 描画イベント。
        /// subscriber がいないときは何も描画されない（本体 UI に影響なし）。
        /// </summary>
        internal static event Action<IrocaWindow> OnDrawFoldout;

        /// <summary>
        /// IrocaWindow から foldout イベントを発火する薄いラッパ。
        /// </summary>
        internal static void RaiseDrawFoldout(IrocaWindow window)
        {
            OnDrawFoldout?.Invoke(window);
        }
    }
}
