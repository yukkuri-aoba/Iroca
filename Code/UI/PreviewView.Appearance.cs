// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 「アバターの色」表示。マテリアルの色の設定(メインカラー・色調補正・重ね描きなど)を掛けた色で
    /// プレビューを描く(<see cref="AppearanceRenderer"/>)。色替えの計算・スポイト・マスクはテクスチャの色の
    /// ままで、描く画像だけを差し替える。表示だけで保存される内容は変わらないので、既定でオン。
    /// どのマテリアルの設定を使うかはホスト(<see cref="IrocaWindow.AppearanceReport"/>)が決める。
    /// </summary>
    internal partial class PreviewView
    {
        public bool appearanceMode = true;

        [System.NonSerialized] private AppearanceRenderer _appearance;
        [System.NonSerialized] private Texture2D _appearancePreview, _appearanceRaw, _appearanceDetail;
        // 表示用テクスチャ(全体・切り出し)を差し替えるたびに進める番号と、見た目を作ったときの番号・設定。
        [System.NonSerialized] private int _previewVersion, _detailVersion;
        [System.NonSerialized] private int _appearancePreviewBuilt = -1, _appearanceDetailBuilt = -1;
        [System.NonSerialized] private string _appearancePreviewFor, _appearanceDetailFor;
        // Layout のときに決めた「アバターの色」ボタンを出すか(同じフレームの Repaint で並ぶ要素をそろえる)。
        [System.NonSerialized] private bool _appearanceAvailableAtLayout;

        private MaterialColorSettings.Report AppearanceReport => _host != null ? _host.AppearanceReport : null;

        /// <summary>見た目の表示を使っているか(ボタンが出ていて、オンになっている)。</summary>
        private bool AppearanceActive => appearanceMode && _appearanceAvailableAtLayout;

        /// <summary>
        /// 見た目の画像を必要なら作り直す。<see cref="Draw"/> が表示用テクスチャを反映した後に毎回呼ぶ。
        /// GUILayout は呼ばない。
        /// </summary>
        private void TickAppearance()
        {
            var report = AppearanceReport;
            if (Event.current.type == EventType.Layout)
                _appearanceAvailableAtLayout = AppearanceRenderer.KindOf(report) != AppearanceRenderer.Kind.None;
            if (!AppearanceActive)
            {
                ReleaseAppearance();
                return;
            }
            _appearance ??= new AppearanceRenderer();
            string sig = report.signature;

            if (previewTexture != null && (_appearancePreviewBuilt != _previewVersion || _appearancePreviewFor != sig))
            {
                _appearance.Render(report, previewTexture, ref _appearancePreview);
                if (rawPreviewTexture != null) _appearance.Render(report, rawPreviewTexture, ref _appearanceRaw);
                _appearancePreviewBuilt = _previewVersion;
                _appearancePreviewFor = sig;
            }

            var detail = _detailView.detailPreviewTexture;
            // 拡大表示をやめたら、切り出し用の作業 RenderTexture(テクスチャ全体の寸法になり得る)は持たない。
            if (detail == null) _appearance.ReleaseScratch();
            if (detail != null && _trueSourceW > 0 && _trueSourceH > 0
                && (_appearanceDetailBuilt != _detailVersion || _appearanceDetailFor != sig))
            {
                var uv = new Rect(
                    _detailView.detailOriginX / (float)_trueSourceW, _detailView.detailOriginY / (float)_trueSourceH,
                    _detailView.detailCropW / (float)_trueSourceW, _detailView.detailCropH / (float)_trueSourceH);
                _appearance.RenderRegion(report, detail, uv, _trueSourceW, _trueSourceH, ref _appearanceDetail);
                _appearanceDetailBuilt = _detailVersion;
                _appearanceDetailFor = sig;
            }
        }

        /// <summary>全体表示に描く画像(見た目の表示中はそれ、まだ無ければテクスチャの色)。</summary>
        private Texture ShownPreview => AppearanceActive && _appearancePreview != null ? _appearancePreview : previewTexture;

        /// <summary>変更前として描く画像。</summary>
        private Texture ShownRaw => AppearanceActive && _appearanceRaw != null ? _appearanceRaw : rawPreviewTexture;

        /// <summary>
        /// 拡大表示の切り出しに描く画像。見た目の表示中に今の切り出しの見た目がまだ無ければ null
        /// (テクスチャの色の切り出しを見た目の全体表示の上に重ねると、そこだけ色が違って見える)。
        /// </summary>
        private Texture ShownDetail
        {
            get
            {
                var detail = _detailView.detailPreviewTexture;
                if (detail == null || !AppearanceActive) return detail;
                return _appearanceDetail != null && _appearanceDetailBuilt == _detailVersion ? _appearanceDetail : null;
            }
        }

        /// <summary>操作行の「アバターの色」ボタン。マテリアルに色の設定があるときだけ出す。</summary>
        private void DrawAppearanceToggle()
        {
            if (!_appearanceAvailableAtLayout) return;
            bool on = GUILayout.Toggle(appearanceMode,
                new GUIContent(Localization.AppearanceMode, Localization.AppearanceModeTooltip),
                EditorStyles.miniButton, GUILayout.ExpandWidth(false));
            if (on == appearanceMode) return;
            appearanceMode = on;
            _host.RequestRepaint();
        }

        /// <summary>見た目の画像と作業用の RenderTexture を手放す(オフにした・テクスチャを替えた・閉じたとき)。</summary>
        private void ReleaseAppearance()
        {
            if (_appearance == null && _appearancePreview == null && _appearanceRaw == null && _appearanceDetail == null)
                return;
            _appearance?.Dispose();
            _appearance = null;
            TextureSlot.Release(ref _appearancePreview);
            TextureSlot.Release(ref _appearanceRaw);
            TextureSlot.Release(ref _appearanceDetail);
            _appearancePreviewBuilt = _appearanceDetailBuilt = -1;
            _appearancePreviewFor = _appearanceDetailFor = null;
        }
    }
}
