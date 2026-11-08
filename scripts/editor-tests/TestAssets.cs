// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// テストが作る一時アセットの置き場。各テストは <see cref="Create"/> で自分専用のフォルダを作り、
    /// TearDown で <see cref="Dispose"/> する（ホストプロジェクトに痕跡を残さない）。
    /// </summary>
    internal sealed class TestAssets : IDisposable {
        public readonly string Folder;   // "Assets/IrocaEditorTests_Temp/<id>"

        private const string Root = "Assets/IrocaEditorTests_Temp";

        private TestAssets(string folder) { Folder = folder; }

        /// <summary>
        /// 原本の画素を管理配列で読む(メインスレッド専用)。製品はヒープの外へ読むので
        /// (<see cref="ExportPipeline.ReadWorkingPixelsNative"/>・<see cref="ExportService.ReadSource"/>)、
        /// これは GetPixels32 の値と比べる基準と、配列のほうが書きやすいテストのためのもの。
        /// </summary>
        public static ExportPipeline.SourceKind ReadSourcePixels(string srcPath, Texture2D imported,
            out Color32[] pixels, out int width, out int height, bool workingSize = false) {
            pixels = null; width = height = 0;
            Texture2D loadTex = null;
            try {
                byte[] srcBytes = File.ReadAllBytes(srcPath);
                loadTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (loadTex.LoadImage(srcBytes)) {
                    if (workingSize) {
                        pixels = WorkingResolution.PixelsOf(loadTex, imported, out width, out height);
                    } else {
                        pixels = loadTex.GetPixels32();
                        width = loadTex.width;
                        height = loadTex.height;
                    }
                    return ExportPipeline.SourceKind.File;
                }
            } finally {
                if (loadTex != null) {
                    UnityEngine.Object.DestroyImmediate(loadTex);
                }
            }
            if (!TextureIO.IsReadable(imported)) {
                return ExportPipeline.SourceKind.Unavailable;
            }
            pixels = imported.GetPixels32();
            width = imported.width;
            height = imported.height;
            return ExportPipeline.SourceKind.ImportedTexture;
        }

        public static TestAssets Create() {
            if (!AssetDatabase.IsValidFolder(Root)) {
                AssetDatabase.CreateFolder("Assets", "IrocaEditorTests_Temp");
            }
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            AssetDatabase.CreateFolder(Root, id);
            return new TestAssets(Root + "/" + id);
        }

        public static string Abs(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        /// <summary>画素をそのまま PNG にしてアセットとして取り込む。</summary>
        public string WritePng(string name, Color32[] pixels, int w, int h, bool readable = false) {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try {
                tex.SetPixels32(pixels);
                string path = Folder + "/" + name + ".png";
                File.WriteAllBytes(Abs(path), tex.EncodeToPNG());
                Import(path, readable);
                return path;
            } finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        /// <summary>任意バイト列をファイルとして置いて取り込む（PNG 以外の形式を作る用）。</summary>
        public string WriteBytes(string fileName, byte[] bytes, bool readable = false) {
            string path = Folder + "/" + fileName;
            File.WriteAllBytes(Abs(path), bytes);
            Import(path, readable);
            return path;
        }

        public static void Import(string path, bool readable) {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null && imp.isReadable != readable) {
                imp.isReadable = readable;
                imp.SaveAndReimport();
            }
        }

        /// <summary>
        /// メインスレッドへ戻ってくる非同期処理を待つ。同期のテストの間は EditorApplication.update が回らないので、
        /// 戻り先(<see cref="PreviewJobMainThread"/>)をここで回す。
        /// </summary>
        public static T Wait<T>(Task<T> task, int timeoutMs = 30000) {
            var sw = Stopwatch.StartNew();
            while (!task.IsCompleted) {
                if (sw.ElapsedMilliseconds > timeoutMs) {
                    throw new TimeoutException("非同期処理が終わらない");
                }
                PreviewJobMainThread.Drain();
                Thread.Sleep(2);
            }
            return task.Result;
        }

        public static Color32[] Solid(int w, int h, Color32 c) {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) {
                px[i] = c;
            }
            return px;
        }

        public void Dispose() {
            AssetDatabase.DeleteAsset(Folder);
            // 最後の 1 つならルートも消す（他テストが並行して使っていなければ空になっている）。
            if (AssetDatabase.IsValidFolder(Root) && AssetDatabase.FindAssets("", new[] { Root }).Length == 0) {
                AssetDatabase.DeleteAsset(Root);
            }
        }
    }
}
