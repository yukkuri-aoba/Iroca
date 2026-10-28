// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// 後から届く結果(自動調整・AI 提案・レシピの読み直し)を、独立した「元に戻す」1 回分として記録する
    /// (0.4.0 の計画の 4-1 で IrocaWindow から移した)。
    /// </summary>
    internal static class SeparateUndo
    {
        /// <summary>
        /// Unity の Undo のまとまりはマウスを押したとき・メニューを実行したときにしか切り替わらないので、
        /// 結果を待っている間に別のウィンドウでした操作(物の移動・マテリアルの値の変更など)と同じ 1 回分に
        /// まとまり、Ctrl+Z 1 回で両方が戻っていた。記録の前後で区切る(後ろも区切るのは、届いた直後に
        /// 続く別の操作をこちらへ混ぜないため)。
        /// </summary>
        internal static void Record(string name, System.Action record)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            int group = Undo.GetCurrentGroup();
            try { record(); }
            finally
            {
                // 記録の中で別の名前が付いても(マスクのストロークなど)、このまとまりの名前で出す。
                Undo.SetCurrentGroupName(name);
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
        }
    }
}
