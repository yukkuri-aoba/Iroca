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
    internal partial class PreviewView
    {

        // dragOnly: ドラッグ中の追従。プロキシ段だけを回し(縮小しないテクスチャはフル段がそのまま
        // 軽いのでフル段)、入力を _dragReq に控える。確定は FinishDragPreview が同じ入力で行う。
        private void GeneratePreviewAsync(bool dragOnly = false)
        {
            long prepStart = PreviewLatencyCycle.Now;
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !EnsureTrueSource(sourceTexture)) return;

            int srcW = _trueSourceW;
            int srcH = _trueSourceH;

            // 表示寸法の丸めは ComputeFitSize が単一の正(ハーネスのプレビュー段検証と共有)。
            PixelProcessor.ComputeFitSize(srcW, srcH, IrocaConsts.Preview.MaxSize,
                out int prevW, out int prevH, out float scale);

            Color32[] srcPixels;
            // rawDisplay: 既に確定しているもの(キャッシュヒット or scale>=1 で src と同一)は非 null。
            // キャッシュミス かつ scale<1 のときのみ null にし、ジョブ側で BoxDownsample してから
            // apply でキャッシュへ確定する(メインスレッドのダウンサンプルヒッチを回避)。
            Color32[] rawDisplay;
            if (_cachedSourceTexture == sourceTexture &&
                _cachedSrcPixels != null &&
                _cachedRawDisplay != null &&
                _cachedSrcW == srcW && _cachedSrcH == srcH &&
                _cachedPrevW == prevW && _cachedPrevH == prevH)
            {
                srcPixels = _cachedSrcPixels;
                rawDisplay = _cachedRawDisplay;
            }
            else
            {
                srcPixels = _trueSourcePixels;
                // scale>=1 は縮小不要で raw==src(コスト 0)。scale<1 はジョブ側で生成するため null。
                rawDisplay = scale < 1f ? null : srcPixels;

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
            _selectionCache?.RetainOnly(liveZoneIds);
            _proxySelectionCache?.RetainOnly(liveZoneIds);
            // リストの並び順が優先度。先頭(上)ほど優先で先に処理し、重なりを占有する。
            // ソロ表示中はそのゾーンだけを処理する（★表示専用★。エクスポートは
            // 常に有効ゾーン全部を適用し、ExportView がソロ中である旨を注意表示する）。
            var soloZone = _host.SoloZone;
            var zonesSnapshot = session.zones
                .Where(z => z.enabled && (soloZone == null || ReferenceEquals(z, soloZone)))
                .Select(z => z.Clone())
                .ToList();

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
                maskSnap = maskSnap, zonesSnapshot = zonesSnapshot,
                settings = RecolorSettings.From(session),
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
            public Color32[] srcPixels;
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
        // parityCache は公開しない(詳細プレビューはフル解像度の正確な統計を使い続ける)。
        private void ScheduleProxyPreview(PreviewRequest req, bool chainFull)
        {
            // プロキシ寸法の丸めは ComputeFitSize が単一の正(ハーネスのプレビュー段検証と共有)。
            PixelProcessor.ComputeFitSize(req.srcW, req.srcH, IrocaConsts.Preview.ProxyMaxSize,
                out int proxyW, out int proxyH, out float proxyScale);
            var proxySelCache = _proxySelectionCache;
            var marks = req.latency.Proxy;
            marks.Scheduled = PreviewLatencyCycle.Now;

            _proxyJob.Schedule(
                work: token =>
                {
                    marks.WorkStart = PreviewLatencyCycle.Now;
                    // ソースをプロキシ解像度へ縮小してから処理する(全フェーズが画素数に比例して軽くなる)。
                    // BoxDownsample は新規配列を返すので ProcessPixelsArray の破壊書き換えで clone 不要。
                    Color32[] proxyPixels = PixelProcessor.BoxDownsample(
                        req.srcPixels, req.srcW, req.srcH, proxyW, proxyH, proxyScale);
                    // raw(比較/差分表示の before)は processed と同じリサンプル鎖を通す。
                    // src→表示 の 1 段縮小と src→proxy→表示 の 2 段縮小では箱平均の境界が
                    // 揃わず、再着色していない画素まで差分閾値を超えて誤点灯するため
                    // (実テクスチャで表示画素の ~17%)。処理前のプロキシを控えて同じ鎖へ流す。
                    Color32[] proxyRaw = (Color32[])proxyPixels.Clone();
                    marks.CoreStart = PreviewLatencyCycle.Now;
                    PixelProcessor.ProcessPixelsArray(proxyPixels, proxyW, proxyH, req.maskSnap, req.zonesSnapshot,
                        req.settings, token,
                        debug: null, parityCache: null, selectionCache: proxySelCache);
                    marks.CoreEnd = PreviewLatencyCycle.Now;

                    // 表示寸法へ。ProxyMaxSize==MaxSize なら proxy==表示で再縮小なし(最頻ケース)。
                    bool needsResample = proxyW != req.prevW || proxyH != req.prevH;
                    float toDisplay = req.prevW / (float)proxyW;
                    Color32[] processedDisplay = needsResample
                        ? PixelProcessor.BoxDownsample(proxyPixels, proxyW, proxyH, req.prevW, req.prevH, toDisplay)
                        : proxyPixels;
                    Color32[] rawForJob = needsResample
                        ? PixelProcessor.BoxDownsample(proxyRaw, proxyW, proxyH, req.prevW, req.prevH, toDisplay)
                        : proxyRaw;
                    marks.WorkEnd = PreviewLatencyCycle.Now;
                    return (processedDisplay, rawForJob);
                },
                apply: result =>
                {
                    marks.Applied = PreviewLatencyCycle.Now;
                    _pendingIsFinal          = false;
                    _pendingLatency          = req.latency;
                    _pendingLatencyStage     = marks;
                    _pendingRawDisplay       = result.raw;
                    _pendingProcessedDisplay = result.processed;
                    _pendingPrevW            = req.prevW;
                    _pendingPrevH            = req.prevH;
                    // プロキシは近似。parityCache 公開・raw キャッシュ確定・debug 公開はフル段に委ねる
                    // (詳細プレビューの正確さを死守し、二重管理を避ける)。
                    // シーンのアバターにも概要を映す(ドラッグ中の追従。手を止めるとフル段が置き換える)。
                    LivePreview.Push(req.source, result.processed, req.prevW, req.prevH);
                    _host.RequestRepaint();
                    // 続けてフル解像度で確定(同一スナップショット)。
                    if (chainFull) ScheduleFullPreview(req);
                });
        }

        // 段階的リファイン第2段(=従来のフル解像度処理)。フル解像度で処理→表示解像度へ縮小し、
        // プロキシ表示を確定結果へ差し替える。parityCache を公開して詳細プレビューを一致させる。
        // この経路はフル解像度処理そのままなので出力は段階的リファイン導入前とバイト不変。
        private void ScheduleFullPreview(PreviewRequest req)
        {
            var selCache = _selectionCache;
            var marks = req.latency.Full;
            marks.Scheduled = PreviewLatencyCycle.Now;
            _previewJob.Schedule(
                work: token =>
                {
                    marks.WorkStart = PreviewLatencyCycle.Now;
                    Color32[] pixels = (Color32[])req.srcPixels.Clone();
                    marks.CoreStart = PreviewLatencyCycle.Now;
                    PixelProcessor.ProcessPixelsArray(pixels, req.srcW, req.srcH, req.maskSnap, req.zonesSnapshot,
                        req.settings, token,
                        debug: req.debugCap, selectionCache: selCache);
                    marks.CoreEnd = PreviewLatencyCycle.Now;

                    Color32[] processedDisplay = req.scale < 1f
                        ? PixelProcessor.BoxDownsample(pixels, req.srcW, req.srcH, req.prevW, req.prevH, req.scale)
                        : pixels;
                    // raw が未確定(キャッシュミス & scale<1)ならバックグラウンドで生成する。
                    // それ以外(キャッシュヒット or scale>=1)は確定済みをそのまま使う。
                    Color32[] rawForJob = req.rawDisplay ?? PixelProcessor.BoxDownsample(
                        req.srcPixels, req.srcW, req.srcH, req.prevW, req.prevH, req.scale);
                    marks.WorkEnd = PreviewLatencyCycle.Now;
                    return (processedDisplay, rawForJob, pixels);
                },
                apply: result =>
                {
                    marks.Applied = PreviewLatencyCycle.Now;
                    // AI 提案コミットの E2E 計測(アーム中のみ 1 回ログ)。実際の画面反映は
                    // 次フレームの ApplyPendingPreview だが、差は 1 フレームなので近似で計上。
                    MaskSuggestPerf.NotifyFullPreviewApplied();
                    _pendingIsFinal          = true;
                    _pendingLatency          = req.latency;
                    _pendingLatencyStage     = marks;
                    _pendingRawDisplay       = result.raw;
                    _pendingProcessedDisplay = result.processed;
                    _pendingPrevW            = req.prevW;
                    _pendingPrevH            = req.prevH;
                    // フル解像度の結果を保持する。拡大表示(詳細クロップ)はここから切り出すだけなので、
                    // 書き出しと同じ計算結果そのものになる(切り出しを計算し直さない)。
                    _fullOutput       = result.full;
                    _fullOutputSource = req.srcPixels;
                    // ジョブ側で生成した raw をキャッシュへ確定する(まだ未確定で、対象テクスチャと
                    // 寸法が変わっていない場合のみ。新しいミスで上書きされていれば触らない)。
                    if (_cachedRawDisplay == null && _cachedSrcPixels == req.srcPixels &&
                        _cachedSrcW == req.srcW && _cachedSrcH == req.srcH &&
                        _cachedPrevW == req.prevW && _cachedPrevH == req.prevH)
                    {
                        _cachedRawDisplay = result.raw;
                    }
                    // シーンのアバターにはフル解像度の結果を映す(取り込み済みテクスチャの寸法へは LivePreview が合わせる)。
                    LivePreview.Push(req.source, result.full, req.srcW, req.srcH);
                    _host.LatestDebugCapture = req.debugCap;
                    // 充填完了したキャプチャを（メインスレッドの）ここで初めて公開する。
                    // 生成時に公開すると、DebugWindow が Add 中のリストを foreach して競合する。
                    if (req.debugCap != null)
                        DebugCaptureHooks.RaiseCaptureComplete(req.debugCap);
                    _host.RequestRepaint();
                });
        }

        private void ApplyPendingPreview()
        {
            var processed = _pendingProcessedDisplay;
            var raw       = _pendingRawDisplay;
            int w = _pendingPrevW;
            int h = _pendingPrevH;
            bool isFinal = _pendingIsFinal;
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

            // Color32[] が手元にあるのでそのままバックグラウンドへ。GetPixels32 を再度呼ばない。
            ScheduleDiffTexture(raw, processed, w, h);

            var maskView = _host._maskView;
            // オーバーレイ寸法はプレビュー寸法の整数倍(表示倍率に追従。OverlayScale が正)。
            int ovScale = maskView.OverlayScale(w, h, previewZoom);
            int ovW = w * ovScale, ovH = h * ovScale;
            if (maskView.overlayBuiltW != ovW || maskView.overlayBuiltH != ovH || maskView.maskDirty)
            {
                // 非同期スケジュール：結果は次フレームの Draw 冒頭で ApplyPendingOverlay により反映
                maskView.RebuildMaskOverlay(ovW, ovH);
                maskView.maskDirty = false;
            }

            // 拡大表示(詳細クロップ)は確定(フル段)の出力から切り出すので、確定を出したときだけ
            // 作り直させる。待たない: 詳細の 0.3 秒の待ちはスクロール・ズーム中の作り直しを間引く
            // ためのもの。プロキシの直後は何もしない(切り出し元のフル段の出力がまだ前の状態のまま)。
            if (isFinal)
            {
                double now = EditorApplication.timeSinceStartup;
                _detailView.lastDetailDirtyTime =
                    System.Math.Max(double.Epsilon, now - DetailPreviewView.DetailDebounceSeconds);
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

        /// <summary>
        /// Before/After の Color32 配列から差分ハイライトをバックグラウンドで生成する。
        /// 結果は <see cref="ApplyPendingDiff"/> で次フレーム以降にテクスチャへ反映される。
        /// </summary>
        private void ScheduleDiffTexture(Color32[] before, Color32[] after, int w, int h)
        {
            if (before == null || after == null || before.Length != w * h || after.Length != w * h) return;
            int capW = w, capH = h;
            _diffJob.Schedule(
                work: token => BuildDiffPixels(before, after, capW, capH, token),
                apply: result =>
                {
                    _pendingDiffPixels = result;
                    _pendingDiffW = capW;
                    _pendingDiffH = capH;
                    _host.RequestRepaint();
                });
        }

        private static Color32[] BuildDiffPixels(Color32[] a, Color32[] b, int w, int h, CancellationToken token)
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
            if (_pendingDiffPixels == null) return;
            var pixels = _pendingDiffPixels;
            int w = _pendingDiffW, h = _pendingDiffH;
            _pendingDiffPixels = null;

            TextureSlot.Resize(ref diffTexture, w, h);
            diffTexture.SetPixels32(pixels);
            diffTexture.Apply();
        }
    }
}
