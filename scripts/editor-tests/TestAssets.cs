// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// テストが作る一時アセットの置き場。各テストは <see cref="Create"/> で自分専用のフォルダを作り、
    /// TearDown で <see cref="Dispose"/> する（ホストプロジェクトに痕跡を残さない）。
    /// </summary>
    internal sealed class TestAssets : IDisposable
    {
        public readonly string Folder;   // "Assets/IrocaEditorTests_Temp/<id>"

        private const string Root = "Assets/IrocaEditorTests_Temp";

        private TestAssets(string folder) { Folder = folder; }

        public static TestAssets Create()
        {
            if (!AssetDatabase.IsValidFolder(Root))
                AssetDatabase.CreateFolder("Assets", "IrocaEditorTests_Temp");
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            AssetDatabase.CreateFolder(Root, id);
            return new TestAssets(Root + "/" + id);
        }

        public static string Abs(string assetPath)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        /// <summary>画素をそのまま PNG にしてアセットとして取り込む。</summary>
        public string WritePng(string name, Color32[] pixels, int w, int h, bool readable = false)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(pixels);
                string path = Folder + "/" + name + ".png";
                File.WriteAllBytes(Abs(path), tex.EncodeToPNG());
                Import(path, readable);
                return path;
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        /// <summary>任意バイト列をファイルとして置いて取り込む（PNG 以外の形式を作る用）。</summary>
        public string WriteBytes(string fileName, byte[] bytes, bool readable = false)
        {
            string path = Folder + "/" + fileName;
            File.WriteAllBytes(Abs(path), bytes);
            Import(path, readable);
            return path;
        }

        public static void Import(string path, bool readable)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null && imp.isReadable != readable)
            {
                imp.isReadable = readable;
                imp.SaveAndReimport();
            }
        }

        public static Color32[] Solid(int w, int h, Color32 c)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            return px;
        }

        public void Dispose()
        {
            AssetDatabase.DeleteAsset(Folder);
            // 最後の 1 つならルートも消す（他テストが並行して使っていなければ空になっている）。
            if (AssetDatabase.IsValidFolder(Root) && AssetDatabase.FindAssets("", new[] { Root }).Length == 0)
                AssetDatabase.DeleteAsset(Root);
        }
    }
}
