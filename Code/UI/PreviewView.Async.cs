// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: 非同期プレビュー生成(プロキシ/フル解像度ジョブ・pending 適用・diff 生成)。
    internal partial class PreviewView
    {
        // ───────────────────────── Preview Async Generation ─────────────────────────

        private void GeneratePreviewAsync()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !EnsureTrueSource(sourceTexture)) return;

            int srcW = _trueSourceW;
            int srcH = _trueSourceH;

            float scale = 1f;
            if (srcW > IrocaConsts.Preview.MaxSize || srcH > IrocaConsts.Preview.MaxSize)
                scale = IrocaConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH);
            int prevW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int prevH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

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
            // リストの並び順が優先度。先頭(上)ほど優先で先に処理し、重なりを占有する。
            var zonesSnapshot = session.zones
                .Where(z => z.enabled)
                .Select(z => z.Clone())
                .ToList();

            // Debug capture: Code.Debug/ asmdef があり、かつ DebugView でトグル ON のときだけ
            // Factory が非 null インスタンスを返す。それ以外は null で、本体は何もキャプチャしない。
            IDebugCapture debugCap = DebugCaptureHooks.Factory?.Invoke();

            // 入力スナップショットを 1 回だけ構築し、プロキシ(概要)とフル(確定)の両段へ渡す。
            // 両段が同一入力を処理することを保証する(プロキシとフルで選択がズレないように)。
            var req = new PreviewRequest
            {
                srcW = srcW, srcH = srcH,
                srcPixels = srcPixels, rawDisplay = rawDisplay,
                scale = scale, prevW = prevW, prevH = prevH,
                maskSnap = maskSnap, zonesSnapshot = zonesSnapshot,
                feather = session.edgeFeather, aaCleanup = session.antiAliasCleanup,
                hfPasses = session.holeFillPasses, hfMinNeighbors = session.holeFillMinNeighbors,
                rSatMin = session.relaxedSatMin, rSatRamp = session.relaxedSatRamp,
                useDecontam = session.useDecontamination, decontamRadius = session.decontaminationRadius,
                debugCap = debugCap,
                // 連続領域モードの keep と再着色アンカー/wash/領域L統計をフル画像で解いて公開する
                // (詳細プレビューが転写して出力色まで一致させる)。プロキシ段は公開しない。
                // sourceId を刻んでおき、詳細側が「同寸法の別テクスチャ」を取り違えないようにする。
                parityCache = new PreviewParityCache { sourceId = sourceTexture.GetInstanceID() },
            };

            // 段階的リファイン: ソースが縮小される(scale<1)ときだけ、まず低解像度プロキシで概要を
            // 即表示し、続けてフル解像度で確定する。scale>=1(ソースが既に小さい)ではプロキシの利得が
            // 無いので従来どおりフルのみ走らせる。
            if (scale < 1f)
                ScheduleProxyPreview(req);
            else
                ScheduleFullPreview(req);
        }

        // 段階的リファインの入力スナップショット。GeneratePreviewAsync が 1 回構築し、プロキシ段と
        // フル段が同一の値を処理する。フィールドはバックグラウンドジョブからの読み取り専用(不変)。
        private sealed class PreviewRequest
        {
            public int srcW, srcH;
            public Color32[] srcPixels;
            public Color32[] rawDisplay;       // null=ジョブ側で BoxDownsample して確定
            public float scale;
            public int prevW, prevH;
            public MaskSnapshot maskSnap;
            public System.Collections.Generic.List<ColorZone> zonesSnapshot;
            public float feather; public int aaCleanup;
            public int hfPasses, hfMinNeighbors;
            public float rSatMin, rSatRamp;
            public bool useDecontam; public int decontamRadius;
            public IDebugCapture debugCap;
            public PreviewParityCache parityCache;
        }

        // 段階的リファイン第1段。ソースを ProxyMaxSize へ縮小してから処理し、概要を即表示する。
        // 完了 apply でフル段(ScheduleFullPreview)を同一スナップショットでスケジュールする(直列)。
        // parityCache は公開しない(詳細プレビューはフル解像度の正確な統計を使い続ける)。
        private void ScheduleProxyPreview(PreviewRequest req)
        {
            int srcLong = Mathf.Max(req.srcW, req.srcH);
            float proxyScale = IrocaConsts.Preview.ProxyMaxSize >= srcLong
                ? 1f : IrocaConsts.Preview.ProxyMaxSize / (float)srcLong;
            int proxyW = Mathf.Max(1, Mathf.RoundToInt(req.srcW * proxyScale));
            int proxyH = Mathf.Max(1, Mathf.RoundToInt(req.srcH * proxyScale));
            var proxySelCache = _proxySelectionCache;

            _proxyJob.Schedule(
                work: token =>
                {
                    // ソースをプロキシ解像度へ縮小してから処理する(全フェーズが画素数に比例して軽くなる)。
                    // BoxDownsample は新規配列を返すので ProcessPixelsArray の破壊書き換えで clone 不要。
                    Color32[] proxyPixels = PixelProcessor.BoxDownsample(
                        req.srcPixels, req.srcW, req.srcH, proxyW, proxyH, proxyScale);
                    PixelProcessor.ProcessPixelsArray(proxyPixels, proxyW, proxyH, req.maskSnap, req.zonesSnapshot,
                        req.feather, req.aaCleanup, req.hfPasses, req.hfMinNeighbors, req.rSatMin, req.rSatRamp,
                        0, 0, 0, 0, token,
                        req.useDecontam, req.decontamRadius,
                        debug: null, parityCache: null, selectionCache: proxySelCache);

                    // 表示寸法へ。ProxyMaxSize==MaxSize なら proxy==表示で再縮小なし(最頻ケース)。
                    Color32[] processedDisplay = (proxyW != req.prevW || proxyH != req.prevH)
                        ? PixelProcessor.BoxDownsample(proxyPixels, proxyW, proxyH, req.prevW, req.prevH,
                            req.prevW / (float)proxyW)
                        : proxyPixels;
                    // raw(比較表示用の縮小済み元画像)は表示解像度・ソース由来。確定済みならそれを使う。
                    Color32[] rawForJob = req.rawDisplay ?? PixelProcessor.BoxDownsample(
                        req.srcPixels, req.srcW, req.srcH, req.prevW, req.prevH, req.scale);
                    return (processedDisplay, rawForJob);
                },
                apply: result =>
                {
                    _pendingRawDisplay       = result.raw;
                    _pendingProcessedDisplay = result.processed;
                    _pendingPrevW            = req.prevW;
                    _pendingPrevH            = req.prevH;
                    // プロキシは近似。parityCache 公開・raw キャッシュ確定・debug 公開はフル段に委ねる
                    // (詳細プレビューの正確さを死守し、二重管理を避ける)。
                    _host.RequestRepaint();
                    // 続けてフル解像度で確定(同一スナップショット)。
                    ScheduleFullPreview(req);
                });
        }

        // 段階的リファイン第2段(=従来のフル解像度処理)。フル解像度で処理→表示解像度へ縮小し、
        // プロキシ表示を確定結果へ差し替える。parityCache を公開して詳細プレビューを一致させる。
        // この経路はフル解像度処理そのままなので出力は段階的リファイン導入前とバイト不変。
        private void ScheduleFullPreview(PreviewRequest req)
        {
            var selCache = _selectionCache;
            _previewJob.Schedule(
                work: token =>
                {
                    Color32[] pixels = (Color32[])req.srcPixels.Clone();
                    PixelProcessor.ProcessPixelsArray(pixels, req.srcW, req.srcH, req.maskSnap, req.zonesSnapshot,
                        req.feather, req.aaCleanup, req.hfPasses, req.hfMinNeighbors, req.rSatMin, req.rSatRamp,
                        0, 0, 0, 0, token,
                        req.useDecontam, req.decontamRadius,
                        debug: req.debugCap, parityCache: req.parityCache, selectionCache: selCache);

                    Color32[] processedDisplay = req.scale < 1f
                        ? PixelProcessor.BoxDownsample(pixels, req.srcW, req.srcH, req.prevW, req.prevH, req.scale)
                        : pixels;
                    // raw が未確定(キャッシュミス & scale<1)ならバックグラウンドで生成する。
                    // それ以外(キャッシュヒット or scale>=1)は確定済みをそのまま使う。
                    Color32[] rawForJob = req.rawDisplay ?? PixelProcessor.BoxDownsample(
                        req.srcPixels, req.srcW, req.srcH, req.prevW, req.prevH, req.scale);
                    return (processedDisplay, rawForJob);
                },
                apply: result =>
                {
                    _pendingRawDisplay       = result.raw;
                    _pendingProcessedDisplay = result.processed;
                    _pendingPrevW            = req.prevW;
                    _pendingPrevH            = req.prevH;
                    // フル画像で解いた keep と領域統計を公開(以降は不変として詳細プレビューが参照)。
                    _host.previewParityCache = req.parityCache;
                    // ジョブ側で生成した raw をキャッシュへ確定する(まだ未確定で、対象テクスチャと
                    // 寸法が変わっていない場合のみ。新しいミスで上書きされていれば触らない)。
                    if (_cachedRawDisplay == null && _cachedSrcPixels == req.srcPixels &&
                        _cachedSrcW == req.srcW && _cachedSrcH == req.srcH &&
                        _cachedPrevW == req.prevW && _cachedPrevH == req.prevH)
                    {
                        _cachedRawDisplay = result.raw;
                    }
                    _host.LatestDebugCapture = req.debugCap;
                    _host.RequestRepaint();
                });
        }

        private void ApplyPendingPreview()
        {
            var processed = _pendingProcessedDisplay;
            var raw       = _pendingRawDisplay;
            int w = _pendingPrevW;
            int h = _pendingPrevH;
            _pendingProcessedDisplay = null;
            _pendingRawDisplay       = null;

            if (processed == null || raw == null) return;

            TextureSlot.Resize(ref previewTexture, w, h);
            previewTexture.SetPixels32(processed);
            previewTexture.Apply();

            TextureSlot.Resize(ref rawPreviewTexture, w, h);
            rawPreviewTexture.SetPixels32(raw);
            rawPreviewTexture.Apply();

            // Color32[] が手元にあるのでそのままバックグラウンドへ。GetPixels32 を再度呼ばない。
            ScheduleDiffTexture(raw, processed, w, h);

            var maskView = _host._maskView;
            if (maskView.maskOverlayTexture == null
                || maskView.maskOverlayTexture.width != w
                || maskView.maskOverlayTexture.height != h
                || maskView.maskDirty)
            {
                // 非同期スケジュール：結果は次フレームの Draw 冒頭で ApplyPendingOverlay により反映
                maskView.RebuildMaskOverlay(w, h);
                maskView.maskDirty = false;
            }

            // Invalidate detail preview so it regenerates at the new crop
            _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
            _detailView.detailJob.Cancel();
        }

        // ───────────────────────── Diff Texture（非同期） ─────────────────────────

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
