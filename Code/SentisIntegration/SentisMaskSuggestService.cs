// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
using System.Collections.Generic;
using Unity.Sentis;
using UnityEditor;
using UnityEngine;

namespace Iroca.SentisIntegration
{
    /// <summary>
    /// IMaskSuggestService の Sentis 実装。
    ///
    /// 実行モデル(メインスレッド規約は IMaskSuggestService 参照):
    ///   - 前処理(リサイズ/正規化)と後処理(拡大/二値化)は PreviewJob で BG スレッド実行
    ///   - エンコーダ推論は Worker.ScheduleIterable を EditorApplication.update から
    ///     時間予算で pump(GPU ディスパッチはメインスレッド必須のため)
    ///   - デコーダ推論は数十 ms なのでメインスレッド同期実行
    ///   - 埋め込み(4.2MB float[])はテクスチャキー単位で LRU キャッシュ
    ///   - GPUCompute で失敗したら CPU バックエンドへ自動フォールバック(1 回)
    /// </summary>
    internal sealed class SentisMaskSuggestService : IMaskSuggestService
    {
        const int EmbeddingCacheCapacity = 2;
        // クロップ埋め込み(ズームイン再推論用)。1 件 4.2MB × 4 = 約 17MB。
        // 近接クリックは SamZoomOps の矩形グリッドスナップで同一キーに揃う。
        const int CropEmbeddingCacheCapacity = 4;
        const int EmbeddingLength = 1 * 256 * 64 * 64;

        // ─── モデル/ワーカー ───
        Model _encoderModel, _decoderModel;
        Worker _encoder, _decoder;
        BackendType _backend;
        bool _modelsLoaded;
        bool _triedCpuFallback;

        // ─── 状態 ───
        MaskSuggestPhase _phase = MaskSuggestPhase.NoModel;
        float _progress;
        string _error;
        public event Action StateChanged;

        // ─── ソース/埋め込み ───
        string _sourceKey;
        Color32[] _sourcePixels;
        int _texW, _texH;
        float[] _embedding;                         // 現テクスチャの埋め込み(null=未計算)
        readonly Dictionary<string, float[]> _embeddingCache = new Dictionary<string, float[]>();
        readonly List<string> _lruOrder = new List<string>();

        // ─── 進行中の処理 ───
        readonly EditorIteratorPump _pump = new EditorIteratorPump();
        PreviewJob<float[]> _prepJob;
        PreviewJob<PostOutcome> _postJob;
        Tensor<float> _encInput;                    // pump 中だけ保持
        MaskSuggestProposal _proposal;
        bool _hasPendingClick;
        float _pendingU, _pendingV;
        MaskSuggestGranularity _pendingGranularity;

        // ─── クロップ埋め込み(ズームイン再推論) ───
        readonly Dictionary<string, float[]> _cropEmbeddingCache = new Dictionary<string, float[]>();
        readonly List<string> _cropLruOrder = new List<string>();

        /// <summary>後処理ジョブの結果。第 1 段では提案ペイロード+ズーム計画、
        /// ズーム後処理では提案ペイロードのみ(hasCrop=false)。</summary>
        sealed class PostOutcome
        {
            public bool[] mask;
            public float score;
            public float areaFrac;
            public bool floodWarning;
            // ズーム計画(hasCrop=true のときのみ有効。下原点矩形)
            public bool hasCrop;
            public int cropX0, cropY0, cropSide;
            public Color32[] cropPixels;
        }

        public SentisMaskSuggestService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
        }

        public MaskSuggestPhase Phase => _phase;
        public float Progress => _progress;
        public string ErrorMessage => _error;

        void SetPhase(MaskSuggestPhase phase, float progress = 0f, string error = null)
        {
            _phase = phase;
            _progress = progress;
            _error = error;
            StateChanged?.Invoke();
        }

