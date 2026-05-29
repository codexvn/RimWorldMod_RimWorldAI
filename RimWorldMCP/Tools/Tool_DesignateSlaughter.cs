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
    public class Tool_DesignateSlaughter : ITool
    {
        public string Name => "designate_slaughter";
        public string Description => "标记指定区域的已驯服动物进行宰杀。殖民者会自动执行宰杀。坐标范围为闭区间（两端坐标均包含）。";

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
                int count = 0;
                var names = new List<string>();
                for (int x = minX; x <= maxX; x++)
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        if (!cell.InBounds(map) || cell.Fogged(map)) continue;
                        foreach (var t in cell.GetThingList(map))
                        {
                            if (t is not Pawn pawn) continue;
                            if (!pawn.IsAnimal || pawn.Faction != Faction.OfPlayer) continue;
                            if (pawn.InAggroMentalState) continue;
                            if (map.designationManager.DesignationOn(pawn, DesignationDefOf.Slaughter) != null) continue;
                            map.designationManager.AddDesignation(new Designation(pawn, DesignationDefOf.Slaughter));
                            var release = map.designationManager.DesignationOn(pawn, DesignationDefOf.ReleaseAnimalToWild);
                            if (release != null) map.designationManager.RemoveDesignation(release);
                            count++;
                            names.Add(pawn.LabelShort);
                        }
                    }
                if (count == 0) return ToolResult.Success("区域内没有可宰杀的动物。");
                return ToolResult.Success($"已标记 {count} 个宰杀目标: {string.Join(", ", names)}");
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
