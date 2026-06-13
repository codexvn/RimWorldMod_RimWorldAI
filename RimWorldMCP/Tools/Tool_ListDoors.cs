using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ListDoors : ITool
    {
        public string Name => "list_doors";
        public string Description => "列出指定范围内所有门，含 ID、位置、状态和材质。提供 end_x/end_y 划定查询范围，不提供则查全图。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "查询范围左下 X（不提供则查全图）" },
                pos_y = new { type = "integer", description = "查询范围左下 Y（不提供则查全图）" },
                end_x = new { type = "integer", description = "查询范围右上 X（可选）" },
                end_y = new { type = "integer", description = "查询范围右上 Y（可选）" },
                only_hold_open = new { type = "boolean", description = "仅返回保持开启的门（默认 false）" },
                only_closed = new { type = "boolean", description = "仅返回关闭的门（默认 false）" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            bool hasRange = args != null && args.Value.TryGetProperty("pos_x", out _);
            int posX = 0, posY = 0, endX = 0, endY = 0;
            bool allMap = true;

            if (hasRange)
            {
                if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out posX)) return ToolResult.Error("pos_x 需要整数");
                if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out posY)) return ToolResult.Error("pos_y 需要整数");
                allMap = false;

                endX = posX; endY = posY;
                if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex)) endX = ex;
                if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey)) endY = ey;
            }

            bool onlyHoldOpen = false, onlyClosed = false;
            if (args != null)
            {
                if (args.Value.TryGetProperty("only_hold_open", out var jHo) && jHo.ValueKind == JsonValueKind.True) onlyHoldOpen = true;
                if (args.Value.TryGetProperty("only_closed", out var jCl) && jCl.ValueKind == JsonValueKind.True) onlyClosed = true;
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载存档。");

                    var doors = new List<Building_Door>();
                    if (allMap)
                    {
                        // 全图搜索
                        foreach (var b in map.listerBuildings.allBuildingsColonist)
                        {
                            if (b is Building_Door door1) doors.Add(door1);
                        }
                    }
                    else
                    {
                        int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
                        int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);
                        var area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                        area.ClipInsideMap(map);

                        foreach (IntVec3 cell in area)
                        {
                            var door2 = cell.GetDoor(map);
                            if (door2 != null && !doors.Contains(door2)) doors.Add(door2);
                        }
                    }

                    // 过滤
                    if (onlyHoldOpen) doors = doors.Where(d => d.HoldOpen).ToList();
                    else if (onlyClosed) doors = doors.Where(d => !d.Open && !d.HoldOpen).ToList();

                    if (doors.Count == 0)
                        return ToolResult.Success("指定范围内没有符合条件的门。");

                    var sb = new StringBuilder();
                    sb.AppendLine($"共 {doors.Count} 扇门：");
                    sb.AppendLine();
                    sb.AppendLine("| ID | 位置 | 状态 | 材质 | 耐久 |");
                    sb.AppendLine("|----|------|------|------|------|");

                    foreach (var door in doors.OrderBy(d => d.Position.x).ThenBy(d => d.Position.z))
                    {
                        var state = door.Open ? "开启中" : (door.HoldOpen ? "保持开启" : "关闭");
                        var stuff = door.Stuff?.label ?? door.def.label;
                        var hp = $"{door.HitPoints}/{door.MaxHitPoints}";
                        sb.AppendLine($"| {door.thingIDNumber} | ({door.Position.x},{door.Position.z}) | {state} | {stuff} | {hp} |");
                    }

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"查询门失败: {ex.Message}"); }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex)
                && args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey))
                return (Math.Min(posX, ex), Math.Min(posY, ey), Math.Max(posX, ex), Math.Max(posY, ey));
            return (posX, posY, posX, posY);
        }
    }
}
