// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;

namespace Iroca {
    /// <summary>
    /// AI 提案の推論だけ(モデルを走らせる所)。製品は Sentis(推論のサービスが非同期で回すので、このインターフェースは
    /// 使わず <see cref="SamProposalFlow"/> の段に従う)、ハーネスは同期の実装を <see cref="SamProposalFlow.RunSynchronously(ISamInference, NativePixels, string, float, float, MaskSuggestGranularity, MaskSuggestUse, CancellationToken, out SamProposalDiag)"/> へ渡す。
    /// どれも失敗は例外で知らせる。logits は 4*256*256、scores は 4 の並び(上原点・パディング込みのキャンバス)。
    /// </summary>
    internal interface ISamInference {
        /// <summary>元の画像全体の埋め込みでデコードする(元の画像と全体の埋め込みは実装が持つ)。点は 1024 空間。</summary>
        void DecodeFull(float x1024, float y1024, out float[] logits, out float[] scores);

        /// <summary>
        /// 切り出し(side 四方の画素。読むだけ)をエンコードする。key は <see cref="SamProposalFlow.CropEmbeddingKey"/>
        /// (実装が控えるときの鍵)。埋め込みは参照 1 で返す(受け取った側が Release)。
        /// </summary>
        NativeBuffer EncodeCrop(NativePixels crop, string key);

        /// <summary>切り出しの埋め込みでデコードする。点は切り出しの 1024 空間。</summary>
        void DecodeWith(NativeBuffer cropEmbedding, float x1024, float y1024, out float[] logits, out float[] scores);
    }

    /// <summary>段の機械が次に要るもの。</summary>
    internal enum SamStep {
        /// <summary>全体の埋め込みでデコードする(点は <see cref="SamProposalFlow.DecodePoint"/>)。</summary>
        DecodeFull,
        /// <summary>背景で回してよい計算が 1 つある(<see cref="SamProposalFlow.TakeWork"/>)。</summary>
        Work,
        /// <summary>切り出しの埋め込みが要る(<see cref="SamProposalFlow.CropPixels"/>。鍵は <see cref="SamProposalFlow.CropEmbeddingKey"/>)。</summary>
        EncodeCrop,
        /// <summary>切り出しの埋め込みでデコードする(点は <see cref="SamProposalFlow.DecodePoint"/>)。</summary>
        DecodeCrop,
        /// <summary>終わった(<see cref="SamProposalFlow.TakeResult"/>)。</summary>
        Done,
    }

    /// <summary>同期の駆動(<see cref="SamProposalFlow.RunSynchronously(ISamInference, NativePixels, string, float, float, MaskSuggestGranularity, MaskSuggestUse, CancellationToken, out SamProposalDiag)"/>)の診断。</summary>
    internal struct SamProposalDiag {
        /// <summary>第 1 段がズームを計画したか(クリックの塊が小さかった)。</summary>
        public bool zoomPlanned;
        /// <summary>ズームの結果を返したか(false で zoomPlanned なら、第 1 段へ戻した)。</summary>
        public bool zoomed;
        /// <summary>第 1 段へ戻したとき、精密化したか(元の画素が無ければ粗いまま)。</summary>
        public bool fallbackRefined;
        /// <summary>第 1 段へ戻した理由(切り出しのエンコード・寸法・デコードの失敗の文言。戻さなければ null)。</summary>
        public string cropError;
        /// <summary>切り出しの矩形(下原点。zoomPlanned のときだけ)。</summary>
        public int cropX0, cropY0, cropSide;
        /// <summary>返した提案の採用チャンネル(1..3)・予測 IoU・面積比・背景に流れた可能性。</summary>
        public int channel;
        public float score;
        public float areaFrac;
        public bool floodWarning;
    }

