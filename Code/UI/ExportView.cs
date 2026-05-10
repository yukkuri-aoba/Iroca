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

        public void Initialize(VACCWindow host)
        {
            _host = host;
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

            if (_host.SourceTexture == null)
            {
                GUI.enabled = false;
            }

            saveAsNewFile = EditorGUILayout.Toggle(
                new GUIContent(Localization.SaveAsNewFile, Localization.SaveAsNewFileTooltip),
                saveAsNewFile);
            if (saveAsNewFile)
            {
                newFileName = EditorGUILayout.TextField(
                    new GUIContent(Localization.FileName, Localization.FileNameTooltip),
                    newFileName);
            }

            inheritImportSettings = EditorGUILayout.Toggle(
                new GUIContent(Localization.InheritImportSettings, Localization.InheritImportSettingsTooltip),
                inheritImportSettings);

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

            GUI.enabled = true;
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        public void SetSourceTextureBaseName(string baseNameWithoutExtension)
        {
            newFileName = baseNameWithoutExtension + "_recolored";
        }

        private void ApplyRecolor()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !VACCWindow.IsReadable(sourceTexture))
            {
                EditorUtility.DisplayDialog(Localization.Error, Localization.TextureReadError, Localization.OK);
                return;
            }

            string srcPath = AssetDatabase.GetAssetPath(sourceTexture);
            if (string.IsNullOrEmpty(srcPath))
            {
                EditorUtility.DisplayDialog(Localization.Error, Localization.PathNotFound, Localization.OK);
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

            // ─── 実処理 ───
            // ディスク上のファイルから元のフル解像度で直接読み込む、
            // Unity の TextureImporter maxTextureSize / 圧縮設定をバイパス。
            Texture2D fullTex = null;
            byte[] pngData = null;
            try
            {
                EditorUtility.DisplayProgressBar(Localization.ApplyAndSave, Localization.Processing, 0.1f);

                byte[] srcBytes = File.ReadAllBytes(srcPath);
                fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!fullTex.LoadImage(srcBytes))
                {
                    EditorUtility.DisplayDialog(Localization.Error, Localization.TextureLoadError, Localization.OK);
                    return;
                }

                Color32[] pixels = fullTex.GetPixels32();
                int texW = fullTex.width, texH = fullTex.height;
                var session = _host.Session;
                var sorted = session.zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();

                EditorUtility.DisplayProgressBar(Localization.ApplyAndSave, Localization.Processing, 0.3f);
                if (sorted.Count > 0)
                {
                    var maskSnap = _host.BuildMaskSnapshot();
                    PixelProcessor.ProcessPixelsArray(pixels, texW, texH,
                        maskSnap, sorted, session.edgeFeather, session.antiAliasCleanup,
                        session.holeFillPasses, session.holeFillMinNeighbors, session.relaxedSatMin, session.relaxedSatRamp,
                        useDecontamination: session.useDecontamination,
                        decontaminationRadius: session.decontaminationRadius);
                }

                EditorUtility.DisplayProgressBar(Localization.ApplyAndSave, Localization.Export, 0.7f);
                fullTex.SetPixels32(pixels);
                fullTex.Apply();
                pngData = fullTex.EncodeToPNG();
            }
            finally
            {
                if (fullTex != null) Object.DestroyImmediate(fullTex);
                EditorUtility.ClearProgressBar();
            }

            if (pngData == null) return;

            File.WriteAllBytes(outputPath, pngData);
            string relativePath = VACCWindow.ToAssetsRelative(outputPath);
            if (relativePath != null)
                AssetDatabase.ImportAsset(relativePath);

            if (inheritImportSettings)
                CopyImportSettings(srcPath, outputPath);

            Debug.Log($"[VACC] Saved: {outputPath}");
            EditorUtility.DisplayDialog(Localization.Complete, Localization.Saved(outputPath), Localization.OK);
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
