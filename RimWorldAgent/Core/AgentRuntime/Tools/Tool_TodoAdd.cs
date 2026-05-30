using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_TodoAdd : IInternalTool
    {
        public string Name => "todo_add";
        public string Description => "添加待办任务到 TODO 列表。任务会持久化到存档。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                description = new { type = "string", description = "任务描述" },
                priority = new { type = "integer", description = "优先级 1-5（默认 3，5 最高）" }
            },
            required = new[] { "description" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null || !args.Value.TryGetProperty("description", out var descEl) || string.IsNullOrWhiteSpace(descEl.GetString()))
                return Task.FromResult(("任务描述不能为空。", false));
            var desc = descEl.GetString()!;
            var priority = 3;
            if (args.Value.TryGetProperty("priority", out var p) && p.TryGetInt32(out var pv))
                priority = pv;
            var item = TodoManager.Add(desc, priority);
            return Task.FromResult(($"已添加任务 [{item.Id}]: {item.Description} (优先级 {item.Priority})", false));
        }
    }
}
