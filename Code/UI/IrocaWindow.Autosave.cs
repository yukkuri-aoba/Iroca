// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca
{
    /// <summary>
    /// 編集の合図(<see cref="EditingSession.Changed"/>)を受ける所: プレビューの作り直し・マスクの重ね表示・
    /// 自動保存(<see cref="AutosaveSchedule"/>)。
    /// </summary>
    public partial class IrocaWindow
    {
        [System.NonSerialized] private AutosaveSchedule _autosave;

        private AutosaveSchedule Autosave => _autosave ??= new AutosaveSchedule(
            // 書き出し・手動の自動調整の最中や、マスクを塗っている途中は待つ(終わってから 1 回保存する)。
            () => IsJobBlockingUI || (_maskView != null && _maskView.isPainting),
            () => SavePersistedSessionForCurrentTexture());

        // OnEnable の初めで呼ぶ(有効化の途中の読み込みも編集として受ける。前の作りでは、開いた直後の読み込みも
        // 自動保存の予定を立てていた)。二重に購読しないよう、一度外してから付ける。
        private void SubscribeEditChanges()
        {
            Editing.Changed -= OnEditChanged;
            Editing.Changed += OnEditChanged;
            Autosave.Start(Editing);
        }

        // OnDisable で呼ぶ(この後で保存するので、自動保存の予定は消す)。
        private void UnsubscribeEditChanges()
        {
            if (_editing != null) _editing.Changed -= OnEditChanged;
            _autosave?.Stop();
        }

        private void OnEditChanged(EditChange kind)
        {
            // マスクが変わった・まるごと差し替わったなら、重ね表示も作り直す。
            if (kind != EditChange.Content && _maskView != null) _maskView.maskDirty = true;
            MarkPreviewDirty();
        }
    }
}
