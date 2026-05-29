using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_CreateGrowingZone : ITool
    {
        public string Name => "create_growing_zone";
        public string Description => "在指定矩形区域创建种植区并设置要种植的植物。坐标使用左上→右下模式,不提供 end 则只操作单格。⚠ 调用前应先使用 get_structure_layout 查看当前布局。坐标范围为闭区间（两端坐标均包含）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上角 X 坐标" },
                pos_y = new { type = "integer", description = "左上角 Y 坐标（映射到网格垂直轴 Z）" },
                end_x = new { type = "integer", description = "右下角 X 坐标（可选，与 end_y 配对划定矩形范围）" },
                end_y = new { type = "integer", description = "右下角 Y 坐标（可选，与 end_x 配对划定矩形范围）" },
                skip_roof_check = new { type = "boolean", description = "跳过屋顶校验（默认 false，种植区要求无屋顶以获取光照）" },
                ignore_unreachable = new { type = "boolean", description = "跳过可达性检测（默认 false）" },
                plant_defName = new
                {
                    type = "string",
                    description = "要种植的植物 DefName，先用 search_thing_def(keyword=\"水稻\", category=\"plant\") 查可用植物"
                }
            },
            required = new[] { "pos_x", "pos_y", "plant_defName" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");
            if (!args.Value.TryGetProperty("plant_defName", out var jPlant) || jPlant.GetString() is not { Length: > 0 } plantDefName)
                return ToolResult.Error("缺少必填参数: plant_defName");

            int endX = posX, endY = posY;
            bool isRange = args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out endX)
                        && args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out endY);

            bool skipRoof = false;
            if (args.Value.TryGetProperty("skip_roof_check", out var jSkipRoof) && jSkipRoof.ValueKind == JsonValueKind.True)
                skipRoof = true;
            bool ignore_unreachable = false;
            if (args.Value.TryGetProperty("ignore_unreachable", out var jIgnore) && jIgnore.ValueKind == JsonValueKind.True)
                ignore_unreachable = true;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    // 验证 plant def
                    var plantDef = DefDatabase<ThingDef>.GetNamed(plantDefName, false);
                    if (plantDef == null)
                        return ToolResult.Error($"找不到植物 Def: {plantDefName}");
                    if (plantDef.category != ThingCategory.Plant)
                        return ToolResult.Error($"{plantDefName} 不是植物类型");
                    if (plantDef.plant?.sowTags?.Contains("Ground") != true)
                        return ToolResult.Error($"{plantDef.label} ({plantDefName}) 不支持在地面种植区种植");

                    // 检查研究前置
                    if (plantDef.plant.sowResearchPrerequisites != null)
                    {
                        foreach (var prereq in plantDef.plant.sowResearchPrerequisites)
                        {
                            if (!prereq.IsFinished)
                                return ToolResult.Error($"{plantDef.label} 需要先研究: {prereq.label}");
                        }
                    }
                    if (plantDef.plant.mustBeWildToSow && !map.wildPlantSpawner.AllWildPlants.Contains(plantDef))
                        return ToolResult.Error($"{plantDef.label} 只能野生播种，地图上未发现该植物");

                    // 构建区域
                    int minX = Math.Min(posX, endX);
                    int maxX = Math.Max(posX, endX);
                    int minZ = Math.Min(posY, endY);
                    int maxZ = Math.Max(posY, endY);

                    CellRect area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    area.ClipInsideMap(map);

                    if (area.IsEmpty)
                        return ToolResult.Error($"指定范围 ({minX},{minZ})~({maxX},{maxZ}) 完全在地图外");

                    // 屋顶校验：种植区上方必须无屋顶（植物需要光照）
                    if (!skipRoof)
                    {
                        var roofed = area.Cells.Where(c => c.Roofed(map)).ToList();
                        if (roofed.Count > 0)
                        {
                            var sample = roofed.Take(3).Select(c => $"({c.x},{c.z})").ToList();
                            string sampleStr = string.Join(", ", sample);
                            int roofTotal = area.Width * area.Height;
                            if (roofed.Count < roofTotal)
                                return ToolResult.Error($"种植区不能有屋顶！{roofed.Count} 格有屋顶: {sampleStr}... 植物需要露天光照。移除屋顶或传 skip_roof_check=true");
                            else
                                return ToolResult.Error($"指定范围全部有屋顶，植物无法生长。请选择露天区域或传 skip_roof_check=true");
                        }
                    }

                    // 肥力检查 (参照 Designator_ZoneAdd_Growing.CanDesignateCell)
                    float minFertility = ModsConfig.BiotechActive ? 0.5f : ThingDefOf.Plant_Potato.plant.fertilityMin;
                    if (ModsConfig.IdeologyActive)
                        minFertility = Math.Min(minFertility, ThingDefOf.Plant_Nutrifungus.plant.fertilityMin);

                    var infertileCells = area.Cells
                        .Where(c => c.GetFertility(map) < minFertility)
                        .ToList();
                    if (infertileCells.Count > 0)
                    {
                        if (infertileCells.Count == area.Width * area.Height)
                            return ToolResult.Error($"指定范围的所有单元格肥力不足（最低需要 {minFertility:F0}%），无法创建种植区");
                        // 部分格子肥力不足,继续创建但会跳过
                    }

                    // 创建种植区
                    var zone = new Zone_Growing(map.zoneManager);
                    map.zoneManager.RegisterZone(zone);

                    int added = 0, skipped = 0;
                    foreach (IntVec3 cell in area)
                    {
                        if (zone.Cells.Contains(cell)) { skipped++; continue; }
                        if (map.zoneManager.ZoneAt(cell) is Zone_Growing) { skipped++; continue; }
                        if (cell.GetFertility(map) < minFertility) { skipped++; continue; }
                        var things = cell.GetThingList(map);
                        if (things.Any(t => !t.def.CanOverlapZones)) { skipped++; continue; }
                        zone.AddCell(cell);
                        added++;
                    }

                    if (zone.Cells.Count == 0)
                    {
                        map.zoneManager.DeregisterZone(zone);
                        return ToolResult.Error("指定区域的所有单元格无法添加（已被占用或肥力不足）");
                    }

                    zone.CheckContiguous();
                    zone.SetPlantDefToGrow(plantDef);

                    // 验证殖民者可达
                    if (!ignore_unreachable)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        bool reachable = false;
                        foreach (var cell in zone.Cells)
                        {
                            if (colonists.Any(c => c.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)))
                            {
                                reachable = true;
                                break;
                            }
                        }
                        if (!reachable)
                        {
                            map.zoneManager.DeregisterZone(zone);
                            return ToolResult.Error("殖民者无法到达此种植区（被墙壁/障碍物完全阻隔），请确保有门连通或传 ignore_unreachable=true。");
                        }
                    }

                    var sb = new StringBuilder();
                    sb.Append(isRange
                        ? $"已创建种植区 ({minX},{minZ})~({maxX},{maxZ})：{added} 格"
                        : $"已创建种植区 ({posX}, {posY})：{added} 格");
                    if (skipped > 0) sb.Append($"（跳过 {skipped} 格）");
                    sb.Append($" | 植物: {plantDef.label}");
                    sb.Append($" | 区域 ID: {zone.ID}");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"创建种植区失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var endX)
                && args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var endY))
                return (posX, posY, endX, endY);
            return (posX, posY, posX, posY);
        }
    }
}
