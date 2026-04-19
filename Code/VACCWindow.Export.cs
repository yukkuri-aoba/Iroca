using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // 一括適用
        [SerializeField] private bool batchFoldout;
        [SerializeField] private List<Texture2D> batchTextures = new List<Texture2D>();
        private Vector2 batchScrollPos;

        // エクスポート
        [SerializeField] private bool exportFoldout = true;

        // ─────────────────────── エクスポート ─────────────────────────

        private void DrawExportSection()
        {
            exportFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(exportFoldout, Localization.Export);
            if (!exportFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            if (sourceTexture == null)
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
                string path = AssetDatabase.GetAssetPath(sourceTexture);
                if (!string.IsNullOrEmpty(path))
                    EditorUtility.RevealInFinder(path);
            }

            GUI.enabled = true;
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void ApplyRecolor()
        {
            if (sourceTexture == null || !IsReadable(sourceTexture))
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
            // (sourceTexture.GetPixels32() は*インポート*解像度を返します。これは
            //  インポーター設定に応じて 2048 以下にスケーリングされている可能性があります。)
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

                // ピクセル配列の取得（メインスレッド）
                Color32[] pixels = fullTex.GetPixels32();
                int texW = fullTex.width, texH = fullTex.height;
                var sorted = zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();

                // 重い計算をバックグラウンドスレッドで実行
                EditorUtility.DisplayProgressBar(Localization.ApplyAndSave, Localization.Processing, 0.3f);
                if (sorted.Count > 0)
                {
                    var task = System.Threading.Tasks.Task.Run(() =>
                        ProcessPixelsArray(pixels, texW, texH,
                            exclusionMask, maskWidth, maskHeight, sorted, edgeFeather, antiAliasCleanup,
                            holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp));
                    task.Wait();
                }

                // 結果をテクスチャに反映（メインスレッド）
                EditorUtility.DisplayProgressBar(Localization.ApplyAndSave, Localization.Export, 0.7f);
                fullTex.SetPixels32(pixels);
                fullTex.Apply();
                pngData = fullTex.EncodeToPNG();
            }
            finally
            {
                if (fullTex != null) DestroyImmediate(fullTex);
                EditorUtility.ClearProgressBar();
            }

            if (pngData == null) return;

            File.WriteAllBytes(outputPath, pngData);
            AssetDatabase.Refresh();

            if (inheritImportSettings)
                CopyImportSettings(srcPath, outputPath);

            Debug.Log($"[VACC] Saved: {outputPath}");
            EditorUtility.DisplayDialog(Localization.Complete, Localization.Saved(outputPath), Localization.OK);
        }

        // ─────────────────────── 一括適用 ──────────────────────────

        private void DrawBatchSection()
        {
            batchFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(batchFoldout, Localization.BatchApply);
            if (!batchFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(Localization.BatchHint, MessageType.Info);

            // テクスチャ一覧
            batchScrollPos = EditorGUILayout.BeginScrollView(batchScrollPos, GUILayout.MaxHeight(120));
            int removeIdx = -1;
            for (int i = 0; i < batchTextures.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                batchTextures[i] = (Texture2D)EditorGUILayout.ObjectField(
                    batchTextures[i], typeof(Texture2D), false);
                if (GUILayout.Button("×", GUILayout.Width(22)))
                    removeIdx = i;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (removeIdx >= 0) batchTextures.RemoveAt(removeIdx);

            if (GUILayout.Button(new GUIContent(Localization.AddBatchTexture, Localization.AddBatchTextureTooltip)))
                batchTextures.Add(null);

            EditorGUI.BeginDisabledGroup(batchTextures.Count == 0 || zones.Count == 0);
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
            // 既存ファイルを検出して、先にまとめて上書き確認するか判定する
            var plannedOutputs = new List<string>();
            foreach (var tex in batchTextures)
            {
                if (tex == null) continue;
                string srcPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(srcPath)) continue;
                string dir      = System.IO.Path.GetDirectoryName(srcPath);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(srcPath) + "_recolored";
                plannedOutputs.Add(System.IO.Path.Combine(dir, baseName + ".png"));
            }

            bool anyExists = plannedOutputs.Any(System.IO.File.Exists);
            if (anyExists)
            {
                // 既存ファイルがある場合はユーザーに明示的に上書き確認を取る。
                // 個別ダイアログだと件数が多いと煩雑なので、一括で判断させる。
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
                for (int i = 0; i < batchTextures.Count; i++)
                {
                    var tex = batchTextures[i];
                    if (tex == null) continue;

                    // キャンセル可能なプログレスバーに変更。長時間処理でユーザーが中断できるようにする。
                    if (EditorUtility.DisplayCancelableProgressBar(
                            Localization.BatchProgress,
                            tex.name,
                            (float)i / Mathf.Max(1, batchTextures.Count)))
                    {
                        break;
                    }

                    if (!IsReadable(tex)) EnableReadWrite(tex);
                    if (!IsReadable(tex)) continue;

                    string srcPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(srcPath)) continue;

                    byte[] srcBytes = System.IO.File.ReadAllBytes(srcPath);
                    var fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!fullTex.LoadImage(srcBytes)) { DestroyImmediate(fullTex); continue; }

                    // ピクセル配列取得（メインスレッド）→ 計算（バックグラウンド）→ 反映（メインスレッド）
                    Color32[] pixels = fullTex.GetPixels32();
                    int texW = fullTex.width, texH = fullTex.height;
                    var sorted = zones.Where(z => z.enabled).OrderBy(z => z.layerIndex).ToList();

                    if (sorted.Count > 0)
                    {
                        var task = System.Threading.Tasks.Task.Run(() =>
                            ProcessPixelsArray(pixels, texW, texH,
                                exclusionMask, maskWidth, maskHeight, sorted, edgeFeather, antiAliasCleanup,
                                holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp));
                        task.Wait();
                    }

                    fullTex.SetPixels32(pixels);
                    fullTex.Apply();
                    byte[] pngData = fullTex.EncodeToPNG();
                    DestroyImmediate(fullTex);

                    string dir      = System.IO.Path.GetDirectoryName(srcPath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(srcPath) + "_recolored";
                    string outPath  = System.IO.Path.Combine(dir, baseName + ".png");
                    System.IO.File.WriteAllBytes(outPath, pngData);
                    savedPairs.Add((srcPath, outPath));
                    success++;
                }
            }
            finally
            {
                // finally で確実にプログレスバーをクリアする（例外でも閉じ忘れないように）
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();

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
