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

        Model _encoderModel, _decoderModel;
        Worker _encoder, _decoder;
        BackendType _backend;
        bool _modelsLoaded;
        bool _triedCpuFallback;
        // デコーダの初回実行は推論カーネル(Burst / コンピュートシェーダ)のコンパイルを伴い、
        // Unity 起動後の 1 回だけ数秒〜数十秒かかる。クリック後に踏むと「押しても返ってこない」
        // 時間になるので、埋め込み計算の直後に 1 回だけ捨て推論して温めておく。
        bool _decoderWarmed;

        MaskSuggestPhase _phase = MaskSuggestPhase.NoModel;
        float _progress;
        string _error;
        public event Action StateChanged;

        string _sourceKey;
        Color32[] _sourcePixels;
        int _texW, _texH;
        float[] _embedding;                         // 現テクスチャの埋め込み(null=未計算)
        readonly Dictionary<string, float[]> _embeddingCache = new Dictionary<string, float[]>();
        readonly List<string> _lruOrder = new List<string>();

        readonly EditorIteratorPump _pump = new EditorIteratorPump();
        readonly EditorReadbackPoller _readback = new EditorReadbackPoller();
        PreviewJob<float[]> _prepJob;
        PreviewJob<PostOutcome> _postJob;
        Tensor<float> _encInput;                    // pump 中だけ保持
        MaskSuggestProposal _proposal;
        // 推論中に来たクリックは FIFO で貯めて順に処理する。1 クリック = 1 Undo ストローク
        // の追加操作なので、全クリックを順に反映するのがコミット規約と整合する
        // (以前は最新 1 件だけ残して他を黙って捨てていた)。
        readonly Queue<PendingClick> _clickQueue = new Queue<PendingClick>();
        bool _clickInFlight;    // デコード開始〜提案確定/破棄まで true(暖機は含まない)
        bool _discardInFlight;  // Undo 割り込み: 進行中クリックは完走させ提案だけ捨てる

        readonly struct PendingClick
        {
            public readonly float u, v;
            public readonly MaskSuggestGranularity granularity;
            public readonly long requestedAt; // キュー待ち時間の計測用
            public PendingClick(float u, float v, MaskSuggestGranularity granularity)
            {
                this.u = u;
                this.v = v;
                this.granularity = granularity;
                requestedAt = MaskSuggestPerf.Now;
            }
        }

        long _clickStartedAt;        // クリック処理(デコード)開始
        double _clickQueueWaitMs;    // クリック受理 → 処理開始までの待ち
        double _clickDecodeMs;       // 第 1 段デコーダ
        double _clickPostMs;         // 第 1 段後処理(粗マスク生成+ズーム判定)
        double _clickRefineMs;       // 第 1 段精密化(ズーム不発時のみ走る)
        double _clickZoomDecodeMs;   // ズーム第 2 段デコーダ(走った時のみ)
        double _clickZoomPostMs;     // ズーム第 2 段後処理(同上)
        bool _clickZoomRan;
        long _encodeStartedAt;       // エンコード(全体/クロップ)開始
        double _encodePrepMs;        // BuildEncoderInput(BG スレッド)
        long _pumpStartedAt;         // pump 開始

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
            // ズーム計画(hasCrop=true のときのみ有効。下原点矩形)。
            // hasCrop=true の mask は粗マスクのまま(精密化はズーム不発時のみ行う)。
            // ズーム失敗で第 1 段へ退避するときは FallbackToStage1 が配信前に精密化する。
            public bool hasCrop;
            public int cropX0, cropY0, cropSide;
            public Color32[] cropPixels;
            public double postMs;
            public double refineMs; // 精密化 3 段(走ったときのみ非 0)
        }

        public SentisMaskSuggestService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
        }

        public MaskSuggestPhase Phase => _phase;
        public float Progress => _progress;
        public string ErrorMessage => _error;

        // Burst の ILPP はアセンブリ内の BurstDirectCall 関数ポインタをドメインロード時に
        // 一括で先行コンパイルするため、GPUCompute でしか動かさない環境でも
        // Unity.Sentis.CPUBackend 側の失敗ログは出る。その関数は呼ばれないので推論には
        // 影響しない(GPU の推論カーネルはコンピュートシェーダで Burst を通らない)。
        // 実際に CPU で走るときだけが、Burst 失敗を警告してよい状況。
        public bool UsesCpuBackend => _modelsLoaded && _backend == BackendType.CPU;

        void SetPhase(MaskSuggestPhase phase, float progress = 0f, string error = null)
        {
            // Error は手動復帰(AI モード入り直し)まで続き、処理待ちクリックが実行される
            // ことはない。残すと復帰後に古いクリックが突然走ったように見えるため破棄する。
            if (phase == MaskSuggestPhase.Error)
            {
                _clickQueue.Clear();
                _clickInFlight = false;
                _discardInFlight = false;
            }
            _phase = phase;
            _progress = progress;
            _error = error;
            StateChanged?.Invoke();
        }

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
            // ここは同期処理でエディタが止まるため、無反応に見えないよう進捗バーを出す。
            SetPhase(MaskSuggestPhase.LoadingModel);
            try
            {
                EditorUtility.DisplayProgressBar(
                    Localization.AiSuggest, Localization.AiSuggestLoadingModel, 0.1f);
                _encoderModel = SentisModelRepository.LoadOrConvert(
                    SentisModelRepository.EncoderOnnxPath, out string encErr);
                if (_encoderModel == null)
                {
                    SetPhase(MaskSuggestPhase.Error, error: encErr);
                    return false;
                }
                EditorUtility.DisplayProgressBar(
                    Localization.AiSuggest, Localization.AiSuggestLoadingModel, 0.6f);
                _decoderModel = SentisModelRepository.LoadOrConvert(
                    SentisModelRepository.DecoderOnnxPath, out string decErr);
                if (_decoderModel == null)
                {
                    SetPhase(MaskSuggestPhase.Error, error: decErr);
                    return false;
                }
                EditorUtility.DisplayProgressBar(
                    Localization.AiSuggest, Localization.AiSuggestLoadingModel, 0.9f);
                _backend = SystemInfo.supportsComputeShaders ? BackendType.GPUCompute : BackendType.CPU;
                if (!TryCreateWorkers(out string werr))
                {
                    SetPhase(MaskSuggestPhase.Error, error: werr);
                    return false;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
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
                _decoderWarmed = false; // バックエンドが変わればカーネルも作り直しになる
                return true;
            }
            catch (Exception e)
            {
                error = $"推論ワーカーの作成に失敗しました({_backend}): {e.Message}";
                return false;
            }
        }

        public void SetSource(string cacheKey, Color32[] pixelsBottomUp, int width, int height)
        {
            if (!_modelsLoaded) return;
            if (cacheKey == _sourceKey &&
                (_embedding != null || _phase == MaskSuggestPhase.Encoding))
                return;

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
                MaskSuggestPerf.Log("エンコード(全体): 埋め込みキャッシュ命中");
                SetPhase(MaskSuggestPhase.Idle);
                return;
            }
            StartEncode();
        }

        void StartEncode()
        {
            SetPhase(MaskSuggestPhase.Encoding);
            _encodeStartedAt = MaskSuggestPerf.Now;
            var px = _sourcePixels;
            int w = _texW, h = _texH;
            double prepMs = 0; // work(BG)で書き apply(メイン)で読む。Post キュー経由で順序保証あり
            _prepJob ??= new PreviewJob<float[]>();
            _prepJob.Schedule(
                ct =>
                {
                    long t0 = MaskSuggestPerf.Now;
                    // ct を貫通させ、キャンセル済みジョブが全コア−2 を占有し続けないようにする
                    // (完走時の出力には影響しない)。
                    var chw = SamImageOps.BuildEncoderInput(px, w, h, ct);
                    prepMs = MaskSuggestPerf.MsSince(t0);
                    return chw;
                },
                chw =>
                {
                    _encodePrepMs = prepMs;
                    StartEncoderPump(chw);
                },
                e => SetPhase(MaskSuggestPhase.Error, error: $"前処理に失敗しました: {e.Message}"));
        }

        void StartEncoderPump(float[] chw)
        {
            if (_phase != MaskSuggestPhase.Encoding) return;
            try
            {
                _encInput = new Tensor<float>(
                    new TensorShape(1, 3, SamImageOps.InputSize, SamImageOps.InputSize), chw);
                var it = _encoder.ScheduleIterable(_encInput);
                int total = _encoderModel.layers != null ? _encoderModel.layers.Count : 0;
                _pumpStartedAt = MaskSuggestPerf.Now;
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
                double pumpMs = MaskSuggestPerf.MsSince(_pumpStartedAt);
                long tRead = MaskSuggestPerf.Now;
                // pump 完了 = ディスパッチ完了であって GPU 実行完了ではない。同期
                // ReadbackAndClone は実行完了まで(数百 ms 級)メインスレッドを止めるため、
                // 非同期リクエスト + ポーリングで待つ(待機中もエディタは応答する)。
                // 待機中も Phase は Encoding のままなのでクリックはキューに積まれ、
                // 同一 Worker への新規 Schedule は起きない(フェーズの単一実行)。
                var output = _encoder.PeekOutput() as Tensor<float>;
                _readback.Start(output,
                    data => CompleteEncode(data, pumpMs, tRead),
                    OnEncoderError);
            }
            catch (Exception e)
            {
                OnEncoderError(e);
            }
        }

        void CompleteEncode(float[] data, double pumpMs, long readStartedAt)
        {
            try
            {
                _embedding = data;
                if (MaskSuggestPerf.Enabled)
                    MaskSuggestPerf.Log(
                        $"エンコード(全体 {_texW}x{_texH}): 前処理 {_encodePrepMs:F0}ms" +
                        $" / pump {pumpMs:F0}ms ({_pump.Steps} steps / {_pump.Ticks} ticks)" +
                        $" / 読出 {MaskSuggestPerf.MsSince(readStartedAt):F0}ms(非同期)" +
                        $" / 合計 {MaskSuggestPerf.MsSince(_encodeStartedAt):F0}ms ({_backend})");
                DisposeEncInput();
                if (_embedding == null || _embedding.Length != EmbeddingLength)
                    throw new InvalidOperationException(
                        $"埋め込みサイズが不正です: {_embedding?.Length ?? 0}");
                // 形は正しいのに中身が定数/NaN = 推論カーネルが実行されていない。下流では
                // 「提案は返るのにマスクへ 1 画素も足されない」という分かりにくい失敗になるため、
                // ここで異常として扱いフォールバック/報告へ回す。
                if (IsDegenerate(_embedding))
                {
                    throw new InvalidOperationException(
                        "推論結果が空です(バックエンドがカーネルを実行できていません)");
                }
                CacheEmbedding(_sourceKey, _embedding);
                _triedCpuFallback = false;
                // 健全な埋め込みが得られた = 実際に使うバックエンドで推論カーネルが動いた証拠。
                // ログ文字列からの Burst 失敗推定より強い根拠なので、再起動を促す案内を取り下げる
                // (CPU へフォールバックしたうえで成功した場合もここを通る)。
                SessionState.SetBool(MaskSuggestBurstWatch.BurstFailedKey, false);
                SessionState.SetBool(MaskSuggestInstall.RestartRecommendedKey, false);
                if (_clickQueue.Count > 0)
                {
                    StartNextClick();
                }
                else if (!TryStartDecoderWarmup())
                {
                    // 暖機を始めた場合は Decoding 表示のまま次の tick に渡す(完了時に Idle へ戻る)
                    SetPhase(MaskSuggestPhase.Idle);
                }
            }
            catch (Exception e)
            {
                OnEncoderError(e);
            }
        }

        /// <summary>
        /// 埋め込みが「形は正しいが中身が無い」状態か(全要素同値、または NaN/Inf を含む)。
        /// Burst のコールドスタート失敗などでカーネルが実行されないと、例外が出ないまま
        /// 出力が定数になることがある。定数出力は全域で定数なので、全走査せず間引いて見る。
        /// </summary>
        static bool IsDegenerate(float[] embedding)
        {
            float first = embedding[0];
            bool allSame = true;
            int step = Mathf.Max(1, embedding.Length / 4096);
            for (int i = 0; i < embedding.Length; i += step)
            {
                float v = embedding[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) return true;
                if (v != first) allSame = false;
            }
            return allSame;
        }

        /// <summary>
        /// デコーダを 1 回だけ捨て推論し、推論カーネルのコンパイルをクリック前に済ませる。
        /// 結果は使わない。失敗しても無視する(実クリック時に通常のエラー経路で報告される)。
        ///
        /// 同期実行すると初回は固まって見えるので、Decoding を表示してから次の tick で走らせる。
        /// Decoding 中のクリックは保留される規約なので、暖機中に押されても取りこぼさない
        /// (完了時に <see cref="RunDecoderWarmup"/> が拾う)。
        /// </summary>
        /// <returns>暖機を開始したか(false = 済み・不要で、呼び出し側が Idle へ戻す)。</returns>
        bool TryStartDecoderWarmup()
        {
            if (_decoderWarmed || _embedding == null) return false;
            _decoderWarmed = true;
            SetPhase(MaskSuggestPhase.Decoding);
            EditorApplication.delayCall += RunDecoderWarmup;
            return true;
        }

        void RunDecoderWarmup()
        {
            EditorApplication.delayCall -= RunDecoderWarmup;
            if (_phase != MaskSuggestPhase.Decoding || _embedding == null) return;
            long t0 = MaskSuggestPerf.Now;
            TryRunDecoderCore(0.5f, 0.5f, cropRect: null, out _, out _, out _);
            MaskSuggestPerf.Log($"デコーダ暖機: {MaskSuggestPerf.MsSince(t0):F0}ms");
            if (_phase != MaskSuggestPhase.Decoding) return;
            if (_clickQueue.Count > 0)
            {
                StartNextClick();
                return;
            }
            SetPhase(MaskSuggestPhase.Idle);
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

        public int PendingClickCount => _clickQueue.Count + (_clickInFlight ? 1 : 0);

        public bool RequestProposal(float u, float v, MaskSuggestGranularity granularity)
        {
            if (!_modelsLoaded) return false;
            switch (_phase)
            {
                case MaskSuggestPhase.Encoding:
                case MaskSuggestPhase.Decoding:
                    _clickQueue.Enqueue(new PendingClick(u, v, granularity));
                    StateChanged?.Invoke();
                    return true;
                case MaskSuggestPhase.Idle:
                case MaskSuggestPhase.ProposalReady:
                    if (_embedding == null) return false;
                    _clickQueue.Enqueue(new PendingClick(u, v, granularity));
                    StartNextClick();
                    return true;
                default:
                    return false;
            }
        }

        public void FlushPendingClicks()
        {
            if (_clickQueue.Count == 0 && !_clickInFlight) return;
            _clickQueue.Clear();
            // 進行中の 1 件は途中で殺さず完走させ、提案だけ捨てる(GPU/ジョブの中断より単純で安全)。
            if (_clickInFlight) _discardInFlight = true;
            StateChanged?.Invoke();
        }

        /// <summary>キュー先頭のクリックを取り出してデコードを開始する。</summary>
        void StartNextClick()
        {
            var c = _clickQueue.Dequeue();
            _clickInFlight = true;
            RunDecode(c.u, c.v, c.granularity, MaskSuggestPerf.MsSince(c.requestedAt));
        }

        /// <summary>
        /// クリック 1 件の完了後、待ちがあれば次を予約し、なければ Idle へ戻す。
        /// 次のデコードは delayCall で 1 tick 逃がす: 取り出した提案のマスク反映・再描画を
        /// 先に済ませ、StateChanged ハンドラ内からの深い再入(同期デコード数十 ms)も避ける。
        /// </summary>
        void ScheduleNextClickOrIdle()
        {
            if (_clickQueue.Count > 0)
            {
                SetPhase(MaskSuggestPhase.Decoding);
                EditorApplication.delayCall += ProcessNextQueuedClick;
            }
            else
            {
                SetPhase(MaskSuggestPhase.Idle);
            }
        }

        void ProcessNextQueuedClick()
        {
            EditorApplication.delayCall -= ProcessNextQueuedClick;
            if (_phase != MaskSuggestPhase.Decoding) return;
            if (_clickQueue.Count == 0) // 予約後に FlushPendingClicks が挟まった
            {
                SetPhase(MaskSuggestPhase.Idle);
                return;
            }
            StartNextClick();
        }

        void RunDecode(float u, float v, MaskSuggestGranularity granularity, double queueWaitMs)
        {
            SetPhase(MaskSuggestPhase.Decoding);
            _clickStartedAt = MaskSuggestPerf.Now;
            _clickQueueWaitMs = queueWaitMs;
            _clickDecodeMs = _clickPostMs = _clickRefineMs = _clickZoomDecodeMs = _clickZoomPostMs = 0;
            _clickZoomRan = false;
            long tDec = MaskSuggestPerf.Now;
            if (!TryRunDecoderCore(u, v, cropRect: null, out float[] logits, out float[] scores,
                                   out Exception decErr))
            {
                SetPhase(MaskSuggestPhase.Error, error: $"提案の推論に失敗しました: {decErr.Message}");
                return;
            }
            _clickDecodeMs = MaskSuggestPerf.MsSince(tDec);

            int w = _texW, h = _texH;
            var px = _sourcePixels;
            _postJob ??= new PreviewJob<PostOutcome>();
            _postJob.Schedule(
                ct =>
                {
                    long t0 = MaskSuggestPerf.Now;
                    // まず精密化なしの粗マスクだけ作り、ズームイン再推論の要否を判定する。
                    // ズーム発火時は第 1 段マスクが最終出力に使われない(RunZoomDecode が
                    // 空配列へ PasteCrop した結果で置き換える)ため、発火時に精密化すると
                    // その分(4K 実測 0.8-1.4s)が丸ごと捨てられる。不発時のみ従来と同一
                    // 順序・同一入力で精密化する(= SelectAndUpscale(px) と厳密同値)。
                    var s1 = SamMaskPostprocess.SelectAndUpscale(
                        logits, scores, w, h, pixelsBottomUp: null, granularity: granularity,
                        token: ct);
                    var o = new PostOutcome
                    {
                        mask = s1.maskBottomUp,
                        score = s1.score,
                        areaFrac = s1.areaFrac,
                        floodWarning = s1.floodWarning,
                    };
                    // ズームイン再推論の判定: クリック成分が小さい(=256²ロジットで形状表現
                    // できない)場合のみ、クリック周辺クロップの再推論計画を積む。粗マスクの
                    // bbox は精密化後と数 px しか違わず、クロップ矩形は 2 冪スナップで吸収される
                    // (実 SAM fixture で crop 決定の一致を確認済み)。
                    int cx = Mathf.Clamp((int)(u * w), 0, w - 1);
                    int cy = Mathf.Clamp((int)(v * h), 0, h - 1); // v は下原点 → 下原点行と一致
                    int bb = SamZoomOps.ClickComponentBBoxLong(s1.maskBottomUp, w, h, cx, cy);
                    bool hasCrop = SamZoomOps.TryDeriveCropRect(bb, cx, cy, w, h,
                                                                out int x0, out int y0, out int side);
                    if (hasCrop)
                    {
                        o.hasCrop = true;
                        o.cropX0 = x0;
                        o.cropY0 = y0;
                        o.cropSide = side;
                        o.cropPixels = SamZoomOps.ExtractCrop(px, w, h, x0, y0, side);
                    }
                    o.postMs = MaskSuggestPerf.MsSince(t0);
                    if (!hasCrop)
                    {
                        long tr = MaskSuggestPerf.Now;
                        SamMaskPostprocess.RefineInPlace(s1.maskBottomUp, px, w, h, ct);
                        o.refineMs = MaskSuggestPerf.MsSince(tr);
                    }
                    return o;
                },
                o =>
                {
                    if (_phase != MaskSuggestPhase.Decoding) return;
                    _clickPostMs = o.postMs;
                    _clickRefineMs = o.refineMs;
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

        /// <summary>提案を確定する(Undo 割り込み済みなら捨てて次の待ちクリックへ)。</summary>
        void DeliverProposal(PostOutcome o)
        {
            _clickInFlight = false;
            if (MaskSuggestPerf.Enabled)
                MaskSuggestPerf.Log(
                    $"クリック: 待ち {_clickQueueWaitMs:F0}ms / デコード {_clickDecodeMs:F0}ms" +
                    $" / 後処理(粗) {_clickPostMs:F0}ms" +
                    (_clickRefineMs > 0 ? $" / 精密化 {_clickRefineMs:F0}ms" : "") +
                    (_clickZoomRan
                        ? $" / ズーム: デコード {_clickZoomDecodeMs:F0}ms + 後処理 {_clickZoomPostMs:F0}ms" +
                          "(クロップのエンコードは別行)"
                        : "") +
                    $" / 合計 {MaskSuggestPerf.MsSince(_clickStartedAt):F0}ms" +
                    (_discardInFlight ? " (Undo 割り込みのため破棄)" : ""));
            if (_discardInFlight)
            {
                _discardInFlight = false;
                _proposal = null;
                ScheduleNextClickOrIdle();
                return;
            }
            _proposal = new MaskSuggestProposal
            {
                maskBottomUp = o.mask,
                width = _texW,
                height = _texH,
                score = o.score,
                areaFrac = o.areaFrac,
                floodWarning = o.floodWarning,
            };
            SetPhase(MaskSuggestPhase.ProposalReady);
        }

        // 小パーツはクリック周辺クロップを再エンコード・再デコードして実効解像度を上げる
        // (計測: dev_safe/ml/zoom_infer_spike2.py。バンダナ三角 IoU 0.04-0.10 → 0.94-0.97)。
        // 失敗時は第 1 段の提案へグレースフルに退避し、エラー状態にはしない。

        void StartZoomStage(PostOutcome plan, float u, float v, MaskSuggestGranularity granularity)
        {
            _clickZoomRan = true;
            string key = $"{_sourceKey}|{plan.cropX0},{plan.cropY0},{plan.cropSide}";
            if (_cropEmbeddingCache.TryGetValue(key, out var cached))
            {
                TouchCropLru(key);
                MaskSuggestPerf.Log("エンコード(ズーム): クロップ埋め込みキャッシュ命中");
                RunZoomDecode(cached, plan, u, v, granularity);
                return;
            }
            _encodeStartedAt = MaskSuggestPerf.Now;
            var cropPx = plan.cropPixels;
            int side = plan.cropSide;
            double prepMs = 0; // work(BG)で書き apply(メイン)で読む
            _prepJob ??= new PreviewJob<float[]>();
            _prepJob.Schedule(
                ct =>
                {
                    long t0 = MaskSuggestPerf.Now;
                    var chw = SamImageOps.BuildEncoderInput(cropPx, side, side, ct);
                    prepMs = MaskSuggestPerf.MsSince(t0);
                    return chw;
                },
                chw =>
                {
                    _encodePrepMs = prepMs;
                    StartZoomEncoderPump(chw, key, plan, u, v, granularity);
                },
                e => FallbackToStage1(plan, e));
        }

        void StartZoomEncoderPump(float[] chw, string key, PostOutcome plan,
                                  float u, float v, MaskSuggestGranularity granularity)
        {
            if (_phase != MaskSuggestPhase.Decoding) return;
            try
            {
                _encInput = new Tensor<float>(
                    new TensorShape(1, 3, SamImageOps.InputSize, SamImageOps.InputSize), chw);
                var it = _encoder.ScheduleIterable(_encInput);
                int total = _encoderModel.layers != null ? _encoderModel.layers.Count : 0;
                _pumpStartedAt = MaskSuggestPerf.Now;
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
            try
            {
                double pumpMs = MaskSuggestPerf.MsSince(_pumpStartedAt);
                long tRead = MaskSuggestPerf.Now;
                // 全体エンコードと同じく非同期リクエスト + ポーリング(FinishEncode 参照)。
                // ズームは毎クリック走り得るため、同期読出のメイン停止(実測 129-194ms)が
                // そのまま操作の引っ掛かりになっていた。
                var output = _encoder.PeekOutput() as Tensor<float>;
                _readback.Start(output,
                    data => CompleteZoomEncode(data, key, plan, u, v, granularity, pumpMs, tRead),
                    e => { DisposeEncInput(); FallbackToStage1(plan, e); });
            }
            catch (Exception e)
            {
                DisposeEncInput();
                FallbackToStage1(plan, e);
            }
        }

        void CompleteZoomEncode(float[] emb, string key, PostOutcome plan,
                                float u, float v, MaskSuggestGranularity granularity,
                                double pumpMs, long readStartedAt)
        {
            try
            {
                if (MaskSuggestPerf.Enabled)
                    MaskSuggestPerf.Log(
                        $"エンコード(ズーム crop {plan.cropSide}px): 前処理 {_encodePrepMs:F0}ms" +
                        $" / pump {pumpMs:F0}ms ({_pump.Steps} steps / {_pump.Ticks} ticks)" +
                        $" / 読出 {MaskSuggestPerf.MsSince(readStartedAt):F0}ms(非同期)" +
                        $" / 合計 {MaskSuggestPerf.MsSince(_encodeStartedAt):F0}ms ({_backend})");
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
            if (_phase != MaskSuggestPhase.Decoding) return;
            long tDec = MaskSuggestPerf.Now;
            if (!TryRunDecoderCore(u, v, (plan.cropX0, plan.cropY0, plan.cropSide, cropEmbedding),
                                   out float[] logits, out float[] scores, out Exception decErr))
            {
                FallbackToStage1(plan, decErr);
                return;
            }
            _clickZoomDecodeMs = MaskSuggestPerf.MsSince(tDec);
            int w = _texW, h = _texH;
            int x0 = plan.cropX0, y0 = plan.cropY0, side = plan.cropSide;
            var cropPx = plan.cropPixels;
            _postJob.Schedule(
                ct =>
                {
                    long t0 = MaskSuggestPerf.Now;
                    var res = SamMaskPostprocess.SelectAndUpscale(
                        logits, scores, side, side, pixelsBottomUp: cropPx, granularity: granularity,
                        token: ct);
                    var full = SamZoomOps.PasteCrop(res.maskBottomUp, side, w, h, x0, y0,
                                                    out int trueCount);
                    return new PostOutcome
                    {
                        mask = full,
                        score = res.score,
                        areaFrac = trueCount / (float)(w * h),
                        floodWarning = res.floodWarning,
                        postMs = MaskSuggestPerf.MsSince(t0),
                    };
                },
                o =>
                {
                    if (_phase != MaskSuggestPhase.Decoding) return;
                    _clickZoomPostMs = o.postMs;
                    DeliverProposal(o);
                },
                e => SetPhase(MaskSuggestPhase.Error, error: $"提案の生成に失敗しました: {e.Message}"));
        }

        void FallbackToStage1(PostOutcome plan, Exception e)
        {
            if (_phase != MaskSuggestPhase.Decoding) return;
            if (e != null)
                Debug.LogWarning($"[Iroca] ズームイン再推論に失敗したため全体推論の提案を表示します: {e.Message}");
            // ズーム計画の第 1 段マスクは粗マスクのまま(発火時は精密化を省く)。そのまま
            // 配信すると品質後退なので、破棄予定でなければここで精密化してから配信する
            // (稀な経路。所要時間は従来の第 1 段精密化と同等)。
            var px = _sourcePixels;
            if (!plan.hasCrop || _discardInFlight || px == null)
            {
                DeliverProposal(plan);
                return;
            }
            int w = _texW, h = _texH;
            _postJob.Schedule(
                ct =>
                {
                    long t0 = MaskSuggestPerf.Now;
                    SamMaskPostprocess.RefineInPlace(plan.mask, px, w, h, ct);
                    plan.refineMs = MaskSuggestPerf.MsSince(t0);
                    return plan;
                },
                o =>
                {
                    if (_phase != MaskSuggestPhase.Decoding) return;
                    _clickRefineMs = o.refineMs;
                    DeliverProposal(o);
                },
                err => SetPhase(MaskSuggestPhase.Error, error: $"提案の生成に失敗しました: {err.Message}"));
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
            ScheduleNextClickOrIdle();
            return true;
        }

        public void CancelAll()
        {
            CancelOps();
            _sourceKey = null;
            _sourcePixels = null;
            _embedding = null;
            // Error を持ち越すと以降のクリックが全部無視されるので、モデルが載っているなら
            // Idle に戻す(UI からはテクスチャ切替・AI モードの入り直しが再試行導線になる)。
            if (_modelsLoaded)
                SetPhase(MaskSuggestPhase.Idle);
        }

        void CancelOps()
        {
            EditorApplication.delayCall -= RunDecoderWarmup;
            EditorApplication.delayCall -= ProcessNextQueuedClick;
            _pump.Stop();
            _readback.Stop();
            _prepJob?.Cancel();
            _postJob?.Cancel();
            DisposeEncInput();
            _proposal = null;
            _clickQueue.Clear();
            _clickInFlight = false;
            _discardInFlight = false;
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
