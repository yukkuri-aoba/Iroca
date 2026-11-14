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

namespace Iroca.EditorTests {
    /// <summary>
    /// MemoryBench の場面（ケースの scenario）。0.4.0 の計画の 0-1 で、プレビューの繰り返しのほかに Mono のヒープを
    /// 使う所（計画の 1.3 の表）を、製品の経路のまま steps 回繰り返して測る。品質は見ない。
    /// <list type="bullet">
    /// <item>maskPaint: ウィンドウで共通とゾーンごとの層に 1 ストロークずつ塗り（1 回の Undo ずつ）、重ね表示と
    /// プレビューを作り直す。続けて全部を Undo、全部を Redo する（1.3 の A1・A2・B2・B3）。</item>
    /// <item>recipeBuild: マスクつきのレシピから、NDMF のビルドと同じ差し替えテクスチャを作る（B1）。手ごとにゾーンを
    /// 少し変えて保存し、キャッシュに当たらないようにする。ケースの windowOpen が true なら、同じテクスチャをウィンドウで
    /// 編集している最中にビルドする(<see cref="RunRecipeBuildWithWindow"/>)。</item>
    /// <item>recipeApply: レシピが複数あるアバターを非破壊ビルドに通す(<see cref="RunRecipeApplyScenario"/>)。</item>
    /// <item>autoTune: 自動化 API で、色で指定したゾーンを自動調整つきで足す（証拠なしの解析。A5）。</item>
    /// <item>mcpRecolor: 自動化 API で PNG に書き出す（A6）。</item>
    /// </list>
    /// まだ無い場面: メッシュの部位の右クリック（A4。シーンにテクスチャを使うメッシュが要る）と AI 提案の準備と
    /// クリック（B4。Sentis とモデルが要る）。手元のホストで足す。
    /// 最後に作業配列のプールを手放して GC し、OS へ返るかを releaseWaitSeconds の間記録する（ウィンドウの場面と同じ）。
    /// </summary>
    public partial class MemoryBenchTests {
        private const string BenchDir = "Assets/IrocaMemBench";

        private static void RunScenarioCase(CaseDto c, CasesDto cfg, StringBuilder sb) {
            switch (c.scenario) {
                case "maskPaint": RunMaskPaintScenario(c, cfg, sb); break;
                case "recipeBuild": RunRecipeBuildScenario(c, cfg, sb); break;
                case "recipeApply": RunRecipeApplyScenario(c, cfg, sb); break;
                case "autoTune": RunAutomationScenario(c, cfg, sb, autoTune: true); break;
                case "mcpRecolor": RunAutomationScenario(c, cfg, sb, autoTune: false); break;
                case AiScenario: break;   // フレームを回すので別のテスト(MemoryBenchTests.Ai.cs)
                default: Assert.Fail($"{c.name}: 知らない場面です: {c.scenario}"); break;
            }
        }

        // ───────────── maskPaint ─────────────

