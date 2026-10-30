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
        // 「元を表示」中の変更前の切り出しの見た目と、それを作った元(変更前の切り出し)・設定。
        [System.NonSerialized] private Texture2D _appearanceDetailRaw;
        [System.NonSerialized] private int _appearanceDetailRawBuilt = -1;
        [System.NonSerialized] private string _appearanceDetailRawFor;
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
            if (detail != null && DetailW > 0 && DetailH > 0
                && (_appearanceDetailBuilt != _detailVersion || _appearanceDetailFor != sig))
            {
                var uv = new Rect(
                    _detailView.detailOriginX / (float)DetailW, _detailView.detailOriginY / (float)DetailH,
                    _detailView.detailCropW / (float)DetailW, _detailView.detailCropH / (float)DetailH);
                _appearance.RenderRegion(report, detail, uv, DetailW, DetailH, ref _appearanceDetail);
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

        /// <summary>
        /// 「元を表示」を押している間に、拡大表示の切り出しに描く変更前の画像(変更後の切り出しと同じ範囲・寸法・
        /// 作業解像度)。作れないとき(切り出しが無い・見た目がまだ作れない)は null で、呼び出し側は全体の変更前を出す。
        /// </summary>
        private Texture ShownDetailRaw()
        {
            if (!_detailView.EnsureDetailRaw()) return null;
            var raw = _detailView.detailRawTexture;
            if (!AppearanceActive) return raw;
            var report = AppearanceReport;
            if (_appearance == null || report == null) return null;
            if (_appearanceDetailRaw == null || _appearanceDetailRawBuilt != _detailView.detailRawVersion
                || _appearanceDetailRawFor != report.signature)
            {
                var uv = new Rect(
                    _detailView.detailOriginX / (float)DetailW, _detailView.detailOriginY / (float)DetailH,
                    _detailView.detailCropW / (float)DetailW, _detailView.detailCropH / (float)DetailH);
                _appearance.RenderRegion(report, raw, uv, DetailW, DetailH, ref _appearanceDetailRaw);
                _appearanceDetailRawBuilt = _detailView.detailRawVersion;
                _appearanceDetailRawFor = report.signature;
            }
            return _appearanceDetailRaw;
        }

        /// <summary>
        /// 差分表示で、拡大表示の切り出しの上の差分を描くのに使う変更前の切り出し(テクスチャの色。変更後の切り出しと
        /// 同じ範囲・寸法)。差分表示の間に作り始めた切り出しなら一緒に届いている。届いていない(差分表示へ切り替えた
        /// 直後など)ときはその場で作る。作れないときは null。
        /// </summary>
        private Texture ShownDetailRawForDiff()
            => _detailView.EnsureDetailRaw() ? _detailView.detailRawTexture : null;

        /// <summary>「元を表示」を離したときに、変更前の切り出しとその見た目を手放す。</summary>
        private void ReleaseDetailRaw()
        {
            _detailView.ReleaseDetailRaw();
            if (_appearanceDetailRaw == null) return;
            TextureSlot.Release(ref _appearanceDetailRaw);
            _appearanceDetailRawBuilt = -1;
            _appearanceDetailRawFor = null;
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
            if (_appearance == null && _appearancePreview == null && _appearanceRaw == null && _appearanceDetail == null
                && _appearanceDetailRaw == null)
                return;
            _appearance?.Dispose();
            _appearance = null;
            TextureSlot.Release(ref _appearancePreview);
            TextureSlot.Release(ref _appearanceRaw);
            TextureSlot.Release(ref _appearanceDetail);
            TextureSlot.Release(ref _appearanceDetailRaw);
            _appearanceDetailRawBuilt = -1;
            _appearanceDetailRawFor = null;
            _appearancePreviewBuilt = _appearanceDetailBuilt = -1;
            _appearancePreviewFor = _appearanceDetailFor = null;
        }
    }
}
