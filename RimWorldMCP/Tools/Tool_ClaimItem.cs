using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_ClaimItem : ITool
    {
        public string Name => "claim_item";
        public string Description => "占有指定区域的物品或建筑，将其归属为玩家派系。用于战后缴获和占领。坐标范围为闭区间（两端坐标均包含）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上 X 坐标" },
                pos_y = new { type = "integer", description = "左上 Y 坐标" },
                end_x = new { type = "integer", description = "右下 X 坐标（可选）" },
                end_y = new { type = "integer", description = "右下 Y 坐标（可选）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var startX)) return ToolResult.Error("缺少 pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var startY)) return ToolResult.Error("缺少 pos_y");

            int endX = startX, endY = startY;
            if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey)) endY = ey;

            int minX = Math.Min(startX, endX), maxX = Math.Max(startX, endX);
            int minZ = Math.Min(startY, endY), maxZ = Math.Max(startY, endY);

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前没有可用地图。");
                if (Faction.OfPlayer == null) return ToolResult.Error("玩家派系不存在");

                int count = 0;
                var items = new List<string>();
                for (int x = minX; x <= maxX; x++)
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        if (!cell.InBounds(map) || cell.Fogged(map)) continue;
                        foreach (var t in cell.GetThingList(map))
                        {
                            if (t.Faction == Faction.OfPlayer) continue;
                            if (!t.ClaimableBy(Faction.OfPlayer)) continue;
                            t.SetFaction(Faction.OfPlayer);
                            count++;
                            if (items.Count < 10) items.Add(t.Label);
                        }
                    }
                if (count == 0) return ToolResult.Success("区域内没有可占有的物品或建筑。");
                var sb = new StringBuilder($"已占有 {count} 个物品/建筑");
                if (items.Count > 0) sb.Append($": {string.Join(", ", items)}");
                if (count > 10) sb.Append($" ... 等");
                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var x)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var y)) return null;
            int ex = x, ey = y;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var _ex)) ex = _ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var _ey)) ey = _ey;
            return (Math.Min(x, ex), Math.Min(y, ey), Math.Max(x, ex), Math.Max(y, ey));
        }
}
}
