// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using UnityEngine;

namespace Iroca
{
    // Assets 配下のパスを "Assets/..." 形式の相対パスへ正規化する純粋ユーティリティ。
    // 永続化(PresetStore/MaskFileStore)・自動化(IrocaAutomation)・エクスポート(ExportView)が
    // 共通で使うため、UI ウィンドウ(IrocaWindow)から切り離して Infra に置く。これにより
    // 下位層(Infra/Automation)が UI へ逆依存しなくなる。
    // UnityEditor 非依存(Application.dataPath は UnityEngine)なので headless でも参照できる。
    internal static class PathUtils
    {
        // Assets 配下のパスを "Assets/..." 形式の相対パスに正規化する。
        // 既に "Assets/" で始まる相対パスでも、絶対パスでも受け付ける。
        // Assets 配下でない場合は null を返す。
        //
        // 判定は「絶対化してから Assets フォルダ配下かを見る」方式。以前は文字列の前方一致だけで
        // 判定しており、次の 2 つの穴があった（レビュー 2026-08-06 §5 中）:
        //   - ".." を正規化しないため "Assets/../外部ファイル" が Assets 配下として通っていた
        //   - StartsWith がカルチャ依存かつ大小区別で、Windows のドライブレター大小揺れ
        //     （C:/ と c:/）で Assets 配下なのに null を返すことがあった
        // 大小無視の Ordinal 比較は IrocaAutomation.IsWithinProjectRoot と同じ基準に揃えたもの。
        internal static string ToAssetsRelativeOrNull(string path)
            => ToAssetsRelativeOrNull(path, Application.dataPath);

        // 判定本体。Assets フォルダの絶対パスを引数で受け取るので Unity なしで検証できる
        // （scripts/unit-run が実行する）。
        internal static string ToAssetsRelativeOrNull(string path, string assetsFolder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(assetsFolder)) return null;

            string dataPath = assetsFolder.Replace('\\', '/').TrimEnd('/');
            string projectRoot = Path.GetDirectoryName(dataPath);

            string abs;
            try
            {
                // 相対パスはプロジェクトルート基準（Unity の作業ディレクトリと同じ）で絶対化する。
                abs = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(projectRoot, path));
            }
            catch (Exception)
            {
                // 不正な文字・長すぎるパスなど。Assets 配下と判定できないので null。
                return null;
            }
            abs = abs.Replace('\\', '/').TrimEnd('/');

            if (string.Equals(abs, dataPath, StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (abs.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                return "Assets" + abs.Substring(dataPath.Length);
            return null;
        }

        // Windows の予約デバイス名(拡張子有無・大小無視で衝突する)。これらの名前のファイルは
        // そのままでは作成に失敗するため、前置 '_' で退避する。
        private static readonly string[] s_reservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// ユーザーが入力した名前を、同じフォルダに作れる 1 つのファイル名（拡張子なし）へ直す。
        /// 区切り文字も不正文字として置換するので、".." や "a/b" でフォルダの外へ出られない。
        /// 空になったら <paramref name="fallback"/> を使う。プリセット保存とエクスポートの共通規則。
        /// </summary>
        internal static string SanitizeFileName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) name = fallback;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");
            // Windows 以外では '\' が不正文字に入らないが、Windows で開くと区切りになる。
            name = name.Replace('\\', '_');
            // Windows は末尾の '.' / ' ' を無言で除去するため、そのままだと保存名と参照名がずれる。
            name = name.TrimEnd('.', ' ');
            if (name.Length == 0) name = fallback;
            // 予約名判定は拡張子より前の基底名で行う(例: "CON.foo" も予約)。
            int dot = name.IndexOf('.');
            string baseName = dot >= 0 ? name.Substring(0, dot) : name;
            if (Array.IndexOf(s_reservedNames, baseName.ToUpperInvariant()) >= 0)
                name = "_" + name;
            return name;
        }
    }
}
