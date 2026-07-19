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
        PreviewJob<SamMaskPostprocess.Result> _postJob;
        Tensor<float> _encInput;                    // pump 中だけ保持
        MaskSuggestProposal _proposal;
        bool _hasPendingClick;
        float _pendingU, _pendingV;

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
                    RunDecode(_pendingU, _pendingV);
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
        public void RequestProposal(float u, float v)
        {
            if (!_modelsLoaded) return;
            switch (_phase)
            {
                case MaskSuggestPhase.Encoding:
                case MaskSuggestPhase.Decoding:
                    _hasPendingClick = true; // 最新クリックだけ残す
                    _pendingU = u;
                    _pendingV = v;
                    return;
                case MaskSuggestPhase.Idle:
                case MaskSuggestPhase.ProposalReady:
                    if (_embedding == null) return;
                    RunDecode(u, v);
                    return;
                default:
                    return;
            }
        }

        void RunDecode(float u, float v)
        {
            SetPhase(MaskSuggestPhase.Decoding);
            float[] logits, scores;
            try
            {
                SamCoordMapper.UvTo1024(u, v, _texW, _texH, out float x, out float y);
                using var emb = new Tensor<float>(new TensorShape(1, 256, 64, 64), _embedding);
                using var pts = new Tensor<float>(new TensorShape(1, 2, 2), new[] { x, y, 0f, 0f });
                using var lbl = new Tensor<float>(new TensorShape(1, 2), new[] { 1f, -1f });
                _decoder.Schedule(emb, pts, lbl);
                using var lo = (_decoder.PeekOutput("low_res_logits") as Tensor<float>).ReadbackAndClone();
                using var sc = (_decoder.PeekOutput("iou_predictions") as Tensor<float>).ReadbackAndClone();
                logits = lo.DownloadToArray();
                scores = sc.DownloadToArray();
            }
            catch (Exception e)
            {
                SetPhase(MaskSuggestPhase.Error, error: $"提案の推論に失敗しました: {e.Message}");
                return;
            }

            int w = _texW, h = _texH;
            var px = _sourcePixels;
            _postJob ??= new PreviewJob<SamMaskPostprocess.Result>();
            _postJob.Schedule(
                ct => SamMaskPostprocess.SelectAndUpscale(
                    logits, scores, w, h, pixelsBottomUp: px),
                res =>
                {
                    if (_phase != MaskSuggestPhase.Decoding) return; // キャンセル済み
                    _proposal = new MaskSuggestProposal
                    {
                        maskBottomUp = res.maskBottomUp,
                        width = w,
                        height = h,
                        score = res.score,
                        areaFrac = res.areaFrac,
                        floodWarning = res.floodWarning,
                    };
                    if (_hasPendingClick)
                    {
                        // 提案表示前に次クリックが来ていたら差し替え(取り直し)
                        _hasPendingClick = false;
                        RunDecode(_pendingU, _pendingV);
                        return;
                    }
                    SetPhase(MaskSuggestPhase.ProposalReady);
                },
                e => SetPhase(MaskSuggestPhase.Error, error: $"提案の生成に失敗しました: {e.Message}"));
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
