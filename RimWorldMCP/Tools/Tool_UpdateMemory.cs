using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_UpdateMemory : ITool
    {
        public string Name => "update_memory";
        public string Description => "更新指定 ID 记忆的优先级和/或内容。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string", description = "记忆 ID" },
                priority = new
                {
                    type = "integer",
                    description = "新优先级 1-5（可选，不传则不修改）",
                    minimum = 1,
                    maximum = 5
                },
                content = new
                {
                    type = "string",
                    description = "新内容（可选，不传则不修改）"
                }
            },
            required = new[] { "id" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(ToolResult.Error("缺少参数: id"));

            if (!args.Value.TryGetProperty("id", out var idProp))
                return Task.FromResult(ToolResult.Error("缺少必填参数: id"));

            var id = idProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult(ToolResult.Error("id 不能为空。"));

            int? priority = null;
            if (args.Value.TryGetProperty("priority", out var p))
            {
                if (p.TryGetInt32(out var pVal))
                {
                    if (pVal < 1 || pVal > 5)
                        return Task.FromResult(ToolResult.Error("priority 必须在 1-5 范围内。"));
                    priority = pVal;
                }
            }

            string? content = null;
            if (args.Value.TryGetProperty("content", out var c))
            {
                content = c.GetString();
                if (content != null && string.IsNullOrWhiteSpace(content))
                    return Task.FromResult(ToolResult.Error("content 不能为空字符串。"));
            }

            if (priority == null && content == null)
                return Task.FromResult(ToolResult.Error("至少需要提供 priority 或 content 之一。"));

            if (MemoryManager.Update(id, priority, content))
                return Task.FromResult(ToolResult.Success($"已更新记忆 #{id}。"));

            return Task.FromResult(ToolResult.Error($"未找到记忆 #{id}。请用 list_memories 查看可用的 ID。"));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
