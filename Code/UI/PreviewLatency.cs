// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Iroca
{
    // プレビューの体感速度(操作 → 画面に絵が出るまで)を区間ごとに測る計測専用の記録。値・分岐は変えない。
    //
    // PerfReport は ProcessPixelsArray 1 回分(コア処理)の時間で、利用者が待たされる時間とは一致しない。
    // 操作してから絵が変わるまでには、ドラッグ中のデバウンス・スレッドプールの起動・EditorApplication.update
    // での受け渡し・再描画待ち・メインスレッドの準備とテクスチャ転送、それにプロキシ→フル→拡大表示の
    // 直列の段が挟まる。ここではそれらを同じ時間軸(起点 = 最後の操作)に並べる。
    //
    // 時刻は Stopwatch タイムスタンプ(打つだけなら数十 ns)で常時打ち、レポートの組み立ては
    // 受け手(Debug アセンブリの PerfView)がいるときだけ行う。Unity に依存しないので単体テストできる。

    /// <summary>1 つの段(プロキシ/フル/拡大表示)の時刻。0 = まだ打っていない。</summary>
    internal sealed class LatencyStageMarks
    {
        internal long PrepStart;   // メインスレッドの準備開始(拡大表示のみ。プロキシ/フルは周期側の準備を使う)
        internal long Scheduled;   // ジョブ投入(メインスレッド)
        internal long WorkStart;   // ワーカースレッドで開始
        internal long CoreStart;   // ProcessPixelsArray 開始
        internal long CoreEnd;     // ProcessPixelsArray 終了
        internal long WorkEnd;     // ワーカースレッドで完了
        internal long Applied;     // メインスレッドで結果を受け取った(apply)
        internal long UploadStart; // テクスチャ転送の開始(次の OnGUI)
        internal long UploadEnd;   // テクスチャ転送の終了 = 画面に出た

        internal bool Shown => UploadEnd != 0;
    }

    /// <summary>
    /// 再生成 1 回(GeneratePreviewAsync 1 回)ぶんの時刻。拡大表示中のスクロール・ズームで拡大表示
    /// だけを作り直したときも 1 周期として測る(<see cref="ViewChange"/>)。ジョブ内の時刻はワーカーが書き、
    /// メインスレッドは apply 以降にだけ読む(PreviewJobMainThread のキューが前後関係を保証する)。
    /// </summary>
    internal sealed class PreviewLatencyCycle
    {
        // true = スクロール・ズームを起点に拡大表示だけを作り直した周期。プロキシ・フル段と準備は無く、
        // Input は表示範囲(位置・倍率)が最後に変わった時刻。
        internal bool ViewChange;
        internal long Input;       // この再生成が反映する最後の操作。0 = 操作なし(初回表示など)
        internal long FirstInput;  // 前回画面が更新されてから最初の操作(ドラッグ中に止まっていた時間の起点)
        internal long PrepStart;   // GeneratePreviewAsync 開始
        internal long PrepEnd;     // ジョブ投入の直前
        internal int SourceW, SourceH;
        internal readonly LatencyStageMarks Proxy = new LatencyStageMarks();
        internal readonly LatencyStageMarks Full = new LatencyStageMarks();
        internal readonly LatencyStageMarks Detail = new LatencyStageMarks();

        internal static long Now => Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// 操作の時刻を控え、再生成のたびに「最後の操作」と「画面が止まってから最初の操作」を渡す。
    /// 画面に出た再生成より後の操作は、まだ反映されていない操作として持ち越す。
    /// </summary>
    internal sealed class PreviewInputClock
    {
        private long _last;
        private long _firstUnshown;
        private long _firstSinceSnapshot;

        internal void NoteInput(long now)
        {
            _last = now;
            if (_firstUnshown == 0) _firstUnshown = now;
            if (_firstSinceSnapshot == 0) _firstSinceSnapshot = now;
        }

        /// <summary>再生成の入力を確定したとき(GeneratePreviewAsync)に呼ぶ。</summary>
        internal void Snapshot(PreviewLatencyCycle cycle)
        {
            cycle.Input = _last;
            cycle.FirstInput = _firstUnshown;
            _firstSinceSnapshot = 0;
        }

        /// <summary>
        /// 直近の再生成の結果が画面に出たときに呼ぶ。それより後の操作はまだ画面に出ていない。
        /// 画面に出せるのは常に直近の再生成だけ(新しい操作はジョブを取り消し、次の再生成は
        /// 保留中の結果を転送した後の OnGUI でしか始まらない)なので、これで足りる。
        /// </summary>
        internal void NoteShown() => _firstUnshown = _firstSinceSnapshot;
    }

    internal enum LatencyKind
    {
        Wait, // 待ち・受け渡し(誰も計算していない)
        Main, // メインスレッドの処理(この間 Editor の UI が止まる)
        Job,  // ジョブ内のコア以外(複製・縮小)
        Core, // ProcessPixelsArray(PerfReport が測っている部分)
    }

    internal readonly struct LatencySegment
    {
        internal readonly int Lane;
        internal readonly LatencyKind Kind;
        internal readonly string Name;
        internal readonly double StartMs;
        internal readonly double EndMs;

        internal LatencySegment(int lane, LatencyKind kind, string name, double startMs, double endMs)
        {
            Lane = lane;
            Kind = kind;
            Name = name;
            StartMs = startMs;
            EndMs = endMs;
        }

        internal double Ms => EndMs - StartMs;
    }

    /// <summary>再生成 1 回ぶんの体感時間。時刻はすべて起点(最後の操作)からのミリ秒。</summary>
    internal sealed class PreviewLatencyReport
    {
        internal const int LaneRequest = 0;
        internal const int LaneProxy = 1;
        internal const int LaneFull = 2;
        internal const int LaneDetail = 3;
        internal const int LaneCount = 4;

        internal bool ViewChange;      // スクロール・ズームで拡大表示だけを作り直した(初回・確定・止まっていた時間は NaN)
        internal bool HasInput;        // false = 操作を起点にできない再生成(起点は生成開始)
        internal double FirstShownMs;  // 初回表示(プロキシ。無ければフル)
        internal double FinalShownMs;  // 確定表示(フル)。NaN = まだ(ドラッグの追従中・確定前に取り消し)
        internal double DetailShownMs = double.NaN; // 拡大表示(詳細クロップ)。NaN = なし
        internal double StaleMs;       // 画面が止まってから最初の操作 → 初回表示
        internal double MainThreadMs;  // メインスレッドの処理の合計(UI が止まった時間)
        internal double CoreMs;        // コア処理の合計
        internal int SourceW, SourceH;
        internal LatencySegment[] Segments;

        /// <summary>確定表示(フル段)まで出たか。</summary>
        internal bool IsFinal => !double.IsNaN(FinalShownMs);

        /// <summary>最後に画面へ出た時刻(拡大表示 → 確定 → 初回の順に、出ているもの)。</summary>
        internal double LastShownMs =>
            !double.IsNaN(DetailShownMs) ? DetailShownMs : IsFinal ? FinalShownMs : FirstShownMs;

        /// <summary>
        /// 時刻から区間を組み立てる。画面にまだ何も出ていなければ null。
        /// プロキシだけ出た段階(ドラッグの追従・確定前)でも組み立て、確定は NaN にする。
        /// スクロール・ズームの周期は拡大表示が出たときだけ組み立てる。
        /// frequency は 1 秒あたりのタイムスタンプ数(Stopwatch.Frequency。テストでは任意の値)。
        /// </summary>
        internal static PreviewLatencyReport Build(PreviewLatencyCycle c, long frequency)
        {
            if (c == null) return null;
            if (c.ViewChange ? !c.Detail.Shown : !(c.Full.Shown || c.Proxy.Shown)) return null;

            long origin = c.Input != 0 ? c.Input : c.PrepStart;
            double Ms(long t) => (t - origin) * 1000.0 / frequency;

            var segs = new List<LatencySegment>();
            void Add(int lane, LatencyKind kind, string name, long from, long to)
            {
                if (from == 0 || to == 0 || to < from) return;
                segs.Add(new LatencySegment(lane, kind, name, Ms(from), Ms(to)));
            }

            // スクロール・ズームでは、手を止めて拡大表示の作り直しを始めるまで(0.3 秒の間引き)が待ち。
            if (c.Input != 0)
                Add(LaneRequest, LatencyKind.Wait, "待ち(操作→生成開始)", c.Input,
                    c.ViewChange ? c.Detail.PrepStart : c.PrepStart);
            Add(LaneRequest, LatencyKind.Main, "準備(入力のスナップショット)", c.PrepStart, c.PrepEnd);

            AddStage(Add, LaneProxy, c.Proxy);
            // ドラッグの追従では、プロキシを出したあと手を止める(0.2 秒)か離すまでフル段を始めない。
            // 通常の直列(プロキシの受け取りで即フル段を投入)ではほぼ 0 なので、1 ms 未満は出さない。
            if (c.Proxy.Shown && c.Full.Scheduled - c.Proxy.Applied >= frequency / 1000)
                Add(LaneFull, LatencyKind.Wait, "待ち(確定の開始待ち)", c.Proxy.Applied, c.Full.Scheduled);
            AddStage(Add, LaneFull, c.Full);
            if (c.Detail.Shown)
            {
                Add(LaneDetail, LatencyKind.Wait, "待ち(拡大表示の再生成待ち)", c.Full.UploadEnd, c.Detail.PrepStart);
                Add(LaneDetail, LatencyKind.Main, "準備(切り出し範囲の計算)", c.Detail.PrepStart, c.Detail.Scheduled);
                AddStage(Add, LaneDetail, c.Detail);
            }

            var r = new PreviewLatencyReport
            {
                ViewChange = c.ViewChange,
                HasInput = c.Input != 0,
                FinalShownMs = c.Full.Shown ? Ms(c.Full.UploadEnd) : double.NaN,
                SourceW = c.SourceW,
                SourceH = c.SourceH,
                Segments = segs.ToArray(),
            };
            r.FirstShownMs = !c.Proxy.Shown ? r.FinalShownMs
                : r.IsFinal ? Math.Min(Ms(c.Proxy.UploadEnd), r.FinalShownMs)
                : Ms(c.Proxy.UploadEnd);
            if (c.Detail.Shown) r.DetailShownMs = Ms(c.Detail.UploadEnd);
            r.StaleMs = c.Input != 0 && c.FirstInput != 0 && c.FirstInput <= c.Input
                ? r.FirstShownMs + (c.Input - c.FirstInput) * 1000.0 / frequency
                : r.FirstShownMs;
            foreach (var s in segs)
            {
                if (s.Kind == LatencyKind.Main) r.MainThreadMs += s.Ms;
                else if (s.Kind == LatencyKind.Core) r.CoreMs += s.Ms;
            }
            return r;
        }

        private static void AddStage(Action<int, LatencyKind, string, long, long> add, int lane, LatencyStageMarks m)
        {
            if (!m.Shown) return;
            add(lane, LatencyKind.Wait, "起動待ち(スレッドプール)", m.Scheduled, m.WorkStart);
            if (m.CoreStart == 0 && m.CoreEnd == 0)
            {
                // コア処理の無い段(拡大表示はフル段の出力を切り出すだけ)。
                add(lane, LatencyKind.Job, "切り出し・縮小", m.WorkStart, m.WorkEnd);
            }
            else
            {
                add(lane, LatencyKind.Job, "複製・縮小", m.WorkStart, m.CoreStart);
                add(lane, LatencyKind.Core, "コア処理", m.CoreStart, m.CoreEnd);
                add(lane, LatencyKind.Job, "表示寸法へ縮小", m.CoreEnd, m.WorkEnd);
            }
            add(lane, LatencyKind.Wait, "受け渡し(update 待ち)", m.WorkEnd, m.Applied);
            add(lane, LatencyKind.Wait, "再描画待ち", m.Applied, m.UploadStart);
            add(lane, LatencyKind.Main, "反映(テクスチャ転送など)", m.UploadStart, m.UploadEnd);
        }
    }

    /// <summary>
    /// レポートの受け渡し口。画面に絵が出るたび(プロキシ・確定・拡大表示)に呼ばれ、同じ再生成の
    /// レポートを段が進むごとに差し替える。受け手(Debug アセンブリの PerfView)がいないときは
    /// 組み立てもしない。メインスレッドからのみ呼ぶ。
    /// </summary>
    internal static class PreviewLatency
    {
        internal static event Action<PreviewLatencyReport> OnReport;

        /// <summary>レポートを組み立てて渡す。渡した(受け手がいた)ら true。</summary>
        internal static bool Publish(PreviewLatencyCycle cycle)
        {
            var handler = OnReport;
            if (handler == null) return false;
            var report = PreviewLatencyReport.Build(cycle, Stopwatch.Frequency);
            if (report == null) return false;
            handler(report);
            return true;
        }
    }
}