    /// <summary>
    /// AI 提案 1 クリック分の段取り(どの順で何を呼び、どの条件で分岐するか)。「次に要るもの」(<see cref="SamStep"/>)を返すだけで、
    /// 時計・スレッド・推論・取り消しを知らない。推論のサービス(Sentis)は今と同じ所で待ち・背景へ投げ・メインスレッドへ戻り、
    /// 段の分かれ目だけをここに聞く。ハーネスは <see cref="RunSynchronously(ISamInference, NativePixels, string, float, float, MaskSuggestGranularity, MaskSuggestUse, CancellationToken, out SamProposalDiag)"/> で同じ段取りを同期で回す。
    ///   全体のデコード → 第 1 段(<see cref="SamProposalSteps.Stage1"/>)
    ///     → ズームしない: 終わり
    ///     → ズームする: 切り出しのエンコード → 切り出しのデコード → ズームの後処理(<see cref="SamProposalSteps.ZoomPost"/>) → 終わり
    ///       切り出しのエンコード・埋め込みの寸法・デコードが失敗したら第 1 段へ戻す(<see cref="OnCropFailed"/>):
    ///       精密化しない条件なら粗いまま終わり、それ以外は精密化(<see cref="SamProposalSteps.RefineStage1"/>)して終わり。
    /// メモリ: 元の画素は参照を足して持つ。第 1 段の計画(粗い範囲と切り出しの画素)と結果はこのフローが持ち、
    /// <see cref="TakeResult"/> で渡す。どの段でやめても <see cref="Dispose"/> が返す。背景の計算(<see cref="SamWork"/>)は
    /// 読む物の参照を自分で持つので、走っている間にフローを捨ててよい。メインスレッド(1 本のスレッド)だけで使う。
    /// </summary>
    internal sealed class SamProposalFlow : IDisposable {
        /// <summary>埋め込みの float の数(MobileSAM のエンコーダの出力 [1,256,64,64])。切り出しの埋め込みの寸法の検査に使う。</summary>
        public const int EmbeddingFloatCount = 1 * 256 * 64 * 64;

        enum Stage { Created, DecodingFull, Stage1Work, NeedCropEmbedding, DecodingCrop, ZoomWork, RefineWork, Done, Disposed }

        readonly NativePixels _source;
        readonly int _w, _h;
        /// <summary>クリックの UV(下原点。サービスの RequestProposal と同じ)。</summary>
        public readonly float U, V;
        public readonly MaskSuggestGranularity Granularity;
        public readonly MaskSuggestUse Use;

        Stage _stage = Stage.Created;
        float[] _logits, _scores;
        // 第 1 段の結果(ズームする間。粗い範囲と切り出しの画素)と、渡す結果。
        SamPostOutcome _plan, _result;
        // 切り出しの矩形(第 1 段がズームを計画したときに写す。計画を返した後も読める)。
        int _cropX0, _cropY0, _cropSide;

        /// <summary>
        /// source は元の画素(下原点。参照を足して持つ。null なら第 1 段へ戻すときに精密化しない)。w, h はその寸法
        /// (サービスが持つ元の寸法。source があれば同じ)。
        /// </summary>
        public SamProposalFlow(NativePixels source, int w, int h, float u, float v,
                               MaskSuggestGranularity granularity, MaskSuggestUse use) {
            _source = source?.AddRef();
            _w = w;
            _h = h;
            U = u;
            V = v;
            Granularity = granularity;
            Use = use;
        }

        /// <summary>始める(全体のデコードが要る)。</summary>
        public SamStep Start() {
            Expect(Stage.Created);
            _stage = Stage.DecodingFull;
            return SamStep.DecodeFull;
        }

        /// <summary>デコードの点(1024 空間)。全体のデコード・切り出しのデコードのとき。</summary>
        public void DecodePoint(out float x1024, out float y1024) {
            if (_stage == Stage.DecodingFull) {
                SamCoordMapper.UvTo1024(U, V, _w, _h, out x1024, out y1024);
                return;
            }
            Expect(Stage.DecodingCrop);
            SamCoordMapper.UvToCrop1024(U, V, _w, _h, _cropX0, _cropY0, _cropSide, out x1024, out y1024);
        }

        /// <summary>
        /// デコードが済んだ(全体なら第 1 段、切り出しならズームの後処理の計算が要る)。logits・scores は計算が読み終えるまで
        /// 書き換えないこと(サービスは使い回す並びを渡し、次のデコードは後処理が済んでから始める)。
        /// </summary>
        public SamStep OnDecoded(float[] logits, float[] scores) {
            if (_stage == Stage.DecodingFull) {
                _stage = Stage.Stage1Work;
            } else {
                Expect(Stage.DecodingCrop);
                _stage = Stage.ZoomWork;
            }
            _logits = logits;
            _scores = scores;
            return SamStep.Work;
        }