        // ───────────────────────── モデルロード ─────────────────────────
        public bool TryEnsureModels()
        {
            if (_modelsLoaded) return true;
            // 共有フォルダ化(80d1000)以前に旧プロジェクト内へ置いたモデルを、初回だけ共有先へ引き継ぐ。
            MaskSuggestBridge.MigrateLegacyModelsIfNeeded();
            if (!SentisModelRepository.ModelsPresent())
            {
                if (_phase != MaskSuggestPhase.NoModel) SetPhase(MaskSuggestPhase.NoModel);
                return false;
            }
            // 初回は ONNX→.sentis 変換込みで数秒かかる(2 回目以降はキャッシュで高速)。
            SetPhase(MaskSuggestPhase.LoadingModel);
            _encoderModel = SentisModelRepository.LoadOrConvert(
                SentisModelRepository.EncoderOnnxPath, out string encErr);
            if (_encoderModel == null)
            {
                SetPhase(MaskSuggestPhase.Error, error: encErr);
                return false;
            }
            _decoderModel = SentisModelRepository.LoadOrConvert(
                SentisModelRepository.DecoderOnnxPath, out string decErr);
            if (_decoderModel == null)
            {
                SetPhase(MaskSuggestPhase.Error, error: decErr);
                return false;
            }
            _backend = SystemInfo.supportsComputeShaders ? BackendType.GPUCompute : BackendType.CPU;
            if (!TryCreateWorkers(out string werr))
            {
                SetPhase(MaskSuggestPhase.Error, error: werr);
                return false;
            }
            _modelsLoaded = true;
            SetPhase(MaskSuggestPhase.Idle);
            return true;
        }

        bool TryCreateWorkers(out string error)
        {
            error = null;
            try
            {
                DisposeWorkers();
                _encoder = new Worker(_encoderModel, _backend);
                _decoder = new Worker(_decoderModel, _backend);
                return true;
            }
            catch (Exception e)
            {
                error = $"推論ワーカーの作成に失敗しました({_backend}): {e.Message}";
                return false;
            }
        }

        // ───────────────────────── ソース設定/エンコード ─────────────────────────
        public void SetSource(string cacheKey, Color32[] pixelsBottomUp, int width, int height)
        {
            if (!_modelsLoaded) return;
            if (cacheKey == _sourceKey &&
                (_embedding != null || _phase == MaskSuggestPhase.Encoding))
                return; // 同一ソースで計算済み/計算中

            CancelOps();
            _sourceKey = cacheKey;
            _sourcePixels = pixelsBottomUp;
            _texW = width;
            _texH = height;
            _embedding = null;

            if (_embeddingCache.TryGetValue(cacheKey, out var cached))
            {
                _embedding = cached;
                TouchLru(cacheKey);
                SetPhase(MaskSuggestPhase.Idle);
                return;
            }
            StartEncode();
        }

        void StartEncode()
        {
            SetPhase(MaskSuggestPhase.Encoding);
            var px = _sourcePixels;
            int w = _texW, h = _texH;
            _prepJob ??= new PreviewJob<float[]>();
            _prepJob.Schedule(
                ct => SamImageOps.BuildEncoderInput(px, w, h),
                chw => StartEncoderPump(chw),
                e => SetPhase(MaskSuggestPhase.Error, error: $"前処理に失敗しました: {e.Message}"));
        }

        void StartEncoderPump(float[] chw)
        {
            if (_phase != MaskSuggestPhase.Encoding) return; // キャンセル済み
            try
            {
                _encInput = new Tensor<float>(
                    new TensorShape(1, 3, SamImageOps.InputSize, SamImageOps.InputSize), chw);
                var it = _encoder.ScheduleIterable(_encInput);
                int total = _encoderModel.layers != null ? _encoderModel.layers.Count : 0;
                _pump.Start(it, total,
                    onDone: FinishEncode,
                    onError: OnEncoderError,
                    onProgress: p => { _progress = p; StateChanged?.Invoke(); });
            }
            catch (Exception e)
            {
                OnEncoderError(e);
            }
        }

        void FinishEncode()
        {
            try
            {
                using (var output = (_encoder.PeekOutput() as Tensor<float>).ReadbackAndClone())
                {
                    _embedding = output.DownloadToArray();
                }
                DisposeEncInput();
                if (_embedding == null || _embedding.Length != EmbeddingLength)
                    throw new InvalidOperationException(
                        $"埋め込みサイズが不正です: {_embedding?.Length ?? 0}");
                CacheEmbedding(_sourceKey, _embedding);
                _triedCpuFallback = false;
                if (_hasPendingClick)
                {
                    _hasPendingClick = false;
                    RunDecode(_pendingU, _pendingV, _pendingGranularity);
                }
                else
                {
                    SetPhase(MaskSuggestPhase.Idle);
                }
            }
            catch (Exception e)
            {
                OnEncoderError(e);
            }
        }

