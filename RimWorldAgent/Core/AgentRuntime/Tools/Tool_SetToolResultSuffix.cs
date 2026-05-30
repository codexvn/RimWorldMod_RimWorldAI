using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>设置 MCP 工具结果后缀，之后每次工具调用结果末尾自动追加此文本</summary>
    public class Tool_SetToolResultSuffix : IInternalTool
    {
        public string Name => "set_tool_result_suffix";
        public string Description => "设置工具结果后缀（一次性）。下一次工具调用的结果末尾会追加此文本，追加后自动清空。用于向 AI 注入实时通知。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                suffix = new { type = "string", description = "要追加到工具结果末尾的文本" }
            },
            required = new[] { "suffix" }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null || !args.Value.TryGetProperty("suffix", out var suffixEl) || string.IsNullOrWhiteSpace(suffixEl.GetString()))
                return ("suffix 不能为空。", false);
            var suffix = suffixEl.GetString()!;
            var mcp = AgentOrchestrator.SessionMcp;
            if (mcp == null) return ("MCP 连接不可用，无法设置后缀。", false);

            try
            {
                var mcpArgs = new Dictionary<string, JsonElement>
                {
                    ["suffix"] = JsonSerializer.SerializeToElement(suffix)
                };
                var result = await mcp.CallTool("set_tool_result_suffix", mcpArgs);
                return (result, false);
            }
            catch (System.Exception ex)
            {
                CoreLog.Error($"[SetToolResultSuffix] 调用 MCP 失败: {ex.Message}");
                return ($"设置后缀失败: {ex.Message}", false);
            }
        }
    }
}
