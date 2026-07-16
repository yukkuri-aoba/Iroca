// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
// Assets/Iroca/Editor/Infra/BuildHelper.cs
// unitypackage エクスポート用ビルドヘルパー。
// PowerShell スクリプト (build/ExportUnityPackage.ps1) から
// Unity バッチモード (-executeMethod) 経由で呼び出される。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    public static class BuildHelper
    {
        // エクスポート対象の Assets 相対パス
        private const string ExportRoot = "Assets/Iroca";

        // Debug 衛星(Code/Debug/)は配布対象外。IrocaEditor.Debug.asmdef は defineConstraints 無し
        // ・autoReferenced=true で、同梱すると全ユーザーで常時コンパイルされ Debug ウィンドウが
        // 見えてしまう。zip 生成(Build-VpmPackage.ps1)は既にこのフォルダを除外しており、
        // unitypackage も同じ非同梱運用に揃える。
        private const string DebugFolder = "Assets/Iroca/Code/Debug";

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
                    "com.yukkuri-aoba.iroca.unitypackage");
            }

            outputPath = Path.GetFullPath(outputPath);
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Debug.Log($"[BuildHelper] エクスポート開始: {ExportRoot} → {outputPath}");

            // Code/Debug 以外の Assets/Iroca 配下アセットを明示列挙して同梱する。
            // フォルダ 1 つを Recurse で丸ごと渡すと Debug も入ってしまうため、対象パスを
            // フィルタした配列を渡し Recurse は使わない(=渡したアセットのみを厳密に同梱)。
            string debugPrefix = DebugFolder + "/";
            var included = new List<string>();
            foreach (string p in AssetDatabase.GetAllAssetPaths())
            {
                if (p != ExportRoot && !p.StartsWith(ExportRoot + "/", StringComparison.Ordinal)) continue;
                if (p == DebugFolder || p.StartsWith(debugPrefix, StringComparison.Ordinal)) continue;
                included.Add(p);
            }

            AssetDatabase.ExportPackage(
                included.ToArray(),
                outputPath,
                ExportPackageOptions.Default);

            Debug.Log($"[BuildHelper] エクスポート完了: {outputPath}（{included.Count} アセット, Code/Debug 除外）");
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
