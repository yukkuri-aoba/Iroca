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
        private bool batchFoldout;
        private List<Texture2D> batchTextures = new List<Texture2D>();
        private Vector2 batchScrollPos;

        // エクスポート
        private bool exportFoldout = true;

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

            // ディスク上のファイルから元のフル解像度で直接読み込む、
            // Unity の TextureImporter maxTextureSize / 圧縮設定をバイパス。
            // (sourceTexture.GetPixels32() は*インポート*解像度を返します。これは
            //  インポーター設定に応じて 2048 以下にスケーリングされている可能性があります。)
            EditorUtility.DisplayProgressBar(Localization.ApplyAndSave, Localization.Processing, 0.1f);

            byte[] srcBytes = File.ReadAllBytes(srcPath);
            var fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!fullTex.LoadImage(srcBytes))
            {
                EditorUtility.ClearProgressBar();
                DestroyImmediate(fullTex);
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

            byte[] pngData = fullTex.EncodeToPNG();
            DestroyImmediate(fullTex);
            EditorUtility.ClearProgressBar();

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

            File.WriteAllBytes(outputPath, pngData);
            AssetDatabase.Refresh();

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

        private void RunBatchApply()
        {
            int success = 0;
            for (int i = 0; i < batchTextures.Count; i++)
            {
                var tex = batchTextures[i];
                if (tex == null) continue;

                EditorUtility.DisplayProgressBar(
                    Localization.BatchProgress,
                    tex.name,
                    (float)i / batchTextures.Count);

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
                success++;
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(Localization.Complete, Localization.BatchComplete(success), Localization.OK);
        }
    }
}