        /// <summary>
        /// 背景で回してよい計算を 1 つ渡す(<see cref="SamStep.Work"/> のとき)。読む物の参照はここで足し、
        /// <see cref="SamWork.Dispose"/> で返す(走らせずにやめるときも Dispose する)。結果は <see cref="Complete"/> へ渡す。
        /// </summary>
        public SamWork TakeWork() {
            switch (_stage) {
                case Stage.Stage1Work:
                    return SamWork.ForStage1(_logits, _scores, _w, _h, _source?.AddRef(), U, V, Granularity, Use);
                case Stage.ZoomWork:
                    return SamWork.ForZoom(_plan, Granularity, _logits, _scores, _w, _h, _plan.cropPixels.AddRef());
                case Stage.RefineWork:
                    return SamWork.ForRefine(_plan, _source.AddRef(), _plan.mask.AddRef());
                default:
                    throw new InvalidOperationException($"背景の計算が要る段ではない: {_stage}");
            }
        }

        /// <summary>
        /// 計算の結果を受け取る(メインスレッドで。結果の持ち主はフローへ移る)。第 1 段でズームを計画したら切り出しの埋め込みが要る。
        /// ズームの後処理なら第 1 段の計画を返して終わり。第 1 段へ戻す精密化なら計画そのものが結果。
        /// </summary>
        public SamStep Complete(SamPostOutcome r) {
            switch (_stage) {
                case Stage.Stage1Work:
                    _logits = _scores = null;
                    if (r.hasCrop) {
                        // 第 2 段の間は持っておく(結果にするか、第 1 段へ戻すか、捨てるときに返す)。
                        _plan = r;
                        _cropX0 = r.cropX0;
                        _cropY0 = r.cropY0;
                        _cropSide = r.cropSide;
                        _stage = Stage.NeedCropEmbedding;
                        return SamStep.EncodeCrop;
                    }
                    return Finish(r);
                case Stage.ZoomWork: {
                        _logits = _scores = null;
                        // 第 1 段の結果はもう使わない。
                        var p = _plan;
                        _plan = null;
                        p.Release();
                        return Finish(r);
                    }
                case Stage.RefineWork:
                    if (!ReferenceEquals(r, _plan)) {
                        throw new InvalidOperationException("第 1 段へ戻す精密化の結果が計画と違う");
                    }
                    _plan = null;
                    return Finish(r);
                default:
                    throw new InvalidOperationException($"計算の結果を受け取る段ではない: {_stage}");
            }
        }

        SamStep Finish(SamPostOutcome r) {
            _result = r;
            _stage = Stage.Done;
            return SamStep.Done;
        }

        /// <summary>切り出しの画素(<see cref="SamStep.EncodeCrop"/> のとき。持ち主はフロー。読む側は参照を足す)。</summary>
        public NativePixels CropPixels => _plan?.cropPixels;

        /// <summary>切り出しの矩形(下原点。第 1 段がズームを計画した後)。</summary>
        public int CropX0 => _cropX0;
        public int CropY0 => _cropY0;
        public int CropSide => _cropSide;

        /// <summary>切り出しの埋め込みの鍵(<see cref="SamProposalSteps.CropEmbeddingKey"/>)。</summary>
        public string CropEmbeddingKey(string sourceKey)
            => SamProposalSteps.CropEmbeddingKey(sourceKey, _cropX0, _cropY0, _cropSide);

        /// <summary>切り出しの埋め込みができた(控えにあった)。切り出しのデコードが要る。</summary>
        public SamStep OnCropEmbedding() {
            Expect(Stage.NeedCropEmbedding);
            _stage = Stage.DecodingCrop;
            return SamStep.DecodeCrop;
        }

        /// <summary>
        /// 切り出しのエンコード・埋め込みの寸法・切り出しのデコードが失敗した。第 1 段の提案へ戻す(エラーにしない)。
        /// 第 1 段の範囲は粗いまま(ズームするときは精密化を省いた)なので、そのまま返すと品質が下がる。精密化しない条件
        /// (計画に切り出しが無い・skipRefine・元の画素が無い)なら粗いまま終わり、それ以外は精密化の計算が要る。
        /// skipRefine は宿主の都合で精密化しても使わないとき(サービスは取り消し済みのクリック = _discardInFlight)。
        /// </summary>
        public SamStep OnCropFailed(bool skipRefine) {
            if (_stage != Stage.NeedCropEmbedding && _stage != Stage.DecodingCrop) {
                throw new InvalidOperationException($"切り出しの失敗を受ける段ではない: {_stage}");
            }
            if (!_plan.hasCrop || skipRefine || _source == null) {
                var p = _plan;
                _plan = null;
                return Finish(p);
            }
            _stage = Stage.RefineWork;
            return SamStep.Work;
        }

