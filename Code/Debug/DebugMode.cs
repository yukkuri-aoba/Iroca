// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca.DebugTools
{
    /// <summary>
    /// 「デバッグモード」のマスタースイッチ（EditorPrefs で永続化）。
    ///
    /// OFF（既定）: パフォーマンス表示は合計時間だけの簡略表示。段階キャプチャは完全 no-op。
    /// ON: フェーズ別/ゾーン別の詳細内訳・スレッド数調整・段階ごとのキャプチャ（パイプライン
    ///     透明化）が利用可能になる。
    ///
    /// この 1 フラグを <see cref="PerfView"/>（表示の詳細/簡略切替）と
    /// <see cref="DebugView"/>（キャプチャの実行可否ゲート）が共有する。
    /// Debug asmdef ごと削除すればフラグごと消え、本体には影響しない。
    /// </summary>
    internal static class DebugMode
    {
        private const string PrefKey = "Iroca.Debug.MasterEnabled";

        private static bool s_loaded;
        private static bool s_enabled;

        internal static bool IsEnabled
        {
            get { EnsureLoaded(); return s_enabled; }
            set
            {
                EnsureLoaded();
                if (s_enabled == value) return;
                s_enabled = value;
                EditorPrefs.SetBool(PrefKey, value);
            }
        }

        private static void EnsureLoaded()
        {
            if (s_loaded) return;
            s_loaded = true;
            s_enabled = EditorPrefs.GetBool(PrefKey, false);
        }
    }
}
