using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_DeleteMemory : ITool
    {
        public string Name => "delete_memory";
        public string Description => "删除指定 ID 的记忆。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string", description = "记忆 ID" }
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

            if (MemoryManager.Delete(id))
                return Task.FromResult(ToolResult.Success($"已删除记忆 #{id}。"));

            return Task.FromResult(ToolResult.Error($"未找到记忆 #{id}。请用 list_memories 查看可用的 ID。"));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
