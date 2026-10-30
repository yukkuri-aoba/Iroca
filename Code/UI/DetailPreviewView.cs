// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 詳細プレビュー（拡大表示）の生成と表示用テクスチャ管理。
    /// PreviewView の補助として動作し、単独のトップレベル状態管理は持たない。
    /// 拡大表示はタイル(<see cref="DetailTileGrid"/>)で作る(0.4.0 の計画の 3.5 の案 A)。見えている範囲を覆うタイルを
    /// 並べた 1 枚をテクスチャにし、差分・「元を表示」・見た目・マスクの重ね表示は、今までの 1 枚の切り出しと同じく
    /// その範囲と寸法で作る。縮めるタイルは控え(<see cref="DetailTileCache"/>。ヒープの外)に覚え、パンでは新しく
    /// 見えたタイルだけを作る。タイルの並びが変わらないパンでは作り直さない(上げ直しもしない)。
    /// </summary>
    internal unsafe class DetailPreviewView
    {
        // マスクの重ね表示はここでは持たない: 詳細と同じ範囲・寸法の等倍の重ね表示は
        // MaskPaintView(RebuildDetailOverlay)が作り、PreviewView.Draw が詳細の上に描く。
        // ブラシは両方の重ね表示へ直接書くので、詳細の再生成を待たずに塗った所が見える。
        [System.NonSerialized] public Texture2D detailPreviewTexture;
        // 「元を表示」を押している間と差分表示の間だけ持つ、変更前の切り出し(detailPreviewTexture と同じ範囲・寸法)。
        // 差分は変更後の切り出しとこれから GPU で描く(PreviewDiffDrawer)。どちらも終わったら手放す
        // (EnsureDetailRaw / ReleaseDetailRaw)ので、拡大中の VRAM が増えるのはその間だけ。
        [System.NonSerialized] public Texture2D detailRawTexture;
        // detailRawTexture を作り直すたびに進める番号(テクスチャは使い回すので、見た目の作り直しの判定に使う)。
        [System.NonSerialized] public int detailRawVersion;

        // 表示中の変更後・変更前の切り出しを作った要求(同じ要求なら作り直さない)。
        [System.NonSerialized] private DetailTileRange _shown, _rawShown;
        [System.NonSerialized] private bool _hasShown, _hasRawShown;

        [System.NonSerialized] public readonly PreviewJob<DetailPreviewResult> detailJob = new PreviewJob<DetailPreviewResult>();
        // 見えている範囲の周り 1 枚分のタイルを、切り出しを出した後に作っておく(次のパンで作らずに済む)。
        // 次の切り出しを始めるときに取り消す(切り出しの計算を待たせない)。
        [System.NonSerialized] private readonly PreviewJob<int> _prefetchJob = new PreviewJob<int>();
        // 縮めたタイルの控え(変更後・変更前)。
        [System.NonSerialized] private readonly DetailTileCache _afterTiles = new DetailTileCache();
        [System.NonSerialized] private readonly DetailTileCache _beforeTiles = new DetailTileCache();
        internal DetailTileCache AfterTiles => _afterTiles;
        // 先読みが走っているか(テストが、控えの枚数を数える前に終わりを待つ)。
        internal bool IsPrefetching => _prefetchJob.IsRunning;

        [System.NonSerialized] private DetailPreviewResult _pending;
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

        // スクロール・ズームのあとは待たずに作り直す。作り直しは確定(フル段)の出力のタイルを並べるだけで軽い
        // (新しく見えたタイルだけを作る)。走っている間に表示範囲が動いたら、終わってすぐ最新の範囲で作り直す
        // (取り消さない。取り消すと連続したスクロールの間に一度も出ない)。作り直すまでは前の切り出しを元の画像の
        // 座標の位置に描き続ける(ComputeDetailScreenRect)ので、スクロールに付いて動き、新しく見えた端だけが
        // 縮小表示の引き伸ばしになる。
        // 詳細モード: 表示倍率(PreviewView.EffectiveZoom＝プレビューテクスチャ 1 画素が何 pt か。
        // 全体表示の倍率込み)がこの値を超えたら、低解像度プレビューの引き伸ばしをやめて
        // ソース解像度から作り直したクロップへ切り替える。
        // 旧実装は「ディスプレイ/ソース比 >= 1」を条件にしていたが、ソースが
        // 大きいほど閾値が previewZoom の上限(4x)を超えてしまい、2K超のテクスチャで
        // 詳細プレビューが一切起動しなくなっていた。
        // なお表示倍率 > 1 は「ソース画素より大きく表示されている」ことを意味しない
        // (画面倍率は scale * 表示倍率で、4K なら 125% でも 1/8.5 の縮小)。タイルは
        // 表示ピクセル数へ面平均してから並べる(DetailTileGrid)。
        // 1.0 ちょうどでなく少し上にするのは、全体表示の倍率が 1 をわずかに超えるだけのウィンドウで
        // (見た目が変わらないのに)確定のたびに全面のクロップを作り直さないため。ズームのストップは
        // 1 の次が 1.25 なので、全体表示が 1 のときの切り替わりは以前と同じ。
        public const float DetailMinZoom = 1.1f;

        // 見えている範囲の外に足す余白(画面の pt)。ホイールの 1 段や小さなドラッグのパンで新しく見える幅を覆う程度。
        // 範囲はこれを覆うタイルの並びまで広がる。
        private const float CropMarginPoints = 96f;

        // 永続的な詳細クロップ原点（詳細プレビュー適用時に設定、レンダラーで読み取られます）
        [System.NonSerialized] public int detailOriginX, detailOriginY;
        // クロップが覆うソース画素数。表示矩形の計算はこちらを使う（テクスチャ解像度は
        // 縮小表示のとき表示ピクセル数まで落とすので、両者は一致しない）。
        [System.NonSerialized] public int detailCropW, detailCropH;

        [System.NonSerialized] private IrocaWindow _host;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
        }

        private PreviewPipeline Pipeline => _host?.Preview?.Pipeline;

        public bool HasPendingResult => _pending.Processed != null;

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
            // タイルを並べた変更後(Mono のヒープの外。結果が参照を 1 つ持つ)と、その要求。
            public NativePixels Processed;
            public DetailTileRange Range;
            // 変更前の切り出し(Processed と同じ範囲・寸法)。差分表示中に作り始めたときだけ(それ以外は null)。
            // 切り出しと同じジョブで作り、同じフレームに反映する(差分はこの 2 枚から描くので、範囲が食い違わない)。
            public NativePixels Raw;
            public DetailTileRange RawRange;

            public void Release()
            {
                Processed?.Release();
                Raw?.Release();
                Processed = Raw = null;
            }
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
        /// 現在のスクロール位置、ズーム、プレビュースケールからソーステクスチャ座標で見える範囲を求め、それを覆う
        /// タイルを並べる(<see cref="ScheduleRange"/>)。土台はフル段の出力(書き出しと同じ計算結果)で、切り出しを
        /// 計算し直さない(以前はクロップだけを計算し直して書き出しと食い違っていた。
        /// dev_safe/docs/zoom_roi_study_2026-10-03.md §3)。
        /// フル段の出力が無い(まだ確定していない・元画素が変わった)ときは何もしない。
        /// displayZoom は画面上の表示倍率(PreviewView.EffectiveZoom。全体表示の倍率込み)。
        /// </summary>
        public void GenerateDetailPreviewAsync(float scale, float displayZoom, Vector2 previewScrollPos,
            float viewportW, float viewportH, Rect afterRect, Rect beforeRect, PreviewLatencyCycle latency = null)
        {
            long prepStart = PreviewLatencyCycle.Now;
            var p = Pipeline;
            if (_host.SourceTexture == null || p == null) return;
            if (scale >= 1f) return;
            if (displayZoom <= DetailMinZoom) return;
            if (viewportW <= 0f || viewportH <= 0f) return;
            int srcW = p.DetailW, srcH = p.DetailH;

            // クロップは「画面に見えている範囲」だけに限定する。以前は画像全幅(previewRect.width
            // ＝displayW)を使っていたため x1 が常に srcW までクランプされ、ズーム時に
            // 可視範囲をはるかに超える全幅をフル解像度で処理していた。viewportW/H は
            // スクロールビューの可視サイズなので、ここから可視ソース範囲を求める。
            // 前後比較では変更前・変更後の 2 枚が並ぶので、それぞれの見えている範囲を合わせた範囲を切り出す
            // (同じ切り出しを両方に重ねる。変更前は EnsureDetailRaw が同じ範囲で作る)。
            int x0 = srcW, y0 = srcH, x1 = 0, y1 = 0;
            float pxPerSrc = displayZoom * scale;
            var viewRect = ClampedViewRect(previewScrollPos, viewportW, viewportH, afterRect, beforeRect);
            AddVisibleRange(afterRect, viewRect, pxPerSrc, srcW, srcH, ref x0, ref y0, ref x1, ref y1);
            if (beforeRect.width > 0f)
                AddVisibleRange(beforeRect, viewRect, pxPerSrc, srcW, srcH, ref x0, ref y0, ref x1, ref y1);

            if (x1 <= x0 || y1 <= y0) return;
            // 上下左右に少し余白を足す(小さなパンなら、作り直しが届くまでの間も新しく見えた端が粗くならない)。
            // 余白は画面の大きさで決めるので、切り出しの手間は倍率に依らず見えている範囲の数割増しで済む。
            int margin = Mathf.CeilToInt(CropMarginPoints / pxPerSrc);
            ScheduleRange(Mathf.Max(0, x0 - margin), Mathf.Max(0, y0 - margin),
                Mathf.Min(srcW, x1 + margin), Mathf.Min(srcH, y1 + margin), pxPerSrc, latency, prepStart);
        }

        /// <summary>
        /// 拡大表示の土台のうち [x0, x1) × [y0, y1)(行 0 = 画像の下端)を覆うタイルを、元の 1 画素が画面の pxPerSrc pt に
        /// なる倍率で並べるジョブを予約する。表示中と同じタイルの並び・同じ土台なら何もしない(false)。
        /// 範囲は <see cref="GenerateDetailPreviewAsync"/> が見えている範囲から決める(テストは直接呼ぶ)。
        /// </summary>
        internal bool ScheduleRange(int x0, int y0, int x1, int y1, float pxPerSrc,
            PreviewLatencyCycle latency = null, long prepStart = 0)
        {
            var p = Pipeline;
            var output = p?.DetailOutput;
            var source = p?.DetailSource;
            int w = p?.DetailW ?? 0, h = p?.DetailH ?? 0;
            if (output == null || source == null || w <= 0 || h <= 0
                || output.Length != w * h || source.Length != w * h) return false;
            if (x1 <= x0 || y1 <= y0) return false;

            var grid = DetailTileGrid.For(w, h, pxPerSrc);
            var range = DetailTileRange.Covering(grid, x0, y0, x1, y1, p.DetailOutputVersion);
            var rawRange = range.WithVersion(p.DetailSourceVersion);
            // 差分表示の間は、変更前の切り出しも一緒に作る(差分は変更後の切り出しとこれから描く)。差分表示へ
            // 切り替えた直後の切り出しと「元を表示」の変更前は、描くときに EnsureDetailRaw が同じ範囲で作る。
            bool needRaw = _host.Preview.diffMode;
            if (_hasShown && _shown.Equals(range) && detailPreviewTexture != null
                && (!needRaw || (_hasRawShown && _rawShown.Equals(rawRange) && detailRawTexture != null)))
                return false;

            // 前の要求の先読みは止める(今の切り出しの計算を待たせない)。控えは今の要求に合わせる(前の要求のジョブが
            // 後から作ったタイルは控えに入らない)。
            _prefetchJob.Cancel();
            _afterTiles.Retarget(grid, range.Version);
            if (needRaw) _beforeTiles.Retarget(grid, rawRange.Version);
            // 並べ終わるまで、土台の参照を持つ(使い回しにも渡されない)。後始末で返す。
            var outputForTask = output.AddRef();
            var sourceForTask = needRaw ? source.AddRef() : null;
            var afterTiles = _afterTiles;
            var beforeTiles = _beforeTiles;

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
                    var result = new DetailPreviewResult { Range = range, RawRange = rawRange };
                    try
                    {
                        result.Processed = DetailTileCache.Assemble(range, outputForTask, afterTiles, token);
                        if (sourceForTask != null)
                            result.Raw = DetailTileCache.Assemble(rawRange, sourceForTask, beforeTiles, token);
                        token.ThrowIfCancellationRequested();
                    }
                    catch
                    {
                        result.Release();
                        throw;
                    }
                    if (marks != null) marks.WorkEnd = PreviewLatencyCycle.Now;
                    return result;
                },
                apply: result =>
                {
                    if (marks != null) marks.Applied = PreviewLatencyCycle.Now;
                    _pending.Release();
                    _pending = result;
                    _pendingDetailLatency = latency;
                    _host.RequestRepaint();
                    SchedulePrefetch(range, result.Raw != null);
                },
                cleanup: () =>
                {
                    outputForTask.Release();
                    sourceForTask?.Release();
                },
                discard: result => result.Release());
            return true;
        }

        /// <summary>
        /// 並べたタイルの周り 1 枚分(縮めるタイルだけ)を控えへ作っておく。土台がもう替わっていたら作らない。
        /// </summary>
        private void SchedulePrefetch(DetailTileRange range, bool withRaw)
        {
            var grid = range.Grid;
            if (!grid.Shrinks) return;
            var p = Pipeline;
            if (p == null || p.DetailOutputVersion != range.Version || p.DetailW != grid.W || p.DetailH != grid.H) return;
            var output = p.DetailOutput;
            var source = withRaw ? p.DetailSource : null;
            if (output == null) return;
            int sourceVersion = p.DetailSourceVersion;
            int c0 = Mathf.Max(0, range.C0 - 1), c1 = Mathf.Min(grid.Columns, range.C1 + 1);
            int r0 = Mathf.Max(0, range.R0 - 1), r1 = Mathf.Min(grid.Rows, range.R1 + 1);
            if (c0 == range.C0 && c1 == range.C1 && r0 == range.R0 && r1 == range.R1) return;

            var outputForTask = output.AddRef();
            var sourceForTask = source?.AddRef();
            var afterTiles = _afterTiles;
            var beforeTiles = _beforeTiles;
            _prefetchJob.Schedule(
                work: token =>
                {
                    int made = 0;
                    for (int r = r0; r < r1; r++)
                        for (int c = c0; c < c1; c++)
                        {
                            if (c >= range.C0 && c < range.C1 && r >= range.R0 && r < range.R1) continue;
                            token.ThrowIfCancellationRequested();
                            if (afterTiles.Prefetch(grid, range.Version, c, r, outputForTask)) made++;
                            if (sourceForTask != null)
                            {
                                token.ThrowIfCancellationRequested();
                                beforeTiles.Prefetch(grid, sourceVersion, c, r, sourceForTask);
                            }
                        }
                    return made;
                },
                apply: _ => { },
                cleanup: () =>
                {
                    outputForTask.Release();
                    sourceForTask?.Release();
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
            var result = _pending;
            var latency = _pendingDetailLatency;
            _pending = default;
            _pendingDetailLatency = null;

            if (result.Processed == null) return;
            if (latency != null) latency.Detail.UploadStart = PreviewLatencyCycle.Now;

            var range = result.Range;
            detailOriginX = range.OriginX;
            detailOriginY = range.OriginY;
            detailCropW   = range.SourceW;
            detailCropH   = range.SourceH;
            _shown = range;
            _hasShown = true;

            // filterMode は Resize では決めない(再利用時に既存設定が維持され、縮小あり/なしを
            // 行き来したときに前回のモードが残るため)。確保後に毎回明示する。
            PreviewRenderer.UploadPixels(ref detailPreviewTexture, result.Processed);
            detailPreviewTexture.filterMode = DetailFilterMode(detailPreviewTexture.width, detailCropW);

            // 差分表示中に作り始めた切り出しは、同じ範囲の変更前の切り出しも持ってくる(切り出しと同じジョブで作って
            // あり、ここで同時に差し替える)。「元を表示」と同じテクスチャに置き、EnsureDetailRaw は同じ範囲なら作り直さない。
            // 持っていない結果のときは何もしない(範囲が変われば、要るときに EnsureDetailRaw が作り直す)。
            if (result.Raw != null)
            {
                PreviewRenderer.UploadPixels(ref detailRawTexture, result.Raw);
                detailRawTexture.filterMode = DetailFilterMode(detailRawTexture.width, detailCropW);
                _rawShown = result.RawRange;
                _hasRawShown = true;
                detailRawVersion++;
            }
            result.Release();

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
        /// 「元を表示」・差分表示用の変更前の切り出しを、今の切り出し(detailPreviewTexture)と同じタイルの並びで
        /// 拡大表示の変更前の土台(PreviewPipeline.DetailSource)から作る。同じ並び・同じ土台のものがあれば作り直さない。
        /// 切り出しが無い・土台の寸法が合わないときは false。見えている範囲だけなので、押した最初のフレームに
        /// メインスレッドで作る(縮めたタイルは控えを使う)。
        /// </summary>
        public bool EnsureDetailRaw()
        {
            if (detailPreviewTexture == null || !_hasShown) return false;
            var p = Pipeline;
            var source = p?.DetailSource;
            var grid = _shown.Grid;
            if (source == null || p.DetailW != grid.W || p.DetailH != grid.H || source.Length != grid.W * grid.H) return false;
            var rawRange = _shown.WithVersion(p.DetailSourceVersion);
            if (detailRawTexture != null && _hasRawShown && _rawShown.Equals(rawRange)) return true;

            _beforeTiles.Retarget(grid, rawRange.Version);
            var pixels = DetailTileCache.Assemble(rawRange, source, _beforeTiles, CancellationToken.None);
            try
            {
                PreviewRenderer.UploadPixels(ref detailRawTexture, pixels);
            }
            finally { pixels.Release(); }
            detailRawTexture.filterMode = DetailFilterMode(detailRawTexture.width, detailCropW);
            _rawShown = rawRange;
            _hasRawShown = true;
            detailRawVersion++;
            return true;
        }

        /// <summary>「元を表示」用の変更前の切り出しを手放す(離したとき・切り出しを捨てたとき)。</summary>
        public void ReleaseDetailRaw()
        {
            _hasRawShown = false;
            if (detailRawTexture == null) return;
            TextureSlot.Release(ref detailRawTexture);
        }

        /// <summary>
        /// 縮めたタイルの控えを手放す(拡大表示をやめた・切り出しを捨てた)。表示中の切り出しはそのまま。
        /// </summary>
        internal void ReleaseTiles()
        {
            _prefetchJob.Cancel();
            _afterTiles.Clear();
            _beforeTiles.Clear();
        }

        public void Dispose()
        {
            Suspend();
            detailJob.Dispose();
            _prefetchJob.Dispose();
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
            _pending.Release();
            _pending = default;
            _pendingDetailLatency = null;
            _hasShown = false;
            detailCropW = detailCropH = 0;
            TextureSlot.Release(ref detailPreviewTexture);
            ReleaseDetailRaw();
            ReleaseTiles();
        }
    }
}
