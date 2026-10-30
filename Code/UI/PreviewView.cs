// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// メインプレビュー描画、比較 / 差分モード、ズーム、プレビュー生成ジョブの起動、
    /// 表示用 Texture の所有を担当する。詳細プレビューは DetailPreviewView を保持する補助形式。
    /// </summary>
    [System.Serializable]
    internal partial class PreviewView
    {
        public float previewZoom = 1f;
        public bool comparisonMode;
        public bool diffMode;

        private const float MinPreviewZoom = 0.25f;
        // ピクセル単位で確認できるよう、最大ズーム時に「ソース 1px が画面上で最低
        // PixelInspectTargetPx ピクセルになる」ところまで拡大を許可する。高解像度
        // プレビューが映すのはソース画素で、画面倍率は scale*表示倍率なので 表示倍率 = target/scale。
        // 大きいテクスチャ(scale 小)ほど高ズームを許す。小さいテクスチャでも最低 16x。
        private const float PixelInspectTargetPx = 16f;
        // 表示倍率そのものの上限。プレビュー枠(内側 ScrollView)の内容幅は
        // previewTexture.width * 表示倍率 まで広がり、ここでは最大 MaxSize(384) * 64 ≒ 24.6k px。
        // これ以上は IMGUI のレイアウト/スクロール可動域とパン操作量が現実的でなくなるため
        // 頭打ちにする(4K テクスチャなら 64x でソース 1px ≒ 画面 6px 相当)。
        private const float AbsoluteMaxPreviewZoom = 64f;

        // テクスチャの縮小率 scale に応じた表示倍率(EffectiveZoom)の上限。
        // ズーム(previewZoom)の上限はこれを全体表示の倍率で割ったもの(Draw 参照)。
        private static float ComputeMaxZoom(float scale)
        {
            if (scale <= 0f) return AbsoluteMaxPreviewZoom;
            return Mathf.Clamp(PixelInspectTargetPx / scale, PixelInspectTargetPx, AbsoluteMaxPreviewZoom);
        }

        // 表示倍率が 104% のような半端な値にならないよう、ズームは「きれいな数字」の
        // 固定ストップにスナップさせる。1 ノッチ＝隣のストップ。25%〜12800% を網羅
        // (全体表示が小さい狭いウィンドウでは、同じ表示倍率に届くのにズームの数字が大きくなる)。
        private static readonly float[] ZoomStops =
        {
            0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f, 5f, 6f,
            8f, 10f, 12f, 16f, 20f, 24f, 32f, 40f, 48f, 64f, 96f, 128f
        };

        // ── 全体表示(100%)の倍率 ──────────────────────────────────
        // ズーム 100% は「画像全体がプレビュー枠にちょうど収まる大きさ」。以前は 100% =
        // プレビューテクスチャ(長辺 MaxSize=384)1 画素 = 1pt の固定で、ウィンドウを広げると
        // 画像が左上に小さく残って右と下が大きく空き、狭めると 100% でも画像の一部しか
        // 見えずにスクロールバーが出ていた。枠の大きさから倍率を決め、previewZoom はそれに
        // 掛ける相対値にする(100%/リセットで常に全体が見える)。
        // 画面上の大きさ(プレビューテクスチャ 1 画素あたりの pt)は EffectiveZoom が正。
        // テクスチャより大きく引き伸ばす分は、詳細プレビュー(フル解像度の切り出し)がくっきり描き直す。
        [System.NonSerialized] private float _fitScale = 1f;
        // 全体表示の倍率の下限(枠が異常に小さい実測値で 0 や負にならないための保険)。
        private const float MinFitScale = 0.05f;
        // 全体表示の画像と枠の間に残す丸めの逃げ(px)。これが無いと浮動小数の端数で
        // 100% でもスクロールバーが出ることがある。
        private const float FitSlack = 2f;

        /// <summary>画面上の表示倍率(プレビューテクスチャ 1 画素が何 pt か)。全体表示の倍率 × ズーム。</summary>
        internal float EffectiveZoom => previewZoom * _fitScale;

        private int PanelCount => (comparisonMode && rawPreviewTexture != null) ? 2 : 1;

        // 比較モードの Before/After 見出し行の高さ。
        private static float ComparisonLabelHeight =>
            EditorGUIUtility.singleLineHeight + EditorStyles.label.margin.vertical;

        // プレビュー枠に使える高さが分かっているか(カラム高はホストが渡し、枠より上の UI の
        // 高さは前フレームの Repaint で実測する。どちらも無い初回フレームは従来の 1:1 表示)。
        private bool HasFrameHeightBudget => availableColumnHeight > 0f && _chromeAboveViewportH > 0f;

        // プレビュー枠に使える高さ(カラム高 − 枠より上の UI − 枠下の余白)。低いウィンドウでも
        // 下限(MinViewportHeight)は確保し、あふれた分は外側の ScrollView のスクロールに任せる。
        private float FrameHeightBudget => Mathf.Max(IrocaConsts.Preview.MinViewportHeight,
            availableColumnHeight - _chromeAboveViewportH - ViewportBottomPadding);

        /// <summary>
        /// 全体表示(ズーム 100%)の倍率。枠の内側(スクロールビューの余白と比較モードの見出し・
        /// パネル間隔を除く)に、画像全体がスクロールバー無しで収まる最大の倍率。
        /// </summary>
        private float ComputeFitScale(int texW, int texH, int panelCount, float frameW)
        {
            if (texW <= 0 || texH <= 0 || frameW <= 1f || !HasFrameHeightBudget) return 1f;
            var pad = GUI.skin.scrollView.padding;
            float innerW = frameW - pad.horizontal - (panelCount - 1) * IrocaConsts.Preview.PanelSpacing - FitSlack;
            float innerH = FrameHeightBudget - pad.vertical - FitSlack
                           - (panelCount == 2 ? ComparisonLabelHeight : 0f);
            float fit = Mathf.Min(innerW / (texW * (float)panelCount), innerH / texH);
            return Mathf.Max(MinFitScale, fit);
        }

        /// <summary>
        /// 全体表示の倍率を枠の大きさに合わせて更新する。倍率が変わったとき(ウィンドウのリサイズ・
        /// 比較モードの切替・枠より上の UI の増減)は、見ていた位置を保つようにスクロール量
        /// (表示 pt 単位)を同じ比で伸縮し、拡大表示を新しい倍率で作り直させる。
        /// </summary>
        private void UpdateFitScale(int panelCount, float frameW)
        {
            if (previewTexture == null) return;
            float fit = ComputeFitScale(previewTexture.width, previewTexture.height, panelCount, frameW);
            if (Mathf.Abs(fit - _fitScale) <= 1e-4f) return;
            _previewScrollPos *= fit / _fitScale;
            _fitScale = fit;
            _detailView.MarkViewChanged();
        }
        private const float ZoomEpsilon = 1e-4f;

        // Ctrl+スクロールズームの感度。マウスホイールやトラックパッドは機種によって
        // 1 回のスクロール操作で複数の ScrollWheel イベントを発生させたり、1 イベント
        // あたりの delta.y が大きかったりする。1 イベント = 1 ストップで処理すると、
        // こうしたデバイスではズームが一気に飛んで「感度が高すぎる」と感じる。
        // delta.y を蓄積し、この閾値ぶんたまるごとに 1 ストップだけ動かすことで、
        // デバイス差を吸収して操作感を一定（かつ控えめ）に保つ。値を大きくするほど
        // 1 ストップ進めるのに必要なスクロール量が増える＝感度が下がる。
        // 一般的なマウスのノッチ 1 段は delta.y≈3 なので、4.0 にすると概ね
        // 「1.5 ノッチで 1 ストップ」程度の落ち着いた感度になる。
        private const float ZoomScrollStepThreshold = 4.0f;

        // 現在のズームから、指定方向(zoomIn=拡大)へ 1 ストップ動いた値を返す。
        // 上限(maxZoom)・下限(MinPreviewZoom)を超えるストップは選ばない。
        private static float StepZoom(float current, bool zoomIn, float maxZoom)
        {
            if (zoomIn)
            {
                for (int i = 0; i < ZoomStops.Length; i++)
                    if (ZoomStops[i] > current + ZoomEpsilon && ZoomStops[i] <= maxZoom + ZoomEpsilon)
                        return ZoomStops[i];
                return SnapToStop(maxZoom, maxZoom); // これ以上拡大できない
            }
            for (int i = ZoomStops.Length - 1; i >= 0; i--)
                if (ZoomStops[i] < current - ZoomEpsilon && ZoomStops[i] >= MinPreviewZoom - ZoomEpsilon)
                    return ZoomStops[i];
            return MinPreviewZoom;
        }

        // 任意のズーム値を、有効範囲内で最も近いストップに丸める。
        // シリアライズで残った半端な値(例 1.04)を毎フレームここで整える。
        private static float SnapToStop(float zoom, float maxZoom)
        {
            float best = Mathf.Clamp(zoom, MinPreviewZoom, maxZoom);
            float bestDist = float.MaxValue;
            foreach (float s in ZoomStops)
            {
                if (s < MinPreviewZoom - ZoomEpsilon || s > maxZoom + ZoomEpsilon) continue;
                float d = Mathf.Abs(zoom - s);
                if (d < bestDist) { bestDist = d; best = s; }
            }
            return best;
        }

        [System.NonSerialized] public Texture2D previewTexture;
        [System.NonSerialized] public Texture2D rawPreviewTexture;
        // 差分表示は描いている変更前・変更後のテクスチャから GPU で描く(差分の画像は持たない)。
        [System.NonSerialized] private PreviewDiffDrawer _diffDrawer;

        // プレビューの計算(概要段・確定段・ドラッグの追従・選択キャッシュ・拡大表示の土台。PreviewPipeline)。
        // このビューは受け取った結果をテクスチャへ上げて描く。
        [System.NonSerialized] private PreviewPipeline _pipeline;
        internal PreviewPipeline Pipeline => _pipeline;

        /// <summary>
        /// プレビューの作り直しの要求。立てた時刻を体感速度の起点(操作の時刻)として控える(PreviewLatency)。
        /// 編集はウィンドウが EditingSession.Changed を受けて立てる(IrocaWindow.OnEditChanged)。
        /// 自動保存は Changed を直接見る(AutosaveSchedule)。下ろすのは計算の側(受け取ったとき)だけ。
        /// </summary>
        public bool previewDirty
        {
            get => _pipeline == null || _pipeline.Dirty;
            set
            {
                if (!value || _pipeline == null) return;
                _pipeline.MarkDirty();
                // 別ウィンドウ(プレビュー・マスク編集)の描き直しの合図にもする(IrocaWindow.DrawStamp)。
                _host?.BumpUiState();
            }
        }
        [System.NonSerialized] private Vector2 _previewScrollPos;

        // ── Undo/Redo で巻き戻さないビュー状態 ───────────────────────
        // Unity の Undo はウィンドウのシリアライズ状態を丸ごと記録・書き戻すため、
        // previewZoom（シリアライズ対象）も編集内容と一緒に巻き戻る。拡大して細部を
        // 見ている最中に Ctrl+Z（AI マスク提案・ブラシ塗りなど）を押すと、その操作を
        // 記録した時点のズームまで戻され、倍率が下がるとスクロールの可動域も詰められて
        // 見ていた箇所が左上へ飛ぶ。ズーム倍率とスクロール位置は「何を編集したか」では
        // なく「今どこを見ているか」なので、Undo 履歴に属さない。
        // static はシリアライズされない＝ Undo の書き戻し対象外なので、直近の描画で
        // 使ったビュー状態をここへ控え、Undo/Redo 後に書き戻して視点を保つ
        // （PreviewView 自体が書き戻しでインスタンスごと差し替わっても残る）。
        // キーは所有ウィンドウの InstanceID。別ウィンドウの値は復元に使わない。
        private static int s_viewStateOwner;
        private static float s_viewStateZoom;
        private static Vector2 s_viewStateScroll;

        // Ctrl+スクロールズームで未消化のスクロール量。ZoomScrollStepThreshold を
        // 超えたぶんだけストップを進め、端数は次イベントへ繰り越す（感度を下げるため）。
        [System.NonSerialized] private float _zoomScrollAccum;
        // プレビュー用 ScrollView の実測ビューポート幅。詳細クロップの可視範囲算出に使う。
        // テクスチャ実寸基準ではカラム/ウィンドウ幅と食い違うため、毎フレーム実測する。
        [System.NonSerialized] private float _viewportWidth;
        // プレビューカラム(外側 ScrollView)の高さ。ホストがレイアウト確定値を毎フレーム渡す。
        // プレビュー枠をこの中に収める動的高さ調整に使う。0 は未設定＝調整なし(固定高)。
        [System.NonSerialized] public float availableColumnHeight;
        // プレビューカラム(外側 ScrollView)の幅。ホストがレイアウト確定値を毎フレーム渡す。
        // プレビュー枠の幅をここへ固定するために使う(理由は Draw の frameW 算出コメント)。
        // 0 は未設定＝従来どおり ExpandWidth で親のクライアント幅いっぱいに伸ばす。
        [System.NonSerialized] public float availableColumnWidth;
        // 外側 ScrollView の内容座標系で、プレビュー枠より上に積まれた UI の実測高
        // (セクション見出し・操作行(比較/差分・ズーム率・生成状態)。縦並びレイアウトでは
        // 設定群も含む)。Repaint 時に実測し、次フレームの動的高さ算出に使う。
        [System.NonSerialized] private float _chromeAboveViewportH;
        // プレビュー枠の下の Space(4) と丸めの逃げ。動的高さの計算で差し引く。
        private const float ViewportBottomPadding = 8f;
        // ズーム率ラベルは毎フレーム描画されるため、ズーム値か言語が変わったときだけ
        // 文字列を再生成してアロケーションを避ける（IrocaWindow.EnsureZoneListCache と同方針）。
        [System.NonSerialized] private string _cachedZoomLabel;
        // 見出し行に収まらない狭いカラム用(操作方法の括弧書きを省く。ツールチップには残る)。
        [System.NonSerialized] private string _cachedZoomLabelShort;
        [System.NonSerialized] private int _cachedZoomPercent = -1;
        [System.NonSerialized] private LanguageMode _cachedZoomLang = (LanguageMode)(-1);

        // 拡大中のドラッグでは、古い拡大表示(前の確定から切り出したもの)を隠して、追従しているプロキシを
        // 見せる(古いくっきりした絵の下に新しい絵が隠れて、ドラッグ中に画面が止まって見えないように)。
        // 隠している間は古いフル段の出力から切り出し直さない(古い色の拡大表示が戻ってしまう)。隠したときの
        // 確定段の出力の番号(_hiddenAtFullVersion)から進んでいれば、切り出し元が新しくなっている。
        [System.NonSerialized] private bool _detailHiddenForDrag;
        [System.NonSerialized] private int _hiddenAtFullVersion;
        private bool FullOutputFreshSinceHide => _pipeline != null && _pipeline.FullOutputVersion != _hiddenAtFullVersion;
        // ペイント中のオーバーレイ再構築の最小間隔（10Hz）。
        // bool[] の clone とジョブ再スケジュールがメインスレッドで頻発すると
        // GC でフレームが詰まるため、ペイント中だけ意図的に間引く。
        private const double PaintOverlayThrottleSeconds = 0.1;

        // 原本の画素(作業解像度)。読み直すかどうかと、原本から導いた状態を捨てるのは計算の側が決める。
        private SourcePixelCache Sources => _pipeline?.Sources;
        private NativePixels TrueSource => _pipeline?.TrueSource;
        private int TrueSourceW => _pipeline?.TrueSourceW ?? 0;
        private int TrueSourceH => _pipeline?.TrueSourceH ?? 0;
        private bool EnsureTrueSource(Texture2D tex) => _pipeline != null && _pipeline.EnsureSource(tex);

        // Play を始めたときのドメインリロードの直後は、原本の読み直しと色替えの計算し直しを、ウィンドウが
        // 操作されるまで待つ(PreviewPipeline.SourceLoadDeferred)。
        internal bool SourceLoadDeferred => _pipeline != null && _pipeline.SourceLoadDeferred;
        // Draw が Layout のときに見た SourceLoadDeferred(同じフレームの他のイベントもそれで描く)。
        [System.NonSerialized] private bool _deferredAtLayout;
        internal void DeferSourceLoad() => _pipeline?.DeferSourceLoad();
        internal void EndSourceLoadDeferral() => _pipeline?.EndSourceLoadDeferral();

        [System.NonSerialized] private DetailPreviewView _detailView;

        [System.NonSerialized] private IrocaWindow _host;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
            if (_pipeline == null)
            {
                _pipeline = new PreviewPipeline(host);
                _pipeline.SourceInvalidated += OnSourceInvalidated;
            }
            _pipeline.Resume();
            _detailView ??= new DetailPreviewView();
            _detailView.Initialize(host);
            _diffDrawer ??= new PreviewDiffDrawer();
        }

        /// <summary>
        /// 保持しているフル段の出力を捨てる(拡大表示の切り出し元にしない)。処理したゾーンの集合が
        /// 変わったとき(ソロ表示の切り替え)に呼ぶ。次のフル段の確定までは拡大表示を作り直さない。
        /// </summary>
        public void InvalidateFullOutput() => _pipeline?.InvalidateFullOutput();

        /// <summary>次のプレビュー再生成では、段階的リファインのプロキシ段を使わない(<see cref="PreviewPipeline.SkipProxyOnce"/>)。</summary>
        public void SkipProxyOnce() => _pipeline?.SkipProxyOnce();

        /// <summary>
        /// ソース画素が変わったとき（テクスチャ差し替え・セッションリセット・エクスポートで
        /// 元ファイルを上書き）に、その画素から導かれた状態を漏れなく捨てる(<see cref="PreviewPipeline.InvalidateSourceCache"/>)。
        /// 表示の側は <see cref="OnSourceInvalidated"/> で捨てる。
        /// </summary>
        public void InvalidateSourceCache() => _pipeline?.InvalidateSourceCache();

        // 計算の側が原本から導いた状態を捨てた。表示も旧テクスチャのものを捨てる。
        private void OnSourceInvalidated()
        {
            _detailHiddenForDrag = false;
            // 旧テクスチャのクロップが新テクスチャ上に重なって見えるのを防ぐ
            // （詳細ジョブのキャンセルと表示テクスチャの解放を含む）。
            _detailView?.InvalidateDisplay();
            // 見た目の画像も旧テクスチャのもの。
            ReleaseAppearance();
        }

        /// <summary>
        /// 拡大表示の土台を「元の解像度」の設定(<see cref="DetailFullResolution"/>)に合わせる。Draw が毎回呼び、
        /// 合っていれば何もしない。切り替わったら、今の拡大表示を捨てて作り直させる。
        /// </summary>
        internal void SyncDetailResolution()
        {
            if (_pipeline == null || !_pipeline.SyncDetailResolution()) return;
            _detailView.InvalidateDisplay();
            _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
            _host?.RequestRepaint();
        }

        // 拡大表示(詳細クロップ)の座標の土台。取り込み後の寸法で映すときはその寸法の原本、そうでなければ作業解像度の原本。
        internal NativePixels DetailSource => _pipeline?.DetailSource;
        internal int DetailW => _pipeline?.DetailW ?? 0;
        internal int DetailH => _pipeline?.DetailH ?? 0;
        internal DetailPreviewView DetailView => _detailView;

        // 拡大表示の切り出し元(いまの原本で作ったフル段の出力。DetailSource と同じ寸法)。まだ無ければ null。
        internal NativePixels DetailOutput => _pipeline?.DetailOutput;

        // テスト・計測(WindowOutputParityTests・MemoryBench)が、OnGUI を回さずに作り直し、概要段・確定段の
        // 終わりを待って確定段の出力を読むための口。
        internal void GeneratePreviewAsync(bool dragOnly = false) => _pipeline?.GeneratePreviewAsync(dragOnly);
        internal bool IsGenerating => _pipeline != null && _pipeline.IsGenerating;
        internal NativePixels FullOutput => _pipeline?.FullOutput;

        /// <summary>いま持っている処理用の画素(作業解像度)の寸法(<see cref="PreviewPipeline.TryGetWorkingSize"/>)。</summary>
        internal bool TryGetWorkingSize(out int w, out int h)
        {
            if (_pipeline != null) return _pipeline.TryGetWorkingSize(out w, out h);
            w = h = 0;
            return false;
        }

        /// <summary>直近に確保した処理用の画素が、元ファイルをデコードしたものか(取り込み済みテクスチャではなく)。</summary>
        public bool TrueSourceFromFile => _pipeline != null && _pipeline.TrueSourceFromFile;

        /// <summary>このテクスチャから処理用の画素を取れるか(<see cref="PreviewPipeline.CanProvideSourcePixels"/>)。</summary>
        public bool CanProvideSourcePixels(Texture2D tex) => _pipeline != null && _pipeline.CanProvideSourcePixels(tex);

        /// <summary>
        /// 自動調整など他ビューが、プレビューと同一の true source 画素(作業解像度)で解析するための読み取り専用アクセサ
        /// (<see cref="PreviewPipeline.TryGetTrueSourcePixels"/>。参照は足さない)。
        /// </summary>
        public bool TryGetTrueSourcePixels(Texture2D tex, out NativePixels pixels, out int w, out int h)
        {
            if (_pipeline != null) return _pipeline.TryGetTrueSourcePixels(tex, out pixels, out w, out h);
            pixels = null;
            w = h = 0;
            return false;
        }

        public void Dispose()
        {
            Suspend();
            _pipeline?.Dispose();
            _detailView?.Dispose();
        }

        public void Suspend()
        {
            // ジョブを止め、プレビューの原本・確定段の出力・使い回しのバッファ・選択キャッシュ(Mono のヒープの外)を
            // 手放す。閉じる・リロードの前に呼ばれるので、ここで返せば OS へ戻る。開き直したら Initialize が作り直し、
            // Draw が読み直す。
            _pipeline?.Suspend();
            _detailView?.Suspend();
            TextureSlot.Release(ref previewTexture);
            TextureSlot.Release(ref rawPreviewTexture);
            // 差分のマテリアルも手放す(HideAndDontSave なので、リロードで参照を失うと残る)。次に描くときに作り直す。
            _diffDrawer?.Dispose();
            ReleaseAppearance();
            ClearSceneHighlight();
        }

        public void Draw()
        {
            // 見出し行はズーム率を右端に載せるため、ズーム率が確定した後で描く（DrawTitleRow 参照）。
            // プレビューが確立していない経路は、それぞれの return の直前で見出しだけを描く。
            // ここからズーム率確定までの間に GUILayout の呼び出しを置かないこと（見出しより上に積まれる）。
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null)
            {
                DrawTitleRow(false, 0f);
                EditorGUILayout.HelpBox(Localization.SetTexture, MessageType.Info);
                return;
            }

            // Play 中のリロード直後は、ウィンドウが操作されるまで原本を読まない(_deferSourceLoad)。
            // 決めるのは Layout のとき(ボタンの処理で途中から原本を読んでも、同じフレームの描くものを変えない)。
            if (Event.current.type == EventType.Layout) _deferredAtLayout = SourceLoadDeferred;
            if (_deferredAtLayout)
            {
                DrawTitleRow(false, 0f);
                EditorGUILayout.HelpBox(Localization.PreviewDeferredInPlayMode, MessageType.Info);
                return;
            }

            // エクスポートと同じフル解像度ソースを確保（プレビュー＝実結果の一致のため）。
            // EnsureTrueSource は原本が読めなければインポート済みテクスチャへ落ちるので、
            // ここで Read/Write を別途要求しない（PNG/JPG は Read/Write なしで表示できる）。
            if (!EnsureTrueSource(sourceTexture))
            {
                DrawTitleRow(false, 0f);
                return;
            }

            ApplyPendingResults();
            // 「元の解像度」を切り替えた直後なら、拡大表示の土台を合わせる(このフレームの拡大表示の寸法を決める前に)。
            SyncDetailResolution();
            // 作り直しの状態機械(ドラッグ中はプロキシで追従し、手を止めるか離したら確定する)。
            _pipeline.Tick(pointerHeld: GUIUtility.hotControl != 0);

            var maskView = _host._maskView;
            TickMaskOverlay(maskView);
            TickAppearance();

            string generatingLabel = CurrentGeneratingLabel();

            if (previewTexture == null)
            {
                // 初回生成中はまだ操作行が無いので、生成状態だけ単独の 1 行で表示する。
                DrawTitleRow(false, 0f);
                EditorGUILayout.LabelField(generatingLabel);
                return;
            }

            int srcW = TrueSourceW;
            int srcH = TrueSourceH;
            float scale = (srcW > IrocaConsts.Preview.MaxSize || srcH > IrocaConsts.Preview.MaxSize)
                ? IrocaConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH)
                : 1f;
            // 拡大表示の座標の土台(取り込み後の寸法で映すときはその寸法)と、その 1 画素あたりのプレビューテクスチャの画素数。
            int detW = DetailW, detH = DetailH;
            float detScale = detW > 0 ? scale * srcW / detW : scale;

            // プレビュー枠の幅は「カラムの見えている幅」に固定する。ExpandWidth(true) だと
            // GUILayout は外側 ScrollView の clientWidth をそのまま配るが、GUIScrollGroup は
            // 「子の最小幅がカラム幅を超えたら clientWidth をその最小幅まで広げ、子をその幅で
            // 並べる」仕様で、横バーの style を GUIStyle.none にしてもレイアウト上の横スクロールは
            // 生きている(none は"描かない・幅0"であって"許可しない"ではない)。
            // その結果、操作行(比較/差分・ズーム率・生成状態)の最小幅がカラム幅を超えるほど
            // 狭い横並びでは、生成状態ラベルが出入りするだけで枠幅が数十 px 動き、
            //   ・枠幅がカラム可視幅を超えた分、画像右端がカラムのクリップ外に出る
            //     (内側の横スクロールでも届かない＝右端が外側スクロールバーの位置で隠れる)
            //   ・横スクロールの可動域(内容幅 − 枠幅)が縮み、右端まで送った表示が左へ戻る
            // が同時に起きていた。カラム幅から外側縦バー分を引いた固定値にすれば、枠は常に
            // カラムの可視範囲へ収まり、chrome の都合で幅が動かない。縦バーは出入りするので
            // 常に引いておく(左カラムで縦バーを常時確保しているのと同じ「幅を動かさない」方針)。
            var vBarStyle = GUI.skin.verticalScrollbar;
            float vBarReserve = vBarStyle.fixedWidth + vBarStyle.margin.left;
            float frameW = availableColumnWidth > 1f
                ? Mathf.Max(IrocaConsts.Preview.MinViewportWidth, availableColumnWidth - vBarReserve)
                : 0f;

            // 全体表示(100%)の倍率は枠の大きさで決まり、ズームの上限はそれに依存する
            // (上限は画面上の表示倍率で決まっているので、全体表示が大きいほどズームの数字は小さく済む)。
            UpdateFitScale(PanelCount, frameW);
            float maxZoom = ComputeMaxZoom(scale) / _fitScale;

            // テクスチャ切り替えやデシリアライズで残った半端な/上限超過のズーム値を、
            // 毎フレーム最も近い「きれいな数字」のストップへ丸める（表示倍率の見映え対策）。
            // ズームのリセットボタンが上限を知る必要があるので、操作行より前で確定させる。
            previewZoom = SnapToStop(previewZoom, maxZoom);

            int zoomPercent = Mathf.RoundToInt(previewZoom * 100f);
            if (zoomPercent != _cachedZoomPercent || _cachedZoomLang != Localization.CurrentLanguage || _cachedZoomLabel == null)
            {
                _cachedZoomPercent = zoomPercent;
                _cachedZoomLang = Localization.CurrentLanguage;
                _cachedZoomLabel = string.Format(Localization.ZoomLabel, zoomPercent);
                _cachedZoomLabelShort = string.Format(Localization.ZoomLabelShort, zoomPercent);
            }

            DrawTitleRow(true, maxZoom);

            // 操作行: 比較/差分トグル・元を表示・生成状態を 1 行にまとめる。以前は
            // それぞれ 1 行ずつ計 3 行を使っており、既定ウィンドウ高(IrocaWindow.ShowWindow)
            // ではプレビュー枠の残り高が等倍 512px に届かず、100% でも縦スクロールバーが
            // 常に出ていた。トグルは内容幅に縮め、生成状態は右端に置く。
            // ズーム率は見出し行にある（この行に並べると既定幅に収まらない。DrawTitleRow 参照）。
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(comparisonMode, new GUIContent(Localization.ComparisonMode, Localization.ComparisonModeTooltip), EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false)) != comparisonMode)
            {
                comparisonMode = !comparisonMode;
                if (comparisonMode) diffMode = false;
                // 画像の並び(位置)が変わるので、拡大表示の切り出しを新しい並びで作り直す。
                _detailView.OnViewMoved();
            }
            if (GUILayout.Toggle(diffMode, new GUIContent(Localization.DiffMode, Localization.DiffModeTooltip), EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)) != diffMode)
            {
                diffMode = !diffMode;
                // 拡大表示の差分は、変更後の切り出しと同じ範囲の変更前の切り出しから描く。今の切り出しの分は
                // 描くときにその場で作る(ShownDetailRawForDiff)ので、作り直しは待たない。
                if (diffMode) comparisonMode = false;
            }
            // 押している間だけ変更前を表示する。前後比較(横並び)は高ズームで使えず、差分表示は
            // 「変わった画素」しか示さないため、拡大して細部を見ているときに「元はどうだったか」を
            // 確かめる手段が無かった。比較モード中は両方が既に並んでいるので無効化する。
            bool peekOriginal;
            using (new EditorGUI.DisabledScope(comparisonMode || rawPreviewTexture == null))
            {
                peekOriginal = GUILayout.RepeatButton(
                    new GUIContent(Localization.PeekOriginal, Localization.PeekOriginalTooltip),
                    EditorStyles.miniButton, GUILayout.ExpandWidth(false));
            }
            peekOriginal &= !comparisonMode && rawPreviewTexture != null;
            // RepeatButton は押されている間 true を返し続けるが、Editor は要求が無いと
            // 再描画しない。押下中は継続的に再描画を要求しないと 1 フレームで戻って見える。
            if (peekOriginal) _host.RequestRepaint();
            DrawAppearanceToggle();

            GUILayout.FlexibleSpace();
            // MinWidth(0): 生成状態ラベルは文字数が多く、既定では「文字幅＝最小幅」として
            // この行の最小幅に丸ごと乗る。狭いカラムではそれがカラム幅を超え、外側 ScrollView が
            // カラムより広い clientWidth を配ってしまう(GUIScrollGroup の仕様。frameW のコメント参照)。
            // 最小幅 0 にすると足りない分は文字が切れるだけで、他の要素の配置には影響しない。
            GUILayout.Label(generatingLabel, GUILayout.MinWidth(0f));
            EditorGUILayout.EndHorizontal();

            DrawInteractionModeRow();

            // 比較/差分トグルを押したイベントではパネル数が変わり得るので、全体表示の倍率をここで揃え直す。
            int panelCount = PanelCount;
            UpdateFitScale(panelCount, frameW);
            float effZoom = EffectiveZoom;
            SyncMagnifyFilters(scale, effZoom);

            // 詳細モード: ソースが縮小されて表示されている(scale<1)テクスチャを、プレビューテクスチャより
            // 大きく表示しているとき(拡大したとき・広いウィンドウの全体表示)、低解像度プレビューの
            // 引き伸ばしではなくソース解像度から作り直したクロップを出す。前後比較では同じ切り出しを
            // 変更前・変更後の両方に重ねる。
            bool detailActive = detScale < 1f &&
                                effZoom > DetailPreviewView.DetailMinZoom;

            float displayW = previewTexture.width  * effZoom;
            float displayH = previewTexture.height * effZoom;
            // 枠内に並べる画像の総幅(比較モードは 2 枚＋間隔)。
            float imagesW = displayW * panelCount + (panelCount - 1) * IrocaConsts.Preview.PanelSpacing;

            // 横スクロールバーが出たときに IMGUI がクライアント高から差し引く高さ。固定 16px
            // (ViewportMargin)では足りる保証がないので skin の実寸から導出する(+2 は丸めの保険)。
            // 不足すると画像が枠より横に広いとき、縦も収まるはずなのに縦バーが消えない。
            var hBarStyle = GUI.skin.horizontalScrollbar;
            float hBarReserve = hBarStyle.fixedHeight + hBarStyle.margin.vertical + 2f;
            var scrollPad = GUI.skin.scrollView.padding;
            // 枠の内容高。比較モードは Before/After の見出し行も含める(含めないと縦バーが残る)。
            float contentH = displayH + scrollPad.vertical + (panelCount == 2 ? ComparisonLabelHeight : 0f);

            // 詳細クロップの「見えている範囲」は上で確定させた枠幅(frameW)を使う。テクスチャ
            // 実寸基準だと、広いウィンドウで可視幅を過小評価して右側の高解像度クロップを
            // 取りこぼす。カラム幅が未設定のホストでは実測値(_viewportWidth)、それも未計測の
            // 初回フレームだけテクスチャ基準を暫定値にする(過大評価＝安全側)。
            float fallbackViewW = Mathf.Min(imagesW,
                previewTexture.width * panelCount + (panelCount - 1) * IrocaConsts.Preview.PanelSpacing)
                + IrocaConsts.Preview.ViewportMargin;
            _detailView.lastViewportW = frameW > 1f
                ? frameW
                : (_viewportWidth > 1f ? _viewportWidth : fallbackViewW);

            // プレビュー枠の高さ。
            float maxViewH;
            // 画像が枠からはみ出してスクロール(パン)が要るか。
            bool overflowsFrame;
            if (frameW > 1f && HasFrameHeightBudget)
            {
                // 枠はカラムの残り(FrameHeightBudget)を上限に、内容の高さまで縮める。全体表示(100%)では
                // 画像がちょうど収まるので枠＝画像で、スクロールバーは出ない。拡大中は残りいっぱいまで
                // 使い、縮小中は画像の高さまで縮む(枠の下に余白を作らない)。
                // 横バーの分の高さは、画像が枠より横に広いときだけ足す。縦にもはみ出すときは縦バーが
                // 幅を奪うので、その分を引いた幅で横のはみ出しを判定する。
                float budget = FrameHeightBudget;
                bool vOverflow = contentH + FitSlack > budget;
                float clientW = frameW - (vOverflow ? vBarReserve : 0f);
                bool hOverflow = imagesW + scrollPad.horizontal > clientW + 0.5f;
                maxViewH = Mathf.Min(contentH + (hOverflow ? hBarReserve : FitSlack), budget);
                overflowsFrame = hOverflow || contentH > maxViewH - (hOverflow ? hBarReserve : 0f) + 0.5f;
            }
            else
            {
                // 枠の大きさが分からない初回フレーム・カラム寸法を渡さないホスト: 等倍の自然サイズ。
                contentH = Mathf.Min(displayH, previewTexture.height) + scrollPad.vertical
                           + (panelCount == 2 ? ComparisonLabelHeight : 0f);
                maxViewH = contentH + Mathf.Max(IrocaConsts.Preview.ViewportMargin, hBarReserve);
                overflowsFrame = displayH > maxViewH - hBarReserve
                                 || imagesW > _detailView.lastViewportW - vBarReserve;
            }
            // 高さは GUILayout.Height で固定なので maxViewH が実値。
            _detailView.lastViewportH = maxViewH;

            // プレビュー枠より上に積まれた UI の実測高(外側 ScrollView の内容座標系なので
            // 外側のスクロール位置に依存しない)。動的高さ調整(次フレーム)に使う。
            // 同一フレーム内の Layout/Repaint は前フレームの値を共有するため整合する。
            // 値が変わったら追い再描画を 1 回要求する。エディタウィンドウは要求が無い限り
            // 再描画されないため、これが無いと chrome が変わる操作(マスク/プリセット節の
            // 開閉など previewDirty を立てないもの)の最終フレームが旧値のレイアウトのまま
            // 画面に固定され、不要なスクロールバー付きの枠が出たままになる。chrome は枠より
            // 上の UI のみで maxViewH に依存しないため、追い再描画は 1 回で収束しループしない。
            if (Event.current.type == EventType.Repaint)
            {
                float chrome = GUILayoutUtility.GetLastRect().yMax;
                if (chrome > 1f && Mathf.Abs(chrome - _chromeAboveViewportH) > 0.5f)
                {
                    _chromeAboveViewportH = chrome;
                    _host.RequestRepaint();
                }
            }

            // Scene でモデルをクリックした直後は、拡大中ならその場所が見えるところまでスクロールする。
            ApplyPendingSceneFocus(overflowsFrame, frameW > 1f ? frameW : _detailView.lastViewportW, maxViewH);

            Vector2 prevScroll = _previewScrollPos;
            _previewScrollPos = EditorGUILayout.BeginScrollView(
                _previewScrollPos,
                GUILayout.Height(maxViewH),
                frameW > 1f ? GUILayout.Width(frameW) : GUILayout.ExpandWidth(true));
            if (_previewScrollPos != prevScroll)
            {
                // 見えている範囲が動いたので拡大表示を作り直させる(できるまでは前の切り出しを描き続ける)。
                _detailView.OnViewMoved();
            }

            Rect activePreviewRect = default;
            // Ctrl+スクロールズームの判定領域。比較モードでは Before/After 両パネルを
            // またぐ矩形にし、どちらのパネル上でもズームできるようにする。
            Rect zoomHitRect = default;
            // 前後比較の変更前の画像の矩形。ツールは載らないので、どのモードでも左ドラッグでパンできる。
            Rect beforePanRect = default;

            // 画像を枠の左右中央に置く。全体表示は枠の縦横比と画像の縦横比が合わないと片方に余白が
            // 残り、左寄せだと広いウィンドウで右側だけが大きく空く。画像が枠より広いときは
            // 両側の FlexibleSpace が 0 になり、従来どおり左端からスクロールする。
            // 座標を使う側(スポイト・ペイント・目印・詳細クロップ)はどれも画像の矩形
            // (activePreviewRect)基準なので、置き場所が動いても食い違わない。
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (comparisonMode && rawPreviewTexture != null)
            {
                // 見出しの幅はラベルの左右マージンぶん詰める。画像と同じ幅にすると、マージンが
                // パネル幅へ上乗せされて 2 枚で枠をわずかに超え、全体表示でもスクロールバーが出る。
                float captionW = Mathf.Max(1f, displayW - EditorStyles.label.margin.horizontal);
                EditorGUILayout.BeginVertical(GUILayout.Width(displayW));
                EditorGUILayout.LabelField(Localization.Before, GUILayout.Width(captionW));
                var rawRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                EditorGUI.DrawPreviewTexture(rawRect, ShownRaw);
                EditorGUILayout.EndVertical();

                GUILayout.Space(IrocaConsts.Preview.PanelSpacing);

                EditorGUILayout.BeginVertical(GUILayout.Width(displayW));
                EditorGUILayout.LabelField(Localization.After, GUILayout.Width(captionW));
                activePreviewRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                EditorGUI.DrawPreviewTexture(activePreviewRect, ShownPreview);

                // 拡大中は、変更後の切り出しと同じ範囲の変更前の切り出し(ShownDetailRaw。原本の作業解像度から作る)を
                // 変更前の画像に重ねる。片方しか作れないときは両方とも全体表示のまま(解像度をそろえて比べる)。
                var shownDetail = ShownDetail;
                Texture beforeDetail = detailActive && shownDetail != null && !_detailHiddenForDrag
                    ? ShownDetailRaw() : null;
                Rect afterDetailRect = default;
                if (beforeDetail != null)
                {
                    GUI.DrawTexture(_detailView.ComputeDetailScreenRect(rawRect, detScale, effZoom, _previewScrollPos, detW, detH),
                        beforeDetail, ScaleMode.StretchToFill, false);
                    afterDetailRect = _detailView.ComputeDetailScreenRect(activePreviewRect, detScale, effZoom, _previewScrollPos, detW, detH);
                    GUI.DrawTexture(afterDetailRect, shownDetail, ScaleMode.StretchToFill, false);
                }
                var dtc = _detailView.detailPreviewTexture;
                if (beforeDetail != null &&
                    maskView.DetailOverlayMatches(detW, detH, _detailView.detailOriginX, _detailView.detailOriginY,
                                                  _detailView.detailCropW, _detailView.detailCropH, dtc.width, dtc.height))
                    DrawMaskOverlaysWithDetail(activePreviewRect, afterDetailRect, maskView);
                else
                    DrawMaskOverlays(activePreviewRect, maskView);
                EditorGUILayout.EndVertical();

                if (Event.current.type == EventType.Repaint) _detailView.lastBeforeRect = rawRect;
                beforePanRect = rawRect;

                zoomHitRect = Rect.MinMaxRect(
                    Mathf.Min(rawRect.x, activePreviewRect.x),
                    Mathf.Min(rawRect.y, activePreviewRect.y),
                    Mathf.Max(rawRect.xMax, activePreviewRect.xMax),
                    Mathf.Max(rawRect.yMax, activePreviewRect.yMax));
            }
            else
            {
                activePreviewRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                zoomHitRect = activePreviewRect;

                // 「元を表示」で押下中は、変更後の切り出しと同じ範囲・寸法の変更前の切り出しを、原本(拡大表示の寸法。
                // 取り込み後の寸法へ縮めたもの。DetailSource)から作って重ねる(ShownDetailRaw)。変更前の
                // 切り出しは押している間だけ持ち、離したら手放す(拡大中の VRAM が増えるのは押している間だけ)。
                // 作れないとき(切り出しがまだ無い・見た目がまだ作れない)は、全体の変更前だけを出す。
                // 「アバターの色」の表示中は、切り出しの見た目ができるまで切り出しを重ねない(ShownDetail)。
                var shownDetail = ShownDetail;
                bool detailShown = detailActive && shownDetail != null && !_detailHiddenForDrag;
                Texture peekDetail = detailShown && peekOriginal ? ShownDetailRaw() : null;
                // 差分表示の間は、変更前の切り出しを差分が読むので持っておく。
                if (!peekOriginal && !diffMode) ReleaseDetailRaw();
                if (detailShown && (!peekOriginal || peekDetail != null))
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect, peekOriginal ? ShownRaw : ShownPreview);

                    Rect detailScreenRect = _detailView.ComputeDetailScreenRect(activePreviewRect, detScale, effZoom, _previewScrollPos, detW, detH);
                    GUI.DrawTexture(detailScreenRect, peekOriginal ? peekDetail : shownDetail,
                        ScaleMode.StretchToFill, false);

                    // 変更前を見せている間は差分ハイライトを重ねない(下の全体表示と同じ理由)。
                    if (!peekOriginal && diffMode)
                    {
                        // 切り出しの上は、変更後の切り出しと同じ範囲の変更前の切り出しから差分を描く(描いている 2 枚から
                        // 作るので、切り出しと差分が食い違わない)。切り出しの外側(新しく見えた端)は、下に描いている
                        // 縮小表示の 2 枚から同じ定義で描く。変更前の切り出しが作れないときは全体を縮小表示の 2 枚から描く。
                        // 切り出しの内側には縮小表示の分を重ねない(二重に塗って色が濃くなる)。
                        var detailBefore = ShownDetailRawForDiff();
                        _diffDrawer.DrawOutside(activePreviewRect, detailBefore != null ? detailScreenRect : default,
                                                rawPreviewTexture, previewTexture);
                        if (detailBefore != null)
                            _diffDrawer.Draw(detailScreenRect, detailBefore, _detailView.detailPreviewTexture, new Rect(0f, 0f, 1f, 1f));
                    }
                    else
                    {
                        // マスクの重ね表示: 詳細の範囲は、詳細と同じ範囲・寸法で描いた等倍のもの
                        // (MaskPaintView.RebuildDetailOverlay)を重ね、その外は全体用を引き伸ばす。等倍のものが
                        // まだ無い・範囲が古いときは全体用だけ(以前と同じ)。ペイント中の直接書き込み(PaintMask)は
                        // 両方へ入るので、塗ったそばから見える。
                        var dt = _detailView.detailPreviewTexture;
                        if (maskView.DetailOverlayMatches(detW, detH, _detailView.detailOriginX, _detailView.detailOriginY,
                                                          _detailView.detailCropW, _detailView.detailCropH, dt.width, dt.height))
                            DrawMaskOverlaysWithDetail(activePreviewRect, detailScreenRect, maskView);
                        else
                            DrawMaskOverlays(activePreviewRect, maskView);
                    }
                }
                else
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect,
                        peekOriginal ? ShownRaw : ShownPreview);

                    // 変更前を見せている間は差分ハイライトを重ねない（変更前に「変わった場所」を
                    // 塗ると、元テクスチャに無い色が乗って見え、確認したい当のものが隠れる）。
                    if (!peekOriginal && diffMode)
                        _diffDrawer.DrawOutside(activePreviewRect, default, rawPreviewTexture, previewTexture);
                    else
                    {
                        DrawMaskOverlays(activePreviewRect, maskView);
                    }
                }

                // 詳細プレビュー生成中の表示は、枠内のラベルを廃止し
                // 枠外の単一行に統合する（c107e85）。
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // 連続領域モードのシード(任意上書き)を十字オーバーレイで描画。
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
            {
                // メッシュの UV の島の輪郭(表示を ON にしているときだけ)。目印より下に敷く。
                DrawMeshIslandOverlay(activePreviewRect);
                // Scene でモデルをクリックした場所(その UV の島とクリック位置)
                DrawSceneHighlightOverlay(activePreviewRect);
                DrawFloodFillSeedOverlay(activePreviewRect);
                // スポイトで色を取った位置(自動調整が AI 提案をかける位置)の目印
                DrawSampleUvOverlay(activePreviewRect);
                // AI 提案の反映待ちクリック位置(受理済み・未反映)の目印
                DrawAiSuggestPendingOverlay(activePreviewRect);
            }

            // 画像の矩形を控える(拡大表示の切り出しの範囲と、描く位置の計算に使う)。
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                _detailView.lastPreviewRect = activePreviewRect;

            // 拡大表示の切り出しを作り直す。範囲はこのフレームの配置(画像の矩形・スクロール位置・倍率)で決める
            // (以前は描く前に前のフレームの矩形で決めていたので、ズームした直後は 1 コマ古い範囲を切り出していた)。
            if (detailActive)
                TickDetailGeneration(detW, detH, detScale, effZoom, panelCount, srcW, srcH);

            HandlePreviewGlobalInput(zoomHitRect, maxZoom);

            // スポイトとマスクペイントは排他。ペイントに入ったらスポイトを解除する。
            if (maskView.maskPaintActive && !string.IsNullOrEmpty(_host.EyedropperZoneId))
                _host.EyedropperZoneId = null;

            bool eyedropperArmed = !string.IsNullOrEmpty(_host.EyedropperZoneId) && !maskView.maskPaintActive;
            // シード指定の武装（ゾーンカードの「指定」ボタン）。スポイトと同じ one-shot で、
            // 素のクリックを横取りする。両方が武装することはない（互いに解除し合う）が、
            // 念のためスポイトを優先する。
            bool seedPickArmed = !string.IsNullOrEmpty(_host.SeedPickZoneId)
                                 && !maskView.maskPaintActive && !eyedropperArmed;

            // スポイト武装中はプレビュークリックを横取りして実画素からサンプル取得に充てる
            // （シード設定・パンより優先。取得すると one-shot で自動解除）。
            if (eyedropperArmed)
                HandleEyedropperInput(activePreviewRect, srcW, srcH);

            // 連続領域モードのシード入力。ゾーンカードの「指定」で武装しているときは素のクリックを、
            // それ以外は従来どおり Shift+クリックを受ける。マスクペイント中・スポイト中は無効。
            if (!maskView.maskPaintActive && !eyedropperArmed)
                HandleFloodFillSeedInput(activePreviewRect, seedPickArmed);

            // ブラシ操作 UI は MaskBrushWindow パレットに分離されたため、メインの
            // マスク foldout の開閉とペイント可否は連動させない（閉じても塗れる）。
            if (maskView.maskPaintActive)
            {
                HandlePreviewPaintInput(activePreviewRect);
            }

            // 右クリックメニュー(AI 提案・メッシュのパーツ操作)。どのモードでも左ボタンしか使わないので、
            // ブラシ中でも開ける。スポイト・シード指定は one-shot の左クリック待ちなので、その間は出さない
            // (待ちの最中に別の操作が割り込むと、どちらが効いたのか分からなくなる)。
            if (!eyedropperArmed && !seedPickArmed)
                HandlePreviewContextMenu(activePreviewRect);

            // パンはズーム>1 に限らず「画像がビューポートに収まっていない」とき常に許可する
            // (枠の下限高で止まる低いウィンドウでは、全体表示でも縦がはみ出すことがある。
            // ズーム率だけで判定するとスクロールバー以外に位置を動かす手段がなくなる)。
            // はみ出しの判定(overflowsFrame)は枠の高さを決めたところで、縦バーが幅を奪う分も
            // 含めて済ませてある。
            //
            // 呼ぶのはツール群より後: 同じ枠にカーソル矩形を出すものがあっても、後勝ちの
            // AddCursorRect でパンのカーソルが見える。
            //
            // 中ボタンドラッグと Alt+左ドラッグはどのツールとも衝突しないので、**どのモードでも**
            // パンできる。以前は塗る/消す中に左ドラッグがブラシへ取られ、拡大して塗っていると
            // スクロールバー以外に表示を動かす手段が無かった（2026-09-11 の UX 見直し）。
            // 素の左ドラッグは、ブラシ・スポイト・シード指定が使っていないときだけパンに充てる。
            if (previewZoom > 1f || overflowsFrame)
            {
                bool leftDragPans = !maskView.maskPaintActive && !eyedropperArmed && !seedPickArmed;
                HandlePreviewPanInput(activePreviewRect, leftDragPans, beforePanRect);
            }

            EditorGUILayout.EndScrollView();

            // スクロールビューの実幅を測り、次フレームの詳細クロップ可視範囲に使う。
            // Repaint 時のみ有効値が返るため、そのときだけ更新する。値が変わったら追い再描画を
            // 1 回要求し、旧幅ベースの表示が画面に固定されないようにする(chrome 実測と同方針。
            // 幅はレイアウト高に影響しないため追い再描画がループすることはない)。
            if (Event.current.type == EventType.Repaint)
            {
                float vw = GUILayoutUtility.GetLastRect().width;
                if (vw > 1f && Mathf.Abs(vw - _viewportWidth) > 0.5f)
                {
                    _viewportWidth = vw;
                    _host.RequestRepaint();
                }
            }

            EditorGUILayout.Space(4);

            // 今の視点を Undo 非対象の場所へ控える（Undo/Redo 後にここから復元する）。
            if (_host != null)
            {
                s_viewStateOwner = _host.GetInstanceID();
                s_viewStateZoom = previewZoom;
                s_viewStateScroll = _previewScrollPos;
            }
        }

        /// <summary>
        /// バックグラウンドのジョブ(プレビュー・差分・詳細)が仕上げた結果をテクスチャへ反映する。
        /// <see cref="Draw"/> が毎フレーム、再生成の判断より前に呼ぶ。GUILayout は呼ばない。
        /// </summary>
        private void ApplyPendingResults()
        {
            // バックグラウンドプレビュータスクからの結果を適用（Texture2D API: メインスレッドのみ）
            if (_pipeline.TakePendingFrame(out var frame))
            {
                ApplyPendingPreview(frame);
                _previewVersion++;
            }

            // バックグラウンド詳細プレビュータスクからの結果を適用
            if (_detailView.HasPendingResult)
            {
                _detailView.ApplyPendingResult();
                _detailVersion++;
                // 拡大表示用のマスクの重ね表示も、新しい範囲で作り直す(MaskPaintView.RebuildDetailOverlay)。
                RebuildDetailMaskOverlay();
                // 隠している間は新しい切り出ししか作らない(Draw の詳細生成の条件)ので、届いたら見せてよい。
                _detailHiddenForDrag = false;
            }
        }

        /// <summary>
        /// Mono のヒープの外の画素をテクスチャへ上げる(寸法が変わったら作り直す)。RGBA32 でミップなしなので、
        /// Color32 の並びがそのままテクスチャのデータになる(管理配列へ写さない。SetPixels32 と同じ中身になる)。
        /// </summary>
        internal static unsafe void UploadPixels(ref Texture2D tex, NativePixels pixels)
        {
            TextureSlot.Resize(ref tex, pixels.Width, pixels.Height);
            tex.LoadRawTextureData((System.IntPtr)pixels.Ptr, pixels.Length * sizeof(Color32));
            tex.Apply();
        }

        /// <summary>
        /// 計算の側から受け取った結果(表示寸法の変更後・変更前)をテクスチャへ上げ、マスクの重ね表示と拡大表示を
        /// 追従させる。
        /// </summary>
        private void ApplyPendingPreview(PreviewFrame frame)
        {
            int w = frame.Width;
            int h = frame.Height;
            var stage = frame.Stage;
            if (stage != null) stage.UploadStart = PreviewLatencyCycle.Now;

            UploadPixels(ref previewTexture, frame.Processed);
            UploadPixels(ref rawPreviewTexture, frame.Raw);
            frame.Release();

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
            if (frame.IsDrag)
            {
                // ドラッグの追従: 古い拡大表示を隠してこのプロキシを見せる。走っている拡大表示のジョブは
                // 古い切り出し元のものなので取り消す(確定の後で作り直す)。
                _detailHiddenForDrag = true;
                _hiddenAtFullVersion = _pipeline.FullOutputVersion;
                _detailView.detailJob.Cancel();
            }
            if (frame.IsFinal)
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
                _pipeline.NoteShown();
                var latency = frame.Latency;
                if (latency != null)
                {
                    if (ReferenceEquals(stage, latency.Full)) _detailView.AttachLatency(latency);
                    if (PreviewLatency.Publish(latency)) _host.RequestRepaint();
                }
            }
        }

        /// <summary>
        /// マスクオーバーレイの反映と、再構築のスケジュール(ペイント中は間引く)。
        /// <see cref="Draw"/> が毎フレーム、再生成の判断の後に呼ぶ。GUILayout は呼ばない。
        /// </summary>
        private void TickMaskOverlay(MaskPaintView maskView)
        {
            // バックグラウンドで完了したオーバーレイ Color32[] を先に Texture2D へ適用する。
            maskView.ApplyPendingOverlay();
            maskView.ApplyPendingDetailOverlay();

            // マスクオーバーレイ再構築をスケジュール（バックグラウンド計算）。
            // ペイント中は MouseDrag が毎フレーム maskDirty を立てるため、
            // 毎回フル解像度 bool[] を clone してジョブを Cancel→再 Schedule すると
            // GC 圧と CPU 浪費だけが積み上がってジョブが完了しない。
            // ペイント中だけは PaintOverlayThrottleSeconds 間隔に絞り、
            // 進行中のジョブが Apply まで届くようにする。
            // オーバーレイの目標寸法。表示倍率が上がると Point 補間でも粗く見えないよう
            // プレビュー寸法の整数倍へ引き上げる(MaskPaintView.OverlayScale が正)。倍率変更でも
            // 目標が変わるため、maskDirty と同じ経路で再構築する。実寸ではなく最後に構築した
            // 寸法(overlayBuilt*)と比べるのは、マスクが空でテクスチャが無いときに毎フレーム
            // 再構築を撃たないため。倍率は画面上の表示倍率(全体表示の倍率込み。値は前フレームで
            // 確定したもの)。
            if (previewTexture != null)
            {
                int ovScale = maskView.OverlayScale(previewTexture.width, previewTexture.height, EffectiveZoom);
                int ovW = previewTexture.width * ovScale;
                int ovH = previewTexture.height * ovScale;
                bool sizeStale = maskView.overlayBuiltW != ovW || maskView.overlayBuiltH != ovH;
                if (maskView.maskDirty || sizeStale)
                {
                    bool throttle = maskView.isPainting &&
                        (EditorApplication.timeSinceStartup - maskView.lastOverlayRebuildTime)
                            < PaintOverlayThrottleSeconds;
                    if (!throttle)
                    {
                        maskView.lastOverlayRebuildTime = EditorApplication.timeSinceStartup;
                        maskView.RebuildMaskOverlay(ovW, ovH);
                        // マスクが変わったら拡大表示用も作り直す(寸法の変化だけなら範囲は同じなので要らない)。
                        if (maskView.maskDirty) RebuildDetailMaskOverlay();
                        maskView.maskDirty = false;
                    }
                    // throttle 時は maskDirty を残し、次フレームで再評価する。
                    // ペイント中は MouseDrag が継続的に Repaint を呼ぶので追加の RequestRepaint は不要。
                }
                maskView.SyncOverlayFilter(EffectiveZoom);
            }
        }

        /// <summary>
        /// 操作行の右端(初回生成中は単独の 1 行)に出す生成状態の文言。GUILayout は呼ばない。
        /// </summary>
        private string CurrentGeneratingLabel()
        {
            // 「生成中…」インジケータの文言。プレビュー確立後は下の操作行（比較/差分・
            // 元を表示と同じ行）の右端に出す。非生成時も空白 " " を同じ場所に描き、
            // 出入りで UI が上下にジャンプしないよう行高を固定する。
            // 詳細プレビュー生成も同じ表示に統一する（c107e85）。
            string generatingLabel;
            if (IsGenerating)
                generatingLabel = Localization.GeneratingPreview;
            else if (_detailView.detailJob.IsRunning)
                generatingLabel = Localization.GeneratingDetailPreview;
            else
                generatingLabel = " ";
            return generatingLabel;
        }

        /// <summary>
        /// マスクのオーバーレイを、共通 → ゾーンの順にプレビュー画像の矩形へ重ねる
        /// (通常表示・比較モードの変更後・拡大中の詳細クロップの上で共通)。
        /// </summary>
        private static void DrawMaskOverlays(Rect r, MaskPaintView mv)
        {
            if (mv.maskOverlayTexture != null)
                GUI.DrawTexture(r, mv.maskOverlayTexture, ScaleMode.StretchToFill, true);
            if (mv.zoneMaskOverlayTexture != null)
                GUI.DrawTexture(r, mv.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
        }

        /// <summary>
        /// 拡大表示中のマスクの重ね表示: detail(詳細プレビューの画面上の矩形)には等倍のもの、その外には全体用を
        /// 描く(全体用を detail の上にも重ねると、半透明が二重になって濃く見える)。
        /// </summary>
        private static void DrawMaskOverlaysWithDetail(Rect full, Rect detail, MaskPaintView mv)
        {
            DrawOutside(full, detail, mv.maskOverlayTexture);
            DrawOutside(full, detail, mv.zoneMaskOverlayTexture);
            if (mv.detailMaskOverlayTexture != null)
                GUI.DrawTexture(detail, mv.detailMaskOverlayTexture, ScaleMode.StretchToFill, true);
            if (mv.detailZoneOverlayTexture != null)
                GUI.DrawTexture(detail, mv.detailZoneOverlayTexture, ScaleMode.StretchToFill, true);
        }

        /// <summary>tex を full へ引き伸ばして描くときの、hole の外の部分だけを描く(上下左右の帯に分けて、
        /// それぞれ対応するテクスチャ座標で)。</summary>
        private static void DrawOutside(Rect full, Rect hole, Texture tex)
        {
            if (tex == null || full.width <= 0f || full.height <= 0f) return;
            var h = Rect.MinMaxRect(Mathf.Max(full.xMin, hole.xMin), Mathf.Max(full.yMin, hole.yMin),
                                    Mathf.Min(full.xMax, hole.xMax), Mathf.Min(full.yMax, hole.yMax));
            if (h.width <= 0f || h.height <= 0f)
            {
                GUI.DrawTexture(full, tex, ScaleMode.StretchToFill, true);
                return;
            }
            Strip(full, Rect.MinMaxRect(full.xMin, full.yMin, full.xMax, h.yMin), tex);      // 上
            Strip(full, Rect.MinMaxRect(full.xMin, h.yMax, full.xMax, full.yMax), tex);      // 下
            Strip(full, Rect.MinMaxRect(full.xMin, h.yMin, h.xMin, h.yMax), tex);            // 左
            Strip(full, Rect.MinMaxRect(h.xMax, h.yMin, full.xMax, h.yMax), tex);            // 右
        }

        private static void Strip(Rect full, Rect part, Texture tex)
        {
            if (part.width <= 0f || part.height <= 0f) return;
            // テクスチャ座標は下原点(画面の y は下向き)。
            float u0 = (part.xMin - full.xMin) / full.width, u1 = (part.xMax - full.xMin) / full.width;
            float v1 = 1f - (part.yMin - full.yMin) / full.height, v0 = 1f - (part.yMax - full.yMin) / full.height;
            GUI.DrawTextureWithTexCoords(part, tex, new Rect(u0, v0, u1 - u0, v1 - v0), true);
        }

        /// <summary>
        /// 拡大表示の切り出しの作り直しを始める(作り直しが要り、走っているものが無いとき)。範囲はこのフレームの
        /// 画像の矩形(lastPreviewRect・lastBeforeRect)とスクロール位置から決めるので、Repaint の、画像の矩形が
        /// 決まった後で呼ぶ。作り直し待ち・走っている間は再描画を頼み続ける(終わったらすぐ最新の範囲で作り直す)。
        /// </summary>
        private void TickDetailGeneration(int detW, int detH, float detScale, float effZoom, int panelCount, int srcW, int srcH)
        {
            var dv = _detailView;
            if (Event.current.type == EventType.Repaint &&
                !dv.detailJob.IsRunning &&
                (!_detailHiddenForDrag || FullOutputFreshSinceHide) &&
                dv.lastDetailDirtyTime > 0 &&
                dv.lastPreviewRect.width > 0)
            {
                var latency = dv.TakeLatencyFor(dv.lastDetailDirtyTime, srcW, srcH);
                dv.lastDetailDirtyTime = 0;
                var beforeRect = panelCount == 2 ? dv.lastBeforeRect : default;
                dv.GenerateDetailPreviewAsync(detW, detH, DetailSource, DetailOutput, detScale, effZoom, _previewScrollPos,
                    dv.lastViewportW, dv.lastViewportH, dv.lastPreviewRect, beforeRect, latency);
            }
            else if (dv.lastDetailDirtyTime > 0 || dv.detailJob.IsRunning)
            {
                _host.RequestRepaint();
            }
        }

        /// <summary>
        /// 拡大表示用のマスクの重ね表示を、詳細プレビューのいまの範囲で作り直す(詳細が無ければ何もしない)。
        /// </summary>
        internal void RebuildDetailMaskOverlay()
        {
            var mv = _host._maskView;
            var t = _detailView.detailPreviewTexture;
            if (mv == null || t == null || DetailW <= 0 || DetailH <= 0) return;
            if (_detailView.detailCropW <= 0 || _detailView.detailCropH <= 0) return;
            mv.RebuildDetailOverlay(DetailW, DetailH, _detailView.detailOriginX, _detailView.detailOriginY,
                                    _detailView.detailCropW, _detailView.detailCropH, t.width, t.height);
        }

        /// <summary>
        /// プレビュー上のクリックがいま何をするか（スポイト／シード指定／マスクのブラシ／
        /// AI 提案）と、ソロ表示中かどうかを 1 行で示す。
        ///
        /// これらのモードは「クリックの意味」を丸ごと変えるのに、状態はゾーンカードや
        /// マスク編集ウィンドウのボタンの色にしか出ていなかった。設定列をスクロールしていたり、
        /// プレビューを別ウィンドウへ切り出していると手掛かりが画面の外にあり、
        /// 「押したのに何も起きない」「意図せず塗ってしまった」の原因になっていた。
        /// </summary>
        private void DrawInteractionModeRow()
        {
            var maskView = _host._maskView;

            // ★実際にクリックを取る側の優先順位をそのまま写す★（PreviewView.Draw 末尾の
            // 横取り順）。ここだけ別の順に書くと、表示と実際に効くモードが食い違い、
            // 「表示どおりに操作したのに違うことが起きる」という最悪の食い違いになる。
            //   ブラシ中は シード指定 が止まる（maskPaintActive のガード。右クリックメニューは開ける）
            //   スポイト中は シード指定・右クリックメニュー が止まる
            //   シード指定中は 右クリックメニュー が止まる
            //   AI 提案はモードではなく、推論待ちの間だけ状態として出す
            bool paintActive = maskView != null && maskView.maskPaintActive;
            bool eyedropper = !paintActive && !string.IsNullOrEmpty(_host.EyedropperZoneId);
            bool seed = !paintActive && !eyedropper && !string.IsNullOrEmpty(_host.SeedPickZoneId);
            // AI 提案はモードを持たないので、推論待ちがある間だけ状態として出す。
            var aiCtl = maskView != null ? maskView.SuggestControllerIfCreated : null;
            bool ai = !paintActive && !eyedropper && !seed
                      && aiCtl != null && aiCtl.PendingClicks.Count > 0;

            string mode = null;
            if (eyedropper)
                mode = Localization.PreviewModeEyedropper;
            else if (paintActive)
                mode = string.Format(
                    maskView.brushEraseMode ? Localization.PreviewModeEraseFormat
                                            : Localization.PreviewModePaintFormat,
                    maskView.ActiveTargetName(), maskView.ActiveLayerName());
            else if (seed)
                mode = Localization.PreviewModeSeed;
            else if (ai)
                mode = Localization.PreviewModeAi;

            var soloZone = _host.SoloZone;

            EditorGUILayout.BeginHorizontal();
            if (mode != null)
            {
                GUILayout.Label(new GUIContent(mode + Localization.PreviewModeEscHint, Localization.PreviewModeTooltip),
                                EditorStyles.miniLabel, GUILayout.MinWidth(0f));
            }
            else
            {
                // モードが無いときは、Scene でモデルをクリックした結果の案内か、右クリック・Scene のクリックの
                // 使い方を出す(何も無ければ空白 1 文字)。どれも miniLabel の高さに揃える。行を出入りさせたり高さを
                // 変えたりするとプレビュー枠が上下に跳ね、chrome 実測（_chromeAboveViewportH）も揺れる。
                DrawIdleRow();
            }
            if (soloZone != null)
            {
                GUILayout.FlexibleSpace();
                var prevContent = GUI.contentColor;
                GUI.contentColor = IrocaColors.ActiveMaskTarget;
                string zoneName = string.IsNullOrEmpty(soloZone.name)
                    ? Localization.UnnamedZone : soloZone.name;
                GUILayout.Label(
                    new GUIContent(string.Format(Localization.PreviewSoloFormat, zoneName),
                                   Localization.PreviewSoloTooltip),
                    EditorStyles.miniLabel, GUILayout.MinWidth(0f));
                GUI.contentColor = prevContent;
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 「③ プレビュー」見出し行。プレビューが確立していれば、ズーム率とリセットを右端に置く。
        ///
        /// ズーム率はもともと比較/差分と同じ操作行にあったが、「元を表示」「リセット」を足した
        /// 結果その行が既定ウィンドウ幅（右カラム ~408px）に収まらなくなり、ズーム率を miniLabel に
        /// 落として詰めていた。それでも右端の生成状態ラベルは幅 0 近くまで潰れて読めず、
        /// miniLabel はボタンの文字より小さく上寄りに描かれて行の中で浮いて見えた。
        /// 見出し行は題名以外が空いているので、ズーム率は元の文字サイズのままこちらへ置く。
        /// </summary>
        private void DrawTitleRow(bool showZoom, float maxZoom)
        {
            EditorGUILayout.BeginHorizontal();
            var title = new GUIContent(Localization.StepPrefixPreview + Localization.Preview);
            GUILayout.Label(title, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            if (showZoom)
            {
                GUILayout.FlexibleSpace();
                // ズームは Ctrl+スクロールのみ。拡大したあと初期表示へ戻す手段がスクロールを
                // 戻し切ることしか無かったので、リセットボタンだけ置く（−／＋／全体の段階ボタンは
                // 見た目が煩雑になったため 2026-09-14 に撤去）。
                var reset = new GUIContent(Localization.ZoomReset, Localization.ZoomResetTooltip);
                var zoom = new GUIContent(_cachedZoomLabel, Localization.ZoomHint);
                // 狭いカラムでは「(Ctrl+スクロール)」を省く。省かないと行がカラムより広くなり、
                // 右端のリセットボタンが見えない位置へ押し出される(外側 ScrollView はカラムより広い
                // 内容を黙って切る。frameW のコメント参照)。判定はカラム幅だけで決まるので、
                // 同一フレームの Layout と Repaint で同じ文言になる。
                if (!TitleRowFits(title, zoom, reset))
                    zoom.text = _cachedZoomLabelShort;
                GUILayout.Label(zoom, GUILayout.ExpandWidth(false));
                bool smooth = GUILayout.Toggle(SmoothMagnify, SmoothContent, EditorStyles.miniButton, GUILayout.ExpandWidth(false));
                if (smooth != SmoothMagnify)
                {
                    SmoothMagnify = smooth;
                    _host.RequestRepaint();
                }
                bool fullRes = GUILayout.Toggle(DetailFullResolution, DetailFullResolutionContent, EditorStyles.miniButton, GUILayout.ExpandWidth(false));
                if (fullRes != DetailFullResolution)
                {
                    // 土台の作り直しは次の Draw の始め(SyncDetailResolution)。このイベントの残りは今の土台のまま描く。
                    DetailFullResolution = fullRes;
                    _host.RequestRepaint();
                }
                if (GUILayout.Button(reset, EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    ResetZoom(maxZoom);
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── 拡大したときの補間(表示だけの設定) ─────────────────────────
        // オフ(既定): 等倍以上に拡大したら 1 画素ずつ四角く見せる(画素単位で確かめる)。
        // オン: 画素の間をなめらかにつなぐ(ゲームの中でテクスチャを近くから見たときの見え方に近い)。
        // 縮小表示は面平均で作った画像を描くので、どちらでも変わらない。マスクの重ね表示は
        // 「入っている / いない」を見せるものなので、ここでは変えない(MaskPaintView.SyncOverlayFilter)。
        private const string SmoothMagnifyPrefKey = IrocaPrefs.PreviewSmoothMagnify;
        private static bool? s_smoothMagnify;

        /// <summary>拡大したときに画素の間をなめらかにつなぐか。この PC のいろか全体の表示の設定。</summary>
        internal static bool SmoothMagnify
        {
            get => s_smoothMagnify ??= EditorPrefs.GetBool(SmoothMagnifyPrefKey, false);
            set
            {
                s_smoothMagnify = value;
                EditorPrefs.SetBool(SmoothMagnifyPrefKey, value);
            }
        }

        private static GUIContent SmoothContent => new GUIContent(Localization.SmoothMagnify, Localization.SmoothMagnifyTooltip);

        // ── 拡大表示の解像度(表示だけの設定。確定段が縮めたものを作るかを決めるので、PreviewPipeline が持つ) ──
        internal const string DetailFullResolutionPrefKey = PreviewPipeline.DetailFullResolutionPrefKey;

        /// <summary>拡大表示を作業解像度のまま映すか(<see cref="PreviewPipeline.DetailFullResolution"/>)。</summary>
        internal static bool DetailFullResolution
        {
            get => PreviewPipeline.DetailFullResolution;
            set => PreviewPipeline.DetailFullResolution = value;
        }

        private static GUIContent DetailFullResolutionContent
            => new GUIContent(Localization.DetailFullResolution, Localization.DetailFullResolutionTooltip);

        /// <summary>
        /// 表示用テクスチャの補間を今の設定と倍率に合わせる(描く前に毎フレーム。値が同じなら何もしない)。
        /// 全体表示の画像は、元の画像をそのまま載せている(scale>=1)ときだけ、拡大中に「ドット」なら Point にする
        /// (縮小した画像の画素を四角く見せても、元の画素ではないので意味が無い)。
        /// </summary>
        private void SyncMagnifyFilters(float scale, float effZoom)
        {
            var whole = scale >= 1f && effZoom > 1f && !SmoothMagnify ? FilterMode.Point : FilterMode.Bilinear;
            SetFilter(previewTexture, whole);
            SetFilter(rawPreviewTexture, whole);
            SetFilter(_appearancePreview, whole);
            SetFilter(_appearanceRaw, whole);
            var dt = _detailView.detailPreviewTexture;
            if (dt == null || _detailView.detailCropW <= 0) return;
            var detail = DetailPreviewView.DetailFilterMode(dt.width, _detailView.detailCropW);
            SetFilter(dt, detail);
            SetFilter(_detailView.detailRawTexture, detail);
            SetFilter(_appearanceDetail, detail);
            SetFilter(_appearanceDetailRaw, detail);
        }

        private static void SetFilter(Texture tex, FilterMode mode)
        {
            if (tex != null && tex.filterMode != mode) tex.filterMode = mode;
        }

        // 見出し行(題名・ズーム率・なめらか・元の解像度・リセット)がカラムの見えている幅に収まるか。
        // カラム幅を渡さないホストでは従来どおり省略しない。
        private bool TitleRowFits(GUIContent title, GUIContent zoom, GUIContent reset)
        {
            if (availableColumnWidth <= 1f) return true;
            var vBar = GUI.skin.verticalScrollbar;
            float avail = availableColumnWidth - vBar.fixedWidth - vBar.margin.left;
            var bold = EditorStyles.boldLabel;
            var label = GUI.skin.label;
            var button = EditorStyles.miniButton;
            float need = bold.CalcSize(title).x + bold.margin.horizontal
                         + label.CalcSize(zoom).x + label.margin.horizontal
                         + button.CalcSize(reset).x + button.margin.horizontal
                         + button.CalcSize(SmoothContent).x + button.margin.horizontal
                         + button.CalcSize(DetailFullResolutionContent).x + button.margin.horizontal;
            return need <= avail;
        }

        /// <summary>ズームを初期表示（100%）へ戻し、表示位置も先頭へ戻す。</summary>
        private void ResetZoom(float maxZoom)
        {
            previewZoom = Mathf.Clamp(1f, MinPreviewZoom, maxZoom);
            _previewScrollPos = Vector2.zero;
            // 見えている範囲が変わるので拡大表示を作り直させる（Ctrl+スクロール経路と同じ）。
            _detailView.OnViewMoved();
            _host.RequestRepaint();
        }

        /// <summary>
        /// Undo/Redo 直後に、直前まで見ていたズーム倍率・スクロール位置へ戻す。
        /// Undo はウィンドウのシリアライズ状態を丸ごと書き戻すため、これをしないと
        /// 編集内容と一緒に視点まで巻き戻り、拡大中の Ctrl+Z で表示が左上へ飛ぶ。
        /// </summary>
        internal void RestoreViewStateAfterUndo(IrocaWindow host)
        {
            // 一度も描画していない（＝控えが無い）／別ウィンドウの控えなら何もしない。
            if (host == null || s_viewStateOwner != host.GetInstanceID() || s_viewStateZoom <= 0f) return;

            previewZoom = s_viewStateZoom;
            _previewScrollPos = s_viewStateScroll;
        }
    }
}
