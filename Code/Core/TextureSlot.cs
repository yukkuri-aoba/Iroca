using UnityEngine;

namespace VRCAvatarColorChanger
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
        /// </summary>
        public static void Resize(ref Texture2D tex, int w, int h,
            FilterMode filter = FilterMode.Bilinear)
        {
            if (tex != null && tex.width == w && tex.height == h) return;
            DestroyNow(ref tex);
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = filter };
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
