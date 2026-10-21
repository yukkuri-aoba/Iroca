// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    public partial class IrocaWindow
    {
        // 加工設定（エッジぼかし・AA クリーンアップ・境界クリーンアップ）は既定値のまま使う項目なので、
        // 既定で畳む（2026-10-04）。開いた/閉じた状態はウィンドウごとに保存される。
        [SerializeField] private bool processingFoldout = false;
        // 加工設定の内部パラメータ（穴埋め・境界復元・α分解半径）の折りたたみ。既定は閉。
        // 旧「上級モード」で出し分けていた項目をここへ移した（DrawProcessingSection のコメント参照）。
        [SerializeField] private bool processingDetailFoldout;

        private Vector2 scrollPos;
        private Vector2 leftScrollPos;
        // プレビュー側の縦オーバーフロー用。プレビュー枠はカラム高に収まるよう動的に縮む
        // (PreviewView.availableColumnHeight)が、下限(MinViewportHeight)まで縮んでも
        // 収まらない低ウィンドウではあふれるため、外側にも ScrollView を挟んで逃がす。
        private Vector2 rightScrollPos;

        // 横並びモードのテクスチャフィールド込み上部（toolbar＋テクスチャフィールド）の実高を
        // Repaint 時に実測しキャッシュする。WorkflowHint/ReadWriteError の HelpBox は可変高で
        // 固定見積もりに入らないため、前フレーム Repaint の実測値を今フレームの horizH 算出に使う
        // （Layout/Repaint で同一値→GUILayout 整合）。0（未計測）時は決定論フォールバックを使う。
        // PreviewView._viewportWidth と同方針。
        [System.NonSerialized] private float _sideBySideTopHeight;

        // ジョブオーバーレイ（進捗バー＋キャンセル）の実測高。オーバーレイは DisabledScope の外で
        // ウィンドウ下端に重ねて描くので、この高さぶん上から置かないとキャンセルボタンが下端で切れて
        // 押せなくなる（ジョブ中の唯一の脱出口が消える）。
        // _sideBySideTopHeight と同方針で前フレーム Repaint の実測値を使う（定数で見積もると
        // オーバーレイの中身を変えたときに黙ってずれる）。0（未計測）時は決定論フォールバック。
        [System.NonSerialized] private float _jobOverlayHeight;

        /// <summary>
        /// ジョブ実行中で UI 操作を止めるべきか。エクスポート中と自動調整中（証拠待ちを含む）は
        /// 操作を受け付けない。
        /// 別ウィンドウへ切り出したプレビュー(IrocaPreviewWindow)も同じ条件で無効化し、
        /// 本体が止まっている間にプレビュー上のペイント/スポイトだけ通ってしまうのを防ぐ。
        /// </summary>
        internal bool IsJobBlockingUI =>
            (_exportView != null && _exportView.IsExporting)
            || _autoTuneJob.IsRunning
            // 証拠（AI 提案）待ちも自動調整の一部（解析はまだ始まっていないが
            // 途中でゾーンを触られると待ち中の入力と食い違う）。
            || _evidenceWaiting;

        private void OnGUI()
        {
            // 描かれている = 見えている(Scene のクリックを見る条件。IrocaWindow.SceneClick)。
            _windowShown = true;
            // 入力があったら、別ウィンドウ(プレビュー・マスク編集)に描き直しを知らせる(DrawStamp)。
            if (MayChangeState(Event.current.type)) BumpUiState();
            // Ctrl+Z / Ctrl+Y は Unity 標準 Undo に統合済みのため、独自処理は不要。
            ProcessPendingZoneChanges();
            PublishLivePreviewTarget();

            // スポイト武装中にテクスチャが外れた／対象ゾーンが消えたら解除（クリックで解けなくなるのを防ぐ）。
            // 「スポイトで変えたい色を選ぶ」の武装（NewZoneEyedropperId）はまだ無いゾーンを指すので、
            // ゾーンの有無では解除しない（解除すると押した次の描画で武装が消え、トグルにならなかった）。
            if (!string.IsNullOrEmpty(_eyedropperZoneId) &&
                (sourceTexture == null
                 || (_eyedropperZoneId != NewZoneEyedropperId && FindZoneById(_eyedropperZoneId) == null)))
                _eyedropperZoneId = null;
            // シード指定の武装も同じ理由で掃除する（武装したまま対象が消えると、プレビュー上の
            // クリックが行き先の無い状態で待ち続ける）。
            if (!string.IsNullOrEmpty(_seedPickZoneId) &&
                (sourceTexture == null || FindZoneById(_seedPickZoneId) == null))
                _seedPickZoneId = null;
            // ソロ表示の対象が消えた／テクスチャが外れたら通常表示へ戻す。残すと「ゾーンを
            // 消したのにプレビューが元に戻らない」ように見える。
            if (!string.IsNullOrEmpty(_soloZoneId) &&
                (sourceTexture == null || FindZoneById(_soloZoneId) == null))
            {
                SetSoloZone(null);
            }

            // ジョブ実行中はウィンドウ内 UI を全て無効化する。
            // ただしジョブのオーバーレイ（進捗バー＋キャンセル）は DisabledScope の外で
            // 描画し、キャンセルだけは押せるようにする。
            // 対象のジョブはエクスポートと自動調整（証拠待ちを含む）。
            bool blocking = IsJobBlockingUI;

            // ヘッダーは DisabledScope の外に置く（言語切替・クレジットはジョブ中でも安全）。
            // ただしリセットはセッション状態を破壊的に書き換えるので、ジョブ完了時の apply と
            // 競合しないよう blocking を渡してヘッダー内で個別に無効化する。
            DrawHeader(blocking);

            EditorGUI.BeginDisabledGroup(blocking);

            bool sideBySide = position.width >= IrocaConsts.Layout.SideBySideMinWidth;

            // position.height はウィンドウ枠（タイトル/タブバー）を含むため、
            // 実描画領域はそれより低い。エクスポートが画面外に押し出されないよう安全マージンを引く。
            // ジョブのオーバーレイの高さはここで引かない。引くとジョブの開始・終了のたびに
            // プレビューが縮んで下の欄が上下に跳ねるので、オーバーレイは下端に重ねて描く。
            float availableContentH = position.height - IrocaConsts.Layout.WindowChromeMargin;
            float exportH = _exportView.GetSectionHeight();

            if (sideBySide)
                DrawSideBySideLayout(availableContentH, exportH);
            else
                DrawVerticalLayout(availableContentH, exportH);

            EditorGUI.EndDisabledGroup();

            // DisabledScope の外でジョブオーバーレイ（進捗バー＋キャンセル）を描画。
            // ウィンドウ全体が無効化されていてもキャンセルだけは押せる。
            // 本体レイアウトの後ろに積まず、ウィンドウ下端の固定位置に重ねる（下に隠れる欄はジョブ中は押せない）。
            if (blocking)
            {
                float overlayH = JobOverlayReserve();
                var area = new Rect(0f, Mathf.Max(0f, position.height - overlayH - 2f), position.width, overlayH + 2f);
                EditorGUI.DrawRect(area, IrocaColors.WindowBackground);
                GUILayout.BeginArea(area);
                var overlayRect = EditorGUILayout.BeginVertical();
                DrawJobOverlay();
                EditorGUILayout.EndVertical();
                GUILayout.EndArea();

                // 実測値を次フレームの高さに使う。値が変わったら追い再描画を 1 回要求する
                // （高さはオーバーレイ自身の中身では変わらないので収束する）。
                if (Event.current.type == EventType.Repaint && overlayRect.height > 1f
                    && Mathf.Abs(overlayRect.height - _jobOverlayHeight) > 0.5f)
                {
                    _jobOverlayHeight = overlayRect.height;
                    Repaint();
                }
                // 進捗バーを次フレームで更新するため、ジョブ中は継続的に再描画を要求する。
                Repaint();
            }
            else
            {
                _jobOverlayHeight = 0f;
            }
        }

        // ジョブオーバーレイ 1 行（DrawJobProgressRow）の進捗バーとキャンセルボタンの高さ。
        internal const float JobProgressBarHeight = 18f;
        internal const float JobCancelButtonHeight = 22f;

        /// <summary>
        /// ジョブオーバーレイの高さ（ウィンドウ下端からこの高さの帯に描く）。
        /// 実測値があればそれを、未計測の初回フレームだけ決定論フォールバックを返す。
        /// </summary>
        private float JobOverlayReserve()
        {
            if (_jobOverlayHeight > 1f) return _jobOverlayHeight;
            // 初回フレーム用の見積もり: DrawJobProgressRow 1 行ぶん（Space + 進捗バー + キャンセル + Space）＋行間。
            return 2f + JobProgressBarHeight + JobCancelButtonHeight + 2f + EditorGUIUtility.standardVerticalSpacing * 3f;
        }

        // 設定列のプレフィックスラベル幅。既定(150)のままだと狭いカラムでは
        // 「ラベル150＋スライダー最小幅＋数値フィールド」等の行最小幅がカラム幅を超え、
        // 横スクロールバーを無効化している設定列では超過分が右端で切れて
        // プレビューの下に隠れて見える。カラム幅に比例させ、行がカラム内に収まるようにする。
        // 下限 95 はラベルが読める最低限、上限 150 は Unity 既定（広いカラムでは従来どおり）。
        //
        // ただし比率だけで決めると、狭いカラムでは最長ラベルが入りきらず末尾が欠ける
        // （実キャプチャで JA「サンプル自動補正（再着色）」が閉じ括弧ごと切れ、EN は
        // "Shadow Forgiveness Sat Min" など複数が欠けていた）。設定行のラベルを実測し、
        // その幅までは広げる。広げすぎるとスライダーが潰れるので、スライダー＋数値欄に
        // 最低限（SettingsFieldReserve）を残す範囲に限る。
        private static float SettingsLabelWidth(float contentWidth)
        {
            float byRatio = Mathf.Clamp(contentWidth * 0.45f, 95f, 150f);
            float fit = Mathf.Min(MeasuredSettingsLabelWidth(), contentWidth - SettingsFieldReserve);
            return Mathf.Max(byRatio, fit);
        }

        // ラベルを広げてもスライダー(~60)＋数値欄(50)＋カード余白が残るようにする幅。
        private const float SettingsFieldReserve = 125f;

        // 設定列の実内容幅（カラム幅 − 縦スクロールバー）。行ごとにラベル幅を詰める判断に使う。
        [System.NonSerialized] private float _settingsContentWidth;

        private static LanguageMode s_labelWidthLang = (LanguageMode)(-1);
        private static float s_measuredLabelWidth;

        // 設定列（ゾーンカード・加工設定）のプレフィックスラベルの最大実測幅。
        // 文字列は Localization 由来なので言語切替時だけ測り直す。
        private static float MeasuredSettingsLabelWidth()
        {
            if (s_labelWidthLang == Localization.CurrentLanguage && s_measuredLabelWidth > 0f)
                return s_measuredLabelWidth;
            s_labelWidthLang = Localization.CurrentLanguage;

            var style = EditorStyles.label;
            var tmp = new GUIContent();
            float max = 0f;
            void Measure(float indent, params string[] labels)
            {
                foreach (var s in labels)
                {
                    tmp.text = s;
                    max = Mathf.Max(max, style.CalcSize(tmp).x + indent);
                }
            }
            Measure(0f,
                Localization.SampleColor, Localization.TargetColor, Localization.Tolerance,
                Localization.UseFloodFill, Localization.PatternPreserve, Localization.OutputSaturation,
                Localization.EdgeSoftness, Localization.SaturationStrictness, Localization.SaturationGuard,
                Localization.HighlightRecovery, Localization.ApplyHighlightWash,
                Localization.ShadowDesaturation, Localization.ShadowForgivenessSatMin,
                Localization.ShadowValueFloor, Localization.PartSatCeiling, Localization.PartHueBand, Localization.ChromaThreshold, Localization.ChromaCeiling,
                Localization.HoleFillMargin,
                Localization.AutoRecolorAnchor, Localization.ValueWeight, Localization.SatDistWeight,
                Localization.SatRampScale, Localization.EdgeFeather, Localization.AntiAliasCleanup,
                Localization.UseDecontamination);
            // IndentLevelScope 内の行は、インデント 1 段（15px）ぶんラベル領域が狭い。
            Measure(15f,
                Localization.FloodFillSeedPoint, Localization.HighlightBandExpand,
                Localization.AutoHighlightSample, Localization.HoleFillPasses,
                Localization.HoleFillMinNeighbors, Localization.RelaxedSatMin,
                Localization.DecontaminationRadius);

            // +4: ラベルとフィールドの間に詰まって見えない程度の余白。
            s_measuredLabelWidth = max + 4f;
            return s_measuredLabelWidth;
        }

        // 左カラム（横並び）/ 上部スクロール（縦並び）共通の設定スタック。
        // 横並び・縦並び双方から呼ぶことで描画の重複を避ける。
        // 呼び出し側の BeginChangeCheck/EndChangeCheck に挟まれて previewDirty 判定に使われる。
        // マスク UI(_maskView.Draw)はここに含めない: ブラシサイズ・ペイントモード切替・foldout 開閉など
        // マスク内容と無関係な操作でも previewDirty が立ち 4K フル再生成が走ってしまうため。マスク内容の
        // 変更はストローク時に maskDirty 経由で別途プレビュー再生成をトリガするので、Draw は呼び出し側が
        // ChangeCheck の外で行う。
        private void DrawLeftColumnSettings()
        {
            DrawZoneList();
            DrawProcessingSection();
        }

        private void DrawSideBySideLayout(float availableContentH, float exportH)
        {
            // 変更の検出はテクスチャ欄と設定群だけを囲み、間にある設定列の BeginScrollView は外す。
            // スクロールバーは BeginScrollView の中で描かれ、つまんで動かすと GUI.changed が立つ。
            // 1 つの ChangeCheck で通して囲んでいた頃は、設定列をスクロールするだけでプレビューの
            // 再生成(ドラッグ中はプロキシの追従)が走り続けていた。縦並びレイアウトは
            // BeginScrollView の後で ChangeCheck を始めているので元から起きない。
            EditorGUI.BeginChangeCheck();
            DrawTextureField();
            bool textureFieldChanged = EditorGUI.EndChangeCheck();

            // エクスポートセクションを常にウィンドウ下部に表示するため、
            // 横並び領域の高さを「描画領域高 - ヘッダー/テクスチャフィールド - エクスポート高」に制限する。
            //
            // 上部（toolbar＋テクスチャフィールド）の高さは固定では見積もれない。テクスチャ未設定時の
            // WorkflowHint や ReadWrite 不可時のエラー HelpBox が可変高で挿入され、固定見積もりだと
            // horizH が過大になりエクスポートが画面外へはみ出すため。DrawTextureField 直後の
            // GetLastRect().yMax はウィンドウ最上部(y=0)からの絶対値＝上部全体高そのものなので、
            // それを Repaint 時に実測してキャッシュし、horizH 算出に使う。
            if (Event.current.type == EventType.Repaint)
            {
                float measured = GUILayoutUtility.GetLastRect().yMax;
                // 値が変わったら追い再描画を 1 回要求する。エディタウィンドウは要求が無い限り
                // 再描画されないため、これが無いと HelpBox の出入り等で上部高が変わった操作の
                // 最終フレームが旧値の horizH のまま画面に固定される(PreviewView の chrome
                // 実測と同方針)。上部高は horizH に依存しないため 1 回で収束しループしない。
                if (measured > 1f && Mathf.Abs(measured - _sideBySideTopHeight) > 0.5f)
                {
                    _sideBySideTopHeight = measured;
                    Repaint();
                }
            }
            // 未計測の初回フレームのみ決定論フォールバック（テクスチャ設定済み相当の見積もり）。
            float fallbackTopH = EditorStyles.toolbar.fixedHeight
                + 4f + (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) * 2 + 4f;
            float topOverheadH = _sideBySideTopHeight > 1f ? _sideBySideTopHeight : fallbackTopH;
            float horizH = Mathf.Max(
                IrocaConsts.Layout.MiddleAreaMinHeight,
                availableContentH - topOverheadH - exportH);

            EditorGUILayout.BeginHorizontal(GUILayout.Height(horizH));

            float leftWidth = Mathf.Clamp(
                position.width * IrocaConsts.Layout.LeftColumnRatio,
                IrocaConsts.Layout.LeftColumnMin,
                IrocaConsts.Layout.LeftColumnMax);
            // 等倍(100%)プレビューがバー無しで収まる幅(PreviewColumnReserve)を右カラムへ優先確保し、
            // 左カラムは下限(LeftColumnMin)までの範囲で譲る。現行の定数(比率 0.4・下限 320・
            // 上限 450・予約 408)では補正の前後で幅は変わらない(幅 728 未満は下限 320 が勝ち、
            // 728 以上は Clamp 済みの幅のままで予約が残る)。Preview.MaxSize や比率を変えたときの
            // 保険として残す。下限は既に狭いウィンドウで常用される幅なので設定 UI は崩れない。
            leftWidth = Mathf.Max(IrocaConsts.Layout.LeftColumnMin,
                Mathf.Min(leftWidth, position.width - IrocaConsts.Layout.PreviewColumnReserve));
            EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
            // 縦バーを常時確保し、横バーは無効化する。簡易オーバーロードは縦バーが内容高で
            // 出入り(トグル)し、その都度コンテンツ幅が ~13px 変わって設定UIが左右にガクつく
            // (プレビュー生成で上部高/列高がわずかに揺れると境界付近でトグルしやすい)。
            // 常時確保すれば内容の有無に関わらず横位置が一定になる。
            leftScrollPos = EditorGUILayout.BeginScrollView(leftScrollPos,
                false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.ExpandHeight(true));

            // ただし「常時表示」はバーを描くだけで、レイアウト上の幅は確保しない(2022.3 で
            // Layout 中の GUIScrollGroup.needsVerticalScrollbar が false のままなのを実測)。
            // Unity が内容幅からバー幅を引くのは内容が列からあふれたときだけで、収まっている
            // ときは列の全幅で並べ、その右端 13px に縦バーが重なって数値欄・ボタンの文字が
            // 隠れていた。内容をバーの手前までの幅に固定し、あふれの有無に関わらず同じ幅にする。
            // 幅は切り捨てる: 列の矩形は整数に丸められるので、端数ぶん内容が広いと横あふれ扱いになる。
            var vScrollbar = GUI.skin.verticalScrollbar;
            _settingsContentWidth = Mathf.Floor(leftWidth) - vScrollbar.fixedWidth - vScrollbar.margin.left;
            EditorGUILayout.BeginVertical(GUILayout.Width(_settingsContentWidth));

            // 設定列の実内容幅に合わせてラベル幅を縮め、行の右端（数値フィールド・ボタン）が
            // 切れないようにする。
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = SettingsLabelWidth(_settingsContentWidth);

            EditorGUI.BeginChangeCheck();
            DrawLeftColumnSettings();

            if (EditorGUI.EndChangeCheck() || textureFieldChanged)
            {
                MarkPreviewDirty();
            }

            // マスク UI は ChangeCheck の外・スクロール領域内で描画する(DrawLeftColumnSettings のコメント参照)。
            _maskView.Draw();

            _presetsView.Draw();

            // パイプライン透明化（Debug View）の描画フック。
            // Code/Debug/ asmdef がない or 未登録なら subscriber がいないので何も描画されない。
            // 左カラムのスクロール領域内に置くことで、エクスポートのピン留めと
            // 競合せず、スクロールで到達できるようにする。
            DebugCaptureHooks.RaiseDrawFoldout(this);

            EditorGUIUtility.labelWidth = prevLabelWidth;
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // ExpandHeight な ScrollView で囲うことで、プレビューが
            // 横並びセクション高（horizH）を超えても列内でスクロールするようになり、
            // 下部のエクスポートセクションを押し出さない。
            float rightWidth = Mathf.Max(0f, position.width - leftWidth);
            EditorGUILayout.BeginVertical();
            // 横バーは描かない(GUIStyle.none)。この外側 ScrollView は縦オーバーフロー専用で、
            // 横スクロールは内側プレビューに任せる。ただし GUIStyle.none は「描かない・幅0」で
            // あってレイアウト上の横スクロールを禁止はしないので、カラム幅より最小幅の大きい子
            // (狭幅時の操作行など)があると、この ScrollView はカラムより広いクライアント幅を
            // 子へ配る。プレビュー枠が一緒に広がらないよう、枠幅は availableColumnWidth 経由で
            // 明示的に固定する(PreviewView 側 frameW のコメント参照)。
            rightScrollPos = EditorGUILayout.BeginScrollView(rightScrollPos,
                false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.ExpandHeight(true));
            // 別ウィンドウへ切り出している間は本体では描かない(同一 PreviewView の二重
            // レイアウトを避ける。理由は IrocaPreviewWindow のクラスコメント)。
            if (IrocaPreviewWindow.IsOpen)
            {
                DrawPreviewDetachedSection(canReattach: true);
            }
            else
            {
                // プレビュー枠が右カラム高に収まるよう動的に縮むためのカラム高と、
                // 枠幅を固定するためのカラム幅を渡す。
                _previewView.availableColumnHeight = horizH;
                _previewView.availableColumnWidth = rightWidth;
                _previewView.Draw();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // 一括適用は実装継続中のため当面 UI から非表示。
            // _exportView.DrawBatchSection();
            _exportView.DrawExportSection();
        }

        private void DrawVerticalLayout(float availableContentH, float exportH)
        {
            // エクスポートを常にウィンドウ下部に表示するため、上部だけをスクロール領域にする。
            float toolbarH = EditorStyles.toolbar.fixedHeight + 4f;
            float topScrollH = Mathf.Max(
                IrocaConsts.Layout.MiddleAreaMinHeight,
                availableContentH - toolbarH - exportH);

            // 横バーは無効化(GUIStyle.none)。この外側 ScrollView は縦スクロール専用で、
            // 横スクロールは内側プレビューに任せる（横並びレイアウトと同じ理由）。
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos,
                false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                GUILayout.Height(topScrollH));

            // 縦並び＝狭いウィンドウなので、設定行が右端で切れないよう
            // ラベル幅を内容幅（ウィンドウ幅 − 縦スクロールバー）に追従させる。
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            _settingsContentWidth = position.width - GUI.skin.verticalScrollbar.fixedWidth;
            EditorGUIUtility.labelWidth = SettingsLabelWidth(_settingsContentWidth);

            EditorGUI.BeginChangeCheck();

            DrawTextureField();
            DrawLeftColumnSettings();

            if (EditorGUI.EndChangeCheck())
            {
                MarkPreviewDirty();
            }

            // マスク UI は ChangeCheck の外・スクロール領域内で描画する(DrawLeftColumnSettings のコメント参照)。
            _maskView.Draw();

            _presetsView.Draw();

            // 縦並び(＝狭幅)ではプレビューを本体に描かない。設定列の下へ積まれると、
            // プレビューに割ける高さも幅も足りず検分に使えないため、別ウィンドウへ
            // 切り出す導線だけを置く。ウィンドウを広げれば従来どおり本体内に戻る。
            DrawPreviewDetachedSection(canReattach: false);

            // パイプライン透明化（Debug View）の描画フック。
            // Code/Debug/ asmdef がない or 未登録なら subscriber がいないので何も描画されない。
            // メインスクロール領域内に置くことで、エクスポートのピン留めと
            // 競合せず、スクロールで到達できるようにする。
            DebugCaptureHooks.RaiseDrawFoldout(this);

            EditorGUIUtility.labelWidth = prevLabelWidth;
            EditorGUILayout.EndScrollView();

            // 一括適用は実装継続中のため当面 UI から非表示。
            // _exportView.DrawBatchSection();
            _exportView.DrawExportSection();
        }

        // 縦並び(狭幅)では常にここを描き、横並びでは別ウィンドウが開いている間だけ描く。
        // canReattach=false(縦並び)では「本体に戻す」を出さない。戻しても設定列の下に
        // 押し出されて実用にならず、押した直後にまたこの案内へ戻るだけになるため。
        private void DrawPreviewDetachedSection(bool canReattach)
        {
            EditorGUILayout.LabelField(
                Localization.StepPrefixPreview + Localization.Preview, EditorStyles.boldLabel);

            bool open = IrocaPreviewWindow.IsOpen;
            EditorGUILayout.HelpBox(
                open ? Localization.PreviewDetachedActive : Localization.PreviewDetachedNarrowHint,
                MessageType.Info);

            if (GUILayout.Button(new GUIContent(
                    open ? Localization.FocusPreviewWindow : Localization.OpenPreviewWindow,
                    Localization.OpenPreviewWindowTooltip)))
            {
                if (open) IrocaPreviewWindow.FocusIfOpen();
                else IrocaPreviewWindow.Open(this);
            }

            if (open && canReattach &&
                GUILayout.Button(new GUIContent(
                    Localization.ReattachPreview, Localization.ReattachPreviewTooltip)))
            {
                IrocaPreviewWindow.CloseIfOpen();
            }

            EditorGUILayout.Space(4);
        }

        private void DrawJobOverlay()
        {
            _exportView?.DrawJobOverlay();

            if (_evidenceWaiting)
            {
                // 解析開始前の証拠（AI 提案セグメント）待ち。無言だと押しても無反応に見えるので、
                // 解析中と同じ場所に何を待っているかを出す。中止は解析を始めない。
                if (DrawJobProgressRow(_autoTuneProgress.Value,
                    $"{Localization.AutoTune}  {(_evidencePending ? Localization.AutoTuneEvidencePreparing : Localization.AutoTuneEvidenceFetching)}"))
                {
                    CancelEvidenceWait();
                }
            }
            else if (_autoTuneJob.IsRunning)
            {
                float pct = _autoTuneProgress.Value;
                if (DrawJobProgressRow(pct, $"{Localization.AutoTune}  {Mathf.RoundToInt(pct * 100f)}%"))
                {
                    _autoTuneJob.Cancel();
                }
            }
        }

        /// <summary>
        /// 固定幅のボタンの幅。文言（状態で切り替わるものは全部）が既定のボタンの書体で入りきる幅と
        /// minWidth の大きい方を返す。言語で長さが変わる文言を固定値の幅に入れると、片方の言語で末尾が欠ける。
        /// OnGUI の中で呼ぶ。
        /// </summary>
        internal static float FitButtonWidth(float minWidth, params string[] texts)
        {
            float w = minWidth;
            var style = GUI.skin.button;
            foreach (var t in texts)
                w = Mathf.Max(w, Mathf.Ceil(style.CalcSize(new GUIContent(t)).x));
            return w;
        }

        /// <summary>
        /// ジョブオーバーレイの 1 行（進捗バー＋キャンセルボタン）を描画し、キャンセルが押されたら true を返す。
        /// 書き出し・自動調整の解析中・証拠待ちで共通。キャンセル処理は呼び出し側が行う
        /// （どの処理も GUILayout を呼ばないので、後ろの Space の後で行ってもコントロール列は変わらない）。
        /// </summary>
        internal static bool DrawJobProgressRow(float progress, string label)
        {
            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, JobProgressBarHeight);
            EditorGUI.ProgressBar(rect, progress, label);
            bool cancel = GUILayout.Button(new GUIContent(Localization.Cancel, Localization.CancelActionTooltip), GUILayout.Height(JobCancelButtonHeight));
            EditorGUILayout.Space(2);
            return cancel;
        }

        /// <param name="jobBlocking">
        /// ジョブ実行中か。ヘッダー自体は DisabledScope の外に置く（言語切替・クレジットは
        /// ジョブ中でも安全で、むしろ待ち時間に触れて困らない）が、セッション状態を破壊的に
        /// 書き換えるリセットだけはここで無効化する。
        /// </param>
        private void DrawHeader(bool jobBlocking)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var labels = new[]
            {
                new GUIContent(Localization.LangAuto, Localization.LanguageToolbarTooltip),
                new GUIContent(Localization.LangJapanese, Localization.LanguageToolbarTooltip),
                new GUIContent(Localization.LangEnglish, Localization.LanguageToolbarTooltip),
            };
            int current = (int)Localization.CurrentLanguage;
            int next = GUILayout.Toolbar(current, labels, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            if (next != current)
            {
                Localization.CurrentLanguage = (LanguageMode)next;
                Localization.SaveLanguagePreference();
                Repaint();
            }

            GUILayout.FlexibleSpace();

            // 現在のテクスチャの編集内容（ゾーン・色・処理設定・マスク）を初期状態へ戻す逃げ道。
            // 確認ダイアログを挟み、Undo 登録するので誤操作しても「元に戻す」で復元できる。
            // ジョブ実行中は無効化する: エクスポート/自動調整の完了時 apply がリセット後の
            // セッションへ書き戻し、状態不整合を生み得るため。
            using (new EditorGUI.DisabledScope(sourceTexture == null || jobBlocking))
            {
                if (GUILayout.Button(new GUIContent(Localization.ResetSession, Localization.ResetSessionTooltip),
                        EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog(
                            Localization.Confirm, Localization.ResetSessionConfirm, Localization.OK, Localization.Cancel))
                        ResetCurrentSession();
                }
            }

            if (GUILayout.Button(new GUIContent(Localization.Manual, Localization.ManualTooltip), EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                IrocaManual.Open();

            if (GUILayout.Button(new GUIContent(Localization.Credit, Localization.CreditTooltip), EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
            {
                if (EditorUtility.DisplayDialog(Localization.CreditTitle, Localization.CreditBody,
                        Localization.CreditOpenList, Localization.CreditClose))
                    Application.OpenURL(Localization.CreditsUrl);
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 元テクスチャが選ばれ、かつ画素を読める状態か（＝手順①が済んでいるか）。
        /// </summary>
        private bool StepTextureDone => CanReadSource(sourceTexture);

        /// <summary>
        /// 有効なゾーンが 1 つ以上あるか（無ければ「スポイトで変えたい色を選ぶ」から始める）。
        /// シーンへのライブプレビュー（PublishLivePreviewTarget）の可否にも使う。案内の都合で条件を
        /// 変えるときは、そちらへの影響も確認すること。
        /// </summary>
        private bool HasEnabledZone
        {
            get
            {
                var list = _session?.zones;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].enabled) return true;
                return false;
            }
        }

        /// <summary>
        /// 色替えの指定が済んでいるか（＝手順②が済んでいるか）。
        /// 有効なゾーンのどれかで、サンプルカラーと変更先カラーの両方が指定済みであること。
        /// 変更先が既定の白のままだと、選んだ所が白っぽくなるだけで色替えは済んでいない
        /// （以前はサンプルカラーだけで ✓ を付け、案内もそこで終わっていた）。
        /// プレビューの右クリックの案内（PreviewView.DrawIdleRow）を出し始める条件にも使う。
        /// </summary>
        internal bool StepZonesDone
        {
            get
            {
                var list = _session?.zones;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    var z = list[i];
                    if (z != null && z.enabled && z.HasSampleColor && z.HasTargetColor) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 「次に何をすればよいか」の 1 行案内。色を選ぶ → 変更先カラーを選ぶ → 自動調整（任意）の順に進み、
        /// 済んだら消える（常設の帯でプレビューの高さを奪い続けないため）。
        /// 自動調整は変更先の明るさも入力に取る（模様保持の導出）ので、変更先の後に案内する。
        /// null なら何も描かない。
        /// </summary>
        // DrawTextureField が描いている案内の文面（NextStepHint の結果を Layout ごとに取り直す）。
        [System.NonSerialized] private string _nextStepHint;

        private string NextStepHint()
        {
            if (!StepTextureDone) return null;               // テクスチャ欄の HelpBox が案内済み
            if (!HasEnabledZone) return Localization.NextStepAddZone;

            // 手順の途中のゾーンを、最後に足したもの（リストの下）から探す。2 色目を足したときに、
            // そのゾーンの次の一手を案内するため。
            var list = _session.zones;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var z = list[i];
                if (z == null || !z.enabled) continue;
                string name = string.IsNullOrEmpty(z.name) ? Localization.UnnamedZone : z.name;
                if (!z.HasSampleColor) return string.Format(Localization.NextStepPickColorFormat, name);
                if (!z.HasTargetColor) return string.Format(Localization.NextStepPickTargetFormat, name);
                if (IsUntunedNewZone(z)) return string.Format(Localization.NextStepAutoTuneFormat, name);
            }
            return null;
        }

        /// <summary>
        /// UI で作ったままの範囲設定か（自動調整も許容範囲の手直しもしていない）。
        /// 自動調整はこのウィンドウで走らせれば由来が残る。ウィンドウを開き直した後は由来が無いので、
        /// 自動調整が書くもの（内部サンプル・初期値以外の許容範囲）で見分ける。
        /// </summary>
        internal bool IsUntunedNewZone(ColorZone z)
        {
            if (!string.IsNullOrEmpty(AutoTuneProvenance(z.id))) return false;
            if (z.extraSamples != null && z.extraSamples.Count > 0) return false;
            return Mathf.Approximately(z.tolerance, NewZoneInitialTolerance);
        }

        // 英語表示の AI 翻訳のお知らせを出すか。Layout のときに決め、同じフレームの Repaint もそれで描く
        // (ヘッダーで言語を切り替えたフレームに、Layout と Repaint で並ぶ要素が食い違わないように)。
        [System.NonSerialized] private bool _showTranslationNotice;

        // 英語表示の AI 翻訳のお知らせ(閉じるまで出す。Localization.ShowEnglishTranslationNotice)。
        // エディタを止めるダイアログはやめ、ウィンドウの上部に閉じられる帯で出す。
        private void DrawTranslationNotice()
        {
            if (Event.current.type == EventType.Layout)
                _showTranslationNotice = Localization.ShowEnglishTranslationNotice;
            if (!_showTranslationNotice) return;
            EditorGUILayout.HelpBox(Localization.EnglishTranslationNotice, MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            // テクスチャ欄は変更の検出(プレビューの作り直し)に囲まれているので、閉じるだけでは立てない。
            bool changed = GUI.changed;
            if (GUILayout.Button(new GUIContent(Localization.EnglishTranslationNoticeClose,
                    Localization.EnglishTranslationNoticeCloseTooltip), GUILayout.ExpandWidth(false)))
                Localization.CloseEnglishTranslationNotice();
            GUI.changed = changed;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTextureField()
        {
            EditorGUILayout.Space(4);
            DrawTranslationNotice();
            // 済んだ手順に ✓ を付ける。番号だけでは「どこまで進んだか」が分からず、
            // テクスチャを入れた瞬間に導入ヒントが消えて案内が途切れていた。
            EditorGUILayout.LabelField(
                Localization.StepPrefixTexture + Localization.SourceTexture
                + (StepTextureDone ? Localization.StepDoneMark : ""),
                EditorStyles.boldLabel);

            // 開始点が分かりにくいので、テクスチャ未設定時だけ一連の流れを案内する。
            if (sourceTexture == null)
                EditorGUILayout.HelpBox(Localization.WorkflowHint, MessageType.Info);

            var newTex = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent(Localization.Texture, Localization.TextureTooltip),
                sourceTexture, typeof(Texture2D), false,
                // 1 行高に固定する。Texture2D の ObjectField は既定で 64px のサムネイル枠になり、
                // 右端の小さな枠のために行の左側が 3 行ぶん空いて、下の設定列とプレビューの
                // 高さを恒常的に奪っていた。絵はプレビューで見えるのでここでは名前だけで足りる。
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (newTex != sourceTexture)
                SwitchSourceTexture(newTex);

            // Read/Write を求めるのは、原本ファイルからも画素を取れないときだけ（PSD/TGA 等）。
            // PNG/JPG は原本を直接読めるので、インポート設定を書き換える必要はない。
            if (sourceTexture != null && !CanReadSource(sourceTexture))
            {
                EditorGUILayout.HelpBox(Localization.ReadWriteError, MessageType.Error);

                if (GUILayout.Button(new GUIContent(Localization.EnableReadWrite, Localization.EnableReadWriteTooltip)))
                {
                    EnableReadWrite(sourceTexture);
                }
            }

            DrawLargeSourceNotice();

            // 次の一手。HelpBox でなく miniLabel なのは、常設の帯がプレビュー列の
            // 高さを恒常的に奪わないようにするため。手順が済めば消える。
            // 折り返しあり: ゾーン名が入って長くなり、狭いウィンドウや英語表示では 1 行に収まらない
            // （EditorGUILayout.LabelField は 1 行固定高で、折り返した 2 行目が隠れる）。
            // 上部の高さは実測しているので（_sideBySideTopHeight）、行数が変わってもレイアウトは崩れない。
            // ドラッグ中は前の文面のまま据え置く。許容範囲のスライダーを動かした瞬間に案内が消えると、
            // つまんでいるスライダーごと設定列が上へずれる。文面は Layout のときだけ決め、同じフレームの
            // Repaint と行数を揃える。
            if (Event.current.type == EventType.Layout && GUIUtility.hotControl == 0)
                _nextStepHint = NextStepHint();
            string next = _nextStepHint;
            if (!string.IsNullOrEmpty(next))
                GUILayout.Label(next, EditorStyles.wordWrappedMiniLabel);

            // AI(Sentis + モデル)の準備状況。未準備のときだけ非モーダルの帯で案内する
            // （起動時モーダルの置き換え。導入・ダウンロード・再起動の導線をここへ集約した）。
            MaskSuggestSection.DrawSetupBanner();

            // 新しい版のお知らせ。更新は VCC / ALCOM に任せ、ここでは知らせて手順へ案内するだけ。
            UpdateNotice.DrawBanner();

            EditorGUILayout.Space(4);
        }

        // 原本の画素数がこれ(8192²)を超えたら、メモリを多く使うことを案内する。
        private const long LargeSourcePixels = 8192L * 8192L;
        // 作業中の Unity が原本 1 画素あたりに使うメモリの目安(バイト)。2026-10-11 の実測で、16K の通し
        // (取り込み〜色替え〜書き出し〜NDMF)のピークが約 25GB、8K のプレビュー相当の繰り返しが約 6GB だった。
        private const double SourceBytesPerPixel = 96.0;
        private const string LargeSourceDismissedKeyPrefix = "Iroca.LargeSource.Dismissed:";

        /// <summary>
        /// 8K を超える元画像を開いたときの案内。「テクスチャとして書き出す」は元ファイルの解像度で処理するので、
        /// 編集を取り込み後の寸法で行っていても(<see cref="WorkingResolution"/>)、書き出すときはメモリを多く使う。
        /// 作業解像度も元ファイルのまま(Max Size が元以上・「元の解像度で編集」がオン)なら編集中から使う。
        /// 16K では PC の搭載量を超え得るため、使う量の目安と搭載量を並べて開いた時点で知らせる。
        /// 閉じると、そのテクスチャでは Unity を閉じるまで出さない(上部の高さを奪い続けないため)。
        /// </summary>
        private void DrawLargeSourceNotice()
        {
            if (sourceTexture == null || _previewView == null) return;
            if (!_previewView.TryGetTrueSourcePixels(sourceTexture, out _, out int ww, out int wh)) return;
            // 元ファイルの寸法。取り込み済みテクスチャから読んでいる(PSD など)ときは、書き出しもその寸法。
            int w = ww, h = wh;
            if (_previewView.TrueSourceFromFile) SourceFileSize(sourceTexture, ref w, ref h);
            if ((long)w * h <= LargeSourcePixels) return;
            string key = LargeSourceDismissedKeyPrefix + CurrentTexturePath(sourceTexture);
            if (SessionState.GetBool(key, false)) return;

            double needGB = (double)w * h * SourceBytesPerPixel / (1L << 30);
            // 搭載量は切り上げる。systemMemorySize は OS が使える量なので、32GB の PC でも 31.5GB 前後になる。
            double haveGB = System.Math.Ceiling(SystemInfo.systemMemorySize / 1024.0);
            string text;
            if ((long)ww * wh < (long)w * h)
                text = string.Format(Localization.LargeSourceExportNoticeFormat, w, h, ww, wh, needGB, haveGB);
            else if (WorkingResolution.AtSourceResolution)
                text = string.Format(Localization.LargeSourceAtSourceNoticeFormat, w, h, needGB, haveGB);
            else
                text = string.Format(Localization.LargeSourceNoticeFormat, w, h, needGB, haveGB);
            EditorGUILayout.HelpBox(text, MessageType.Warning);
            if (GUILayout.Button(new GUIContent(Localization.LargeSourceDismiss, Localization.LargeSourceDismissTooltip)))
                SessionState.SetBool(key, true);
        }

        // 元ファイルの寸法の覚え(案内は OnGUI ごとに通るので、取り込み設定を引くのはテクスチャか
        // 取り込み後の寸法が変わったときだけにする)。
        [System.NonSerialized] private Texture2D _fileSizeFor;
        [System.NonSerialized] private int _fileSizeImportW, _fileSizeImportH, _fileW, _fileH;

        // 元ファイルの寸法(取れなければ w, h はそのまま)。
        private void SourceFileSize(Texture2D tex, ref int w, ref int h)
        {
            if (_fileSizeFor != tex || _fileSizeImportW != tex.width || _fileSizeImportH != tex.height)
            {
                int fw = 0, fh = 0;
                string path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter ti)
                    ti.GetSourceTextureWidthAndHeight(out fw, out fh);
                _fileSizeFor = tex;
                _fileSizeImportW = tex.width;
                _fileSizeImportH = tex.height;
                _fileW = fw;
                _fileH = fh;
            }
            if (_fileW > 0 && _fileH > 0) { w = _fileW; h = _fileH; }
        }

        private void DrawProcessingSection()
        {
            processingFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(processingFoldout, Localization.Processing);
            if (!processingFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            edgeFeather = UndoHelper.Slider(this,
                new GUIContent(Localization.EdgeFeather, Localization.EdgeFeatherTooltip),
                edgeFeather, 0f, 5f);

            antiAliasCleanup = UndoHelper.IntSlider(this,
                new GUIContent(Localization.AntiAliasCleanup, Localization.AntiAliasCleanupTooltip),
                antiAliasCleanup, 0, 5);

            useDecontamination = UndoHelper.Toggle(this,
                new GUIContent(Localization.UseDecontamination, Localization.UseDecontaminationTooltip),
                useDecontamination);

            // 作業解像度(WorkingResolution)。この PC のいろか全体の設定なので、作業ごとには保存せず Undo にも
            // 載せない。ラベルは設定列の幅を決める計測の外に置く(ToggleLeft)。
            bool atSource = EditorGUILayout.ToggleLeft(
                new GUIContent(Localization.EditAtSourceResolution, Localization.EditAtSourceResolutionTooltip),
                WorkingResolution.AtSourceResolution);
            if (atSource != WorkingResolution.AtSourceResolution)
            {
                WorkingResolution.AtSourceResolution = atSource;
                InvalidateSourceAndRepaint();
            }

            // 内部パラメータ（穴埋め・境界復元・α分解半径）は折りたたみの中へ。
            //
            // 以前はゾーンリスト上部の「通常 / 上級」トグルで出し分けていたが、ゾーン側にも
            // 「詳細設定」の折りたたみがあり、同じ「どこまで見せるか」の軸を 2 つの操作で
            // 制御していた（さらにプリセットの「詳細」もあって三重）。モードトグルを廃し、
            // 「畳まれた折りたたみを開く」の一段に統一する（2026-09-11 の UX 見直し）。
            EditorGUILayout.Space(2);
            processingDetailFoldout = EditorGUILayout.Foldout(processingDetailFoldout,
                new GUIContent(Localization.ProcessingDetailFoldout, Localization.ProcessingDetailFoldoutTooltip),
                true);
            if (processingDetailFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    holeFillPasses = UndoHelper.IntSlider(this,
                        new GUIContent(Localization.HoleFillPasses, Localization.HoleFillPassesTooltip),
                        holeFillPasses, 0, 10);
                    holeFillMinNeighbors = UndoHelper.IntSlider(this,
                        new GUIContent(Localization.HoleFillMinNeighbors, Localization.HoleFillMinNeighborsTooltip),
                        holeFillMinNeighbors, 1, 8);
                    relaxedSatMin = UndoHelper.Slider(this,
                        new GUIContent(Localization.RelaxedSatMin, Localization.RelaxedSatMinTooltip),
                        relaxedSatMin, 0f, 0.2f);
                    // relaxedSatRamp は出さない。緩和マッチで使わなくなり、動かしても何も変わらないため。
                    // 値は選択キャッシュのキーと、プリセット・セッションの保存形式の互換のために残している。
                    using (new EditorGUI.DisabledScope(!useDecontamination))
                    {
                        decontaminationRadius = UndoHelper.IntSlider(this,
                            new GUIContent(Localization.DecontaminationRadius, Localization.DecontaminationRadiusTooltip),
                            decontaminationRadius,
                            RecolorSettings.MinDecontaminationRadius, RecolorSettings.MaxDecontaminationRadius);
                    }
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }
    }
}
