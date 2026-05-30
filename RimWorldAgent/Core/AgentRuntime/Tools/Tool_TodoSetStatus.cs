using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_TodoSetStatus : IInternalTool
    {
        public string Name => "todo_set_status";
        public string Description => "设置待办任务状态。done/cancelled 不会删除任务，保留记录。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string", description = "任务 ID" },
                status = new { type = "string", description = "新状态: pending / done / cancelled" }
            },
            required = new[] { "id", "status" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null
                || !args.Value.TryGetProperty("id", out var idEl) || string.IsNullOrWhiteSpace(idEl.GetString())
                || !args.Value.TryGetProperty("status", out var statusEl) || string.IsNullOrWhiteSpace(statusEl.GetString()))
                return Task.FromResult(("参数 id 和 status 不能为空。", false));
            var id = idEl.GetString()!;
            var status = statusEl.GetString()!.ToLower();
            if (status != "pending" && status != "done" && status != "cancelled")
                return Task.FromResult(($"无效状态: {status}。可选: pending, done, cancelled", false));
            var found = TodoManager.UpdateStatus(id, status);
            return Task.FromResult((found ? $"任务 [{id}] 状态已更新为 {status}" : $"任务 [{id}] 不存在", false));
        }
    }
}
