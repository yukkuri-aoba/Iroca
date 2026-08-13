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
        {
            if (string.IsNullOrEmpty(path)) return null;

            string dataPath = Application.dataPath.Replace('\\', '/').TrimEnd('/');
            string projectRoot = Path.GetDirectoryName(Application.dataPath);

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
    }
}
