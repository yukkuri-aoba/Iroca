// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 単体出力 / 一括適用の UI 描画と PNG 書き出しを担当する。
    /// プレビュー生成・プリセット管理・マスク編集には関与しない。
    /// </summary>
    [System.Serializable]
    internal class ExportView {
        public bool batchFoldout;
        public List<Texture2D> batchTextures = new List<Texture2D>();

        public bool saveAsNewFile = true;
        public string newFileName = "";
        public bool inheritImportSettings = true;
        // NDMF があるときの「テクスチャとして書き出す」の開閉。主は非破壊の登録なので既定は閉じる。
        public bool textureExportFoldout;

        [System.NonSerialized] private Vector2 _batchScrollPos;
        [System.NonSerialized] private IrocaWindow _host;

        // シーンで「このテクスチャを書き出した画像」を使っているマテリアルの数(非破壊へ移る案内に使う)。
        // シーン全体を毎フレーム探すと重いので、テクスチャが変わったときと 2 秒ごとに数え直す。
        // 数え直すのは Layout のときだけ(同じ描画の Layout と Repaint で表示が食い違うと GUILayout が崩れる)。
        [System.NonSerialized] private Texture2D _exportUsersFor;
        [System.NonSerialized] private double _exportUsersAt;
        [System.NonSerialized] private int _exportUsersCount;

        // 直近のエラー。ShowNotification はウィンドウ右下に数秒出て消えるので、席を外していた
        // ユーザーには「押したのに保存されていない」だけが残っていた。消えない表示を欄内に置き、
        // 「閉じる」で明示的に消してもらう（内容は Console にも残る）。
        [SerializeField] private string _lastError;
        // 直近に書き出したファイルのプロジェクト相対パス（Assets/ 配下でないときは null）。
        // 保存後に Project ウィンドウで選択・表示する導線に使う。
        [SerializeField] private string _lastSavedAssetPath;

        // メインスレッドで pixels を取得し、PixelProcessor 計算 + PNG エンコードを
        // Task.Run(バックグラウンド)で実行する。完了後、メインスレッドでは書き込みと AssetDatabase 操作のみ。
        // (旧: エンコード/書き込みもメインスレッドで行い 4K で終了時にフリーズしていた)
        [System.NonSerialized] private readonly PreviewJob<ExportPayload> _exportJob = new PreviewJob<ExportPayload>();
        [System.NonSerialized] private readonly PreviewJobProgress _exportProgress = new PreviewJobProgress();

        private struct ExportPayload {
            public string outputPath;
            public string srcPath;
            public bool inheritImportSettings;
            // エンコード済み PNG(ヒープの外。apply か discard で手放す)。ディスクへの書き込みは apply(メインスレッド)で行う。
            // BG で書くと「ファイルは書けたがキャンセルで apply が走らない」中途半端な状態
            // (ImportAsset も InvalidateSourceAndRepaint も未実行)が作れてしまうため。
            public Unity.Collections.NativeArray<byte> png;
        }

        /// <summary>エクスポート処理中。true の間はウィンドウ全体を Disabled に。</summary>
        public bool IsExporting => _exportJob.IsRunning;

        /// <summary>進捗バーに出している進み(%)。ウィンドウはこれが変わったときだけ描き直す(IrocaWindow.TickRepaintWatch)。</summary>
        internal int ProgressPercent => Mathf.RoundToInt(_exportProgress.Value * 100f);

        public void Initialize(IrocaWindow host) {
            _host = host;
        }

        public void Dispose() {
            _exportJob.Dispose();
        }

        /// <summary>直近のエラー(欄に残している文)。無ければ null か空。</summary>
        internal string LastError => _lastError;

        /// <summary>
        /// スクリプトの再読み込みが始まるときに呼ぶ。書き込みはメインスレッドへ戻ってから行うが、戻る予定は再読み込みで消えるので、
        /// 走っている書き出しは止めて知らせる(知らせないと、ファイルが書かれないまま何も出ない)。文は保存されるので再読み込みの後も残る。
        /// </summary>
        internal void CancelForReload() {
            if (!_exportJob.IsRunning) {
                return;
            }
            _exportJob.Cancel();
            _lastError = Localization.ExportInterruptedByReload;
            Debug.LogWarning("[Iroca] " + _lastError);
            // 届いている結果(エンコード済みの PNG)を手放す。
            PreviewJobMainThread.Drain();
        }

        // 並びと高さは GetSectionHeight と対で保つ(食い違うとエクスポート欄が画面外へ押し出される)。
        public void DrawExportSection() {
#if IROCA_NDMF_PRESENT
            // 非破壊(NDMF)が主: 元のテクスチャを書き換えず、アバターのビルド時にだけ差し替える。
            // テクスチャへの書き出しは、配布する衣装や NDMF を使わない場面のために畳んで残す。
            EditorGUILayout.LabelField(Localization.StepPrefixExport + Localization.ApplyToAvatar, EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(_host.SourceTexture == null);
            DrawRegister(primary: true);
            EditorGUILayout.Space(4);
            textureExportFoldout = EditorGUILayout.Foldout(textureExportFoldout,
                TempContent.Of(Localization.ExportAsTexture, Localization.ExportAsTextureTooltip), true);
            if (textureExportFoldout) {
                DrawTextureExport(primary: false);
            }
#else
            EditorGUILayout.LabelField(Localization.StepPrefixExport + Localization.Export, EditorStyles.boldLabel);
            // 外側の DisabledScope（IrocaWindow.OnGUI で囲まれる）を壊さないよう、
            // GUI.enabled の直接代入ではなく BeginDisabledGroup を使う。
            EditorGUI.BeginDisabledGroup(_host.SourceTexture == null);
            DrawTextureExport(primary: true);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(Localization.NdmfUnavailable(NdmfSupport.State), MessageType.Warning);
            DrawRegister(primary: false);
#endif

            // 直近のエラー（消えない表示）。書き出し欄を畳んでも見えるよう外に置く。
            if (!string.IsNullOrEmpty(_lastError)) {
                EditorGUILayout.HelpBox(_lastError, MessageType.Error);
                if (GUILayout.Button(TempContent.Of(Localization.DismissError, Localization.DismissErrorTooltip))) {
                    _lastError = null;
                }
            }

            EditorGUI.EndDisabledGroup();
        }

        /// <summary>テクスチャへの書き出し(PNG)。primary のときだけ実行ボタンを大きくする。</summary>
        private void DrawTextureExport(bool primary) {
            var saveAsNewContent = TempContent.Of(Localization.SaveAsNewFile, Localization.SaveAsNewFileTooltip);
            var inheritContent = TempContent.Of(Localization.InheritImportSettings, Localization.InheritImportSettingsTooltip);
            // トグルのラベルが入りきる幅までラベル列を広げる。狭いウィンドウの既定のラベル幅（120）では
            // 「インポート設定を引き継ぐ」の末尾が欠ける。
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(prevLabelWidth, Mathf.Ceil(Mathf.Max(
                EditorStyles.label.CalcSize(saveAsNewContent).x,
                EditorStyles.label.CalcSize(inheritContent).x)) + 4f);

            saveAsNewFile = EditorGUILayout.Toggle(saveAsNewContent, saveAsNewFile);

            inheritImportSettings = EditorGUILayout.Toggle(inheritContent, inheritImportSettings);

            if (saveAsNewFile) {
                newFileName = EditorGUILayout.TextField(
                    TempContent.Of(Localization.FileName, Localization.FileNameTooltip),
                    newFileName);
            }
            EditorGUIUtility.labelWidth = prevLabelWidth;

            // ソロ表示はプレビュー専用。保存すると全ゾーンが適用されるので、
            // 「見えているもの＝保存されるもの」でないことをここで明示する。
            if (_host.SoloZone != null) {
                EditorGUILayout.HelpBox(Localization.ExportSoloWarning, MessageType.Info);
            }

            var applyContent = TempContent.Of(Localization.ApplyAndSave, Localization.ApplyAndSaveTooltip);
            if (primary ? GUILayout.Button(applyContent, GUILayout.Height(PrimaryButtonHeight)) : GUILayout.Button(applyContent)) {
                ApplyRecolor();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(TempContent.Of(Localization.OpenFolder, Localization.OpenFolderTooltip))) {
                string path = AssetDatabase.GetAssetPath(_host.SourceTexture);
                if (!string.IsNullOrEmpty(path)) {
                    EditorUtility.RevealInFinder(path);
                }
            }
            // 保存後に Unity 内で書き出し先へ辿り着く導線。「フォルダを開く」は OS の
            // ファイルマネージャを開くだけで、マテリアルへ差し替える作業には使えなかった。
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_lastSavedAssetPath))) {
                if (GUILayout.Button(TempContent.Of(Localization.RevealInProject, Localization.RevealInProjectTooltip))) {
                    RevealLastSavedInProject();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>非破壊(NDMF)の登録。primary のときだけボタンを大きくする。</summary>
        private void DrawRegister(bool primary) {
            var registerContent = TempContent.Of(Localization.RegisterNonDestructive, Localization.RegisterNonDestructiveTooltip);
            if (primary ? GUILayout.Button(registerContent, GUILayout.Height(PrimaryButtonHeight)) : GUILayout.Button(registerContent)) {
                _host.RegisterToAvatar();
            }
            var bound = _host.BoundRecipe;
            if (bound != null) {
                // 表示だけ(差し替えは「いろかで開く」で行う。ここでドロップされても結び付けない)。
                using (new EditorGUI.DisabledScope(true)) {
                    EditorGUILayout.ObjectField(TempContent.Of(Localization.BoundRecipe, Localization.BoundRecipeTooltip),
                        bound, typeof(IrocaRecipe), false);
                }
                // 読めないレシピ(新しい版のいろかで作られた・壊れた)には保存も登録もしないので、そう知らせる。
                if (BoundRecipeUnreadable(bound)) {
                    EditorGUILayout.HelpBox(Localization.BoundRecipeUnreadable, MessageType.Warning);
                }
            }
            // 以前書き出した画像をマテリアルに差していると非破壊が効かないので、登録で戻せることを知らせる。
            int exportUsers = ExportUsersCount();
            if (exportUsers > 0) {
                EditorGUILayout.HelpBox(string.Format(Localization.ExportUsersHintFormat, exportUsers), MessageType.Info);
            }
        }

        /// <summary>直近に書き出したテクスチャを Project ウィンドウで選択・表示する。</summary>
        private void RevealLastSavedInProject() {
            if (string.IsNullOrEmpty(_lastSavedAssetPath)) {
                return;
            }
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(_lastSavedAssetPath);
            if (asset == null) {
                // 保存後に消された／移動された。導線を残しておくと押しても無反応になるので畳む。
                _lastSavedAssetPath = null;
                return;
            }
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        public void SetSourceTextureBaseName(string baseNameWithoutExtension) {
            newFileName = baseNameWithoutExtension + PathUtils.RecoloredSuffix;
        }

        private const float PrimaryButtonHeight = 32f;

        /// <summary>
        /// エクスポートセクションの描画想定高さを返す。
        /// IrocaWindow の横並びレイアウトで「上部 + プレビュー領域」の高さ計算に使う。
        /// DrawExportSection と同じ並び・同じ条件で足し合わせる。
        /// </summary>
        public float GetSectionHeight() {
            float lineH = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            float h = lineH;          // 見出しラベル
#if IROCA_NDMF_PRESENT
            h += RegisterHeight(primary: true);
            h += 4f;                  // Space(4)
            h += lineH;               // 「テクスチャとして書き出す」の折りたたみ
            if (textureExportFoldout) {
                h += TextureExportHeight(primary: false);
            }
#else
            h += TextureExportHeight(primary: true);
            h += 4f;                  // Space(4)
            h += NdmfUnavailableHelpBoxHeight();
            h += RegisterHeight(primary: false);
#endif
            if (!string.IsNullOrEmpty(_lastError)) {
                // エラー HelpBox +「閉じる」ボタン。エラーの文は長く(パスを含む)、狭い窓では何行にも折り返すので、文で測る
                // (固定の 40 では足りず、「閉じる」が窓の下で切れうる)。
                h += HelpBoxHeight(_lastError) + lineH;
            }
            return h;
        }

        private float TextureExportHeight(bool primary) {
            float lineH = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            float h = lineH;          // saveAsNewFile トグル
            if (saveAsNewFile) {
                h += lineH;           // ファイル名フィールド
            }
            h += lineH;               // inheritImportSettings トグル
            // HelpBox は内容と幅で高さが変わるので、文で測る(HelpBoxHeight)。
            if (_host != null && _host.SoloZone != null) {
                h += HelpBoxHeight(Localization.ExportSoloWarning);   // ソロ表示中の注意
            }
            h += primary ? PrimaryButtonHeight + EditorGUIUtility.standardVerticalSpacing : lineH; // ApplyAndSave ボタン
            h += lineH;               // OpenFolder / Project で表示（1 行に横並び）
            return h;
        }

        private float RegisterHeight(bool primary) {
            float lineH = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            float h = primary ? PrimaryButtonHeight + EditorGUIUtility.standardVerticalSpacing : lineH; // アバターに非破壊で登録
            if (_host != null && _host.BoundRecipe != null) {
                h += lineH;           // 保存先のレシピ
                if (BoundRecipeUnreadable(_host.BoundRecipe)) {
                    h += HelpBoxHeight(Localization.BoundRecipeUnreadable);
                }
            }
            int exportUsers = ExportUsersCount();
            if (exportUsers > 0) {      // 書き出した画像を使うマテリアルの案内
                h += HelpBoxHeight(string.Format(Localization.ExportUsersHintFormat, exportUsers));
            }
            return h;
        }

#if !IROCA_NDMF_PRESENT
        private static float NdmfUnavailableHelpBoxHeight() => HelpBoxHeight(Localization.NdmfUnavailable(NdmfSupport.State));
#endif

        // 長い HelpBox は縦並び(狭幅)では 3 行以上に折り返すので固定の 40 では足りない。
        // ウィンドウ幅からアイコンと余白ぶんを引いた幅で実測し、40 を下限にする。
        private static float HelpBoxHeight(string text) {
            float textW = Mathf.Max(100f, EditorGUIUtility.currentViewWidth - 60f);
            float measured = EditorStyles.helpBox.CalcHeight(TempContent.Of(text), textW);
            return Mathf.Max(40f, measured) + EditorGUIUtility.standardVerticalSpacing;
        }

        // 保存先のレシピを読めるか。描画のたびに JSON(マスク込み)を読まないよう、中身が変わったときだけ読み直す。
        [System.NonSerialized] private readonly RecipeSummaryCache _boundSummary = new RecipeSummaryCache();

        private bool BoundRecipeUnreadable(IrocaRecipe bound)
            => bound != null && !string.IsNullOrEmpty(bound.sessionJson) && !_boundSummary.Readable(bound);

        private int ExportUsersCount() {
            var source = _host != null ? _host.SourceTexture : null;
            var e = Event.current;
            bool layout = e == null || e.type == EventType.Layout;
            double now = EditorApplication.timeSinceStartup;
            if (layout && (source != _exportUsersFor || now - _exportUsersAt > 2.0)) {
                _exportUsersFor = source;
                _exportUsersAt = now;
                _exportUsersCount = source != null
                    ? RecipeRegistration.CountSceneMaterialsUsing(RecipeRegistration.ExportsOf(source))
                    : 0;
            }
            return _exportUsersFor == source ? _exportUsersCount : 0;
        }

        // internal は WindowOutputParityTests がボタンを押さずに書き出すため。
        internal void ApplyRecolor() {
            if (_exportJob.IsRunning) {
                return;
            }

            // 前回のエラー表示は新しい試行の開始で畳む（成功したのに古い赤が残らないように）。
            _lastError = null;

            // 有効なゾーンが無いと無変更ファイルを書き出して「完了」表示になり誤解を生むため、
            // 処理に入る前に止めてユーザーへ誘導する。
            if (_host.Session == null || !_host.Session.zones.Any(z => z.enabled)) {
                _host?.ShowNotification(new GUIContent(Localization.NoEnabledZones));
                return;
            }

            // Read/Write は事前条件にしない。原本が PNG/JPG なら直接読めるし、読めない形式は
            // 下のフォールバックが取り込み済みテクスチャを使う（そこで初めて Read/Write が要る）。
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null) {
                NotifyError(Localization.TextureReadError);
                return;
            }

            string srcPath = AssetDatabase.GetAssetPath(sourceTexture);
            if (string.IsNullOrEmpty(srcPath)) {
                NotifyError(Localization.PathNotFound);
                return;
            }

            // 重い処理のあとでキャンセルされると計算が全て無駄になるので、
            // 確認はユーザー入力の時点（＝処理前）に行う。
            // パッケージの中へは書かない(取り込まれず、パッケージの更新で消える)。
            if (!saveAsNewFile && !ExportPipeline.CanOverwriteInPlace(srcPath)) {
                NotifyError(Localization.ExportCannotOverwritePackage);
                return;
            }
            string outputPath = ExportPipeline.SingleOutputPath(srcPath, saveAsNewFile, newFileName);
            if (saveAsNewFile) {
                if (File.Exists(outputPath)) {
                    if (!EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.FileExistsConfirm(outputPath), Localization.Overwrite, Localization.Cancel)) {
                        return;
                    }
                }
            } else {
                var (confirmMessage, confirmOk) = InPlaceSaveConfirm(srcPath, outputPath, File.Exists(outputPath));
                if (!EditorUtility.DisplayDialog(Localization.Confirm, confirmMessage, confirmOk, Localization.Cancel)) {
                    return;
                }
            }
            StartExport(sourceTexture, srcPath, outputPath, readInJobAllowed: true);
        }

        /// <summary>
        /// 背景で PNG の画素を読めなかった(見出しは読めたが途中で壊れている)。メインスレッドで読み直して(LoadImage・取り込み済み
        /// テクスチャ)書き出し直す印。窓・非破壊ビルドと同じ画素から書き出すため(読み直さないと書き出しだけが止まっていた)。
        /// </summary>
        private sealed class ReadSourceOnMainThread : System.Exception { }

        /// <summary>
        /// 原本を読んで、色替え・PNG 化(背景)と書き込み(メインスレッド)を始める。確認の画面は済んでいること。
        /// <paramref name="readInJobAllowed"/> なら、別スレッドで読める PNG は背景で読む。
        /// </summary>
        private void StartExport(Texture2D sourceTexture, string srcPath, string outputPath, bool readInJobAllowed) {
            // Texture2D API はメインスレッド必須なのでここで全て済ませ、計算本体だけ Task.Run へ渡す。
            // 原本を直接デコードできるのは PNG/JPG だけ。PSD/TGA/EXR 等は Unity が取り込み時に
            // 変換しているので LoadImage が読めず、以前はここで停止していた＝プレビューでは
            // 色替えできるのに、作り込んだ後の「適用して保存」だけが失敗していた。
            // プレビューと同じく、読めないときは取り込み済みテクスチャの画素で書き出す。
            // 画素は Mono のヒープの外(ExportService)。ジョブの後始末(cleanup)で手放す。
            // PNG はジョブの中で読む(4K で 150〜320ms、8K で 560ms メインスレッドを止めない。元の解像度のまま)。
            // それ以外と扱わない PNG は LoadImage・GPU の展開が要るのでここで読む。
            ExportPipeline.SourceKind source = ExportPipeline.SourceKind.File;
            NativePixels pixels = null;
            // プレビューが同じ元ファイルを元の解像度のまま読んで持っていれば、読み直さずにそれを写して使う(ジョブの中で)。
            NativePixels shared = null;
            int texW = 0, texH = 0;
            bool readInJob;
            try {
                readInJob = readInJobAllowed && PngFileDecoder.CanDecode(srcPath);
                if (readInJob) {
                    shared = ShareFullResolutionSource(sourceTexture, srcPath);
                }
                if (!readInJob) {
                    source = ExportService.ReadSource(srcPath, sourceTexture, out pixels);
                }
            } catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException) {
                // ほかのソフトが開いている・オンライン専用のファイルなど。例外のままだと欄に理由が出ない。
                NotifyError(string.Format(Localization.ExportSourceReadFailedFormat, e.Message));
                return;
            }
            if (!ExportPipeline.HasPixels(source)) {
                // 原本も取り込み側も読めない。Read/Write を有効にすれば後者が使える(原本が壊れた PNG/JPG なら、まず保存し直す)。
                NotifyError(source == ExportPipeline.SourceKind.Undecodable
                    ? Localization.ExportSourceUndecodable : Localization.ExportSourceUnavailable);
                return;
            }
            if (pixels != null) {
                texW = pixels.Width;
                texH = pixels.Height;
            } else if (shared != null) {
                texW = shared.Width;
                texH = shared.Height;
            }

            if (source == ExportPipeline.SourceKind.ImportedTexture) {
                // 取り込み済みテクスチャは maxTextureSize の縮小・圧縮を受けている。出力が原本と
                // 同じ解像度・画質になるとは限らないので、書き出す前に知らせる。
                Debug.LogWarning($"[Iroca] Source file could not be decoded ({Path.GetExtension(srcPath)}); "
                    + $"exporting from the imported texture ({texW}x{texH}).");
                _host?.ShowNotification(new GUIContent(Localization.ExportFromImportedTexture(texW, texH)));
            }

            var session = _host.Session;
            // リスト先頭のゾーンほど先に処理され、重なった領域を占有する。
            // ゾーンは Clone してから BG へ渡す(プレビュー系と同じ防御コピー)。書き出し中も
            // Undo などでゾーンが変異し得るため、生参照だと一部ゾーンだけ新旧混在の出力に
            // なり得る。Clone は値等価コピーで出力ビット不変。
            // 非破壊ビルド(SessionRecolor.Apply)と同じ選び方を通す(RecolorInput)。
            // 計算に必要な値を全てローカル変数に退避（Task.Run の中から session を直接触らない）
            var input = RecolorInput.FromEditing(session,
                RecolorInput.CountEnabled(session.zones) > 0 ? _host.BuildMaskSnapshot() : null);
            bool inheritFlag = inheritImportSettings;
            // 書き出しの寸法が編集の作業解像度と同じなら、書き出しが使う作業配列はプレビューが温めていたものと
            // 同じ大きさなので、終わっても手放さない(次の 1 手が作業配列と色の表を作り直さずに済む)。
            // 違う寸法(取り込み後の寸法で編集して元の解像度で書き出す)なら、その解像度の作業配列
            // (8K で約 3GB)を OS へ返す。
            int workW = 0, workH = 0;
            bool haveWorkSize = _host?.Preview != null && _host.Preview.TryGetWorkingSize(out workW, out workH);
            // ジョブの中で読むときは、読み終えるまで書き出しの寸法が分からないので、終わったときに決める。
            bool ReleaseAfterExport() => !(haveWorkSize && workW == texW && workH == texH);

            _exportProgress.Reset();
            _exportProgress.Report(0.05f);

            _exportJob.Schedule(
                work: ct => {
                    if (shared != null) {
                        // 色替えはその場で書き換えるので、プレビューと共有の画素を写してから使う(デコードより速い)。
                        ct.ThrowIfCancellationRequested();
                        pixels = NativePixels.Allocate(shared.Width, shared.Height);
                        pixels.CopyFrom(shared);
                        shared.Release();
                        shared = null;
                    } else if (readInJob) {
                        try {
                            pixels = TextureIO.TryDecodePngOffMainThread(srcPath, 0, 0, ct);
                        } catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException) {
                            throw new System.Exception(string.Format(Localization.ExportSourceReadFailedFormat, e.Message), e);
                        }
                        // 見出しは読めたが画素の途中で壊れている。メインスレッドで読み直す(onError)。
                        if (pixels == null) {
                            throw new ReadSourceOnMainThread();
                        }
                        texW = pixels.Width;
                        texH = pixels.Height;
                    }
                    _exportProgress.Report(0.10f);
                    // 取り消しに対応する(処理の中で ThrowIfCancellationRequested を呼ぶ)。
                    ExportService.Recolor(pixels, input, ct);
                    ct.ThrowIfCancellationRequested();
                    _exportProgress.Report(0.80f);

                    // PNG エンコードもバックグラウンドで行う(メインスレッドの終了時フリーズを解消。
                    // 旧版は Texture2D + EncodeToPNG をメインスレッドで実行し 4K で秒単位ブロックしていた)。
                    // スレッド安全性の根拠は ExportPipeline.EncodePng / TextureIO.EncodePng を参照。
                    var png = ExportService.EncodePng(pixels);
                    _exportProgress.Report(0.95f);

                    // ★ここでディスクへ書かない★ — 書いてしまうと、この直後〜apply までの間に
                    // キャンセルされた場合に「PNG は置き換わったのに ImportAsset も
                    // InvalidateSourceAndRepaint も走っていない」状態が残る。上書きモードでは
                    // A-2 で塞いだ二重適用経路(プレビューが古い原本画素を握り続ける)が復活する。
                    // 重いのはエンコードでありファイル書き込みではないので、書き込みは apply で行う。
                    return new ExportPayload {
                        outputPath = outputPath,
                        srcPath = srcPath,
                        inheritImportSettings = inheritFlag,
                        png = png,
                    };
                },
                apply: payload => {
                    // メインスレッド: 書き込み + AssetDatabase 操作(重いエンコードは BG で完了済み)。
                    // ここまで来たらキャンセルされないので、書き込みと取り込みは必ず対で走る。
                    //
                    // PreviewJob は apply で投げた例外を onError へ回さない(BG の例外だけを拾う)。
                    // 書き込みは権限不足・ディスクフル等で普通に失敗し得るので、ここで受けて
                    // BG 失敗時と同じ経路へ流す。放置すると失敗が UI に一切出ない。
                    try {
                        // 書き込みはアトミック(書き潰す相手が元テクスチャそのものであり得るため)。
                        string relativePath = ExportService.WriteAndImport(
                            payload.outputPath, payload.png, payload.srcPath, payload.inheritImportSettings);
                        // 「Project で表示」の対象。Assets/ の外へ書いた場合は null のまま
                        // （Unity のアセットではないので Project ウィンドウに出せない）。
                        _lastSavedAssetPath = relativePath;

                        // ソース自身を書き換えたなら、プレビューが握っている「ディスク原本の画素」は
                        // もう古い。捨てないと、次のエクスポートが再着色済みファイルを読み直して
                        // 二重適用になる（プレビューは旧画素を表示し続けるので画面では気づけない）。
                        if (ExportPipeline.IsSameFile(payload.outputPath, payload.srcPath)) {
                            _host?.InvalidateSourceAndRepaint();
                        }

                        _exportProgress.Report(1.0f);
                        Debug.Log($"[Iroca] Saved: {payload.outputPath}");
                        // 非モーダル通知: ファイル名のみウィンドウ右下に短時間表示。詳細パスは Debug.Log。
                        _host?.ShowNotification(new GUIContent($"{Localization.Complete}: {Path.GetFileName(payload.outputPath)}"));
                    } catch (System.Exception ex) {
                        ReportExportFailure(ex);
                    } finally {
                        payload.png.Dispose();
                        // 書き出しの解像度の作業配列を OS へ返す(一括書き出しの末尾と同じ理由)。
                        if (ReleaseAfterExport()) {
                            PixelProcessor.ReleaseWorkBuffersInBackground();
                        }
                    }
                },
                onError: ex => {
                    if (ex is ReadSourceOnMainThread) {
                        // 窓・非破壊ビルドと同じく、LoadImage・取り込み済みテクスチャで読み直して書き出し直す(1 回だけ)。
                        StartExport(sourceTexture, srcPath, outputPath, readInJobAllowed: false);
                        return;
                    }
                    ReportExportFailure(ex);
                    if (ReleaseAfterExport()) {
                        PixelProcessor.ReleaseWorkBuffersInBackground();
                    }
                },
                // 原本の画素は計算の後で(取り消されて計算しなかったときも)返す。
                cleanup: () => {
                    pixels?.Release();
                    shared?.Release();   // 取り消されて写さなかったとき
                    input.ReleaseMasks();
                },
                // エンコードまで済んだが書き込まなかった(取り消し・破棄)PNG を手放す。
                discard: payload => payload.png.Dispose());
        }

        /// <summary>
        /// プレビューが持つ原本(<see cref="SourcePixelCache"/>)が、書き出しがいま元ファイルを元の解像度で読み直すのと
        /// 同じ画素なら、参照を 1 つ足して返す(呼んだ側が Release する。共有なので書き換えない)。同じと言えるのは、
        /// 同じ元ファイルを読む前と今とでファイルの大きさと更新時刻が同じで(<see cref="SourcePixelCache.ShareIfCurrent"/>)、
        /// 作業解像度が元ファイルの寸法のまま(縮めていない)のとき。PNG はどちらも同じデコーダで読むので同じ画素になる。
        /// 確かめられなければ null(今までどおり読み直す)。
        /// </summary>
        private NativePixels ShareFullResolutionSource(Texture2D tex, string srcPath) {
            var sources = _host?.Editing?.Sources;
            if (sources == null || sources.Texture != tex || sources.Pixels == null) {
                return null;
            }
            int fileW, fileH;
            try {
                if (!PngFileDecoder.TryReadSize(srcPath, out fileW, out fileH)) {
                    return null;
                }
            } catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException) {
                return null;
            }
            if (sources.Width != fileW || sources.Height != fileH) {
                return null;
            }
            return sources.ShareIfCurrent(tex, srcPath);
        }

        /// <summary>
        /// 「新しいファイルにしない」で保存するときの確認の文と、確定のボタン。
        /// 元が PNG なら真の上書き。非 PNG（.jpg/.tga 等）だと ChangeExtension は元ファイルを上書きせず隣に .png を作るだけで、
        /// マテリアルは旧ファイル参照のまま＝「色が変わらない」ように見える。文言も「上書き」で矛盾するので、新規作成＋非自動切替を
        /// 明示する。ただし隣に同じ名前の PNG があれば、それは元テクスチャとは別のファイルなので、「新規作成」と案内せず置き換えを確かめる
        /// (PSD と書き出した PNG を並べて置いている素材は多く、黙って失っていた)。
        /// internal は ExportPipelineTests が確かめるため(ダイアログはテストで押せない)。
        /// </summary>
        internal static (string message, string ok) InPlaceSaveConfirm(string srcPath, string outputPath, bool outputExists) {
            if (string.Equals(Path.GetExtension(srcPath), ".png", System.StringComparison.OrdinalIgnoreCase)) {
                return (Localization.OverwriteConfirm, Localization.Overwrite);
            }
            if (outputExists) {
                return (Localization.FileExistsConfirm(outputPath), Localization.Overwrite);
            }
            return (Localization.OverwriteNonPngConfirm(Path.GetFileName(outputPath)), Localization.OK);
        }

        /// <summary>エクスポート失敗をログ＋UI 通知へ流す（BG 側と apply 側で共有）。</summary>
        private void ReportExportFailure(System.Exception ex) {
            Debug.LogError($"[Iroca] Export failed: {ex.Message}\n{ex.StackTrace}");
            NotifyError(ex.Message);
        }

        /// <summary>
        /// エラーを Console に出しつつ Iroca ウィンドウ内に非モーダル通知を表示する。
        /// EditorUtility.DisplayDialog は Editor 全体をブロックするため避ける。
        /// </summary>
        private void NotifyError(string message) {
            Debug.LogError($"[Iroca] {message}");
            _host?.ShowNotification(new GUIContent($"{Localization.Error}: {message}"));
            // 通知は数秒で消えるので、欄内にも残す（「閉じる」まで消えない）。
            _lastError = message;
        }

        /// <summary>
        /// エクスポート実行中の進捗バーとキャンセルボタンを描画する。
        /// IrocaWindow.OnGUI の DisabledScope の外で呼び出すことで、ウィンドウ全体が
        /// 無効化されている状況でもキャンセルだけは押せるようにする。
        /// </summary>
        public void DrawJobOverlay() {
            if (!_exportJob.IsRunning) {
                return;
            }

            float pct = _exportProgress.Value;
            string label = $"{Localization.ApplyAndSave}  {Mathf.RoundToInt(pct * 100f)}%";
            if (IrocaWindow.DrawJobProgressRow(pct, label)) {
                _exportJob.Cancel();
            }
        }

        public void DrawBatchSection() {
            batchFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(batchFoldout, Localization.BatchApply);
            if (!batchFoldout) {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(Localization.BatchHint, MessageType.Info);

            _batchScrollPos = EditorGUILayout.BeginScrollView(_batchScrollPos, GUILayout.MaxHeight(120));
            int removeIdx = -1;
            for (int i = 0; i < batchTextures.Count; i++) {
                EditorGUILayout.BeginHorizontal();
                batchTextures[i] = (Texture2D)EditorGUILayout.ObjectField(
                    batchTextures[i], typeof(Texture2D), false);
                if (GUILayout.Button(TempContent.Of("×", Localization.RemoveBatchTextureTooltip),
                        GUILayout.Width(IrocaUiConsts.Layout.RemoveButtonWidth))) {
                    removeIdx = i;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (removeIdx >= 0) {
                batchTextures.RemoveAt(removeIdx);
            }

            if (GUILayout.Button(TempContent.Of(Localization.AddBatchTexture, Localization.AddBatchTextureTooltip))) {
                batchTextures.Add(null);
            }

            var session = _host.Session;
            EditorGUI.BeginDisabledGroup(batchTextures.Count == 0 || session.zones.Count == 0);
            if (GUILayout.Button(TempContent.Of(Localization.BatchApplyAndSave, Localization.BatchApplyAndSaveTooltip), GUILayout.Height(28))) {
                RunBatchApply();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        private void RunBatchApply() {
            var session = _host.Session;
            var plannedOutputs = new List<string>();
            foreach (var tex in batchTextures) {
                if (tex == null) {
                    continue;
                }
                string srcPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(srcPath)) {
                    continue;
                }
                plannedOutputs.Add(ExportPipeline.BatchOutputPath(srcPath));
            }

            bool anyExists = plannedOutputs.Any(File.Exists);
            if (anyExists) {
                if (!EditorUtility.DisplayDialog(
                        Localization.Confirm,
                        Localization.OverwriteConfirm,
                        Localization.Overwrite,
                        Localization.Cancel)) {
                    return;
                }
            }

            int success = 0;
            // 一括対象に現在プレビュー中のテクスチャの出力先が含まれると、ソース画素が書き換わる。
            // 単体エクスポートと同じくキャッシュを捨てないと二重適用になるので、書き出し先を照合する。
            string previewSrcPath = _host.SourceTexture != null
                ? AssetDatabase.GetAssetPath(_host.SourceTexture) : null;
            bool previewSourceOverwritten = false;
            // マスクは編集中のもの 1 組をどのテクスチャにも当てる(ループの間に編集は変わらない)。1 枚ごとに作り直さず、
            // 1 つ作って各テクスチャの入力が参照を 1 つずつ持つ。
            bool anyZones = RecolorInput.CountEnabled(session.zones) > 0;
            var masks = anyZones ? _host.BuildMaskSnapshot() : null;
            // 書き出した寸法がどれも編集の作業解像度と同じなら、作業配列はプレビューが温めていたものと同じ大きさなので
            // 手放さない(単体の書き出しと同じ規則。次の 1 手が作業配列を作り直さずに済む)。
            int workW = 0, workH = 0;
            bool haveWorkSize = _host?.Preview != null && _host.Preview.TryGetWorkingSize(out workW, out workH);
            bool otherSize = false;
            // 作業配列を借りる計算の区間(手を止めて 10 秒で空きを返す予約は、この区間が終わったときに入る)。
            NativeMemoryLifecycle.WorkStarted();
            try {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < batchTextures.Count; i++) {
                    var tex = batchTextures[i];
                    if (tex == null) {
                        continue;
                    }

                    if (EditorUtility.DisplayCancelableProgressBar(
                            Localization.BatchProgress,
                            tex.name,
                            (float)i / Mathf.Max(1, batchTextures.Count))) {
                        break;
                    }

                    // Read/Write は事前に要求しない。原本が PNG/JPG なら直接読めるので、
                    // 1 枚ごとに「Undo できません」のモーダルを出す必要はなかった
                    // （読めない形式は下のフォールバックが取り込み済みテクスチャを使う）。
                    string srcPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(srcPath)) {
                        continue;
                    }

                    NativePixels pixels = null;
                    try {
                        // 読み込み・PNG 化・書き込みは単体書き出しと同じ ExportService を通す
                        // （以前は一括だけ書き込みがアトミックでなかった）。画素も PNG もヒープの外。
                        var source = ExportService.ReadSource(srcPath, tex, out pixels);
                        // 原本も取り込み側も読めない（PSD/TGA 等で Read/Write 無効・壊れた PNG/JPG）。
                        if (!ExportPipeline.HasPixels(source)) {
                            Debug.LogWarning($"[Iroca] Batch: skipped {tex.name}; the source file could not be read ({source}).");
                            continue;
                        }
                        if (source == ExportPipeline.SourceKind.ImportedTexture) {
                            // 取り込み済みテクスチャで代替する（縮小・圧縮の影響を受ける）。以前は黙って
                            // スキップしていたので、完了件数だけを見たユーザーには理由が残らなかった。
                            Debug.LogWarning($"[Iroca] Batch: source file could not be decoded for {tex.name}; "
                                + $"using the imported texture ({pixels.Width}x{pixels.Height}).");
                        }

                        if (!haveWorkSize || pixels.Width != workW || pixels.Height != workH) {
                            otherSize = true;
                        }
                        // リスト先頭のゾーンほど先に処理され、重なった領域を占有する(単体の書き出しと同じ組み立て)。
                        if (anyZones) {
                            var batchInput = RecolorInput.FromEditing(session, masks?.AddRef());
                            try { ExportService.Recolor(pixels, batchInput); } finally { batchInput.ReleaseMasks(); }
                        }

                        string outPath = ExportPipeline.BatchOutputPath(srcPath);
                        using (var png = ExportService.EncodePng(pixels)) {
                            ExportService.WriteAndImport(outPath, png, srcPath, inheritImportSettings);
                        }
                        if (ExportPipeline.IsSameFile(outPath, previewSrcPath)) {
                            previewSourceOverwritten = true;
                        }
                        success++;
                    } catch (System.Exception ex) {
                        Debug.LogWarning($"[Iroca] Batch apply failed for {tex.name}: {ex.Message}");
                    } finally {
                        pixels?.Release();
                    }
                }
            } finally {
                masks?.Release();
                NativeMemoryLifecycle.WorkEnded();
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            if (previewSourceOverwritten) {
                _host?.InvalidateSourceAndRepaint();
            }
            // 書き出しは元の解像度で処理するので、作業解像度で編集しているとその解像度の作業配列が丸ごと残る
            // (8K で約 3GB)。違う寸法を書き出したときは、使っていない分を OS へ返す(次のプレビューは新しく確保し直す)。
            // 同じ寸法だけなら残す(10 秒手を止めれば、空きは NativeMemoryLifecycle が返す)。
            if (otherSize) {
                PixelProcessor.ReleaseWorkBuffersInBackground();
            }

            EditorUtility.DisplayDialog(Localization.Complete,
                Localization.BatchComplete(success), Localization.OK);
        }
    }
}
