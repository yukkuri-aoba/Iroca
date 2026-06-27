// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
//
// MCPForUnity（https://github.com/CoplayDev/unity-mcp）のカスタムツールとして Iroca を公開する。
// このアセンブリは MCPForUnity（パッケージ id com.coplaydev.unity-mcp）が導入されているときだけ
// コンパイルされる（asmdef の versionDefines + defineConstraints による IROCA_MCP_PRESENT ゲート）。
// → 配布パッケージ本体は MCPForUnity への依存ゼロを維持する。
//
// 駆動経路: AI エージェントは execute_custom_tool("iroca_recolor", {...}) のように呼ぶ。
// 各ツールは IrocaAutomation の公開静的 API を薄くラップするだけで、変換の中核（RunRecolorCore）は共有。
#if IROCA_MCP_PRESENT
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;

namespace Iroca.McpIntegration
{
    /// <summary>
    /// テクスチャを再着色して PNG を書き出す MCP カスタムツール。
    /// <c>preset</c> を渡せばプリセット経路（<see cref="IrocaAutomation.RecolorByPreset"/>）、
    /// <c>zones</c> を渡せばその場のゾーン指定経路（<see cref="IrocaAutomation.RecolorWithZones"/>）を通る。
    /// </summary>
    [McpForUnityTool("iroca_recolor")]
    public static class IrocaRecolorTool
    {
        public class Parameters
        {
            [ToolParameter("入力テクスチャのパス（Assets 相対 / プロジェクト相対 / 絶対のいずれも可）")]
            public string source { get; set; }

            [ToolParameter("出力 PNG のパス（.png）。Assets 配下なら自動で取り込まれる")]
            public string output { get; set; }

            [ToolParameter("プリセットのファイルパス・プリセット名・インライン JSON のいずれか。zones と排他", Required = false)]
            public string preset { get; set; }

            [ToolParameter("その場のゾーン設定 JSON（{\"zones\":[...],\"settings\":{...}}）。preset と排他。スキーマは iroca_describe_schema 参照", Required = false)]
            public string zones { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = @params != null ? @params.ToObject<Parameters>() : new Parameters();

            if (string.IsNullOrEmpty(p.source))
                return new ErrorResponse("source is required");
            if (string.IsNullOrEmpty(p.output))
                return new ErrorResponse("output is required");

            bool hasPreset = !string.IsNullOrEmpty(p.preset);
            bool hasZones = !string.IsNullOrEmpty(p.zones);
            if (hasPreset == hasZones)
                return new ErrorResponse("exactly one of 'preset' or 'zones' must be provided");

            // IrocaAutomation は例外を投げず {"ok":...} の JSON 文字列を返す設計。
            string json = hasPreset
                ? IrocaAutomation.RecolorByPreset(p.source, p.preset, p.output)
                : IrocaAutomation.RecolorWithZones(p.source, p.zones, p.output);

            return WrapResult(json, "recolor failed");
        }

        // IrocaAutomation の JSON 文字列を MCP の Success/Error レスポンスへ整形する。
        internal static object WrapResult(string json, string fallbackError)
        {
            JObject data;
            try { data = JObject.Parse(json); }
            catch { return new ErrorResponse($"{fallbackError}: invalid JSON from Iroca: {json}"); }

            bool ok = data.TryGetValue("ok", out var okToken) && okToken.Type == JTokenType.Boolean && okToken.Value<bool>();
            if (!ok)
            {
                string err = data.TryGetValue("error", out var errToken) ? errToken.ToString() : fallbackError;
                return new ErrorResponse(string.IsNullOrEmpty(err) ? fallbackError : err);
            }
            return new SuccessResponse("ok", data);
        }

        // "ok" フィールドを持たない素の JSON オブジェクト（DescribeSchema / ListPresets 等）を
        // そのまま success data として返す。パース不能時のみ error。
        internal static object WrapResultLenient(string json, string fallbackError)
        {
            try { return new SuccessResponse("ok", JObject.Parse(json)); }
            catch { return new ErrorResponse($"{fallbackError}: invalid JSON from Iroca: {json}"); }
        }
    }

    /// <summary>呼び出し方・入力スキーマ・既定値・フィールド説明を返す自己発見用ツール。</summary>
    [McpForUnityTool("iroca_describe_schema")]
    public static class IrocaDescribeSchemaTool
    {
        public class Parameters { }

        public static object HandleCommand(JObject @params)
        {
            return IrocaRecolorTool.WrapResultLenient(IrocaAutomation.DescribeSchema(), "describe_schema failed");
        }
    }

    /// <summary>プロジェクト/ユーザー保存先のプリセット一覧を返すツール。</summary>
    [McpForUnityTool("iroca_list_presets")]
    public static class IrocaListPresetsTool
    {
        public class Parameters { }

        public static object HandleCommand(JObject @params)
        {
            return IrocaRecolorTool.WrapResultLenient(IrocaAutomation.ListPresets(), "list_presets failed");
        }
    }
}
#endif
