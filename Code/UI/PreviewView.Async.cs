// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: 非同期プレビュー生成(プロキシ/フル解像度ジョブ・pending 適用・diff 生成)。
    internal unsafe partial class PreviewView
    {

        // dragOnly: ドラッグ中の追従。プロキシ段だけを回し(縮小しないテクスチャはフル段がそのまま
        // 軽いのでフル段)、入力を _dragReq に控える。確定は FinishDragPreview が同じ入力で行う。
        // internal は WindowOutputParityTests が OnGUI を回さずに作り直すため。
        internal void GeneratePreviewAsync(bool dragOnly = false)
        {
            long prepStart = PreviewLatencyCycle.Now;
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !EnsureTrueSource(sourceTexture)) return;

            int srcW = TrueSourceW;
            int srcH = TrueSourceH;

            // 表示寸法の丸めは ComputeFitSize が単一の正(ハーネスのプレビュー段検証と共有)。
            PixelProcessor.ComputeFitSize(srcW, srcH, IrocaConsts.Preview.MaxSize,
                out int prevW, out int prevH, out float scale);

            NativePixels srcPixels;
            // rawDisplay: 既に確定しているもの(キャッシュヒット or scale>=1 で src の写し)は非 null。
            // キャッシュミス かつ scale<1 のときのみ null にし、ジョブ側で BoxDownsample してから
            // apply でキャッシュへ確定する(メインスレッドのダウンサンプルヒッチを回避)。
            Color32[] rawDisplay;
            if (_cachedSourceTexture == sourceTexture &&
                _cachedSrcPixels != null &&
                ReferenceEquals(_cachedSrcPixels, TrueSource) &&
                _cachedRawDisplay != null &&
                _cachedSrcW == srcW && _cachedSrcH == srcH &&
                _cachedPrevW == prevW && _cachedPrevH == prevH)
            {
                srcPixels = _cachedSrcPixels;
                rawDisplay = _cachedRawDisplay;
            }
            else
            {
                srcPixels = TrueSource;
                // scale>=1 は縮小不要で raw は src の写し(表示の上限 IrocaConsts.Preview.MaxSize 以下なので小さい)。
                // scale<1 はジョブ側で生成するため null。
                rawDisplay = scale < 1f ? null : srcPixels.ToArray();

                // テクスチャ/寸法が変わった = キャッシュ済み選択(strength)の前提画素が変わる。
                // 選択キャッシュはキーに画素内容を含まないので、ここで必ず破棄する(寸法不一致は
                // TryGet で自動ミスするが、同寸法の別テクスチャを取り違えないよう明示的に Clear)。
                _selectionCache?.Clear();
                _proxySelectionCache?.Clear();

                _cachedSourceTexture = sourceTexture;
                _cachedSrcPixels     = srcPixels;
                _cachedRawDisplay    = rawDisplay;   // scale<1 のときは一旦 null、apply で確定
                _cachedSrcW          = srcW;
                _cachedSrcH          = srcH;
                _cachedPrevW         = prevW;
                _cachedPrevH         = prevH;
            }

            var maskSnap = _host._maskView.BuildSnapshot();

            var session = _host.Session;
            // 削除されたゾーンの選択キャッシュ(4K で 67MB/ゾーン)が恒久残留しないよう、セッションに
            // 現存するゾーン id 以外のエントリを毎回刈る(無効ゾーンは残す=再有効化で再計算を避ける)。
            var liveZoneIds = new HashSet<string>(session.zones.Select(z => z.id));
            // 自動調整の入力(証拠マスク)も同じ集合で刈る。
            _host.RetainAutoTuneInputs(liveZoneIds);
            _selectionCache?.RetainOnly(liveZoneIds);
            _proxySelectionCache?.RetainOnly(liveZoneIds);
            // リストの並び順が優先度。先頭(上)ほど優先で先に処理し、重なりを占有する。
            // ソロ表示中はそのゾーンだけを処理する（★表示専用★。エクスポートは
            // 常に有効ゾーン全部を適用し、ExportView がソロ中である旨を注意表示する）。
            var input = RecolorInput.FromEditing(session, maskSnap, _host.SoloZone);

            // Debug capture: Code.Debug/ asmdef があり、かつ DebugView でトグル ON のときだけ
            // Factory が非 null インスタンスを返す。それ以外は null で、本体は何もキャプチャしない。
            IDebugCapture debugCap = DebugCaptureHooks.Factory?.Invoke();

            // 体感速度の計測(値・分岐は変えない)。起点はこの再生成が反映する最後の操作。
            var latency = new PreviewLatencyCycle { PrepStart = prepStart, SourceW = srcW, SourceH = srcH };
            InputClock.Snapshot(latency);

            // 入力スナップショットを 1 回だけ構築し、プロキシ(概要)とフル(確定)の両段へ渡す。
            // 両段が同一入力を処理することを保証する(プロキシとフルで選択がズレないように)。
            var req = new PreviewRequest
            {
                source = sourceTexture,
                srcW = srcW, srcH = srcH,
                srcPixels = srcPixels, rawDisplay = rawDisplay,
                scale = scale, prevW = prevW, prevH = prevH,
                maskSnap = input.Masks, zonesSnapshot = input.Zones,
                settings = input.Settings,
                debugCap = debugCap,
                latency = latency,
            };

            // 段階的リファイン: ソースが縮小される(scale<1)ときだけ、まず低解像度プロキシで概要を
            // 即表示し、続けてフル解像度で確定する。scale>=1(ソースが既に小さい)ではプロキシの利得が
            // 無いので従来どおりフルのみ走らせる。
            // MarkDirtyFullRefine 経由(AI 提案コミット等)はプロキシを飛ばし、確定表示を保った
            // ままフルで差し替える(プロキシへ一瞬戻る「ちらつき」の防止)。
            latency.PrepEnd = PreviewLatencyCycle.Now;
            _dragRunning = dragOnly;
            if (dragOnly)
            {
                // _skipProxyOnce はここでは消費しない(確定側で消える)。
                _dragReq = req;
                _dragReqStale = false;
                if (scale < 1f)
                    ScheduleProxyPreview(req, chainFull: false);
                else
                    ScheduleFullPreview(req);
                return;
            }
            _dragReq = null;
            bool skipProxy = _skipProxyOnce;
            _skipProxyOnce = false;
            if (scale < 1f && !skipProxy)
                ScheduleProxyPreview(req, chainFull: true);
            else
                ScheduleFullPreview(req);
        }

        /// <summary>
        /// ドラッグの追従を確定する。最後の追従と同じ入力(スナップショット)でフル段だけを走らせるので、
        /// 体感の計測も同じ周期のまま続く(初回表示 = 追従のプロキシ、確定 = このフル段)。
        /// 呼び出し側は、その追従より後に操作が無いことを確かめてから呼ぶ。
        /// </summary>
        private void FinishDragPreview()
        {
            var req = _dragReq;
            _dragReq = null;
            _dragRunning = false;
            _skipProxyOnce = false;
            // 縮小しないテクスチャは、追従のときにフル段まで済んでいる。
            if (req == null || req.latency.Full.Scheduled != 0) return;
            ScheduleFullPreview(req);
        }

        // 段階的リファインの入力スナップショット。GeneratePreviewAsync が 1 回構築し、プロキシ段と
        // フル段が同一の値を処理する。フィールドはバックグラウンドジョブからの読み取り専用(不変)。
        private sealed class PreviewRequest
        {
            public Texture2D source;           // 結果をシーンのアバターへ渡すときの宛先(LivePreview)
            public int srcW, srcH;
            // 原本(参照は持たない。ジョブを予約するときに足し、ジョブの後始末で返す)。
            public NativePixels srcPixels;
            public Color32[] rawDisplay;       // null=ジョブ側で BoxDownsample して確定
            public float scale;
            public int prevW, prevH;
            public MaskSnapshot maskSnap;
            public System.Collections.Generic.List<ColorZone> zonesSnapshot;
            public RecolorSettings settings;
            public IDebugCapture debugCap;
            public PreviewLatencyCycle latency;
        }

        // 段階的リファイン第1段。ソースを ProxyMaxSize へ縮小してから処理し、概要を即表示する。
        // chainFull なら完了 apply でフル段(ScheduleFullPreview)を同一スナップショットでスケジュールする(直列)。
        // ドラッグ中の追従(chainFull=false)はプロキシで止め、確定は FinishDragPreview に任せる。
        // 出力は保持しない(詳細プレビューの切り出し元はフル段の出力だけ)。
        private void ScheduleProxyPreview(PreviewRequest req, bool chainFull)
        {
            // プロキシ寸法の丸めは ComputeFitSize が単一の正(ハーネスのプレビュー段検証と共有)。
            PixelProcessor.ComputeFitSize(req.srcW, req.srcH, IrocaConsts.Preview.ProxyMaxSize,
                out int proxyW, out int proxyH, out float proxyScale);
            var proxySelCache = _proxySelectionCache;
            var marks = req.latency.Proxy;
            marks.Scheduled = PreviewLatencyCycle.Now;
            // 同じソース・同じ寸法で縮小済みなら使い回す(ジョブは読むだけ)。
            var cached = _proxySource;
            if (cached != null && !(ReferenceEquals(cached.src, req.srcPixels) && cached.w == proxyW
                    && cached.h == proxyH && cached.prevW == req.prevW && cached.prevH == req.prevH))
                cached = null;

            // 縮小した控えが無いときだけジョブが原本を読む。読む間の参照を足し、後始末で返す。
            var src = cached == null ? req.srcPixels.AddRef() : null;
            _proxyJob.Schedule(
                work: token =>
                {
                    marks.WorkStart = PreviewLatencyCycle.Now;
                    // ソースをプロキシ解像度へ縮小してから処理する(全フェーズが画素数に比例して軽くなる)。
                    // raw(比較/差分表示の before)は processed と同じリサンプル鎖を通す。
                    // src→表示 の 1 段縮小と src→proxy→表示 の 2 段縮小では箱平均の境界が
                    // 揃わず、再着色していない画素まで差分閾値を超えて誤点灯するため
                    // (実テクスチャで表示画素の ~17%)。処理前のプロキシを控えて同じ鎖へ流す。
                    // 控え(proxyRaw)はソースと寸法だけで決まるので、前の回のものがあればそれを使う。
                    // 処理は ProcessPixelsArray が書き換えるので、その複製に対して行う。
                    Color32[] proxyRaw = cached?.proxy ?? PixelProcessor.BoxDownsample(
                        src.Ptr, req.srcW, req.srcH, proxyW, proxyH, proxyScale);
                    Color32[] proxyPixels = (Color32[])proxyRaw.Clone();
                    marks.CoreStart = PreviewLatencyCycle.Now;
                    // 控えた proxyRaw は処理前と同じ中身なので、Core の入力スナップショットにも使う(複製しない)。
                    PixelProcessor.ProcessPixelsArray(proxyPixels, proxyW, proxyH, req.maskSnap, req.zonesSnapshot,
                        req.settings, token,
                        debug: null, selectionCache: proxySelCache,
                        sourcePixels: proxyRaw);
                    marks.CoreEnd = PreviewLatencyCycle.Now;
                    marks.CoreReport = DebugCaptureHooks.TakeThreadPerfReport();

                    // 表示寸法へ。ProxyMaxSize==MaxSize なら proxy==表示で再縮小なし(最頻ケース)。
                    bool needsResample = proxyW != req.prevW || proxyH != req.prevH;
                    float toDisplay = req.prevW / (float)proxyW;
                    Color32[] processedDisplay = needsResample
                        ? PixelProcessor.BoxDownsample(proxyPixels, proxyW, proxyH, req.prevW, req.prevH, toDisplay)
                        : proxyPixels;
                    Color32[] rawForJob = cached?.display ?? (needsResample
                        ? PixelProcessor.BoxDownsample(proxyRaw, proxyW, proxyH, req.prevW, req.prevH, toDisplay)
                        : proxyRaw);
                    marks.WorkEnd = PreviewLatencyCycle.Now;
                    return (processedDisplay, rawForJob, proxyRaw);
                },
                apply: result =>
                {
                    marks.Applied = PreviewLatencyCycle.Now;
                    // 縮小した控えを確定する(ソースが替わっていなければ。表示側も before を読むだけ)。
                    if (cached == null && ReferenceEquals(req.srcPixels, _cachedSrcPixels))
                        _proxySource = new ProxySourceCache
                        {
                            src = req.srcPixels, w = proxyW, h = proxyH, prevW = req.prevW, prevH = req.prevH,
                            proxy = result.proxy, display = result.raw,
                        };
                    _pendingIsFinal          = false;
                    _pendingIsDrag           = !chainFull;
                    _pendingLatency          = req.latency;
                    _pendingLatencyStage     = marks;
                    _pendingRawDisplay       = result.raw;
                    _pendingProcessedDisplay = result.processed;
                    _pendingPrevW            = req.prevW;
                    _pendingPrevH            = req.prevH;
                    // プロキシは近似。フル解像度出力の保持(詳細プレビューの切り出し元)・raw キャッシュ確定・
                    // debug 公開はフル段に委ねる(詳細プレビューの正確さを死守し、二重管理を避ける)。
                    // シーンのアバターに概要を映すのはドラッグ中の追従だけ(手を止めるとフル段が置き換える)。
                    // 続けてフル段が来るとき(ブラシのストローク・Ctrl+Z などの 1 回きりの操作)は映さない:
                    // 概要は表示解像度なので、アバターのテクスチャ寸法へ引き伸ばすとフル段が届くまでの
                    // 一瞬だけ粗く崩れて見える。アバターは前の確定表示のまま、フル段で一度に差し替える。
                    if (!chainFull) LivePreview.Push(req.source, result.processed, req.prevW, req.prevH);
                    _host.RequestRepaint();
                    // 続けてフル解像度で確定(同一スナップショット)。
                    if (chainFull) ScheduleFullPreview(req);
                },
                cleanup: () => src?.Release());
        }

        // 段階的リファイン第2段(=従来のフル解像度処理)。フル解像度で処理→表示解像度へ縮小し、
        // プロキシ表示を確定結果へ差し替える。フル解像度の出力を保持し、詳細プレビューはそこから切り出す。
        // この経路はフル解像度処理そのままなので出力は段階的リファイン導入前とバイト不変。
        private void ScheduleFullPreview(PreviewRequest req)
        {
            var selCache = _selectionCache;
            var marks = req.latency.Full;
            marks.Scheduled = PreviewLatencyCycle.Now;
            // 出力バッファ(フル解像度)は前のフル段から使い回す(取り出したものの参照はこのジョブが持つ)。
            var buffers = _outputBuffers;
            NativePixels reused = buffers?.Take(req.srcW, req.srcH);
            // 原本はジョブが読む間の参照を足し、後始末で返す。
            var src = req.srcPixels.AddRef();
            // 拡大表示を取り込み後の寸法で映すなら、その寸法(非破壊ビルドと同じ面平均で縮める)。
            int dispW = _displaySource != null ? _displayW : 0, dispH = _displaySource != null ? _displayH : 0;
            bool workStarted = false;
            // もう書かない出力バッファを使い回しへ戻す(参照を渡す。使い回しが無ければ返す)。
            void RetireOutput(NativePixels b)
            {
                if (b == null) return;
                if (buffers != null) buffers.Retire(b);
                else b.Release();
            }
            _previewJob.Schedule(
                work: token =>
                {
                    workStarted = true;
                    marks.WorkStart = PreviewLatencyCycle.Now;
                    NativePixels pixels = reused ?? NativePixels.Allocate(req.srcW, req.srcH);
                    bool handedOff = false;
                    NativePixels display = null;
                    try
                    {
                        pixels.CopyFrom(src);
                        marks.CoreStart = PreviewLatencyCycle.Now;
                        // 写し元のソース画素は誰も書き換えない(テクスチャが変われば別の画素になる)ので、
                        // Core の入力スナップショットにそのまま使う(4K で 64MB の複製を作らない)。
                        PixelProcessor.ProcessPixelsNative(pixels.Ptr, req.srcW, req.srcH, req.maskSnap, req.zonesSnapshot,
                            req.settings, token,
                            debug: req.debugCap, selectionCache: selCache,
                            sourcePixels: src.Ptr);
                        marks.CoreEnd = PreviewLatencyCycle.Now;
                        marks.CoreReport = DebugCaptureHooks.TakeThreadPerfReport();

                        // 表示用は管理配列(縮小しないテクスチャは表示の上限 IrocaConsts.Preview.MaxSize 以下なので小さい)。
                        Color32[] processedDisplay = req.scale < 1f
                            ? PixelProcessor.BoxDownsample(pixels.Ptr, req.srcW, req.srcH, req.prevW, req.prevH, req.scale)
                            : pixels.ToArray();
                        // raw が未確定(キャッシュミス & scale<1)ならバックグラウンドで生成する。
                        // それ以外(キャッシュヒット or scale>=1)は確定済みをそのまま使う。
                        Color32[] rawForJob = req.rawDisplay ?? PixelProcessor.BoxDownsample(
                            src.Ptr, req.srcW, req.srcH, req.prevW, req.prevH, req.scale);
                        if (dispW > 0)
                        {
                            display = NativePixels.Allocate(dispW, dispH);
                            WorkingResolution.ResizeArea(pixels.Ptr, req.srcW, req.srcH, dispW, dispH, display.Ptr);
                        }
                        marks.WorkEnd = PreviewLatencyCycle.Now;
                        handedOff = true;
                        return (processedDisplay, rawForJob, pixels, display);
                    }
                    finally
                    {
                        // 取り消し・例外で結果を渡さなかった作業バッファは、もう書かないので使い回しへ戻す。
                        if (!handedOff)
                        {
                            RetireOutput(pixels);
                            display?.Release();
                        }
                    }
                },
                apply: result =>
                {
                    marks.Applied = PreviewLatencyCycle.Now;
                    // AI 提案コミットの E2E 計測(アーム中のみ 1 回ログ)。実際の画面反映は
                    // 次フレームの ApplyPendingPreview だが、差は 1 フレームなので近似で計上。
                    MaskSuggestPerf.NotifyFullPreviewApplied();
                    _pendingIsFinal          = true;
                    _pendingIsDrag           = false;
                    _pendingLatency          = req.latency;
                    _pendingLatencyStage     = marks;
                    _pendingRawDisplay       = result.raw;
                    _pendingProcessedDisplay = result.processed;
                    _pendingPrevW            = req.prevW;
                    _pendingPrevH            = req.prevH;
                    // フル解像度の結果を保持する(ジョブが持っていた参照を引き取る)。拡大表示(詳細クロップ)は
                    // ここから切り出すだけなので、書き出しと同じ計算結果そのものになる(切り出しを計算し直さない)。
                    RetireFullOutput();
                    _fullOutput       = result.full;
                    _fullOutputSource = req.srcPixels;
                    // 拡大表示の寸法が変わっていたら(原本を読み直した・「元の解像度」をオンにした)使わない
                    // (原本を読み直したなら _fullOutputSource も別の原本になる)。
                    if (result.display != null && (result.display.Width != _displayW || result.display.Height != _displayH))
                    {
                        result.display.Release();
                        _displayOutput = null;
                    }
                    else _displayOutput = result.display;
                    // 走っている間に「元の解像度」をオフへ戻したら、縮めたものがまだ無いので、ここで縮める。
                    if (_displayOutput == null && _displaySource != null && ReferenceEquals(req.srcPixels, TrueSource))
                        _displayOutput = ShrinkToDisplay(result.full);
                    _fullOutputFreshSinceHide = true;
                    // ジョブ側で生成した raw をキャッシュへ確定する(まだ未確定で、対象テクスチャと
                    // 寸法が変わっていない場合のみ。新しいミスで上書きされていれば触らない)。
                    if (_cachedRawDisplay == null && _cachedSrcPixels == req.srcPixels &&
                        _cachedSrcW == req.srcW && _cachedSrcH == req.srcH &&
                        _cachedPrevW == req.prevW && _cachedPrevH == req.prevH)
                    {
                        _cachedRawDisplay = result.raw;
                    }
                    // シーンのアバターにはフル解像度の結果を映す(取り込み済みテクスチャの寸法へは LivePreview が合わせる。
                    // 映している間は LivePreview が参照を持つ)。
                    LivePreview.Push(req.source, result.full);
                    _host.LatestDebugCapture = req.debugCap;
                    // 充填完了したキャプチャを（メインスレッドの）ここで初めて公開する。
                    // 生成時に公開すると、DebugWindow が Add 中のリストを foreach して競合する。
                    if (req.debugCap != null)
                        DebugCaptureHooks.RaiseCaptureComplete(req.debugCap);
                    _host.RequestRepaint();
                },
                cleanup: () =>
                {
                    src.Release();
                    // 取り消されて計算しなかったときは、取り出した出力バッファを戻す。
                    if (!workStarted) RetireOutput(reused);
                },
                // 計算は済んだが apply しなかった(後から来た操作で古くなった)出力も戻す。
                discard: result =>
                {
                    RetireOutput(result.full);
                    result.display?.Release();
                });
        }

        private void ApplyPendingPreview()
        {
            var processed = _pendingProcessedDisplay;
            var raw       = _pendingRawDisplay;
            int w = _pendingPrevW;
            int h = _pendingPrevH;
            bool isFinal = _pendingIsFinal;
            bool isDrag = _pendingIsDrag;
            var latency = _pendingLatency;
            var stage   = _pendingLatencyStage;
            _pendingProcessedDisplay = null;
            _pendingRawDisplay       = null;
            _pendingLatency          = null;
            _pendingLatencyStage     = null;

            if (processed == null || raw == null) return;
            if (stage != null) stage.UploadStart = PreviewLatencyCycle.Now;

            TextureSlot.Resize(ref previewTexture, w, h);
            previewTexture.SetPixels32(processed);
            previewTexture.Apply();

            TextureSlot.Resize(ref rawPreviewTexture, w, h);
            rawPreviewTexture.SetPixels32(raw);
            rawPreviewTexture.Apply();

            var maskView = _host._maskView;
            // オーバーレイ寸法はプレビュー寸法の整数倍(表示倍率に追従。OverlayScale が正)。
            int ovScale = maskView.OverlayScale(w, h, EffectiveZoom);
            int ovW = w * ovScale, ovH = h * ovScale;
            if (maskView.overlayBuiltW != ovW || maskView.overlayBuiltH != ovH || maskView.maskDirty)
            {
                // 非同期スケジュール：結果は次フレームの Draw 冒頭で ApplyPendingOverlay により反映
                maskView.RebuildMaskOverlay(ovW, ovH);
                maskView.maskDirty = false;
            }

            // 拡大表示(詳細クロップ)は確定(フル段)の出力から切り出すので、確定を出したときだけ
            // 作り直させる。プロキシの直後は何もしない(切り出し元のフル段の出力がまだ前の状態のまま)。
            if (isDrag)
            {
                // ドラッグの追従: 古い拡大表示を隠してこのプロキシを見せる。走っている拡大表示のジョブは
                // 古い切り出し元のものなので取り消す(確定の後で作り直す)。
                _detailHiddenForDrag = true;
                _fullOutputFreshSinceHide = false;
                _detailView.detailJob.Cancel();
            }
            if (isFinal)
            {
                // 走っている切り出しは古いフル段の出力のものなので取り消す(色が古い)。
                _detailView.lastDetailDirtyTime = System.Math.Max(double.Epsilon, EditorApplication.timeSinceStartup);
                _detailView.detailJob.Cancel();
            }

            // 体感速度: ここで描いたものがこの OnGUI の終わりに画面へ出る。画面に出るたびに
            // (ドラッグの追従のプロキシも含めて)ここまでのレポートを出す。確定表示(フル)なら
            // 続く拡大表示の再生成も同じ起点で測れるよう引き渡す。
            if (stage != null)
            {
                stage.UploadEnd = PreviewLatencyCycle.Now;
                InputClock.NoteShown();
                if (latency != null)
                {
                    if (ReferenceEquals(stage, latency.Full)) _detailView.AttachLatency(latency);
                    if (PreviewLatency.Publish(latency)) _host.RequestRepaint();
                }
            }
        }
    }
}
