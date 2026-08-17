// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// Unity MCP / AI エージェントから Iroca をヘッドレス駆動するための自動化 API。
    ///
    /// UI（IrocaWindow / ExportView）を介さず、テクスチャ読込 → 再着色 → PNG 出力までを
    /// 静的メソッド一発で実行できる。<see cref="ExportView"/> の実出力経路（ディスクの PNG を
    /// 直接読み、<see cref="PixelProcessor.ProcessPixelsArray(Color32[],int,int,MaskSnapshot,System.Collections.Generic.IList{ColorZone},float,int,int,int,float,float,int,int,int,int,bool,int,float,IDebugCapture)"/>
    /// に通す）をそのまま同期で再現するので、製品の出力と一致する。
    ///
    /// 呼び出し経路は 3 つ。いずれも同じ中核（<see cref="RunRecolorCore"/>）を通る:
    ///   1. 静的 API: <see cref="RecolorByPreset"/> / <see cref="RecolorWithZones"/> 等。戻り値は JSON 文字列。
    ///      生 C# 実行が可能な MCP クライアント・EditMode テストから直接呼ぶ。
    ///   2. MCPForUnity カスタムツール: <c>Code/McpIntegration/IrocaMcpTools.cs</c> の <c>iroca_recolor</c> 等。
    ///      <c>execute_custom_tool</c> から本クラスの公開静的 API を呼ぶ。MCPForUnity 導入時のみ
    ///      コンパイルされる別 asmdef（IROCA_MCP_PRESENT ゲート）で、配布パッケージ本体は依存ゼロを保つ。
    ///      生 C# 実行に非対応のクライアントでも駆動できる（Tools メニューには何も追加しない）。
    ///   3. batchmode CLI: <c>Unity.exe -batchmode -executeMethod Iroca.IrocaAutomation.RunFromCommandLine ...</c>。
    ///
    /// v1 ではプリセット同梱マスクはヘッドレス適用しない（パーツ単位の粗いマスクは後続対応）。
    /// マスクを含むプリセットを渡した場合は結果 JSON の warnings で明示する。
    /// </summary>
    public static class IrocaAutomation
    {
        // ジョブ/結果ファイルの置き場。git 非追跡の UserSettings 配下に置き、Assets の import 揺れを避ける。
        private static string McpDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "UserSettings/Iroca/mcp"));

        /// <summary>再着色 1 回分の結果。JSON 文字列としてエージェントへ返す。</summary>
        [Serializable]
        public class RecolorResult
        {
            public bool ok;
            public string error = "";
            public string output = "";
            public int width;
            public int height;
            public int zonesApplied;
            // 比較パネル PNG(左=変換前 / 中=変換後 / 右=変化画素をマゼンタ表示)の絶対パス。
            // 生成に失敗した場合は空文字(理由は warnings)。エージェントはこれを画像として開いて目視検証する。
            public string preview = "";
            // preview を「見て確かめる」ようエージェントを誘導する短い案内。
            public string note = "";
            // 変化量メトリクス。意図通りかを数値でも裏取りできるようにする(過検出/変換漏れの早期検知)。
            public int changedPixels;
            public float changedFraction;              // 変化画素 / 全画素
            public float largestComponentFraction;     // 変化画素中、最大連結成分の占有率(小=散在=過検出の兆候)
            public int changeComponentCount;           // 変化領域の 4-連結成分数
            public int[] changeBBox = new int[0];      // 変化領域の [x, y, w, h]
            public List<string> warnings = new List<string>();
        }

        // --zones / zones モード用のフラットなゾーン設定（Harness.ZoneCfg と同一スキーマ、色は [r,g,b] 0..1）。
        // JsonUtility 用に public フィールドで定義する。
        // 既定値は ZonesJsonDefaults が唯一の正。ここにリテラルを書き戻さないこと
        // (Harness.ZoneCfg と乖離し、テストが製品でない挙動を測る事故になる)。
        [Serializable]
        private class ZoneDto
        {
            public string name = ZonesJsonDefaults.Name;
            public float[] sample = ZonesJsonDefaults.NewSample();
            public float[] target = ZonesJsonDefaults.NewTarget();
            public float tolerance = ZonesJsonDefaults.Tolerance;
            public float valueBlend = ZonesJsonDefaults.ValueBlend;
            public float edgeSoftness = ZonesJsonDefaults.EdgeSoftness;
            public float saturationStrictness = ZonesJsonDefaults.SaturationStrictness;
            public float saturationGuard = ZonesJsonDefaults.SaturationGuard;
            public float chromaThreshold = ZonesJsonDefaults.ChromaThreshold;
            public float shadowDesaturation = ZonesJsonDefaults.ShadowDesaturation;
            public float shadowForgivenessSatMin = ZonesJsonDefaults.ShadowForgivenessSatMin;
            public float outputSaturation = ZonesJsonDefaults.OutputSaturation;
            public bool highlightRecovery = ZonesJsonDefaults.HighlightRecovery;
            public bool highlightBandExpand = ZonesJsonDefaults.HighlightBandExpand;
            public bool applyHighlightWash = ZonesJsonDefaults.ApplyHighlightWash;
            public bool autoHighlightSample = ZonesJsonDefaults.AutoHighlightSample;
            public bool autoRecolorAnchor = ZonesJsonDefaults.AutoRecolorAnchor;
            public int layerIndex = ZonesJsonDefaults.LayerIndex;
            public bool useFloodFill = ZonesJsonDefaults.UseFloodFill;
            // 連続領域モードの上書きシード [u,v]（0-1）。未指定/長さ不足 = 自動アンカリング。
            // JsonUtility は未知フィールドを無言で捨てるため、ハーネス側だけが持っていた頃は
            // seedUV 付き zones JSON を製品へ渡しても黙って無視されていた。
            public float[] seedUV = null;
        }

        [Serializable]
        private class SettingsDto
        {
            public float edgeFeather = ZonesJsonDefaults.EdgeFeather;
            public int antiAliasCleanup = ZonesJsonDefaults.AntiAliasCleanup;
            public int holeFillPasses = ZonesJsonDefaults.HoleFillPasses;
            public int holeFillMinNeighbors = ZonesJsonDefaults.HoleFillMinNeighbors;
            public float relaxedSatMin = ZonesJsonDefaults.RelaxedSatMin;
            public float relaxedSatRamp = ZonesJsonDefaults.RelaxedSatRamp;
            public bool useDecontamination = ZonesJsonDefaults.UseDecontamination;
            public int decontaminationRadius = ZonesJsonDefaults.DecontaminationRadius;
        }

        [Serializable]
        private class ZonesRequest
        {
            public List<ZoneDto> zones = new List<ZoneDto>();
            public SettingsDto settings = new SettingsDto();
        }

        [Serializable]
        private class JobRequest
        {
            // "preset" | "zones"
            public string mode = "preset";
            public string source = "";
            public string output = "";
            // preset モード: プリセットのファイルパス / 名前 / インライン JSON のいずれか。
            public string preset = "";
            // zones モード: フラットなゾーン設定。
            public ZonesRequest zones;
        }

        [Serializable]
        private class PresetEntry { public string name = ""; public string path = ""; public string scope = ""; }

        [Serializable]
        private class PresetList { public List<PresetEntry> presets = new List<PresetEntry>(); }

        [Serializable]
        private class SchemaDoc
        {
            public string name = "com.yukkuri-aoba.iroca";
            public string version = "";
            public string[] methods;
            public string[] notes;
            public string[] fieldDocs;
            public ZoneDto zoneDefaults = new ZoneDto();
            public SettingsDto settingsDefaults = new SettingsDto();
            public JobRequest jobExample = new JobRequest();
        }

        /// <summary>パッケージ名とバージョンを JSON で返す。</summary>
        public static string GetVersion()
        {
            string version = "unknown";
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(IrocaAutomation).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.version)) version = info.version;
            }
            catch { /* 埋め込み配置などで解決できない場合は unknown のまま */ }
            return $"{{\"ok\":true,\"name\":\"com.yukkuri-aoba.iroca\",\"version\":\"{version}\"}}";
        }

        /// <summary>
        /// リクエスト/プリセットの JSON スキーマ・既定値・フィールド説明を JSON で返す。
        /// エージェントが呼び出し方を自己発見するための一次情報。
        /// </summary>
        public static string DescribeSchema()
        {
            var doc = new SchemaDoc
            {
                version = ExtractVersion(),
                methods = new[]
                {
                    "RecolorByPreset(sourceAssetPath, presetPathOrNameOrJson, outputAssetPath) -> RecolorResult",
                    "RecolorWithZones(sourceAssetPath, zonesRequestJson, outputAssetPath) -> RecolorResult",
                    "ListPresets() -> { presets:[{name,path,scope}] }",
                    "GetVersion() -> { name, version }",
                    "DescribeSchema() -> this document",
                },
                notes = new[]
                {
                    "パスは Assets 相対（Assets/...）・プロジェクト相対・絶対のいずれも可。出力は .png。",
                    "色は [r,g,b]（0..1）。enabled なゾーンが 1 つも無いと error になる。",
                    "v1 ではプリセット同梱マスクはヘッドレス適用しない（適用時は warnings に明記）。",
                    "MCP 経路: execute_custom_tool(\"iroca_recolor\", { source, output, preset|zones }) で呼ぶ（メニュー非依存）。",
                    "結果には 'preview'（比較パネル PNG のパス: 左=変換前 / 中=変換後 / 右=変化画素をマゼンタ表示）と "
                        + "変化メトリクス（changedPixels / changedFraction / largestComponentFraction / changeComponentCount / changeBBox）が付く。",
                    "検証ループ: 再着色 → 'preview' 画像を開いて意図通りか目視 → ずれていれば target/tolerance を調整して再実行。"
                        + "largestComponentFraction が小さい・changeComponentCount が多い＝変化が散在＝過検出の疑い。",
                },
                fieldDocs = new[]
                {
                    "sample: 変換元の色（このゾーンが拾う色）",
                    "target: 変換後の色",
                    "tolerance: 色マッチの許容範囲（大きいほど広く拾う）",
                    "valueBlend: 明度（模様）の保持度。原則 1.0、明度差が極端なときだけ下げる",
                    "edgeSoftness: エッジのソフトランプ。原則 0",
                    "saturationStrictness: サンプル彩度に対するマッチ厳格度 0..1",
                    "saturationGuard: 高彩度サンプル時に低彩度画素を弾く強さ 0..1",
                    "chromaThreshold: 無彩（グレー）判定のしきい値",
                    "shadowDesaturation: 暗部の脱彩量",
                    "outputSaturation: 出力色の彩度スケール（<1 で全体的に脱彩）",
                    "highlightRecovery/highlightBandExpand/applyHighlightWash: ハイライト処理の切替",
                    "settings.edgeFeather: 全体エッジぼかし。原則 0",
                    "settings.antiAliasCleanup / holeFill* / relaxedSat* / decontamination*: 後処理パラメータ",
                },
            };
            doc.jobExample = new JobRequest
            {
                mode = "preset",
                source = "Assets/Textures/body.png",
                output = "Assets/Textures/body_recolored.png",
                preset = "MyPreset",
                zones = new ZonesRequest
                {
                    zones = new List<ZoneDto> { new ZoneDto { name = "Example", sample = new[] { 1f, 1f, 1f }, target = new[] { 0.1f, 0.3f, 0.8f }, tolerance = 0.25f } },
                    settings = new SettingsDto(),
                },
            };
            return JsonUtility.ToJson(doc, true);
        }

        /// <summary>プロジェクト/ユーザー保存先のプリセット一覧を JSON で返す。</summary>
        public static string ListPresets()
        {
            var list = new PresetList();
            AddPresets(list, PresetStore.ProjectPresetFolder, "project");
            AddPresets(list, PresetStore.UserPresetFolder, "user");
            return JsonUtility.ToJson(list, true);
        }

        /// <summary>
        /// プリセットを使ってテクスチャを再着色し PNG を書き出す。
        /// <paramref name="presetPathOrNameOrJson"/> はファイルパス・プリセット名・インライン JSON のいずれでも可。
        /// </summary>
        public static string RecolorByPreset(string sourceAssetPath, string presetPathOrNameOrJson, string outputAssetPath)
        {
            var warnings = new List<string>();
            try
            {
                var preset = ResolvePreset(presetPathOrNameOrJson);
                if (preset == null)
                    return Fail($"preset not found / parse failed: {presetPathOrNameOrJson}");

                if (HasEmbeddedMask(preset))
                    warnings.Add("preset contains embedded masks; masks are NOT applied in headless automation (v1).");

                var zones = preset.zones ?? new List<ColorZone>();
                var settings = SettingsFromPreset(preset);
                return Json(RunRecolorCore(sourceAssetPath, zones, settings, outputAssetPath, warnings));
            }
            catch (Exception ex)
            {
                return Fail($"{ex.GetType().Name}: {ex.Message}", warnings);
            }
        }

        /// <summary>
        /// フラットなゾーン設定 JSON（<c>{"zones":[...],"settings":{...}}</c>）で再着色して PNG を書き出す。
        /// プリセット保存を経由せず、その場の色指定で変換したいときに使う。
        /// </summary>
        public static string RecolorWithZones(string sourceAssetPath, string zonesRequestJson, string outputAssetPath)
        {
            var warnings = new List<string>();
            try
            {
                ZonesRequest req;
                try { req = JsonUtility.FromJson<ZonesRequest>(zonesRequestJson); }
                catch (Exception ex) { return Fail($"zones JSON parse failed: {ex.Message}"); }
                if (req == null || req.zones == null || req.zones.Count == 0)
                    return Fail("zones JSON has no zones.");

                var zones = req.zones.Select(BuildZone).ToList();
                var settings = SettingsFromDto(req.settings ?? new SettingsDto());
                return Json(RunRecolorCore(sourceAssetPath, zones, settings, outputAssetPath, warnings));
            }
            catch (Exception ex)
            {
                return Fail($"{ex.GetType().Name}: {ex.Message}", warnings);
            }
        }

        // メニュー経路（execute_menu_item）は廃止。MCP からの駆動は MCPForUnity カスタムツール
        // （Code/McpIntegration/IrocaMcpTools.cs の iroca_recolor 等）経由で公開静的 API を呼ぶ。
        // Tools メニューにはウィンドウ起動の単一項目だけを残し、サブメニュー二重表示を避ける。

        /// <summary>
        /// <c>Unity.exe -batchmode -quit -executeMethod Iroca.IrocaAutomation.RunFromCommandLine
        ///   -irocaSource &lt;path&gt; -irocaOutput &lt;path&gt; (-irocaPreset &lt;path|name&gt; | -irocaZonesFile &lt;json&gt; | -irocaJob &lt;json&gt;)</c>
        /// で呼ぶ、MCP を介さない完全ヘッドレス経路。結果は Console と UserSettings/Iroca/mcp/result.json に出す。
        /// </summary>
        public static void RunFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string source = GetArg(args, "-irocaSource");
            string output = GetArg(args, "-irocaOutput");
            string preset = GetArg(args, "-irocaPreset");
            string zonesFile = GetArg(args, "-irocaZonesFile");
            string jobFile = GetArg(args, "-irocaJob");

            string resultJson;
            try
            {
                if (!string.IsNullOrEmpty(jobFile))
                {
                    var job = JsonUtility.FromJson<JobRequest>(File.ReadAllText(jobFile));
                    if (job != null && string.Equals(job.mode, "zones", StringComparison.OrdinalIgnoreCase))
                        resultJson = RecolorWithZones(job.source, job.zones != null ? JsonUtility.ToJson(job.zones) : "", job.output);
                    else if (job != null)
                        resultJson = RecolorByPreset(job.source, job.preset, job.output);
                    else
                        resultJson = Fail("job file parse failed.");
                }
                else if (!string.IsNullOrEmpty(zonesFile))
                {
                    resultJson = RecolorWithZones(source, File.ReadAllText(zonesFile), output);
                }
                else
                {
                    resultJson = RecolorByPreset(source, preset, output);
                }
            }
            catch (Exception ex)
            {
                resultJson = Fail($"{ex.GetType().Name}: {ex.Message}");
            }

            WriteMcpFile(Path.Combine(McpDir, "result.json"), resultJson);
            Debug.Log($"[Iroca][MCP] CLI result: {resultJson}");
            Console.WriteLine($"[Iroca][MCP] {resultJson}");

            // batchmode の呼び出し側が終了コードで成否を検知できるようにする。
            // -quit だけでは RunFromCommandLine の成否に関わらず Unity は exit 0 で終了し、
            // 失敗(デコード失敗・ゾーン無し・書き込み失敗等)が握り潰されていた。result.json と
            // ログは上で出力済みなので、ここで成否に応じた終了コードで明示終了する。
            bool ok = false;
            try { ok = JsonUtility.FromJson<RecolorResult>(resultJson)?.ok ?? false; }
            catch { ok = false; }
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// テクスチャ読込 → <see cref="PixelProcessor.ProcessPixelsArray"/>（同期）→ PNG 書き出し。
        /// 全経路がここを通る。<see cref="ExportView.ApplyRecolor"/> のメインスレッド前処理と同じ手順。
        /// </summary>
        private static RecolorResult RunRecolorCore(
            string sourceAssetPath, List<ColorZone> zones, IrocaSessionState s,
            string outputAssetPath, List<string> warnings)
        {
            var result = new RecolorResult { warnings = warnings ?? new List<string>() };

            if (string.IsNullOrEmpty(outputAssetPath))
            { result.error = "output path is empty."; return result; }

            string srcAbs = ResolveExistingFile(sourceAssetPath);
            if (srcAbs == null)
            { result.error = $"source not found: {sourceAssetPath}"; return result; }

            var sorted = (zones ?? new List<ColorZone>()).Where(z => z != null && z.enabled).ToList();
            if (sorted.Count == 0)
            { result.error = "no enabled zones."; return result; }
            foreach (var z in sorted) { z.EnsureId(); z.UpdateCacheIfNeeded(); }

            // ── ディスクの PNG を直接読む（import 設定/readable に依存せず製品経路と一致させる） ──
            Color32[] pixels;
            int w, h;
            Texture2D loadTex = null;
            try
            {
                byte[] srcBytes = File.ReadAllBytes(srcAbs);
                loadTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!loadTex.LoadImage(srcBytes))
                { result.error = "failed to decode source image."; return result; }
                pixels = loadTex.GetPixels32();
                w = loadTex.width;
                h = loadTex.height;
            }
            finally
            {
                if (loadTex != null) UnityEngine.Object.DestroyImmediate(loadTex);
            }

            // 比較パネル/メトリクス用に、再着色前の画素を退避する(ProcessPixelsArray は in-place 変換)。
            var originalPixels = (Color32[])pixels.Clone();

            // v1: マスクはヘッドレス適用しない（masks=null は ProcessPixelsArray で安全に扱われる）。
            PixelProcessor.ProcessPixelsArray(
                pixels, w, h, null, sorted,
                s.edgeFeather, s.antiAliasCleanup,
                s.holeFillPasses, s.holeFillMinNeighbors, s.relaxedSatMin, s.relaxedSatRamp,
                0, 0, 0, 0,
                useDecontamination: s.useDecontamination, decontaminationRadius: s.decontaminationRadius);

            string outAbs = ResolveOutputPath(outputAssetPath);
            if (outAbs == null)
            { result.error = $"output must be a .png inside the project: {outputAssetPath}"; return result; }
            // 出力先が元テクスチャそのものだと、再着色結果で原本を上書きして戻せなくなる。
            // UI の「上書き保存」は明示操作＋確認ダイアログ＋Undo があるが、MCP/batchmode 経由には
            // 何も無く、引数のミス 1 つで非可逆に原本が失われる。ここで断る。
            // 比較は大小無視: 取りこぼして原本を壊すより、別ファイルを拒否する側に倒す。
            if (string.Equals(outAbs, srcAbs, StringComparison.OrdinalIgnoreCase))
            {
                result.error = "output must differ from source "
                             + $"(recoloring in place would destroy the original irreversibly): {outputAssetPath}";
                return result;
            }
            Texture2D outTex = null;
            try
            {
                outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.SetPixels32(pixels);
                byte[] png = outTex.EncodeToPNG();
                if (png == null) { result.error = "PNG encode failed."; return result; }

                string dir = Path.GetDirectoryName(outAbs);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // 出力先に既存 PNG があると、書き込み途中で落ちたときその既存物を壊す。
                // AtomicFile は一時ファイルへ書き切ってから置換する。
                AtomicFile.WriteAllBytes(outAbs, png);

                string rel = PathUtils.ToAssetsRelativeOrNull(outAbs);
                if (rel != null) AssetDatabase.ImportAsset(rel);
            }
            finally
            {
                if (outTex != null) UnityEngine.Object.DestroyImmediate(outTex);
            }

            var metrics = RecolorPreview.ComputeMetrics(originalPixels, pixels, w, h);
            result.changedPixels = metrics.changedPixels;
            result.changedFraction = metrics.changedFraction;
            result.largestComponentFraction = metrics.largestComponentFraction;
            result.changeComponentCount = metrics.componentCount;
            result.changeBBox = new[] { metrics.bboxX, metrics.bboxY, metrics.bboxW, metrics.bboxH };
            WritePreviewPanel(originalPixels, pixels, w, h, outAbs, srcAbs, result);

            result.ok = true;
            result.output = outAbs;
            result.width = w;
            result.height = h;
            result.zonesApplied = sorted.Count;
            return result;
        }

        /// <summary>
        /// 変換前/後から比較パネル PNG を組み立てて <paramref name="outAbs"/> の隣(&lt;stem&gt;_preview.png)へ書き出す。
        /// 生成できたら <see cref="RecolorResult.preview"/> とエージェント向け <see cref="RecolorResult.note"/> を設定。
        /// 失敗は非致命(本体出力は成功のまま)で warnings に残す。
        /// </summary>
        private static void WritePreviewPanel(
            Color32[] before, Color32[] after, int w, int h, string outAbs, string srcAbs, RecolorResult result)
        {
            // 比較パネルの行き先は outAbs から機械的に導く(&lt;stem&gt;_preview.png)ため、
            // 本体出力が原本と別でも、パネルだけが原本を指すことがある
            // (例: source=Assets/tex_preview.png, output=Assets/tex.png)。
            // 呼び出し側の output != source ガードはここを通らないので、同じ判定をこちらにも置く。
            // 目視検証用の後段生成のために原本を壊すことは絶対に許容できない。
            string previewAbs = MakePreviewPath(outAbs);
            if (string.Equals(previewAbs, srcAbs, StringComparison.OrdinalIgnoreCase))
            {
                result.warnings.Add(
                    "preview panel skipped: its path would overwrite the source texture "
                    + $"(rename the output so that <stem>_preview.png differs from the source): {previewAbs}");
                return;
            }

            Texture2D panelTex = null;
            try
            {
                var panel = RecolorPreview.BuildComparisonPanel(
                    before, after, w, h, RecolorPreview.DefaultMaxTile, out int pw, out int ph);
                panelTex = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
                panelTex.SetPixels32(panel);
                byte[] png = panelTex.EncodeToPNG();
                if (png == null) { result.warnings.Add("preview panel PNG encode failed."); return; }

                string dir = Path.GetDirectoryName(previewAbs);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                AtomicFile.WriteAllBytes(previewAbs, png);

                string rel = PathUtils.ToAssetsRelativeOrNull(previewAbs);
                if (rel != null) AssetDatabase.ImportAsset(rel);

                result.preview = previewAbs;
                result.note = "Open 'preview' to visually verify the result: left=before, middle=after, "
                    + "right=changed pixels (magenta). Scattered magenta or a low largestComponentFraction "
                    + "suggests over-selection; empty magenta where you expected change suggests it was missed. "
                    + "Adjust 'target'/'tolerance' and re-run if it does not match intent.";
            }
            catch (Exception ex)
            {
                result.warnings.Add($"preview generation failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (panelTex != null) UnityEngine.Object.DestroyImmediate(panelTex);
            }
        }

        private static string MakePreviewPath(string outAbs)
        {
            string dir = Path.GetDirectoryName(outAbs) ?? "";
            string stem = Path.GetFileNameWithoutExtension(outAbs);
            return Path.Combine(dir, stem + "_preview.png");
        }

        private static ColorZone BuildZone(ZoneDto z)
        {
            var zone = new ColorZone
            {
                name = z.name,
                enabled = true,
                mode = SelectionMode.ColorPick,
                sampleColor = ToColor(z.sample),
                sampleColorSet = true,
                targetColor = ToColor(z.target),
                tolerance = z.tolerance,
                valueBlend = z.valueBlend,
                edgeSoftness = z.edgeSoftness,
                saturationStrictness = z.saturationStrictness,
                saturationGuard = z.saturationGuard,
                chromaThreshold = z.chromaThreshold,
                highlightRecovery = z.highlightRecovery,
                highlightBandExpand = z.highlightBandExpand,
                applyHighlightWash = z.applyHighlightWash,
                autoHighlightSample = z.autoHighlightSample,
                autoRecolorAnchor = z.autoRecolorAnchor,
                outputSaturation = z.outputSaturation,
                shadowDesaturation = z.shadowDesaturation,
                shadowForgivenessSatMin = z.shadowForgivenessSatMin,
                layerIndex = z.layerIndex,
                useFloodFill = z.useFloodFill,
                // 未指定/長さ不足 = 負値 = 自動アンカリング（Harness.BuildZone と同一の写像）。
                seedUV = (z.seedUV != null && z.seedUV.Length >= 2)
                    ? new Vector2(z.seedUV[0], z.seedUV[1]) : new Vector2(-1f, -1f),
            };
            zone.EnsureId();
            zone.UpdateCacheIfNeeded();
            return zone;
        }

        private static Color ToColor(float[] c) =>
            new Color(
                c != null && c.Length > 0 ? c[0] : 0f,
                c != null && c.Length > 1 ? c[1] : 0f,
                c != null && c.Length > 2 ? c[2] : 0f, 1f);

        private static IrocaSessionState SettingsFromPreset(IrocaPresetData p)
        {
            var s = IrocaSessionState.CreateDefault();
            s.edgeFeather = p.edgeFeather;
            // advancedMode は UI 表示レベルのみで処理結果に影響しないため移送しない。
            s.antiAliasCleanup = p.antiAliasCleanup;
            s.holeFillPasses = p.holeFillPasses;
            s.holeFillMinNeighbors = p.holeFillMinNeighbors;
            s.relaxedSatMin = p.relaxedSatMin;
            s.relaxedSatRamp = p.relaxedSatRamp;
            s.useDecontamination = p.useDecontamination;
            s.decontaminationRadius = p.decontaminationRadius;
            return s;
        }

        private static IrocaSessionState SettingsFromDto(SettingsDto d)
        {
            var s = IrocaSessionState.CreateDefault();
            s.edgeFeather = d.edgeFeather;
            s.antiAliasCleanup = d.antiAliasCleanup;
            s.holeFillPasses = d.holeFillPasses;
            s.holeFillMinNeighbors = d.holeFillMinNeighbors;
            s.relaxedSatMin = d.relaxedSatMin;
            s.relaxedSatRamp = d.relaxedSatRamp;
            s.useDecontamination = d.useDecontamination;
            s.decontaminationRadius = d.decontaminationRadius;
            return s;
        }

        /// <summary>
        /// プリセット指定（ファイルパス / プリセット名 / インライン JSON）を <see cref="IrocaPresetData"/> へ解決する。
        /// </summary>
        private static IrocaPresetData ResolvePreset(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;

            string abs = ResolveExistingFile(spec);
            if (abs != null) return PresetStore.Load(abs);

            string trimmed = spec.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                try { return JsonUtility.FromJson<IrocaPresetData>(spec); }
                catch { return null; }
            }

            foreach (var folder in new[] { PresetStore.ProjectPresetFolder, PresetStore.UserPresetFolder })
            {
                string p = PresetStore.PresetFilePath(folder, spec);
                if (File.Exists(p)) return PresetStore.Load(p);
            }
            return null;
        }

        private static bool HasEmbeddedMask(IrocaPresetData p)
        {
            if (p == null) return false;
            if (p.maskWidth <= 0 || p.maskHeight <= 0) return false;
            if (!string.IsNullOrEmpty(p.commonMaskBase64)) return true;
            return p.zoneMasks != null && p.zoneMasks.Any(z => z != null && !string.IsNullOrEmpty(z.maskBase64));
        }

        private static void AddPresets(PresetList list, string folder, string scope)
        {
            foreach (var path in PresetStore.ListJson(folder))
            {
                list.presets.Add(new PresetEntry
                {
                    name = Path.GetFileNameWithoutExtension(path),
                    path = path,
                    scope = scope,
                });
            }
        }

        // 絶対 / プロジェクト相対 / Assets 相対のいずれかを実在する絶対パスへ解決する。
        // プロジェクトルート配下でなければ拒否する（範囲外ファイルの読み取りを防ぐ）。無ければ null。
        private static string ResolveExistingFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string abs = ToProjectAbsolute(path);
            if (abs == null || !IsWithinProjectRoot(abs)) return null;
            return File.Exists(abs) ? abs : null;
        }

        // 出力パスを絶対パスへ解決する（存在不要）。プロジェクトルート配下かつ .png のみ許可する。
        // 自動化/MCP 経由での範囲外書き込み（任意ファイルの上書き）と非画像ファイル生成を防ぐ。
        // 条件を満たさなければ null。
        private static string ResolveOutputPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string abs = ToProjectAbsolute(path);
            if (abs == null || !IsWithinProjectRoot(abs)) return null;
            if (!abs.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            return abs;
        }

        // 絶対パスはそのまま、相対パスはプロジェクトルート基準で絶対化する。失敗時は null。
        private static string ToProjectAbsolute(string path)
        {
            try
            {
                string projRoot = Path.GetDirectoryName(Application.dataPath);
                return Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(projRoot, path));
            }
            catch { return null; }
        }

        // 解決済み絶対パスがプロジェクトルート配下かを判定する（パストラバーサル/範囲外 I/O 防止）。
        private static bool IsWithinProjectRoot(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return false;
            string projRoot;
            try { projRoot = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)); }
            catch { return false; }
            // 末尾セパレータを付けて "Proj" と "ProjectX" の前方一致誤判定を防ぐ。
            string rootWithSep = projRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return absPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static void WriteMcpFile(string path, string content)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 呼び出し側は result.json を読んで成否を判断する。直書きだとクラッシュ時に
                // 途中まで書けた壊れた JSON を読ませてしまう。
                AtomicFile.WriteAllText(path, content);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca][MCP] failed to write {path}: {ex.Message}");
            }
        }

        private static string ExtractVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(IrocaAutomation).Assembly);
                return info != null && !string.IsNullOrEmpty(info.version) ? info.version : "unknown";
            }
            catch { return "unknown"; }
        }

        private static string Json(RecolorResult r) => JsonUtility.ToJson(r);

        private static string Fail(string error, List<string> warnings = null)
        {
            var r = new RecolorResult { ok = false, error = error, warnings = warnings ?? new List<string>() };
            return JsonUtility.ToJson(r);
        }
    }
}
