// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
//
// MCPForUnity（https://github.com/CoplayDev/unity-mcp）のカスタムツールとして Iroca を公開する。
// このアセンブリは MCPForUnity（パッケージ id com.coplaydev.unity-mcp）が導入されているときだけ
// コンパイルされる（asmdef の versionDefines + defineConstraints による IROCA_MCP_PRESENT ゲート）。
// → 配布パッケージ本体は MCPForUnity への依存ゼロを維持する。
// 0.3.0 から配布物に入れる利用者向けの機能（マニュアル「AI エージェントから使う（MCP）」）。MCPForUnity は
// カスタムツールを既定で有効にして AI に出すので、使わせたくない利用者は MCPForUnity の Tools 画面で外す。
//
// 駆動経路: AI エージェントは execute_custom_tool("iroca_recolor", {...}) のように呼ぶ。
// ツールは 3 つ（調べる・色替え・窓で開く）。各ツールは IrocaAutomation の公開静的 API を薄くラップするだけで、
// 判断は持たない（MCP の無いテストでも IrocaAutomation を直接測れるように）。
// 説明文は AI が読むので英語。MCPForUnity の型はツールの印（McpForUnityTool・ToolParameter）だけを使い、
// 応答は McpResponses が JObject で組む。
#if IROCA_MCP_PRESENT
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Tools;

namespace Iroca.McpIntegration
{
    /// <summary>
    /// 色替えの対象を調べるツール（<see cref="IrocaAutomation.Inspect"/>）。シーンのアバター一覧・
    /// オブジェクトが使うテクスチャ・テクスチャの主な色と代表点（印付きの縮小画像つき）を返す。
    /// </summary>
    [McpForUnityTool("iroca_inspect", Description =
        "Iroca (texture recolor tool for avatars), step 1: find what to recolor. With no target: lists scene root objects "
        + "that have renderers (avatars). With a scene object (hierarchy path like 'Avatar/Body', a unique name, or '#instanceID'): "
        + "lists the main textures used under it, with their materials, renderers and existing Iroca recipes. With a texture or "
        + "material asset path: returns the texture's main colors (hex, coverage) and a click position 'uv' (bottom-left origin) "
        + "inside each color's largest region, plus 'thumbnail', a PNG path showing the texture with a ring at each uv. "
        + "Look at the thumbnail, then pass the uv of the part to change as 'at' to iroca_recolor.")]
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

    /// <summary>
    /// 色替えのツール（<see cref="IrocaAutomation.Recolor"/>）。引数の名前は依頼 JSON の欄と同じなので、
    /// そのまま渡す（配列を文字列で送ってくるクライアントのために、文字列なら配列へ読み直す）。
    /// </summary>
    [McpForUnityTool("iroca_recolor", Description =
        "Iroca step 2: recolor parts of a texture, the same way a user does in the Iroca window (pick a color, choose the new color). "
        + "Each entry of 'changes' becomes one zone: {name, at:[u,v] or from:'#RRGGBB', to:'#RRGGBB'}. All pixels of that color "
        + "(with its shading) change, not only the clicked spot. A change whose name matches an existing zone replaces it, so "
        + "send the same name to retry a part. Existing zones (from the Iroca window or the avatar's recipe) are kept unless "
        + "'reset' is true. apply='avatar' (default) registers the result non-destructively on the avatar (NDMF; visible in the "
        + "Scene view, applied at upload); apply='png' writes <name>_recolored.png; apply='preview' saves nothing. Always open the "
        + "returned 'preview' image (left=before, middle=after, right=changed pixels in magenta) to check the result.")]
    public static class IrocaRecolorTool
    {
        public class Parameters
        {
            [ToolParameter("Texture or material asset path (Assets/...).")]
            public string texture { get; set; }

            [ToolParameter("Array of changes: [{\"name\":\"skirt\",\"at\":[u,v],\"to\":\"#1A2B5C\"}]. Use 'from':'#RRGGBB' instead of 'at' to pick by color. Colors may also be Unity color names like \"navy\".", Required = false)]
            public JToken changes { get; set; }

            [ToolParameter("Array of zone names to remove.", Required = false)]
            public JToken remove { get; set; }

            [ToolParameter("true = discard existing zones and masks before applying 'changes'.", Required = false)]
            public bool? reset { get; set; }

            [ToolParameter("Start from this Iroca preset (file path, name, or JSON) instead of the current zones.", Required = false)]
            public string preset { get; set; }

            [ToolParameter("'avatar' (default): register on the avatar non-destructively; 'png': write a recolored PNG; 'preview': save nothing.", Required = false)]
            public string apply { get; set; }

            [ToolParameter("Scene object to register on (default: the object using the texture).", Required = false)]
            public string avatar { get; set; }

            [ToolParameter("Output PNG path for apply='png' (default: <texture>_recolored.png next to the texture).", Required = false)]
            public string output { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var request = @params != null ? (JObject)@params.DeepClone() : new JObject();
            foreach (var key in new[] { "changes", "remove" })
            {
                if (request[key] is JValue v && v.Type == JTokenType.String)
                {
                    try { request[key] = JToken.Parse((string)v); }
                    catch { return McpResponses.Error($"'{key}' must be a JSON array."); }
                }
            }
            return McpResponses.FromAutomationResult(IrocaAutomation.Recolor(request.ToString()), "recolor failed");
        }
    }

    /// <summary>いろかの窓で開くツール（<see cref="IrocaAutomation.Open"/>）。人が手直しする入口。</summary>
    [McpForUnityTool("iroca_open", Description =
        "Iroca step 3 (optional): open a texture in the Iroca window so the user can fine-tune it by hand. "
        + "Target: a recipe, texture or material asset path, or a scene object that has one Iroca recipe.")]
    public static class IrocaOpenTool
    {
        public class Parameters
        {
            [ToolParameter("Recipe/texture/material asset path, or a scene object with an Iroca recipe.")]
            public string target { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = @params != null ? @params.ToObject<Parameters>() : new Parameters();
            return McpResponses.FromAutomationResult(IrocaAutomation.Open(p.target), "open failed");
        }
    }
}
#endif
