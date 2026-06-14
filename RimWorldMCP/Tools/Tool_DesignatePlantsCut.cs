using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignatePlantsCut : ITool, IRequiresAdvanceTick
    {
        public string Name => "designate_plants_cut";
        public string Description => "标记指定区域的树木及可收获植物（如龙血树、仙人掌等）进行砍伐收获。等效建筑师工具栏\"砍伐木材\"过滤 + Cut 模式标记，砍完自动清理树桩。割草/灌木请使用 designate_clear_plants，收割作物请使用 designate_harvest。提供 end_x/end_y 可划定矩形范围。坐标范围为闭区间（两端坐标均包含）。";
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
                            return ToolResult.Error("殖民者无法到达砍伐区域，请确保有路径连通或传 ignore_unreachable=true。");
                    }

                    // 过滤 = 建筑师工具栏 HarvestWood（只可收获树木+树桩优先级）
                    // 标记 = CutPlant（Cut 模式，砍完自动清树桩）
                    var harvestWood = new Designator_PlantsHarvestWood();
                    var cutDesignator = new Designator_PlantsCut();
                    int designated = 0, skipped = 0;

                    foreach (IntVec3 cell in area)
                    {
                        if (!harvestWood.CanDesignateCell(cell).Accepted) { skipped++; continue; }
                        cutDesignator.DesignateSingleCell(cell);
                        designated++;
                    }

                    var sb = new StringBuilder();
                    sb.Append(isRange
                        ? $"已标记砍伐范围 ({minX},{minZ})~({maxX},{maxZ})：{designated} 株"
                        : $"已标记砍伐坐标 ({posX}, {posY})：{designated} 株");
                    sb.Append($"。（跳过 {skipped} 格）");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"标记砍伐失败: {ex.Message}"); }
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
