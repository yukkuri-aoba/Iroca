using UnityEngine;

namespace Iroca
{
    // Assets 配下のパスを "Assets/..." 形式の相対パスへ正規化する純粋ユーティリティ。
    // 永続化(PresetStore/MaskFileStore)・自動化(IrocaAutomation)・エクスポート(ExportView)が
    // 共通で使うため、UI ウィンドウ(IrocaWindow)から切り離して Infra に置く。これにより
    // 下位層(Infra/Automation)が UI へ逆依存しなくなる(architecture_review_2026-06-27 §2.2-1)。
    // UnityEditor 非依存(Application.dataPath は UnityEngine)なので headless でも参照できる。
    internal static class PathUtils
    {
        // Assets 配下のパスを "Assets/..." 形式の相対パスに正規化する。
        // 既に "Assets/" で始まる相対パスでも、絶対パスでも受け付ける。
        // Assets 配下でない場合は null を返す。
        internal static string ToAssetsRelativeOrNull(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/") || normalized == "Assets")
                return normalized;
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(dataPath + "/"))
                return "Assets" + normalized.Substring(dataPath.Length);
            return null;
        }
    }
}
