// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca
{
    /// <summary>
    /// パイプライン透明化機能の静的接続点。実装が同梱されていなくても
    /// 本体は型としてこのクラスだけを参照する。
    ///
    /// Code/Debug/ の asmdef が存在し、かつ <c>[InitializeOnLoadMethod]</c> で
    /// <see cref="Factory"/> と UI 側の <c>DebugWindowHooks.OnDrawFoldout</c>（Code/UI/DebugWindowHooks.cs）を
    /// 登録したときだけデバッグ機能が有効化される。フォルダごと削除すれば本クラスは
    /// 単なる「null 入れ物」として静かに無効化される。
    /// ウィンドウの型を使う部分は UI の DebugWindowHooks に分けた(0.4.0 の計画の 3-2。partial のままだと、
    /// Core と UI を別の asmdef にしたときに 1 つの型にできない)。
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
        /// ProcessPixelsArray 完了時に発火されるパフォーマンスレポートイベント。
        /// バックグラウンドスレッドから呼ばれるためハンドラは最小限の処理に留めること。
        /// </summary>
        internal static event Action<PerfReport> OnPerfReport;

        // このスレッドで最後に出たレポート(TakeThreadPerfReport で取り出す)。
        [ThreadStatic] private static PerfReport t_lastPerfReport;

        internal static void RaisePerfReport(PerfReport report)
        {
            t_lastPerfReport = report;
            OnPerfReport?.Invoke(report);
        }

        /// <summary>
        /// 呼び出したスレッドで最後に出たレポートを取り出す(取り出したら空にする)。ProcessPixelsArray は
        /// 呼び出したスレッドでレポートを出すので、呼んだ直後に取ればその呼び出しのものになる
        /// (エクスポートなど別のスレッドの処理と重なっても取り違えない)。
        /// </summary>
        internal static PerfReport TakeThreadPerfReport()
        {
            var report = t_lastPerfReport;
            t_lastPerfReport = null;
            return report;
        }

        /// <summary>
        /// プレビュージョブ完了時（メインスレッド）に、埋め終わったキャプチャを公開するイベント。
        /// これを完了時に発火することで、DebugWindow が「背景ジョブが Add 中のリスト」を読まずに済む。
        /// 生成時に公開すると OnGUI の foreach と Snapshots.Add がスレッド競合する。
        /// </summary>
        internal static event Action<IDebugCapture> OnCaptureComplete;

        internal static void RaiseCaptureComplete(IDebugCapture capture)
        {
            OnCaptureComplete?.Invoke(capture);
        }

        /// <summary>
        /// Parallel.For の MaxDegreeOfParallelism を手動設定する。
        /// 0 以下のとき自動設定 (PixelProcessor.DefaultParallelism) を使用する。
        /// Debug モジュールの PerfView が EditorPrefs に永続化して管理する。
        /// 値は Foundation の <see cref="CoreParallelism.Override"/> が持つ(ここは転送役。0.4.0 の計画の 3-4)。
        /// </summary>
        internal static int ParallelismOverride
        {
            get => CoreParallelism.Override;
            set => CoreParallelism.Override = value;
        }

        /// <summary>
        /// true のとき色の表(PixelProcessor.Palette.cs)を作らず、全部の段を画素ごとの計算で回す。
        /// 色の表がある経路と無い経路は同じ式の別実装なので、両方の出力がビット単位で同じことを
        /// ハーネスを IROCA_NO_PALETTE=1 で動かして確かめる(scripts/golden/test_palette_parity.py)。製品からは設定しない。
        /// </summary>
        internal static bool DisablePalette;
    }
}
