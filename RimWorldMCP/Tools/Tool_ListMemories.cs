using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_ListMemories : ITool
    {
        public string Name => "list_memories";
        public string Description => "列出所有记忆，按优先级降序排列。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            @required = new string[] { }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var entries = MemoryManager.ListAll();

            if (entries.Count == 0)
                return Task.FromResult(ToolResult.Success("暂无记忆记录。"));

            var sb = new StringBuilder();
            sb.AppendLine($"共 {entries.Count} 条记忆：\n");

            foreach (var entry in entries)
            {
                sb.AppendLine($"#{entry.Id} [P{entry.Priority}] {entry.Timestamp}");
                sb.AppendLine($"  {entry.Content}");
                sb.AppendLine();
            }

            return Task.FromResult(ToolResult.Success(sb.ToString().TrimEnd()));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
