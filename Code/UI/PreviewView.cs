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
        [System.NonSerialized] public Texture2D diffTexture;
        // 立てた時刻を体感速度の起点(操作の時刻)として控える(PreviewLatency)。初期値の true は
        // 操作ではないので、プロパティを通さずに入れる。
        [System.NonSerialized] private bool _previewDirty = true;
        public bool previewDirty
        {
            get => _previewDirty;
            set
            {
                _previewDirty = value;
                if (!value) return;
                InputClock.NoteInput(PreviewLatencyCycle.Now);
                // 編集はどれもここを通るので、自動保存の合図にもする(IrocaWindow.Autosave)。
                _host?.NoteSessionEdited();
            }
        }
        [System.NonSerialized] private PreviewInputClock _inputClock;
        private PreviewInputClock InputClock => _inputClock ??= new PreviewInputClock();
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

        // 戻り値は (processed, raw) のタプル。raw(ダウンサンプル済み元表示)もジョブ側で
        // 生成することで、テクスチャ切替直後のキャッシュミス時にメインスレッドで走っていた
        // BoxDownsample のヒッチをバックグラウンドへ追い出す。
        // full はフル解像度の処理結果(シーンのアバターへのプレビュー LivePreview へ渡す)。
        [System.NonSerialized] private readonly PreviewJob<(Color32[] processed, Color32[] raw, Color32[] full)> _previewJob =
            new PreviewJob<(Color32[] processed, Color32[] raw, Color32[] full)>();
        // 段階的リファインの第1段。ソースが大きい(scale<1)とき、まず縮小プロキシで概要を即表示する
        // 専用ジョブ。完了 apply で _previewJob(フル解像度)を同一スナップショットでスケジュールする。
        [System.NonSerialized] private readonly PreviewJob<(Color32[] processed, Color32[] raw)> _proxyJob =
            new PreviewJob<(Color32[] processed, Color32[] raw)>();
        [System.NonSerialized] private Color32[] _pendingProcessedDisplay;
        [System.NonSerialized] private Color32[] _pendingRawDisplay;
        [System.NonSerialized] private int _pendingPrevW, _pendingPrevH;
        // 直近に確定したフル段のフル解像度の出力と、その元画素。拡大表示(詳細クロップ)はここから
        // 切り出す。元画素が変わったら使わない(InvalidateSourceCache / InvalidateFullOutput)。
        [System.NonSerialized] private Color32[] _fullOutput;
        [System.NonSerialized] private Color32[] _fullOutputSource;
        // 保留中の結果が確定(フル段)か。確定なら拡大表示をすぐ作り直す(ApplyPendingPreview)。
        [System.NonSerialized] private bool _pendingIsFinal;
        // 保留中の結果がドラッグの追従(プロキシだけ)か。
        [System.NonSerialized] private bool _pendingIsDrag;
        // 拡大中のドラッグでは、古い拡大表示(前の確定から切り出したもの)を隠して、追従しているプロキシを
        // 見せる(古いくっきりした絵の下に新しい絵が隠れて、ドラッグ中に画面が止まって見えないように)。
        // _fullOutputFreshSinceHide: 隠したあとにフル段が確定した(切り出し元が新しくなった)。
        // 隠している間は古いフル段の出力から切り出し直さない(古い色の拡大表示が戻ってしまう)。
        [System.NonSerialized] private bool _detailHiddenForDrag;
        [System.NonSerialized] private bool _fullOutputFreshSinceHide;
        // 保留中の結果がどの再生成のどの段か(体感速度の計測用。転送が済んだ時刻を打つ)。
        [System.NonSerialized] private PreviewLatencyCycle _pendingLatency;
        [System.NonSerialized] private LatencyStageMarks _pendingLatencyStage;
        [System.NonSerialized] private double _lastDirtyTime;
        // 手を止めてからフル解像度で確定するまでの待ち。ドラッグ中はこの間、プロキシだけを回して
        // 追従する(大きいテクスチャのフル段は数百 ms かかり、毎フレーム回すと取り消しの繰り返しになる)。
        private const double PreviewDebounceSeconds = 0.2;
        // ドラッグ中の追従プレビュー。_dragReq は直近の追従の入力、_dragReqStale はそれより後に
        // 操作があったか。_dragRunning は走っているジョブが追従のもの(新しい操作で取り消さない)か。
        [System.NonSerialized] private PreviewRequest _dragReq;
        [System.NonSerialized] private bool _dragReqStale = true;
        [System.NonSerialized] private bool _dragRunning;
        private bool IsDragPreviewRunning => _dragRunning && (_proxyJob.IsRunning || _previewJob.IsRunning);
        // ペイント中のオーバーレイ再構築の最小間隔（10Hz）。
        // bool[] の clone とジョブ再スケジュールがメインスレッドで頻発すると
        // GC でフレームが詰まるため、ペイント中だけ意図的に間引く。
        private const double PaintOverlayThrottleSeconds = 0.1;

        // Diff テクスチャ生成: ピクセル比較はバックグラウンドへ、SetPixels32/Apply はメインスレッド。
        [System.NonSerialized] private readonly PreviewJob<Color32[]> _diffJob = new PreviewJob<Color32[]>();
        [System.NonSerialized] private Color32[] _pendingDiffPixels;
        [System.NonSerialized] private int _pendingDiffW, _pendingDiffH;

        [System.NonSerialized] private Texture2D _cachedSourceTexture;
        [System.NonSerialized] private Color32[] _cachedSrcPixels;
        [System.NonSerialized] private Color32[] _cachedRawDisplay;
        [System.NonSerialized] private int _cachedSrcW, _cachedSrcH;
        [System.NonSerialized] private int _cachedPrevW, _cachedPrevH;

        // 選択結果キャッシュ: 「ターゲット色など再着色のみ」を変えたプレビュー再生成で、選択フェーズ
        // (Match/FloodFill/穴埋め/境界/ブラー/マスク再適用)を再計算せず復元して高速化する。
        // 出力はフル再計算と byte 一致(ProcessPixelsArray が選択パラメータのキーで管理)。テクスチャ/
        // 寸法が変わると画素前提が崩れるので Clear する。Unity の [Serializable]/ドメインリロード後も
        // 確実に生成されるよう、inline 初期化でなく Initialize() で ??= する(NonSerialized の流儀)。
        [System.NonSerialized] private SelectionCache _selectionCache;

        // 段階的リファインのプロキシ専用選択キャッシュ。プロキシはフルと寸法が異なり、SelectionCache は
        // zoneId 単位で (W,H) 一致を見て上書きするため、フルと共有するとスラッシュする。別インスタンスに
        // 分けてプロキシの再着色のみ変更も高速化する。テクスチャ/寸法変更時は _selectionCache と同時に Clear。
        [System.NonSerialized] private SelectionCache _proxySelectionCache;

        // エクスポートと同じ「ディスク上のフル解像度ファイル」をプレビュー処理にも使うための
        // キャッシュ。Unity のインポート設定（maxTextureSize / 圧縮）で縮小・劣化した
        // 画素ではなく元ファイルの画素で処理することで、プレビューと実際のエクスポート結果を
        // 一致させる。テクスチャ単位でキャッシュする。
        [System.NonSerialized] private Texture2D _trueSourceFor;
        [System.NonSerialized] private Color32[] _trueSourcePixels;
        [System.NonSerialized] private int _trueSourceW, _trueSourceH;
        // 直近に画素を取れなかったテクスチャ（毎フレームの可否判定で読み直さないための負のキャッシュ）。
        [System.NonSerialized] private Texture2D _trueSourceFailedFor;

        [System.NonSerialized] private DetailPreviewView _detailView;

        [System.NonSerialized] private IrocaWindow _host;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
            _selectionCache ??= new SelectionCache();
            _proxySelectionCache ??= new SelectionCache();
            _detailView ??= new DetailPreviewView();
            _detailView.Initialize(host);
        }

        /// <summary>
        /// 保持しているフル段の出力を捨てる(拡大表示の切り出し元にしない)。処理したゾーンの集合が
        /// 変わったとき(ソロ表示の切り替え)に呼ぶ。次のフル段の確定までは拡大表示を作り直さない。
        /// </summary>
        public void InvalidateFullOutput()
        {
            _fullOutput = null;
            _fullOutputSource = null;
        }

        // 次回の再生成でプロキシ段(低解像度の概要表示)を使わない 1 回限りのフラグ。
        // GeneratePreviewAsync が消費してリセットする。
        [System.NonSerialized] private bool _skipProxyOnce;

        /// <summary>
        /// プレビュー再生成を要求するが、段階的リファインのプロキシ段は使わない。
        /// 確定表示が既にある状態での差分的な更新(AI 提案のマスク反映など)でプロキシを挟むと、
        /// 鮮明な表示が一瞬低解像度へ戻る「ちらつき」になる(クリックキュー化でコミットが
        /// 連続すると特に目立つ)。フル段のみで、旧表示を保ったまま静かに差し替える。
        /// 通常の <see cref="previewDirty"/> が直後に重なった場合もフラグは 1 回で消費され、
        /// その再生成のプロキシが 1 度飛ぶだけ(結果は同じ・フィードバックが少し遅れるのみ)。
        /// </summary>
        public void MarkDirtyFullRefine()
        {
            previewDirty = true;
            _skipProxyOnce = true;
        }

        /// <summary>
        /// ソース画素が変わったとき（テクスチャ差し替え・セッションリセット・エクスポートで
        /// 元ファイルを上書き）に、その画素から導かれた状態を漏れなく捨てる。
        /// ここで捨て損ねた状態は「プレビュー＝実出力」の一致を破る:
        /// 走行中ジョブは旧画素の結果を新テクスチャの表示へ apply し、保持しているフル段の出力を
        /// 残すと、拡大表示(詳細クロップ)が旧テクスチャの色替え結果を切り出して見せる。
        /// </summary>
        public void InvalidateSourceCache()
        {
            // 走行中のジョブは旧画素を処理中。世代をぶつけて apply ごと無効化する。
            _proxyJob.Cancel();
            _previewJob.Cancel();
            _diffJob.Cancel();
            _pendingProcessedDisplay = null;
            _pendingRawDisplay = null;
            _pendingDiffPixels = null;
            // 追従プレビューの入力とフル段の出力も旧画素のもの。使い回さない。
            InvalidateFullOutput();
            _detailHiddenForDrag = false;
            _fullOutputFreshSinceHide = false;
            _dragReq = null;
            _dragReqStale = true;
            _dragRunning = false;

            _cachedSourceTexture = null;
            _cachedSrcPixels = null;
            _cachedRawDisplay = null;
            _cachedSrcW = _cachedSrcH = 0;
            _cachedPrevW = _cachedPrevH = 0;
            _trueSourceFor = null;
            _trueSourcePixels = null;
            _trueSourceW = _trueSourceH = 0;
            _trueSourceFailedFor = null;

            // ソース画素が変わる = キャッシュ済み選択の前提が変わるので選択キャッシュも破棄する。
            _selectionCache?.Clear();
            _proxySelectionCache?.Clear();


            // 旧テクスチャのクロップが新テクスチャ上に重なって見えるのを防ぐ
            // （詳細ジョブのキャンセルと表示テクスチャの解放を含む）。
            _detailView?.InvalidateDisplay();
        }

        /// <summary>
        /// プレビュー処理用のソース画素を確保する。可能ならディスク上の元ファイルを
        /// フル解像度で読み込み（エクスポートと同一経路）、PNG/JPG 以外や生成テクスチャ等で
        /// 失敗した場合はインポート済みテクスチャの GetPixels32 にフォールバックする。
        /// 成功すると <see cref="_trueSourcePixels"/> / <see cref="_trueSourceW"/> /
        /// <see cref="_trueSourceH"/> が有効になる。
        /// </summary>
        private bool EnsureTrueSource(Texture2D tex)
        {
            if (tex == null) return false;
            if (_trueSourceFor == tex && _trueSourcePixels != null) return true;
            // 失敗もテクスチャ単位で覚える。UI の可否判定（スポイト・自動調整・手順表示）が
            // OnGUI ごとにここを通るので、覚えないと「読めないテクスチャ」で毎フレーム
            // ファイル読み込みとデコードを試すことになる。あとで Read/Write を有効にすれば
            // IsReadable が true へ変わるため、そのときは覚えた失敗を無視して取り直す。
            if (_trueSourceFailedFor == tex && !IrocaWindow.IsReadable(tex)) return false;

            string path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                Texture2D tmp = null;
                try
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tmp.LoadImage(bytes))
                    {
                        _trueSourcePixels = tmp.GetPixels32();
                        _trueSourceW = tmp.width;
                        _trueSourceH = tmp.height;
                        _trueSourceFor = tex;
                        _trueSourceFailedFor = null;
                        return true;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Iroca] Source file load failed, falling back to imported texture: {ex.Message}");
                }
                finally
                {
                    if (tmp != null) Object.DestroyImmediate(tmp);
                }
            }

            // フォールバック: インポート済みテクスチャ（要 Read/Write）。
            if (!IrocaWindow.IsReadable(tex))
            {
                _trueSourceFailedFor = tex;
                return false;
            }
            _trueSourcePixels = tex.GetPixels32();
            _trueSourceW = tex.width;
            _trueSourceH = tex.height;
            _trueSourceFor = tex;
            _trueSourceFailedFor = null;
            return true;
        }

        /// <summary>
        /// このテクスチャから処理用の画素を取れるか（原本ファイルのデコード、または
        /// インポート済みテクスチャの GetPixels32）。UI の可否判定が「Read/Write が
        /// 有効か」だけを見ていると、PNG/JPG のように原本を直接読める場合でも
        /// 不要にインポート設定の変更を要求してしまうため、実際に取れるかで判定する。
        /// 成否はテクスチャ単位でキャッシュする。
        /// </summary>
        public bool CanProvideSourcePixels(Texture2D tex) => EnsureTrueSource(tex);

        /// <summary>
        /// 自動調整など他ビューが、プレビュー/エクスポートと同一の true source 画素で解析する
        /// ための読み取り専用アクセサ。返す配列は内部キャッシュの共有インスタンスなので、
        /// 呼び出し側は書き換えないこと（プレビュージョブ側も clone してから処理する規約）。
        /// 取得不能（元ファイル読込失敗 かつ 非 Readable）なら false。
        /// </summary>
        public bool TryGetTrueSourcePixels(Texture2D tex, out Color32[] pixels, out int w, out int h)
        {
            if (EnsureTrueSource(tex))
            {
                pixels = _trueSourcePixels;
                w = _trueSourceW;
                h = _trueSourceH;
                return true;
            }
            pixels = null;
            w = h = 0;
            return false;
        }

        public void Dispose()
        {
            Suspend();
            _proxyJob.Dispose();
            _previewJob.Dispose();
            _diffJob.Dispose();
            _detailView?.Dispose();
        }

        public void Suspend()
        {
            _proxyJob.Cancel();
            _previewJob.Cancel();
            _diffJob.Cancel();
            _detailView?.Suspend();
            _pendingProcessedDisplay = null;
            _pendingRawDisplay = null;
            _pendingDiffPixels = null;
            _lastDirtyTime = 0;
            TextureSlot.Release(ref previewTexture);
            TextureSlot.Release(ref rawPreviewTexture);
            TextureSlot.Release(ref diffTexture);
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

            // エクスポートと同じフル解像度ソースを確保（プレビュー＝実結果の一致のため）。
            // EnsureTrueSource は原本が読めなければインポート済みテクスチャへ落ちるので、
            // ここで Read/Write を別途要求しない（PNG/JPG は Read/Write なしで表示できる）。
            if (!EnsureTrueSource(sourceTexture))
            {
                DrawTitleRow(false, 0f);
                return;
            }

            // バックグラウンドプレビュータスクからの結果を適用（Texture2D API: メインスレッドのみ）
            if (_pendingProcessedDisplay != null)
                ApplyPendingPreview();

            // バックグラウンドで仕上がった diff ピクセルをテクスチャへ反映
            ApplyPendingDiff();

            // バックグラウンド詳細プレビュータスクからの結果を適用
            if (_detailView.HasPendingResult)
            {
                _detailView.ApplyPendingResult();
                // 隠している間は新しい切り出ししか作らない(下の条件)ので、届いたら見せてよい。
                _detailHiddenForDrag = false;
            }
            _detailView.ApplyPendingDiff();

            if (previewDirty)
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
                previewDirty = false;
            }
            else if (!_proxyJob.IsRunning && !_previewJob.IsRunning &&
                     _lastDirtyTime > 0 &&
                     (GUIUtility.hotControl == 0 ||
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

            var maskView = _host._maskView;

            // バックグラウンドで完了したオーバーレイ Color32[] を先に Texture2D へ適用する。
            maskView.ApplyPendingOverlay();

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
                        maskView.maskDirty = false;
                    }
                    // throttle 時は maskDirty を残し、次フレームで再評価する。
                    // ペイント中は MouseDrag が継続的に Repaint を呼ぶので追加の RequestRepaint は不要。
                }
                maskView.SyncOverlayFilter(EffectiveZoom);
            }

            // 「生成中…」インジケータの文言。プレビュー確立後は下の操作行（比較/差分・
            // 元を表示と同じ行）の右端に出す。非生成時も空白 " " を同じ場所に描き、
            // 出入りで UI が上下にジャンプしないよう行高を固定する。
            // 詳細プレビュー生成も同じ表示に統一する（c107e85）。
            string generatingLabel;
            if (_proxyJob.IsRunning || _previewJob.IsRunning)
                generatingLabel = Localization.GeneratingPreview;
            else if (_detailView.detailJob.IsRunning)
                generatingLabel = Localization.GeneratingDetailPreview;
            else
                generatingLabel = " ";

            if (previewTexture == null)
            {
                // 初回生成中はまだ操作行が無いので、生成状態だけ単独の 1 行で表示する。
                DrawTitleRow(false, 0f);
                EditorGUILayout.LabelField(generatingLabel);
                return;
            }

            int srcW = _trueSourceW;
            int srcH = _trueSourceH;
            float scale = (srcW > IrocaConsts.Preview.MaxSize || srcH > IrocaConsts.Preview.MaxSize)
                ? IrocaConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH)
                : 1f;

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
            }
            if (GUILayout.Toggle(diffMode, new GUIContent(Localization.DiffMode, Localization.DiffModeTooltip), EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)) != diffMode)
            {
                diffMode = !diffMode;
                if (diffMode)
                {
                    comparisonMode = false;
                    // 詳細 diff は diff モード表示中しか生成しない(DetailPreviewView 参照)ため、
                    // ON へ切り替えたら詳細クロップを再生成して diff を作らせる。
                    _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                }
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

            // 詳細モード: ソースが縮小されて表示されている(scale<1)テクスチャを、プレビューテクスチャより
            // 大きく表示しているとき(拡大したとき・広いウィンドウの全体表示)、低解像度プレビューの
            // 引き伸ばしではなくソース解像度から作り直したクロップを出す。
            bool detailActive = scale < 1f &&
                                effZoom > DetailPreviewView.DetailMinZoom &&
                                !comparisonMode;

            if (detailActive)
            {
                if (!_detailView.detailJob.IsRunning &&
                    (!_detailHiddenForDrag || _fullOutputFreshSinceHide) &&
                    _detailView.lastDetailDirtyTime > 0 &&
                    (EditorApplication.timeSinceStartup - _detailView.lastDetailDirtyTime)
                        >= DetailPreviewView.DetailDebounceSeconds &&
                    _detailView.lastPreviewRect.width > 0)
                {
                    var latency = _detailView.TakeLatencyFor(_detailView.lastDetailDirtyTime, srcW, srcH);
                    _detailView.lastDetailDirtyTime = 0;
                    var processedFull = ReferenceEquals(_fullOutputSource, _trueSourcePixels) ? _fullOutput : null;
                    _detailView.GenerateDetailPreviewAsync(srcW, srcH, _trueSourcePixels, processedFull, scale, effZoom, _previewScrollPos, _detailView.lastViewportW, _detailView.lastViewportH, latency);
                }
                else if (_detailView.lastDetailDirtyTime > 0 || _detailView.detailJob.IsRunning)
                {
                    _host.RequestRepaint();
                }
            }

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
                _detailView.MarkViewChanged();
                // 古い詳細プレビューは新しいスクロール位置と整合しないため、
                // 一旦表示を破棄して低解像度プレビューに統一する（c107e85）。
                _detailView.InvalidateDisplay();
            }

            Rect activePreviewRect = default;
            // Ctrl+スクロールズームの判定領域。比較モードでは Before/After 両パネルを
            // またぐ矩形にし、どちらのパネル上でもズームできるようにする。
            Rect zoomHitRect = default;

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
                EditorGUI.DrawPreviewTexture(rawRect, rawPreviewTexture);
                EditorGUILayout.EndVertical();

                GUILayout.Space(IrocaConsts.Preview.PanelSpacing);

                EditorGUILayout.BeginVertical(GUILayout.Width(displayW));
                EditorGUILayout.LabelField(Localization.After, GUILayout.Width(captionW));
                activePreviewRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);
                if (maskView.maskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, maskView.maskOverlayTexture, ScaleMode.StretchToFill, true);
                if (maskView.zoneMaskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, maskView.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
                EditorGUILayout.EndVertical();

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

                // 「元を表示」で押下中は、詳細クロップ(再着色後のみ保持)を使わず低解像度の
                // 変更前を出す。クロップの raw をテクスチャ化して持つとズーム中の VRAM が倍に
                // なるため、押している間だけ解像度が落ちることを許容する(ツールチップに明記)。
                if (detailActive && _detailView.detailPreviewTexture != null && !peekOriginal && !_detailHiddenForDrag)
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    Rect detailScreenRect = _detailView.ComputeDetailScreenRect(activePreviewRect, scale, effZoom, _previewScrollPos, srcW, srcH);
                    GUI.DrawTexture(detailScreenRect, _detailView.detailPreviewTexture,
                        ScaleMode.StretchToFill, false);

                    if (diffMode && _detailView.detailDiffTexture != null)
                        GUI.DrawTexture(detailScreenRect, _detailView.detailDiffTexture,
                            ScaleMode.StretchToFill, true);
                    else
                    {
                        // マスクオーバーレイは等倍用の低解像度テクスチャを画像全体へ引き伸ばして
                        // 描く。ブロック整列ペイント後は 1 画素 = マスクブロック一様なので Point
                        // 拡大でも正確で、クロップ専用オーバーレイ(詳細再生成まで更新されず
                        // ペイントが見えなかった)を廃止できる。ペイント中の直接書き込み
                        // (PaintMask)も即このテクスチャに反映される。
                        if (maskView.maskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, maskView.maskOverlayTexture, ScaleMode.StretchToFill, true);
                        if (maskView.zoneMaskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, maskView.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
                    }
                }
                else
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect,
                        peekOriginal ? rawPreviewTexture : previewTexture);

                    // 変更前を見せている間は差分ハイライトを重ねない（変更前に「変わった場所」を
                    // 塗ると、元テクスチャに無い色が乗って見え、確認したい当のものが隠れる）。
                    if (!peekOriginal && diffMode && diffTexture != null)
                        GUI.DrawTexture(activePreviewRect, diffTexture, ScaleMode.StretchToFill, true);
                    else
                    {
                        if (maskView.maskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, maskView.maskOverlayTexture, ScaleMode.StretchToFill, true);
                        if (maskView.zoneMaskOverlayTexture != null)
                            GUI.DrawTexture(activePreviewRect, maskView.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
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

            // プレビューレクトを格納して、次の詳細生成ティックで使用
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                _detailView.lastPreviewRect = activePreviewRect;

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
                HandlePreviewPanInput(activePreviewRect, leftDragPans);
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
                // モードが無いときは、Scene でモデルをクリックした結果の案内か使い方を出す
                // (何も無ければ空白 1 文字)。どれも miniLabel の高さに揃える。行を出入りさせたり高さを
                // 変えたりするとプレビュー枠が上下に跳ね、chrome 実測（_chromeAboveViewportH）も揺れる。
                DrawSceneClickRow();
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
                if (GUILayout.Button(reset, EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    ResetZoom(maxZoom);
            }
            EditorGUILayout.EndHorizontal();
        }

        // 見出し行(題名・ズーム率・リセット)がカラムの見えている幅に収まるか。
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
                         + button.CalcSize(reset).x + button.margin.horizontal;
            return need <= avail;
        }

        /// <summary>ズームを初期表示（100%）へ戻し、表示位置も先頭へ戻す。</summary>
        private void ResetZoom(float maxZoom)
        {
            previewZoom = Mathf.Clamp(1f, MinPreviewZoom, maxZoom);
            _previewScrollPos = Vector2.zero;
            _detailView.MarkViewChanged();
            // ズーム比が変わると古い詳細クロップは整合しない（Ctrl+スクロール経路と同じ）。
            _detailView.InvalidateDisplay();
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
