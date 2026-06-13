using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateRemoveFloor : ITool, IRequiresAdvanceTick
    {
        public string Name => "designate_remove_floor";
        public string Description => "标记拆除指定区域的可拆卸地板，还原为自然地形。不可拆卸的地形（如泥土、岩石）会自动跳过。提供 end_x/end_y 可划定矩形范围。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "起点 X 坐标（水平）" },
                pos_y = new { type = "integer", description = "起点 Y 坐标（垂直）" },
                end_x = new { type = "integer", description = "终点 X 坐标（可选，与 end_y 配对划定矩形范围）" },
                end_y = new { type = "integer", description = "终点 Y 坐标（可选，与 end_x 配对划定矩形范围）" },
                ignore_unreachable = new { type = "boolean", description = "跳过可达性检测（默认 false）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            int endX = posX, endY = posY;
            bool isRange = args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out endX)
                        && args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out endY);

            bool ignore_unreachable = false;
            if (args.Value.TryGetProperty("ignore_unreachable", out var jIgnore) && jIgnore.ValueKind == JsonValueKind.True)
                ignore_unreachable = true;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    int minX = Math.Min(posX, endX);
                    int maxX = Math.Max(posX, endX);
                    int minZ = Math.Min(posY, endY);
                    int maxZ = Math.Max(posY, endY);

                    CellRect area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    area.ClipInsideMap(map);

                    if (area.IsEmpty)
                        return ToolResult.Error($"指定范围 ({minX},{minZ})~({maxX},{maxZ}) 完全在地图外。");

                    if (!ignore_unreachable)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        var sampleCells = area.Cells.Take(20).ToList();
                        if (!sampleCells.Any(cell => colonists.Any(c => c.CanReach(cell, PathEndMode.ClosestTouch, Danger.Deadly))))
                            return ToolResult.Error("殖民者无法到达拆除区域，请确保有路径连通或传 ignore_unreachable=true。");
                    }

                    int designated = 0, skippedNotRemovable = 0, skippedBlocked = 0, skippedFogged = 0;

                    foreach (IntVec3 cell in area)
                    {
                        if (cell.Fogged(map)) { skippedFogged++; continue; }

                        // 检查是否有不可拆除的建筑阻挡（如墙体会阻止地板拆除）
                        if (WorkGiver_ConstructRemoveFloor.AnyBuildingBlockingFloorRemoval(cell, map))
                        { skippedBlocked++; continue; }

                        // 检查地形是否可拆
                        if (!map.terrainGrid.CanRemoveTopLayerAt(cell))
                        { skippedNotRemovable++; continue; }

                        // 避免重复标记
                        if (map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFloor) != null)
                        { skippedBlocked++; continue; }

                        map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.RemoveFloor));
                        designated++;
                    }

                    var sb = new StringBuilder();
                    sb.Append(isRange
                        ? $"已标记拆除范围 ({minX},{minZ})~({maxX},{maxZ}) 的地板：{designated} 格"
                        : $"已标记拆除坐标 ({posX}, {posY}) 的地板：{designated} 格");
                    sb.Append($"。（跳过 {skippedNotRemovable + skippedBlocked + skippedFogged} 格");

                    var details = new System.Collections.Generic.List<string>();
                    if (skippedNotRemovable > 0) details.Add($"不可拆卸地形 {skippedNotRemovable} 格");
                    if (skippedBlocked > 0) details.Add($"建筑阻挡/已标记 {skippedBlocked} 格");
                    if (skippedFogged > 0) details.Add($"迷雾 {skippedFogged} 格");
                    if (details.Count > 0) sb.Append("：" + string.Join("，", details));
                    sb.Append("）");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"标记地板拆除失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var endX)
                && args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var endY))
                return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
            return (posX, posY, posX, posY);
        }
    }
}
