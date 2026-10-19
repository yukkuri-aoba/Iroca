// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// いろかがネイティブメモリに持つ作業配列を、スクリプトのリロードの前に OS へ返す。
    /// static の参照はリロードで消えるので、返さないと塊のファイナライザが走るまで残る。
    /// 計算中の分は計算が返した後にプールへ戻り、リロードのアンロードでファイナライザが解放する。
    /// </summary>
    internal static class NativeMemoryLifecycle
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Release;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
        }

        private static void Release() => PixelProcessor.ReleaseWorkBuffers();
    }
}
