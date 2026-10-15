// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;

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
        // フェーズの内訳(サブ段、全ゾーン合算)。各フェーズ内の主要な呼び出しごとの時間で、
        // スレッド数を変えても縮まない段(逐次で残っている段)を特定するのに使う。null になり得る。
        internal readonly PhasePerfEntry[] SubPhases;

        internal PerfReport(double totalMs, int width, int height, ZonePerfEntry[] zones,
            PhasePerfEntry[] phases = null, PhasePerfEntry[] subPhases = null)
        {
            TotalMs = totalMs;
            Width = width;
            Height = height;
            Zones = zones;
            Phases = phases;
            SubPhases = subPhases;
        }
    }

    /// <summary>
    /// 区切りから区切りまでの時間を ID ごとに累積する計測専用の時計(値・分岐は変えない)。
    /// 区切りは処理を呼び出したスレッドだけが打つ(各段の Parallel.For の外側)ので同期は要らない。
    /// 直前の区切りからの時間を、次に打った区切りの ID に計上する。
    /// </summary>
    internal sealed class SubPhaseClock
    {
        private readonly long[] _ticks;
        private long _last;

        internal SubPhaseClock(int count)
        {
            _ticks = new long[count];
            _last = Stopwatch.GetTimestamp();
        }

        /// <summary>計上せずに起点だけを今へ移す(ゾーンの頭など)。</summary>
        internal void Restart() => _last = Stopwatch.GetTimestamp();

        internal void Mark(int id)
        {
            long now = Stopwatch.GetTimestamp();
            _ticks[id] += now - _last;
            _last = now;
        }

        internal PhasePerfEntry[] ToEntries(string[] names)
        {
            var e = new PhasePerfEntry[_ticks.Length];
            for (int i = 0; i < e.Length; i++)
                e[i] = new PhasePerfEntry(names[i], _ticks[i] * 1000.0 / Stopwatch.Frequency);
            return e;
        }
    }
}