        void OnEncoderError(Exception e)
        {
            DisposeEncInput();
            // GPU 固有の失敗(ドライバ/シェーダ)に備えて CPU で 1 回だけ再試行する
            if (_backend == BackendType.GPUCompute && !_triedCpuFallback)
            {
                Debug.LogWarning($"[Iroca] GPUCompute でのエンコードに失敗したため CPU で再試行します: {e.Message}");
                _triedCpuFallback = true;
                _backend = BackendType.CPU;
                if (TryCreateWorkers(out string werr))
                {
                    StartEncode();
                    return;
                }
                SetPhase(MaskSuggestPhase.Error, error: werr);
                return;
            }
            SetPhase(MaskSuggestPhase.Error, error: $"画像の解析に失敗しました: {e.Message}");
        }

        // ───────────────────────── クリック → 提案 ─────────────────────────
        public void RequestProposal(float u, float v, MaskSuggestGranularity granularity)
        {
            if (!_modelsLoaded) return;
            switch (_phase)
            {
                case MaskSuggestPhase.Encoding:
                case MaskSuggestPhase.Decoding:
                    _hasPendingClick = true; // 最新クリックだけ残す
                    _pendingU = u;
                    _pendingV = v;
                    _pendingGranularity = granularity;
                    return;
                case MaskSuggestPhase.Idle:
                case MaskSuggestPhase.ProposalReady:
                    if (_embedding == null) return;
                    RunDecode(u, v, granularity);
                    return;
                default:
                    return;
            }
        }

        void RunDecode(float u, float v, MaskSuggestGranularity granularity)
        {
            SetPhase(MaskSuggestPhase.Decoding);
            if (!TryRunDecoderCore(u, v, cropRect: null, out float[] logits, out float[] scores,
                                   out Exception decErr))
            {
                SetPhase(MaskSuggestPhase.Error, error: $"提案の推論に失敗しました: {decErr.Message}");
                return;
            }

            int w = _texW, h = _texH;
            var px = _sourcePixels;
            _postJob ??= new PreviewJob<PostOutcome>();
            _postJob.Schedule(
                ct =>
                {
                    var s1 = SamMaskPostprocess.SelectAndUpscale(
                        logits, scores, w, h, pixelsBottomUp: px, granularity: granularity);
                    var o = new PostOutcome
                    {
                        mask = s1.maskBottomUp,
                        score = s1.score,
                        areaFrac = s1.areaFrac,
                        floodWarning = s1.floodWarning,
                    };
                    // ズームイン再推論の判定: クリック成分が小さい(=256²ロジットで形状表現
                    // できない)場合のみ、クリック周辺クロップの再推論計画を積む。
                    int cx = Mathf.Clamp((int)(u * w), 0, w - 1);
                    int cy = Mathf.Clamp((int)(v * h), 0, h - 1); // v は下原点 → 下原点行と一致
                    int bb = SamZoomOps.ClickComponentBBoxLong(s1.maskBottomUp, w, h, cx, cy);
                    if (SamZoomOps.TryDeriveCropRect(bb, cx, cy, w, h,
                                                     out int x0, out int y0, out int side))
                    {
                        o.hasCrop = true;
                        o.cropX0 = x0;
                        o.cropY0 = y0;
                        o.cropSide = side;
                        o.cropPixels = SamZoomOps.ExtractCrop(px, w, h, x0, y0, side);
                    }
                    return o;
                },
                o =>
                {
                    if (_phase != MaskSuggestPhase.Decoding) return; // キャンセル済み
                    if (o.hasCrop) StartZoomStage(o, u, v, granularity);
                    else DeliverProposal(o);
                },
                e => SetPhase(MaskSuggestPhase.Error, error: $"提案の生成に失敗しました: {e.Message}"));
        }

