// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>拡大表示を画像のどこに重ねるか(<see cref="DetailPreviewView.ComputeDetailScreenRect"/> に渡すもの)。</summary>
    internal readonly struct DetailPlacement
    {
        // 拡大表示を重ねてよいか(倍率が足りている・ドラッグの追従で隠していない)。
        public readonly bool Allowed;
        // 拡大表示の土台 1 画素あたりのプレビューテクスチャの画素数と、画面上の表示倍率。
        public readonly float Scale, Zoom;
        public readonly Vector2 Scroll;
        // 拡大表示の土台の寸法。
        public readonly int W, H;

        public DetailPlacement(bool allowed, float scale, float zoom, Vector2 scroll, int w, int h)
        {
            Allowed = allowed;
            Scale = scale;
            Zoom = zoom;
            Scroll = scroll;
            W = w;
            H = h;
        }
    }

    /// <summary>
    /// プレビューの表示(0.4.0 の計画の 4-4)。計算(<see cref="PreviewPipeline"/>)の結果を上げたテクスチャ(全体の
    /// 変更後・変更前)、拡大表示(<see cref="DetailPreviewView"/>)、差分(<see cref="PreviewDiffDrawer"/>)、
    /// 「アバターの色」の見た目(<see cref="AppearanceRenderer"/>)を持ち、画像の矩形へ描く。どこに描くか(レイアウト・
    /// ズーム・スクロール)と入力は <c>PreviewView</c> が決める。メインスレッド専用。
    /// </summary>
    internal sealed class PreviewRenderer
    {
        // 全体の変更後・変更前(表示寸法)。
        public Texture2D Preview;
        public Texture2D Raw;
        public readonly DetailPreviewView Detail = new DetailPreviewView();
        // 差分表示は描いている変更前・変更後のテクスチャから GPU で描く(差分の画像は持たない)。
        private PreviewDiffDrawer _diff;
        private IrocaWindow _host;

        // 「アバターの色」の見た目。マテリアルの色の設定(メインカラー・色調補正・重ね描きなど)を掛けた色で描く画像。
        private AppearanceRenderer _appearance;
        private Texture2D _appearancePreview, _appearanceRaw, _appearanceDetail;
        // 「元を表示」中の変更前の切り出しの見た目と、それを作った元(変更前の切り出し)・設定。
        private Texture2D _appearanceDetailRaw;
        private int _appearanceDetailRawBuilt = -1;
        private string _appearanceDetailRawFor;
        // 表示用テクスチャ(全体・切り出し)を差し替えるたびに進める番号と、見た目を作ったときの番号・設定。
        private int _previewVersion, _detailVersion;
        private int _appearancePreviewBuilt = -1, _appearanceDetailBuilt = -1;
        private string _appearancePreviewFor, _appearanceDetailFor;
        // このフレームで見た目を使うか(ボタンが出ていてオン)と、使うマテリアルの設定(TickAppearance で受け取る)。
        private bool _appearanceActive;
        private MaterialColorSettings.Report _report;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
            Detail.Initialize(host);
            _diff ??= new PreviewDiffDrawer();
        }

        private PreviewPipeline Pipeline => _host?.Preview?.Pipeline;
        private int DetailW => Pipeline?.DetailW ?? 0;
        private int DetailH => Pipeline?.DetailH ?? 0;

        /// <summary>
        /// Mono のヒープの外の画素をテクスチャへ上げる(寸法が変わったら作り直す)。RGBA32 でミップなしなので、
        /// Color32 の並びがそのままテクスチャのデータになる(管理配列へ写さない。SetPixels32 と同じ中身になる)。
        /// </summary>
        internal static unsafe void UploadPixels(ref Texture2D tex, NativePixels pixels)
        {
            TextureSlot.Resize(ref tex, pixels.Width, pixels.Height);
            tex.LoadRawTextureData((System.IntPtr)pixels.Ptr, pixels.Length * sizeof(Color32));
            tex.Apply();
        }

        /// <summary>計算の側から受け取った結果(表示寸法の変更後・変更前)をテクスチャへ上げ、画素の参照を返す。</summary>
        internal void UploadFrame(PreviewFrame frame)
        {
            UploadPixels(ref Preview, frame.Processed);
            UploadPixels(ref Raw, frame.Raw);
            frame.Release();
            _previewVersion++;
        }

        /// <summary>届いている拡大表示の切り出しを上げる。上げたら true(表示はマスクの重ね表示を同じ範囲で作り直す)。</summary>
        internal bool ApplyPendingDetail()
        {
            if (!Detail.HasPendingResult) return false;
            Detail.ApplyPendingResult();
            _detailVersion++;
            return true;
        }

        // ── 拡大したときの補間 ─────────────────────────────────────

        /// <summary>
        /// 表示用テクスチャの補間を今の設定と倍率に合わせる(描く前に毎フレーム。値が同じなら何もしない)。
        /// 全体表示の画像は、元の画像をそのまま載せている(scale>=1)ときだけ、拡大中に「ドット」なら Point にする
        /// (縮小した画像の画素を四角く見せても、元の画素ではないので意味が無い)。
        /// </summary>
        internal void SyncMagnifyFilters(float scale, float effZoom)
        {
            var whole = scale >= 1f && effZoom > 1f && !PreviewView.SmoothMagnify ? FilterMode.Point : FilterMode.Bilinear;
            SetFilter(Preview, whole);
            SetFilter(Raw, whole);
            SetFilter(_appearancePreview, whole);
            SetFilter(_appearanceRaw, whole);
            var dt = Detail.detailPreviewTexture;
            if (dt == null || Detail.detailCropW <= 0) return;
            var detail = DetailPreviewView.DetailFilterMode(dt.width, Detail.detailCropW);
            SetFilter(dt, detail);
            SetFilter(Detail.detailRawTexture, detail);
            SetFilter(_appearanceDetail, detail);
            SetFilter(_appearanceDetailRaw, detail);
        }

        private static void SetFilter(Texture tex, FilterMode mode)
        {
            if (tex != null && tex.filterMode != mode) tex.filterMode = mode;
        }

        // ── 「アバターの色」の見た目 ───────────────────────────────────

        /// <summary>
        /// 見た目の画像を必要なら作り直す。表示用テクスチャを反映した後に毎回呼ぶ。active はこのフレームで見た目を使うか
        /// (ボタンが出ていてオン)、report は使うマテリアルの設定。
        /// </summary>
        internal void TickAppearance(MaterialColorSettings.Report report, bool active)
        {
            _appearanceActive = active;
            _report = report;
            if (!active)
            {
                ReleaseAppearance();
                return;
            }
            _appearance ??= new AppearanceRenderer();
            string sig = report.signature;

            if (Preview != null && (_appearancePreviewBuilt != _previewVersion || _appearancePreviewFor != sig))
            {
                _appearance.Render(report, Preview, ref _appearancePreview);
                if (Raw != null) _appearance.Render(report, Raw, ref _appearanceRaw);
                _appearancePreviewBuilt = _previewVersion;
                _appearancePreviewFor = sig;
            }

            var detail = Detail.detailPreviewTexture;
            // 拡大表示をやめたら、切り出し用の作業 RenderTexture(テクスチャ全体の寸法になり得る)は持たない。
            if (detail == null) _appearance.ReleaseScratch();
            if (detail != null && DetailW > 0 && DetailH > 0
                && (_appearanceDetailBuilt != _detailVersion || _appearanceDetailFor != sig))
            {
                _appearance.RenderRegion(report, detail, DetailUv(), DetailW, DetailH, ref _appearanceDetail);
                _appearanceDetailBuilt = _detailVersion;
                _appearanceDetailFor = sig;
            }
        }

        // 拡大表示の切り出しが覆う範囲(土台の UV)。
        private Rect DetailUv() => new Rect(
            Detail.detailOriginX / (float)DetailW, Detail.detailOriginY / (float)DetailH,
            Detail.detailCropW / (float)DetailW, Detail.detailCropH / (float)DetailH);

        /// <summary>全体表示に描く画像(見た目の表示中はそれ、まだ無ければテクスチャの色)。</summary>
        internal Texture ShownPreview => _appearanceActive && _appearancePreview != null ? _appearancePreview : Preview;

        /// <summary>変更前として描く画像。</summary>
        internal Texture ShownRaw => _appearanceActive && _appearanceRaw != null ? _appearanceRaw : Raw;

        /// <summary>
        /// 拡大表示の切り出しに描く画像。見た目の表示中に今の切り出しの見た目がまだ無ければ null
        /// (テクスチャの色の切り出しを見た目の全体表示の上に重ねると、そこだけ色が違って見える)。
        /// </summary>
        private Texture ShownDetail
        {
            get
            {
                var detail = Detail.detailPreviewTexture;
                if (detail == null || !_appearanceActive) return detail;
                return _appearanceDetail != null && _appearanceDetailBuilt == _detailVersion ? _appearanceDetail : null;
            }
        }

        /// <summary>
        /// 「元を表示」を押している間に、拡大表示の切り出しに描く変更前の画像(変更後の切り出しと同じ範囲・寸法)。
        /// 作れないとき(切り出しが無い・見た目がまだ作れない)は null で、呼び出し側は全体の変更前を出す。
        /// </summary>
        private Texture ShownDetailRaw()
        {
            if (!Detail.EnsureDetailRaw()) return null;
            var raw = Detail.detailRawTexture;
            if (!_appearanceActive) return raw;
            var report = _report;
            if (_appearance == null || report == null) return null;
            if (_appearanceDetailRaw == null || _appearanceDetailRawBuilt != Detail.detailRawVersion
                || _appearanceDetailRawFor != report.signature)
            {
                _appearance.RenderRegion(report, raw, DetailUv(), DetailW, DetailH, ref _appearanceDetailRaw);
                _appearanceDetailRawBuilt = Detail.detailRawVersion;
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
            => Detail.EnsureDetailRaw() ? Detail.detailRawTexture : null;

        /// <summary>「元を表示」を離したときに、変更前の切り出しとその見た目を手放す。</summary>
        private void ReleaseDetailRaw()
        {
            Detail.ReleaseDetailRaw();
            if (_appearanceDetailRaw == null) return;
            TextureSlot.Release(ref _appearanceDetailRaw);
            _appearanceDetailRawBuilt = -1;
            _appearanceDetailRawFor = null;
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

        // ── 描く ─────────────────────────────────────────────

        /// <summary>
        /// 前後比較: 変更前(rawRect)と変更後(afterRect)を並べて描く。拡大中は、変更後の切り出しと同じ範囲の変更前の
        /// 切り出し(拡大表示の土台から作る)を変更前の画像に重ねる。片方しか作れないときは両方とも全体表示のまま
        /// (解像度をそろえて比べる)。マスクの重ね表示は変更後に描く。
        /// </summary>
        internal void DrawComparison(Rect rawRect, Rect afterRect, in DetailPlacement d, MaskPaintView mv)
        {
            EditorGUI.DrawPreviewTexture(rawRect, ShownRaw);
            EditorGUI.DrawPreviewTexture(afterRect, ShownPreview);

            var shownDetail = ShownDetail;
            Texture beforeDetail = d.Allowed && shownDetail != null ? ShownDetailRaw() : null;
            Rect afterDetailRect = default;
            if (beforeDetail != null)
            {
                GUI.DrawTexture(Detail.ComputeDetailScreenRect(rawRect, d.Scale, d.Zoom, d.Scroll, d.W, d.H),
                    beforeDetail, ScaleMode.StretchToFill, false);
                afterDetailRect = Detail.ComputeDetailScreenRect(afterRect, d.Scale, d.Zoom, d.Scroll, d.W, d.H);
                GUI.DrawTexture(afterDetailRect, shownDetail, ScaleMode.StretchToFill, false);
            }
            if (beforeDetail != null && DetailOverlayMatches(mv, d))
                DrawMaskOverlaysWithDetail(afterRect, afterDetailRect, mv);
            else
                DrawMaskOverlays(afterRect, mv);
        }

        /// <summary>
        /// 1 枚の表示: 変更後(「元を表示」を押している間は変更前)を描き、拡大中は切り出しを重ねる。差分表示ではその上に
        /// 差分を、それ以外ではマスクの重ね表示を描く。
        /// </summary>
        internal void DrawSingle(Rect rect, bool peekOriginal, bool diffMode, in DetailPlacement d, MaskPaintView mv)
        {
            // 「元を表示」で押下中は、変更後の切り出しと同じ範囲・寸法の変更前の切り出しを、拡大表示の土台(取り込み後の
            // 寸法へ縮めたもの。PreviewPipeline.DetailSource)から作って重ねる(ShownDetailRaw)。変更前の
            // 切り出しは押している間だけ持ち、離したら手放す(拡大中の VRAM が増えるのは押している間だけ)。
            // 作れないとき(切り出しがまだ無い・見た目がまだ作れない)は、全体の変更前だけを出す。
            // 「アバターの色」の表示中は、切り出しの見た目ができるまで切り出しを重ねない(ShownDetail)。
            var shownDetail = ShownDetail;
            bool detailShown = d.Allowed && shownDetail != null;
            Texture peekDetail = detailShown && peekOriginal ? ShownDetailRaw() : null;
            // 差分表示の間は、変更前の切り出しを差分が読むので持っておく。
            if (!peekOriginal && !diffMode) ReleaseDetailRaw();
            if (detailShown && (!peekOriginal || peekDetail != null))
            {
                EditorGUI.DrawPreviewTexture(rect, peekOriginal ? ShownRaw : ShownPreview);

                Rect detailScreenRect = Detail.ComputeDetailScreenRect(rect, d.Scale, d.Zoom, d.Scroll, d.W, d.H);
                GUI.DrawTexture(detailScreenRect, peekOriginal ? peekDetail : shownDetail,
                    ScaleMode.StretchToFill, false);

                // 変更前を見せている間は差分ハイライトを重ねない(下の全体表示と同じ理由)。
                if (!peekOriginal && diffMode)
                {
                    // 切り出しの上は、変更後の切り出しと同じ範囲の変更前の切り出しから差分を描く(描いている 2 枚から
                    // 作るので、切り出しと差分が食い違わない)。切り出しの外側(新しく見えた端)は、下に描いている
                    // 縮小表示の 2 枚から同じ定義で描く。変更前の切り出しが作れないときは全体を縮小表示の 2 枚から描く。
                    // 切り出しの内側には縮小表示の分を重ねない(二重に塗って色が濃くなる)。
                    var detailBefore = ShownDetailRawForDiff();
                    _diff.DrawOutside(rect, detailBefore != null ? detailScreenRect : default, Raw, Preview);
                    if (detailBefore != null)
                        _diff.Draw(detailScreenRect, detailBefore, Detail.detailPreviewTexture, new Rect(0f, 0f, 1f, 1f));
                }
                else
                {
                    // マスクの重ね表示: 詳細の範囲は、詳細と同じ範囲・寸法で描いた等倍のもの
                    // (MaskPaintView.RebuildDetailOverlay)を重ね、その外は全体用を引き伸ばす。等倍のものが
                    // まだ無い・範囲が古いときは全体用だけ(以前と同じ)。ペイント中の直接書き込み(PaintMask)は
                    // 両方へ入るので、塗ったそばから見える。
                    if (DetailOverlayMatches(mv, d))
                        DrawMaskOverlaysWithDetail(rect, detailScreenRect, mv);
                    else
                        DrawMaskOverlays(rect, mv);
                }
            }
            else
            {
                EditorGUI.DrawPreviewTexture(rect, peekOriginal ? ShownRaw : ShownPreview);

                // 変更前を見せている間は差分ハイライトを重ねない（変更前に「変わった場所」を
                // 塗ると、元テクスチャに無い色が乗って見え、確認したい当のものが隠れる）。
                if (!peekOriginal && diffMode)
                    _diff.DrawOutside(rect, default, Raw, Preview);
                else
                    DrawMaskOverlays(rect, mv);
            }
        }

        // 拡大表示用のマスクの重ね表示が、今の切り出しと同じ範囲・寸法か。
        private bool DetailOverlayMatches(MaskPaintView mv, in DetailPlacement d)
        {
            var dt = Detail.detailPreviewTexture;
            return dt != null && mv.DetailOverlayMatches(d.W, d.H, Detail.detailOriginX, Detail.detailOriginY,
                Detail.detailCropW, Detail.detailCropH, dt.width, dt.height);
        }

        /// <summary>
        /// マスクのオーバーレイを、共通 → ゾーンの順にプレビュー画像の矩形へ重ねる
        /// (通常表示・比較モードの変更後・拡大中の詳細クロップの上で共通)。
        /// </summary>
        private static void DrawMaskOverlays(Rect r, MaskPaintView mv)
        {
            if (mv.maskOverlayTexture != null)
                GUI.DrawTexture(r, mv.maskOverlayTexture, ScaleMode.StretchToFill, true);
            if (mv.zoneMaskOverlayTexture != null)
                GUI.DrawTexture(r, mv.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
        }

        /// <summary>
        /// 拡大表示中のマスクの重ね表示: detail(詳細プレビューの画面上の矩形)には等倍のもの、その外には全体用を
        /// 描く(全体用を detail の上にも重ねると、半透明が二重になって濃く見える)。
        /// </summary>
        private static void DrawMaskOverlaysWithDetail(Rect full, Rect detail, MaskPaintView mv)
        {
            DrawOutside(full, detail, mv.maskOverlayTexture);
            DrawOutside(full, detail, mv.zoneMaskOverlayTexture);
            if (mv.detailMaskOverlayTexture != null)
                GUI.DrawTexture(detail, mv.detailMaskOverlayTexture, ScaleMode.StretchToFill, true);
            if (mv.detailZoneOverlayTexture != null)
                GUI.DrawTexture(detail, mv.detailZoneOverlayTexture, ScaleMode.StretchToFill, true);
        }

        /// <summary>tex を full へ引き伸ばして描くときの、hole の外の部分だけを描く(上下左右の帯に分けて、
        /// それぞれ対応するテクスチャ座標で)。</summary>
        private static void DrawOutside(Rect full, Rect hole, Texture tex)
        {
            if (tex == null || full.width <= 0f || full.height <= 0f) return;
            var h = Rect.MinMaxRect(Mathf.Max(full.xMin, hole.xMin), Mathf.Max(full.yMin, hole.yMin),
                                    Mathf.Min(full.xMax, hole.xMax), Mathf.Min(full.yMax, hole.yMax));
            if (h.width <= 0f || h.height <= 0f)
            {
                GUI.DrawTexture(full, tex, ScaleMode.StretchToFill, true);
                return;
            }
            Strip(full, Rect.MinMaxRect(full.xMin, full.yMin, full.xMax, h.yMin), tex);      // 上
            Strip(full, Rect.MinMaxRect(full.xMin, h.yMax, full.xMax, full.yMax), tex);      // 下
            Strip(full, Rect.MinMaxRect(full.xMin, h.yMin, h.xMin, h.yMax), tex);            // 左
            Strip(full, Rect.MinMaxRect(h.xMax, h.yMin, full.xMax, h.yMax), tex);            // 右
        }

        private static void Strip(Rect full, Rect part, Texture tex)
        {
            if (part.width <= 0f || part.height <= 0f) return;
            // テクスチャ座標は下原点(画面の y は下向き)。
            float u0 = (part.xMin - full.xMin) / full.width, u1 = (part.xMax - full.xMin) / full.width;
            float v1 = 1f - (part.yMin - full.yMin) / full.height, v0 = 1f - (part.yMax - full.yMin) / full.height;
            GUI.DrawTextureWithTexCoords(part, tex, new Rect(u0, v0, u1 - u0, v1 - v0), true);
        }

        // ── 寿命 ─────────────────────────────────────────────

        /// <summary>原本から導いた表示(拡大表示・見た目)を捨てる(テクスチャを替えた・原本を読み直した)。</summary>
        internal void OnSourceInvalidated()
        {
            // 旧テクスチャのクロップが新テクスチャ上に重なって見えるのを防ぐ
            // （詳細ジョブのキャンセルと表示テクスチャの解放を含む）。
            Detail.InvalidateDisplay();
            // 見た目の画像も旧テクスチャのもの。
            ReleaseAppearance();
        }

        /// <summary>
        /// テクスチャ・差分のマテリアル・見た目・拡大表示を手放す(閉じる・リロードの前)。差分のマテリアルは
        /// HideAndDontSave なので、リロードで参照を失うと残る。次に描くときに作り直す。
        /// </summary>
        internal void Release()
        {
            Detail.Suspend();
            TextureSlot.Release(ref Preview);
            TextureSlot.Release(ref Raw);
            _diff?.Dispose();
            ReleaseAppearance();
        }

        public void Dispose()
        {
            Release();
            Detail.Dispose();
        }
    }
}
