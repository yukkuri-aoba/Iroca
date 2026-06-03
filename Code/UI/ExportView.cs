// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// 単体出力 / 一括適用の UI 描画と PNG 書き出しを担当する。
    /// プレビュー生成・プリセット管理・マスク編集には関与しない。
    /// </summary>
    [System.Serializable]
    internal class ExportView
    {
        // 一括適用
        public bool batchFoldout;
        public List<Texture2D> batchTextures = new List<Texture2D>();

        // エクスポート
        public bool exportFoldout = true;
        public bool saveAsNewFile = true;
        public string newFileName = "";
        public bool inheritImportSettings = true;

        [System.NonSerialized] private Vector2 _batchScrollPos;
        [System.NonSerialized] private VACCWindow _host;

        // ─── 非同期エクスポート ───
        // メインスレッドで pixels を取得し、PixelProcessor 計算を Task.Run で実行する。
        // 完了後、メインスレッドで Texture2D 復元 → PNG エンコード → ファイル書き込み。
        [System.NonSerialized] private readonly PreviewJob<ExportPayload> _exportJob = new PreviewJob<ExportPayload>();
        [System.NonSerialized] private readonly PreviewJobProgress _exportProgress = new PreviewJobProgress();

        private struct ExportPayload
        {
            public Color32[] pixels;
            public int width, height;
            public string outputPath;
            public string srcPath;
            public bool inheritImportSettings;
        }

        /// <summary>エクスポート処理中。true の間はウィンドウ全体を Disabled に。</summary>
        public bool IsExporting => _exportJob.IsRunning;

        public void Initialize(VACCWindow host)
        {
            _host = host;
        }

        public void Dispose()
        {
            _exportJob.Dispose();
        }

        // ─────────────────────── エクスポート ─────────────────────────

        public void DrawExportSection()
        {
            exportFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(exportFoldout, Localization.Export);
            if (!exportFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            // 外側の DisabledScope（VACCWindow.OnGUI で囲まれる）を壊さないよう、
            // GUI.enabled の直接代入ではなく BeginDisabledGroup を使う。
            EditorGUI.BeginDisabledGroup(_host.SourceTexture == null);

            // チェックボックス類を上にまとめる
            saveAsNewFile = EditorGUILayout.Toggle(
                new GUIContent(Localization.SaveAsNewFile, Localization.SaveAsNewFileTooltip),
                saveAsNewFile);

            inheritImportSettings = EditorGUILayout.Toggle(
                new GUIContent(Localization.InheritImportSettings, Localization.InheritImportSettingsTooltip),
                inheritImportSettings);

            // ファイル名はチェックボックスの下に置く
            if (saveAsNewFile)
            {
                newFileName = EditorGUILayout.TextField(
                    new GUIContent(Localization.FileName, Localization.FileNameTooltip),
                    newFileName);
            }

            if (GUILayout.Button(new GUIContent(Localization.ApplyAndSave, Localization.ApplyAndSaveTooltip), GUILayout.Height(32)))
            {
                ApplyRecolor();
            }

            if (GUILayout.Button(new GUIContent(Localization.OpenFolder, Localization.OpenFolderTooltip)))
            {
                string path = AssetDatabase.GetAssetPath(_host.SourceTexture);
                if (!string.IsNullOrEmpty(path))
                    EditorUtility.RevealInFinder(path);
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        public void SetSourceTextureBaseName(string baseNameWithoutExtension)
        {
            newFileName = baseNameWithoutExtension + "_recolored";
        }

        /// <summary>
        /// エクスポートセクションの描画想定高さを返す。
        /// VACCWindow の横並びレイアウトで「上部 + プレビュー領域」の高さ計算に使う。
        /// 折りたたみ時はヘッダー1行分のみ、展開時は内部コントロールの合計を返す。
        /// </summary>
        public float GetSectionHeight()
        {
            float lineH = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            if (!exportFoldout)
                return lineH;

            // 展開時の内訳: 折りたたみヘッダ + 新規保存トグル + (新規時のみ)ファイル名 +
            //               インポート設定継承トグル + 適用ボタン(高さ32) + フォルダを開くボタン + 余白
            float h = lineH;          // foldout header
            h += lineH;               // saveAsNewFile トグル
            if (saveAsNewFile)
                h += lineH;           // ファイル名フィールド
            h += lineH;               // inheritImportSettings トグル
            h += 32f + EditorGUIUtility.standardVerticalSpacing; // ApplyAndSave ボタン
            h += lineH;               // OpenFolder ボタン
            h += 4f;                  // 末尾余白
            return h;
        }

        private void ApplyRecolor()
        {
            // 既に実行中なら無視（DisabledScope で防がれているはずだが念のため）
            if (_exportJob.IsRunning) return;

            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !VACCWindow.IsReadable(sourceTexture))
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

            // ─── 先に出力先パスと上書き確認を済ませる ───
            // 重い処理のあとでキャンセルされると計算が全て無駄になるので、
            // 確認はユーザー入力の時点（＝処理前）に行う。
            string outputPath;
            if (saveAsNewFile)
            {
                string dir = Path.GetDirectoryName(srcPath);
                string safeName = string.IsNullOrWhiteSpace(newFileName) ? "recolored" : newFileName;
                // セキュリティ: ファイル名部分のみを取得してパストラバーサルを防ぐ
                safeName = Path.GetFileName(safeName);
                // ファイル名に無効な文字を削除
                foreach (char c in Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(c.ToString(), "_");
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "recolored";
                outputPath = Path.Combine(dir, safeName + ".png");

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
                outputPath = Path.ChangeExtension(srcPath, ".png");
                if (!EditorUtility.DisplayDialog(Localization.Confirm,
                    Localization.OverwriteConfirm, Localization.Overwrite, Localization.Cancel))
                {
                    return;
                }
            }

            // ─── メインスレッド前処理: PNG 読み込み → GetPixels32 → マスクスナップショット ───
            // Texture2D API はメインスレッド必須なのでここで全て済ませ、計算本体だけ Task.Run へ渡す。
            Texture2D loadTex = null;
            Color32[] pixels;
            int texW, texH;
            try
            {
                byte[] srcBytes = File.ReadAllBytes(srcPath);
                loadTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!loadTex.LoadImage(srcBytes))
                {
                    NotifyError(Localization.TextureLoadError);
                    return;
                }
                pixels = loadTex.GetPixels32();
                texW = loadTex.width;
                texH = loadTex.height;
            }
            finally
            {
                if (loadTex != null) Object.DestroyImmediate(loadTex);
            }

            var session = _host.Session;
            var sorted = session.zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();
            var maskSnap = (sorted.Count > 0) ? _host.BuildMaskSnapshot() : null;

            // 計算に必要な値を全てローカル変数に退避（Task.Run の中から session を直接触らない）
            float edgeFeather = session.edgeFeather;
            int antiAliasCleanup = session.antiAliasCleanup;
            int holeFillPasses = session.holeFillPasses;
            int holeFillMinNeighbors = session.holeFillMinNeighbors;
            float relaxedSatMin = session.relaxedSatMin;
            float relaxedSatRamp = session.relaxedSatRamp;
            bool useDecontamination = session.useDecontamination;
            int decontaminationRadius = session.decontaminationRadius;
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
                            maskSnap, sorted, edgeFeather, antiAliasCleanup,
                            holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp,
                            0, 0, 0, 0, ct,
                            useDecontamination: useDecontamination,
                            decontaminationRadius: decontaminationRadius);
                    }
                    _exportProgress.Report(0.85f);
                    return new ExportPayload
                    {
                        pixels = pixels,
                        width = texW,
                        height = texH,
                        outputPath = outputPath,
                        srcPath = srcPath,
                        inheritImportSettings = inheritFlag,
                    };
                },
                apply: payload =>
                {
                    // メインスレッドで Texture2D を組み立てて PNG エンコード → 保存。
                    Texture2D outTex = null;
                    try
                    {
                        outTex = new Texture2D(payload.width, payload.height, TextureFormat.RGBA32, false);
                        outTex.SetPixels32(payload.pixels);
                        outTex.Apply();
                        _exportProgress.Report(0.95f);
                        byte[] pngData = outTex.EncodeToPNG();
                        if (pngData == null) return;

                        File.WriteAllBytes(payload.outputPath, pngData);
                        string relativePath = VACCWindow.ToAssetsRelative(payload.outputPath);
                        if (relativePath != null)
                            AssetDatabase.ImportAsset(relativePath);

                        if (payload.inheritImportSettings)
                            CopyImportSettings(payload.srcPath, payload.outputPath);

                        _exportProgress.Report(1.0f);
                        Debug.Log($"[VACC] Saved: {payload.outputPath}");
                        // 非モーダル通知: ファイル名のみウィンドウ右下に短時間表示。詳細パスは Debug.Log。
                        _host?.ShowNotification(new GUIContent($"{Localization.Complete}: {Path.GetFileName(payload.outputPath)}"));
                    }
                    finally
                    {
                        if (outTex != null) Object.DestroyImmediate(outTex);
                    }
                },
                onError: ex =>
                {
                    Debug.LogError($"[VACC] Export failed: {ex.Message}\n{ex.StackTrace}");
                    NotifyError(ex.Message);
                });
        }

        /// <summary>
        /// エラーを Console に出しつつ VACC ウィンドウ内に非モーダル通知を表示する。
        /// EditorUtility.DisplayDialog は Editor 全体をブロックするため避ける。
        /// </summary>
        private void NotifyError(string message)
        {
            Debug.LogError($"[VACC] {message}");
            _host?.ShowNotification(new GUIContent($"{Localization.Error}: {message}"));
        }

        /// <summary>
        /// エクスポート実行中の進捗バーとキャンセルボタンを描画する。
        /// VACCWindow.OnGUI の DisabledScope の外で呼び出すことで、ウィンドウ全体が
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

            if (GUILayout.Button(Localization.Cancel, GUILayout.Height(22)))
            {
                _exportJob.Cancel();
            }
            EditorGUILayout.Space(2);
        }

        // ─────────────────────── 一括適用 ──────────────────────────

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
                if (GUILayout.Button("×", GUILayout.Width(VACCConsts.Layout.RemoveButtonWidth)))
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

        private static void CopyImportSettings(string srcPath, string dstPath)
        {
            var srcImporter = AssetImporter.GetAtPath(srcPath) as TextureImporter;
            var dstImporter = AssetImporter.GetAtPath(dstPath) as TextureImporter;
            if (srcImporter == null || dstImporter == null) return;

            var settings = new TextureImporterSettings();
            srcImporter.ReadTextureSettings(settings);
            dstImporter.SetTextureSettings(settings);
            dstImporter.SetPlatformTextureSettings(srcImporter.GetDefaultPlatformTextureSettings());
            dstImporter.SaveAndReimport();
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
                string dir      = Path.GetDirectoryName(srcPath);
                string baseName = Path.GetFileNameWithoutExtension(srcPath) + "_recolored";
                plannedOutputs.Add(Path.Combine(dir, baseName + ".png"));
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
            var savedPairs = new List<(string src, string dst)>();
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < batchTextures.Count; i++)
                {
                    var tex = batchTextures[i];
                    if (tex == null) continue;
                    Texture2D fullTex = null;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            Localization.BatchProgress,
                            tex.name,
                            (float)i / Mathf.Max(1, batchTextures.Count)))
                    {
                        break;
                    }

                    if (!VACCWindow.IsReadable(tex)) VACCWindow.EnableReadWrite(tex);
                    if (!VACCWindow.IsReadable(tex)) continue;

                    string srcPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(srcPath)) continue;

                    try
                    {
                        byte[] srcBytes = File.ReadAllBytes(srcPath);
                        fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!fullTex.LoadImage(srcBytes)) continue;

                        Color32[] pixels = fullTex.GetPixels32();
                        int texW = fullTex.width, texH = fullTex.height;
                        var sorted = session.zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();

                        if (sorted.Count > 0)
                        {
                            var maskSnap = _host.BuildMaskSnapshot();
                            PixelProcessor.ProcessPixelsArray(pixels, texW, texH,
                                maskSnap, sorted, session.edgeFeather, session.antiAliasCleanup,
                                session.holeFillPasses, session.holeFillMinNeighbors, session.relaxedSatMin, session.relaxedSatRamp,
                                useDecontamination: session.useDecontamination,
                                decontaminationRadius: session.decontaminationRadius);
                        }

                        fullTex.SetPixels32(pixels);
                        fullTex.Apply();
                        byte[] pngData = fullTex.EncodeToPNG();

                        string dir      = Path.GetDirectoryName(srcPath);
                        string baseName = Path.GetFileNameWithoutExtension(srcPath) + "_recolored";
                        string outPath  = Path.Combine(dir, baseName + ".png");
                        File.WriteAllBytes(outPath, pngData);
                        string relOutPath = VACCWindow.ToAssetsRelative(outPath);
                        if (relOutPath != null)
                            AssetDatabase.ImportAsset(relOutPath);
                        savedPairs.Add((srcPath, outPath));
                        success++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[VACC] Batch apply failed for {tex.name}: {ex.Message}");
                    }
                    finally
                    {
                        if (fullTex != null)
                            Object.DestroyImmediate(fullTex);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            if (inheritImportSettings)
            {
                foreach (var (src, dst) in savedPairs)
                    CopyImportSettings(src, dst);
            }

            EditorUtility.DisplayDialog(Localization.Complete,
                Localization.BatchComplete(success), Localization.OK);
        }
    }
}
