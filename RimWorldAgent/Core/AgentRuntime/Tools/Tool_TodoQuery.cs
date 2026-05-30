using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_TodoQuery : IInternalTool
    {
        public string Name => "todo_query";
        public string Description => "查询待办任务列表。可按状态过滤。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                status = new { type = "string", description = "过滤状态: pending / done / cancelled（不传返回全部）" }
            }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            string? status = null;
            if (args?.TryGetProperty("status", out var statusEl) == true)
                status = statusEl.GetString();
            var items = TodoManager.Query(status);
            if (items.Count == 0)
                return Task.FromResult((string.IsNullOrEmpty(status) ? "TODO 列表为空。" : $"没有状态为 [{status}] 的任务。", false));
            var sb = new StringBuilder();
            sb.AppendLine($"TODO 列表 ({items.Count} 项):");
            sb.AppendLine("| ID | 优先级 | 状态 | 描述 |");
            sb.AppendLine("|---|--------|------|------|");
            foreach (var i in items)
                sb.AppendLine($"| {i.Id} | {i.Priority} | {i.Status} | {i.Description} |");
            return Task.FromResult((sb.ToString().TrimEnd(), false));
        }
    }
}
