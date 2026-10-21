// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using UnityEngine;

namespace Iroca
{
    // Assets 配下のパスを "Assets/..." 形式の相対パスへ正規化する純粋ユーティリティ。
    // 永続化(PresetStore/RecipeStore)・自動化(IrocaAutomation)・エクスポート(ExportPipeline/ExportView)・
    // メッシュ探索(MeshUvLocator)と scripts/unit-run が
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

        /// <summary>
        /// いろかの書き出し名の接尾辞（&lt;元の名前&gt;_recolored.png）。書き出し（ExportPipeline / ExportView）と、
        /// 書き出し物から元へ辿る探索（MeshUvLocator）が共有する。片側だけ変えると非破壊登録やメッシュ探索が
        /// 黙って外れるため 1 か所に置く。表示文言（Localization・NdmfMessages）にも同じ綴りがある。
        /// </summary>
        internal const string RecoloredSuffix = "_recolored";

        /// <summary>
        /// 元テクスチャが Assets の外（VCC / ALCOM で入れたパッケージの Packages/ など）にあるときの書き出し先。
        /// パッケージのフォルダは更新・入れ直しで丸ごと入れ替わり、中に書いた画像も消えるため。
        /// </summary>
        internal const string ExportsFolder = "Assets/Iroca/Exports";

        /// <summary>
        /// 書き出し（&lt;元の名前&gt;_recolored.png など）を置くフォルダ（'/' 区切り）。元が Assets の中なら元と同じフォルダ。
        /// 外なら <see cref="ExportsFolder"/> の下に元のフォルダの並びを写す
        /// （Packages/com.example/Textures → Assets/Iroca/Exports/Packages/com.example/Textures）。写しておくと、
        /// 書き出した画像から元を辿れる（<see cref="SourceFolderOfExport"/>）。単体・一括・自動化 API の書き出しが共有する。
        /// </summary>
        internal static string ExportFolderFor(string srcPath)
        {
            string dir = (Path.GetDirectoryName(srcPath ?? "") ?? "").Replace('\\', '/');
            if (dir == "Assets" || dir.StartsWith("Assets/", StringComparison.Ordinal)) return dir;
            // アセットのパスでないもの（絶対パス・フォルダ無し）は従来どおり元と同じ場所。
            if (dir.Length == 0 || Path.IsPathRooted(dir)) return dir;
            if (Array.IndexOf(dir.Split('/'), "..") >= 0) return ExportsFolder;
            return ExportsFolder + "/" + dir;
        }

        /// <summary>
        /// <see cref="ExportFolderFor"/> の逆: 書き出しのフォルダから、元テクスチャのフォルダを返す。
        /// Assets の外の元を写したフォルダでなければ、そのまま返す。
        /// </summary>
        internal static string SourceFolderOfExport(string exportDir)
        {
            string dir = (exportDir ?? "").Replace('\\', '/');
            string prefix = ExportsFolder + "/";
            if (!dir.StartsWith(prefix, StringComparison.Ordinal)) return dir;
            string rest = dir.Substring(prefix.Length);
            // 写すのは Assets の外の元だけ。Assets/Iroca/Exports/Assets/... は利用者が作ったフォルダ。
            return rest == "Assets" || rest.StartsWith("Assets/", StringComparison.Ordinal) ? dir : rest;
        }
    }
}
