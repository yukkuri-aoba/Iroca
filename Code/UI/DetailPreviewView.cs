// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// 詳細プレビュー（フル解像度クロップ）の生成と表示用テクスチャ管理。
    /// PreviewView の補助として動作し、単独のトップレベル状態管理は持たない。
    /// </summary>
    internal class DetailPreviewView
    {
        // 詳細プレビュー: ズームイン時にレンダリングされるフル解像度クロップ
        [System.NonSerialized] public Texture2D detailPreviewTexture;
        [System.NonSerialized] public Texture2D rawDetailPreviewTexture;
        [System.NonSerialized] public Texture2D detailMaskOverlayTexture;
        [System.NonSerialized] public Texture2D detailDiffTexture;

        // 非同期生成
        [System.NonSerialized] public readonly PreviewJob<DetailPreviewResult> detailJob = new PreviewJob<DetailPreviewResult>();
        [System.NonSerialized] private Color32[] _pendingDetailProcessed;
        [System.NonSerialized] private Color32[] _pendingDetailRaw;
        [System.NonSerialized] private int _pendingDetailW, _pendingDetailH;
        [System.NonSerialized] private int _pendingDetailOriginX, _pendingDetailOriginY;
        [System.NonSerialized] private int _pendingDetailFullW, _pendingDetailFullH;
        [System.NonSerialized] public double lastDetailDirtyTime;
        [System.NonSerialized] public Rect lastPreviewRect;

        public const double DetailDebounceSeconds = 0.3;
        // 詳細モード: プレビュー画像がネイティブ解像度を超えて拡大表示される
        // （= previewZoom がこの値を超える）ときにフル解像度クロップへ切り替える。
        // 旧実装は「ディスプレイ/ソース比 >= 1」を条件にしていたが、ソースが
        // 大きいほど閾値が previewZoom の上限(4x)を超えてしまい、2K超のテクスチャで
        // 詳細プレビューが一切起動しなくなっていた。
        public const float DetailMinZoom = 1.0f;

        // 永続的な詳細クロップ原点（詳細プレビュー適用時に設定、レンダラーで読み取られます）
        [System.NonSerialized] public int detailOriginX, detailOriginY;

        // Diff テクスチャ生成（バックグラウンド）
        [System.NonSerialized] private readonly PreviewJob<Color32[]> _diffJob = new PreviewJob<Color32[]>();
        [System.NonSerialized] private Color32[] _pendingDetailDiffPixels;
        [System.NonSerialized] private int _pendingDetailDiffW, _pendingDetailDiffH;

        [System.NonSerialized] private VACCWindow _host;

        public void Initialize(VACCWindow host)
        {
            _host = host;
        }

        public bool HasPendingResult => _pendingDetailProcessed != null;

        public struct DetailPreviewResult
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
        /// スクロールされたプレビューの対応するリージョンと正確に整列する詳細クロップをレンダリングする
        /// スクリーン空間矩形を返します。
        /// </summary>
        public Rect ComputeDetailScreenRect(Rect activePreviewRect, float scale, float previewZoom, Vector2 previewScrollPos, int srcW, int srcH)
        {
            if (detailPreviewTexture == null) return activePreviewRect;

            // ソースピクセルあたりのディスプレイピクセル
            float pxPerSrc = scale * previewZoom;

            float left   = detailOriginX * pxPerSrc - previewScrollPos.x + activePreviewRect.x;
            float top    = detailOriginY * pxPerSrc - previewScrollPos.y + activePreviewRect.y;
            float width  = detailPreviewTexture.width  * pxPerSrc;
            float height = detailPreviewTexture.height * pxPerSrc;

            return new Rect(left, top, width, height);
        }

        /// <summary>
        /// 現在のスクロール位置、ズーム、プレビュースケールからソーステクスチャ座標で見える
        /// クロップリージョンを計算してから、フルソース解像度でそのクロップのみを処理する
        /// バックグラウンドタスクを開始します。
        /// </summary>
        public void GenerateDetailPreviewAsync(int srcW, int srcH, Color32[] srcPixels,
            float scale, float previewZoom, Vector2 previewScrollPos, Rect previewRect)
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !VACCWindow.IsReadable(sourceTexture)) return;
            if (scale >= 1f) return;

            if (previewZoom <= DetailMinZoom) return;

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

            var maskSnap = _host._maskView.BuildSnapshot();

            var session = _host.Session;
            var zonesSnapshot = session.zones
                .Where(z => z.enabled)
                .OrderBy(z => z.layerIndex)
                .Select(z => z.Clone())
                .ToList();
            float feather   = session.edgeFeather;
            int aaCleanup   = session.antiAliasCleanup;
            int hfPasses = session.holeFillPasses;
            int hfMinNeighbors = session.holeFillMinNeighbors;
            float rSatMin = session.relaxedSatMin;
            float rSatRamp = session.relaxedSatRamp;
            bool useDecontam = session.useDecontamination;
            int decontamRadius = session.decontaminationRadius;
            int capX0 = x0, capY0 = y0, capSrcW = srcW, capSrcH = srcH;
            var srcPixelsForTask = srcPixels;

            detailJob.Schedule(
                work: token =>
                {
                    Color32[] rawCrop       = new Color32[cropW * cropH];
                    Color32[] processedCrop = new Color32[cropW * cropH];
                    for (int cy = 0; cy < cropH; cy++)
                    {
                        int sy = capY0 + cy;
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
                    _host.RequestRepaint();
                });
        }

        public void ApplyPendingResult()
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

            detailOriginX = ox;
            detailOriginY = oy;

            TextureSlot.Resize(ref detailPreviewTexture, w, h, FilterMode.Point);
            detailPreviewTexture.SetPixels32(processed);
            detailPreviewTexture.Apply();

            TextureSlot.Resize(ref rawDetailPreviewTexture, w, h, FilterMode.Point);
            rawDetailPreviewTexture.SetPixels32(raw);
            rawDetailPreviewTexture.Apply();

            // Color32[] が手元にあるのでそのままバックグラウンド diff へ。GetPixels32 を再度呼ばない。
            ScheduleDetailDiffTexture(raw, processed, w, h);

            RebuildDetailMaskOverlay(w, h, ox, oy, fw, fh);
        }

        private void ScheduleDetailDiffTexture(Color32[] before, Color32[] after, int w, int h)
        {
            if (before == null || after == null || before.Length != w * h || after.Length != w * h) return;
            int capW = w, capH = h;
            _diffJob.Schedule(
                work: token => BuildDetailDiffPixels(before, after, capW, capH, token),
                apply: result =>
                {
                    _pendingDetailDiffPixels = result;
                    _pendingDetailDiffW = capW;
                    _pendingDetailDiffH = capH;
                    _host.RequestRepaint();
                });
        }

        private static Color32[] BuildDetailDiffPixels(Color32[] a, Color32[] b, int w, int h, CancellationToken token)
        {
            var d = new Color32[w * h];
            var highlight = new Color32(255, 220, 0, 160);
            for (int i = 0; i < d.Length; i++)
            {
                int diff = Mathf.Abs(a[i].r - b[i].r)
                         + Mathf.Abs(a[i].g - b[i].g)
                         + Mathf.Abs(a[i].b - b[i].b);
                if (diff > 10) d[i] = highlight;
                if ((i & 0x7FFF) == 0) token.ThrowIfCancellationRequested();
            }
            return d;
        }

        public void ApplyPendingDiff()
        {
            if (_pendingDetailDiffPixels == null) return;
            var pixels = _pendingDetailDiffPixels;
            int w = _pendingDetailDiffW, h = _pendingDetailDiffH;
            _pendingDetailDiffPixels = null;

            TextureSlot.Resize(ref detailDiffTexture, w, h, FilterMode.Point);
            detailDiffTexture.SetPixels32(pixels);
            detailDiffTexture.Apply();
        }

        private void RebuildDetailMaskOverlay(int cropW, int cropH,
            int originX, int originY, int fullW, int fullH)
        {
            var maskView = _host._maskView;
            var zones = _host.Session.zones;

            // メインのオーバーレイと同じく、編集対象のマスクだけを表示する。
            bool commonIsActive = maskView.activeMaskTarget < 0
                || zones == null || maskView.activeMaskTarget >= zones.Count;

            bool hasCommon = commonIsActive && maskView.exclusionMask != null;

            bool[] activeZoneMask = null;
            Color32 activeZoneColor = default;
            if (!commonIsActive)
            {
                var zone = zones[maskView.activeMaskTarget];
                if (zone != null && !string.IsNullOrEmpty(zone.id)
                    && maskView.zoneMasks.TryGetValue(zone.id, out var zm) && zm != null)
                {
                    activeZoneMask = zm;
                    activeZoneColor = MaskPaintView.OverlayColorForZone(maskView.activeMaskTarget);
                }
            }

            if (!hasCommon && activeZoneMask == null)
            {
                TextureSlot.Release(ref detailMaskOverlayTexture);
                return;
            }

            TextureSlot.Resize(ref detailMaskOverlayTexture, cropW, cropH, FilterMode.Point);

            var overlayPixels = new Color32[cropW * cropH];
            var commonColor = new Color32(255, 60, 60, 80);
            var clear       = new Color32(0, 0, 0, 0);

            int mw = maskView.maskWidth;
            int mh = maskView.maskHeight;
            var common = maskView.exclusionMask;

            for (int i = 0; i < overlayPixels.Length; i++)
            {
                int cx = i % cropW;
                int cy = i / cropW;
                int mx = Mathf.Clamp((originX + cx) * mw / fullW, 0, mw - 1);
                int my = Mathf.Clamp((originY + cy) * mh / fullH, 0, mh - 1);
                int mi = my * mw + mx;

                Color32 px = clear;
                if (hasCommon && common[mi]) px = commonColor;
                if (activeZoneMask != null && activeZoneMask[mi]) px = activeZoneColor;

                overlayPixels[i] = px;
            }

            detailMaskOverlayTexture.SetPixels32(overlayPixels);
            detailMaskOverlayTexture.Apply();
        }

        public void Dispose()
        {
            Suspend();
            detailJob.Dispose();
            _diffJob.Dispose();
        }

        public void Suspend()
        {
            detailJob.Cancel();
            _diffJob.Cancel();
            _pendingDetailProcessed = null;
            _pendingDetailRaw = null;
            _pendingDetailDiffPixels = null;
            lastDetailDirtyTime = 0;
            TextureSlot.Release(ref detailPreviewTexture);
            TextureSlot.Release(ref rawDetailPreviewTexture);
            TextureSlot.Release(ref detailMaskOverlayTexture);
            TextureSlot.Release(ref detailDiffTexture);
        }

        /// <summary>
        /// スクロール/ズーム変更時に呼び、古い詳細プレビュー表示を破棄する。
        /// 詳細クロップは「変更前のスクロール位置で生成」されているため、
        /// 変更後に同じテクスチャを出すと隠していた低解像度プレビューと位置がずれ、
        /// 結果的に低解像度プレビューが端から見えてしまう（fix.md 項目1）。
        /// 次の詳細プレビューが完成するまで表示自体を消すことで一貫性を保つ。
        /// lastDetailDirtyTime は呼び出し側が再設定するので維持する。
        /// </summary>
        public void InvalidateDisplay()
        {
            detailJob.Cancel();
            _diffJob.Cancel();
            _pendingDetailProcessed = null;
            _pendingDetailRaw = null;
            _pendingDetailDiffPixels = null;
            TextureSlot.Release(ref detailPreviewTexture);
            TextureSlot.Release(ref rawDetailPreviewTexture);
            TextureSlot.Release(ref detailMaskOverlayTexture);
            TextureSlot.Release(ref detailDiffTexture);
        }
    }
}
