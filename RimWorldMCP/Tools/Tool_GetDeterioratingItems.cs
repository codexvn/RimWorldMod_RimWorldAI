using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_GetDeterioratingItems : ITool
    {
        public string Name => "get_deteriorating_items";
        public string Description => "返回前 10 个正在腐烂或露天掉耐久的物品，按危险程度降序排列。可指定数量。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                count = new
                {
                    type = "integer",
                    description = "可选，返回数量（默认 10）",
                    minimum = 1,
                    maximum = 50
                }
            },
            required = new string[] { }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args) => Task.FromResult(Execute(args));

        private static ToolResult Execute(JsonElement? args)
        {
            int count = 10;
            if (args != null && args.Value.TryGetProperty("count", out var jCount) && jCount.TryGetInt32(out var c))
                count = Math.Max(1, Math.Min(50, c));

            var items = DeteriorationTracker.GetTopDangerous(count);

            if (items.Count == 0)
                return ToolResult.Success("当前未检测到正在腐烂或露天掉耐久的物品。\n（提示：数据由周期性扫描更新，若刚加载存档请稍后再查。）");

            var sb = new StringBuilder();
            sb.AppendLine($"## 正在恶化的物品（前 {items.Count} 个）\n");

            int rank = 1;
            foreach (var item in items)
            {
                string dangerIcon = item.DangerType == "腐烂" ? "🧊" : "☀️";
                sb.AppendLine($"### {rank}. {dangerIcon} {item.Label}");
                sb.AppendLine($"- 类型: {item.DangerType}");
                sb.AppendLine($"- 恶化程度: {item.DegradationPct:F1}%");
                sb.AppendLine($"- 位置: ({item.PosX}, {item.PosZ})");
                sb.AppendLine($"- ID: {item.ThingID}");
                if (!string.IsNullOrEmpty(item.Detail))
                    sb.AppendLine($"- 详情: {item.Detail}");
                rank++;
            }

            return ToolResult.Success(sb.ToString().TrimEnd());
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
