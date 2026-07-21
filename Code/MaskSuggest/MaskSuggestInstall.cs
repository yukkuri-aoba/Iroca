// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案が必要とする Unity Sentis パッケージを、ユーザーのボタン操作で
    /// Package Manager 経由でワンクリック導入する。
    ///
    /// 本体アセンブリ側(Sentis 不在でも常にコンパイルされる)に置くのが要点。これが無いと
    /// Sentis を手動導入した開発環境でしか AI 機能に到達できず、「Code をそのまま入れた素の
    /// プロジェクトでは機能が見えない」状態になる。
    ///
    /// 導入に成功すると Unity が自動でドメインリロード＋リコンパイルし、
    /// IROCA_SENTIS_PRESENT が定義されて Iroca.SentisIntegration がサービスを登録する(= 機能 ON)。
    /// </summary>
    internal static class MaskSuggestInstall
    {
        public const string SentisPackageId = "com.unity.sentis";

        // asmdef の versionDefine 範囲 [2.0.0,3.0.0) に収まる検証済みバージョンを固定導入する。
        // (配布モデルの ONNX もこの版でエクスポート・検証している。版を上げるときはここと
        //  Iroca.SentisIntegration.asmdef の versionDefines、MANUAL の記載を揃える)
        public const string SentisPackageVersion = "2.1.3";

        static AddRequest _request;

        /// <summary>導入処理が進行中か。</summary>
        public static bool InProgress => _request != null && !_request.IsCompleted;

        /// <summary>直近の導入失敗メッセージ(成功・未実行時は null)。</summary>
        public static string Error { get; private set; }

        /// <summary>Sentis の導入を開始する(進行中なら no-op)。完了/失敗は状態プロパティで観測する。</summary>
        public static void StartInstall()
        {
            if (InProgress) return;
            Error = null;
            _request = Client.Add(SentisPackageId + "@" + SentisPackageVersion);
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (_request == null)
            {
                EditorApplication.update -= Tick;
                return;
            }
            if (!_request.IsCompleted) return;

            if (_request.Status == StatusCode.Failure)
                Error = _request.Error != null ? _request.Error.message : "unknown error";
            // 成功時はここでの後処理は不要: パッケージ追加により Unity が自動で
            // リコンパイルし、Sentis 統合アセンブリが [InitializeOnLoad] でサービスを登録する。

            _request = null;
            EditorApplication.update -= Tick;
        }
    }
}
