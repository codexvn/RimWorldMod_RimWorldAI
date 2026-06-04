using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>设置工具结果后缀（一次性），直接写入本地缓冲，无 MCP 往返</summary>
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

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null || !args.Value.TryGetProperty("suffix", out var suffixEl) || string.IsNullOrWhiteSpace(suffixEl.GetString()))
                return Task.FromResult(("suffix 不能为空。", false));
            var suffix = suffixEl.GetString()!;
            ToolDispatcher.EnqueueNotifSuffix(suffix);
            return Task.FromResult(($"工具结果后缀已设置（{suffix.Length} 字符）。", false));
        }
    }
}