        /// <summary>結果(<see cref="SamStep.Done"/> のとき。持ち主ごと渡す。範囲は受け取った側が返す)。</summary>
        public SamPostOutcome TakeResult() {
            Expect(Stage.Done);
            var r = _result;
            _result = null;
            return r;
        }

        /// <summary>持ち物(元の画素の参照・第 1 段の計画・渡していない結果)を返す。何度呼んでもよい。</summary>
        public void Dispose() {
            if (_stage == Stage.Disposed) {
                return;
            }
            _stage = Stage.Disposed;
            _logits = _scores = null;
            var p = _plan;
            _plan = null;
            p?.Release();
            var r = _result;
            _result = null;
            r?.Release();
            _source?.Release();
        }

        void Expect(Stage s) {
            if (_stage != s) {
                throw new InvalidOperationException($"段が違う: {_stage}(期待 {s})");
            }
        }

        /// <summary>
        /// ハーネス・テスト用の同期の駆動(製品は使わない)。source は元の画素(下原点。読むだけ)、sourceKey は切り出しの
        /// 埋め込みの鍵の頭。結果の提案(範囲の参照ごと渡す。受け取った側が Release)と診断を返す。全体のデコードと計算の失敗・
        /// 取り消しは例外のまま投げる(製品ではエラーの状態)。切り出しの失敗だけ第 1 段へ戻す(製品と同じ)。
        /// </summary>
        public static MaskSuggestProposal RunSynchronously(ISamInference ai, NativePixels source, string sourceKey,
            float u, float v, MaskSuggestGranularity granularity, MaskSuggestUse use, CancellationToken ct,
            out SamProposalDiag diag)
            => RunSynchronously(ai, source, source.Width, source.Height, sourceKey, u, v, granularity, use, ct, out diag);

        /// <summary>寸法を別に渡す版(元の画素が無い = null のときの段取りを確かめる用)。</summary>
        public static MaskSuggestProposal RunSynchronously(ISamInference ai, NativePixels source, int w, int h,
            string sourceKey, float u, float v, MaskSuggestGranularity granularity, MaskSuggestUse use,
            CancellationToken ct, out SamProposalDiag diag) {
            diag = default;
            using var flow = new SamProposalFlow(source, w, h, u, v, granularity, use);
            NativeBuffer cropEmbedding = null;
            try {
                var step = flow.Start();
                while (step != SamStep.Done) {
                    ct.ThrowIfCancellationRequested();
                    switch (step) {
                        case SamStep.DecodeFull: {
                                flow.DecodePoint(out float x, out float y);
                                ai.DecodeFull(x, y, out var logits, out var scores);
                                step = flow.OnDecoded(logits, scores);
                                break;
                            }
                        case SamStep.Work: {
                                using var work = flow.TakeWork();
                                step = flow.Complete(work.Run(ct));
                                if (step == SamStep.EncodeCrop) {
                                    diag.zoomPlanned = true;
                                    diag.cropX0 = flow.CropX0;
                                    diag.cropY0 = flow.CropY0;
                                    diag.cropSide = flow.CropSide;
                                }
                                break;
                            }
                        case SamStep.EncodeCrop: {
                                try {
                                    cropEmbedding = ai.EncodeCrop(flow.CropPixels, flow.CropEmbeddingKey(sourceKey));
                                    long floats = cropEmbedding == null ? 0 : cropEmbedding.Bytes / sizeof(float);
                                    if (floats != EmbeddingFloatCount) {
                                        throw new InvalidOperationException($"切り出しの埋め込みの寸法が違う: {floats}");
                                    }
                                } catch (Exception e) when (!(e is OperationCanceledException)) {
                                    cropEmbedding?.Release();
                                    cropEmbedding = null;
                                    diag.cropError = e.Message;
                                    step = flow.OnCropFailed(skipRefine: false);
                                    diag.fallbackRefined = step == SamStep.Work;
                                    break;
                                }
                                step = flow.OnCropEmbedding();
                                break;
                            }
                        case SamStep.DecodeCrop: {
                                float[] logits, scores;
                                try {
                                    flow.DecodePoint(out float x, out float y);
                                    ai.DecodeWith(cropEmbedding, x, y, out logits, out scores);
                                } catch (Exception e) when (!(e is OperationCanceledException)) {
                                    diag.cropError = e.Message;
                                    step = flow.OnCropFailed(skipRefine: false);
                                    diag.fallbackRefined = step == SamStep.Work;
                                    break;
                                } finally {
                                    cropEmbedding?.Release();
                                    cropEmbedding = null;
                                }
                                step = flow.OnDecoded(logits, scores);
                                diag.zoomed = true;
                                break;
                            }
                        default:
                            throw new InvalidOperationException($"知らない段: {step}");
                    }
                }
            } finally {
                cropEmbedding?.Release();
            }
            var r = flow.TakeResult();
            diag.channel = r.channel;
            diag.score = r.score;
            diag.areaFrac = r.areaFrac;
            diag.floodWarning = r.floodWarning;
            // 範囲は提案へ渡す(切り出しはもう使わないので返す。サービスの配信と同じ)。
            var proposal = new MaskSuggestProposal {
                mask = r.mask,
                width = w,
                height = h,
                score = r.score,
                areaFrac = r.areaFrac,
                floodWarning = r.floodWarning,
            };
            r.mask = null;
            r.Release();
            return proposal;
        }
    }

