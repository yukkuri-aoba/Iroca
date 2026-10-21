// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
//
// MCP for Unity へ返す応答を JObject で組む。
// MCP for Unity の SuccessResponse / ErrorResponse / PendingResponse と同じ形(サーバーは JSON の
// success・message・error・data・_mcp_status を読むだけ)を、その型を使わずに作る。
// MCP for Unity の型への依存をツールの印(McpForUnityToolAttribute・ToolParameterAttribute)だけに減らし、
// 更新の速い MCP for Unity の API 変更で利用者のプロジェクトのコンパイルを止める心配を小さくするため。
#if IROCA_MCP_PRESENT
using Newtonsoft.Json.Linq;

namespace Iroca.McpIntegration
{
    internal static class McpResponses
    {
        /// <summary>成功。SuccessResponse と同じ形({success:true, message, data})。</summary>
        public static JObject Success(string message, JToken data = null)
        {
            var o = new JObject { ["success"] = true, ["message"] = message ?? "" };
            if (data != null) o["data"] = data;
            return o;
        }

        /// <summary>失敗。ErrorResponse と同じ形({success:false, error, data})。</summary>
        public static JObject Error(string error, JToken data = null)
        {
            var o = new JObject { ["success"] = false, ["error"] = error ?? "" };
            if (data != null) o["data"] = data;
            return o;
        }

        /// <summary>
        /// IrocaAutomation の JSON 文字列({"ok":...,"error":...})を成功/失敗の応答へ整える。
        /// ok が true でなければ error の文言で失敗にする。
        /// </summary>
        public static JObject FromAutomationResult(string json, string fallbackError)
        {
            JObject data;
            try { data = JObject.Parse(json); }
            catch { return Error($"{fallbackError}: invalid JSON from Iroca: {json}"); }

            bool ok = data.TryGetValue("ok", out var okToken) && okToken.Type == JTokenType.Boolean && okToken.Value<bool>();
            if (!ok)
            {
                string err = data.TryGetValue("error", out var errToken) ? errToken.ToString() : fallbackError;
                return Error(string.IsNullOrEmpty(err) ? fallbackError : err);
            }
            return Success("ok", data);
        }

        /// <summary>
        /// 待っている途中。PendingResponse と同じ形({success:true, _mcp_status:"pending", _mcp_poll_interval, message, data})。
        /// サーバーは poll_interval 秒後に、同じ引数に action="status" を足して問い合わせ直す(0.1〜5 秒に丸められる)。
        /// </summary>
        public static JObject Pending(string message, double pollIntervalSeconds, JToken data = null)
        {
            var o = new JObject
            {
                ["success"] = true,
                ["_mcp_status"] = "pending",
                ["_mcp_poll_interval"] = pollIntervalSeconds,
                ["message"] = message ?? "",
            };
            if (data != null) o["data"] = data;
            return o;
        }

        /// <summary>
        /// IrocaAutomation の JSON が途中経過({"pending":true,...})なら <see cref="Pending"/>、そうでなければ
        /// <see cref="FromAutomationResult"/>。
        /// </summary>
        public static JObject FromAutomationPendingOrResult(string json, double pollIntervalSeconds, string fallbackError)
        {
            JObject data;
            try { data = JObject.Parse(json); }
            catch { return Error($"{fallbackError}: invalid JSON from Iroca: {json}"); }
            bool pending = data.TryGetValue("pending", out var p) && p.Type == JTokenType.Boolean && p.Value<bool>();
            if (pending) return Pending(data.Value<string>("message"), pollIntervalSeconds, data);
            return FromAutomationResult(json, fallbackError);
        }
    }
}
#endif
