// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 単体出力 / 一括適用の UI 描画と PNG 書き出しを担当する。
    /// プレビュー生成・プリセット管理・マスク編集には関与しない。
    /// </summary>
    [System.Serializable]
    internal class ExportView
    {
        public bool batchFoldout;
        public List<Texture2D> batchTextures = new List<Texture2D>();

        public bool saveAsNewFile = true;
        public string newFileName = "";
        public bool inheritImportSettings = true;

        [System.NonSerialized] private Vector2 _batchScrollPos;
        [System.NonSerialized] private IrocaWindow _host;

        // 直近のエラー。ShowNotification はウィンドウ右下に数秒出て消えるので、席を外していた
        // ユーザーには「押したのに保存されていない」だけが残っていた。消えない表示を欄内に置き、
        // 「閉じる」で明示的に消してもらう（内容は Console にも残る）。
        [SerializeField] private string _lastError;
        // 直近に書き出したファイルのプロジェクト相対パス（Assets/ 配下でないときは null）。
        // 保存後に Project ウィンドウで選択・表示する導線に使う。
        [SerializeField] private string _lastSavedAssetPath;

        // メインスレッドで pixels を取得し、PixelProcessor 計算 + PNG エンコード + 書き込みを
        // Task.Run(バックグラウンド)で実行する。完了後、メインスレッドでは AssetDatabase 操作のみ。
        // (旧: エンコード/書き込みもメインスレッドで行い 4K で終了時にフリーズしていた)
        [System.NonSerialized] private readonly PreviewJob<ExportPayload> _exportJob = new PreviewJob<ExportPayload>();
        [System.NonSerialized] private readonly PreviewJobProgress _exportProgress = new PreviewJobProgress();

        private struct ExportPayload
        {
            public string outputPath;
            public string srcPath;
            public bool inheritImportSettings;
            // エンコード済み PNG。ディスクへの書き込みは apply(メインスレッド)で行う。
            // BG で書くと「ファイルは書けたがキャンセルで apply が走らない」中途半端な状態
            // (ImportAsset も InvalidateSourceAndRepaint も未実行)が作れてしまうため。
            public byte[] pngData;
        }

        /// <summary>エクスポート処理中。true の間はウィンドウ全体を Disabled に。</summary>
        public bool IsExporting => _exportJob.IsRunning;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
        }

        public void Dispose()
        {
            _exportJob.Dispose();
        }

        public void DrawExportSection()
        {
            EditorGUILayout.LabelField(Localization.StepPrefixExport + Localization.Export, EditorStyles.boldLabel);

            // 外側の DisabledScope（IrocaWindow.OnGUI で囲まれる）を壊さないよう、
            // GUI.enabled の直接代入ではなく BeginDisabledGroup を使う。
            EditorGUI.BeginDisabledGroup(_host.SourceTexture == null);

            saveAsNewFile = EditorGUILayout.Toggle(
                new GUIContent(Localization.SaveAsNewFile, Localization.SaveAsNewFileTooltip),
                saveAsNewFile);

            inheritImportSettings = EditorGUILayout.Toggle(
                new GUIContent(Localization.InheritImportSettings, Localization.InheritImportSettingsTooltip),
                inheritImportSettings);

            if (saveAsNewFile)
            {
                newFileName = EditorGUILayout.TextField(
                    new GUIContent(Localization.FileName, Localization.FileNameTooltip),
                    newFileName);
            }

            // ソロ表示はプレビュー専用。保存すると全ゾーンが適用されるので、
            // 「見えているもの＝保存されるもの」でないことをここで明示する。
            if (_host.SoloZone != null)
                EditorGUILayout.HelpBox(Localization.ExportSoloWarning, MessageType.Info);

            if (GUILayout.Button(new GUIContent(Localization.ApplyAndSave, Localization.ApplyAndSaveTooltip), GUILayout.Height(32)))
            {
                ApplyRecolor();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(Localization.OpenFolder, Localization.OpenFolderTooltip)))
            {
                string path = AssetDatabase.GetAssetPath(_host.SourceTexture);
                if (!string.IsNullOrEmpty(path))
                    EditorUtility.RevealInFinder(path);
            }
            // 保存後に Unity 内で書き出し先へ辿り着く導線。「フォルダを開く」は OS の
            // ファイルマネージャを開くだけで、マテリアルへ差し替える作業には使えなかった。
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_lastSavedAssetPath)))
            {
                if (GUILayout.Button(new GUIContent(Localization.RevealInProject, Localization.RevealInProjectTooltip)))
                    RevealLastSavedInProject();
            }
            EditorGUILayout.EndHorizontal();

            // 直近のエラー（消えない表示）。
            if (!string.IsNullOrEmpty(_lastError))
            {
                EditorGUILayout.HelpBox(_lastError, MessageType.Error);
                if (GUILayout.Button(new GUIContent(Localization.DismissError, Localization.DismissErrorTooltip)))
                    _lastError = null;
            }

            EditorGUI.EndDisabledGroup();
        }

        /// <summary>直近に書き出したテクスチャを Project ウィンドウで選択・表示する。</summary>
        private void RevealLastSavedInProject()
        {
            if (string.IsNullOrEmpty(_lastSavedAssetPath)) return;
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(_lastSavedAssetPath);
            if (asset == null)
            {
                // 保存後に消された／移動された。導線を残しておくと押しても無反応になるので畳む。
                _lastSavedAssetPath = null;
                return;
            }
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        public void SetSourceTextureBaseName(string baseNameWithoutExtension)
        {
            newFileName = baseNameWithoutExtension + "_recolored";
        }

        /// <summary>
        /// エクスポートセクションの描画想定高さを返す。
        /// IrocaWindow の横並びレイアウトで「上部 + プレビュー領域」の高さ計算に使う。
        /// 折りたたみは廃止したので常に内部コントロールの合計を返す。
        /// </summary>
        public float GetSectionHeight()
        {
            float lineH = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            // 内訳: 見出しラベル + 新規保存トグル + (新規時のみ)ファイル名 +
            //       インポート設定継承トグル + (ソロ中のみ)注意 HelpBox + 適用ボタン(高さ32) +
            //       フォルダ/Project 行 + (エラー時のみ)HelpBox と「閉じる」
            float h = lineH;          // 見出しラベル
            h += lineH;               // saveAsNewFile トグル
            if (saveAsNewFile)
                h += lineH;           // ファイル名フィールド
            h += lineH;               // inheritImportSettings トグル
            // HelpBox は内容と幅で高さが変わるため固定では測れない。狭い設定列で 2 行に
            // 折り返す想定の概算を置く。過小だとエクスポートが画面外へ押し出されるので、
            // 切り上げ側（安全側）に取る。
            if (_host != null && _host.SoloZone != null)
                h += 40f;             // ソロ表示中の注意
            h += 32f + EditorGUIUtility.standardVerticalSpacing; // ApplyAndSave ボタン
            h += lineH;               // OpenFolder / Project で表示（1 行に横並び）
            if (!string.IsNullOrEmpty(_lastError))
                h += 40f + lineH;     // エラー HelpBox +「閉じる」ボタン
            return h;
        }

        private void ApplyRecolor()
        {
            if (_exportJob.IsRunning) return;

            // 前回のエラー表示は新しい試行の開始で畳む（成功したのに古い赤が残らないように）。
            _lastError = null;

            // 有効なゾーンが無いと無変更ファイルを書き出して「完了」表示になり誤解を生むため、
            // 処理に入る前に止めてユーザーへ誘導する。
            if (_host.Session == null || !_host.Session.zones.Any(z => z.enabled))
            {
                _host?.ShowNotification(new GUIContent(Localization.NoEnabledZones));
                return;
            }

            // Read/Write は事前条件にしない。原本が PNG/JPG なら直接読めるし、読めない形式は
            // 下のフォールバックが取り込み済みテクスチャを使う（そこで初めて Read/Write が要る）。
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null)
            {
                NotifyError(Localization.TextureReadError);
                return;
            }

            string srcPath = AssetDatabase.GetAssetPath(sourceTexture);
            if (string.IsNullOrEmpty(srcPath))
            {
                NotifyError(Localization.PathNotFound);
                return;
            }

            // 重い処理のあとでキャンセルされると計算が全て無駄になるので、
            // 確認はユーザー入力の時点（＝処理前）に行う。
            string outputPath = ExportPipeline.SingleOutputPath(srcPath, saveAsNewFile, newFileName);
            if (saveAsNewFile)
            {
                if (File.Exists(outputPath))
                {
                    if (!EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.FileExistsConfirm(outputPath), Localization.Overwrite, Localization.Cancel))
                    {
                        return;
                    }
                }
            }
            else
            {
                // ソースが PNG なら真の上書き。非 PNG（.jpg/.tga 等）だと ChangeExtension は
                // 元ファイルを上書きせず隣に .png を新規作成するだけで、マテリアルは旧ファイル参照の
                // まま＝「色が変わらない」ように見える。ダイアログ文言も「上書き」で矛盾するので、
                // 非 PNG のときは新規作成＋非自動切替を明示する専用ダイアログを出す。
                bool srcIsPng = string.Equals(Path.GetExtension(srcPath), ".png", System.StringComparison.OrdinalIgnoreCase);
                bool confirmed = srcIsPng
                    ? EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.OverwriteConfirm, Localization.Overwrite, Localization.Cancel)
                    : EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.OverwriteNonPngConfirm(Path.GetFileName(outputPath)), Localization.OK, Localization.Cancel);
                if (!confirmed)
                {
                    return;
                }
            }

            // Texture2D API はメインスレッド必須なのでここで全て済ませ、計算本体だけ Task.Run へ渡す。
            // 原本を直接デコードできるのは PNG/JPG だけ。PSD/TGA/EXR 等は Unity が取り込み時に
            // 変換しているので LoadImage が読めず、以前はここで停止していた＝プレビューでは
            // 色替えできるのに、作り込んだ後の「適用して保存」だけが失敗していた。
            // プレビューと同じく、読めないときは取り込み済みテクスチャの画素で書き出す。
            var source = ExportPipeline.ReadSourcePixels(srcPath, sourceTexture,
                out Color32[] pixels, out int texW, out int texH);
            if (source == ExportPipeline.SourceKind.Unavailable)
            {
                // 原本も取り込み側も読めない。Read/Write を有効にすれば後者が使える。
                NotifyError(Localization.ExportSourceUnavailable);
                return;
            }

            if (source == ExportPipeline.SourceKind.ImportedTexture)
            {
                // 取り込み済みテクスチャは maxTextureSize の縮小・圧縮を受けている。出力が原本と
                // 同じ解像度・画質になるとは限らないので、書き出す前に知らせる。
                Debug.LogWarning($"[Iroca] Source file could not be decoded ({Path.GetExtension(srcPath)}); "
                    + $"exporting from the imported texture ({texW}x{texH}).");
                _host?.ShowNotification(new GUIContent(Localization.ExportFromImportedTexture(texW, texH)));
            }

            var session = _host.Session;
            // リスト先頭のゾーンほど先に処理され、重なった領域を占有する。
            // ゾーンは Clone してから BG へ渡す(プレビュー系と同じ防御コピー)。かんたんモードの
            // 自動調整はエクスポート中も裏で走るため、生参照だと apply や Undo でゾーンが変異し
            // 一部ゾーンだけ新旧混在の出力になり得る。Clone は値等価コピーで出力ビット不変。
            var sorted = session.zones.Where(z => z.enabled).Select(z => z.Clone()).ToList();
            var maskSnap = (sorted.Count > 0) ? _host.BuildMaskSnapshot() : null;

            // 計算に必要な値を全てローカル変数に退避（Task.Run の中から session を直接触らない）
            var settings = RecolorSettings.From(session);
            bool inheritFlag = inheritImportSettings;

            _exportProgress.Reset();
            _exportProgress.Report(0.05f);

            _exportJob.Schedule(
                work: ct =>
                {
                    _exportProgress.Report(0.10f);
                    if (sorted.Count > 0)
                    {
                        // CancellationToken 対応オーバーロード: 内部で ThrowIfCancellationRequested を呼ぶ
                        PixelProcessor.ProcessPixelsArray(pixels, texW, texH,
                            maskSnap, sorted, settings, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    _exportProgress.Report(0.80f);

                    // PNG エンコードもバックグラウンドで行う(メインスレッドの終了時フリーズを解消。
                    // 旧版は Texture2D + EncodeToPNG をメインスレッドで実行し 4K で秒単位ブロックしていた)。
                    // スレッド安全性と Unity 6 移行時の注意は ExportPipeline.EncodePng を参照。
                    byte[] pngData = ExportPipeline.EncodePng(pixels, texW, texH);
                    _exportProgress.Report(0.95f);

                    // ★ここでディスクへ書かない★ — 書いてしまうと、この直後〜apply までの間に
                    // キャンセルされた場合に「PNG は置き換わったのに ImportAsset も
                    // InvalidateSourceAndRepaint も走っていない」状態が残る。上書きモードでは
                    // A-2 で塞いだ二重適用経路(プレビューが古い原本画素を握り続ける)が復活する。
                    // 重いのはエンコードでありファイル書き込みではないので、書き込みは apply で行う。
                    return new ExportPayload
                    {
                        outputPath = outputPath,
                        srcPath = srcPath,
                        inheritImportSettings = inheritFlag,
                        pngData = pngData,
                    };
                },
                apply: payload =>
                {
                    // メインスレッド: 書き込み + AssetDatabase 操作(重いエンコードは BG で完了済み)。
                    // ここまで来たらキャンセルされないので、書き込みと取り込みは必ず対で走る。
                    //
                    // PreviewJob は apply で投げた例外を onError へ回さない(BG の例外だけを拾う)。
                    // 書き込みは権限不足・ディスクフル等で普通に失敗し得るので、ここで受けて
                    // BG 失敗時と同じ経路へ流す。放置すると失敗が UI に一切出ない。
                    try
                    {
                        // 書き込みはアトミック(書き潰す相手が元テクスチャそのものであり得るため)。
                        string relativePath = ExportPipeline.WriteAndImport(
                            payload.outputPath, payload.pngData, payload.srcPath, payload.inheritImportSettings);
                        // 「Project で表示」の対象。Assets/ の外へ書いた場合は null のまま
                        // （Unity のアセットではないので Project ウィンドウに出せない）。
                        _lastSavedAssetPath = relativePath;

                        // ソース自身を書き換えたなら、プレビューが握っている「ディスク原本の画素」は
                        // もう古い。捨てないと、次のエクスポートが再着色済みファイルを読み直して
                        // 二重適用になる（プレビューは旧画素を表示し続けるので画面では気づけない）。
                        if (ExportPipeline.IsSameFile(payload.outputPath, payload.srcPath))
                            _host?.InvalidateSourceAndRepaint();

                        _exportProgress.Report(1.0f);
                        Debug.Log($"[Iroca] Saved: {payload.outputPath}");
                        // 非モーダル通知: ファイル名のみウィンドウ右下に短時間表示。詳細パスは Debug.Log。
                        _host?.ShowNotification(new GUIContent($"{Localization.Complete}: {Path.GetFileName(payload.outputPath)}"));
                    }
                    catch (System.Exception ex)
                    {
                        ReportExportFailure(ex);
                    }
                },
                onError: ReportExportFailure);
        }

        /// <summary>エクスポート失敗をログ＋UI 通知へ流す（BG 側と apply 側で共有）。</summary>
        private void ReportExportFailure(System.Exception ex)
        {
            Debug.LogError($"[Iroca] Export failed: {ex.Message}\n{ex.StackTrace}");
            NotifyError(ex.Message);
        }

        /// <summary>
        /// エラーを Console に出しつつ Iroca ウィンドウ内に非モーダル通知を表示する。
        /// EditorUtility.DisplayDialog は Editor 全体をブロックするため避ける。
        /// </summary>
        private void NotifyError(string message)
        {
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
        public void DrawJobOverlay()
        {
            if (!_exportJob.IsRunning) return;

            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, 18f);
            float pct = _exportProgress.Value;
            string label = $"{Localization.ApplyAndSave}  {Mathf.RoundToInt(pct * 100f)}%";
            EditorGUI.ProgressBar(rect, pct, label);

            if (GUILayout.Button(new GUIContent(Localization.Cancel, Localization.CancelActionTooltip), GUILayout.Height(22)))
            {
                _exportJob.Cancel();
            }
            EditorGUILayout.Space(2);
        }

        public void DrawBatchSection()
        {
            batchFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(batchFoldout, Localization.BatchApply);
            if (!batchFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(Localization.BatchHint, MessageType.Info);

            _batchScrollPos = EditorGUILayout.BeginScrollView(_batchScrollPos, GUILayout.MaxHeight(120));
            int removeIdx = -1;
            for (int i = 0; i < batchTextures.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                batchTextures[i] = (Texture2D)EditorGUILayout.ObjectField(
                    batchTextures[i], typeof(Texture2D), false);
                if (GUILayout.Button(new GUIContent("×", Localization.RemoveBatchTextureTooltip),
                        GUILayout.Width(IrocaConsts.Layout.RemoveButtonWidth)))
                    removeIdx = i;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (removeIdx >= 0) batchTextures.RemoveAt(removeIdx);

            if (GUILayout.Button(new GUIContent(Localization.AddBatchTexture, Localization.AddBatchTextureTooltip)))
                batchTextures.Add(null);

            var session = _host.Session;
            EditorGUI.BeginDisabledGroup(batchTextures.Count == 0 || session.zones.Count == 0);
            if (GUILayout.Button(new GUIContent(Localization.BatchApplyAndSave, Localization.BatchApplyAndSaveTooltip), GUILayout.Height(28)))
                RunBatchApply();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        private void RunBatchApply()
        {
            var session = _host.Session;
            var plannedOutputs = new List<string>();
            foreach (var tex in batchTextures)
            {
                if (tex == null) continue;
                string srcPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(srcPath)) continue;
                plannedOutputs.Add(ExportPipeline.BatchOutputPath(srcPath));
            }

            bool anyExists = plannedOutputs.Any(File.Exists);
            if (anyExists)
            {
                if (!EditorUtility.DisplayDialog(
                        Localization.Confirm,
                        Localization.OverwriteConfirm,
                        Localization.Overwrite,
                        Localization.Cancel))
                {
                    return;
                }
            }

            int success = 0;
            // 一括対象に現在プレビュー中のテクスチャの出力先が含まれると、ソース画素が書き換わる。
            // 単体エクスポートと同じくキャッシュを捨てないと二重適用になるので、書き出し先を照合する。
            string previewSrcPath = _host.SourceTexture != null
                ? AssetDatabase.GetAssetPath(_host.SourceTexture) : null;
            bool previewSourceOverwritten = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < batchTextures.Count; i++)
                {
                    var tex = batchTextures[i];
                    if (tex == null) continue;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            Localization.BatchProgress,
                            tex.name,
                            (float)i / Mathf.Max(1, batchTextures.Count)))
                    {
                        break;
                    }

                    // Read/Write は事前に要求しない。原本が PNG/JPG なら直接読めるので、
                    // 1 枚ごとに「Undo できません」のモーダルを出す必要はなかった
                    // （読めない形式は下のフォールバックが取り込み済みテクスチャを使う）。
                    string srcPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(srcPath)) continue;

                    try
                    {
                        // 読み込み・PNG 化・書き込みは単体書き出しと同じ ExportPipeline を通す
                        // （以前は一括だけ書き込みがアトミックでなかった）。
                        var source = ExportPipeline.ReadSourcePixels(srcPath, tex,
                            out Color32[] pixels, out int texW, out int texH);
                        // 原本も取り込み側も読めない（PSD/TGA 等で Read/Write 無効）。
                        if (source == ExportPipeline.SourceKind.Unavailable) continue;
                        if (source == ExportPipeline.SourceKind.ImportedTexture)
                        {
                            // 取り込み済みテクスチャで代替する（縮小・圧縮の影響を受ける）。以前は黙って
                            // スキップしていたので、完了件数だけを見たユーザーには理由が残らなかった。
                            Debug.LogWarning($"[Iroca] Batch: source file could not be decoded for {tex.name}; "
                                + $"using the imported texture ({texW}x{texH}).");
                        }

                        // リスト先頭のゾーンほど先に処理され、重なった領域を占有する。
                        var sorted = session.zones.Where(z => z.enabled).ToList();
                        if (sorted.Count > 0)
                        {
                            var maskSnap = _host.BuildMaskSnapshot();
                            PixelProcessor.ProcessPixelsArray(pixels, texW, texH,
                                maskSnap, sorted, RecolorSettings.From(session));
                        }

                        string outPath = ExportPipeline.BatchOutputPath(srcPath);
                        ExportPipeline.WriteAndImport(outPath, ExportPipeline.EncodePng(pixels, texW, texH),
                            srcPath, inheritImportSettings);
                        if (ExportPipeline.IsSameFile(outPath, previewSrcPath)) previewSourceOverwritten = true;
                        success++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[Iroca] Batch apply failed for {tex.name}: {ex.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            if (previewSourceOverwritten) _host?.InvalidateSourceAndRepaint();

            EditorUtility.DisplayDialog(Localization.Complete,
                Localization.BatchComplete(success), Localization.OK);
        }
    }
}
