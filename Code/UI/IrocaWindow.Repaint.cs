// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca {
    // 毎フレーム描き直さずに済ませるための、再描画の予約とジョブの進み具合の見張り。
    //
    // 以前は、書き出し・自動調整・プレビューの計算・拡大表示の切り出しが走っている間、描くたびに次の描き直しを頼んで
    // いた(ウィンドウ全体と切り出したプレビューを、エディタの更新のたびに描き直し続ける)。進捗バーの値は数段階しか
    // 変わらず、計算の終わりは apply が描き直しを頼むので、見張りは EditorApplication.update で軽く行い、描き直すのは
    // 見た目が変わるときだけにする。
    public partial class IrocaWindow {
        // 予約した描き直しの時刻(EditorApplication.timeSinceStartup。0 = 予約なし)。
        [System.NonSerialized] private double _repaintDueAt;
        // ジョブの帯(進捗バーとキャンセル)を最後に描いたときの、ジョブ中か・書き出しの進み(%。-1 = 書き出していない)。
        [System.NonSerialized] private bool _jobShownBlocking;
        [System.NonSerialized] private int _exportShownPct = -1;

        /// <summary>
        /// seconds 秒後に描き直す(0 以下なら次の更新で)。続けて呼んだときは早い方の時刻で 1 回だけ描く
        /// (<see cref="IPreviewPipelineHost.RequestRepaintAfter"/>)。
        /// </summary>
        internal void RequestRepaintAfter(double seconds) {
            double at = EditorApplication.timeSinceStartup + System.Math.Max(0.0, seconds);
            if (_repaintDueAt <= 0 || at < _repaintDueAt) {
                _repaintDueAt = at;
            }
        }

        void IPreviewPipelineHost.RequestRepaintAfter(double seconds) => RequestRepaintAfter(seconds);

        /// <summary>
        /// プレビューの中だけが変わったときの描き直し(計算の見回り・拡大表示の切り出しやマスクの重ね表示が届いた・
        /// 枠の実測の追い描き・押している間の「元を表示」)。プレビューを描いている窓だけを描き、本体の状態の版は
        /// 進めない(マスク編集の窓は描き直さない)。<see cref="RequestRepaint"/> は 1 回で本体・切り出したプレビュー・
        /// マスク編集の窓(版の番号を見て描く)をすべて描き直す。
        /// </summary>
        internal void RequestPreviewRepaint() {
            if (IrocaPreviewWindow.IsOpen) {
                // 切り出している間、本体はプレビューを描かない。ただし切り出した窓が裏のタブで描かれない間は、
                // 本体が計算だけを進める(PreviewView.TickWithoutDrawing)ので本体を描く。
                IrocaPreviewWindow.RepaintIfOpen();
                if (!IrocaPreviewWindow.IsShown) {
                    Repaint();
                }
            } else {
                Repaint();
            }
        }

        private void InstallRepaintWatch() {
            _repaintDueAt = 0;
            _jobShownBlocking = false;
            _exportShownPct = -1;
            EditorApplication.update -= TickRepaintWatch;
            EditorApplication.update += TickRepaintWatch;
        }

        private void UninstallRepaintWatch() {
            EditorApplication.update -= TickRepaintWatch;
            _repaintDueAt = 0;
        }

        // EditorApplication.update から毎回呼ばれる。比べるだけで、描き直すのは変わったときと予約の時刻だけ。
        private void TickRepaintWatch() {
            // 破棄済みウィンドウ(fake-null)は自己解除する。
            if (this == null) {
                EditorApplication.update -= TickRepaintWatch;
                return;
            }
            // ジョブの始まり・終わり(完了・取り消し・失敗・破棄のどの道でも)と、書き出しの進捗バーの値の変化。
            // 自動調整の進みは TickAutoTune が見ている。
            bool blocking = IsJobBlockingUI;
            int exportPct = _exportView != null && _exportView.IsExporting ? _exportView.ProgressPercent : -1;
            if (blocking != _jobShownBlocking || exportPct != _exportShownPct) {
                _jobShownBlocking = blocking;
                _exportShownPct = exportPct;
                Repaint();
            }
            // 予約はプレビューの計算・拡大表示の見回りと待ちの明けなので、プレビューを描いている窓だけを描く。
            if (_repaintDueAt > 0 && EditorApplication.timeSinceStartup >= _repaintDueAt) {
                _repaintDueAt = 0;
                RequestPreviewRepaint();
            }
        }
    }
}
