// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// ドメインリロード直後に出る Burst のコンパイル失敗を捕まえて、セッションフラグに残す。
    ///
    /// Burst のコールドスタートに失敗した世代では、Sentis の推論カーネルが実行されず
    /// 「AI 提案は完走するのにマスクへ 1 画素も足されない」という分かりにくい壊れ方をする
    /// (エラーも出ないので、ユーザーからは「AI が動かない」としか見えない)。この状態は
    /// 同じドメインでは直らず、Unity の再起動でしか回復しない。
    ///
    /// 失敗ログは型初期化子の例外として [InitializeOnLoadMethod] の処理中に出る。
    /// Unity は [InitializeOnLoad] を先に処理するので、ここで購読しておけば取りこぼさない。
    /// 監視は最初の update までの一瞬だけで、以降はフックを外す。
    /// SessionState はエディタ再起動で消える = 再起動すれば警告も自然に消える。
    /// </summary>
    [InitializeOnLoad]
    internal static class MaskSuggestBurstWatch
    {
        /// <summary>このセッションで Burst のコンパイル失敗を観測した(SessionState キー)。</summary>
        public const string BurstFailedKey = "Iroca.Burst.CompileFailed";

        // Burst 1.8 が型初期化失敗時に出すメッセージ。バージョンが変わっても
        // 「Burst が関数をコンパイルできなかった」旨は同じ語で出ている。
        const string FailureMarker = "Burst failed to compile";

        public static bool FailedThisSession => SessionState.GetBool(BurstFailedKey, false);

        static MaskSuggestBurstWatch()
        {
            if (FailedThisSession) return;
            Application.logMessageReceived += OnLog;
            EditorApplication.update += StopWatching;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception) return;
            if (condition == null ||
                condition.IndexOf(FailureMarker, System.StringComparison.Ordinal) < 0) return;
            SessionState.SetBool(BurstFailedKey, true);
            StopWatching();
        }

        static void StopWatching()
        {
            Application.logMessageReceived -= OnLog;
            EditorApplication.update -= StopWatching;
        }
    }
}
