// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マニュアルを開く。パッケージに同梱した 1 枚の HTML(Documentation~/manual.html)を既定のブラウザで開き、
    /// 無ければ(リポジトリから直接入れた開発環境など)オンライン版を開く。
    /// <para>
    /// 同梱版はインストールした版の説明なので、オンライン版(最新)より優先する。
    /// HTML は docs/manual/build_manual.py が作り、scripts/Build-VpmPackage.ps1 が zip に入れる。
    /// Documentation~ は末尾の ~ で Unity の取り込み対象外になるので、.meta もインポートも増えない。
    /// </para>
    /// </summary>
    internal static class IrocaManual
    {
        public const string OnlineUrl = "https://yukkuri-aoba.github.io/Iroca/manual/";
        const string BundledRelativePath = "Documentation~/manual.html";

        /// <summary>同梱のマニュアルの絶対パス。見つからなければ null。</summary>
        public static string BundledPath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(IrocaManual).Assembly);
            if (info == null || string.IsNullOrEmpty(info.resolvedPath)) return null;
            var path = Path.GetFullPath(Path.Combine(info.resolvedPath, BundledRelativePath));
            return File.Exists(path) ? path : null;
        }

        public static void Open()
        {
            var bundled = BundledPath();
            // ローカルのファイルは URL に組み立てず、パスのまま既定のアプリ(ブラウザ)に渡す。
            if (bundled != null) EditorUtility.OpenWithDefaultApp(bundled);
            else Application.OpenURL(OnlineUrl);
        }
    }
}
