// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 一時 Texture2D の確保・解放補助。メインスレッドからのみ呼ぶ前提。
    /// 再確保時は同サイズなら再利用し、サイズ違いまたは未確保時に作り直す。
    /// </summary>
    internal static class TextureSlot
    {
        /// <summary>
        /// 既存テクスチャが指定サイズに一致しなければ破棄して作り直す。
        /// filterMode は新規確保時のみ適用される（再利用時は既存設定を維持）。
        ///
        /// wrapMode は必ず Clamp にする。Unity の既定は Repeat で、Bilinear と組み合わせると
        /// 拡大描画時に画像の端の外側半テクセルが「反対側の端」と混ざる。プレビューは最大 64x
        /// まで拡大でき、その半テクセルは画面上 zoom/2 px(64x なら 32px)に広がるため、端に接した
        /// マスクオーバーレイが最外周で最大 50%(角は 4 テクセル混合で 25%)まで薄まり、
        /// 「端・角のマスクが表示されていない」ように見えていた。反対側の端の内容次第で薄まったり
        /// 薄まらなかったりするのも同じ原因。Clamp なら端のテクセルがそのまま外側へ延長される。
        /// </summary>
        public static void Resize(ref Texture2D tex, int w, int h,
            FilterMode filter = FilterMode.Bilinear)
        {
            if (tex != null && tex.width == w && tex.height == h) return;
            DestroyNow(ref tex);
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        /// <summary>
        /// テクスチャが存在すれば即時破棄して null にする。
        /// </summary>
        public static void Release(ref Texture2D tex)
        {
            DestroyNow(ref tex);
        }

        private static void DestroyNow(ref Texture2D t)
        {
            if (t == null) return;
            Object.DestroyImmediate(t);
            t = null;
        }
    }
}
