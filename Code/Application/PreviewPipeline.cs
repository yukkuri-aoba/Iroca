// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// プレビューの計算が読む、持ち主(いろかのウィンドウ)の状態と知らせ先。<see cref="PreviewPipeline"/> は
    /// ウィンドウを型で知らない(Application は UI を使わない)。
    /// </summary>
    internal interface IPreviewPipelineHost
    {
        Texture2D SourceTexture { get; }
        EditingSession Editing { get; }
        /// <summary>ソロ表示中のゾーン(表示だけ。無ければ null)。</summary>
        ColorZone SoloZone { get; }
        /// <summary>今あるゾーンの id(削除されたゾーンの、自動調整の入力を刈る)。</summary>
        void RetainZoneInputs(ICollection<string> liveZoneIds);
        void RequestRepaint();
        /// <summary>確定段の色替えで記録したデバッグの記録(記録していなければ null)。</summary>
        void PublishDebugCapture(IDebugCapture capture);
    }

    /// <summary>
    /// 確定段(フル段)の結果を表示へ渡すもの。表示はこれを受け取ってテクスチャへ上げる(<see cref="PreviewPipeline.TakePendingFrame"/>)。
    /// </summary>
    internal struct PreviewFrame
    {
        public Color32[] Processed;   // 表示寸法の変更後
        public Color32[] Raw;         // 表示寸法の変更前
        public int Width, Height;
        // 確定(フル段)か。確定なら拡大表示をすぐ作り直す。
        public bool IsFinal;
        // ドラッグの追従(プロキシだけ)か。
        public bool IsDrag;
        // どの再生成のどの段か(体感速度の計測用。転送が済んだ時刻を打つ)。
        public PreviewLatencyCycle Latency;
        public LatencyStageMarks Stage;
    }

    /// <summary>
    /// プレビューの計算(0.4.0 の計画の 4-4)。概要段(プロキシ)・確定段(フル段)・ドラッグの追従の状態と、
    /// 選択キャッシュ・出力バッファ・拡大表示の土台・シーンへの反映を持つ。表示(テクスチャ・差分・拡大表示)は
    /// <c>PreviewView</c> が受け取った結果から作る。
    /// 再計算はウィンドウの描画から進める(<see cref="Tick"/>。EditorApplication.update へ移すと、隠れている間の
    /// CPU の使い方とシーンへの反映が変わる)。メインスレッド専用(ジョブの中身はワーカー)。
    /// </summary>
    internal sealed unsafe class PreviewPipeline : IDisposable
    {
        private readonly IPreviewPipelineHost _host;

        internal PreviewPipeline(IPreviewPipelineHost host)
        {
            _host = host;
            Resume();
        }

        /// <summary>
        /// 閉じた後(<see cref="Suspend"/>)に開き直したとき、手放した選択キャッシュ・出力バッファを作り直す
        /// (ウィンドウの OnEnable。作ってあれば何もしない)。
        /// </summary>
        internal void Resume()
        {
            _selectionCache ??= new SelectionCache();
            _proxySelectionCache ??= new SelectionCache();
            _outputBuffers ??= new PreviewOutputBuffers();
        }

        /// <summary>
        /// 原本から導いた状態(ジョブ・確定段の出力・キャッシュ)を捨てた。表示も原本から導いたもの(拡大表示・見た目)を
        /// 捨てる。
        /// </summary>
        internal event Action SourceInvalidated;

        // ── 作り直しの要求 ─────────────────────────────────────────

        // 立てた時刻を体感速度の起点(操作の時刻)として控える(PreviewLatency)。初期値の true は操作ではない。
        private bool _dirty = true;
        private PreviewInputClock _inputClock;
        private PreviewInputClock InputClock => _inputClock ??= new PreviewInputClock();

        /// <summary>作り直しを頼まれている(次の <see cref="Tick"/> で受け取る)。</summary>
        internal bool Dirty => _dirty;

        /// <summary>作り直しを頼む。今の時刻を操作の時刻として控える。</summary>
        internal void MarkDirty()
        {
            _dirty = true;
            InputClock.NoteInput(PreviewLatencyCycle.Now);
        }

        // 次回の再生成でプロキシ段(低解像度の概要表示)を使わない 1 回限りのフラグ。GeneratePreviewAsync が消費する。
        private bool _skipProxyOnce;

        /// <summary>
        /// 次のプレビュー再生成では、段階的リファインのプロキシ段を使わない(作り直しの要求は別に立てる。
        /// IrocaWindow.NotifyMasksEditedFullRefine)。
        /// 確定表示が既にある状態での差分的な更新(AI 提案のマスク反映など)でプロキシを挟むと、
        /// 鮮明な表示が一瞬低解像度へ戻る「ちらつき」になる(クリックキュー化でコミットが
        /// 連続すると特に目立つ)。フル段のみで、旧表示を保ったまま静かに差し替える。
        /// 通常の作り直しの要求が直後に重なった場合もフラグは 1 回で消費され、
        /// その再生成のプロキシが 1 度飛ぶだけ(結果は同じ・フィードバックが少し遅れるのみ)。
        /// </summary>
        internal void SkipProxyOnce() => _skipProxyOnce = true;

        // ── ジョブ ─────────────────────────────────────────────

        // 戻り値は (processed, raw) のタプル。raw(ダウンサンプル済み元表示)もジョブ側で
        // 生成することで、テクスチャ切替直後のキャッシュミス時にメインスレッドで走っていた
        // BoxDownsample のヒッチをバックグラウンドへ追い出す。
        // full はフル解像度の出力(Mono のヒープの外。結果を受け取った側が参照を 1 つ持つ)。シーンのアバターへの
        // プレビュー LivePreview へも渡す。
        // display はフル段の出力を拡大表示の寸法(取り込み後の寸法)へ縮めたもの(その寸法で映さないときは null)。
        private readonly PreviewJob<(Color32[] processed, Color32[] raw, NativePixels full, NativePixels display)> _previewJob =
            new PreviewJob<(Color32[] processed, Color32[] raw, NativePixels full, NativePixels display)>();
        // 段階的リファインの第1段。ソースが大きい(scale<1)とき、まず縮小プロキシで概要を即表示する
        // 専用ジョブ。完了 apply で _previewJob(フル解像度)を同一スナップショットでスケジュールする。
        // proxy はソースをプロキシ寸法へ縮小した処理前の画素(_proxySource へ確定する)。
        private readonly PreviewJob<(Color32[] processed, Color32[] raw, Color32[] proxy)> _proxyJob =
            new PreviewJob<(Color32[] processed, Color32[] raw, Color32[] proxy)>();

        // 表示へまだ渡していない結果(TakePendingFrame で渡す)。
        private PreviewFrame _pending;

        /// <summary>概要段・確定段のどちらかが走っている(テストが終わりを待つのにも使う)。</summary>
        internal bool IsGenerating => _proxyJob.IsRunning || _previewJob.IsRunning;

        // 直近に確定したフル段のフル解像度の出力(参照を 1 つ持つ)と、その元画素(同じ元画素から作ったかを
        // 見るためだけに覚える。参照は持たない)。拡大表示(詳細クロップ)はここから切り出す。元画素が変わったら
        // 使わない(InvalidateSourceCache / InvalidateFullOutput)。
        private NativePixels _fullOutput;
        private NativePixels _fullOutputSource;

        /// <summary>今の原本で作った確定段の出力(フル解像度)。まだ無ければ null。</summary>
        internal NativePixels FullOutput => ReferenceEquals(_fullOutputSource, TrueSource) ? _fullOutput : null;

        /// <summary>確定段の出力を差し替えるたびに進む番号(表示が「隠した後に新しくなったか」を見る)。</summary>
        internal int FullOutputVersion { get; private set; }

        // フル段の出力バッファの使い回し(PreviewOutputBuffers)。置き換えられた前の出力を次のフル段で再利用する。
        private PreviewOutputBuffers _outputBuffers;

        private double _lastDirtyTime;
        // 手を止めてからフル解像度で確定するまでの待ち。ドラッグ中はこの間、プロキシだけを回して
        // 追従する(大きいテクスチャのフル段は数百 ms かかり、毎フレーム回すと取り消しの繰り返しになる)。
        private const double PreviewDebounceSeconds = 0.2;
        // ドラッグ中の追従プレビュー。_dragReq は直近の追従の入力、_dragReqStale はそれより後に
        // 操作があったか。_dragRunning は走っているジョブが追従のもの(新しい操作で取り消さない)か。
        private PreviewRequest _dragReq;
        private bool _dragReqStale = true;
        private bool _dragRunning;
        private bool IsDragPreviewRunning => _dragRunning && (_proxyJob.IsRunning || _previewJob.IsRunning);

        private Texture2D _cachedSourceTexture;
        // TrueSource と同じものを指す(どの元画素で作ったキャッシュかを見るためだけ。参照は持たない)。
        private NativePixels _cachedSrcPixels;
        private Color32[] _cachedRawDisplay;
        private int _cachedSrcW, _cachedSrcH;
        private int _cachedPrevW, _cachedPrevH;

        // プロキシ段の入力(ソースをプロキシ寸法へ縮小したもの)と、その表示寸法の before。ソース画素と寸法だけで
        // 決まるので、同じソースの間は使い回す(操作のたびに 4K から縮小し直さない)。ジョブは読むだけで、
        // このフィールドを触るのはメインスレッド(スケジュール時に読み、apply で確定)だけ。
        private sealed class ProxySourceCache
        {
            public NativePixels src;     // 縮小元のソース画素(同じものかを照合するだけ。参照は持たない)
            public int w, h, prevW, prevH;
            public Color32[] proxy;      // プロキシ寸法(処理前)
            public Color32[] display;    // proxy を表示寸法へ縮小したもの(寸法が同じなら proxy と同じ配列)
        }
        private ProxySourceCache _proxySource;

        // 選択結果キャッシュ: 「ターゲット色など再着色のみ」を変えたプレビュー再生成で、選択フェーズ
        // (Match/FloodFill/穴埋め/境界/ブラー/マスク再適用)を再計算せず復元して高速化する。
        // 出力はフル再計算と byte 一致(ProcessPixelsArray が選択パラメータのキーで管理)。テクスチャ/
        // 寸法が変わると画素前提が崩れるので Clear する。
        private SelectionCache _selectionCache;

        // 段階的リファインのプロキシ専用選択キャッシュ。プロキシはフルと寸法が異なり、SelectionCache は
        // zoneId 単位で (W,H) 一致を見て上書きするため、フルと共有するとスラッシュする。別インスタンスに
        // 分けてプロキシの再着色のみ変更も高速化する。テクスチャ/寸法変更時は _selectionCache と同時に Clear。
        private SelectionCache _proxySelectionCache;

        /// <summary>表示中のプレビューを作った選択(右クリックの「いま塗っているゾーン」が読む)。閉じた後は null。</summary>
        internal SelectionCache Selection => _selectionCache;

        // ── 原本 ─────────────────────────────────────────────

        // プレビュー処理に使う原本の画素(作業解像度。SourcePixelCache)。持ち主は編集中の文書(EditingSession)で、
        // テクスチャ単位で持つ。読み直すかどうか(取り込み後の寸法が変わった)と、原本から導いた状態を捨てるのは
        // ここが決める(EnsureSource)。
        internal SourcePixelCache Sources => _host?.Editing?.Sources;
        internal NativePixels TrueSource => Sources?.Pixels;
        internal int TrueSourceW => Sources?.Width ?? 0;
        internal int TrueSourceH => Sources?.Height ?? 0;

        // 拡大表示に映す寸法(取り込み後の寸法。ゲームに出る大きさ。WorkingResolution.DisplaySize)の原本と、
        // フル段の出力をその寸法へ縮めたもの(面平均。非破壊ビルドがテクスチャを作るときと同じ縮小)。
        // 作業解像度のままでよいとき(取り込みで小さくならない・「元の解像度」の設定がオン)は null で、
        // 作業解像度のものをそのまま映す(WantsDisplaySize)。
        private NativePixels _displaySource, _displayOutput;
        private int _displayW, _displayH;

        // Play を始めたときのドメインリロードの直後は、原本の読み直し(デコード)と色替えの計算し直しを、
        // ウィンドウが操作されるまで待つ(Play を始めてすぐの動作確認と重なって、エディタが止まらないように)。
        // 待つのは Play 中だけで、どこかで原本を読んだら(自動調整・AI・書き出しなど画素が要る操作)終わる。
        // 操作の検出と解除は IrocaWindow.TrackDeferredPreview。
        private bool _deferSourceLoad;

        internal bool SourceLoadDeferred => _deferSourceLoad && EditorApplication.isPlayingOrWillChangePlaymode;
        internal void DeferSourceLoad() => _deferSourceLoad = true;
        internal void EndSourceLoadDeferral() => _deferSourceLoad = false;

        // ── 拡大表示の解像度(表示だけの設定) ─────────────────────────
        // オフ(既定): 取り込み後の寸法(インポート設定の Max Size。ゲームに出る大きさ)へ縮めて映す。
        // オン: 作業解像度(色替えを計算している解像度。元の画像の辺が 8192 以下なら元の解像度)のまま映す。
        // 縮めた原本・出力を持たないので、その分メモリが減る。処理の解像度(WorkingResolution.Size)は変えないので、
        // 色替えの結果・書き出し・非破壊ビルド・自動化には効かない(0.3.0 で消えた「元の解像度で編集」と違う点)。
        // 表示の設定だが、確定段が縮めたものを作るかを決めるので、計算の側に置く。
        internal const string DetailFullResolutionPrefKey = IrocaPrefs.PreviewDetailFullResolution;
        private static bool? s_detailFullResolution;

        /// <summary>拡大表示を作業解像度のまま映すか。この PC のいろか全体の表示の設定。</summary>
        internal static bool DetailFullResolution
        {
            get => s_detailFullResolution ??= EditorPrefs.GetBool(DetailFullResolutionPrefKey, false);
            set
            {
                s_detailFullResolution = value;
                EditorPrefs.SetBool(DetailFullResolutionPrefKey, value);
            }
        }

        /// <summary>
        /// 保持しているフル段の出力を捨てる(拡大表示の切り出し元にしない)。処理したゾーンの集合が
        /// 変わったとき(ソロ表示の切り替え)に呼ぶ。次のフル段の確定までは拡大表示を作り直さない。
        /// </summary>
        internal void InvalidateFullOutput()
        {
            RetireFullOutput();
            _fullOutputSource = null;
        }

        // いまのフル段の出力を使い回しへ回し(持っていた参照を渡す)、_fullOutput を空にする。拡大表示のジョブが
        // 切り出し中・シーンへの反映が保持中なら、それぞれ参照を持っているので、手放されるまでは使い回されない。
        private void RetireFullOutput()
        {
            _displayOutput?.Release();
            _displayOutput = null;
            var old = _fullOutput;
            _fullOutput = null;
            if (old == null) return;
            if (_outputBuffers != null) _outputBuffers.Retire(old);
            else old.Release();
        }

        /// <summary>
        /// ソース画素が変わったとき（テクスチャ差し替え・セッションリセット・エクスポートで
        /// 元ファイルを上書き）に、その画素から導かれた状態を漏れなく捨てる。
        /// ここで捨て損ねた状態は「プレビュー＝実出力」の一致を破る:
        /// 走行中ジョブは旧画素の結果を新テクスチャの表示へ apply し、保持しているフル段の出力を
        /// 残すと、拡大表示(詳細クロップ)が旧テクスチャの色替え結果を切り出して見せる。
        /// 表示が原本から導いたもの(拡大表示・見た目)は <see cref="SourceInvalidated"/> を受けて捨てる。
        /// </summary>
        internal void InvalidateSourceCache()
        {
            // 走行中のジョブは旧画素を処理中。世代をぶつけて apply ごと無効化する。
            CancelJobsAndDropPending();
            // 追従プレビューの入力とフル段の出力も旧画素のもの。使い回さない。
            InvalidateFullOutput();
            // 寸法の違うテクスチャへ移ると使えないので、置いてある出力バッファも手放す。
            _outputBuffers?.Clear();
            _dragReq = null;
            _dragReqStale = true;
            _dragRunning = false;

            _cachedSourceTexture = null;
            _cachedSrcPixels = null;
            _cachedRawDisplay = null;
            _proxySource = null;
            _cachedSrcW = _cachedSrcH = 0;
            _cachedPrevW = _cachedPrevH = 0;
            DropTrueSource();

            // ソース画素が変わる = キャッシュ済み選択の前提が変わるので選択キャッシュも破棄する。
            _selectionCache?.Clear();
            _proxySelectionCache?.Clear();

            SourceInvalidated?.Invoke();
        }

        /// <summary>
        /// プレビュー処理用のソース画素(<see cref="SourcePixelCache"/>)を確保する。別のテクスチャの画素や、取り込み後の
        /// 寸法が変わる前の画素を持っていたら、そこから導いた状態ごと捨ててから読む。
        /// 成功すると <see cref="TrueSource"/> / <see cref="TrueSourceW"/> /
        /// <see cref="TrueSourceH"/> が有効になる。
        /// </summary>
        internal bool EnsureSource(Texture2D tex)
        {
            var sources = Sources;
            if (tex == null || sources == null) return false;
            if (sources.Texture == tex && sources.Pixels != null)
            {
                if (sources.BasisWidth == tex.width && sources.BasisHeight == tex.height) return true;
                // 取り込み設定(Max Size)が変わって、作業解像度か拡大表示の寸法が変わり得る。
                // 旧画素から導いた状態ごと捨てて読み直し、表示も作り直す。
                InvalidateSourceCache();
                MarkDirty();
            }
            // 失敗もテクスチャ単位で覚える(SourcePixelCache.IsKnownUnreadable)。
            if (sources.IsKnownUnreadable(tex)) return false;
            // 別のテクスチャの画素を持ったまま読み直す(SwitchSourceTexture を通らない切り替え)ときは、古い画素を
            // 指したままのジョブ・キャッシュ・ドラッグの追従の入力も捨てる(手放した画素へ参照を足しに行かないように)。
            if (sources.Pixels != null && sources.Texture != tex) InvalidateSourceCache();

            if (!sources.Load(tex)) return false;
            // 拡大表示に映す寸法の原本もここで作る(取り込みで小さくなり、「元の解像度」の設定がオフのときだけ。
            // 4K→2K で数十 ms)。
            if (WantsDisplaySize(out int dw, out int dh)) BuildDisplaySource(dw, dh);
            _deferSourceLoad = false;
            return true;
        }

        // 処理用の画素を手放し、どのテクスチャを読んだか・読めなかったかも忘れる。拡大表示に映す寸法の原本も手放す。
        // 読んでいる途中のジョブは自分の参照を持っているので、ここで返しても消えない。
        private void DropTrueSource()
        {
            Sources?.Clear();
            _displaySource?.Release();
            _displaySource = null;
            _displayW = _displayH = 0;
        }

        // 拡大表示を取り込み後の寸法へ縮めて映すか(取り込みで小さくなり、「元の解像度」の設定がオフ)と、その寸法。
        private bool WantsDisplaySize(out int w, out int h)
        {
            WorkingResolution.DisplaySize(TrueSourceW, TrueSourceH, Sources.BasisWidth, Sources.BasisHeight, out w, out h);
            return !DetailFullResolution && (w != TrueSourceW || h != TrueSourceH);
        }

        // 拡大表示に映す寸法の原本を作る。いまの原本で作ったフル段の出力があれば、それも同じ寸法へ縮める
        // (設定をオフへ戻したとき。色替えはやり直さない。確定段のジョブが作るものと同じ縮小なので同じ画素になる)。
        private void BuildDisplaySource(int w, int h)
        {
            _displayW = w;
            _displayH = h;
            _displaySource = NativePixels.Allocate(w, h);
            WorkingResolution.ResizeArea(TrueSource.Ptr, TrueSourceW, TrueSourceH, w, h, _displaySource.Ptr);
            _displayOutput?.Release();
            _displayOutput = _fullOutput != null && ReferenceEquals(_fullOutputSource, TrueSource)
                ? ShrinkToDisplay(_fullOutput)
                : null;
        }

        // フル段の出力(作業解像度)を拡大表示に映す寸法へ縮めたもの(参照 1 で返す)。
        private NativePixels ShrinkToDisplay(NativePixels full)
        {
            var display = NativePixels.Allocate(_displayW, _displayH);
            WorkingResolution.ResizeArea(full.Ptr, full.Width, full.Height, _displayW, _displayH, display.Ptr);
            return display;
        }

        // 縮めた原本・出力を手放す(拡大表示は作業解像度のものを映す)。
        private void ReleaseDisplaySource()
        {
            _displaySource?.Release();
            _displaySource = null;
            _displayOutput?.Release();
            _displayOutput = null;
            _displayW = _displayH = 0;
        }

        /// <summary>
        /// 拡大表示の土台を「元の解像度」の設定(<see cref="DetailFullResolution"/>)に合わせる。合っていれば何もしない
        /// (設定はこの PC のいろか全体のもので、どのウィンドウも次に描くときに合わせる)。土台を作り直したら true
        /// (表示は今の拡大表示を捨てて作り直す)。色替えの結果には効かない(表示の寸法だけ)。
        /// </summary>
        internal bool SyncDetailResolution()
        {
            if (TrueSource == null) return false;
            bool want = WantsDisplaySize(out int dw, out int dh);
            if (want == (_displaySource != null)) return false;
            if (want) BuildDisplaySource(dw, dh);
            else ReleaseDisplaySource();
            return true;
        }

        // 拡大表示(詳細クロップ)の座標の土台。取り込み後の寸法で映すときはその寸法の原本、そうでなければ作業解像度の原本。
        internal NativePixels DetailSource => _displaySource ?? TrueSource;
        internal int DetailW => _displaySource != null ? _displayW : TrueSourceW;
        internal int DetailH => _displaySource != null ? _displayH : TrueSourceH;

        // 拡大表示の切り出し元(いまの原本で作ったフル段の出力。DetailSource と同じ寸法)。まだ無ければ null。
        internal NativePixels DetailOutput
        {
            get
            {
                if (!ReferenceEquals(_fullOutputSource, TrueSource)) return null;
                return _displaySource != null ? _displayOutput : _fullOutput;
            }
        }

        /// <summary>
        /// いま持っている処理用の画素(作業解像度)の寸法。まだ読んでいない・読めなかったときは false。
        /// 書き出しが、終わった後に作業配列を手放すか(寸法が違うときだけ)を決めるのに使う。
        /// </summary>
        internal bool TryGetWorkingSize(out int w, out int h)
        {
            w = TrueSourceW;
            h = TrueSourceH;
            return TrueSource != null && w > 0 && h > 0;
        }

        /// <summary>直近に確保した処理用の画素が、元ファイルをデコードしたものか(取り込み済みテクスチャではなく)。</summary>
        internal bool TrueSourceFromFile => TrueSource != null && Sources.FromFile;

        /// <summary>
        /// このテクスチャから処理用の画素を取れるか（原本ファイルのデコード、または
        /// インポート済みテクスチャの GetPixels32）。UI の可否判定が「Read/Write が
        /// 有効か」だけを見ていると、PNG/JPG のように原本を直接読める場合でも
        /// 不要にインポート設定の変更を要求してしまうため、実際に取れるかで判定する。
        /// 成否はテクスチャ単位でキャッシュする。
        /// Play 中のリロード直後で原本の読み直しを待っている間(<see cref="SourceLoadDeferred"/>)は、読まずに
        /// 取れるものとして答える(UI の可否判定のために読むと、待たせている意味が無くなる)。
        /// </summary>
        internal bool CanProvideSourcePixels(Texture2D tex) =>
            tex != null && (SourceLoadDeferred || EnsureSource(tex));

        /// <summary>
        /// 自動調整など他ビューが、プレビューと同一の true source 画素(作業解像度)で解析する
        /// ための読み取り専用アクセサ。返すのは内部キャッシュの共有インスタンス(参照は足さない)なので、
        /// 呼び出し側は書き換えないこと。メインスレッドの外や後で読むなら、呼び出し側が
        /// <see cref="NativePixels.AddRef"/> して持ち、読み終えたら Release すること(テクスチャの切り替え・
        /// ウィンドウを閉じたときに、ここが持つ参照は手放される)。
        /// 取得不能（元ファイル読込失敗 かつ 非 Readable）なら false。
        /// </summary>
        internal bool TryGetTrueSourcePixels(Texture2D tex, out NativePixels pixels, out int w, out int h)
        {
            if (EnsureSource(tex))
            {
                pixels = TrueSource;
                w = TrueSourceW;
                h = TrueSourceH;
                return true;
            }
            pixels = null;
            w = h = 0;
            return false;
        }

        /// <summary>
        /// 埋め込みキャッシュのキー。テクスチャの中身が変わったら別キーになるよう
        /// アセットパス + ファイル更新時刻 + 実寸で構成する。
        /// 自動調整の証拠要求も AI 提案と同じキーで SetSource するために使う(別キーだとソース切替扱いになり埋め込みを捨てる)。
        /// </summary>
        internal string SourceCacheKey()
        {
            var sourceFor = Sources?.Texture;
            string path = sourceFor != null ? AssetDatabase.GetAssetPath(sourceFor) : null;
            long ticks = 0;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                ticks = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            return $"{path}|{ticks}|{TrueSourceW}x{TrueSourceH}";
        }

        // ── 寿命 ─────────────────────────────────────────────

        public void Dispose()
        {
            Suspend();
            _proxyJob.Dispose();
            _previewJob.Dispose();
        }

        /// <summary>
        /// ジョブを止め、プレビューの原本・確定段の出力・使い回しのバッファ(Mono のヒープの外。4K で 1 本 64MB)・
        /// 選択キャッシュを手放す。閉じる・リロードの前に呼ばれるので、ここで返せば OS へ戻る。この後は使わない
        /// (開き直したら作り直す)。
        /// </summary>
        internal void Suspend()
        {
            InvalidateSourceCache();
            // 止めたジョブが後から返してくる出力も、置かずにすぐ手放す。
            _outputBuffers?.Close();
            _outputBuffers = null;
            // 選択キャッシュ(ゾーンごとに 4K で 64MB・8K で 256MB)はネイティブメモリなので、閉じる・リロードの前に
            // ここで解放すれば OS へ戻る。取り消した計算が後から Store しても残らない(Dispose 済みは保存しない)。
            _selectionCache?.Dispose();
            _selectionCache = null;
            _proxySelectionCache?.Dispose();
            _proxySelectionCache = null;
            _lastDirtyTime = 0;
        }

        /// <summary>
        /// 2 つのジョブ(追従・確定)を止め、まだ表示へ渡していない結果を捨てる。
        /// ソースの差し替え(<see cref="InvalidateSourceCache"/>)と停止(<see cref="Suspend"/>)の共通部分。
        /// </summary>
        private void CancelJobsAndDropPending()
        {
            _proxyJob.Cancel();
            _previewJob.Cancel();
            _pending = default;
        }

        // ── 再生成の状態機械 ─────────────────────────────────────

        /// <summary>
        /// プレビュー再生成の状態機械。作り直しの要求を受けて打刻し、ドラッグ中はプロキシだけで追従、
        /// 手を止めて PreviewDebounceSeconds 経つか離したらフル段で確定する。
        /// 表示が毎フレーム(描画のたびに)呼ぶ。<paramref name="pointerHeld"/> はマウスのボタンを押したまま
        /// (スライダーのドラッグ中など。GUIUtility.hotControl)か。
        /// </summary>
        internal void Tick(bool pointerHeld)
        {
            if (_dirty)
            {
                _lastDirtyTime = EditorApplication.timeSinceStartup;
                _dragReqStale = true;
                // プロキシ・フル両段をキャンセル。プロキシ進行中の再ダーティでは、プロキシの
                // キャンセル(世代ぶつけ)で apply が抑止されフルが起動しない。
                // ただしドラッグ中の追従プレビューは取り消さない。ドラッグ中は値がほぼ毎フレーム
                // 変わるので、取り消すと一度も画面に出ない。終わってから最新の値で回し直す。
                if (!IsDragPreviewRunning)
                {
                    _proxyJob.Cancel();
                    _previewJob.Cancel();
                }
                _host.RequestRepaint();
                _dirty = false;
            }
            else if (!_proxyJob.IsRunning && !_previewJob.IsRunning &&
                     _lastDirtyTime > 0 &&
                     (!pointerHeld ||
                      (EditorApplication.timeSinceStartup - _lastDirtyTime)
                          >= PreviewDebounceSeconds))
            {
                _lastDirtyTime = 0;
                // 最後の追従プレビュー以降に操作が無ければ、その入力のままフル段だけで確定する
                // (同じ入力のプロキシをやり直すと、確定がその分遅れるだけ)。
                if (_dragReq != null && !_dragReqStale) FinishDragPreview();
                else GeneratePreviewAsync(dragOnly: false);
            }
            else if (!_proxyJob.IsRunning && !_previewJob.IsRunning &&
                     _lastDirtyTime > 0 && _dragReqStale)
            {
                // ドラッグ中(手を止めて PreviewDebounceSeconds 経つまで): プロキシだけを回して
                // 絵を操作に追従させる。フル段は手を止めるか離してから。
                GeneratePreviewAsync(dragOnly: true);
            }
            else if (_lastDirtyTime > 0 || _proxyJob.IsRunning || _previewJob.IsRunning)
            {
                _host.RequestRepaint();
            }
        }

        /// <summary>
        /// 表示へまだ渡していない結果を取り出す(無ければ false)。表示はこれをテクスチャへ上げ、上げ終えたら
        /// <see cref="NoteShown"/> を呼ぶ。
        /// </summary>
        internal bool TakePendingFrame(out PreviewFrame frame)
        {
            frame = _pending;
            _pending = default;
            return frame.Processed != null && frame.Raw != null;
        }

        /// <summary>
        /// 直近の再生成の結果が画面に出た(<see cref="TakePendingFrame"/> の結果を上げ終えた)。それより後の操作は、
        /// まだ画面に出ていない操作として体感速度の計測へ持ち越す。
        /// </summary>
        internal void NoteShown() => InputClock.NoteShown();

        // dragOnly: ドラッグ中の追従。プロキシ段だけを回し(縮小しないテクスチャはフル段がそのまま
        // 軽いのでフル段)、入力を _dragReq に控える。確定は FinishDragPreview が同じ入力で行う。
        // internal は WindowOutputParityTests が OnGUI を回さずに作り直すため。
        internal void GeneratePreviewAsync(bool dragOnly = false)
        {
            long prepStart = PreviewLatencyCycle.Now;
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !EnsureSource(sourceTexture)) return;

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

            var editing = _host.Editing;
            var maskSnap = editing.Masks.ToSnapshot();

            var session = editing.State;
            // 削除されたゾーンの選択キャッシュ(4K で 67MB/ゾーン)が恒久残留しないよう、セッションに
            // 現存するゾーン id 以外のエントリを毎回刈る(無効ゾーンは残す=再有効化で再計算を避ける)。
            var liveZoneIds = new HashSet<string>(session.zones.Select(z => z.id));
            // 自動調整の入力(証拠マスク)も同じ集合で刈る。
            _host.RetainZoneInputs(liveZoneIds);
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
            // SkipProxyOnce 経由(AI 提案コミット等)はプロキシを飛ばし、確定表示を保った
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
            public List<ColorZone> zonesSnapshot;
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
                    _pending = new PreviewFrame
                    {
                        Processed = result.processed, Raw = result.raw,
                        Width = req.prevW, Height = req.prevH,
                        IsFinal = false, IsDrag = !chainFull,
                        Latency = req.latency, Stage = marks,
                    };
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
                    // 次フレームの表示の反映だが、差は 1 フレームなので近似で計上。
                    MaskSuggestPerf.NotifyFullPreviewApplied();
                    _pending = new PreviewFrame
                    {
                        Processed = result.processed, Raw = result.raw,
                        Width = req.prevW, Height = req.prevH,
                        IsFinal = true, IsDrag = false,
                        Latency = req.latency, Stage = marks,
                    };
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
                    unchecked { FullOutputVersion++; }
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
                    _host.PublishDebugCapture(req.debugCap);
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
    }
}
