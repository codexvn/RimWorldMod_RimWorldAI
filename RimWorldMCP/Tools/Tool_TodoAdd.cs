using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Helpers;

namespace RimWorldMCP.Tools
{
    public class Tool_TodoAdd : ITool
    {
        public string Name => "todo_add";
        public string Description => "添加一条待办事项到 TODO 列表。优先级 1-5，数字越大越紧急。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                description = new { type = "string", description = "待办事项描述" },
                priority = new
                {
                    type = "integer",
                    description = "优先级 1-5，数字越大越紧急",
                    minimum = 1,
                    maximum = 5,
                    @default = 3
                }
            },
            required = new[] { "description" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(ToolResult.Error("缺少参数: description"));

            if (!args.Value.TryGetProperty("description", out var descProp))
                return Task.FromResult(ToolResult.Error("缺少必填参数: description"));

            var description = descProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(description))
                return Task.FromResult(ToolResult.Error("description 不能为空。"));

            var priority = 3;
            if (args.Value.TryGetProperty("priority", out var p))
            {
                priority = p.TryGetInt32(out var pVal) ? pVal : 3;
                if (priority < 1 || priority > 5)
                    return Task.FromResult(ToolResult.Error("priority 必须在 1-5 范围内。"));
            }

            var item = TodoManager.Add(description, priority);
            return Task.FromResult(ToolResult.Success(
                $"已添加待办 #{item.Id} (P{item.Priority}): {item.Description} ({GameTimeHelper.FormatGameTime(item.CreatedAtTick)})"));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
