using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_TodoDelete : ITool
    {
        public string Name => "todo_delete";
        public string Description => "删除指定 ID 的待办事项。ID 可通过 todo_query 获取。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string", description = "待办事项的 ID" }
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

            var deleted = TodoManager.Delete(id);
            return Task.FromResult(deleted
                ? ToolResult.Success($"已删除待办 #{id}。")
                : ToolResult.Error($"未找到待办 #{id}。请用 todo_query 查看当前的 ID 列表。"));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
