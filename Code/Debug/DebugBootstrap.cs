// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;

namespace Iroca.DebugTools
{
    /// <summary>
    /// Editor 起動時に <see cref="DebugCaptureHooks"/> へファクトリと描画フックを登録する。
    /// この asmdef ごと削除すれば <c>InitializeOnLoadMethod</c> は呼ばれず、
    /// 本体 <see cref="DebugCaptureHooks.Factory"/> は null のままで何も起きない。
    /// </summary>
    internal static class DebugBootstrap
    {
        [InitializeOnLoadMethod]
        private static void Init()
        {
            DebugCaptureHooks.Factory = DebugView.CurrentCaptureOrNull;
            // ジョブ完了時（メインスレッド）に埋め終わったキャプチャを公開する。生成時公開だと
            // 背景ジョブの Snapshots.Add と DebugWindow の OnGUI foreach が競合するため。
            DebugCaptureHooks.OnCaptureComplete += DebugView.PublishCompletedCapture;
            PerfView.Register();
            // 「パフォーマンス」セクション 1 つに集約。デバッグモード ON のとき
            // 詳細内訳・スレッド設定・段階キャプチャ(DebugView)を PerfView が内包して描画する。
            DebugCaptureHooks.OnDrawFoldout += PerfView.Draw;
        }
    }
}
