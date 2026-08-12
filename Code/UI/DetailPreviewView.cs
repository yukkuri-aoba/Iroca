// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 詳細プレビュー（フル解像度クロップ）の生成と表示用テクスチャ管理。
    /// PreviewView の補助として動作し、単独のトップレベル状態管理は持たない。
    /// </summary>
    internal class DetailPreviewView
    {
        // 詳細プレビュー: ズームイン時にレンダリングされるフル解像度クロップ
        // マスクオーバーレイ専用テクスチャは持たない: ブロック整列ペイント後は等倍用の
        // 低解像度オーバーレイ(1 画素 = マスクブロック一様)を Point 拡大するだけで
        // 情報損失なく表示でき、クロップ再生成までペイントが見えない問題と
        // メインスレッドの全画素ループを両方排除できる(PreviewView.Draw 側で描画)。
        [System.NonSerialized] public Texture2D detailPreviewTexture;
        [System.NonSerialized] public Texture2D detailDiffTexture;

        // 非同期生成
        [System.NonSerialized] public readonly PreviewJob<DetailPreviewResult> detailJob = new PreviewJob<DetailPreviewResult>();
        [System.NonSerialized] private Color32[] _pendingDetailProcessed;
        [System.NonSerialized] private Color32[] _pendingDetailRaw;
        [System.NonSerialized] private int _pendingDetailTexW, _pendingDetailTexH;
        [System.NonSerialized] private int _pendingDetailCropW, _pendingDetailCropH;
        [System.NonSerialized] private int _pendingDetailOriginX, _pendingDetailOriginY;
        [System.NonSerialized] public double lastDetailDirtyTime;
        [System.NonSerialized] public Rect lastPreviewRect;
        // スクロールビューの可視領域サイズ（ディスプレイピクセル）。クロップ範囲を
        // 画像全幅ではなく「実際に見えている範囲」だけに絞るために使う。
        [System.NonSerialized] public float lastViewportW, lastViewportH;

        public const double DetailDebounceSeconds = 0.3;
        // 詳細モード: 表示倍率がこの値を超えたら、低解像度プレビューの引き伸ばしをやめて
        // ソース解像度から作り直したクロップへ切り替える。
        // 旧実装は「ディスプレイ/ソース比 >= 1」を条件にしていたが、ソースが
        // 大きいほど閾値が previewZoom の上限(4x)を超えてしまい、2K超のテクスチャで
        // 詳細プレビューが一切起動しなくなっていた。
        // なお previewZoom > 1 は「ソース画素より大きく表示されている」ことを意味しない
        // (画面倍率は scale * previewZoom で、4K なら 125% でも 1/8.5 の縮小)。クロップは
        // 表示ピクセル数へ面平均してからアップロードする(GenerateDetailPreviewAsync 参照)。
        public const float DetailMinZoom = 1.0f;

        // 永続的な詳細クロップ原点（詳細プレビュー適用時に設定、レンダラーで読み取られます）
        [System.NonSerialized] public int detailOriginX, detailOriginY;
        // クロップが覆うソース画素数。表示矩形の計算はこちらを使う（テクスチャ解像度は
        // 縮小表示のとき表示ピクセル数まで落とすので、両者は一致しない）。
        [System.NonSerialized] public int detailCropW, detailCropH;

        // Diff テクスチャ生成（バックグラウンド）
        [System.NonSerialized] private readonly PreviewJob<Color32[]> _diffJob = new PreviewJob<Color32[]>();
        [System.NonSerialized] private Color32[] _pendingDetailDiffPixels;
        [System.NonSerialized] private int _pendingDetailDiffW, _pendingDetailDiffH;

        [System.NonSerialized] private IrocaWindow _host;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
        }

        public bool HasPendingResult => _pendingDetailProcessed != null;

        public struct DetailPreviewResult
        {
            public Color32[] Raw;
            public Color32[] Processed;
            // テクスチャ解像度（＝表示ピクセル数。拡大表示ではクロップ寸法と同じ）
            public int TexW;
            public int TexH;
            // クロップが覆うソース画素数（表示矩形の計算用）
            public int CropW;
            public int CropH;
            public int OriginX;
            public int OriginY;
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

            // activePreviewRect は ScrollView 内のレイアウト座標（＝コンテンツ座標）で渡される。
            // この空間ではスクロール量はグループ変換側で吸収済みのため、ここで previewScrollPos を
            // 引いてはいけない（マスクペイントやズーム中心合わせも scrollPos を使っていない）。
            // 詳細クロップは元画像のピクセル detailOriginX から始まるので、画像左上
            // (activePreviewRect.x) からの相対位置をそのまま足す。
            float left   = activePreviewRect.x + detailOriginX * pxPerSrc;
            float width  = detailCropW * pxPerSrc;
            float height = detailCropH * pxPerSrc;

            // Y はメモリ行(上向き)と画面 y(下向き)が反転している。detailOriginY はクロップ
            // 下端のメモリ行なので、クロップ上端のメモリ行(detailOriginY + 行数)を画面 y の
            // 上端へ変換する: 画面上からの距離 = (srcH - 上端メモリ行) * pxPerSrc。
            float top = activePreviewRect.y
                + (srcH - (detailOriginY + detailCropH)) * pxPerSrc;

            return new Rect(left, top, width, height);
        }

        /// <summary>
        /// 現在のスクロール位置、ズーム、プレビュースケールからソーステクスチャ座標で見える
        /// クロップリージョンを計算してから、フルソース解像度でそのクロップのみを処理する
        /// バックグラウンドタスクを開始します。
        /// </summary>
        public void GenerateDetailPreviewAsync(int srcW, int srcH, Color32[] srcPixels,
            float scale, float previewZoom, Vector2 previewScrollPos, float viewportW, float viewportH)
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !IrocaWindow.IsReadable(sourceTexture)) return;
            if (scale >= 1f) return;

            if (previewZoom <= DetailMinZoom) return;
            if (viewportW <= 0f || viewportH <= 0f) return;

            // クロップは「画面に見えている範囲」だけに限定する。以前は画像全幅(previewRect.width
            // ＝displayW)を使っていたため x1 が常に srcW までクランプされ、ズーム時に
            // 可視範囲をはるかに超える全幅をフル解像度で処理していた。viewportW/H は
            // スクロールビューの可視サイズなので、ここから可視ソース範囲を求める。
            float invZoomScale = 1f / (previewZoom * scale);
            int x0 = Mathf.FloorToInt(previewScrollPos.x * invZoomScale);
            int x1 = Mathf.CeilToInt((previewScrollPos.x + viewportW) * invZoomScale);

            // Y はソースのメモリ行(GetPixels32 は行0=画像下端の上向き)と、スクロール座標
            // (画面の下向き、上端=0)で上下が反転している。テクスチャは正立描画されるため、
            // スクロール量(下向き y)を一旦ソースのメモリ行(上向き)へ変換してからクロップする。
            // これをしないと、ビュー上端に画像下端のクロップが出る＝表示中の領域とズレる。
            int y0 = Mathf.FloorToInt(srcH - (previewScrollPos.y + viewportH) * invZoomScale);
            int y1 = Mathf.CeilToInt (srcH - previewScrollPos.y * invZoomScale);

            x0 = Mathf.Clamp(x0, 0, srcW);
            y0 = Mathf.Clamp(y0, 0, srcH);
            x1 = Mathf.Clamp(x1, 0, srcW);
            y1 = Mathf.Clamp(y1, 0, srcH);

            int cropW = x1 - x0;
            int cropH = y1 - y0;
            if (cropW <= 0 || cropH <= 0) return;

            // クロップをアップロードする解像度。ソース 1 画素が占めるディスプレイ画素数
            // (pxPerSrc = scale * previewZoom) が 1 未満なら、画面上は縮小表示されている。
            // その状態でフル解像度のままテクスチャ化すると、GPU の Point サンプリングが
            // 数画素に 1 つを拾う最近傍間引きになり、細かい模様がモアレ＝ブロック状の
            // ノイズとして出る(4K テクスチャの 125% は 8.5:1 の間引き)。表示ピクセル数まで
            // 面平均(BoxDownsample)してから渡せば、メインプレビューと同じ縮小品質になる。
            // 拡大表示(pxPerSrc>=1)ではソース画素をそのまま見せたいので縮小しない。
            float pxPerSrc = scale * previewZoom;
            int outW = cropW, outH = cropH;
            if (pxPerSrc < 1f)
            {
                outW = Mathf.Clamp(Mathf.RoundToInt(cropW * pxPerSrc), 1, cropW);
                outH = Mathf.Clamp(Mathf.RoundToInt(cropH * pxPerSrc), 1, cropH);
            }

            var maskSnap = _host._maskView.BuildSnapshot();

            var session = _host.Session;
            // リストの並び順が優先度。先頭(上)ほど優先で先に処理し、重なりを占有する。
            var zonesSnapshot = session.zones
                .Where(z => z.enabled)
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

            // メインプレビュー(フル画像)で解いた keep と再着色アンカー/wash/領域L統計を転写して、
            // 詳細クロップの選択・出力色を一致させる。採否は「同じテクスチャ・同じ寸法」で決める。
            // 世代の厳密一致まで条件にすると、メインプレビュー再生成中(許容値スライダー操作中など)に
            // 容易に外れて「絞り込まれない上位集合」や「クロップ統計で再計算した別の色」を見せてしまう。
            // 最新キャッシュは直近で完了したフル画像処理の結果であり、メイン完了時に詳細は再走するため
            // 最終へ収束する(過渡的に1世代古くてもクロップ内再計算よりは正確)。
            // 一方 sourceId は「どのテクスチャを解いた結果か」であって世代ではないため、切替時にしか
            // 変わらない = 上記の過渡的な取りこぼしを起こさずに、寸法が偶然一致する別テクスチャの
            // keep/統計を転写する事故だけを弾ける。
            int sourceId = sourceTexture.GetInstanceID();
            var parityCache = _host.previewParityCache;
            PreviewParityCache parityForTask =
                (parityCache != null && parityCache.sourceId == sourceId &&
                 parityCache.fullW == capSrcW && parityCache.fullH == capSrcH)
                ? parityCache : null;

            detailJob.Schedule(
                work: token =>
                {
                    Color32[] rawCrop       = new Color32[cropW * cropH];
                    Color32[] processedCrop = new Color32[cropW * cropH];
                    // クロップは各行が連続領域なので行単位 Array.Copy(画素単位 2 配列書き込みを回避)。
                    // processed は raw のクローンで十分(直後に ProcessPixelsArray が上書きする)。
                    for (int cy = 0; cy < cropH; cy++)
                    {
                        int srcRow = (capY0 + cy) * capSrcW + capX0;
                        System.Array.Copy(srcPixelsForTask, srcRow, rawCrop, cy * cropW, cropW);
                    }
                    System.Array.Copy(rawCrop, processedCrop, rawCrop.Length);

                    PixelProcessor.ProcessPixelsArray(processedCrop, cropW, cropH,
                        maskSnap, zonesSnapshot, feather, aaCleanup,
                        hfPasses, hfMinNeighbors, rSatMin, rSatRamp,
                        capX0, capY0, capSrcW, capSrcH, token,
                        useDecontam, decontamRadius,
                        debug: null, parityCache: parityForTask);

                    // 縮小表示のときだけ表示解像度へ落とす。BoxDownsample の scale は
                    // dst/src 比なので pxPerSrc をそのまま渡す(端は内部でクランプされる)。
                    Color32[] outProcessed = processedCrop;
                    Color32[] outRaw       = rawCrop;
                    if (outW != cropW || outH != cropH)
                    {
                        outProcessed = PixelProcessor.BoxDownsample(
                            processedCrop, cropW, cropH, outW, outH, pxPerSrc);
                        outRaw = PixelProcessor.BoxDownsample(
                            rawCrop, cropW, cropH, outW, outH, pxPerSrc);
                    }

                    return new DetailPreviewResult
                    {
                        Raw = outRaw,
                        Processed = outProcessed,
                        TexW = outW,
                        TexH = outH,
                        CropW = cropW,
                        CropH = cropH,
                        OriginX = capX0,
                        OriginY = capY0,
                    };
                },
                apply: result =>
                {
                    _pendingDetailRaw       = result.Raw;
                    _pendingDetailProcessed = result.Processed;
                    _pendingDetailTexW      = result.TexW;
                    _pendingDetailTexH      = result.TexH;
                    _pendingDetailCropW     = result.CropW;
                    _pendingDetailCropH     = result.CropH;
                    _pendingDetailOriginX   = result.OriginX;
                    _pendingDetailOriginY   = result.OriginY;
                    _host.RequestRepaint();
                });
        }

        public void ApplyPendingResult()
        {
            var processed = _pendingDetailProcessed;
            var raw       = _pendingDetailRaw;
            int w  = _pendingDetailTexW;
            int h  = _pendingDetailTexH;
            int cw = _pendingDetailCropW;
            int ch = _pendingDetailCropH;
            int ox = _pendingDetailOriginX;
            int oy = _pendingDetailOriginY;
            _pendingDetailProcessed = null;
            _pendingDetailRaw       = null;

            if (processed == null || raw == null) return;

            detailOriginX = ox;
            detailOriginY = oy;
            detailCropW   = cw;
            detailCropH   = ch;

            // filterMode は Resize では決めない(再利用時に既存設定が維持され、縮小あり/なしを
            // 行き来したときに前回のモードが残るため)。確保後に毎回明示する。
            TextureSlot.Resize(ref detailPreviewTexture, w, h);
            detailPreviewTexture.filterMode = DetailFilterMode(w, cw);
            detailPreviewTexture.SetPixels32(processed);
            detailPreviewTexture.Apply();

            // diff は diff モード表示中のみ生成する(クロップは 4K ズーム閾値直上で数十 MB 級。
            // OFF 中の生成+アップロードは描画されず捨てられるだけだった)。ON へ切り替えた
            // ときは PreviewView 側が詳細を dirty にして再生成する。
            // Color32[] が手元にあるのでそのままバックグラウンド diff へ。GetPixels32 を再度呼ばない。
            if (_host.Preview.diffMode)
                ScheduleDetailDiffTexture(raw, processed, w, h);
        }

        /// <summary>
        /// 詳細クロップの拡大フィルタ。表示解像度へ縮小済み(texW &lt; cropW)なら表示ピクセルと
        /// ほぼ 1:1 なので Bilinear で端数のズレをなじませる。等倍以上のときはソース画素を
        /// くっきり見せたいので Point のままにする（ピクセル単位の確認用）。
        /// </summary>
        private static FilterMode DetailFilterMode(int texW, int cropW)
            => texW < cropW ? FilterMode.Bilinear : FilterMode.Point;

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

            TextureSlot.Resize(ref detailDiffTexture, w, h);
            detailDiffTexture.filterMode = DetailFilterMode(w, detailCropW);
            detailDiffTexture.SetPixels32(pixels);
            detailDiffTexture.Apply();
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
            detailCropW = detailCropH = 0;
            TextureSlot.Release(ref detailPreviewTexture);
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
            detailCropW = detailCropH = 0;
            TextureSlot.Release(ref detailPreviewTexture);
            TextureSlot.Release(ref detailDiffTexture);
        }
    }
}
