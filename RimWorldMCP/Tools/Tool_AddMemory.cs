using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_AddMemory : ITool
    {
        public string Name => "add_memory";
        public string Description => "添加一条记忆。优先级 1-5，数字越大越重要。返回生成的 ID。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                content = new { type = "string", description = "记忆内容（经验教训、观察、决策等）" },
                priority = new
                {
                    type = "integer",
                    description = "优先级 1-5，数字越大越重要",
                    minimum = 1,
                    maximum = 5,
                    @default = 3
                }
            },
            required = new[] { "content" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(ToolResult.Error("缺少参数: content"));

            if (!args.Value.TryGetProperty("content", out var contentProp))
                return Task.FromResult(ToolResult.Error("缺少必填参数: content"));

            var content = contentProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult(ToolResult.Error("content 不能为空。"));

            var priority = 3;
            if (args.Value.TryGetProperty("priority", out var p))
            {
                priority = p.TryGetInt32(out var pVal) ? pVal : 3;
                if (priority < 1 || priority > 5)
                    return Task.FromResult(ToolResult.Error("priority 必须在 1-5 范围内。"));
            }

            var entry = MemoryManager.Add(priority, content);
            return Task.FromResult(ToolResult.Success(
                $"已添加记忆 #{entry.Id} (P{entry.Priority})"));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
