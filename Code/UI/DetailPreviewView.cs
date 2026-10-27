// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 詳細プレビュー（フル解像度クロップ）の生成と表示用テクスチャ管理。
    /// PreviewView の補助として動作し、単独のトップレベル状態管理は持たない。
    /// </summary>
    internal unsafe class DetailPreviewView
    {
        // マスクの重ね表示はここでは持たない: 詳細と同じ範囲・寸法の等倍の重ね表示は
        // MaskPaintView(RebuildDetailOverlay)が作り、PreviewView.Draw が詳細の上に描く。
        // ブラシは両方の重ね表示へ直接書くので、詳細の再生成を待たずに塗った所が見える。
        [System.NonSerialized] public Texture2D detailPreviewTexture;
        [System.NonSerialized] public Texture2D detailDiffTexture;
        // 「元を表示」を押している間だけ持つ、変更前の切り出し(detailPreviewTexture と同じ範囲・寸法)。
        // 離したら手放す(EnsureDetailRaw / ReleaseDetailRaw)ので、拡大中の VRAM が増えるのは押している間だけ。
        [System.NonSerialized] public Texture2D detailRawTexture;
        // detailRawTexture を作ったときの切り出し(範囲・寸法が変わったら作り直す)。
        [System.NonSerialized] private int _rawOriginX = -1, _rawOriginY = -1, _rawCropW, _rawCropH, _rawTexW, _rawTexH;
        // detailRawTexture を作り直すたびに進める番号(テクスチャは使い回すので、見た目の作り直しの判定に使う)。
        [System.NonSerialized] public int detailRawVersion;

        [System.NonSerialized] public readonly PreviewJob<DetailPreviewResult> detailJob = new PreviewJob<DetailPreviewResult>();
        [System.NonSerialized] private Color32[] _pendingDetailProcessed;
        [System.NonSerialized] private Color32[] _pendingDetailDiff;
        [System.NonSerialized] private int _pendingDetailTexW, _pendingDetailTexH;
        [System.NonSerialized] private int _pendingDetailCropW, _pendingDetailCropH;
        [System.NonSerialized] private int _pendingDetailOriginX, _pendingDetailOriginY;
        [System.NonSerialized] private float _pendingDetailPxPerSrc;
        [System.NonSerialized] private PreviewLatencyCycle _pendingDetailLatency;
        [System.NonSerialized] public double lastDetailDirtyTime;
        // 体感速度の計測: 確定表示(フル)の直後に始まる再生成を、その操作を起点に測るための引き渡し。
        // スクロール・ズームなど別の契機で lastDetailDirtyTime が打ち直されたら引き渡さない。
        [System.NonSerialized] private PreviewLatencyCycle _latencyCycle;
        [System.NonSerialized] private double _latencyDirtyTime;
        // スクロール・ズーム(MarkViewChanged)が lastDetailDirtyTime を打った時刻と、その操作の時刻。
        // 作り直しがこの打刻から始まったら、拡大表示だけの周期として測る。
        [System.NonSerialized] private long _viewInput;
        [System.NonSerialized] private double _viewDirtyTime;
        [System.NonSerialized] public Rect lastPreviewRect;
        // 前後比較の変更前の画像の矩形(lastPreviewRect と同じ座標。比較していないときは幅 0)。
        [System.NonSerialized] public Rect lastBeforeRect;
        // スクロールビューの可視領域サイズ（ディスプレイピクセル）。クロップ範囲を
        // 画像全幅ではなく「実際に見えている範囲」だけに絞るために使う。
        [System.NonSerialized] public float lastViewportW, lastViewportH;

        // スクロール・ズームのあとは待たずに作り直す。作り直しは確定(フル段)の出力の切り出しだけで軽い
        // (数〜数十 ms)。走っている間に表示範囲が動いたら、終わってすぐ最新の範囲で作り直す(取り消さない。
        // 取り消すと連続したスクロールの間に一度も出ない)。作り直すまでは前の切り出しを元の画像の座標の
        // 位置に描き続ける(ComputeDetailScreenRect)ので、スクロールに付いて動き、新しく見えた端だけが
        // 縮小表示の引き伸ばしになる。以前は手を止めて 0.1 秒待ち、その間は切り出しを消していたため、
        // ホイールを 1 段ずつ回すと段の合間で拡大表示が出入りしてちらついていた。
        // 詳細モード: 表示倍率(PreviewView.EffectiveZoom＝プレビューテクスチャ 1 画素が何 pt か。
        // 全体表示の倍率込み)がこの値を超えたら、低解像度プレビューの引き伸ばしをやめて
        // ソース解像度から作り直したクロップへ切り替える。
        // 旧実装は「ディスプレイ/ソース比 >= 1」を条件にしていたが、ソースが
        // 大きいほど閾値が previewZoom の上限(4x)を超えてしまい、2K超のテクスチャで
        // 詳細プレビューが一切起動しなくなっていた。
        // なお表示倍率 > 1 は「ソース画素より大きく表示されている」ことを意味しない
        // (画面倍率は scale * 表示倍率で、4K なら 125% でも 1/8.5 の縮小)。クロップは
        // 表示ピクセル数へ面平均してからアップロードする(GenerateDetailPreviewAsync 参照)。
        // 1.0 ちょうどでなく少し上にするのは、全体表示の倍率が 1 をわずかに超えるだけのウィンドウで
        // (見た目が変わらないのに)確定のたびに全面のクロップを作り直さないため。ズームのストップは
        // 1 の次が 1.25 なので、全体表示が 1 のときの切り替わりは以前と同じ。
        public const float DetailMinZoom = 1.1f;

        // 永続的な詳細クロップ原点（詳細プレビュー適用時に設定、レンダラーで読み取られます）
        [System.NonSerialized] public int detailOriginX, detailOriginY;
        // クロップが覆うソース画素数。表示矩形の計算はこちらを使う（テクスチャ解像度は
        // 縮小表示のとき表示ピクセル数まで落とすので、両者は一致しない）。
        [System.NonSerialized] public int detailCropW, detailCropH;
        // クロップを表示解像度へ落としたときの倍率(BoxDownsampleRegion に渡した dst/src 比)。変更前の切り出しを
        // 同じ画素の並びで作るのに使う。
        [System.NonSerialized] private float _detailPxPerSrc;

        [System.NonSerialized] private IrocaWindow _host;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
        }

        public bool HasPendingResult => _pendingDetailProcessed != null;

        /// <summary>確定表示が lastDetailDirtyTime を打った直後に呼ぶ(体感速度の計測)。</summary>
        internal void AttachLatency(PreviewLatencyCycle cycle)
        {
            _latencyCycle = cycle;
            _latencyDirtyTime = lastDetailDirtyTime;
        }

        /// <summary>
        /// スクロール・ズームで表示範囲(位置・倍率)が変わったときに呼ぶ。拡大表示を作り直させ
        /// (走っている作り直しが無ければ次の描画で、あればその後すぐ)、体感速度の起点としてこの時刻を控える。
        /// </summary>
        internal void MarkViewChanged()
        {
            lastDetailDirtyTime = EditorApplication.timeSinceStartup;
            _viewDirtyTime = lastDetailDirtyTime;
            _viewInput = PreviewLatencyCycle.Now;
        }

        /// <summary>
        /// パン・ズーム・スクロールなどで表示範囲が動いたときに呼ぶ。打刻(<see cref="MarkViewChanged"/>)するだけで、
        /// 今の拡大表示は捨てない。切り出しは元の画像の座標で持っていて、描く位置は毎回その座標から求める
        /// (<see cref="ComputeDetailScreenRect"/>)ので、表示範囲が動いても下の縮小表示とずれない。
        /// 走っている作り直しも取り消さない(結果は同じく元の画像の座標の切り出しなので、出してよい)。
        /// </summary>
        internal void OnViewMoved() => MarkViewChanged();

        /// <summary>
        /// 再生成を起こした lastDetailDirtyTime の計測を返す。確定表示の打ったものならその再生成の計測、
        /// スクロール・ズームの打ったものならその操作を起点にした拡大表示だけの計測(新しく作る)。
        /// それ以外の契機(差分表示の切り替えなど)なら null。引き渡しは 1 回きり。
        /// </summary>
        internal PreviewLatencyCycle TakeLatencyFor(double dirtyTime, int srcW, int srcH)
        {
            var c = _latencyCycle;
            long viewInput = _viewInput;
            _latencyCycle = null;
            _viewInput = 0;
            if (c != null && dirtyTime == _latencyDirtyTime) return c;
            if (viewInput != 0 && dirtyTime == _viewDirtyTime)
                return new PreviewLatencyCycle { ViewChange = true, Input = viewInput, SourceW = srcW, SourceH = srcH };
            return null;
        }

        public struct DetailPreviewResult
        {
            // 変更前後の差分ハイライト(Processed と同じ範囲・寸法)。差分表示中に作り始めたときだけ(それ以外は null)。
            // 切り出しと同じジョブで作り、同じフレームに反映する(別ジョブにすると、切り出しだけ先に新しい範囲になり、
            // 古い範囲の差分が新しい範囲に引き伸ばされて見えた)。
            public Color32[] Diff;
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
        /// スクリーン空間矩形を返します。displayZoom は画面上の表示倍率(PreviewView.EffectiveZoom)。
        /// </summary>
        public Rect ComputeDetailScreenRect(Rect activePreviewRect, float scale, float displayZoom, Vector2 previewScrollPos, int srcW, int srcH)
        {
            if (detailPreviewTexture == null) return activePreviewRect;

            // ソースピクセルあたりのディスプレイピクセル
            float pxPerSrc = scale * displayZoom;

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
        /// クロップリージョンを計算し、フル段の出力(processedFull)からその範囲を切り出す
        /// バックグラウンドタスクを開始します。
        /// 以前はクロップだけを ProcessPixelsArray で計算し直し、フル画像で解いた選択・統計を転写して
        /// 合わせていたが、転写していない段(グレーモードの中性ツヤ復帰、帯成長)やクロップ端の近傍処理で
        /// 書き出しと食い違っていた(dev_safe/docs/zoom_roi_study_2026-10-03.md §3)。フル段は書き出しと
        /// 同じ入力・同じ関数でフル解像度を計算しているので、その出力の切り出しは書き出しと一致する。
        /// processedFull が無い(まだ確定していない・元画素が変わった)ときは何もしない。
        /// displayZoom は画面上の表示倍率(PreviewView.EffectiveZoom。全体表示の倍率込み)。
        /// </summary>
        public void GenerateDetailPreviewAsync(int srcW, int srcH, NativePixels srcPixels, NativePixels processedFull,
            float scale, float displayZoom, Vector2 previewScrollPos, float viewportW, float viewportH,
            Rect afterRect, Rect beforeRect, PreviewLatencyCycle latency = null)
        {
            long prepStart = PreviewLatencyCycle.Now;
            // 画素は呼び出し元（メインプレビュー）が確保した true source をそのまま受け取るので、
            // ここで Read/Write を要求しない（要求すると PNG/JPG でも詳細プレビューが出ない）。
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null) return;
            if (scale >= 1f) return;

            if (displayZoom <= DetailMinZoom) return;
            if (viewportW <= 0f || viewportH <= 0f) return;
            if (processedFull == null || srcPixels == null ||
                processedFull.Length != srcW * srcH || srcPixels.Length != srcW * srcH) return;

            // クロップは「画面に見えている範囲」だけに限定する。以前は画像全幅(previewRect.width
            // ＝displayW)を使っていたため x1 が常に srcW までクランプされ、ズーム時に
            // 可視範囲をはるかに超える全幅をフル解像度で処理していた。viewportW/H は
            // スクロールビューの可視サイズなので、ここから可視ソース範囲を求める。
            // 前後比較では変更前・変更後の 2 枚が並ぶので、それぞれの見えている範囲を合わせた範囲を切り出す
            // (同じ切り出しを両方に重ねる。変更前は EnsureDetailRaw が同じ範囲で作る)。
            int x0 = srcW, y0 = srcH, x1 = 0, y1 = 0;
            float pxPerSrcView = displayZoom * scale;
            var viewRect = ClampedViewRect(previewScrollPos, viewportW, viewportH, afterRect, beforeRect);
            AddVisibleRange(afterRect, viewRect, pxPerSrcView, srcW, srcH, ref x0, ref y0, ref x1, ref y1);
            if (beforeRect.width > 0f)
                AddVisibleRange(beforeRect, viewRect, pxPerSrcView, srcW, srcH, ref x0, ref y0, ref x1, ref y1);

            int cropW = x1 - x0;
            int cropH = y1 - y0;
            if (cropW <= 0 || cropH <= 0) return;

            // クロップをアップロードする解像度。ソース 1 画素が占めるディスプレイ画素数
            // (pxPerSrc = scale * displayZoom) が 1 未満なら、画面上は縮小表示されている。
            // その状態でフル解像度のままテクスチャ化すると、GPU の Point サンプリングが
            // 数画素に 1 つを拾う最近傍間引きになり、細かい模様がモアレ＝ブロック状の
            // ノイズとして出る(4K テクスチャの 125% は 8.5:1 の間引き)。表示ピクセル数まで
            // 面平均(BoxDownsample)してから渡せば、メインプレビューと同じ縮小品質になる。
            // 拡大表示(pxPerSrc>=1)ではソース画素をそのまま見せたいので縮小しない。
            float pxPerSrc = scale * displayZoom;
            int outW = cropW, outH = cropH;
            if (pxPerSrc < 1f)
            {
                outW = Mathf.Clamp(Mathf.RoundToInt(cropW * pxPerSrc), 1, cropW);
                outH = Mathf.Clamp(Mathf.RoundToInt(cropH * pxPerSrc), 1, cropH);
            }

            ScheduleCrop(srcW, srcPixels, processedFull, x0, y0, cropW, cropH, outW, outH, pxPerSrc, latency, prepStart);
        }

        /// <summary>
        /// フル段の出力(processedFull。幅 srcW)のうち [x0, x0+cropW) × [y0, y0+cropH)(行 0 = 画像の下端)を、
        /// outW×outH の拡大表示として切り出すジョブを予約する。範囲は <see cref="GenerateDetailPreviewAsync"/> が
        /// 見えている範囲から決める。outW×outH が切り出しより小さいときは pxPerSrc(dst/src 比)で面平均して縮める。
        /// </summary>
        internal void ScheduleCrop(int srcW, NativePixels srcPixels, NativePixels processedFull,
            int x0, int y0, int cropW, int cropH, int outW, int outH, float pxPerSrc,
            PreviewLatencyCycle latency = null, long prepStart = 0)
        {
            int capX0 = x0, capY0 = y0, capSrcW = srcW;
            // ここで作る変更前のクロップは差分表示の diff にしか使わない。差分表示を ON にしたときは
            // PreviewView が詳細を作り直させる。「元を表示」の変更前は、押したときに EnsureDetailRaw が作る。
            bool needRaw = _host.Preview.diffMode;
            // 切り出しが終わるまで、このフル段の出力と原本の参照を持つ(使い回しにも渡されない)。後始末で返す。
            var processedForTask = processedFull.AddRef();
            var srcPixelsForTask = needRaw ? srcPixels.AddRef() : null;

            // 体感速度の計測(確定表示から引き渡されたときだけ)。値・分岐は変えない。
            var marks = latency?.Detail;
            if (marks != null)
            {
                marks.PrepStart = prepStart;
                marks.Scheduled = PreviewLatencyCycle.Now;
            }

            detailJob.Schedule(
                work: token =>
                {
                    if (marks != null) marks.WorkStart = PreviewLatencyCycle.Now;
                    Color32[] outProcessed, outRaw = null, outDiff = null;
                    // どちらもジョブの間ほかから書き換えられない(参照を持っている間は使い回されない)。
                    if (outW != cropW || outH != cropH)
                    {
                        // 縮小表示のときは、フル解像度の画素から見えている範囲を直接表示解像度へ落とす
                        // (クロップの複製を作らない。切り出してから縮小したのとビット単位で同じ)。
                        // BoxDownsample の scale は dst/src 比なので pxPerSrc をそのまま渡す。
                        outProcessed = PixelProcessor.BoxDownsampleRegion(processedForTask.Ptr, processedForTask.Length,
                            capSrcW, capX0, capY0, cropW, cropH, outW, outH, pxPerSrc);
                        if (needRaw)
                            outRaw = PixelProcessor.BoxDownsampleRegion(srcPixelsForTask.Ptr, srcPixelsForTask.Length,
                                capSrcW, capX0, capY0, cropW, cropH, outW, outH, pxPerSrc);
                    }
                    else
                    {
                        // 等倍以上ではクロップがそのままテクスチャになる(見えている画素数ぶんなので小さい)。
                        // 各行が連続領域なので行単位で写す。
                        outProcessed = new Color32[cropW * cropH];
                        if (needRaw) outRaw = new Color32[cropW * cropH];
                        long rowBytes = (long)cropW * sizeof(Color32);
                        fixed (Color32* dstP = outProcessed)
                        fixed (Color32* dstR = outRaw)
                            for (int cy = 0; cy < cropH; cy++)
                            {
                                long srcRow = (long)(capY0 + cy) * capSrcW + capX0;
                                System.Buffer.MemoryCopy(processedForTask.Ptr + srcRow, dstP + (long)cy * cropW, rowBytes, rowBytes);
                                if (needRaw)
                                    System.Buffer.MemoryCopy(srcPixelsForTask.Ptr + srcRow, dstR + (long)cy * cropW, rowBytes, rowBytes);
                            }
                    }
                    token.ThrowIfCancellationRequested();
                    if (needRaw) outDiff = PreviewView.BuildDiffPixels(outRaw, outProcessed, outW, outH, token);

                    if (marks != null) marks.WorkEnd = PreviewLatencyCycle.Now;
                    return new DetailPreviewResult
                    {
                        Diff = outDiff,
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
                    if (marks != null) marks.Applied = PreviewLatencyCycle.Now;
                    _pendingDetailLatency   = latency;
                    _pendingDetailDiff      = result.Diff;
                    _pendingDetailProcessed = result.Processed;
                    _pendingDetailTexW      = result.TexW;
                    _pendingDetailTexH      = result.TexH;
                    _pendingDetailCropW     = result.CropW;
                    _pendingDetailCropH     = result.CropH;
                    _pendingDetailOriginX   = result.OriginX;
                    _pendingDetailOriginY   = result.OriginY;
                    _pendingDetailPxPerSrc  = pxPerSrc;
                    _host.RequestRepaint();
                },
                cleanup: () =>
                {
                    processedForTask.Release();
                    srcPixelsForTask?.Release();
                });
        }

        /// <summary>
        /// 画像の矩形 panel(スクロールビューの内容座標)のうち view(同じ座標の見えている範囲)に入る部分を、
        /// ソースの画素範囲([x0,x1)×[y0,y1)。y はメモリ行＝行 0 が画像下端)として足し合わせる。
        /// </summary>
        private static void AddVisibleRange(Rect panel, Rect view, float pxPerSrc, int srcW, int srcH,
            ref int x0, ref int y0, ref int x1, ref int y1)
        {
            if (pxPerSrc <= 0f) return;
            float cx0 = Mathf.Max(view.xMin, panel.xMin), cx1 = Mathf.Min(view.xMax, panel.xMax);
            float cy0 = Mathf.Max(view.yMin, panel.yMin), cy1 = Mathf.Min(view.yMax, panel.yMax);
            if (cx1 <= cx0 || cy1 <= cy0) return;
            float inv = 1f / pxPerSrc;
            int px0 = Mathf.Clamp(Mathf.FloorToInt((cx0 - panel.x) * inv), 0, srcW);
            int px1 = Mathf.Clamp(Mathf.CeilToInt((cx1 - panel.x) * inv), 0, srcW);
            // Y はソースのメモリ行(GetPixels32 は行0=画像下端の上向き)と、画面(下向き、上端=0)で上下が
            // 反転している。テクスチャは正立描画されるため、画面の上からの距離をメモリ行へ変換してから切り出す。
            // これをしないと、ビュー上端に画像下端のクロップが出る＝表示中の領域とズレる。
            int py0 = Mathf.Clamp(Mathf.FloorToInt(srcH - (cy1 - panel.y) * inv), 0, srcH);
            int py1 = Mathf.Clamp(Mathf.CeilToInt(srcH - (cy0 - panel.y) * inv), 0, srcH);
            if (px1 <= px0 || py1 <= py0) return;
            x0 = Mathf.Min(x0, px0);
            y0 = Mathf.Min(y0, py0);
            x1 = Mathf.Max(x1, px1);
            y1 = Mathf.Max(y1, py1);
        }

        /// <summary>
        /// 見えている範囲(スクロールビューの内容座標)。スクロール位置の控えは、パンやズームの補正で内容の端を
        /// 越えて動くことがある(IMGUI は表示のときに端へ丸めるが、控えは丸められない)ので、そのまま使うと
        /// 実際に見えている範囲とずれ、端の見えている部分が切り出しから漏れる(縮小表示のまま残る)。
        /// 内容の端(画像の矩形＋余白)に収まるよう丸める。スクロールバーの分だけ実際の可視範囲は狭いので、
        /// その分は広めに取って、実際に見えている範囲を必ず含むようにする。
        /// </summary>
        private static Rect ClampedViewRect(Vector2 scroll, float viewportW, float viewportH, Rect afterRect, Rect beforeRect)
        {
            float right = afterRect.xMax, bottom = afterRect.yMax;
            if (beforeRect.width > 0f)
            {
                right = Mathf.Max(right, beforeRect.xMax);
                bottom = Mathf.Max(bottom, beforeRect.yMax);
            }
            var pad = GUI.skin.scrollView.padding;
            float contentW = right + pad.right, contentH = bottom + pad.bottom;
            float slack = GUI.skin.verticalScrollbar.fixedWidth + GUI.skin.horizontalScrollbar.fixedHeight + 4f;
            float sx = Mathf.Clamp(scroll.x, 0f, Mathf.Max(0f, contentW - viewportW));
            float sy = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, contentH - viewportH));
            return new Rect(sx, sy, viewportW + slack, viewportH + slack);
        }

        public void ApplyPendingResult()
        {
            var processed = _pendingDetailProcessed;
            var diff      = _pendingDetailDiff;
            int w  = _pendingDetailTexW;
            int h  = _pendingDetailTexH;
            int cw = _pendingDetailCropW;
            int ch = _pendingDetailCropH;
            int ox = _pendingDetailOriginX;
            int oy = _pendingDetailOriginY;
            var latency = _pendingDetailLatency;
            _pendingDetailProcessed = null;
            _pendingDetailDiff      = null;
            _pendingDetailLatency   = null;

            if (processed == null) return;
            if (latency != null) latency.Detail.UploadStart = PreviewLatencyCycle.Now;

            detailOriginX = ox;
            detailOriginY = oy;
            detailCropW   = cw;
            detailCropH   = ch;
            _detailPxPerSrc = _pendingDetailPxPerSrc;

            // filterMode は Resize では決めない(再利用時に既存設定が維持され、縮小あり/なしを
            // 行き来したときに前回のモードが残るため)。確保後に毎回明示する。
            TextureSlot.Resize(ref detailPreviewTexture, w, h);
            detailPreviewTexture.filterMode = DetailFilterMode(w, cw);
            detailPreviewTexture.SetPixels32(processed);
            detailPreviewTexture.Apply();

            // diff は diff モード表示中のみ生成する(クロップは 4K ズーム閾値直上で数十 MB 級。
            // OFF 中の生成+アップロードは描画されず捨てられるだけだった)。ON へ切り替えた
            // ときは PreviewView 側が詳細を dirty にして再生成する。
            // diff は切り出しと同じジョブで作ってあり、ここで同時に差し替える。差分を持たない結果のときは
            // 古い diff を手放す(別の範囲の diff が残って、新しい切り出しの上に重なるのを防ぐ)。
            // 切り出し中に ON へ切り替わったときは diff を持たない結果が届くので作り直させる。
            if (diff != null)
            {
                TextureSlot.Resize(ref detailDiffTexture, w, h);
                detailDiffTexture.filterMode = DetailFilterMode(w, cw);
                detailDiffTexture.SetPixels32(diff);
                detailDiffTexture.Apply();
            }
            else
            {
                TextureSlot.Release(ref detailDiffTexture);
                if (_host.Preview.diffMode) lastDetailDirtyTime = EditorApplication.timeSinceStartup;
            }

            // 体感速度: 拡大表示まで含めたレポートで、確定表示時点のものを差し替える。
            if (latency != null)
            {
                latency.Detail.UploadEnd = PreviewLatencyCycle.Now;
                if (PreviewLatency.Publish(latency)) _host.RequestRepaint();
            }
        }

        /// <summary>
        /// 詳細クロップの拡大フィルタ。表示解像度へ縮小済み(texW &lt; cropW)なら表示ピクセルと
        /// ほぼ 1:1 なので Bilinear で端数のズレをなじませる。等倍以上のときはソース画素を
        /// くっきり見せたいので Point のままにする（ピクセル単位の確認用）。
        /// 「なめらか」(<see cref="PreviewView.SmoothMagnify"/>)がオンなら、等倍以上でも Bilinear にする
        /// (ゲームの中で近くから見たときの見え方に近い)。
        /// </summary>
        internal static FilterMode DetailFilterMode(int texW, int cropW)
            => texW < cropW || PreviewView.SmoothMagnify ? FilterMode.Bilinear : FilterMode.Point;

        /// <summary>
        /// 「元を表示」用の変更前の切り出しを、今の切り出し(detailPreviewTexture)と同じ範囲・寸法・縮小で
        /// 原本(srcPixels。変更後の切り出しの元になったフル段と同じ作業解像度の画素)から作る。
        /// 同じ切り出しのものがあれば作り直さない。切り出しが無い・原本の寸法が合わないときは false。
        /// 見えている範囲だけなので、押した最初のフレームにメインスレッドで作る(数〜数十 ms)。
        /// </summary>
        public bool EnsureDetailRaw(NativePixels srcPixels, int srcW, int srcH)
        {
            var dt = detailPreviewTexture;
            if (dt == null || detailCropW <= 0 || detailCropH <= 0) return false;
            if (srcPixels == null || srcPixels.Length != srcW * srcH) return false;
            if (detailOriginX < 0 || detailOriginY < 0
                || detailOriginX + detailCropW > srcW || detailOriginY + detailCropH > srcH) return false;
            int w = dt.width, h = dt.height;
            if (detailRawTexture != null && _rawOriginX == detailOriginX && _rawOriginY == detailOriginY
                && _rawCropW == detailCropW && _rawCropH == detailCropH && _rawTexW == w && _rawTexH == h)
                return true;

            Color32[] pixels;
            if (w != detailCropW || h != detailCropH)
            {
                // 変更後と同じく、見えている範囲を表示解像度へ面平均で落とす(同じ倍率なので画素の並びがそろう)。
                pixels = PixelProcessor.BoxDownsampleRegion(srcPixels.Ptr, srcPixels.Length,
                    srcW, detailOriginX, detailOriginY, detailCropW, detailCropH, w, h, _detailPxPerSrc);
            }
            else
            {
                pixels = new Color32[detailCropW * detailCropH];
                long rowBytes = (long)detailCropW * sizeof(Color32);
                fixed (Color32* dst = pixels)
                    for (int cy = 0; cy < detailCropH; cy++)
                    {
                        long srcRow = (long)(detailOriginY + cy) * srcW + detailOriginX;
                        System.Buffer.MemoryCopy(srcPixels.Ptr + srcRow, dst + (long)cy * detailCropW, rowBytes, rowBytes);
                    }
            }
            TextureSlot.Resize(ref detailRawTexture, w, h);
            detailRawTexture.filterMode = DetailFilterMode(w, detailCropW);
            detailRawTexture.SetPixels32(pixels);
            detailRawTexture.Apply();
            _rawOriginX = detailOriginX;
            _rawOriginY = detailOriginY;
            _rawCropW = detailCropW;
            _rawCropH = detailCropH;
            _rawTexW = w;
            _rawTexH = h;
            detailRawVersion++;
            return true;
        }

        /// <summary>「元を表示」用の変更前の切り出しを手放す(離したとき・切り出しを捨てたとき)。</summary>
        public void ReleaseDetailRaw()
        {
            if (detailRawTexture == null) return;
            TextureSlot.Release(ref detailRawTexture);
            _rawOriginX = _rawOriginY = -1;
        }

        public void Dispose()
        {
            Suspend();
            detailJob.Dispose();
        }

        public void Suspend()
        {
            // 表示の破棄(InvalidateDisplay)に加えて、作り直し待ちの打刻も消す。
            InvalidateDisplay();
            lastDetailDirtyTime = 0;
        }

        /// <summary>
        /// 拡大表示を捨てる(テクスチャを替えた・元の画素が変わったときなど、切り出しの中身が使えなくなったとき)。
        /// 表示範囲が動いただけなら捨てない(<see cref="OnViewMoved"/>)。
        /// lastDetailDirtyTime は呼び出し側が再設定するので維持する。
        /// </summary>
        public void InvalidateDisplay()
        {
            detailJob.Cancel();
            _pendingDetailProcessed = null;
            _pendingDetailDiff = null;
            detailCropW = detailCropH = 0;
            TextureSlot.Release(ref detailPreviewTexture);
            TextureSlot.Release(ref detailDiffTexture);
            ReleaseDetailRaw();
        }
    }
}
