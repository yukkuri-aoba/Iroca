// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
//
// MCPForUnity（https://github.com/CoplayDev/unity-mcp）のカスタムツールとして Iroca を公開する。
// このアセンブリは MCPForUnity（パッケージ id com.coplaydev.unity-mcp）が導入されているときだけ
// コンパイルされる（asmdef の versionDefines + defineConstraints による IROCA_MCP_PRESENT ゲート）。
// → 配布パッケージ本体は MCPForUnity への依存ゼロを維持する。
// 開発用で、配布物(VPM zip)には入れない(scripts/Build-VpmPackage.ps1)。MCPForUnity はカスタムツールを
// 既定で有効にして AI のツール一覧へ出すので、入れると MCPForUnity を使う利用者の AI にこれらが勝手に並ぶ。
//
// 駆動経路: AI エージェントは execute_custom_tool("iroca_recolor", {...}) のように呼ぶ。
// 各ツールは IrocaAutomation の公開静的 API を薄くラップするだけで、変換の中核（RunRecolorCore）は共有。
// MCPForUnity の型はツールの印（McpForUnityTool・ToolParameter）だけを使う。応答は McpResponses が JObject で組む。
#if IROCA_MCP_PRESENT
using Newtonsoft.Json.Linq;
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
                return McpResponses.Error("source is required");
            if (string.IsNullOrEmpty(p.output))
                return McpResponses.Error("output is required");

            bool hasPreset = !string.IsNullOrEmpty(p.preset);
            bool hasZones = !string.IsNullOrEmpty(p.zones);
            if (hasPreset == hasZones)
                return McpResponses.Error("exactly one of 'preset' or 'zones' must be provided");

            // IrocaAutomation は例外を投げず {"ok":...} の JSON 文字列を返す設計。
            string json = hasPreset
                ? IrocaAutomation.RecolorByPreset(p.source, p.preset, p.output)
                : IrocaAutomation.RecolorWithZones(p.source, p.zones, p.output);

            return McpResponses.FromAutomationResult(json, "recolor failed");
        }
    }

    /// <summary>
    /// 色替えの対象を調べるツール（<see cref="IrocaAutomation.Inspect"/>）。シーンのアバター一覧・
    /// オブジェクトが使うテクスチャ・テクスチャの主な色と代表点（印付きの縮小画像つき）を返す。
    /// </summary>
    [McpForUnityTool("iroca_inspect", Description =
        "Inspect what Iroca can recolor. With no target: lists scene root objects that have renderers (avatars). "
        + "With a scene object (hierarchy path like 'Avatar/Body', a unique name, or '#instanceID'): lists the main textures "
        + "used under it, with their materials, renderers and existing Iroca recipes. With a texture or material asset path: "
        + "returns the texture's main colors (hex, coverage) and a click position 'uv' (bottom-left origin) inside each color's "
        + "largest region, plus 'thumbnail', a PNG path showing the texture with a ring at each uv. Use the uv as 'at' when recoloring.")]
    public static class IrocaInspectTool
    {
        public class Parameters
        {
            [ToolParameter("Scene object path/name/'#instanceID', or a texture/material asset path (Assets/...). Omit to list the scene.", Required = false)]
            public string target { get; set; }

            [ToolParameter("Maximum number of main colors to return for a texture (1-16, default 8).", Required = false)]
            public int? max_colors { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = @params != null ? @params.ToObject<Parameters>() : new Parameters();
            return McpResponses.FromAutomationResult(IrocaAutomation.Inspect(p.target, p.max_colors ?? 0), "inspect failed");
        }
    }

    /// <summary>呼び出し方・入力スキーマ・既定値・フィールド説明を返す自己発見用ツール。</summary>
    [McpForUnityTool("iroca_describe_schema")]
    public static class IrocaDescribeSchemaTool
    {
        public class Parameters { }

        public static object HandleCommand(JObject @params)
        {
            return McpResponses.FromAutomationDocument(IrocaAutomation.DescribeSchema(), "describe_schema failed");
        }
    }

    /// <summary>プロジェクト/ユーザー保存先のプリセット一覧を返すツール。</summary>
    [McpForUnityTool("iroca_list_presets")]
    public static class IrocaListPresetsTool
    {
        public class Parameters { }

        public static object HandleCommand(JObject @params)
        {
            return McpResponses.FromAutomationDocument(IrocaAutomation.ListPresets(), "list_presets failed");
        }
    }
}
#endif
