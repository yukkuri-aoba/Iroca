// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 拡大表示のパン・ズームの追従を測る(0.4.0 の計画の 4-4。3.5 の案 A のタイルと、それより前の 1 枚の切り出しを比べる)。
    /// 4K のテクスチャの確定段の出力を土台に、枠(650pt 四方)を一定の速さで動かしたときの 1 コマごとに、
    /// 拡大表示の作り直しを頼んでから画面用のテクスチャへ上げ終わるまでの時間を、体感速度の計測
    /// (<see cref="PreviewLatencyCycle"/> の拡大表示の段)で測る。コマの間隔は 60fps 相当に合わせる
    /// (タイルの先読みはコマの合間に進む)。
    /// 同じテストを、拡大表示の作りが違う版にも当てて比べるので、版によって違う口は名前で探す
    /// (タイル: <c>ScheduleRange</c>、それより前: <c>ScheduleCrop</c>)。
    /// 普段は IROCA_DETAIL_BENCH_REPORT が無いので Ignore。実行は <c>Run-EditorTests.ps1 -DetailBench</c>。
    /// </summary>
    [Category("DetailBench")]
    public class DetailBenchTests
    {
        private const int W = 4096, H = 4096;
        private const float ViewportPt = 650f;
        private const float MarginPt = 96f;
        private const float StepPt = 24f;      // 1 コマに動かす量(ゆっくりめのドラッグ)
        private const int Steps = 40;          // 右へ最大 40 コマ、続けて左へ同じだけ(戻り)
        private const double FrameMs = 1000.0 / 60.0;

        private TestAssets _assets;
        private string _prefKey;
        private string _prefBefore;
        private bool _fullResBefore;
        private IrocaWindow _win;
        private string _guid;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            _fullResBefore = PreviewView.DetailFullResolution;
        }

        [TearDown]
        public void TearDown()
        {
            if (_win != null)
            {
                Undo.ClearUndo(_win);
                UnityEngine.Object.DestroyImmediate(_win);
            }
            PreviewView.DetailFullResolution = _fullResBefore;
            if (_prefBefore == null) EditorPrefs.DeleteKey(_prefKey);
            else EditorPrefs.SetString(_prefKey, _prefBefore);
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (_guid == null || !Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) File.Delete(f);
            }
            _assets.Dispose();
        }

        [Test]
        public void PanAndZoom()
        {
            string report = Environment.GetEnvironmentVariable("IROCA_DETAIL_BENCH_REPORT");
            if (string.IsNullOrEmpty(report)) Assert.Ignore("IROCA_DETAIL_BENCH_REPORT が未設定です（Run-EditorTests.ps1 -DetailBench で明示実行）");

            var preview = Open();
            var dv = preview.DetailView;
            var scheduleRange = typeof(DetailPreviewView).GetMethod("ScheduleRange", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var scheduleCrop = typeof(DetailPreviewView).GetMethod("ScheduleCrop", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsTrue(scheduleRange != null || scheduleCrop != null, "拡大表示を作り直す口が見つからない");
            string kind = scheduleRange != null ? "tiles" : "crop";
            var sb = new StringBuilder();

            int detW = preview.DetailW, detH = preview.DetailH;
            // 表示倍率(元の 1 画素が画面の何 pt か)。枠が画像の中を動ける倍率(4K を枠いっぱいに出した全体表示 ≒0.16 の
            // 2 倍)から 16 倍まで。枠は画像の左端から右へ動かし、端に着く手前で折り返して左端へ戻る(画像の外へは出さない。
            // 実際の画面でもスクロールは画像の端で止まる)。
            foreach (float pxPerSrc in new[] { 0.32f, 0.64f, 1.28f, 2.56f })
            {
                var samples = new List<Sample>();
                float viewSrc = ViewportPt / pxPerSrc;
                float step = StepPt / pxPerSrc;
                int steps = Mathf.Min(Steps, Mathf.FloorToInt((detW - viewSrc) / step));
                Assert.Greater(steps, 4, "枠が画像の中を動けない");
                float cx = viewSrc / 2f, cy = detH * 0.5f;
                // 最初の 1 コマ(全部作る)は数えない。
                Step(dv, scheduleRange, scheduleCrop, detW, detH, cx, cy, viewSrc, pxPerSrc);
                for (int i = 0; i < steps * 2; i++)
                {
                    cx += (i < steps ? 1 : -1) * step;
                    samples.Add(Step(dv, scheduleRange, scheduleCrop, detW, detH, cx, cy, viewSrc, pxPerSrc));
                }
                sb.Append(Line(kind, "pan", pxPerSrc, samples)).Append('\n');
            }

            // ズーム: 中心を保ったまま、表示倍率を段ごとに上げ下げする(段が変わるたびに全部作り直す)。
            {
                var samples = new List<Sample>();
                float[] stops = { 0.16f, 0.2f, 0.24f, 0.32f, 0.4f, 0.48f, 0.64f, 0.8f, 0.96f, 1.28f };
                foreach (var z in stops.Concat(stops.Reverse()))
                    samples.Add(Step(dv, scheduleRange, scheduleCrop, detW, detH, detW * 0.5f, detH * 0.5f,
                        ViewportPt / z, z));
                sb.Append(Line(kind, "zoom", 0f, samples)).Append('\n');
            }

            File.AppendAllText(report, sb.ToString());
            UnityEngine.Debug.Log("[DetailBench]\n" + sb);
        }

        private struct Sample
        {
            public bool Job;          // 作り直しを頼んだ(タイルの並びが変わらないときは頼まない)
            public double TotalMs;    // 頼んでから、テクスチャへ上げ終わるまで
            public double WaitMs;     // 頼んでからワーカーで始まるまで
            public double WorkMs;     // ワーカーの計算(切り出し・縮小・並べる)
            public double UploadMs;   // メインスレッドのテクスチャへの転送
        }

        private static Sample Step(DetailPreviewView dv, MethodInfo scheduleRange, MethodInfo scheduleCrop,
            int detW, int detH, float cx, float cy, float viewSrc, float pxPerSrc)
        {
            var frame = Stopwatch.StartNew();
            // 見えている範囲に余白を足す(拡大表示の作り直しと同じ求め方)。
            int margin = Mathf.CeilToInt(MarginPt / pxPerSrc);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(cx - viewSrc / 2) - margin, 0, detW);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(cx + viewSrc / 2) + margin, 0, detW);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(cy - viewSrc / 2) - margin, 0, detH);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(cy + viewSrc / 2) + margin, 0, detH);
            // 空の範囲は前の作りのジョブが例外で黙って止まる(届くのを待ち続ける)ので、先に止める。
            Assert.IsTrue(x0 < x1 && y0 < y1, $"見えている範囲が空になった: [{x0},{x1})×[{y0},{y1})");
            var cycle = new PreviewLatencyCycle { ViewChange = true, Input = PreviewLatencyCycle.Now, SourceW = detW, SourceH = detH };
            long prep = PreviewLatencyCycle.Now;
            bool job;
            if (scheduleRange != null)
                job = (bool)scheduleRange.Invoke(dv, new object[] { x0, y0, x1, y1, pxPerSrc, cycle, prep });
            else
            {
                // それより前の作り: 見えている範囲をそのまま切り出し、表示の画素数へ面平均で縮める。
                int cw = x1 - x0, ch = y1 - y0;
                int outW = cw, outH = ch;
                if (pxPerSrc < 1f)
                {
                    outW = Mathf.Clamp(Mathf.RoundToInt(cw * pxPerSrc), 1, cw);
                    outH = Mathf.Clamp(Mathf.RoundToInt(ch * pxPerSrc), 1, ch);
                }
                var pv = s_preview;
                scheduleCrop.Invoke(dv, new object[] { detW, pv.DetailSource, pv.DetailOutput, x0, y0, cw, ch, outW, outH, pxPerSrc, cycle, prep });
                job = true;
            }
            var s = new Sample { Job = job };
            if (job)
            {
                var watch = Stopwatch.StartNew();
                while (!dv.HasPendingResult)
                {
                    Assert.Less(watch.Elapsed.TotalSeconds, 60, "拡大表示が届かない");
                    PreviewJobMainThread.Drain();
                    if (!dv.HasPendingResult) Thread.Sleep(0);
                }
                dv.ApplyPendingResult();
                var m = cycle.Detail;
                double f = Stopwatch.Frequency / 1000.0;
                s.TotalMs = (m.UploadEnd - m.Scheduled) / f;
                s.WaitMs = (m.WorkStart - m.Scheduled) / f;
                s.WorkMs = (m.WorkEnd - m.WorkStart) / f;
                s.UploadMs = (m.UploadEnd - m.UploadStart) / f;
            }
            // 60fps のコマの残りを待つ(タイルの先読みはこの間に進む)。
            while (frame.Elapsed.TotalMilliseconds < FrameMs)
            {
                PreviewJobMainThread.Drain();
                Thread.Sleep(1);
            }
            return s;
        }

        private static PreviewView s_preview;

        private static string Line(string kind, string scenario, float pxPerSrc, List<Sample> samples)
        {
            var jobs = samples.Where(x => x.Job).ToList();
            var totals = samples.Select(x => x.Job ? x.TotalMs : 0.0).OrderBy(x => x).ToList();
            double P(double q) => totals.Count == 0 ? 0 : totals[Math.Min(totals.Count - 1, (int)Math.Floor(q * totals.Count))];
            double Mean(Func<Sample, double> f) => jobs.Count == 0 ? 0 : jobs.Average(f);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"kind\":\"{0}\",\"scenario\":\"{1}\",\"pxPerSrc\":{2},\"steps\":{3},\"jobs\":{4},"
                + "\"meanMs\":{5:F2},\"p50Ms\":{6:F2},\"p95Ms\":{7:F2},\"maxMs\":{8:F2},"
                + "\"jobWaitMs\":{9:F2},\"jobWorkMs\":{10:F2},\"jobUploadMs\":{11:F2} }}",
                kind, scenario, pxPerSrc, samples.Count, jobs.Count,
                totals.Average(), P(0.5), P(0.95), totals.Count == 0 ? 0 : totals[totals.Count - 1],
                Mean(x => x.WaitMs), Mean(x => x.WorkMs), Mean(x => x.UploadMs));
        }

        // 4K の模様(なめらかな陰影に細かい縞と点。縮めると平均の値になる)を開き、色替えの確定段まで済ませる。
        private PreviewView Open()
        {
            var px = new Color32[W * H];
            var rng = new System.Random(7);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float s = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Sin(x * 0.01f) * Mathf.Cos(y * 0.013f));
                    bool stripe = ((x / 3) + (y / 5)) % 2 == 0;
                    byte n = (byte)rng.Next(0, 24);
                    px[y * W + x] = x < W / 2
                        ? new Color32((byte)(200 * s + (stripe ? 10 : 0)), (byte)(40 * s + n), (byte)(40 * s), 255)
                        : new Color32((byte)(40 * s), (byte)(90 * s + n), (byte)(200 * s + (stripe ? 10 : 0)), 255);
                }
            string path = _assets.WritePng("bench4k", px, W, H);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 4096;
            importer.SaveAndReimport();
            _guid = AssetDatabase.AssetPathToGUID(path);
            EditorPrefs.SetString(_prefKey, _guid);
            PreviewView.DetailFullResolution = false;
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            var zone = new ColorZone
            {
                name = "red",
                sampleColor = new Color(0.7f, 0.15f, 0.15f),
                sampleColorSet = true,
                targetColor = new Color(0.10f, 0.60f, 0.60f),
                targetColorSet = true,
                tolerance = 0.25f,
            };
            zone.EnsureId();
            _win.Session.zones.Clear();
            _win.Session.zones.Add(zone);
            var preview = _win.Preview;
            s_preview = preview;
            preview.GeneratePreviewAsync();
            var watch = Stopwatch.StartNew();
            int idle = 0;
            while (idle < 3)
            {
                Assert.Less(watch.Elapsed.TotalSeconds, 300, "プレビューが終わらない");
                PreviewJobMainThread.Drain();
                idle = preview.IsGenerating ? 0 : idle + 1;
                Thread.Sleep(2);
            }
            Assert.IsNotNull(preview.DetailOutput, "確定段の出力が無い");
            Assert.AreEqual((W, H), (preview.DetailW, preview.DetailH), "拡大表示の土台は 4K のはず");
            return preview;
        }
    }
}
