using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_TodoDelete : IInternalTool
    {
        public string Name => "todo_delete";
        public string Description => "删除待办任务。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string", description = "任务 ID" }
            },
            required = new[] { "id" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null || !args.Value.TryGetProperty("id", out var idEl) || string.IsNullOrWhiteSpace(idEl.GetString()))
                return Task.FromResult(("任务 ID 不能为空。", false));
            var id = idEl.GetString()!;
            var removed = TodoManager.Delete(id);
            return Task.FromResult((removed ? $"已删除任务 [{id}]" : $"任务 [{id}] 不存在", false));
        }
    }
}
