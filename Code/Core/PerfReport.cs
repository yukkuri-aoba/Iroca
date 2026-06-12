// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace VRCAvatarColorChanger
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
    /// ProcessPixelsArray 一回分の実行時間レポート。
    /// バックグラウンドスレッドから <see cref="DebugCaptureHooks.OnPerfReport"/> 経由で通知される。
    /// </summary>
    internal sealed class PerfReport
    {
        internal readonly double TotalMs;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly ZonePerfEntry[] Zones;

        internal PerfReport(double totalMs, int width, int height, ZonePerfEntry[] zones)
        {
            TotalMs = totalMs;
            Width = width;
            Height = height;
            Zones = zones;
        }
    }
}
