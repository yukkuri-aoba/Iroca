// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

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
#if UNITY_6000_0_OR_NEWER
        // Unity 6 では com.unity.sentis は中継だけのパッケージ(type: shim)で、実体は com.unity.ai.inference
        // (Inference Engine, 名前空間 Unity.InferenceEngine)。中継を指定しても 2.1.x は入らない。
        public const string SentisPackageId = "com.unity.ai.inference";
#else
        public const string SentisPackageId = "com.unity.sentis";
#endif

        /// <summary>Sentis 導入直後に「再起動推奨」を出すためのフラグ(SessionState キー)。
        /// SessionState はドメインリロードを跨いで残り、エディタ再起動で消える = 再起動するまで表示。</summary>
        public const string RestartRecommendedKey = "Iroca.Sentis.RestartRecommended";

        // asmdef の versionDefine 範囲に収まる検証済みバージョンを固定導入する。
        // 2022.3 は Sentis [2.0.0,2.2.0)、Unity 6 は Inference Engine [2.2.0,3.0.0)。
        // (配布モデルの ONNX もこの版でエクスポート・検証している。版を上げるときはここと
        //  Iroca.SentisIntegration.asmdef の versionDefines、マニュアルの記載を揃える)
#if UNITY_6000_0_OR_NEWER
        public const string SentisPackageVersion = "2.4.1";
#else
        public const string SentisPackageVersion = "2.1.3";
#endif

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

        const string BarracudaPackageId = "com.unity.barracuda";
        static string[] s_onnxConflicts;

        /// <summary>
        /// 導入すると .onnx の取り込みがぶつかるパッケージ(表示用の名前。無ければ空)。
        ///
        /// Unity は 1 つの拡張子を受け持つ取り込み(ScriptedImporter)が 2 つ以上あると、どれも使わなくなる。
        /// Sentis も Barracuda(Sentis の前身)も .onnx を受け持つので、Barracuda を使うツールが入ったプロジェクトに
        /// Sentis を足すと、そのツールの AI モデルも、いろかの AI(モデルの変換に .onnx の取り込みを使う)も動かなくなる。
        /// .onnx を受け持つ取り込みを型から探すので、Unity 6 の実体のある com.unity.sentis も数え、中継だけの
        /// com.unity.sentis(取り込みを持たない)は数えない。差し替えになる同じ名前のパッケージ
        /// (<see cref="SentisPackageId"/>)は数えない(版の差し替えの案内が別に出る)。
        /// パッケージが変わるとドメインがリロードされるので、結果はドメインごとに 1 回だけ調べる。
        /// </summary>
        public static IReadOnlyList<string> OnnxImporterConflicts()
        {
            if (s_onnxConflicts != null) return s_onnxConflicts;
            var found = new List<string>();
            try
            {
                foreach (var owner in OnnxImporterOwners())
                    if (owner.Key != SentisPackageId) found.Add(owner.Value);
            }
            catch { /* 調べられないだけで導入の導線は塞がない */ }
            return s_onnxConflicts = found.ToArray();
        }

        /// <summary>
        /// .onnx を受け持つ取り込みを持つパッケージ(名前 → 表示用の名前。パッケージの外なら名前はアセンブリ名)。
        /// </summary>
        internal static SortedDictionary<string, string> OnnxImporterOwners()
        {
            var owners = new SortedDictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var t in TypeCache.GetTypesWithAttribute<ScriptedImporterAttribute>())
            {
                foreach (ScriptedImporterAttribute a in t.GetCustomAttributes(typeof(ScriptedImporterAttribute), false))
                {
                    if (!TargetsOnnx(a.fileExtensions)) continue;
                    var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(t.Assembly);
                    if (pkg != null) owners[pkg.name] = $"{pkg.displayName} ({pkg.name})";
                    else owners[t.Assembly.GetName().Name] = t.Assembly.GetName().Name;
                }
            }
            // 取り込みの型がコンパイルできていなくても、Barracuda が入っていればぶつかる。
            foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                if (p != null && p.name == BarracudaPackageId) owners[p.name] = $"{p.displayName} ({p.name})";
            return owners;
        }

        static bool TargetsOnnx(string[] extensions)
        {
            if (extensions == null) return false;
            foreach (string e in extensions)
                if (e != null && string.Equals(e.TrimStart('.'), "onnx", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// .onnx の取り込みがぶつかるパッケージが入っていれば、導入する前に確認画面で伝える。ぶつかるものが
        /// 無ければ何も出さずに true。既定のボタン(Enter)と閉じる(Esc)は「導入しない」側にしてある。
        /// 戻り値 = 導入してよいか。
        /// </summary>
        public static bool ConfirmDespiteOnnxConflicts()
        {
            var conflicts = OnnxImporterConflicts();
            if (conflicts.Count == 0) return true;
            if (Application.isBatchMode) return false;
            string body = string.Format(Localization.AiSetupOnnxConflictBody,
                string.Join(Localization.AiOnnxConflictSeparator, conflicts), SentisPackageVersion);
            string replaced = VersionThatWouldBeReplaced();
            if (replaced != null)
                body += "\n\n" + string.Format(Localization.AiSuggestSentisVersionReplace, replaced, SentisPackageVersion);
            // DisplayDialogComplex: 0 = ok(既定), 1 = cancel(Esc・閉じる), 2 = alt。
            int choice = EditorUtility.DisplayDialogComplex(Localization.AiSetupTitle, body,
                Localization.AiSetupDontInstall, Localization.Cancel, Localization.AiSetupInstallAnyway);
            return choice == 2;
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
                Error = _request.Error != null ? _request.Error.message : Localization.AiUnknownError;
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