        private static void RunMaskPaintScenario(CaseDto c, CasesDto cfg, StringBuilder sb) {
            var tex = ImportBenchTexture(c, out string assetPath);
            var (zones, _) = LoadZones(File.ReadAllText(c.zones));
            IrocaWindow win = null;
            try {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                win = ScriptableObject.CreateInstance<IrocaWindow>();
                win.SourceTexture = tex;
                win.Session.zones.Clear();
                foreach (var z in zones) {
                    win.Session.zones.Add(z);
                }
                var pv = win.Preview;
                var masks = win._maskView;
                masks.EnsureMasks();
                Assert.IsTrue(pv.TryGetTrueSourcePixels(tex, out _, out int srcW, out int srcH), $"{c.name}: 原本を読めない");
                PixelProcessor.ComputeFitSize(srcW, srcH, IrocaConsts.Preview.MaxSize, out int prevW, out int prevH, out _);
                RegeneratePreview(pv);
                sb.Append(Sample(c.name, "opened", 0, 0)).Append('\n');

                int layers = zones.Count + 1;   // 共通 + ゾーンごと
                for (int k = 0; k < cfg.steps; k++) {
                    var sw = Stopwatch.StartNew();
                    for (int target = -1; target < zones.Count; target++) {
                        masks.activeMaskTarget = target;
                        masks.editIncludeLayer = false;
                        masks.brushEraseMode = false;
                        Undo.IncrementCurrentGroup();   // 製品では押し始めが別のイベントなので、ストロークごとに 1 回の Undo
                        masks.BeginStroke();
                        float radius = Mathf.Max(2f, srcW * 0.02f);
                        for (int p = 0; p < 12; p++) {
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
            } finally {
                if (win != null) {
                    Undo.ClearUndo(win);
                    UnityEngine.Object.DestroyImmediate(win);
                }
                FinishScenario(c, cfg, sb, assetPath);
            }
        }

        // プレビューを作り直し、概要段・確定段が終わるまでメインスレッドへの戻り(重ね表示の作り直しも)を捌く。
        private static void RegeneratePreview(PreviewView pv) {
            pv.GeneratePreviewAsync();
            var sw = Stopwatch.StartNew();
            int idle = 0;
            while (idle < 3) {
                if (sw.ElapsedMilliseconds > 600000) {
                    Assert.Fail("プレビューが終わらない");
                }
                PreviewJobMainThread.Drain();
                idle = pv.IsGenerating ? 0 : idle + 1;
                Thread.Sleep(2);
            }
        }

        // ───────────── recipeBuild ─────────────

        private static void RunRecipeBuildScenario(CaseDto c, CasesDto cfg, StringBuilder sb) {
            var tex = ImportBenchTexture(c, out string assetPath);
            var (zones, settings) = LoadZones(File.ReadAllText(c.zones));
            try {
                var (recipe, state) = CreateBenchRecipe(c.png, tex, zones, settings);
                if (c.windowOpen) {
                    RunRecipeBuildWithWindow(c, cfg, sb, tex, recipe);
                    return;
                }
                var baseTol = zones.Select(z => z.tolerance).ToArray();

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                for (int k = 0; k < cfg.steps; k++) {
                    for (int i = 0; i < zones.Count; i++) {
                        zones[i].tolerance = baseTol[i] + 0.002f * (k + 1);
                    }
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
                if (again != null) {
                    UnityEngine.Object.DestroyImmediate(again);
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveAfterSteps", cfg.steps, 0)).Append('\n');
            } finally { FinishScenario(c, cfg, sb, assetPath); }
        }

        // ゾーンとマスクの帯を持つレシピを作る(recipeBuild・recipeApply)。マスクはマスク編集と同じく元のファイルの寸法で持つ。
        // 共通は中央の縦帯、ゾーンごとに上端の帯を「塗らない」、下端の帯を「ここも塗る」にする。画素数の大きさの配列を作ると
        // Mono のヒープがその分広がったままになり、ビルドがマスクを展開する分(1.3 の B1)が見えなくなるので、保存形式(RLE)を直接書く。
        private static (IrocaRecipe recipe, IrocaSessionState state) CreateBenchRecipe(string png, Texture2D tex,
            List<ColorZone> zones, RecolorSettings settings) {
            var state = new IrocaSessionState();
            state.zones.AddRange(zones);
            settings.CopyTo(state);
            PngSize(png, out int mw, out int mh);
            MaskRegionEdit.CanvasSize(mw, mh, tex.width, tex.height, out mw, out mh);
            var mask = state.maskState;
            mask.schemaVersion = MaskState.CurrentSchemaVersion;
            mask.width = mw;
            mask.height = mh;
            mask.commonMaskBase64 = BandRle(mw, mh, 0.45f, 0.55f, vertical: true);
            for (int i = 0; i < zones.Count; i++) {
                mask.zones.Add(new MaskZoneEntry {
                    zoneId = zones[i].id,
                    maskBase64 = BandRle(mw, mh, 0.90f - 0.02f * i, 0.92f - 0.02f * i, vertical: false)
                });
                mask.zoneIncludes.Add(new MaskZoneEntry {
                    zoneId = zones[i].id,
                    maskBase64 = BandRle(mw, mh, 0.02f * i, 0.02f * i + 0.02f, vertical: false)
                });
            }
            return (RecipeStore.Create(tex, state, BenchDir), state);
        }

        // ───────────── recipeApply ─────────────

        // レシピが複数あるアバターの非破壊ビルド(Play・アップロードの NonDestructiveApplier.Apply)。pngs の画像ごとに
        // レシピを作り、子の Renderer がそれぞれの画像を使うアバターの根に、全部のレシピを持つコンポーネントを 1 つ置く。
        // 手ごとにゾーンを少し変えて保存し(キャッシュに当たらない)、アバターを作り直して Apply を呼ぶ(apply。ms は全体、
        // cpuMs はプロセスの CPU 時間、sub にレシピごとの BuildTexture の時間・ビルドの間の Private の最大・いろかのネイティブの
        // 用途ごとの最大)。最後に同じレシピのまま 1 回(applyCached。キャッシュから)。
        private static void RunRecipeApplyScenario(CaseDto c, CasesDto cfg, StringBuilder sb) {
            var pngs = c.pngs != null && c.pngs.Count > 0 ? c.pngs : new List<string> { c.png };
            var textures = new List<Texture2D>();
            var recipes = new List<IrocaRecipe>();
            var states = new List<IrocaSessionState>();
            var baseTol = new List<float[]>();
            try {
                for (int i = 0; i < pngs.Count; i++) {
                    var tex = ImportBenchTexture(pngs[i], $"{c.name}_{i}", c.importMaxSize, c.importFormat, out _);
                    var (zones, settings) = LoadZones(File.ReadAllText(c.zones));
                    var (recipe, state) = CreateBenchRecipe(pngs[i], tex, zones, settings);
                    textures.Add(tex);
                    recipes.Add(recipe);
                    states.Add(state);
                    baseTol.Add(zones.Select(z => z.tolerance).ToArray());
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                for (int k = 0; k <= cfg.steps; k++) {
                    bool cachedRun = k == cfg.steps;
                    if (!cachedRun) {
                        for (int i = 0; i < recipes.Count; i++) {
                            var zones = states[i].zones;
                            for (int z = 0; z < zones.Count; z++) {
                                zones[z].tolerance = baseTol[i][z] + 0.002f * (k + 1);
                            }
                            RecipeStore.Save(recipes[i], states[i]);
                        }
                    }
                    RecipeTextureBuilder.WaitForCacheWrites();
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var root = new GameObject(c.name + "-avatar");
                    var host = new BenchApplyHost();
                    try {
                        for (int i = 0; i < textures.Count; i++) {
                            var part = new GameObject("part" + i);
                            part.transform.SetParent(root.transform, false);
                            var mat = new Material(Shader.Find("Standard")) { name = "mat" + i, mainTexture = textures[i] };
                            host.Created.Add(mat);
                            part.AddComponent<MeshRenderer>().sharedMaterial = mat;
                        }
                        var component = root.AddComponent<IrocaRecolor>();
                        component.recipes.AddRange(recipes);

                        IrocaNativeMemory.ResetPeaks();
                        double ms, cpuMs;
                        long maxPrivate;
                        using (var watch = new PrivatePeakWatch()) {
                            var proc = System.Diagnostics.Process.GetCurrentProcess();
                            var cpu0 = proc.TotalProcessorTime;
                            var sw = Stopwatch.StartNew();
                            NonDestructiveApplier.Apply(root, host);
                            ms = sw.Elapsed.TotalMilliseconds;
                            proc.Refresh();
                            cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
                            maxPrivate = watch.Stop();
                        }
                        Assert.IsEmpty(host.Problems, $"{c.name}: " + string.Join(", ", host.Problems));
                        Assert.AreEqual(textures.Count, host.BuildMs.Count, $"{c.name}: 作ったテクスチャの数");
                        string builds = string.Join(",", host.BuildMs.Select((b, i) => "b" + i + "=" + b.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
                        sb.Append(Sample(c.name, cachedRun ? "applyCached" : "apply", k, ms,
                            sub: builds + ",cpuMs=" + cpuMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                                 + "," + NativeBreakdown(peak: true) + ",maxPrivate=" + maxPrivate)).Append('\n');
                    } finally {
                        UnityEngine.Object.DestroyImmediate(root);
                        foreach (var o in host.Created) {
                            if (o != null) {
                                UnityEngine.Object.DestroyImmediate(o);
                            }
                        }
                    }
                }
                RecipeTextureBuilder.WaitForCacheWrites();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveAfterSteps", cfg.steps, 0, sub: NativeBreakdown(peak: false))).Append('\n');
            } finally { FinishScenario(c, cfg, sb, null); }
        }

        // 非破壊ビルドの橋渡し(NDMF の BuildHost と同じく RecipeTextureBuilder.Build で作る)。作ったものは後で捨てる。
        private sealed class BenchApplyHost : NonDestructiveApplier.IHost {
            public readonly List<UnityEngine.Object> Created = new List<UnityEngine.Object>();
            public readonly List<double> BuildMs = new List<double>();
            public readonly List<string> Problems = new List<string>();

            public Texture2D BuildTexture(IrocaRecipe recipe, out RecipeTextureBuilder.Failure failure) {
                var sw = Stopwatch.StartNew();
                var tex = RecipeTextureBuilder.Build(recipe, out failure);
                BuildMs.Add(sw.Elapsed.TotalMilliseconds);
                return tex;
            }

            public void SaveAsset(UnityEngine.Object generated) => Created.Add(generated);
            public void RegisterReplaced(UnityEngine.Object original, UnityEngine.Object replacement) { }
            public void Report(NonDestructiveApplier.Problem problem, IrocaRecolor component, IrocaRecipe recipe,
                RecipeTextureBuilder.Failure failure) => Problems.Add(problem + ":" + failure);
            public void RewriteAnimatedMaterials(Func<Transform, bool> inScope, Func<Material, Material> mapping) { }
        }

        // recipeBuild の windowOpen: 同じテクスチャをいろかのウィンドウで編集している最中に、非破壊ビルドが走る場面。
        // ウィンドウは「前回のテクスチャ」として開く(このテクスチャのレシピは 1 つなので、結び付いてレシピからゾーンとマスクを読む。
        // インスペクタの「いろかで開く」も、ウィンドウを出した後は同じ SwitchSourceTexture)。マスクを展開して重ね表示と
        // プレビューを 1 回作り、ここを「開いた時点」(windowOpen)とする。手ごとに、ウィンドウでゾーンの許容度を変えて
        // プレビューを作り直し(edited。ビルドの直前の状態)、windowIdleSeconds が 1 以上ならその秒数だけ手を止めて(idle)、
        // ビルドの直前の合図(BeforeApply。ウィンドウが編集中の内容をレシピへ書き出す)を出してからビルドする(build。
        // ビルドの間のプロセスの Private の最大と、いろかのネイティブの用途ごとの最大も書く)。ビルドの後はウィンドウを開いたまま releaseWaitSeconds 待ち(windowHeld。作業配列を
        // 持ち続けるか、手を止めたときの縮小で返すか)、閉じてから共通の後始末(released)へ進む。
        private static void RunRecipeBuildWithWindow(CaseDto c, CasesDto cfg, StringBuilder sb, Texture2D tex, IrocaRecipe recipe) {
            string prefKey = IrocaWindow.LastTextureGuidPrefKey;
            string prefBefore = EditorPrefs.HasKey(prefKey) ? EditorPrefs.GetString(prefKey) : null;
            IrocaWindow win = null;
            try {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                var open = Stopwatch.StartNew();
                EditorPrefs.SetString(prefKey, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(tex)));
                win = ScriptableObject.CreateInstance<IrocaWindow>();
                if (win.SourceTexture != tex || win.BoundRecipe != recipe) {
                    win.SwitchSourceTexture(tex, recipe);
                }
                Assert.AreSame(recipe, win.BoundRecipe, $"{c.name}: ウィンドウがレシピに結び付いていない");
                Assert.Greater(win.Session.zones.Count, 0, $"{c.name}: ウィンドウにゾーンが無い");
                var pv = win.Preview;
                var masks = win._maskView;
                masks.EnsureMasks();
                Assert.IsTrue(pv.TryGetTrueSourcePixels(tex, out _, out int srcW, out int srcH), $"{c.name}: 原本を読めない");
                PixelProcessor.ComputeFitSize(srcW, srcH, IrocaConsts.Preview.MaxSize, out int prevW, out int prevH, out _);
                masks.RebuildMaskOverlay(prevW, prevH);
                RegeneratePreview(pv);
                double openMs = open.Elapsed.TotalMilliseconds;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "windowOpen", 0, openMs, sub: NativeBreakdown(peak: false))).Append('\n');

                var zones = win.Session.zones;
                var baseTol = zones.Select(z => z.tolerance).ToArray();
                for (int k = 0; k < cfg.steps; k++) {
                    var ew = Stopwatch.StartNew();
                    for (int i = 0; i < zones.Count; i++) {
                        zones[i].tolerance = baseTol[i] + 0.002f * (k + 1);
                    }
                    win.Editing.NotifyChanged(EditChange.Content);
                    RegeneratePreview(pv);
                    double editMs = ew.Elapsed.TotalMilliseconds;
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    sb.Append(Sample(c.name, "edited", k, editMs, sub: NativeBreakdown(peak: false))).Append('\n');
                    if (c.windowIdleSeconds > 0) {
                        // 編集してから手を止めた後に再生する(製品は計算が 10 秒無いと作業配列のプールの空きを返す)。
                        Thread.Sleep(c.windowIdleSeconds * 1000);
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        sb.Append(Sample(c.name, "idle", k, c.windowIdleSeconds * 1000.0, sub: NativeBreakdown(peak: false))).Append('\n');
                    }

                    NonDestructiveApplier.RaiseBeforeApplyForTests();
                    IrocaNativeMemory.ResetPeaks();
                    Texture2D built;
                    RecipeTextureBuilder.Failure failure;
                    double ms;
                    long maxPrivate;
                    using (var watch = new PrivatePeakWatch()) {
                        var sw = Stopwatch.StartNew();
                        built = RecipeTextureBuilder.Build(recipe, out failure);
                        ms = sw.Elapsed.TotalMilliseconds;
                        maxPrivate = watch.Stop();
                    }
                    Assert.IsNotNull(built, $"{c.name}: {failure}");
                    sb.Append(Sample(c.name, "build", k, ms, sub: NativeBreakdown(peak: true) + ",maxPrivate=" + maxPrivate)).Append('\n');
                    UnityEngine.Object.DestroyImmediate(built);
                }
                var cached = Stopwatch.StartNew();
                var again = RecipeTextureBuilder.Build(recipe, out _);
                sb.Append(Sample(c.name, "buildCached", cfg.steps, cached.Elapsed.TotalMilliseconds)).Append('\n');
                if (again != null) {
                    UnityEngine.Object.DestroyImmediate(again);
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "liveAfterSteps", cfg.steps, 0, sub: NativeBreakdown(peak: false))).Append('\n');

                // ウィンドウを開いたまま待つ(製品は編集中、計算が 10 秒無いと作業配列のプールの空きを返す)。
                var hold = Stopwatch.StartNew();
                for (int s = 0; s <= cfg.releaseWaitSeconds; s += 5) {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    sb.Append(Sample(c.name, "windowHeld", s, hold.Elapsed.TotalMilliseconds, sub: NativeBreakdown(peak: false))).Append('\n');
                    if (s < cfg.releaseWaitSeconds) {
                        Thread.Sleep(5000);
                    }
                }
            } finally {
                if (win != null) {
                    Undo.ClearUndo(win);
                    UnityEngine.Object.DestroyImmediate(win);
                }
                if (prefBefore != null) {
                    EditorPrefs.SetString(prefKey, prefBefore);
                } else {
                    EditorPrefs.DeleteKey(prefKey);
                }
            }
        }

        // いろかのヒープの外のメモリの、用途ごとの量(MB。"Work=…,Pixels=…")。peak なら ResetPeaks からの最大。
        private static string NativeBreakdown(bool peak) {
            var sb = new StringBuilder();
            for (int u = 0; u <= (int)NativeUsage.Other; u++) {
                long b = peak ? IrocaNativeMemory.PeakBytesOf((NativeUsage)u) : IrocaNativeMemory.LiveBytesOf((NativeUsage)u);
                if (sb.Length > 0) {
                    sb.Append(',');
                }
                sb.Append(peak ? "peak." : "").Append((NativeUsage)u).Append('=').Append(b / MB);
            }
            return sb.ToString();
        }

        // 呼び出しの間のプロセスの Private の最大(MB)を、別のスレッドで 5ms ごとに読んで覚える。プロセスの山(peakPrivate)は
        // 起動からの最大なので、ウィンドウのプレビューの山とビルドの山を分けられない。
        private sealed class PrivatePeakWatch : IDisposable {
            private readonly Thread _thread;
            private volatile bool _stop;
            private long _max;

            internal PrivatePeakWatch() {
                Read();
                _thread = new Thread(() => {
                    while (!_stop) {
                        Read();
                        Thread.Sleep(5);
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            private void Read() {
                GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessMemoryCounters>());
                long v = (long)c.PagefileUsage.ToUInt64() / MB;
                if (v > Interlocked.Read(ref _max)) {
                    Interlocked.Exchange(ref _max, v);
                }
            }

            // 止めて、最大を返す。
            internal long Stop() {
                if (!_stop) {
                    _stop = true;
                    _thread.Join();
                    Read();
                }
                return Interlocked.Read(ref _max);
            }

            public void Dispose() => Stop();
        }

        // 寸法 w×h の帯(下原点)を、MaskRle の保存形式("R:" + Base64(幅・高さ・最初の値・ラン長の列))で書く。
        // vertical なら x が [from, to) の縦帯、そうでなければ y が [from, to) の横帯。
        private static string BandRle(int w, int h, float from, float to, bool vertical) {
            int n = vertical ? w : h;
            int a = Mathf.Clamp((int)(n * from), 0, n), b = Mathf.Clamp((int)(n * to), 0, n);
            var runs = new List<uint>();
            bool first = false, last = false;
            void Add(bool v, long count) {
                if (count <= 0) {
                    return;
                }
                if (runs.Count == 0) { first = v; last = v; runs.Add((uint)count); return; }
                if (v == last) {
                    runs[runs.Count - 1] += (uint)count;
                } else { runs.Add((uint)count); last = v; }
            }
            if (vertical) {
                for (int y = 0; y < h; y++) { Add(false, a); Add(true, b - a); Add(false, w - b); }
            } else { Add(false, (long)a * w); Add(true, (long)(b - a) * w); Add(false, (long)(h - b) * w); }
            var bytes = new byte[9 + runs.Count * 4];
            BitConverter.GetBytes(w).CopyTo(bytes, 0);
            BitConverter.GetBytes(h).CopyTo(bytes, 4);
            bytes[8] = first ? (byte)1 : (byte)0;
            for (int i = 0; i < runs.Count; i++) {
                BitConverter.GetBytes(runs[i]).CopyTo(bytes, 9 + i * 4);
            }
            return "R:" + Convert.ToBase64String(bytes);
        }

        // PNG の IHDR から寸法を読む(画素は読まない)。
        private static void PngSize(string path, out int w, out int h) {
            var head = new byte[24];
            using (var f = File.OpenRead(path)) {
                Assert.AreEqual(24, f.Read(head, 0, 24), path);
            }
            w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        }

        // ───────────── autoTune / mcpRecolor ─────────────

        // 依頼は毎回 reset して、zones JSON のゾーンを色で指定し直す(手ごとに変更先の色を少し変える)。
        private static void RunAutomationScenario(CaseDto c, CasesDto cfg, StringBuilder sb, bool autoTune) {
            ImportBenchTexture(c, out string assetPath);
            var (zones, _) = LoadZones(File.ReadAllText(c.zones));
            string phase = autoTune ? "autoTune" : "mcpRecolor";
            try {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "start", 0, 0)).Append('\n');
                for (int k = 0; k < cfg.steps; k++) {
                    string changes = "[" + string.Join(",", zones.Select(z =>
                        "{\"name\":\"" + z.name.Replace("\"", "'").Replace("\\", "/") + "\","
                        + "\"from\":\"#" + ColorUtility.ToHtmlStringRGB(z.sampleColor) + "\","
                        + "\"to\":\"#" + ColorUtility.ToHtmlStringRGB(Shift(z.targetColor, k)) + "\"}")) + "]";
                    string request = "{\"texture\":\"" + assetPath + "\",\"apply\":\"" + (autoTune ? "preview" : "png") + "\","
                        + "\"reset\":true,\"changes\":" + changes + ",\"autoTune\":" + (autoTune ? "true" : "false") + ","
                        + "\"output\":\"" + BenchDir + "/" + c.name + "_out.png\",\"overwrite\":true}";
                    var sw = Stopwatch.StartNew();
                    string json = IrocaAutomation.Recolor(request);
                    while (json.Contains("\"pending\":true")) {
                        if (sw.ElapsedMilliseconds > 600000) {
                            Assert.Fail($"{c.name}: 自動化 API が終わらない: {json}");
                        }
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
            } finally {
                IrocaAutomation.CancelRecolorJob();
                FinishScenario(c, cfg, sb, assetPath);
            }
        }

        private static Color Shift(Color c, int k) => new Color(Mathf.Repeat(c.r + 0.05f * (k + 1), 1f), c.g, c.b, 1f);

        // ───────────── 共通 ─────────────

        // png を一時アセットとして取り込む(ウィンドウの場面と同じ。Max Size は importMaxSize、0 なら 16384)。
        private static Texture2D ImportBenchTexture(CaseDto c, out string assetPath)
            => ImportBenchTexture(c.png, c.name, c.importMaxSize, "", out assetPath);

        // format が空なら非圧縮。書けば Standalone の取り込み形式をそれで上書きする(TextureImporterFormat の名前。BC7・DXT5 など)。
        private static Texture2D ImportBenchTexture(string png, string name, int importMaxSize, string format, out string assetPath) {
            assetPath = $"{BenchDir}/{name}.png";
            Directory.CreateDirectory(BenchDir);
            File.Copy(png, assetPath, true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            imp.maxTextureSize = importMaxSize > 0 ? importMaxSize : 16384;
            if (string.IsNullOrEmpty(format)) {
                imp.textureCompression = TextureImporterCompression.Uncompressed;
            } else {
                var ps = imp.GetPlatformTextureSettings("Standalone");
                ps.overridden = true;
                ps.maxTextureSize = imp.maxTextureSize;
                ps.format = (TextureImporterFormat)Enum.Parse(typeof(TextureImporterFormat), format);
                imp.SetPlatformTextureSettings(ps);
            }
            imp.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            Assert.IsNotNull(tex, $"{name}: 取り込めません");
            return tex;
        }

        // 製品と同じく作業配列を手放し、OS へ返るかを releaseWaitSeconds の間 5 秒ごとに記録してから、一時アセットを消す。
        private static void FinishScenario(CaseDto c, CasesDto cfg, StringBuilder sb, string assetPath) {
            ReleasePools();
            var wait = Stopwatch.StartNew();
            for (int s = 0; s <= cfg.releaseWaitSeconds; s += 5) {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                sb.Append(Sample(c.name, "released", s, wait.Elapsed.TotalMilliseconds)).Append('\n');
                if (s < cfg.releaseWaitSeconds) {
                    Thread.Sleep(5000);
                }
            }
            AssetDatabase.DeleteAsset(BenchDir);
        }
    }
}
