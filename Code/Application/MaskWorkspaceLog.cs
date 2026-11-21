// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca {
    /// <summary>
    /// 編集中のマスク(Core の <see cref="MaskWorkspace"/>)を保存形式から読むときの、エディタの側の入口。読み飛ばした項目の警告を
    /// コンソールへ出す(Core はログを持たない。文と出る条件は Core の <see cref="MaskWorkspace.DecodeFrom(MaskState, System.Action{string}, out int)"/>)。
    /// </summary>
    internal static class MaskWorkspaceLog {
        /// <summary>保存形式で置き換える(規則は <see cref="MaskWorkspace.DecodeFrom(MaskState, System.Action{string}, out int)"/>)。</summary>
        public static bool DecodeFrom(this MaskWorkspace masks, MaskState ms) => masks.DecodeFrom(ms, Warn, out _);

        /// <summary>同じで、読み飛ばした項目の数を <paramref name="dropped"/> に返す。</summary>
        public static bool DecodeFrom(this MaskWorkspace masks, MaskState ms, out int dropped)
            => masks.DecodeFrom(ms, Warn, out dropped);

        private static void Warn(string message) => UnityEngine.Debug.LogWarning(message);
    }
}
