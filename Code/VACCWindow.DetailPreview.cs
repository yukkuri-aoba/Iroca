using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // Detail preview: full-resolution crop rendered when zoomed in
        private Texture2D _detailPreviewTexture;
        private Texture2D _rawDetailPreviewTexture;
        private Texture2D _detailMaskOverlayTexture;
        private volatile bool _detailGenerating;
        private volatile int _detailAsyncGeneration;
        private Color32[] _pendingDetailProcessed;
        private Color32[] _pendingDetailRaw;
        private int _pendingDetailW, _pendingDetailH;
        private int _pendingDetailOriginX, _pendingDetailOriginY; // crop origin in source coords
        private int _pendingDetailFullW, _pendingDetailFullH;     // full source dimensions
        private double _lastDetailDirtyTime;
        private Rect _lastPreviewRect;                            // stored between frames
        private const double DetailDebounceSeconds = 0.3;
        // Detail mode: activates when the display-pixel-to-source-pixel ratio > 1
        // i.e. previewZoom * (srcSize / previewSize) ≥ this value
        private const float DetailUpscaleThreshold = 1.0f;

        // Persistent detail crop origin (set when detail preview is applied, read by renderer)
        private int _detailOriginX, _detailOriginY;
        private Texture2D _detailDiffTexture;

        // ───────────────────────── Detail Preview (full-res crop) ─────────────────────────

        /// <summary>
        /// Returns the screen-space rect that the detail crop should be drawn into,
        /// positioned so it aligns exactly with the corresponding region of the scrolled preview.
        /// </summary>
        private Rect ComputeDetailScreenRect(Rect activePreviewRect, float scale, int srcW, int srcH)
        {
            if (_detailPreviewTexture == null) return activePreviewRect;

            // display pixels per source pixel
            float pxPerSrc = scale * previewZoom;

            float left   = _detailOriginX * pxPerSrc - previewScrollPos.x + activePreviewRect.x;
            float top    = _detailOriginY * pxPerSrc - previewScrollPos.y + activePreviewRect.y;
            float width  = _detailPreviewTexture.width  * pxPerSrc;
            float height = _detailPreviewTexture.height * pxPerSrc;

            return new Rect(left, top, width, height);
        }

        /// <summary>
        /// Computes the visible crop region in source texture coordinates from the current
        /// scroll position, zoom and preview scale, then launches a background task that
        /// processes only that crop at full source resolution.
        /// </summary>
        private void GenerateDetailPreviewAsync(int srcW, int srcH, Color32[] srcPixels,
            float scale, Rect previewRect)
        {
            if (sourceTexture == null || !IsReadable(sourceTexture)) return;
            if (scale >= 1f) return; // source already fits in preview — no upscaling benefit

            // previewZoom * scale = display pixels per source pixel.
            // If < 1, the preview is still downsampled even after zoom → no detail gain yet.
            float displayPxPerSrcPx = previewZoom * scale;
            if (displayPxPerSrcPx < DetailUpscaleThreshold) return;

            // Compute visible region in source pixel coordinates (Y-flipped: texture bottom=0)
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

            _detailGenerating = true;
            int myGen = _detailAsyncGeneration;

            bool[] maskSnapshot = exclusionMask != null ? (bool[])exclusionMask.Clone() : null;
            int mW = maskWidth, mH = maskHeight;

            var zonesSnapshot = zones
                .Where(z => z.enabled)
                .OrderBy(z => z.layerIndex)
                .Select(CloneZone)
                .ToList();
            float feather   = edgeFeather;
            int aaCleanup   = antiAliasCleanup;
            int capX0 = x0, capY0 = y0, capSrcW = srcW, capSrcH = srcH;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Extract crop from source
                    Color32[] rawCrop       = new Color32[cropW * cropH];
                    Color32[] processedCrop = new Color32[cropW * cropH];
                    for (int cy = 0; cy < cropH; cy++)
                    {
                        int sy = capY0 + cy; // source row (texture bottom=0)
                        for (int cx = 0; cx < cropW; cx++)
                        {
                            int srcIdx = sy * capSrcW + (capX0 + cx);
                            rawCrop[cy * cropW + cx] = srcPixels[srcIdx];
                            processedCrop[cy * cropW + cx] = srcPixels[srcIdx];
                        }
                    }

                    ProcessPixelsArray(processedCrop, cropW, cropH,
                        maskSnapshot, mW, mH, zonesSnapshot, feather, aaCleanup,
                        capX0, capY0, capSrcW, capSrcH);

                    if (myGen != _detailAsyncGeneration || _asyncCancelled) return;

                    _pendingDetailRaw       = rawCrop;
                    _pendingDetailProcessed = processedCrop;
                    _pendingDetailW         = cropW;
                    _pendingDetailH         = cropH;
                    _pendingDetailOriginX   = capX0;
                    _pendingDetailOriginY   = capY0;
                    _pendingDetailFullW     = capSrcW;
                    _pendingDetailFullH     = capSrcH;
                }
                finally
                {
                    _detailGenerating = false;
                    UnityEditor.EditorApplication.delayCall += Repaint;
                }
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

            // Persist the crop origin so the renderer can position the texture correctly
            _detailOriginX = ox;
            _detailOriginY = oy;

            if (_detailPreviewTexture == null || _detailPreviewTexture.width != w || _detailPreviewTexture.height != h)
            {
                if (_detailPreviewTexture != null) DestroyImmediate(_detailPreviewTexture);
                _detailPreviewTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                _detailPreviewTexture.filterMode = FilterMode.Point;
            }
            _detailPreviewTexture.SetPixels32(processed);
            _detailPreviewTexture.Apply();

            if (_rawDetailPreviewTexture == null || _rawDetailPreviewTexture.width != w || _rawDetailPreviewTexture.height != h)
            {
                if (_rawDetailPreviewTexture != null) DestroyImmediate(_rawDetailPreviewTexture);
                _rawDetailPreviewTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                _rawDetailPreviewTexture.filterMode = FilterMode.Point;
            }
            _rawDetailPreviewTexture.SetPixels32(raw);
            _rawDetailPreviewTexture.Apply();

            // Build diff overlay for detail view
            BuildDetailDiffTexture(_rawDetailPreviewTexture, _detailPreviewTexture, w, h);

            RebuildDetailMaskOverlay(w, h, ox, oy, fw, fh);
        }

        private void BuildDetailDiffTexture(Texture2D before, Texture2D after, int w, int h)
        {
            if (before == null || after == null) return;
            if (_detailDiffTexture == null || _detailDiffTexture.width != w || _detailDiffTexture.height != h)
            {
                if (_detailDiffTexture != null) DestroyImmediate(_detailDiffTexture);
                _detailDiffTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                _detailDiffTexture.filterMode = FilterMode.Point;
            }

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
            if (exclusionMask == null)
            {
                if (_detailMaskOverlayTexture != null)
                {
                    DestroyImmediate(_detailMaskOverlayTexture);
                    _detailMaskOverlayTexture = null;
                }
                return;
            }

            if (_detailMaskOverlayTexture == null ||
                _detailMaskOverlayTexture.width  != cropW ||
                _detailMaskOverlayTexture.height != cropH)
            {
                if (_detailMaskOverlayTexture != null) DestroyImmediate(_detailMaskOverlayTexture);
                _detailMaskOverlayTexture = new Texture2D(cropW, cropH, TextureFormat.RGBA32, false);
                _detailMaskOverlayTexture.filterMode = FilterMode.Point;
            }

            var overlayPixels = new Color32[cropW * cropH];
            var excluded = new Color32(255, 60, 60, 80);
            var clear    = new Color32(0, 0, 0, 0);

            for (int i = 0; i < overlayPixels.Length; i++)
            {
                int cx = i % cropW;
                int cy = i / cropW;
                int mx = Mathf.Clamp((originX + cx) * maskWidth  / fullW, 0, maskWidth  - 1);
                int my = Mathf.Clamp((originY + cy) * maskHeight / fullH, 0, maskHeight - 1);
                bool ex = exclusionMask != null && exclusionMask[my * maskWidth + mx];
                overlayPixels[i] = ex ? excluded : clear;
            }

            _detailMaskOverlayTexture.SetPixels32(overlayPixels);
            _detailMaskOverlayTexture.Apply();
        }
    }
}
