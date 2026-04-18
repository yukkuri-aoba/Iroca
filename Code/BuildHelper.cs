// Assets/VACC/Editor/BuildHelper.cs
// unitypackage エクスポート用ビルドヘルパー。
// PowerShell スクリプト (build/ExportUnityPackage.ps1) から
// Unity バッチモード (-executeMethod) 経由で呼び出される。

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public static class BuildHelper
    {
        // エクスポート対象の Assets 相対パス
        private const string ExportRoot = "Assets/VACC";

        /// <summary>
        /// バッチモードからのエントリポイント。
        /// コマンドライン引数 -outputPath で出力先を指定できる。
        /// </summary>
        public static void Export()
        {
            string outputPath = GetArgValue("-outputPath");
            if (string.IsNullOrEmpty(outputPath))
            {
                // デフォルト出力先（プロジェクトルート）
                outputPath = Path.Combine(
                    Application.dataPath, "..",
                    "com.yukkuri-aoba.vrc-avatar-color-changer.unitypackage");
            }

            outputPath = Path.GetFullPath(outputPath);
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Debug.Log($"[BuildHelper] エクスポート開始: {ExportRoot} → {outputPath}");

            AssetDatabase.ExportPackage(
                ExportRoot,
                outputPath,
                ExportPackageOptions.Recurse);

            Debug.Log($"[BuildHelper] エクスポート完了: {outputPath}");
        }

        // コマンドライン引数から値を取得するユーティリティ
        private static string GetArgValue(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
