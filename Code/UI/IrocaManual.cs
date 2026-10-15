// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マニュアルを開く。マニュアルはオンライン版(GitHub Pages)だけで配り、パッケージには入れない。
    /// <para>
    /// HTML は docs/manual/build_manual.py が原稿から作り、scripts/pages/build_site.py が Pages に載せる。
    /// 手元に古い版が残らず、いつも最新の説明が開く(入れている版より新しい説明になることはある)。
    /// </para>
    /// </summary>
    internal static class IrocaManual
    {
        public const string OnlineUrl = "https://yukkuri-aoba.github.io/Iroca/manual/";

        public static void Open() => Application.OpenURL(OnlineUrl);
    }
}
