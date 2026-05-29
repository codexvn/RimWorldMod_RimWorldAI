using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_TodoSetStatus : ITool
    {
        public string Name => "todo_set_status";
        public string Description => "修改指定 ID 待办事项的状态。done/cancelled 会自动删除，仅 pending 保留。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string", description = "待办事项的 ID" },
                status = new
                {
                    type = "string",
                    @enum = new[] { "pending", "done", "cancelled" },
                    description = "目标状态: pending=待办, done=完成, cancelled=取消"
                }
            },
            required = new[] { "id", "status" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(ToolResult.Error("缺少参数: id, status"));

            if (!args.Value.TryGetProperty("id", out var idProp))
                return Task.FromResult(ToolResult.Error("缺少必填参数: id"));

            if (!args.Value.TryGetProperty("status", out var statusProp))
                return Task.FromResult(ToolResult.Error("缺少必填参数: status"));

            var id = idProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult(ToolResult.Error("id 不能为空。"));

            var status = statusProp.GetString() ?? "";
            if (status != "pending" && status != "done" && status != "cancelled")
                return Task.FromResult(ToolResult.Error($"无效状态 \"{status}\"，支持: pending, done, cancelled。"));

            var updated = TodoManager.UpdateStatus(id, status);
            if (!updated)
                return Task.FromResult(ToolResult.Error($"未找到待办 #{id}。请用 todo_query 查看当前的 ID 列表。"));

            if (status == "done" || status == "cancelled")
                return Task.FromResult(ToolResult.Success($"待办 #{id} 已完成并删除。"));
            return Task.FromResult(ToolResult.Success($"已更新待办 #{id} 状态为 {status}。"));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
