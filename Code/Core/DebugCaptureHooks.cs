// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;

namespace Iroca
{
    /// <summary>
    /// パイプライン透明化機能の静的接続点。実装が同梱されていなくても
    /// 本体は型としてこのクラスだけを参照する。
    ///
    /// Code.Debug/ asmdef が存在し、かつ <c>[InitializeOnLoadMethod]</c> で
    /// <see cref="Factory"/> と <see cref="OnDrawFoldout"/> を登録したときだけ
    /// デバッグ機能が有効化される。フォルダごと削除すれば本クラスは
    /// 単なる「null 入れ物」として静かに無効化される。
    /// </summary>
    internal static class DebugCaptureHooks
    {
        /// <summary>
        /// プレビュー / エクスポートのジョブが <see cref="PixelProcessor"/> に渡す
        /// <see cref="IDebugCapture"/> を取得するためのファクトリ。
        /// null（= 機能未導入 or トグル OFF）の場合、本体は何もキャプチャしない。
        /// </summary>
        internal static Func<IDebugCapture> Factory;

        /// <summary>
        /// IrocaWindow の OnGUI から発火される foldout 描画イベント。
        /// subscriber がいないときは何も描画されない（本体 UI に影響なし）。
        /// </summary>
        internal static event Action<IrocaWindow> OnDrawFoldout;

        /// <summary>
        /// IrocaWindow から foldout イベントを発火する薄いラッパ。
        /// </summary>
        internal static void RaiseDrawFoldout(IrocaWindow window)
        {
            OnDrawFoldout?.Invoke(window);
        }

        /// <summary>
        /// ProcessPixelsArray 完了時に発火されるパフォーマンスレポートイベント。
        /// バックグラウンドスレッドから呼ばれるためハンドラは最小限の処理に留めること。
        /// </summary>
        internal static event Action<PerfReport> OnPerfReport;

        internal static void RaisePerfReport(PerfReport report)
        {
            OnPerfReport?.Invoke(report);
        }

        /// <summary>
        /// Parallel.For の MaxDegreeOfParallelism を手動設定する。
        /// 0 以下のとき自動設定 (ProcessorCount - 2) を使用する。
        /// Debug モジュールの PerfView が EditorPrefs に永続化して管理する。
        /// </summary>
        internal static int ParallelismOverride;
    }
}
