using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // 詳細プレビュー: ズームイン時にレンダリングされるフル解像度クロップ
        private Texture2D _detailPreviewTexture;
        private Texture2D _rawDetailPreviewTexture;
        private Texture2D _detailMaskOverlayTexture;
        // 非同期生成: 世代管理・キャンセル・メインスレッド復帰は PreviewJob<T> 内部に隠蔽
        private readonly PreviewJob<DetailPreviewResult> _detailJob = new PreviewJob<DetailPreviewResult>();
        private Color32[] _pendingDetailProcessed;
        private Color32[] _pendingDetailRaw;
        private int _pendingDetailW, _pendingDetailH;
        private int _pendingDetailOriginX, _pendingDetailOriginY; // ソース座標でのクロップ原点
        private int _pendingDetailFullW, _pendingDetailFullH;     // フル解像度ソース寸法
        private double _lastDetailDirtyTime;
        private Rect _lastPreviewRect;                            // フレーム間で保存されます
        private const double DetailDebounceSeconds = 0.3;
        // 詳細モード: ディスプレイピクセル数/ソースピクセル数比 > 1 時にアクティベート
        // つまり previewZoom * (srcSize / previewSize) ≥ この値
        private const float DetailUpscaleThreshold = 1.0f;

        // 永続的な詳細クロップ原点（詳細プレビュー適用時に設定、レンダラーで読み取られます）
        private int _detailOriginX, _detailOriginY;
        private Texture2D _detailDiffTexture;

        // ─────────────────────── 詳細プレビュー（フル解像度クロップ） ─────────────────────────

        /// <summary>
        /// スクロールされたプレビューの対応するリージョンと正確に整列する詳細クロップをレンダリングする
        /// スクリーン空間矩形を返します。
        /// </summary>
        private Rect ComputeDetailScreenRect(Rect activePreviewRect, float scale, int srcW, int srcH)
        {
            if (_detailPreviewTexture == null) return activePreviewRect;

            // ソースピクセルあたりのディスプレイピクセル
            float pxPerSrc = scale * previewZoom;

            float left   = _detailOriginX * pxPerSrc - previewScrollPos.x + activePreviewRect.x;
            float top    = _detailOriginY * pxPerSrc - previewScrollPos.y + activePreviewRect.y;
            float width  = _detailPreviewTexture.width  * pxPerSrc;
            float height = _detailPreviewTexture.height * pxPerSrc;

            return new Rect(left, top, width, height);
        }

        private struct DetailPreviewResult
        {
            public Color32[] Raw;
            public Color32[] Processed;
            public int CropW;
            public int CropH;
            public int OriginX;
            public int OriginY;
            public int FullW;
            public int FullH;
        }

        /// <summary>
        /// 現在のスクロール位置、ズーム、プレビュースケールからソーステクスチャ座標で見える
        /// クロップリージョンを計算してから、フルソース解像度でそのクロップのみを処理する
        /// バックグラウンドタスクを開始します。
        /// </summary>
        private void GenerateDetailPreviewAsync(int srcW, int srcH, Color32[] srcPixels,
            float scale, Rect previewRect)
        {
            if (sourceTexture == null || !IsReadable(sourceTexture)) return;
            if (scale >= 1f) return; // ソースはすでにプレビューに適合 — アップスケーリング利点なし

            // previewZoom * scale = ソースピクセルあたりのディスプレイピクセル。
            // < 1 の場合、プレビューはズーム後も縮小 → まだ詳細改善なし。
            float displayPxPerSrcPx = previewZoom * scale;
            if (displayPxPerSrcPx < DetailUpscaleThreshold) return;

            // ソースピクセル座標で見える領域を計算（Y反転: テクスチャ底部=0）
            float invZoomScale = 1f / (previewZoom * scale);
            int x0 = Mathf.FloorToInt(previewScrollPos.x * invZoomScale);
            int y0 = Mathf.FloorToInt(previewScrollPos.y * invZoomScale);
            int x1 = Mathf.CeilToInt((previewScrollPos.x + previewRect.width)  * invZoomScale);
            int y1 = Mathf.CeilToInt((previewScrollPos.y + previewRect.height) * invZoomScale);

            x0 = Mathf.Clamp(x0, 0, srcW);
            y0 = Mathf.Clamp(y0, 0, srcH);
            x1 = Mathf.Clamp(x1, 0, srcW);
            y1 = Mathf.Clamp(y1, 0, srcH);

            int cropW = x1 - x0;
            int cropH = y1 - y0;
            if (cropW <= 0 || cropH <= 0) return;

            var maskSnap = BuildMaskSnapshot();

            var zonesSnapshot = zones
                .Where(z => z.enabled)
                .OrderBy(z => z.layerIndex)
                .Select(CloneZone)
                .ToList();
            float feather   = edgeFeather;
            int aaCleanup   = antiAliasCleanup;
            int hfPasses = holeFillPasses;
            int hfMinNeighbors = holeFillMinNeighbors;
            float rSatMin = relaxedSatMin;
            float rSatRamp = relaxedSatRamp;
            bool useDecontam = useDecontamination;
            int decontamRadius = decontaminationRadius;
            int capX0 = x0, capY0 = y0, capSrcW = srcW, capSrcH = srcH;
            var srcPixelsForTask = srcPixels;

            _detailJob.Schedule(
                work: token =>
                {
                    // ソースからクロップを抽出
                    Color32[] rawCrop       = new Color32[cropW * cropH];
                    Color32[] processedCrop = new Color32[cropW * cropH];
                    for (int cy = 0; cy < cropH; cy++)
                    {
                        int sy = capY0 + cy; // ソース行（テクスチャ底部=0）
                        for (int cx = 0; cx < cropW; cx++)
                        {
                            int srcIdx = sy * capSrcW + (capX0 + cx);
                            rawCrop[cy * cropW + cx] = srcPixelsForTask[srcIdx];
                            processedCrop[cy * cropW + cx] = srcPixelsForTask[srcIdx];
                        }
                    }

                    PixelProcessor.ProcessPixelsArray(processedCrop, cropW, cropH,
                        maskSnap, zonesSnapshot, feather, aaCleanup,
                        hfPasses, hfMinNeighbors, rSatMin, rSatRamp,
                        capX0, capY0, capSrcW, capSrcH, token,
                        useDecontam, decontamRadius);

                    return new DetailPreviewResult
                    {
                        Raw = rawCrop,
                        Processed = processedCrop,
                        CropW = cropW,
                        CropH = cropH,
                        OriginX = capX0,
                        OriginY = capY0,
                        FullW = capSrcW,
                        FullH = capSrcH,
                    };
                },
                apply: result =>
                {
                    _pendingDetailRaw       = result.Raw;
                    _pendingDetailProcessed = result.Processed;
                    _pendingDetailW         = result.CropW;
                    _pendingDetailH         = result.CropH;
                    _pendingDetailOriginX   = result.OriginX;
                    _pendingDetailOriginY   = result.OriginY;
                    _pendingDetailFullW     = result.FullW;
                    _pendingDetailFullH     = result.FullH;
                    Repaint();
                });
        }

        private void ApplyPendingDetailPreview()
        {
            var processed = _pendingDetailProcessed;
            var raw       = _pendingDetailRaw;
            int w  = _pendingDetailW;
            int h  = _pendingDetailH;
            int ox = _pendingDetailOriginX;
            int oy = _pendingDetailOriginY;
            int fw = _pendingDetailFullW;
            int fh = _pendingDetailFullH;
            _pendingDetailProcessed = null;
            _pendingDetailRaw       = null;

            if (processed == null || raw == null) return;

            // crop origin を永続化してレンダラーがテクスチャを正しく配置できるようにします
            _detailOriginX = ox;
            _detailOriginY = oy;

            TextureSlot.Resize(ref _detailPreviewTexture, w, h, FilterMode.Point);
            _detailPreviewTexture.SetPixels32(processed);
            _detailPreviewTexture.Apply();

            TextureSlot.Resize(ref _rawDetailPreviewTexture, w, h, FilterMode.Point);
            _rawDetailPreviewTexture.SetPixels32(raw);
            _rawDetailPreviewTexture.Apply();

            // 詳細ビュー用の差分オーバーレイを構築
            BuildDetailDiffTexture(_rawDetailPreviewTexture, _detailPreviewTexture, w, h);

            RebuildDetailMaskOverlay(w, h, ox, oy, fw, fh);
        }

        private void BuildDetailDiffTexture(Texture2D before, Texture2D after, int w, int h)
        {
            if (before == null || after == null) return;
            TextureSlot.Resize(ref _detailDiffTexture, w, h, FilterMode.Point);

            Color32[] a = before.GetPixels32();
            Color32[] b = after.GetPixels32();
            Color32[] d = new Color32[w * h];
            var highlight = new Color32(255, 220, 0, 160);
            var clear     = new Color32(0, 0, 0, 0);
            for (int i = 0; i < d.Length; i++)
            {
                int diff = Mathf.Abs(a[i].r - b[i].r)
                         + Mathf.Abs(a[i].g - b[i].g)
                         + Mathf.Abs(a[i].b - b[i].b);
                d[i] = diff > 10 ? highlight : clear;
            }

            _detailDiffTexture.SetPixels32(d);
            _detailDiffTexture.Apply();
        }

        private void RebuildDetailMaskOverlay(int cropW, int cropH,
            int originX, int originY, int fullW, int fullH)
        {
            // 表示すべきマスクが無い場合はテクスチャを破棄
            bool hasCommon = exclusionMask != null;
            bool hasAnyZone = false;
            if (zones != null)
            {
                foreach (var z in zones)
                {
                    if (z == null || string.IsNullOrEmpty(z.id)) continue;
                    if (zoneMasks.TryGetValue(z.id, out var zm) && zm != null) { hasAnyZone = true; break; }
                }
            }

            if (!hasCommon && !hasAnyZone)
            {
                TextureSlot.Release(ref _detailMaskOverlayTexture);
                return;
            }

            TextureSlot.Resize(ref _detailMaskOverlayTexture, cropW, cropH, FilterMode.Point);

            var overlayPixels = new Color32[cropW * cropH];
            var commonColor = new Color32(255, 60, 60, 80);
            var clear       = new Color32(0, 0, 0, 0);

            for (int i = 0; i < overlayPixels.Length; i++)
            {
                int cx = i % cropW;
                int cy = i / cropW;
                int mx = Mathf.Clamp((originX + cx) * maskWidth  / fullW, 0, maskWidth  - 1);
                int my = Mathf.Clamp((originY + cy) * maskHeight / fullH, 0, maskHeight - 1);
                int mi = my * maskWidth + mx;

                Color32 px = clear;
                if (hasCommon && exclusionMask[mi]) px = commonColor;

                if (hasAnyZone)
                {
                    for (int zi = 0; zi < zones.Count; zi++)
                    {
                        var zone = zones[zi];
                        if (zone == null || string.IsNullOrEmpty(zone.id)) continue;
                        if (!zoneMasks.TryGetValue(zone.id, out var zm) || zm == null) continue;
                        if (zm[mi]) { px = OverlayColorForZone(zi); break; }
                    }
                }

                overlayPixels[i] = px;
            }

            _detailMaskOverlayTexture.SetPixels32(overlayPixels);
            _detailMaskOverlayTexture.Apply();
        }
    }
}
