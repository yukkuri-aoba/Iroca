// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 「アバターの色」表示。マテリアルの色の設定(メインカラー・色調補正・重ね描きなど)を掛けた色で
    /// プレビューを描く(<see cref="AppearanceRenderer"/>。画像は <see cref="PreviewRenderer"/> が作って持つ)。
    /// 色替えの計算・スポイト・マスクはテクスチャの色のままで、描く画像だけを差し替える。表示だけで保存される内容は
    /// 変わらないので、既定でオン。どのマテリアルの設定を使うかはホスト(<see cref="IrocaWindow.AppearanceReport"/>)が決める。
    /// </summary>
    internal partial class PreviewView
    {
        public bool appearanceMode = true;

        // Layout のときに決めた「アバターの色」ボタンを出すか(同じフレームの Repaint で並ぶ要素をそろえる)。
        [System.NonSerialized] private bool _appearanceAvailableAtLayout;

        private MaterialColorSettings.Report AppearanceReport => _host != null ? _host.AppearanceReport : null;

        /// <summary>見た目の表示を使っているか(ボタンが出ていて、オンになっている)。</summary>
        private bool AppearanceActive => appearanceMode && _appearanceAvailableAtLayout;

        /// <summary>
        /// 見た目の画像を必要なら作り直させる。<see cref="Draw"/> が表示用テクスチャを反映した後に毎回呼ぶ。
        /// GUILayout は呼ばない。
        /// </summary>
        private void TickAppearance()
        {
            var report = AppearanceReport;
            if (Event.current.type == EventType.Layout)
                _appearanceAvailableAtLayout = AppearanceRenderer.KindOf(report) != AppearanceRenderer.Kind.None;
            _renderer.TickAppearance(report, AppearanceActive);
        }

        /// <summary>操作行の「アバターの色」ボタン。マテリアルに色の設定があるときだけ出す。</summary>
        private void DrawAppearanceToggle()
        {
            if (!_appearanceAvailableAtLayout) return;
            bool on = GUILayout.Toggle(appearanceMode,
                TempContent.Of(Localization.AppearanceMode, Localization.AppearanceModeTooltip),
                EditorStyles.miniButton, GUILayout.ExpandWidth(false));
            if (on == appearanceMode) return;
            appearanceMode = on;
            _host.RequestRepaint();
        }
    }
}
