// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// Unity MCP / AI エージェントから VACC をヘッドレス駆動するための自動化 API。
    ///
    /// UI（VACCWindow / ExportView）を介さず、テクスチャ読込 → 再着色 → PNG 出力までを
    /// 静的メソッド一発で実行できる。<see cref="ExportView"/> の実出力経路（ディスクの PNG を
    /// 直接読み、<see cref="PixelProcessor.ProcessPixelsArray(Color32[],int,int,MaskSnapshot,System.Collections.Generic.IList{ColorZone},float,int,int,int,float,float,int,int,int,int,bool,int,float,IDebugCapture)"/>
    /// に通す）をそのまま同期で再現するので、製品の出力と一致する。
    ///
    /// 呼び出し経路は 3 つ。いずれも同じ中核（<see cref="RunRecolorCore"/>）を通る:
    ///   1. 静的 API: <see cref="RecolorByPreset"/> / <see cref="RecolorWithZones"/> 等。戻り値は JSON 文字列。
    ///      生 C# 実行が可能な MCP クライアント・EditMode テストから直接呼ぶ。
    ///   2. MCPForUnity カスタムツール: <c>Code/McpIntegration/VACCMcpTools.cs</c> の <c>vacc_recolor</c> 等。
    ///      <c>execute_custom_tool</c> から本クラスの公開静的 API を呼ぶ。MCPForUnity 導入時のみ
    ///      コンパイルされる別 asmdef（VACC_MCP_PRESENT ゲート）で、配布パッケージ本体は依存ゼロを保つ。
    ///      生 C# 実行に非対応のクライアントでも駆動できる（Tools メニューには何も追加しない）。
    ///   3. batchmode CLI: <c>Unity.exe -batchmode -executeMethod VRCAvatarColorChanger.VACCAutomation.RunFromCommandLine ...</c>。
    ///
    /// v1 ではプリセット同梱マスクはヘッドレス適用しない（パーツ単位の粗いマスクは後続対応）。
    /// マスクを含むプリセットを渡した場合は結果 JSON の warnings で明示する。
    /// </summary>
    public static class VACCAutomation
    {
        // ジョブ/結果ファイルの置き場。git 非追跡の UserSettings 配下に置き、Assets の import 揺れを避ける。
        private static string McpDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "UserSettings/VACC/mcp"));

        // ───────────────────────────── DTO ─────────────────────────────

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
            public List<string> warnings = new List<string>();
        }

        // --zones / zones モード用のフラットなゾーン設定（Harness.ZoneCfg と同一スキーマ、色は [r,g,b] 0..1）。
        // JsonUtility 用に public フィールドで定義する。
        [Serializable]
        private class ZoneDto
        {
            public string name = "Zone";
            public float[] sample = { 1f, 1f, 1f };
            public float[] target = { 0f, 0f, 0f };
            public float tolerance = 0.2f;
            public float valueBlend = 1.0f;
            public float edgeSoftness = 0.0f;
            public float saturationStrictness = 0.5f;
            public float saturationGuard = 0.0f;
            public float chromaThreshold = 0.05f;
            public float shadowDesaturation = 0.35f;
            public float shadowForgivenessSatMin = 0.05f;
            public float outputSaturation = 1.0f;
            public bool highlightRecovery = false;
            public bool highlightBandExpand = true;
            public bool applyHighlightWash = false;
            public int layerIndex = 0;
        }

        [Serializable]
        private class SettingsDto
        {
            public float edgeFeather = 0.0f;
            public int antiAliasCleanup = 3;
            public int holeFillPasses = 5;
            public int holeFillMinNeighbors = 4;
            public float relaxedSatMin = 0.02f;
            public float relaxedSatRamp = 0.08f;
            public bool useDecontamination = true;
            public int decontaminationRadius = 4;
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
            public string name = "com.yukkuri-aoba.vrc-avatar-color-changer";
            public string version = "";
            public string[] methods;
            public string[] notes;
            public string[] fieldDocs;
            public ZoneDto zoneDefaults = new ZoneDto();
            public SettingsDto settingsDefaults = new SettingsDto();
            public JobRequest jobExample = new JobRequest();
        }

        // ───────────────────────── 公開 API ─────────────────────────

        /// <summary>パッケージ名とバージョンを JSON で返す。</summary>
        public static string GetVersion()
        {
            string version = "unknown";
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VACCAutomation).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.version)) version = info.version;
            }
            catch { /* 埋め込み配置などで解決できない場合は unknown のまま */ }
            return $"{{\"ok\":true,\"name\":\"com.yukkuri-aoba.vrc-avatar-color-changer\",\"version\":\"{version}\"}}";
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
                    "MCP 経路: execute_custom_tool(\"vacc_recolor\", { source, output, preset|zones }) で呼ぶ（メニュー非依存）。",
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
        // （Code/McpIntegration/VACCMcpTools.cs の vacc_recolor 等）経由で公開静的 API を呼ぶ。
        // Tools メニューにはウィンドウ起動の単一項目だけを残し、サブメニュー二重表示を避ける。

        // ─────────────────────── batchmode CLI ───────────────────────

        /// <summary>
        /// <c>Unity.exe -batchmode -quit -executeMethod VRCAvatarColorChanger.VACCAutomation.RunFromCommandLine
        ///   -vaccSource &lt;path&gt; -vaccOutput &lt;path&gt; (-vaccPreset &lt;path|name&gt; | -vaccZonesFile &lt;json&gt; | -vaccJob &lt;json&gt;)</c>
        /// で呼ぶ、MCP を介さない完全ヘッドレス経路。結果は Console と UserSettings/VACC/mcp/result.json に出す。
        /// </summary>
        public static void RunFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string source = GetArg(args, "-vaccSource");
            string output = GetArg(args, "-vaccOutput");
            string preset = GetArg(args, "-vaccPreset");
            string zonesFile = GetArg(args, "-vaccZonesFile");
            string jobFile = GetArg(args, "-vaccJob");

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
            Debug.Log($"[VACC][MCP] CLI result: {resultJson}");
            Console.WriteLine($"[VACC][MCP] {resultJson}");
        }

        // ─────────────────────── 中核 ───────────────────────

        /// <summary>
        /// テクスチャ読込 → <see cref="PixelProcessor.ProcessPixelsArray"/>（同期）→ PNG 書き出し。
        /// 全経路がここを通る。<see cref="ExportView.ApplyRecolor"/> のメインスレッド前処理と同じ手順。
        /// </summary>
        private static RecolorResult RunRecolorCore(
            string sourceAssetPath, List<ColorZone> zones, VACCSessionState s,
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

            // v1: マスクはヘッドレス適用しない（masks=null は ProcessPixelsArray で安全に扱われる）。
            PixelProcessor.ProcessPixelsArray(
                pixels, w, h, null, sorted,
                s.edgeFeather, s.antiAliasCleanup,
                s.holeFillPasses, s.holeFillMinNeighbors, s.relaxedSatMin, s.relaxedSatRamp,
                0, 0, 0, 0,
                useDecontamination: s.useDecontamination, decontaminationRadius: s.decontaminationRadius);

            // ── PNG エンコード → 書き出し → AssetDatabase 取り込み（Assets 配下のみ） ──
            string outAbs = ResolveOutputPath(outputAssetPath);
            Texture2D outTex = null;
            try
            {
                outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.SetPixels32(pixels);
                byte[] png = outTex.EncodeToPNG();
                if (png == null) { result.error = "PNG encode failed."; return result; }

                string dir = Path.GetDirectoryName(outAbs);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outAbs, png);

                string rel = VACCWindow.ToAssetsRelative(outAbs);
                if (rel != null) AssetDatabase.ImportAsset(rel);
            }
            finally
            {
                if (outTex != null) UnityEngine.Object.DestroyImmediate(outTex);
            }

            result.ok = true;
            result.output = outAbs;
            result.width = w;
            result.height = h;
            result.zonesApplied = sorted.Count;
            return result;
        }

        // ─────────────────────── ヘルパー ───────────────────────

        private static ColorZone BuildZone(ZoneDto z)
        {
            var zone = new ColorZone
            {
                name = z.name,
                enabled = true,
                mode = SelectionMode.ColorPick,
                sampleColor = ToColor(z.sample),
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
                autoHighlightSample = false,
                autoRecolorAnchor = false,
                outputSaturation = z.outputSaturation,
                shadowDesaturation = z.shadowDesaturation,
                shadowForgivenessSatMin = z.shadowForgivenessSatMin,
                layerIndex = z.layerIndex,
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

        private static VACCSessionState SettingsFromPreset(VACCPresetData p)
        {
            var s = VACCSessionState.CreateDefault();
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

        private static VACCSessionState SettingsFromDto(SettingsDto d)
        {
            var s = VACCSessionState.CreateDefault();
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
        /// プリセット指定（ファイルパス / プリセット名 / インライン JSON）を <see cref="VACCPresetData"/> へ解決する。
        /// </summary>
        private static VACCPresetData ResolvePreset(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;

            // 1) ファイルパスとして解決できれば PresetStore.Load を使う。
            string abs = ResolveExistingFile(spec);
            if (abs != null) return PresetStore.Load(abs);

            // 2) インライン JSON（'{' 始まり）なら直接パース。
            string trimmed = spec.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                try { return JsonUtility.FromJson<VACCPresetData>(spec); }
                catch { return null; }
            }

            // 3) プリセット名としてプロジェクト→ユーザー保存先を探索。
            foreach (var folder in new[] { PresetStore.ProjectPresetFolder, PresetStore.UserPresetFolder })
            {
                string p = PresetStore.PresetFilePath(folder, spec);
                if (File.Exists(p)) return PresetStore.Load(p);
            }
            return null;
        }

        private static bool HasEmbeddedMask(VACCPresetData p)
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

        // 絶対 / プロジェクト相対 / Assets 相対のいずれかを実在する絶対パスへ解決する。無ければ null。
        private static string ResolveExistingFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) return Path.GetFullPath(path);
            string projRoot = Path.GetDirectoryName(Application.dataPath);
            string combined = Path.GetFullPath(Path.Combine(projRoot, path));
            return File.Exists(combined) ? combined : null;
        }

        // 出力パスを絶対パスへ解決する（存在不要）。
        private static string ResolveOutputPath(string path)
        {
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            string projRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projRoot, path));
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
                File.WriteAllText(path, content);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VACC][MCP] failed to write {path}: {ex.Message}");
            }
        }

        private static string ExtractVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VACCAutomation).Assembly);
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
