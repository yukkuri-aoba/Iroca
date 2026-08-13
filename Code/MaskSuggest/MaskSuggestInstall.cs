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

        /// <summary>Sentis 導入直後に「再起動推奨」を出すためのフラグ(SessionState キー)。
        /// SessionState はドメインリロードを跨いで残り、エディタ再起動で消える = 再起動するまで表示。</summary>
        public const string RestartRecommendedKey = "Iroca.Sentis.RestartRecommended";

        // asmdef の versionDefine 範囲 [2.0.0,3.0.0) に収まる検証済みバージョンを固定導入する。
        // (配布モデルの ONNX もこの版でエクスポート・検証している。版を上げるときはここと
        //  Iroca.SentisIntegration.asmdef の versionDefines、MANUAL の記載を揃える)
        public const string SentisPackageVersion = "2.1.3";

        static AddRequest _request;

        /// <summary>導入処理が進行中か。</summary>
        public static bool InProgress => _request != null && !_request.IsCompleted;

        /// <summary>
        /// 導入ボタンが「既存 Sentis の版の差し替え」になる場合、その導入済み版を返す
        /// (未導入、または既に <see cref="SentisPackageVersion"/> と同じ版なら null)。
        ///
        /// 導入導線は「Sentis 統合アセンブリが不在」= IROCA_SENTIS_PRESENT 未定義で出るが、
        /// これは未導入だけでなく asmdef の versionDefines 範囲 [2.0.0,3.0.0) を外れた版
        /// (1.x や将来の 3.x)が既に入っている場合も含む。その状態で Add すると、他のツールの
        /// ために手動導入された Sentis を黙って差し替えてしまう。押す前に伝えるための判定。
        /// </summary>
        public static string VersionThatWouldBeReplaced()
        {
            // PackageInfo は UnityEditor 直下の同名(旧 AssetStore 用)と衝突するので完全修飾する。
            UnityEditor.PackageManager.PackageInfo found = null;
            try
            {
                foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (p != null && p.name == SentisPackageId) { found = p; break; }
                }
            }
            catch { return null; }  // 情報が取れないだけで導入導線は塞がない
            if (found == null || string.IsNullOrEmpty(found.version)) return null;
            return found.version == SentisPackageVersion ? null : found.version;
        }

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
            else if (_request.Status == StatusCode.Success)
                // 導入自体はパッケージ追加→自動リコンパイルで完了するが、その導入時のドメイン
                // リロードでは Burst のコールドスタート失敗(関数ポインタ初期化例外)を踏むことがあり、
                // その世代だけ Sentis の畳み込みが全滅して AI が動かない。確実に有効化するため
                // 再起動を促すフラグを立てる(クリーンな Burst 初期化になる)。
                SessionState.SetBool(RestartRecommendedKey, true);

            _request = null;
            EditorApplication.update -= Tick;
        }
    }
}