        /// <summary>デコーダ同期実行(数十 ms)。cropRect が null なら全体埋め込み+全体座標系、
        /// 指定ありなら渡されたクロップ埋め込み+クロップ座標系で推論する。</summary>
        bool TryRunDecoderCore(float u, float v, (int x0, int y0, int side, float[] emb)? cropRect,
                               out float[] logits, out float[] scores, out Exception error)
        {
            logits = scores = null;
            error = null;
            try
            {
                float x, y;
                float[] embedding;
                if (cropRect.HasValue)
                {
                    var c = cropRect.Value;
                    SamCoordMapper.UvToCrop1024(u, v, _texW, _texH, c.x0, c.y0, c.side, out x, out y);
                    embedding = c.emb;
                }
                else
                {
                    SamCoordMapper.UvTo1024(u, v, _texW, _texH, out x, out y);
                    embedding = _embedding;
                }
                using var emb = new Tensor<float>(new TensorShape(1, 256, 64, 64), embedding);
                using var pts = new Tensor<float>(new TensorShape(1, 2, 2), new[] { x, y, 0f, 0f });
                using var lbl = new Tensor<float>(new TensorShape(1, 2), new[] { 1f, -1f });
                _decoder.Schedule(emb, pts, lbl);
                using var lo = (_decoder.PeekOutput("low_res_logits") as Tensor<float>).ReadbackAndClone();
                using var sc = (_decoder.PeekOutput("iou_predictions") as Tensor<float>).ReadbackAndClone();
                logits = lo.DownloadToArray();
                scores = sc.DownloadToArray();
                return true;
            }
            catch (Exception e)
            {
                error = e;
                return false;
            }
        }

        /// <summary>提案を確定し、保留クリックがあれば取り直す。</summary>
        void DeliverProposal(PostOutcome o)
        {
            _proposal = new MaskSuggestProposal
            {
                maskBottomUp = o.mask,
                width = _texW,
                height = _texH,
                score = o.score,
                areaFrac = o.areaFrac,
                floodWarning = o.floodWarning,
            };
            if (_hasPendingClick)
            {
                // 提案表示前に次クリックが来ていたら差し替え(取り直し)
                _hasPendingClick = false;
                RunDecode(_pendingU, _pendingV, _pendingGranularity);
                return;
            }
            SetPhase(MaskSuggestPhase.ProposalReady);
        }

        // ───────────────────────── ズームイン再推論(第 2 段) ─────────────────────────
        // 小パーツはクリック周辺クロップを再エンコード・再デコードして実効解像度を上げる
        // (計測: dev_safe/ml/zoom_infer_spike2.py。バンダナ三角 IoU 0.04-0.10 → 0.94-0.97)。
        // 失敗時は第 1 段の提案へグレースフルに退避し、エラー状態にはしない。

        void StartZoomStage(PostOutcome plan, float u, float v, MaskSuggestGranularity granularity)
        {
            string key = $"{_sourceKey}|{plan.cropX0},{plan.cropY0},{plan.cropSide}";
            if (_cropEmbeddingCache.TryGetValue(key, out var cached))
            {
                TouchCropLru(key);
                RunZoomDecode(cached, plan, u, v, granularity);
                return;
            }
            var cropPx = plan.cropPixels;
            int side = plan.cropSide;
            _prepJob ??= new PreviewJob<float[]>();
            _prepJob.Schedule(
                ct => SamImageOps.BuildEncoderInput(cropPx, side, side),
                chw => StartZoomEncoderPump(chw, key, plan, u, v, granularity),
                e => FallbackToStage1(plan, e));
        }

        void StartZoomEncoderPump(float[] chw, string key, PostOutcome plan,
                                  float u, float v, MaskSuggestGranularity granularity)
        {
            if (_phase != MaskSuggestPhase.Decoding) return; // キャンセル済み
            try
            {
                _encInput = new Tensor<float>(
                    new TensorShape(1, 3, SamImageOps.InputSize, SamImageOps.InputSize), chw);
                var it = _encoder.ScheduleIterable(_encInput);
                int total = _encoderModel.layers != null ? _encoderModel.layers.Count : 0;
                _pump.Start(it, total,
                    onDone: () => FinishZoomEncode(key, plan, u, v, granularity),
                    onError: e => { DisposeEncInput(); FallbackToStage1(plan, e); },
                    onProgress: p => { _progress = p; StateChanged?.Invoke(); });
            }
            catch (Exception e)
            {
                DisposeEncInput();
                FallbackToStage1(plan, e);
            }
        }

        void FinishZoomEncode(string key, PostOutcome plan,
                              float u, float v, MaskSuggestGranularity granularity)
        {
            float[] emb;
            try
            {
                using (var output = (_encoder.PeekOutput() as Tensor<float>).ReadbackAndClone())
                {
                    emb = output.DownloadToArray();
                }
                DisposeEncInput();
                if (emb == null || emb.Length != EmbeddingLength)
                    throw new InvalidOperationException(
                        $"クロップ埋め込みサイズが不正です: {emb?.Length ?? 0}");
            }
            catch (Exception e)
            {
                DisposeEncInput();
                FallbackToStage1(plan, e);
                return;
            }
            CacheCropEmbedding(key, emb);
            RunZoomDecode(emb, plan, u, v, granularity);
        }

