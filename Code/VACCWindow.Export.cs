using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public partial class VACCWindow
    {
        // Batch apply
        private bool batchFoldout;
        private List<Texture2D> batchTextures = new List<Texture2D>();
        private Vector2 batchScrollPos;

        // Export
        private bool exportFoldout = true;

        // ───────────────────────── Export ─────────────────────────

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

            saveAsNewFile = EditorGUILayout.Toggle(Localization.SaveAsNewFile, saveAsNewFile);
            if (saveAsNewFile)
            {
                newFileName = EditorGUILayout.TextField(Localization.FileName, newFileName);
            }

            if (GUILayout.Button(Localization.ApplyAndSave, GUILayout.Height(32)))
            {
                ApplyRecolor();
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

            // Load at full original resolution directly from the file on disk,
            // bypassing Unity's TextureImporter maxTextureSize / compression settings.
            // (sourceTexture.GetPixels32() returns the *imported* resolution which may be
            //  scaled down to 2048 or lower depending on importer settings.)
            byte[] srcBytes = File.ReadAllBytes(srcPath);
            var fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!fullTex.LoadImage(srcBytes))
            {
                DestroyImmediate(fullTex);
                EditorUtility.DisplayDialog(Localization.Error, Localization.TextureLoadError, Localization.OK);
                return;
            }

            ProcessPixels(fullTex);
            fullTex.Apply();

            byte[] pngData = fullTex.EncodeToPNG();
            DestroyImmediate(fullTex);

            string outputPath;
            if (saveAsNewFile)
            {
                string dir = Path.GetDirectoryName(srcPath);
                string safeName = string.IsNullOrWhiteSpace(newFileName) ? "recolored" : newFileName;
                // Security: prevent path traversal by taking only the filename portion
                safeName = Path.GetFileName(safeName);
                // Strip characters that are invalid in file names
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

        // ───────────────────────── Batch Apply ──────────────────────────

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

            if (GUILayout.Button(Localization.AddBatchTexture))
                batchTextures.Add(null);

            EditorGUI.BeginDisabledGroup(batchTextures.Count == 0 || zones.Count == 0);
            if (GUILayout.Button(Localization.BatchApplyAndSave, GUILayout.Height(28)))
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

                ProcessPixels(fullTex);
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
