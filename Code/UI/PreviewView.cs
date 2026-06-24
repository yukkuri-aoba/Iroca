// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Camereo
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Camereo
{
    /// <summary>
    /// メインプレビュー描画、比較 / 差分モード、ズーム、プレビュー生成ジョブの起動、
    /// 表示用 Texture の所有を担当する。詳細プレビューは DetailPreviewView を保持する補助形式。
    /// </summary>
    [System.Serializable]
    internal class PreviewView
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
        // ズーム率ラベルは毎フレーム描画されるため、ズーム値か言語が変わったときだけ
        // 文字列を再生成してアロケーションを避ける（CamereoWindow.EnsureZoneListCache と同方針）。
        [System.NonSerialized] private string _cachedZoomLabel;
        [System.NonSerialized] private int _cachedZoomPercent = -1;
        [System.NonSerialized] private LanguageMode _cachedZoomLang = (LanguageMode)(-1);

        // 非同期プレビュー状態
        // 戻り値は (processed, raw) のタプル。raw(ダウンサンプル済み元表示)もジョブ側で
        // 生成することで、テクスチャ切替直後のキャッシュミス時にメインスレッドで走っていた
        // BoxDownsample のヒッチをバックグラウンドへ追い出す。
        [System.NonSerialized] private readonly PreviewJob<(Color32[] processed, Color32[] raw)> _previewJob =
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

        // エクスポートと同じ「ディスク上のフル解像度ファイル」をプレビュー処理にも使うための
        // キャッシュ。Unity のインポート設定（maxTextureSize / 圧縮）で縮小・劣化した
        // 画素ではなく元ファイルの画素で処理することで、プレビューと実際のエクスポート結果を
        // 一致させる。テクスチャ単位でキャッシュする。
        [System.NonSerialized] private Texture2D _trueSourceFor;
        [System.NonSerialized] private Color32[] _trueSourcePixels;
        [System.NonSerialized] private int _trueSourceW, _trueSourceH;

        // 詳細プレビューは PreviewView の補助。
        [System.NonSerialized] private DetailPreviewView _detailView;

        [System.NonSerialized] private CamereoWindow _host;

        public void Initialize(CamereoWindow host)
        {
            _host = host;
            _detailView ??= new DetailPreviewView();
            _detailView.Initialize(host);
        }

        public DetailPreviewView Detail => _detailView;
        public bool IsPreviewJobRunning => _previewJob.IsRunning;

        public void MarkDirty() => previewDirty = true;

        /// <summary>
        /// テクスチャが変わったときにソースピクセルキャッシュを無効化する。
        /// </summary>
        public void InvalidateSourceCache()
        {
            _cachedSourceTexture = null;
            _cachedSrcPixels = null;
            _cachedRawDisplay = null;
            _trueSourceFor = null;
            _trueSourcePixels = null;
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
                    Debug.LogWarning($"[Camereo] Source file load failed, falling back to imported texture: {ex.Message}");
                }
                finally
                {
                    if (tmp != null) Object.DestroyImmediate(tmp);
                }
            }

            // フォールバック: インポート済みテクスチャ（要 Read/Write）。
            if (!CamereoWindow.IsReadable(tex)) return false;
            _trueSourcePixels = tex.GetPixels32();
            _trueSourceW = tex.width;
            _trueSourceH = tex.height;
            _trueSourceFor = tex;
            return true;
        }

        public void Dispose()
        {
            Suspend();
            _previewJob.Dispose();
            _diffJob.Dispose();
            _detailView?.Dispose();
        }

        public void Suspend()
        {
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

            if (!CamereoWindow.IsReadable(sourceTexture))
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
                _previewJob.Cancel();
                _host.RequestRepaint();
                previewDirty = false;
            }
            else if (!_previewJob.IsRunning &&
                     _lastDirtyTime > 0 &&
                     (GUIUtility.hotControl == 0 ||
                      (EditorApplication.timeSinceStartup - _lastDirtyTime)
                          >= PreviewDebounceSeconds))
            {
                _lastDirtyTime = 0;
                GeneratePreviewAsync();
            }
            else if (_lastDirtyTime > 0 || _previewJob.IsRunning)
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

            // 「生成中…」インジケータは常に同じ高さの 1 行を確保する。
            // 出入りで後続の UI（ズーム表示・比較ボタン・プレビュー画像）が
            // 上下にジャンプするのを防ぐため、非生成時は空白を表示する。
            // 詳細プレビュー生成も同じ枠外行に統一する（fix.md 項目3）。
            string generatingLabel;
            if (_previewJob.IsRunning)
                generatingLabel = Localization.GeneratingPreview;
            else if (_detailView.detailJob.IsRunning)
                generatingLabel = Localization.GeneratingDetailPreview;
            else
                generatingLabel = " ";
            EditorGUILayout.LabelField(generatingLabel);

            if (previewTexture == null)
            {
                return;
            }

            int zoomPercent = Mathf.RoundToInt(previewZoom * 100f);
            if (zoomPercent != _cachedZoomPercent || _cachedZoomLang != Localization.CurrentLanguage || _cachedZoomLabel == null)
            {
                _cachedZoomPercent = zoomPercent;
                _cachedZoomLang = Localization.CurrentLanguage;
                _cachedZoomLabel = string.Format(Localization.ZoomLabel, zoomPercent);
            }
            EditorGUILayout.LabelField(new GUIContent(_cachedZoomLabel, Localization.ZoomHint));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(comparisonMode, new GUIContent(Localization.ComparisonMode, Localization.ComparisonModeTooltip), EditorStyles.miniButtonLeft) != comparisonMode)
            {
                comparisonMode = !comparisonMode;
                if (comparisonMode) diffMode = false;
            }
            if (GUILayout.Toggle(diffMode, new GUIContent(Localization.DiffMode, Localization.DiffModeTooltip), EditorStyles.miniButtonRight) != diffMode)
            {
                diffMode = !diffMode;
                if (diffMode) comparisonMode = false;
            }
            EditorGUILayout.EndHorizontal();

            int srcW = _trueSourceW;
            int srcH = _trueSourceH;
            float scale = (srcW > CamereoConsts.Preview.MaxSize || srcH > CamereoConsts.Preview.MaxSize)
                ? CamereoConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH)
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

            float maxViewH = Mathf.Min(displayH, previewTexture.height) + CamereoConsts.Preview.ViewportMargin;
            int panelCount = (comparisonMode && rawPreviewTexture != null) ? 2 : 1;

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
                displayW * panelCount + (panelCount - 1) * CamereoConsts.Preview.PanelSpacing,
                previewTexture.width * panelCount + (panelCount - 1) * CamereoConsts.Preview.PanelSpacing)
                + CamereoConsts.Preview.ViewportMargin;
            _detailView.lastViewportW = _viewportWidth > 1f ? _viewportWidth : fallbackViewW;
            _detailView.lastViewportH = maxViewH;

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

                GUILayout.Space(CamereoConsts.Preview.PanelSpacing);

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
                    else if (_detailView.detailMaskOverlayTexture != null)
                        GUI.DrawTexture(detailScreenRect, _detailView.detailMaskOverlayTexture,
                            ScaleMode.StretchToFill, true);
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

            // 連続領域モードの任意シード入力(Shift+クリック)。マスクペイント中は無効。
            if (!maskView.maskPaintActive)
                HandleFloodFillSeedInput(activePreviewRect);

            if (maskView.maskFoldout && maskView.maskPaintActive)
                HandlePreviewPaintInput(activePreviewRect);
            else if (!maskView.maskPaintActive && previewZoom > 1f)
                HandlePreviewPanInput(activePreviewRect);

            EditorGUILayout.EndScrollView();

            // スクロールビューの実幅を測り、次フレームの詳細クロップ可視範囲に使う。
            // Repaint 時のみ有効値が返るため、そのときだけ更新する。
            if (Event.current.type == EventType.Repaint)
            {
                float vw = GUILayoutUtility.GetLastRect().width;
                if (vw > 1f) _viewportWidth = vw;
            }

            EditorGUILayout.Space(4);
        }

        // ───────────────────────── Preview Input ─────────────────────────

        private void HandlePreviewGlobalInput(Rect previewRect, float scale)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.ScrollWheel:
                    if (isInRect && e.control && Mathf.Abs(e.delta.y) > ZoomEpsilon)
                    {
                        // スクロール量を蓄積し、閾値ぶんたまるごとに 1 ストップだけ進める。
                        // 拡大↔縮小で向きが変わったら貯金をリセットし、逆方向の繰り越しが
                        // 残って一瞬反対に動くのを防ぐ（即応性を保つ）。
                        if (_zoomScrollAccum != 0f && Mathf.Sign(e.delta.y) != Mathf.Sign(_zoomScrollAccum))
                            _zoomScrollAccum = 0f;
                        _zoomScrollAccum += e.delta.y;

                        // 閾値を超えたぶんのストップ数。端数は次イベントへ繰り越す。
                        int steps = (int)(_zoomScrollAccum / ZoomScrollStepThreshold);
                        if (steps != 0)
                        {
                            _zoomScrollAccum -= steps * ZoomScrollStepThreshold;

                            float oldZoom = previewZoom;
                            // 上スクロール(delta.y<0 ⇒ steps<0)で拡大。
                            bool zoomIn = steps < 0;
                            int count = Mathf.Abs(steps);
                            float newZoom = oldZoom;
                            for (int i = 0; i < count; i++)
                                newZoom = StepZoom(newZoom, zoomIn, ComputeMaxZoom(scale));

                            if (previewTexture != null && Mathf.Abs(newZoom - oldZoom) > 0.0001f)
                            {
                                Vector2 mouseInImage = e.mousePosition - new Vector2(previewRect.x, previewRect.y);
                                _previewScrollPos += mouseInImage * (newZoom / oldZoom - 1f);
                                _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                                _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                            }

                            previewZoom = newZoom;
                            _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                            // ズーム比が変わるとピクセル/ソース比も変わるため、
                            // 古い詳細プレビューは整合しなくなる。破棄して再生成を待つ。
                            _detailView.InvalidateDisplay();
                            _host.RequestRepaint();
                        }

                        // ストップが動かなくてもイベントは消費し、外側スクロールビューが
                        // 動かないようにする（蓄積中も含め Ctrl+スクロールはここで完結）。
                        e.Use();
                    }
                    break;

            }
        }

        private void HandlePreviewPanInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && isInRect)
                    {
                        GUIUtility.hotControl = controlId;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        // 「掴んで動かす」操作なので、ドラッグ量と画像の移動量を 1:1 にする。
                        // _previewScrollPos はコンテンツ座標(=ズーム後の displayW/H 空間)で持ち、
                        // この単位は画面ピクセルと等価。よって e.delta(画面 px)をそのまま引けば、
                        // カーソル下に掴んだ点が常にカーソルへ追従する（ズーム倍率に依らず一定）。
                        // 以前は e.delta に previewZoom を掛けて「1 ストロークで全幅横断」を狙って
                        // いたが、高ズームほど画像がカーソルの何倍も飛んで感度が高すぎたため廃止。
                        _previewScrollPos -= e.delta;
                        _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                        _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                        _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                        // パンで詳細クロップ位置が変わるので古い詳細プレビューを破棄。
                        _detailView.InvalidateDisplay();
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    if (isInRect)
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Pan);
                    break;
            }
        }

        private void HandlePreviewPaintInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            var maskView = _host._maskView;

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && isInRect)
                    {
                        GUIUtility.hotControl = controlId;
                        maskView.isPainting = true;
                        maskView._maskStrokeStarted = false;
                        maskView.lastPaintUV = -Vector2.one;
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (maskView.isPainting && GUIUtility.hotControl == controlId)
                    {
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        maskView.isPainting = false;
                        // ストローク終了: bool[] バッファを _session.maskState に書き戻す。
                        maskView.EndStroke();
                        maskView.lastPaintUV = -Vector2.one;
                        previewDirty = true;
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.Repaint:
                    if (maskView.isPainting && isInRect)
                    {
                        float brushPixels = maskView.brushSize * previewZoom;
                        var cursorColor = maskView.brushEraseMode
                            ? CamereoColors.BrushCursorInclude
                            : CamereoColors.BrushCursorExclude;
                        float r = brushPixels * 0.5f;
                        EditorGUI.DrawRect(
                            new Rect(e.mousePosition.x - r, e.mousePosition.y - r, r * 2f, r * 2f),
                            cursorColor);
                    }
                    break;
            }
        }

        private void HandleFloodFillSeedInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            var zones = _host.Session.zones;
            int targetZoneIndex = -1;
            int activeZoneIndex = _host._maskView.activeMaskTarget;
            if (zones != null && activeZoneIndex >= 0 && activeZoneIndex < zones.Count)
            {
                var activeZone = zones[activeZoneIndex];
                if (activeZone.enabled && activeZone.mode == SelectionMode.ColorPick && activeZone.useFloodFill)
                    targetZoneIndex = activeZoneIndex;
            }

            if (targetZoneIndex < 0)
            {
                for (int i = 0; i < zones.Count; i++)
                {
                    var zone = zones[i];
                    if (!zone.enabled || zone.mode != SelectionMode.ColorPick || !zone.useFloodFill) continue;
                    targetZoneIndex = i;
                    break;
                }
            }

            bool hasFloodFill = targetZoneIndex >= 0;

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    // 自動アンカリングが既定なので、通常クリックはパン/検分に使えるよう温存し、
                    // シード(任意の上書き=その塊だけ残す)は Shift+クリックでのみ設定する。
                    if (hasFloodFill && e.button == 0 && e.shift && isInRect && !e.control && !e.alt)
                    {
                        float u = (e.mousePosition.x - previewRect.x) / previewRect.width;
                        float v = 1f - (e.mousePosition.y - previewRect.y) / previewRect.height;
                        u = Mathf.Clamp01(u);
                        v = Mathf.Clamp01(v);

                        // 1 つ目の対象ゾーン更新の直前に Undo を登録する（記録されるのは更新前の seedUV）。
                        var newSeed = new Vector2(u, v);
                        bool changed = false;
                        var zone = zones[targetZoneIndex];
                        if (zone.seedUV != newSeed)
                        {
                            Undo.RecordObject(_host, "Set Flood Fill Seed");
                            zone.seedUV = newSeed;
                            changed = true;
                        }
                        if (changed)
                        {
                            previewDirty = true;
                            GUIUtility.hotControl = controlId;
                            e.Use();
                            _host.RequestRepaint();
                        }
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    if (hasFloodFill && isInRect && e.shift)
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Link);
                    break;
            }
        }

        private void DrawFloodFillSeedOverlay(Rect previewRect)
        {
            foreach (var z in _host.Session.zones)
            {
                if (!z.enabled || z.mode != SelectionMode.ColorPick || !z.useFloodFill) continue;
                if (z.seedUV.x < 0f) continue;

                float sx = previewRect.x + z.seedUV.x * previewRect.width;
                float sy = previewRect.y + (1f - z.seedUV.y) * previewRect.height;

                const float armLen = 7f;
                const float thickness = 2f;
                var color = new Color(1f, 0.85f, 0f, 0.9f);
                EditorGUI.DrawRect(new Rect(sx - armLen, sy - thickness * 0.5f, armLen * 2f, thickness), color);
                EditorGUI.DrawRect(new Rect(sx - thickness * 0.5f, sy - armLen, thickness, armLen * 2f), color);
            }
        }

        private void PaintAtScreenPos(Vector2 screenPos, Rect previewRect)
        {
            var maskView = _host._maskView;
            // ストローク開始時に1度だけ Unity Undo を登録する。
            maskView.BeginStroke();

            float u = (screenPos.x - previewRect.x) / previewRect.width;
            float v = 1f - (screenPos.y - previewRect.y) / previewRect.height;
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);

            var currentUV = new Vector2(u, v);

            if (maskView.lastPaintUV.x >= 0f)
            {
                float dist = Vector2.Distance(maskView.lastPaintUV, currentUV);
                maskView.EnsureMasks();
                float step = (maskView.maskWidth > 0) ? 1f / maskView.maskWidth : 0.001f;
                if (dist > step)
                {
                    int steps = Mathf.CeilToInt(dist / step);
                    for (int i = 1; i < steps; i++)
                    {
                        Vector2 lerped = Vector2.Lerp(maskView.lastPaintUV, currentUV, (float)i / steps);
                        maskView.PaintMask(lerped);
                    }
                }
            }

            maskView.PaintMask(currentUV);
            maskView.lastPaintUV = currentUV;
        }

        // ───────────────────────── Preview Async Generation ─────────────────────────

        private void GeneratePreviewAsync()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !EnsureTrueSource(sourceTexture)) return;

            int srcW = _trueSourceW;
            int srcH = _trueSourceH;

            float scale = 1f;
            if (srcW > CamereoConsts.Preview.MaxSize || srcH > CamereoConsts.Preview.MaxSize)
                scale = CamereoConsts.Preview.MaxSize / (float)Mathf.Max(srcW, srcH);
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
            float feather = session.edgeFeather;
            int aaCleanup = session.antiAliasCleanup;
            int hfPasses = session.holeFillPasses;
            int hfMinNeighbors = session.holeFillMinNeighbors;
            float rSatMin = session.relaxedSatMin;
            float rSatRamp = session.relaxedSatRamp;
            bool useDecontam = session.useDecontamination;
            int decontamRadius = session.decontaminationRadius;

            var srcPixelsForTask  = srcPixels;
            var rawDisplayForTask = rawDisplay;
            float scaleForTask = scale;
            int prevWForTask = prevW;
            int prevHForTask = prevH;

            // Debug capture: Code.Debug/ asmdef があり、かつ DebugView でトグル ON のときだけ
            // Factory が非 null インスタンスを返す。それ以外は null で、本体は何もキャプチャしない。
            IDebugCapture debugCap = DebugCaptureHooks.Factory?.Invoke();

            // 連続領域モードの keep をフル画像で解いて公開する(詳細プレビューが転写して一致させる)。
            // 詳細側は最新の公開キャッシュを寸法一致で参照する(メイン完了時に再走して収束)。
            var keepCache = new FloodFillKeepCache();

            _previewJob.Schedule(
                work: token =>
                {
                    Color32[] pixels = (Color32[])srcPixelsForTask.Clone();
                    PixelProcessor.ProcessPixelsArray(pixels, srcW, srcH, maskSnap, zonesSnapshot, feather, aaCleanup,
                        hfPasses, hfMinNeighbors, rSatMin, rSatRamp,
                        0, 0, 0, 0, token,
                        useDecontam, decontamRadius,
                        debug: debugCap, floodFillKeep: keepCache);

                    Color32[] processedDisplay = scaleForTask < 1f
                        ? PixelProcessor.BoxDownsample(pixels, srcW, srcH, prevWForTask, prevHForTask, scaleForTask)
                        : pixels;
                    // raw が未確定(キャッシュミス & scale<1)ならバックグラウンドで生成する。
                    // それ以外(キャッシュヒット or scale>=1)は確定済みをそのまま使う。
                    Color32[] rawForJob = rawDisplayForTask ?? PixelProcessor.BoxDownsample(
                        srcPixelsForTask, srcW, srcH, prevWForTask, prevHForTask, scaleForTask);
                    return (processedDisplay, rawForJob);
                },
                apply: result =>
                {
                    _pendingRawDisplay       = result.raw;
                    _pendingProcessedDisplay = result.processed;
                    _pendingPrevW            = prevWForTask;
                    _pendingPrevH            = prevHForTask;
                    // フル画像で解いた keep を公開(以降は不変として詳細プレビューが参照)。
                    _host.floodFillKeepCache = keepCache;
                    // ジョブ側で生成した raw をキャッシュへ確定する(まだ未確定で、対象テクスチャと
                    // 寸法が変わっていない場合のみ。新しいミスで上書きされていれば触らない)。
                    if (_cachedRawDisplay == null && _cachedSrcPixels == srcPixelsForTask &&
                        _cachedSrcW == srcW && _cachedSrcH == srcH &&
                        _cachedPrevW == prevWForTask && _cachedPrevH == prevHForTask)
                    {
                        _cachedRawDisplay = result.raw;
                    }
                    _host.LatestDebugCapture = debugCap;
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