    /// <summary>
    /// <see cref="SamProposalFlow"/> が渡す背景の計算 1 つ(第 1 段・ズームの後処理・第 1 段へ戻す精密化)。読む物(元の画素・
    /// 切り出しの画素・第 1 段の範囲)の参照を持ち、<see cref="Dispose"/> で返す(背景のスレッドで呼んでよい)。
    /// </summary>
    internal sealed class SamWork : IDisposable {
        enum Kind { Stage1, Zoom, Refine }

        readonly Kind _kind;
        readonly float[] _logits, _scores;
        readonly int _w, _h;
        readonly float _u, _v;
        readonly MaskSuggestGranularity _granularity;
        readonly MaskSuggestUse _use;
        readonly SamPostOutcome _plan;
        // 読む間の参照(Dispose で返す)。
        NativePixels _px;
        NativeBools _mask;

        SamWork(Kind kind, float[] logits, float[] scores, int w, int h, float u, float v,
                MaskSuggestGranularity granularity, MaskSuggestUse use, SamPostOutcome plan, NativePixels px, NativeBools mask) {
            _kind = kind;
            _logits = logits;
            _scores = scores;
            _w = w;
            _h = h;
            _u = u;
            _v = v;
            _granularity = granularity;
            _use = use;
            _plan = plan;
            _px = px;
            _mask = mask;
        }

        internal static SamWork ForStage1(float[] logits, float[] scores, int w, int h, NativePixels px,
                                          float u, float v, MaskSuggestGranularity granularity, MaskSuggestUse use)
            => new SamWork(Kind.Stage1, logits, scores, w, h, u, v, granularity, use, null, px, null);

        internal static SamWork ForZoom(SamPostOutcome plan, MaskSuggestGranularity granularity, float[] logits,
                                        float[] scores, int w, int h, NativePixels cropPx)
            => new SamWork(Kind.Zoom, logits, scores, w, h, 0, 0, granularity, default, plan, cropPx, null);

        internal static SamWork ForRefine(SamPostOutcome plan, NativePixels px, NativeBools mask)
            => new SamWork(Kind.Refine, null, null, 0, 0, 0, 0, default, default, plan, px, mask);

        /// <summary>
        /// 計算する(背景のスレッドで呼んでよい)。結果は <see cref="SamProposalFlow.Complete"/> へ渡す。
        /// coarseDone は第 1 段の粗マスクとズームの判定の後に呼ぶ観察者(計測用。第 1 段のときだけ)。
        /// 第 1 段へ戻す精密化は、第 1 段の計画をその場で精密化して計画そのものを返す。
        /// </summary>
        public SamPostOutcome Run(CancellationToken ct, Action coarseDone = null) {
            switch (_kind) {
                case Kind.Stage1:
                    return SamProposalSteps.Stage1(_logits, _scores, _w, _h, _px, _u, _v, _granularity, _use, ct, coarseDone);
                case Kind.Zoom:
                    return SamProposalSteps.ZoomPost(_plan, _granularity, _logits, _scores, _w, _h, _px, ct);
                default:
                    SamProposalSteps.RefineStage1(_mask, _px, _plan.dropForeign, _plan.clickX, _plan.clickY, ct);
                    return _plan;
            }
        }

        /// <summary>読む間の参照を返す(元の画素 → 範囲の順。何度呼んでもよい)。</summary>
        public void Dispose() {
            var px = Interlocked.Exchange(ref _px, null);
            px?.Release();
            var mask = Interlocked.Exchange(ref _mask, null);
            mask?.Release();
        }
    }
}
