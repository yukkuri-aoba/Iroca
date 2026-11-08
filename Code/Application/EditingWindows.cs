// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 自動化 API(MCP)が、いろかのウィンドウを開く・表示中の編集を読む・読み直させるための窓口。
    /// 自動化は UI の型を知らない(docs/architecture.md の 1 章)ので、UI が起動時に実体を
    /// <see cref="EditingWindows.Current"/> へ登録する。メインスレッド専用。
    /// </summary>
    internal interface IEditingWindows
    {
        /// <summary>レシピを開く(既に開いていれば何もしない)。</summary>
        void OpenRecipe(IrocaRecipe recipe);

        /// <summary>テクスチャを開く(既に開いていれば何もしない)。</summary>
        void OpenTexture(Texture2D texture);

        /// <summary>テクスチャを表示している窓があれば true と、その窓が結び付いているレシピを返す。</summary>
        bool IsShowing(Texture2D texture, out IrocaRecipe boundRecipe);

        /// <summary>テクスチャを表示している窓の編集内容の写し(無ければ null)。</summary>
        IrocaSessionState EditingStateOf(Texture2D texture);

        /// <summary>レシピのテクスチャを表示している窓に、レシピから読み直させる(Undo に積む)。</summary>
        void ReloadFromRecipe(IrocaRecipe recipe, string undoName);

        /// <summary>
        /// テクスチャを表示している窓が、その元ファイルを今読み直したのと同じ作業解像度の画素を持っていれば、参照を 1 つ
        /// 足して返す(<see cref="SourcePixelCache.ShareIfCurrent"/>。呼んだ側が Release する。窓と共有なので書き換えない)。
        /// 無ければ null(呼んだ側が読む)。
        /// </summary>
        NativePixels ShareSourcePixels(Texture2D texture);
    }

    internal static class EditingWindows
    {
        /// <summary>実体。ドメインのロード時に UI が登録する。</summary>
        internal static IEditingWindows Current;
    }
}
