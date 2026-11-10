// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// Unity MCP / AI エージェントから Iroca をヘッドレス駆動するための自動化 API。
    ///
    /// UI（IrocaWindow / ExportView）を介さず、テクスチャ読込 → 再着色 → PNG 出力までを
    /// 静的メソッド一発で実行できる。原本を <see cref="ExportPipeline.ReadWorkingPixelsNative"/> で読み、
    /// ゾーン・処理設定・マスクを編集状態（<see cref="IrocaSessionState"/>）にまとめて、非破壊ビルドと同じ入口
    /// <see cref="SessionRecolor.Apply"/> に通すので、製品の出力と一致する。
    ///
    /// 呼び出し経路は 3 つ。1 と 3 は同じ中核（<see cref="RunRecolorCore"/>）を、2 は同じ <see cref="SessionRecolor.Apply"/> を通る:
    ///   1. 静的 API: <see cref="RecolorByPreset"/> / <see cref="RecolorWithZones"/> 等。戻り値は JSON 文字列。
    ///      生 C# 実行が可能な MCP クライアント・EditMode テストから直接呼ぶ。
    ///   2. MCPForUnity カスタムツール: <c>Code/McpIntegration/IrocaMcpTools.cs</c> の <c>iroca_inspect</c>・
    ///      <c>iroca_recolor</c>・<c>iroca_open</c>（<see cref="Inspect"/>・<see cref="Recolor"/>・<see cref="Open"/>）。
    ///      MCPForUnity 導入時のみコンパイルされる別 asmdef（IROCA_MCP_PRESENT ゲート）で、配布パッケージ本体は
    ///      依存ゼロを保つ。利用者向けの説明はマニュアル「AI エージェントから使う（MCP）」。
    ///      生 C# 実行に非対応のクライアントでも駆動できる（Tools メニューには何も追加しない）。
    ///   3. batchmode CLI: <c>Unity.exe -batchmode -executeMethod Iroca.IrocaAutomation.RunFromCommandLine ...</c>。
    ///
    /// プリセットに入っているマスク（共通の除外・ゾーン別の除外・含める）も、UI でプリセットを読み込んだときと同じく当てる。
    /// </summary>
    public static partial class IrocaAutomation {
        // package.json の name。NDMF の識別子(IrocaNdmfPlugin.QualifiedName・IrocaPreviewFilter のトグルキー)とは
        // 意図的に共有しない(あちらはパッケージ名が変わっても変えてはいけない)。
        private const string PackageName = "com.yukkuri-aoba.iroca";

        /// <summary>再着色 1 回分の結果。JSON 文字列としてエージェントへ返す。</summary>
        [Serializable]
        public class RecolorResult {
            public bool ok;
            public string error = "";
            public string output = "";
            public int width;
            public int height;
            public int zonesApplied;
            // 比較パネル PNG(左=変換前 / 中=変換後 / 右=変化画素をマゼンタ表示)の絶対パス(UserSettings/Iroca/mcp/preview。
            // Assets には置かない)。生成に失敗した場合は空文字(理由は warnings)。エージェントはこれを画像として開いて目視検証する。
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
        // internal: ReproDump(再現データ書き出し)が同一スキーマで zones.json を書くために共有する。
        [Serializable]
        internal class ZoneDto {
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
            public float shadowValueFloor = ZonesJsonDefaults.ShadowValueFloor;
            public float partSatCeiling = ZonesJsonDefaults.PartSatCeiling;
            public float partHueBand = ZonesJsonDefaults.PartHueBand;
            public float chromaCeiling = ZonesJsonDefaults.ChromaCeiling;
            public float holeFillMargin = ZonesJsonDefaults.HoleFillMargin;
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
            // スポイト位置 [u,v]（0-1）。未指定/長さ不足 = 位置なし。この位置を含む連結成分は、
            // 連続領域モードの包絡ゲート（陰影の明度下限など）で落とさない。
            public float[] sampleUV = null;
        }

        [Serializable]
        internal class SettingsDto {
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
        internal class ZonesRequest {
            public List<ZoneDto> zones = new List<ZoneDto>();
            public SettingsDto settings = new SettingsDto();
        }

        [Serializable]
        private class JobRequest {
            // "preset" | "zones"
            public string mode = "preset";
            public string source = "";
            public string output = "";
            // preset モード: プリセットのファイルパス / 名前 / インライン JSON のいずれか。
            public string preset = "";
            // zones モード: フラットなゾーン設定。
            public ZonesRequest zones;
            // true なら、出力先に既にあるファイルを置き換える(既定は断る。コマンドラインの -irocaOverwrite と同じ)
            public bool overwrite;
        }

        [Serializable]
        private class PresetEntry { public string name = ""; public string path = ""; public string scope = ""; }

        [Serializable]
        private class PresetList { public List<PresetEntry> presets = new List<PresetEntry>(); }

        [Serializable]
        private class SchemaDoc {
            public string name = PackageName;
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
            => $"{{\"ok\":true,\"name\":\"{PackageName}\",\"version\":\"{ExtractVersion()}\"}}";

        /// <summary>
        /// リクエスト/プリセットの JSON スキーマ・既定値・フィールド説明を JSON で返す。
        /// エージェントが呼び出し方を自己発見するための一次情報。
        /// </summary>
        public static string DescribeSchema() {
            var doc = new SchemaDoc {
                version = ExtractVersion(),
                methods = new[] {
                    "RecolorByPreset(sourceAssetPath, presetPathOrNameOrJson, outputAssetPath[, overwrite]) -> RecolorResult",
                    "RecolorWithZones(sourceAssetPath, zonesRequestJson, outputAssetPath[, overwrite]) -> RecolorResult",
                    "Inspect(target, maxColors, pointsUv, coverage) -> { kind, ndmf, ai, recolorRunning, presets, objects, "
                        + "textures:[{path, colors:[{hex, uv}], thumbnail, zonesFrom, zones, commonExcludedFraction, meshes, points}] }"
                        + "（target なしでシーンのアバター一覧、シーンのオブジェクトで使っているテクスチャ、テクスチャ・マテリアル・レシピで主な色と代表点・"
                        + "今のゾーンとマスク。pointsUv（u, v の並び）で指定した場所の色・塗っているゾーン・マスク・UV の島。coverage でゾーンごとのおおよその割合）",
                    "Recolor(requestJson) -> { ok, texture, apply, avatar, recipe, output, preview, zones, maskEdits, warnings } か "
                        + "{ pending:true }（依頼 JSON: texture, changes:[{name, at:[u,v] | from, to}], "
                        + "masks:[{op=paint|dont_paint|only|clear, at:[u,v], zone, common, by=ai|island|other_meshes, size=auto|fine|coarse}], "
                        + "remove, reset, preset, apply=avatar|png|preview, avatar, output, overwrite, autoTune）",
                    "RecolorStatus() -> pending を返した Recolor の続き（終わるまで pending、終われば Recolor と同じ形）",
                    "RecolorStatus(requestJson) -> RecolorStatus() と同じ。Recolor に渡したのと同じ依頼 JSON を渡すと、その依頼の続きか"
                        + "確かめる（別の依頼が動いていれば error。MCP の問い合わせ直しはこれを使う）",
                    "Open(target) -> { ok, texture, recipe }（いろかの窓で開く。target はレシピ・テクスチャ・マテリアル・シーンのオブジェクト）",
                    "ListPresets() -> { presets:[{name,path,scope}] }",
                    "GetVersion() -> { name, version }",
                    "DescribeSchema() -> this document",
                },
                notes = new[] {
                    "パスは Assets 相対（Assets/...）・プロジェクト相対・絶対のいずれも可。出力は Assets/ の下の .png だけで、"
                        + "元のテクスチャそのものへは書かない。既にあるファイルは overwrite が true のときだけ置き換える"
                        + "（RecolorByPreset・RecolorWithZones の 3 引数の版は断る。コマンドラインは -irocaOverwrite、ジョブの JSON は overwrite）。",
                    "色は [r,g,b]（0..1）。enabled なゾーンが 1 つも無いと error になる。",
                    "範囲の外・NaN の値（ゾーンの割合は 0..1、settings は窓のスライダーと同じ範囲）は断らずに範囲へ丸め、"
                        + "丸めた欄を warnings で知らせる（プリセットも同じ）。",
                    "プリセットに入っているマスク（除外・含める）も UI と同じく当てる。",
                    "MCP 経路: iroca_inspect・iroca_recolor・iroca_open（引数は Inspect・Recolor の依頼 JSON・Open と同じ）。",
                    "結果には 'preview'（比較パネル PNG のパス。UserSettings/Iroca/mcp/preview に書き、Assets には置かない。"
                        + "左=変換前 / 中=変換後 / 右=変化画素をマゼンタ表示）と "
                        + "変化メトリクス（changedPixels / changedFraction / largestComponentFraction / changeComponentCount / changeBBox）が付く。",
                    "検証ループ: 再着色 → 'preview' 画像を開いて意図通りか目視 → ずれていれば target/tolerance を調整して再実行。"
                        + "largestComponentFraction が小さい・changeComponentCount が多い＝変化が散在＝過検出の疑い。",
                },
                fieldDocs = new[] {
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
            doc.jobExample = new JobRequest {
                mode = "preset",
                source = "Assets/Textures/body.png",
                output = "Assets/Textures/body_recolored.png",
                preset = "MyPreset",
                zones = new ZonesRequest {
                    zones = new List<ZoneDto> { new ZoneDto { name = "Example", sample = new[] { 1f, 1f, 1f }, target = new[] { 0.1f, 0.3f, 0.8f }, tolerance = 0.25f } },
                    settings = new SettingsDto(),
                },
            };
            return JsonUtility.ToJson(doc, true);
        }

        /// <summary>プロジェクト/ユーザー保存先のプリセット一覧を JSON で返す。</summary>
        public static string ListPresets() {
            var list = new PresetList();
            AddPresets(list, PresetStore.ProjectPresetFolder, "project");
            AddPresets(list, PresetStore.UserPresetFolder, "user");
            return JsonUtility.ToJson(list, true);
        }

        /// <summary>
        /// プリセットを使ってテクスチャを再着色し PNG を書き出す。
        /// <paramref name="presetPathOrNameOrJson"/> はファイルパス・プリセット名・インライン JSON のいずれでも可。
        /// 出力先に既にファイルがあれば断る(置き換えるときは overwrite を渡す版。決まりは <see cref="AutomationOutputRules"/>)。
        /// </summary>
        public static string RecolorByPreset(string sourceAssetPath, string presetPathOrNameOrJson, string outputAssetPath)
            => RecolorByPreset(sourceAssetPath, presetPathOrNameOrJson, outputAssetPath, overwrite: false);

        /// <summary>
        /// <see cref="RecolorByPreset(string, string, string)"/> の、出力先に既にあるファイルを置き換えるかを選べる版。
        /// 置き換えると、その画像を使うマテリアル・アバターの見た目も変わるので、利用者に確かめてから true にする。
        /// </summary>
        public static string RecolorByPreset(string sourceAssetPath, string presetPathOrNameOrJson, string outputAssetPath,
            bool overwrite) {
            var warnings = new List<string>();
            try {
                var preset = ResolvePreset(presetPathOrNameOrJson);
                if (preset == null) {
                    return Fail($"preset not found / parse failed: {presetPathOrNameOrJson}");
                }

                string abs = ResolveExistingFile(sourceAssetPath);
                string rel = abs != null ? PathUtils.ToAssetsRelativeOrNull(abs) : null;
                string guid = rel != null ? AssetDatabase.AssetPathToGUID(rel) : "";
                return Json(RunRecolorCore(sourceAssetPath, SessionFromPreset(preset, guid, warnings), outputAssetPath, overwrite,
                    warnings));
            } catch (Exception ex) {
                return Fail($"{ex.GetType().Name}: {ex.Message}", warnings);
            }
        }

        /// <summary>
        /// フラットなゾーン設定 JSON（<c>{"zones":[...],"settings":{...}}</c>）で再着色して PNG を書き出す。
        /// プリセット保存を経由せず、その場の色指定で変換したいときに使う。
        /// 出力先に既にファイルがあれば断る(置き換えるときは overwrite を渡す版)。
        /// </summary>
        public static string RecolorWithZones(string sourceAssetPath, string zonesRequestJson, string outputAssetPath)
            => RecolorWithZones(sourceAssetPath, zonesRequestJson, outputAssetPath, overwrite: false);

        /// <summary>
        /// <see cref="RecolorWithZones(string, string, string)"/> の、出力先に既にあるファイルを置き換えるかを選べる版。
        /// </summary>
        public static string RecolorWithZones(string sourceAssetPath, string zonesRequestJson, string outputAssetPath,
            bool overwrite) {
            var warnings = new List<string>();
            try {
                ZonesRequest req;
                try { req = JsonUtility.FromJson<ZonesRequest>(zonesRequestJson); } catch (Exception ex) { return Fail($"zones JSON parse failed: {ex.Message}"); }
                if (req == null || req.zones == null || req.zones.Count == 0) {
                    return Fail("zones JSON has no zones.");
                }

                var settings = req.settings ?? new SettingsDto();
                var state = new IrocaSessionState { zones = req.zones.Select(BuildZone).ToList() };
                SettingsFromDto(settings).CopyTo(state);
                ClampAndReportOutOfRange(RecolorValueRanges.ClampedSettingNames(
                        settings.edgeFeather, settings.antiAliasCleanup, settings.holeFillPasses, settings.holeFillMinNeighbors,
                        settings.relaxedSatMin, settings.relaxedSatRamp, settings.decontaminationRadius),
                    state.zones, warnings);
                return Json(RunRecolorCore(sourceAssetPath, state, outputAssetPath, overwrite, warnings));
            } catch (Exception ex) {
                return Fail($"{ex.GetType().Name}: {ex.Message}", warnings);
            }
        }

        // メニュー経路（execute_menu_item）は廃止。MCP からの駆動は MCPForUnity カスタムツール
        // （Code/McpIntegration/IrocaMcpTools.cs）経由で公開静的 API を呼ぶ。
        // Tools メニューにはウィンドウ起動の単一項目だけを残し、サブメニュー二重表示を避ける。

        /// <summary>
        /// <c>Unity.exe -batchmode -quit -executeMethod Iroca.IrocaAutomation.RunFromCommandLine
        ///   -irocaSource &lt;path&gt; -irocaOutput &lt;path&gt; (-irocaPreset &lt;path|name&gt; | -irocaZonesFile &lt;json&gt; | -irocaJob &lt;json&gt;)
        ///   [-irocaOverwrite]</c>
        /// で呼ぶ、MCP を介さない完全ヘッドレス経路。結果は Console と UserSettings/Iroca/mcp/result.json に出す。
        /// batchmode のときだけ成否を終了コードにして Unity を終える(開いているエディタから呼ばれたときは終えない)。
        /// </summary>
        public static void RunFromCommandLine() {
            string[] args = Environment.GetCommandLineArgs();
            string source = GetArg(args, "-irocaSource");
            string output = GetArg(args, "-irocaOutput");
            string preset = GetArg(args, "-irocaPreset");
            string zonesFile = GetArg(args, "-irocaZonesFile");
            string jobFile = GetArg(args, "-irocaJob");
            // 出力先に既にあるファイルを置き換える(無ければ断る。静的 API・MCP と同じ決まり)。
            bool overwrite = HasArg(args, "-irocaOverwrite");

            string resultJson;
            try {
                if (!string.IsNullOrEmpty(jobFile)) {
                    var job = JsonUtility.FromJson<JobRequest>(File.ReadAllText(jobFile));
                    if (job != null && string.Equals(job.mode, "zones", StringComparison.OrdinalIgnoreCase)) {
                        resultJson = RecolorWithZones(job.source, job.zones != null ? JsonUtility.ToJson(job.zones) : "", job.output,
                            overwrite || job.overwrite);
                    } else if (job != null) {
                        resultJson = RecolorByPreset(job.source, job.preset, job.output, overwrite || job.overwrite);
                    } else {
                        resultJson = Fail("job file parse failed.");
                    }
                } else if (!string.IsNullOrEmpty(zonesFile)) {
                    resultJson = RecolorWithZones(source, File.ReadAllText(zonesFile), output, overwrite);
                } else {
                    resultJson = RecolorByPreset(source, preset, output, overwrite);
                }
            } catch (Exception ex) {
                resultJson = Fail($"{ex.GetType().Name}: {ex.Message}");
            }

            WriteMcpFile(Path.Combine(AutomationOutputRules.McpDir, "result.json"), resultJson);
            Debug.Log($"[Iroca][MCP] CLI result: {resultJson}");
            Console.WriteLine($"[Iroca][MCP] {resultJson}");

            // batchmode の呼び出し側が終了コードで成否を検知できるようにする。
            // -quit だけでは RunFromCommandLine の成否に関わらず Unity は exit 0 で終了し、
            // 失敗(デコード失敗・ゾーン無し・書き込み失敗等)が握り潰されていた。result.json と
            // ログは上で出力済みなので、ここで成否に応じた終了コードで明示終了する。
            // 開いているエディタの中で呼ばれた(AI のコード実行や、利用者の自動処理の途中の 1 段など)ときに
            // 終了すると、保存していないシーンの変更ごと Unity が閉じる。終えるのは batchmode のときだけ
            // (BuildHelper と同じ形)。
            if (!Application.isBatchMode) {
                return;
            }
            bool ok = false;
            try { ok = JsonUtility.FromJson<RecolorResult>(resultJson)?.ok ?? false; } catch { ok = false; }
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// テクスチャ読込 → <see cref="SessionRecolor.Apply"/>（同期）→ PNG 書き出し。
        /// 全経路がここを通る。原本は製品の書き出しと共有の <see cref="ExportService.ReadSource"/> を
        /// imported=null で呼んで読む（取り込み済みテクスチャへのフォールバックはしない。PNG/JPG のみ）。
        /// 画素はヒープの外に置く（色替え前の写しと、色替えした画素。0.4.0 の計画の A6）。
        /// 色替えは非破壊ビルドと同じ入口なので、有効なゾーンだけを並び順のまま、state のマスクを当てて処理する。
        /// 出力先の決まり(Assets/ の下の .png・既にあれば overwrite のときだけ・元と別)と比較画像の置き場は MCP の
        /// <see cref="Recolor"/> と同じ(<see cref="AutomationOutputRules"/>)。出力先は重い色替えの前に確かめる。
        /// 静的 API と batchmode の経路。MCP の <see cref="Recolor"/> は同じ <see cref="SessionRecolor.Apply"/> を
        /// 自分の手順(作業解像度の比較画像・背景での書き出し)で呼ぶ。
        /// </summary>
        private static unsafe RecolorResult RunRecolorCore(
            string sourceAssetPath, IrocaSessionState state,
            string outputAssetPath, bool overwrite, List<string> warnings) {
            var result = new RecolorResult { warnings = warnings ?? new List<string>() };

            string srcAbs = ResolveExistingFile(sourceAssetPath);
            if (srcAbs == null) { result.error = $"source not found: {sourceAssetPath}"; return result; }

            result.error = AutomationOutputRules.ResolvePngOutput(outputAssetPath, null, overwrite, srcAbs, OverwriteHint,
                out string outAbs);
            if (result.error != null) {
                return result;
            }
            result.error = "";

            int enabledCount = RecolorInput.CountEnabled(state.zones);
            if (enabledCount == 0) { result.error = "no enabled zones."; return result; }
            foreach (var z in state.zones) { if (z != null && z.enabled) { z.EnsureId(); z.UpdateCacheIfNeeded(); } }

            // ── ディスクの PNG を直接読む（import 設定/readable に依存せず製品経路と一致させる） ──
            // imported が null なら ExportPipeline のフォールバックは起きず PNG/JPG のみを受け付ける。
            if (!ExportPipeline.HasPixels(ExportService.ReadSource(srcAbs, null, out NativePixels pixels))) {
                result.error = "failed to decode source image.";
                return result;
            }

            NativePixels originalPixels = null;
            NativeMemoryLifecycle.WorkStarted();
            try {
                int w = pixels.Width, h = pixels.Height;
                // 比較パネル/メトリクス用に、再着色前の画素を退避する(色替えは in-place 変換)。
                originalPixels = Duplicate(pixels);

                SessionRecolor.Apply(pixels.Ptr, w, h, state);

                if (!TryWritePng(pixels, outAbs, srcAbs)) { result.error = "PNG encode failed."; return result; }

                var m = RecolorPreview.ComputeMetrics(originalPixels.Ptr, pixels.Ptr, w, h);
                result.changedPixels = m.changedPixels;
                result.changedFraction = m.changedFraction;
                result.largestComponentFraction = m.largestComponentFraction;
                result.changeComponentCount = m.componentCount;
                result.changeBBox = new[] { m.bboxX, m.bboxY, m.bboxW, m.bboxH };
                WritePreviewPanel(originalPixels, pixels, srcAbs, result);

                result.ok = true;
                result.output = outAbs;
                result.width = w;
                result.height = h;
                result.zonesApplied = enabledCount;
                return result;
            } finally {
                originalPixels?.Release();
                pixels.Release();
                NativeMemoryLifecycle.WorkEnded();
                ReleaseMemoryIfNoWindow();
            }
        }

        // 出力先に既にファイルがあるときの案内に入れる、置き換えの許しの渡し方。
        private const string OverwriteHint = "overwrite = true (the RecolorByPreset/RecolorWithZones overload with 'overwrite'; "
            + "-irocaOverwrite or \"overwrite\": true in the job file on the command line)";

        /// <summary>
        /// 変換前/後から比較パネル PNG を組み立てて、MCP の <see cref="Recolor"/> と同じ置き場
        /// (<see cref="AutomationOutputRules.PreviewPanelPath"/>。UserSettings の下で、取り込まない)へ書く。
        /// 生成できたら <see cref="RecolorResult.preview"/> とエージェント向け <see cref="RecolorResult.note"/> を設定。
        /// 失敗は非致命(本体出力は成功のまま)で warnings に残す。
        /// 以前は出力の隣(&lt;stem&gt;_preview.png)へ書いて取り込んでいたので、出力先ごとに Assets にテクスチャが増え、
        /// 名前しだいで原本を指すこともあった(その判定は、置き場が Assets の外になって要らなくなった)。
        /// </summary>
        private static unsafe void WritePreviewPanel(NativePixels before, NativePixels after, string srcAbs, RecolorResult result) {
            try {
                var panel = RecolorPreview.BuildComparisonPanel(
                    before.Ptr, after.Ptr, before.Width, before.Height, RecolorPreview.DefaultMaxTile, out int pw, out int ph);
                byte[] png;
                try { png = ExportPipeline.EncodePng(panel, pw, ph); } catch (Exception) { result.warnings.Add("preview panel PNG encode failed."); return; }
                string previewAbs = AutomationOutputRules.PreviewPanelPath(PathUtils.ToAssetsRelativeOrNull(srcAbs) ?? srcAbs);
                Directory.CreateDirectory(Path.GetDirectoryName(previewAbs));
                AtomicFile.WriteAllBytes(previewAbs, png);

                result.preview = previewAbs;
                result.note = "Open 'preview' to visually verify the result: left=before, middle=after, "
                    + "right=changed pixels (magenta). Scattered magenta or a low largestComponentFraction "
                    + "suggests over-selection; empty magenta where you expected change suggests it was missed. "
                    + "Adjust 'target'/'tolerance' and re-run if it does not match intent.";
            } catch (Exception ex) {
                result.warnings.Add($"preview generation failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 色替えした画素を PNG にして <paramref name="abs"/> へ書き、取り込む(出力先は確かめ済み)。PNG もヒープの外に作り、
        /// 書いたら手放す。取り込み設定は元から引き継ぐ(窓の書き出しの既定と同じ)。エンコードに失敗したら何も書かずに false。
        /// I/O 等の例外は捕まえず呼び出し側へ伝える。
        /// </summary>
        private static bool TryWritePng(NativePixels pixels, string abs, string srcAbs) {
            NativeArray<byte> png;
            try { png = TextureIO.EncodePng(pixels); } catch (Exception) { return false; }
            try { ExportService.WriteAndImport(abs, png, srcAbs, inheritImportSettings: true); } finally { png.Dispose(); }
            return true;
        }

        private static ColorZone BuildZone(ZoneDto z) {
            var zone = new ColorZone {
                name = z.name,
                enabled = true,
                mode = SelectionMode.ColorPick,
                sampleColor = ToColor(z.sample),
                sampleColorSet = true,
                targetColor = ToColor(z.target),
                targetColorSet = true,
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
                shadowValueFloor = z.shadowValueFloor,
                partSatCeiling = z.partSatCeiling,
                partHueBand = z.partHueBand,
                chromaCeiling = z.chromaCeiling,
                holeFillMargin = z.holeFillMargin,
                layerIndex = z.layerIndex,
                useFloodFill = z.useFloodFill,
                // 未指定/長さ不足 = 負値 = 自動アンカリング（Harness.BuildZone と同一の写像）。
                seedUV = (z.seedUV != null && z.seedUV.Length >= 2)
                    ? new Vector2(z.seedUV[0], z.seedUV[1]) : new Vector2(-1f, -1f),
                sampleUV = (z.sampleUV != null && z.sampleUV.Length >= 2)
                    ? new Vector2(z.sampleUV[0], z.sampleUV[1]) : new Vector2(-1f, -1f),
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

        /// <summary>
        /// プリセットを新しい編集状態へ写す（ゾーン・処理設定・マスク）。UI でプリセットを読み込んだときと同じ規則
        /// （<see cref="PresetApplier"/>。古い優先順位の移行・id を含む）。
        /// advancedMode は UI 表示レベルのみで処理結果に影響しないため移送しない。
        /// 別のテクスチャで作ったマスクは読まない(UI では確かめるが、ここでは聞けない。警告で知らせる)。
        /// </summary>
        private static IrocaSessionState SessionFromPreset(IrocaPresetData p, string textureGuid, List<string> warnings) {
            var state = new IrocaSessionState();
            PresetApplier.ApplyZonesAndSettings(p, state, textureGuid);
            // 手で書いたプリセットは範囲の外の値も持てる(全体設定は読み込みで丸めてあるので、丸める前の値で比べる)。
            ClampAndReportOutOfRange(RecolorValueRanges.ClampedSettingNames(
                    p.edgeFeather, p.antiAliasCleanup, p.holeFillPasses, p.holeFillMinNeighbors,
                    p.relaxedSatMin, p.relaxedSatRamp, p.decontaminationRadius),
                state.zones, warnings);
            if (PresetApplier.HasMasks(p) && PresetApplier.MadeForOtherTexture(p, textureGuid)) {
                warnings.Add("the masks of this preset were made on another texture, so they were not used (zones and settings were).");
                return state;
            }
            state.maskState = PresetApplier.MaskStateOf(p);
            return state;
        }

        /// <summary>
        /// 範囲の外・NaN の値(UI のスライダーの範囲の外)を丸めたことを warnings で知らせる(点検 A-8。断らずに丸める)。
        /// 全体設定は <see cref="RecolorSettings"/> のコンストラクタが丸めるので、丸める前の値で比べた名前を受け取る。
        /// ゾーンはここで <see cref="RecolorValueRanges.ClampZone"/> で丸めて書き戻す(Core の入口でもう一度丸めても同じ値。
        /// 出力は丸めた値を渡したときと同じ)。
        /// </summary>
        private static void ClampAndReportOutOfRange(List<string> clampedSettings, List<ColorZone> zones, List<string> warnings) {
            if (clampedSettings != null && clampedSettings.Count > 0) {
                warnings.Add("settings out of range (or NaN) were clamped to their allowed range: "
                    + string.Join(", ", clampedSettings) + ".");
            }
            if (zones == null) {
                return;
            }
            foreach (var zone in zones) {
                if (zone == null) {
                    continue;
                }
                var names = new List<string>();
                if (!RecolorValueRanges.ClampZone(zone, names)) {
                    continue;
                }
                zone.UpdateCacheIfNeeded();
                warnings.Add($"zone '{zone.name}': values out of range (or NaN) were clamped to their allowed range: "
                    + string.Join(", ", names) + ".");
            }
        }

        private static RecolorSettings SettingsFromDto(SettingsDto d) => new RecolorSettings(
            d.edgeFeather, d.antiAliasCleanup,
            d.holeFillPasses, d.holeFillMinNeighbors,
            d.relaxedSatMin, d.relaxedSatRamp,
            d.useDecontamination, d.decontaminationRadius);

        /// <summary>
        /// プリセット指定（ファイルパス / プリセット名 / インライン JSON）を <see cref="IrocaPresetData"/> へ解決する。
        /// </summary>
        private static IrocaPresetData ResolvePreset(string spec) {
            if (string.IsNullOrWhiteSpace(spec)) {
                return null;
            }

            string abs = ResolveExistingFile(spec) ?? UserPresetFileOrNull(spec);
            if (abs != null) {
                return PresetStore.Load(abs);
            }

            string trimmed = spec.TrimStart();
            if (trimmed.StartsWith("{")) {
                try { return PresetStore.Parse(spec); } catch { return null; }
            }

            foreach (var folder in new[] { PresetStore.ProjectPresetFolder, PresetStore.UserPresetFolder }) {
                string p = PresetStore.PresetFilePath(folder, spec);
                if (File.Exists(p)) {
                    return PresetStore.Load(p);
                }
            }
            return null;
        }

        /// <summary>
        /// ユーザー側のプリセットのフォルダ(<see cref="PresetStore.UserPresetFolder"/>。%APPDATA% の下)の *.json なら、その
        /// 絶対パス。違えば null。プロジェクトの外を読むのはここだけで、読むだけ(<see cref="ListPresets"/> が返す path を
        /// そのまま preset に渡せるように。以前はプロジェクトの外として断っていた)。
        /// </summary>
        private static string UserPresetFileOrNull(string path) {
            string folder;
            string abs;
            try {
                if (!Path.IsPathRooted(path)) {
                    return null;
                }
                folder = Path.GetFullPath(PresetStore.UserPresetFolder);
                abs = Path.GetFullPath(path);
            } catch (Exception) {
                return null;
            }
            // フォルダの直下だけ(区切りまで比べる。名前の前の部分だけが同じ隣のフォルダは外)。
            string dir = Path.GetDirectoryName(abs);
            if (dir == null || !string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) {
                return null;
            }
            if (!abs.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(abs)) {
                return null;
            }
            return abs;
        }

        private static void AddPresets(PresetList list, string folder, string scope) {
            foreach (var path in PresetStore.ListJson(folder)) {
                list.presets.Add(new PresetEntry {
                    name = Path.GetFileNameWithoutExtension(path),
                    path = path,
                    scope = scope,
                });
            }
        }

        // 絶対 / プロジェクト相対 / Assets 相対のいずれかを実在する絶対パスへ解決する。
        // プロジェクトルート配下でなければ拒否する（範囲外ファイルの読み取りを防ぐ）。無ければ null。
        private static string ResolveExistingFile(string path) {
            if (string.IsNullOrEmpty(path)) {
                return null;
            }
            string abs = AutomationOutputRules.ToProjectAbsolute(path);
            if (abs == null || !AutomationOutputRules.IsWithinProjectRoot(abs)) {
                return null;
            }
            return File.Exists(abs) ? abs : null;
        }

        private static bool HasArg(string[] args, string name) {
            foreach (var a in args) {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            return false;
        }

        private static string GetArg(string[] args, string name) {
            for (int i = 0; i < args.Length - 1; i++) {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static void WriteMcpFile(string path, string content) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 呼び出し側は result.json を読んで成否を判断する。直書きだとクラッシュ時に
                // 途中まで書けた壊れた JSON を読ませてしまう。
                AtomicFile.WriteAllText(path, content);
            } catch (Exception ex) {
                Debug.LogWarning($"[Iroca][MCP] failed to write {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// 本体アセンブリのパッケージ版。埋め込み配置などで解決できなければ "unknown"。
        /// GetVersion / DescribeSchema と ReproDump(Debug アセンブリ)が共有する唯一の解決経路。
        /// </summary>
        internal static string ExtractVersion() {
            try {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(IrocaAutomation).Assembly);
                return info != null && !string.IsNullOrEmpty(info.version) ? info.version : "unknown";
            } catch { return "unknown"; }
        }

        private static string Json(RecolorResult r) => JsonUtility.ToJson(r);

        private static string Fail(string error, List<string> warnings = null) {
            var r = new RecolorResult { ok = false, error = error, warnings = warnings ?? new List<string>() };
            return JsonUtility.ToJson(r);
        }
    }
}
