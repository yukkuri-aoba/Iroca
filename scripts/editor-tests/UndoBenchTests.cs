// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 編集状態(マスクの RLE 文字列を含む)を Unity の Undo に記録する手間と、自動保存の各段の時間を測る(0.3.0)。
    /// ウィンドウはスライダーを動かすたびに Undo.RecordObject でウィンドウ全体を記録し、ブラシの押し始めに
    /// RegisterCompleteObjectUndo で丸ごと写す。記録の量はマスクの文字列の長さで決まるので、マスクなし・
    /// ふつう(部位のかたまり数個)・多い(細かい塗りが多数)の 3 通りで比べる。
    /// 普段は IROCA_UNDO_BENCH_REPORT が無いので Ignore。実行は <c>Run-EditorTests.ps1 -UndoBench</c>。
    /// </summary>
    [Category("UndoBench")]
    public class UndoBenchTests
    {
        private const int W = 4096, H = 4096;
        private const int Reps = 15;

        private IrocaEditingDocument _host;

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        private static bool[] Discs(int seed, int count, int rMin, int rMax)
        {
            var rng = new System.Random(seed);
            var m = new bool[W * H];
            for (int i = 0; i < count; i++)
            {
                int cx = rng.Next(W), cy = rng.Next(H), r = rng.Next(rMin, rMax);
                for (int y = Math.Max(0, cy - r); y < Math.Min(H, cy + r + 1); y++)
                    for (int x = Math.Max(0, cx - r); x < Math.Min(W, cx + r + 1); x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) m[y * W + x] = true;
            }
            return m;
        }

        private IrocaSessionState Session(string kind)
        {
            var s = IrocaSessionState.CreateDefault();
            for (int i = 0; i < 4; i++)
            {
                var z = new ColorZone { name = "zone" + i };
                z.EnsureId();
                s.zones.Add(z);
            }
            bool[] common = null;
            var zones = new Dictionary<string, bool[]>();
            var includes = new Dictionary<string, bool[]>();
            if (kind == "typical")
            {
                common = Discs(1, 3, 150, 400);
                zones[s.zones[0].id] = Discs(2, 4, 100, 300);
                includes[s.zones[1].id] = Discs(3, 2, 80, 200);
            }
            else if (kind == "heavy")
            {
                common = Discs(4, 2500, 3, 25);
                for (int i = 0; i < 3; i++) zones[s.zones[i].id] = Discs(5 + i, 2500, 3, 25);
            }
            MaskStateCodec.Encode(s.maskState, W, H, common, zones, includes);
            return s;
        }

        private static double Median(List<double> v)
        {
            v.Sort();
            return v[v.Count / 2];
        }

        private static double Time(Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            return sw.Elapsed.TotalMilliseconds;
        }

        [Test]
        public void Undo_And_Autosave_Costs()
        {
            string report = Environment.GetEnvironmentVariable("IROCA_UNDO_BENCH_REPORT");
            if (string.IsNullOrEmpty(report)) Assert.Ignore("IROCA_UNDO_BENCH_REPORT が未設定です（Run-EditorTests.ps1 -UndoBench で明示実行）");

            string tmpDir = Path.Combine(Path.GetTempPath(), "iroca_undobench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            var sb = new StringBuilder();
            try
            {
                foreach (var kind in new[] { "none", "typical", "heavy" })
                {
                    _host = ScriptableObject.CreateInstance<IrocaEditingDocument>();
                    _host.session = Session(kind);
                    Undo.ClearAll();
                    var ms = _host.session.maskState;
                    long maskChars = (ms.commonMaskBase64?.Length ?? 0);
                    foreach (var e in ms.zones) maskChars += e.maskBase64.Length;
                    foreach (var e in ms.zoneIncludes) maskChars += e.maskBase64.Length;
                    int windowJson = EditorJsonUtility.ToJson(_host).Length;

                    // スライダーのドラッグ 1 フレーム: 代入前に RecordObject、フレームの終わりで差分を取る(Flush で再現)。
                    var slider = new List<double>();
                    Undo.IncrementCurrentGroup();
                    for (int i = 0; i < Reps + 2; i++)
                    {
                        double t = Time(() =>
                        {
                            Undo.RecordObject(_host, "Iroca Edit");
                            _host.session.zones[0].tolerance += 0.0001f;
                            Undo.FlushUndoRecordObjects();
                        });
                        if (i >= 2) slider.Add(t);
                    }

                    // 実際のドラッグに近い形: 1 回の操作の中では毎フレーム RecordObject するだけで、記録の確定
                    // (差分を取る)はマウスを離したときの 1 回(Unity が自動で行う。ここでは Flush で再現)。
                    var dragFrame = new List<double>();
                    var dragRelease = new List<double>();
                    for (int g = 0; g < 5; g++)
                    {
                        Undo.IncrementCurrentGroup();
                        for (int i = 0; i < Reps; i++)
                            dragFrame.Add(Time(() =>
                            {
                                Undo.RecordObject(_host, "Iroca Edit");
                                _host.session.zones[0].tolerance += 0.0001f;
                            }));
                        dragRelease.Add(Time(Undo.FlushUndoRecordObjects));
                    }

                    // ブラシの押し始め: ウィンドウを丸ごと写す。続けて Undo / Redo(書き戻し)。
                    var stroke = new List<double>();
                    var undo = new List<double>();
                    var redo = new List<double>();
                    for (int i = 0; i < Reps; i++)
                    {
                        Undo.IncrementCurrentGroup();
                        stroke.Add(Time(() => Undo.RegisterCompleteObjectUndo(_host, "Paint Mask Stroke")));
                        _host.session.zones[1].tolerance += 0.0001f;
                        Undo.IncrementCurrentGroup();
                        undo.Add(Time(Undo.PerformUndo));
                        redo.Add(Time(Undo.PerformRedo));
                    }

                    // 自動保存の各段(UserSettings のマスクとセッション、結び付いたレシピ)。
                    string maskPath = Path.Combine(tmpDir, kind + ".iroca-mask.json");
                    var maskJson = new List<double>();
                    var write = new List<double>();
                    var writeSame = new List<double>();
                    var sessionJson = new List<double>();
                    var recipeParse = new List<double>();
                    string json = null, fullJson = null;
                    for (int i = 0; i < Reps; i++)
                    {
                        maskJson.Add(Time(() => json = JsonUtility.ToJson(ms)));
                        // 毎回 1 文字変えて書く(実際に書く場合)・同じ中身(書かない場合)。
                        string changed = json + (i % 2 == 0 ? " " : "  ");
                        write.Add(Time(() => AtomicFile.WriteAllText(maskPath, changed)));
                        writeSame.Add(Time(() => AtomicFile.WriteAllTextIfChanged(maskPath, changed)));
                        sessionJson.Add(Time(() => fullJson = JsonUtility.ToJson(_host.session)));
                        recipeParse.Add(Time(() => JsonUtility.FromJson<IrocaSessionState>(fullJson)));
                    }

                    sb.Append("{\"kind\":\"").Append(kind).Append('"')
                      .Append(",\"maskChars\":").Append(maskChars)
                      .Append(",\"windowJson\":").Append(windowJson)
                      .Append(",\"sliderFrameMs\":").Append(Median(slider).ToString("F3"))
                      .Append(",\"dragFrameMs\":").Append(Median(dragFrame).ToString("F3"))
                      .Append(",\"dragReleaseMs\":").Append(Median(dragRelease).ToString("F3"))
                      .Append(",\"strokeBeginMs\":").Append(Median(stroke).ToString("F3"))
                      .Append(",\"undoMs\":").Append(Median(undo).ToString("F3"))
                      .Append(",\"redoMs\":").Append(Median(redo).ToString("F3"))
                      .Append(",\"maskToJsonMs\":").Append(Median(maskJson).ToString("F3"))
                      .Append(",\"atomicWriteMs\":").Append(Median(write).ToString("F3"))
                      .Append(",\"writeIfSameMs\":").Append(Median(writeSame).ToString("F3"))
                      .Append(",\"sessionToJsonMs\":").Append(Median(sessionJson).ToString("F3"))
                      .Append(",\"recipeFromJsonMs\":").Append(Median(recipeParse).ToString("F3"))
                      .Append("}\n");
                    Undo.ClearAll();
                    UnityEngine.Object.DestroyImmediate(_host);
                    _host = null;
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
            File.AppendAllText(report, sb.ToString());
            UnityEngine.Debug.Log("[Iroca] UndoBench\n" + sb);
        }
    }
}
