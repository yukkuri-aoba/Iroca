// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    internal readonly struct ZonePerfEntry
    {
        internal readonly string ZoneId;
        internal readonly double TotalMs;

        internal ZonePerfEntry(string zoneId, double totalMs)
        {
            ZoneId = zoneId;
            TotalMs = totalMs;
        }
    }

    /// <summary>
    /// 処理フェーズ(HSV/Match/FloodFill/...)1 つ分の累積実行時間。
    /// 全ゾーン合算なので、ゾーン別内訳(<see cref="ZonePerfEntry"/>)とは直交する見方になる。
    /// </summary>
    internal readonly struct PhasePerfEntry
    {
        internal readonly string Name;
        internal readonly double TotalMs;

        internal PhasePerfEntry(string name, double totalMs)
        {
            Name = name;
            TotalMs = totalMs;
        }
    }

    /// <summary>
    /// ProcessPixelsArray 一回分の実行時間レポート。
    /// バックグラウンドスレッドから <see cref="DebugCaptureHooks.OnPerfReport"/> 経由で通知される。
    /// </summary>
    internal sealed class PerfReport
    {
        internal readonly double TotalMs;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly ZonePerfEntry[] Zones;
        // フェーズ別累積(全ゾーン合算)。計測を入れていないビルドでは null/空になり得る。
        internal readonly PhasePerfEntry[] Phases;

        internal PerfReport(double totalMs, int width, int height, ZonePerfEntry[] zones,
            PhasePerfEntry[] phases = null)
        {
            TotalMs = totalMs;
            Width = width;
            Height = height;
            Zones = zones;
            Phases = phases;
        }
    }
}