        void RunZoomDecode(float[] cropEmbedding, PostOutcome plan,
                           float u, float v, MaskSuggestGranularity granularity)
        {
            if (_phase != MaskSuggestPhase.Decoding) return; // キャンセル済み
            if (!TryRunDecoderCore(u, v, (plan.cropX0, plan.cropY0, plan.cropSide, cropEmbedding),
                                   out float[] logits, out float[] scores, out Exception decErr))
            {
                FallbackToStage1(plan, decErr);
                return;
            }
            int w = _texW, h = _texH;
            int x0 = plan.cropX0, y0 = plan.cropY0, side = plan.cropSide;
            var cropPx = plan.cropPixels;
            _postJob.Schedule(
                ct =>
                {
                    var res = SamMaskPostprocess.SelectAndUpscale(
                        logits, scores, side, side, pixelsBottomUp: cropPx, granularity: granularity);
                    var full = SamZoomOps.PasteCrop(res.maskBottomUp, side, w, h, x0, y0,
                                                    out int trueCount);
                    return new PostOutcome
                    {
                        mask = full,
                        score = res.score,
                        areaFrac = trueCount / (float)(w * h),
                        floodWarning = res.floodWarning,
                    };
                },
                o =>
                {
                    if (_phase != MaskSuggestPhase.Decoding) return; // キャンセル済み
                    DeliverProposal(o);
                },
                e => SetPhase(MaskSuggestPhase.Error, error: $"提案の生成に失敗しました: {e.Message}"));
        }

        void FallbackToStage1(PostOutcome plan, Exception e)
        {
            if (_phase != MaskSuggestPhase.Decoding) return; // キャンセル済み
            if (e != null)
                Debug.LogWarning($"[Iroca] ズームイン再推論に失敗したため全体推論の提案を表示します: {e.Message}");
            DeliverProposal(plan);
        }

        void CacheCropEmbedding(string key, float[] embedding)
        {
            if (string.IsNullOrEmpty(key)) return;
            _cropEmbeddingCache[key] = embedding;
            TouchCropLru(key);
            while (_cropLruOrder.Count > CropEmbeddingCacheCapacity)
            {
                string evict = _cropLruOrder[0];
                _cropLruOrder.RemoveAt(0);
                _cropEmbeddingCache.Remove(evict);
            }
        }

        void TouchCropLru(string key)
        {
            _cropLruOrder.Remove(key);
            _cropLruOrder.Add(key);
        }

        public bool TryTakeProposal(out MaskSuggestProposal proposal)
        {
            proposal = _proposal;
            if (_phase != MaskSuggestPhase.ProposalReady || proposal == null) return false;
            _proposal = null;
            SetPhase(MaskSuggestPhase.Idle);
            return true;
        }

        // ───────────────────────── キャンセル/破棄 ─────────────────────────
        public void CancelAll()
        {
            CancelOps();
            _sourceKey = null;
            _sourcePixels = null;
            _embedding = null;
            if (_modelsLoaded && _phase != MaskSuggestPhase.Error)
                SetPhase(MaskSuggestPhase.Idle);
        }

        void CancelOps()
        {
            _pump.Stop();
            _prepJob?.Cancel();
            _postJob?.Cancel();
            DisposeEncInput();
            _proposal = null;
            _hasPendingClick = false;
        }

        void CacheEmbedding(string key, float[] embedding)
        {
            if (string.IsNullOrEmpty(key)) return;
            _embeddingCache[key] = embedding;
            TouchLru(key);
            while (_lruOrder.Count > EmbeddingCacheCapacity)
            {
                string evict = _lruOrder[0];
                _lruOrder.RemoveAt(0);
                _embeddingCache.Remove(evict);
            }
        }

        void TouchLru(string key)
        {
            _lruOrder.Remove(key);
            _lruOrder.Add(key);
        }

        void DisposeEncInput()
        {
            _encInput?.Dispose();
            _encInput = null;
        }

        void DisposeWorkers()
        {
            _encoder?.Dispose(); _encoder = null;
            _decoder?.Dispose(); _decoder = null;
        }

        void DisposeAll()
        {
            CancelOps();
            _prepJob?.Dispose(); _prepJob = null;
            _postJob?.Dispose(); _postJob = null;
            DisposeWorkers();
        }
    }
}
#endif
