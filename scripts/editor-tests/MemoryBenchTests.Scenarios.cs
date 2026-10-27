// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Iroca.EditorTests
{
    /// <summary>
    /// MemoryBench の場面（ケースの scenario）。0.4.0 の計画の 0-1 で、プレビューの繰り返しのほかに Mono のヒープを
    /// 使う所（計画の 1.3 の表）を、製品の経路のまま steps 回繰り返して測る。品質は見ない。
    /// <list type="bullet">
    /// <item>maskPaint: ウィンドウで共通とゾーンごとの層に 1 ストロークずつ塗り（1 回の Undo ずつ）、重ね表示と
    /// プレビューを作り直す。続けて全部を Undo、全部を Redo する（1.3 の A1・A2・B2・B3）。</item>
    /// <item>recipeBuild: マスクつきのレシピから、NDMF のビルドと同じ差し替えテクスチャを作る（B1）。手ごとにゾーンを
    /// 少し変えて保存し、キャッシュに当たらないようにする。</item>
    /// <item>autoTune: 自動化 API で、色で指定したゾーンを自動調整つきで足す（証拠なしの解析。A5）。</item>
    /// <item>mcpRecolor: 自動化 API で PNG に書き出す（A6）。</item>
    /// </list>
    /// まだ無い場面: メッシュの部位の右クリック（A4。シーンにテクスチャを使うメッシュが要る）と AI 提案の準備と
    /// クリック（B4。Sentis とモデルが要る）。手元のホストで足す。
    /// 最後に作業配列のプールを手放して GC し、OS へ返るかを releaseWaitSeconds の間記録する（ウィンドウの場面と同じ）。
    /// </summary>
    public partial class MemoryBenchTests
    {
        private const string BenchDir = "Assets/IrocaMemBench";

        private static void RunScenarioCase(CaseDto c, CasesDto cfg, StringBuilder sb)
        {
            switch (c.scenario)
            {
                case "maskPaint": RunMaskPaintScenario(c, cfg, sb); break;
                case "recipeBuild": RunRecipeBuildScenario(c, cfg, sb); break;
                case "autoTune": RunAutomationScenario(c, cfg, sb, autoTune: true); break;
                case "mcpRecolor": RunAutomationScenario(c, cfg, sb, autoTune: false); break;
                default: Assert.Fail($"{c.name}: 知らない場面です: {c.scenario}"); break;
            }
        }

        // ───────────── maskPaint ─────────────

        private static void RunMaskPaintScenario(CaseDto c, CasesDto cfg, StringBuilder sb)
        {
            var tex = ImportBenchTexture(c, out string assetPath);
            var (zones, _) = LoadZones(File.ReadAllText(c.zones));
            IrocaWindow win = null;
            try
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                win = ScriptableObject.CreateInstance<IrocaWindow>();
                win.SourceTexture = tex;
                win.Session.zones.Clear();
                foreach (var z in zones) win.Session.zones.Add(z);
                var pv = win.Preview;
                var masks = win._maskView;
                masks.EnsureMasks();
                Assert.IsTrue(pv.TryGetTrueSourcePixels(tex, out _, out int srcW, out int srcH), $"{c.name}: 原本を読めない");
                PixelProcessor.ComputeFitSize(srcW, srcH, IrocaConsts.Preview.MaxSize, out int prevW, out int prevH, out _);
                RegeneratePreview(pv);
                sb.Append(Sample(c.name, "opened", 0, 0)).Append('\n');

                int layers = zones.Count + 1;   // 共通 + ゾーンごと
                for (int k = 0; k < cfg.steps; k++)
                {
                    var sw = Stopwatch.StartNew();
                    for (int target = -1; target < zones.Count; target++)
                    {
                        masks.activeMaskTarget = target;
                        masks.editIncludeLayer = false;
                        masks.brushEraseMode = false;
                        Undo.IncrementCurrentGroup();   // 製品では押し始めが別のイベントなので、ストロークごとに 1 回の Undo
                        masks.BeginStroke();
                        float radius = Mathf.Max(2f, srcW * 0.02f);
                        for (int p = 0; p < 12; p++)
                        {
                            float t = p / 11f;
                            float u = Mathf.Repeat(0.1f + 0.05f * (target + 1) + 0.03f * k + 0.7f * t, 1f);
                            masks.PaintMask(new Vector2(u, 0.1f + 0.8f * t), radius);
                        }
                        masks.EndStroke();
                        masks.RebuildMaskOverlay(prevW, prevH);
                        RegeneratePreview(pv);
                    }
                    sb.Append(Sample(c.name, "paint", k, sw.Elapsed.TotalMilliseconds)).Append('\n');
                    sw.Restart();
                    for (int u = 0; u < layers; u++) { Undo.PerformUndo(); RegeneratePreview(pv); }
                    sb.Append(Sample(c.name, "undo", k, sw.Elapsed.TotalMilliseconds)).Append('\n');
                    sw.Restart();
                    for (int u = 0; u < layers; u++) { Undo.PerformRedo(); RegeneratePreview(pv); }
                    sb.Append(Sample(c.name, "redo", k, sw.Elapsed.TotalMilliseconds)).Append('\n');
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveWindowOpen", cfg.steps, 0)).Append('\n');
            }
            finally
            {
                if (win != null)
                {
                    Undo.ClearUndo(win);
                    UnityEngine.Object.DestroyImmediate(win);
                }
                FinishScenario(c, cfg, sb, assetPath);
            }
        }

        // プレビューを作り直し、概要段・確定段が終わるまでメインスレッドへの戻り(重ね表示の作り直しも)を捌く。
        private static void RegeneratePreview(PreviewView pv)
        {
            pv.GeneratePreviewAsync();
            var sw = Stopwatch.StartNew();
            int idle = 0;
            while (idle < 3)
            {
                if (sw.ElapsedMilliseconds > 600000) Assert.Fail("プレビューが終わらない");
                PreviewJobMainThread.Drain();
                idle = pv.IsGenerating ? 0 : idle + 1;
                Thread.Sleep(2);
            }
        }

        // ───────────── recipeBuild ─────────────

        private static void RunRecipeBuildScenario(CaseDto c, CasesDto cfg, StringBuilder sb)
        {
            var tex = ImportBenchTexture(c, out string assetPath);
            var (zones, settings) = LoadZones(File.ReadAllText(c.zones));
            try
            {
                var state = new IrocaSessionState();
                state.zones.AddRange(zones);
                settings.CopyTo(state);
                // マスクはマスク編集と同じく元のファイルの寸法で持つ。共通は中央の縦帯、ゾーンごとに上端の帯を
                // 「塗らない」、下端の帯を「ここも塗る」にする。画素数の大きさの配列を作ると Mono のヒープがその分
                // 広がったままになり、ビルドがマスクを展開する分(1.3 の B1)が見えなくなるので、保存形式(RLE)を直接書く。
                PngSize(c.png, out int mw, out int mh);
                MaskRegionEdit.CanvasSize(mw, mh, tex.width, tex.height, out mw, out mh);
                var mask = state.maskState;
                mask.schemaVersion = MaskState.CurrentSchemaVersion;
                mask.width = mw;
                mask.height = mh;
                mask.commonMaskBase64 = BandRle(mw, mh, 0.45f, 0.55f, vertical: true);
                for (int i = 0; i < zones.Count; i++)
                {
                    mask.zones.Add(new MaskZoneEntry { zoneId = zones[i].id,
                        maskBase64 = BandRle(mw, mh, 0.90f - 0.02f * i, 0.92f - 0.02f * i, vertical: false) });
                    mask.zoneIncludes.Add(new MaskZoneEntry { zoneId = zones[i].id,
                        maskBase64 = BandRle(mw, mh, 0.02f * i, 0.02f * i + 0.02f, vertical: false) });
                }
                var recipe = RecipeStore.Create(tex, state, BenchDir);
                var baseTol = zones.Select(z => z.tolerance).ToArray();

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                for (int k = 0; k < cfg.steps; k++)
                {
                    for (int i = 0; i < zones.Count; i++) zones[i].tolerance = baseTol[i] + 0.002f * (k + 1);
                    RecipeStore.Save(recipe, state);
                    var sw = Stopwatch.StartNew();
                    var built = RecipeTextureBuilder.Build(recipe, out var failure);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    Assert.IsNotNull(built, $"{c.name}: {failure}");
                    sb.Append(Sample(c.name, "build", k, ms)).Append('\n');
                    UnityEngine.Object.DestroyImmediate(built);
                }
                var cached = Stopwatch.StartNew();
                var again = RecipeTextureBuilder.Build(recipe, out _);
                sb.Append(Sample(c.name, "buildCached", cfg.steps, cached.Elapsed.TotalMilliseconds)).Append('\n');
                if (again != null) UnityEngine.Object.DestroyImmediate(again);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveAfterSteps", cfg.steps, 0)).Append('\n');
            }
            finally { FinishScenario(c, cfg, sb, assetPath); }
        }

        // 寸法 w×h の帯(下原点)を、MaskRle の保存形式("R:" + Base64(幅・高さ・最初の値・ラン長の列))で書く。
        // vertical なら x が [from, to) の縦帯、そうでなければ y が [from, to) の横帯。
        private static string BandRle(int w, int h, float from, float to, bool vertical)
        {
            int n = vertical ? w : h;
            int a = Mathf.Clamp((int)(n * from), 0, n), b = Mathf.Clamp((int)(n * to), 0, n);
            var runs = new List<uint>();
            bool first = false, last = false;
            void Add(bool v, long count)
            {
                if (count <= 0) return;
                if (runs.Count == 0) { first = v; last = v; runs.Add((uint)count); return; }
                if (v == last) runs[runs.Count - 1] += (uint)count;
                else { runs.Add((uint)count); last = v; }
            }
            if (vertical)
                for (int y = 0; y < h; y++) { Add(false, a); Add(true, b - a); Add(false, w - b); }
            else { Add(false, (long)a * w); Add(true, (long)(b - a) * w); Add(false, (long)(h - b) * w); }
            var bytes = new byte[9 + runs.Count * 4];
            BitConverter.GetBytes(w).CopyTo(bytes, 0);
            BitConverter.GetBytes(h).CopyTo(bytes, 4);
            bytes[8] = first ? (byte)1 : (byte)0;
            for (int i = 0; i < runs.Count; i++) BitConverter.GetBytes(runs[i]).CopyTo(bytes, 9 + i * 4);
            return "R:" + Convert.ToBase64String(bytes);
        }

        // PNG の IHDR から寸法を読む(画素は読まない)。
        private static void PngSize(string path, out int w, out int h)
        {
            var head = new byte[24];
            using (var f = File.OpenRead(path)) Assert.AreEqual(24, f.Read(head, 0, 24), path);
            w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        }

        // ───────────── autoTune / mcpRecolor ─────────────

        // 依頼は毎回 reset して、zones JSON のゾーンを色で指定し直す(手ごとに変更先の色を少し変える)。
        private static void RunAutomationScenario(CaseDto c, CasesDto cfg, StringBuilder sb, bool autoTune)
        {
            ImportBenchTexture(c, out string assetPath);
            var (zones, _) = LoadZones(File.ReadAllText(c.zones));
            string phase = autoTune ? "autoTune" : "mcpRecolor";
            try
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                for (int k = 0; k < cfg.steps; k++)
                {
                    string changes = "[" + string.Join(",", zones.Select(z =>
                        "{\"name\":\"" + z.name.Replace("\"", "'").Replace("\\", "/") + "\","
                        + "\"from\":\"#" + ColorUtility.ToHtmlStringRGB(z.sampleColor) + "\","
                        + "\"to\":\"#" + ColorUtility.ToHtmlStringRGB(Shift(z.targetColor, k)) + "\"}")) + "]";
                    string request = "{\"texture\":\"" + assetPath + "\",\"apply\":\"" + (autoTune ? "preview" : "png") + "\","
                        + "\"reset\":true,\"changes\":" + changes + ",\"autoTune\":" + (autoTune ? "true" : "false") + ","
                        + "\"output\":\"" + BenchDir + "/" + c.name + "_out.png\",\"overwrite\":true}";
                    var sw = Stopwatch.StartNew();
                    string json = IrocaAutomation.Recolor(request);
                    while (json.Contains("\"pending\":true"))
                    {
                        if (sw.ElapsedMilliseconds > 600000) Assert.Fail($"{c.name}: 自動化 API が終わらない: {json}");
                        PreviewJobMainThread.Drain();
                        Thread.Sleep(5);
                        json = IrocaAutomation.RecolorStatus();
                    }
                    double ms = sw.Elapsed.TotalMilliseconds;
                    var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
                    Assert.IsTrue(r.ok, $"{c.name}: {json}");
                    sb.Append(Sample(c.name, phase, k, ms)).Append('\n');
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveAfterSteps", cfg.steps, 0)).Append('\n');
            }
            finally
            {
                IrocaAutomation.CancelRecolorJob();
                FinishScenario(c, cfg, sb, assetPath);
            }
        }

        private static Color Shift(Color c, int k) => new Color(Mathf.Repeat(c.r + 0.05f * (k + 1), 1f), c.g, c.b, 1f);

        // ───────────── 共通 ─────────────

        // png を一時アセットとして取り込む(ウィンドウの場面と同じ。Max Size は importMaxSize、0 なら 16384)。
        private static Texture2D ImportBenchTexture(CaseDto c, out string assetPath)
        {
            assetPath = $"{BenchDir}/{c.name}.png";
            Directory.CreateDirectory(BenchDir);
            File.Copy(c.png, assetPath, true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            imp.maxTextureSize = c.importMaxSize > 0 ? c.importMaxSize : 16384;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            Assert.IsNotNull(tex, $"{c.name}: 取り込めません");
            return tex;
        }

        // 製品と同じく作業配列を手放し、OS へ返るかを releaseWaitSeconds の間 5 秒ごとに記録してから、一時アセットを消す。
        private static void FinishScenario(CaseDto c, CasesDto cfg, StringBuilder sb, string assetPath)
        {
            ReleasePools();
            var wait = Stopwatch.StartNew();
            for (int s = 0; s <= cfg.releaseWaitSeconds; s += 5)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "released", s, wait.Elapsed.TotalMilliseconds)).Append('\n');
                if (s < cfg.releaseWaitSeconds) Thread.Sleep(5000);
            }
            AssetDatabase.DeleteAsset(BenchDir);
        }
    }
}
