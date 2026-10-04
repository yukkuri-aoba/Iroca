// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// 編集内容(ゾーン・色・処理設定・マスク、結び付いたレシピ)の自動保存。
    /// <para>
    /// 以前は保存がテクスチャの切り替え・ウィンドウを閉じる・ドメインリロード・非破壊ビルドの直前だけで、
    /// Unity が落ちると直前の編集が消えた。非破壊ではレシピが成果物そのものなので、編集が止まって
    /// 少ししたら保存する。編集の合図は PreviewView の previewDirty(編集はどれもプレビューの作り直しを
    /// 経由する)。スライダーのドラッグ中・マスクを塗っている途中・書き出しなどの最中は保存しない。
    /// 保存先はどれも中身が変わっていなければ書き込まないか、同じ中身を書くだけなので、空振りしても害は無い。
    /// </para>
    /// </summary>
    public partial class IrocaWindow
    {
        // 最後の編集から保存するまでの待ち。ドラッグ中は編集のたびに延びるので、止まってから 1 回になる。
        private const double AutosaveDelaySeconds = 2.0;
        // 0 = 予定なし。
        [System.NonSerialized] private double _autosaveDue;

        /// <summary>編集があった(PreviewView が previewDirty を立てたとき呼ぶ)。</summary>
        internal void NoteSessionEdited()
            => _autosaveDue = EditorApplication.timeSinceStartup + AutosaveDelaySeconds;

        private void StartAutosave()
        {
            EditorApplication.update -= AutosaveTick;
            EditorApplication.update += AutosaveTick;
        }

        private void StopAutosave()
        {
            EditorApplication.update -= AutosaveTick;
            _autosaveDue = 0;
        }

        private void AutosaveTick()
        {
            if (_autosaveDue <= 0 || EditorApplication.timeSinceStartup < _autosaveDue) return;
            // 書き出し・手動の自動調整の最中や、マスクを塗っている途中は待つ(終わってから 1 回保存する)。
            if (IsJobBlockingUI || (_maskView != null && _maskView.isPainting))
            {
                _autosaveDue = EditorApplication.timeSinceStartup + 0.5;
                return;
            }
            _autosaveDue = 0;
            SavePersistedSessionForCurrentTexture();
        }
    }
}
