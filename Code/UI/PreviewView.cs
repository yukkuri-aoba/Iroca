// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Linq;
using System.Threading;
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
        // ─── シリアライズ対象（UI 状態） ─────────────────────────────
        public float previewZoom = 1f;
        public bool comparisonMode;
        public bool diffMode;

        // ─── ズーム範囲 ─────────────────────────────────────────────
        private const float MinPreviewZoom = 0.25f;
        // ピクセル単位で確認できるよう、最大ズーム時に「ソース 1px が画面上で最低
        // PixelInspectTargetPx ピクセルになる」ところまで拡大を許可する。高解像度
        // プレビューが映すのはソース画素で、画面倍率は scale*zoom なので zoom = target/scale。
        // 大きいテクスチャ(scale 小)ほど高ズームを許す。小さいテクスチャでも最低 8x。
        private const float PixelInspectTargetPx = 8f;
        private const float AbsoluteMaxPreviewZoom = 32f;

        // テクスチャの縮小率 scale に応じたズーム上限。
        private static float ComputeMaxZoom(float scale)
        {
            if (scale <= 0f) return AbsoluteMaxPreviewZoom;
            return Mathf.Clamp(PixelInspectTargetPx / scale, PixelInspectTargetPx, AbsoluteMaxPreviewZoom);
        }

        // 表示倍率が 104% のような半端な値にならないよう、ズームは「きれいな数字」の
        // 固定ストップにスナップさせる。1 ノッチ＝隣のストップ。25%〜3200% を網羅。
        private static readonly float[] ZoomStops =
        {
            0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f, 5f, 6f,
            8f, 10f, 12f, 16f, 20f, 24f, 32f
        };
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

        // ─── 実行時状態（NonSerialized） ──────────────────────────
        [System.NonSerialized] public Texture2D previewTexture;
        [System.NonSerialized] public Texture2D rawPreviewTexture;
        [System.NonSerialized] public Texture2D diffTexture;
        [System.NonSerialized] public bool previewDirty = true;
        [System.NonSerialized] private Vector2 _previewScrollPos;
        // Ctrl+スクロールズームで未消化のスクロール量。ZoomScrollStepThreshold を
        // 超えたぶんだけストップを進め、端数は次イベントへ繰り越す（感度を下げるため）。
        [System.NonSerialized] private float _zoomScrollAccum;
        // プレビュー用 ScrollView の実測ビューポート幅。詳細クロップの可視範囲算出に使う。
        // テクスチャ実寸基準ではカラム/ウィンドウ幅と食い違うため、毎フレーム実測する。
        [System.NonSerialized] private float _viewportWidth;
        // プレビューカラム(外側 ScrollView)の高さ。ホストがレイアウト確定値を毎フレーム渡す。
        // プレビュー枠をこの中に収める動的高さ調整に使う。0 は未設定＝調整なし(固定高)。
        [System.NonSerialized] public float availableColumnHeight;
        // 外側 ScrollView の内容座標系で、プレビュー枠より上に積まれた UI の実測高
        // (セクション見出し・操作行(比較/差分・ズーム率・生成状態)。縦並びレイアウトでは
        // 設定群も含む)。Repaint 時に実測し、次フレームの動的高さ算出に使う。
        [System.NonSerialized] private float _chromeAboveViewportH;
        // プレビュー枠の下の Space(4) と丸めの逃げ。動的高さの計算で差し引く。
        private const float ViewportBottomPadding = 8f;
        // ズーム率ラベルは毎フレーム描画されるため、ズーム値か言語が変わったときだけ
        // 文字列を再生成してアロケーションを避ける（IrocaWindow.EnsureZoneListCache と同方針）。
        [System.NonSerialized] private string _cachedZoomLabel;
        [System.NonSerialized] private int _cachedZoomPercent = -1;
        [System.NonSerialized] private LanguageMode _cachedZoomLang = (LanguageMode)(-1);

        // 非同期プレビュー状態
        // 戻り値は (processed, raw) のタプル。raw(ダウンサンプル済み元表示)もジョブ側で
        // 生成することで、テクスチャ切替直後のキャッシュミス時にメインスレッドで走っていた
        // BoxDownsample のヒッチをバックグラウンドへ追い出す。
        [System.NonSerialized] private readonly PreviewJob<(Color32[] processed, Color32[] raw)> _previewJob =
            new PreviewJob<(Color32[] processed, Color32[] raw)>();
        // 段階的リファインの第1段。ソースが大きい(scale<1)とき、まず縮小プロキシで概要を即表示する
        // 専用ジョブ。完了 apply で _previewjob(フル解像度)を同一スナップショットでスケジュールする。
        [System.NonSerialized] private readonly PreviewJob<(Color32[] processed, Color32[] raw)> _proxyJob =
            new PreviewJob<(Color32[] processed, Color32[] raw)>();
        [System.NonSerialized] private Color32[] _pendingProcessedDisplay;
        [System.NonSerialized] private Color32[] _pendingRawDisplay;
        [System.NonSerialized] private int _pendingPrevW, _pendingPrevH;
        [System.NonSerialized] private double _lastDirtyTime;
        private const double PreviewDebounceSeconds = 0.2;
        // ペイント中のオーバーレイ再構築の最小間隔（10Hz）。
        // bool[] の clone とジョブ再スケジュールがメインスレッドで頻発すると
        // GC でフレームが詰まるため、ペイント中だけ意図的に間引く。
        private const double PaintOverlayThrottleSeconds = 0.1;

        // Diff テクスチャ生成: ピクセル比較はバックグラウンドへ、SetPixels32/Apply はメインスレッド。
        [System.NonSerialized] private readonly PreviewJob<Color32[]> _diffJob = new PreviewJob<Color32[]>();
        [System.NonSerialized] private Color32[] _pendingDiffPixels;
        [System.NonSerialized] private int _pendingDiffW, _pendingDiffH;

        // ソースピクセルキャッシュ（テクスチャが変わったときのみ再取得）
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

        // 詳細プレビューは PreviewView の補助。
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

        public DetailPreviewView Detail => _detailView;
        public bool IsPreviewJobRunning => _proxyJob.IsRunning || _previewJob.IsRunning;

        public void MarkDirty() => previewDirty = true;

        /// <summary>
        /// ソース画素が変わったとき（テクスチャ差し替え・セッションリセット・エクスポートで
        /// 元ファイルを上書き）に、その画素から導かれた状態を漏れなく捨てる。
        /// ここで捨て損ねた状態は「プレビュー＝実出力」の一致を破る:
        /// 走行中ジョブは旧画素の結果を新テクスチャの表示へ apply し、フル段は旧 parityCache を
        /// 公開する。詳細プレビューはその parityCache を寸法一致だけで採用するため、同寸法の
        /// 別テクスチャへ切り替えると旧テクスチャの選択・色をズーム画面に転写する。
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

            _cachedSourceTexture = null;
            _cachedSrcPixels = null;
            _cachedRawDisplay = null;
            _cachedSrcW = _cachedSrcH = 0;
            _cachedPrevW = _cachedPrevH = 0;
            _trueSourceFor = null;
            _trueSourcePixels = null;
            _trueSourceW = _trueSourceH = 0;

            // ソース画素が変わる = キャッシュ済み選択の前提が変わるので選択キャッシュも破棄する。
            _selectionCache?.Clear();
            _proxySelectionCache?.Clear();

            // フル画像で解いた keep / 再着色統計も旧画素由来。
            if (_host != null) _host.previewParityCache = null;

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
            if (!IrocaWindow.IsReadable(tex)) return false;
            _trueSourcePixels = tex.GetPixels32();
            _trueSourceW = tex.width;
            _trueSourceH = tex.height;
            _trueSourceFor = tex;
            return true;
        }

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
        }

        // ─────────────────────── プレビュー ─────────────────────────

        public void Draw()
        {
            EditorGUILayout.LabelField(Localization.StepPrefixPreview + Localization.Preview, EditorStyles.boldLabel);

            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null)
            {
                EditorGUILayout.HelpBox(Localization.SetTexture, MessageType.Info);
                return;
            }

            if (!IrocaWindow.IsReadable(sourceTexture))
                return;

            // エクスポートと同じフル解像度ソースを確保（プレビュー＝実結果の一致のため）。
            if (!EnsureTrueSource(sourceTexture))
                return;

            // バックグラウンドプレビュータスクからの結果を適用（Texture2D API: メインスレッドのみ）
            if (_pendingProcessedDisplay != null)
                ApplyPendingPreview();

            // バックグラウンドで仕上がった diff ピクセルをテクスチャへ反映
            ApplyPendingDiff();

            // バックグラウンド詳細プレビュータスクからの結果を適用
            if (_detailView.HasPendingResult)
                _detailView.ApplyPendingResult();
            _detailView.ApplyPendingDiff();

            if (previewDirty)
            {
                _lastDirtyTime = EditorApplication.timeSinceStartup;
                // プロキシ・フル両段をキャンセル。プロキシ進行中の再ダーティでは、プロキシの
                // キャンセル(世代ぶつけ)で apply が抑止されフルが起動しない。
                _proxyJob.Cancel();
                _previewJob.Cancel();
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
                GeneratePreviewAsync();
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
            if (maskView.maskDirty && previewTexture != null)
            {
                bool throttle = maskView.isPainting &&
                    (EditorApplication.timeSinceStartup - maskView.lastOverlayRebuildTime)
                        < PaintOverlayThrottleSeconds;
                if (!throttle)
                {
                    maskView.lastOverlayRebuildTime = EditorApplication.timeSinceStartup;
                    maskView.RebuildMaskOverlay(previewTexture.width, previewTexture.height);
                    maskView.maskDirty = false;
                }
                // throttle 時は maskDirty を残し、次フレームで再評価する。
                // ペイント中は MouseDrag が継続的に Repaint を呼ぶので追加の RequestRepaint は不要。
            }

            // 「生成中…」インジケータの文言。プレビュー確立後は下の操作行（比較/差分・
            // ズーム率と同じ行）の右端に出す。非生成時も空白 " " を同じ場所に描き、
            // 出入りで UI が上下にジャンプしないよう行高を固定する。
            // 詳細プレビュー生成も同じ表示に統一する（fix.md 項目3）。
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
                EditorGUILayout.LabelField(generatingLabel);
                return;
            }

            int zoomPercent = Mathf.RoundToInt(previewZoom * 100f);
            if (zoomPercent != _cachedZoomPercent || _cachedZoomLang != Localization.CurrentLanguage || _cachedZoomLabel == null)
            {
                _cachedZoomPercent = zoomPercent;
                _cachedZoomLang = Localization.CurrentLanguage;
                _cachedZoomLabel = string.Format(Localization.ZoomLabel, zoomPercent);
            }

            // 操作行: 比較/差分トグル・ズーム率・生成状態を 1 行にまとめる。以前は
            // それぞれ 1 行ずつ計 3 行を使っており、既定ウィンドウ高(IrocaWindow.ShowWindow)
            // ではプレビュー枠の残り高が等倍 512px に届かず、100% でも縦スクロールバーが
            // 常に出ていた。トグルは内容幅に縮め、生成状態は右端に置く。
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(comparisonMode, new GUIContent(Localization.ComparisonMode, Localization.ComparisonModeTooltip), EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false)) != comparisonMode)
            {
                comparisonMode = !comparisonMode;
                if (comparisonMode) diffMode = false;
            }
            if (GUILayout.Toggle(diffMode, new GUIContent(Localization.DiffMode, Localization.DiffModeTooltip), EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)) != diffMode)
            {
                diffMode = !diffMode;
                if (diffMode) comparisonMode = false;
            }
            GUILayout.Space(10f);
            GUILayout.Label(new GUIContent(_cachedZoomLabel, Localization.ZoomHint), GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.Label(generatingLabel);
            EditorGUILayout.EndHorizontal();

            int srcW = _trueSourceW;
            int srcH = _trueSourceH;
            float scale = (srcW > IrocaConsts.Preview.MaxSize || srcH > IrocaConsts.Preview.MaxSize)
                ? IrocaConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH)
                : 1f;

            // テクスチャ切り替えやデシリアライズで残った半端な/上限超過のズーム値を、
            // 毎フレーム最も近い「きれいな数字」のストップへ丸める（表示倍率の見映え対策）。
            previewZoom = SnapToStop(previewZoom, ComputeMaxZoom(scale));

            // 詳細モード: ディスプレイピクセル > ソースピクセル時にアクティブ
            bool detailActive = scale < 1f &&
                                previewZoom > DetailPreviewView.DetailMinZoom &&
                                !comparisonMode;

            // 詳細プレビュー生成をポーリング
            if (detailActive)
            {
                if (!_detailView.detailJob.IsRunning &&
                    _detailView.lastDetailDirtyTime > 0 &&
                    (EditorApplication.timeSinceStartup - _detailView.lastDetailDirtyTime)
                        >= DetailPreviewView.DetailDebounceSeconds &&
                    _detailView.lastPreviewRect.width > 0)
                {
                    _detailView.lastDetailDirtyTime = 0;
                    _detailView.GenerateDetailPreviewAsync(srcW, srcH, _trueSourcePixels, scale, previewZoom, _previewScrollPos, _detailView.lastViewportW, _detailView.lastViewportH);
                }
                else if (_detailView.lastDetailDirtyTime > 0 || _detailView.detailJob.IsRunning)
                {
                    _host.RequestRepaint();
                }
            }

            float displayW = previewTexture.width  * previewZoom;
            float displayH = previewTexture.height * previewZoom;

            int panelCount = (comparisonMode && rawPreviewTexture != null) ? 2 : 1;

            // 縦ビューポート高。以前は固定 16px(ViewportMargin)を足すだけだったが、横スクロール
            // バー表示時に IMGUI がクライアント高から差し引くのは skin 実寸
            // (horizontalScrollbar.fixedHeight + margin)で、16px で足りる保証がない。不足すると
            // 画像がカラムより横に広い(=横バーが出る)とき、等倍(100%)でもクライアント高が
            // 内容高を数 px 下回り、縦スクロールバーが消えない。skin から実寸を導出して常時
            // 確保する(+2 は丸めの保険)。比較モードは Before/After ラベル行も内容高に含める
            // (これも縦バー残留の原因だった)。
            var hBarStyle = GUI.skin.horizontalScrollbar;
            float hBarReserve = hBarStyle.fixedHeight + hBarStyle.margin.vertical + 2f;
            float contentH = Mathf.Min(displayH, previewTexture.height) + GUI.skin.scrollView.padding.vertical;
            if (panelCount == 2)
                contentH += EditorGUIUtility.singleLineHeight + EditorStyles.label.margin.vertical;
            float maxViewH = contentH + Mathf.Max(IrocaConsts.Preview.ViewportMargin, hBarReserve);

            // プレビュー枠をカラムの残り空間に収める(動的高さ調整)。従来はテクスチャ実寸基準の
            // 固定高(等倍 512px なら ~530px)で、ウィンドウが低いとプレビュー枠自体が外側
            // ScrollView(縦オーバーフロー用)をあふれさせ、「③プレビュー」セクション全体が常時
            // スクロール範囲になっていた。カラム高からプレビュー枠より上の実測高を引いた残りへ
            // 縮め、収まらない分は内側 ScrollView のスクロール/パンに任せる。
            //
            // ただし枠を画像の自然サイズ(バー無しで収まる maxViewH)より小さくは潰さない。
            // 潰すと等倍(100%)でも枠内に縦バーが恒常的に出て、縦バーが幅を奪う分だけ横バーも
            // 連鎖しやすい(「100% に戻してもスクロールバーが残る」の主因)。chrome ごと収まる
            // ときだけそこへ縮め、収まらないときは枠にカラムビューポート高まで使わせ、あふれた
            // chrome は外側 ScrollView(縦オーバーフロー用)に任せる。カラム自体が下限未満の
            // 低ウィンドウでは下限で止め、従来どおり外側スクロールへ逃がす。
            if (availableColumnHeight > 0f && _chromeAboveViewportH > 0f)
            {
                float availWithChrome = availableColumnHeight - _chromeAboveViewportH - ViewportBottomPadding;
                float cap = availWithChrome >= maxViewH
                    ? availWithChrome
                    : availableColumnHeight - ViewportBottomPadding;
                maxViewH = Mathf.Min(maxViewH, Mathf.Max(cap, IrocaConsts.Preview.MinViewportHeight));
            }

            // プレビュー枠はカラム/ウィンドウ幅いっぱいに広げる（下の ExpandWidth）。
            // 以前はテクスチャ実寸基準の固定幅(≈528px)を MaxWidth で指定していたため、枠が
            // カラム幅を超えると外側 ScrollView(縦オーバーフロー用)の横バーが横取りし、
            // ズームしても横スクロールの可動幅がほぼゼロになっていた。枠を実際の表示領域に
            // 合わせることで、横パンは内側 ScrollView だけが受け持つ。
            //
            // 詳細クロップの「見えている範囲」は実測したスクロールビュー幅(_viewportWidth)を使う。
            // テクスチャ実寸基準だと、広いウィンドウで可視幅を過小評価して右側の高解像度
            // クロップを取りこぼす。初回フレームは未計測なのでテクスチャ基準を暫定値にする
            // (過大評価＝安全側)。高さは GUILayout.Height で固定なので maxViewH が実値。
            float fallbackViewW = Mathf.Min(
                displayW * panelCount + (panelCount - 1) * IrocaConsts.Preview.PanelSpacing,
                previewTexture.width * panelCount + (panelCount - 1) * IrocaConsts.Preview.PanelSpacing)
                + IrocaConsts.Preview.ViewportMargin;
            _detailView.lastViewportW = _viewportWidth > 1f ? _viewportWidth : fallbackViewW;
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

            Vector2 prevScroll = _previewScrollPos;
            _previewScrollPos = EditorGUILayout.BeginScrollView(
                _previewScrollPos,
                GUILayout.Height(maxViewH),
                GUILayout.ExpandWidth(true));
            if (_previewScrollPos != prevScroll)
            {
                _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                // 古い詳細プレビューは新しいスクロール位置と整合しないため、
                // 一旦表示を破棄して低解像度プレビューに統一する（fix.md 項目1）。
                _detailView.InvalidateDisplay();
            }

            Rect activePreviewRect = default;
            // Ctrl+スクロールズームの判定領域。比較モードでは Before/After 両パネルを
            // またぐ矩形にし、どちらのパネル上でもズームできるようにする。
            Rect zoomHitRect = default;

            if (comparisonMode && rawPreviewTexture != null)
            {
                EditorGUILayout.BeginHorizontal();

                // Before panel
                EditorGUILayout.BeginVertical(GUILayout.Width(displayW));
                EditorGUILayout.LabelField(Localization.Before, GUILayout.Width(displayW));
                var rawRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                EditorGUI.DrawPreviewTexture(rawRect, rawPreviewTexture);
                EditorGUILayout.EndVertical();

                GUILayout.Space(IrocaConsts.Preview.PanelSpacing);

                // After panel
                EditorGUILayout.BeginVertical(GUILayout.Width(displayW));
                EditorGUILayout.LabelField(Localization.After, GUILayout.Width(displayW));
                activePreviewRect = GUILayoutUtility.GetRect(displayW, displayH,
                    GUILayout.Width(displayW), GUILayout.Height(displayH));
                EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);
                if (maskView.maskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, maskView.maskOverlayTexture, ScaleMode.StretchToFill, true);
                if (maskView.zoneMaskOverlayTexture != null)
                    GUI.DrawTexture(activePreviewRect, maskView.zoneMaskOverlayTexture, ScaleMode.StretchToFill, true);
                EditorGUILayout.EndVertical();

                EditorGUILayout.EndHorizontal();

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

                if (detailActive && _detailView.detailPreviewTexture != null)
                {
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    Rect detailScreenRect = _detailView.ComputeDetailScreenRect(activePreviewRect, scale, previewZoom, _previewScrollPos, srcW, srcH);
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
                    EditorGUI.DrawPreviewTexture(activePreviewRect, previewTexture);

                    if (diffMode && diffTexture != null)
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
                // 枠外の単一行に統合する（fix.md 項目3）。
            }

            // 連続領域モードのシード(任意上書き)を十字オーバーレイで描画。
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                DrawFloodFillSeedOverlay(activePreviewRect);

            // プレビューレクトを格納して、次の詳細生成ティックで使用
            if (Event.current.type == EventType.Repaint && activePreviewRect.width > 0)
                _detailView.lastPreviewRect = activePreviewRect;

            HandlePreviewGlobalInput(zoomHitRect, scale);

            // スポイトとマスクペイントは排他。ペイントに入ったらスポイトを解除する。
            if (maskView.maskPaintActive && !string.IsNullOrEmpty(_host.EyedropperZoneId))
                _host.EyedropperZoneId = null;

            bool eyedropperArmed = !string.IsNullOrEmpty(_host.EyedropperZoneId) && !maskView.maskPaintActive;

            // スポイト武装中はプレビュークリックを横取りして実画素からサンプル取得に充てる
            // （シード設定・パンより優先。取得すると one-shot で自動解除）。
            if (eyedropperArmed)
                HandleEyedropperInput(activePreviewRect, srcW, srcH);

            // 連続領域モードの任意シード入力(Shift+クリック)。マスクペイント中・スポイト中は無効。
            if (!maskView.maskPaintActive && !eyedropperArmed)
                HandleFloodFillSeedInput(activePreviewRect);

            if (maskView.maskFoldout && maskView.maskPaintActive)
                HandlePreviewPaintInput(activePreviewRect);
            // パンはズーム>1 に限らず「画像がビューポートに収まっていない」とき常に許可する。
            // 動的高さ調整により等倍(100%)以下でも縦がはみ出すことがあり、そのとき
            // ズーム率だけで判定するとスクロールバー以外に位置を動かす手段がなくなる。
            else if (!maskView.maskPaintActive && !eyedropperArmed &&
                     (previewZoom > 1f
                      || displayH > maxViewH - hBarReserve
                      || displayW * panelCount > _detailView.lastViewportW))
                HandlePreviewPanInput(activePreviewRect);

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
        }

    }
}
