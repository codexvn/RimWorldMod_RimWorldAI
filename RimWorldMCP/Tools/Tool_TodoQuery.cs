using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Helpers;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_TodoQuery : ITool
    {
        public string Name => "todo_query";
        public string Description => "查询所有或指定状态的待办事项。默认返回全部。filter 可选 pending/done/cancelled。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                filter = new
                {
                    type = "string",
                    description = "过滤状态：pending=待办, done=已完成, cancelled=已取消",
                    @enum = new[] { "pending", "done", "cancelled" }
                }
            },
            required = new string[] { }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string? filter = null;
            if (args != null && args.Value.TryGetProperty("filter", out var f))
                filter = f.GetString();

            var items = TodoManager.Query(filter);

            if (items.Count == 0)
            {
                var msg = filter switch
                {
                    "pending" => "目前没有待办的 TODO 事项。",
                    "done" => "目前没有已完成的 TODO 事项。",
                    "cancelled" => "目前没有已取消的 TODO 事项。",
                    _ => "TODO 列表为空。使用 todo_add 添加待办事项。"
                };
                return Task.FromResult(ToolResult.Success(msg));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"TODO 列表 ({items.Count} 项):");

            // 当前游戏时间
            sb.AppendLine($"当前时间: {GameTimeHelper.CurrentTime()}");
            sb.AppendLine();

            foreach (var item in items)
            {
                var statusLabel = item.Status == "done" ? " [已完成]" : item.Status == "cancelled" ? " [已取消]" : "";
                var timeStr = GameTimeHelper.FormatGameTime(item.CreatedAtTick);
                sb.AppendLine($"  [P{item.Priority}] #{item.Id} {item.Description}{statusLabel}");
                sb.AppendLine($"         添加于 {timeStr} | 优先级: {item.Priority}/5");
            }
            return Task.FromResult(ToolResult.Success(sb.ToString().TrimEnd()));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
