using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    /// <summary>Agent 设置工具结果后缀，之后每次工具调用结果末尾自动追加此文本</summary>
    public class Tool_SetToolResultSuffix : ITool
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

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var suffix = args?.GetProperty("suffix").GetString() ?? "";
            ToolRegistry.ToolResultSuffix = suffix;
            return Task.FromResult(ToolResult.Success(
                string.IsNullOrEmpty(suffix)
                    ? "工具结果后缀已清空。"
                    : $"工具结果后缀已设置（{suffix.Length} 字符）。后续所有工具调用结果末尾将追加此文本。"));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
