using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;
using RimWorldMCP.Helpers;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateBuildArea : ITool, IRequiresAdvanceTick
    {
        public string Name => "designate_build_area";
        public string Description => "矩形范围批量放置建造蓝图（墙、地板、家具等均可）。跳过已占用/障碍/迷雾。单格放置用 designate_build（支持旋转），批量放置用本工具。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thingDef_name = new { type = "string", description = "要建造的物品 DefName。例如 Wall(墙), WoodFloor(木地板), Concrete(混凝土), Door(门)" },
                pos_x = new { type = "integer", description = "左下 X 坐标" },
                pos_y = new { type = "integer", description = "左下 Y 坐标" },
                end_x = new { type = "integer", description = "右上 X 坐标（可选，不传=单格）" },
                end_y = new { type = "integer", description = "右上 Y 坐标（可选，不传=单格）" },
                stuff_defName = new { type = "string", description = "建筑材料 DefName（可选），如 Granite, Steel" },
                rotation = new { type = "string", description = "旋转方向（默认 North）", @enum = new[] { "North", "East", "South", "West" } },
                ignore_unreachable = new { type = "boolean", description = "跳过可达性检测（默认 false）" },
                check_plan = new { type = "boolean", description = "检查是否在规划区域内（默认 true）" }
            },
            required = new[] { "thingDef_name", "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("thingDef_name", out var jDefName))
                return ToolResult.Error("缺少必填参数: thingDef_name");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            string thingDefName = jDefName.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(thingDefName))
                return ToolResult.Error("thingDef_name 不能为空");

            int endX = posX, endY = posY;
            bool isRange = args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out endX)
                        && args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out endY);

            string rotationStr = "North";
            if (args.Value.TryGetProperty("rotation", out var jRot))
                rotationStr = jRot.GetString() ?? "North";

            string stuffDefName = "";
            if (args.Value.TryGetProperty("stuff_defName", out var jStuff))
                stuffDefName = jStuff.GetString() ?? "";

            bool ignore_unreachable = false;
            if (args.Value.TryGetProperty("ignore_unreachable", out var jIgnore) && jIgnore.ValueKind == JsonValueKind.True)
                ignore_unreachable = true;
            bool checkPlan = true;
            if (args.Value.TryGetProperty("check_plan", out var jCP) && jCP.ValueKind == JsonValueKind.False)
                checkPlan = false;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    // 解析 Def（ThingDef vs TerrainDef）
                    ThingDef? def = DefDatabase<ThingDef>.GetNamed(thingDefName, false);
                    TerrainDef? terrainDef = null;
                    if (def == null)
                    {
                        terrainDef = DefDatabase<TerrainDef>.GetNamed(thingDefName, false);
                        if (terrainDef == null)
                            return ToolResult.Error($"找不到 Def: {thingDefName}。请确认拼写。");
                    }

                    bool isFloor = terrainDef != null;
                    Rot4 rot = rotationStr switch
                    {
                        "North" => Rot4.North, "East" => Rot4.East,
                        "South" => Rot4.South, "West" => Rot4.West,
                        _ => Rot4.North
                    };

                    // 材料（仅 ThingDef 且非地板）
                    ThingDef? stuff = null;
                    if (!isFloor && def != null)
                    {
                        if (!string.IsNullOrEmpty(stuffDefName))
                        {
                            stuff = DefDatabase<ThingDef>.GetNamed(stuffDefName, false);
                            if (stuff == null) return ToolResult.Error($"找不到材料: {stuffDefName}");
                        }
                        else if (def.MadeFromStuff)
                            stuff = ThingDef.Named("Steel");
                        if (stuff != null && !def.MadeFromStuff)
                            return ToolResult.Error($"{def.label} 不支持材料选择。");
                    }

                    // 矩形范围
                    int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
                    int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);
                    CellRect area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    area.ClipInsideMap(map);
                    if (area.IsEmpty)
                        return ToolResult.Error($"范围 ({minX},{minZ})~({maxX},{maxZ}) 完全在地图外。");

                    // 可达性采样检查
                    if (!ignore_unreachable)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        var sampleCells = area.Cells.Take(20).ToList();
                        if (!sampleCells.Any(cell => colonists.Any(c => c.CanReach(cell, PathEndMode.ClosestTouch, Danger.Deadly))))
                            return ToolResult.Error("殖民者无法到达目标区域，请确保有路径连通或传 ignore_unreachable=true。");
                    }

                    // 资源检查（仅 ThingDef，仅警告）
                    string? resourceWarning = null;
                    if (!isFloor && def != null)
                    {
                        var needed = ResourceCheckHelper.CalculateCost(def, stuff);
                        if (needed.Count > 0)
                        {
                            var shortage = ResourceCheckHelper.CheckResources(map, needed);
                            if (shortage != null)
                                resourceWarning = $"⚠ 资源不足警告（蓝图已放置，但建造需要资源）:\n{shortage}";
                        }
                    }

                    int placed = 0, skippedFog = 0, skippedBlocked = 0, skippedPlan = 0;

                    if (isFloor)
                    {
                        var floorDes = new Designator_Build(terrainDef);
                        foreach (IntVec3 cell in area)
                        {
                            if (cell.Fogged(map)) { skippedFog++; continue; }
                            if (!floorDes.CanDesignateCell(cell).Accepted) { skippedBlocked++; continue; }
                            floorDes.DesignateSingleCell(cell);
                            placed++;
                        }
                    }
                    else
                    {
                        var designator = new Designator_Build(def);
                        if (stuff != null) designator.SetStuffDef(stuff);
                        if (rot != Rot4.North)
                        {
                            typeof(Designator_Place).GetField("placingRot",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                ?.SetValue(designator, rot);
                        }

                        foreach (IntVec3 cell in area)
                        {
                            if (cell.Fogged(map)) { skippedFog++; continue; }
                            if (!designator.CanDesignateCell(cell).Accepted) { skippedBlocked++; continue; }
                            if (checkPlan && map.planManager.PlanAt(cell) == null) { skippedPlan++; continue; }
                            designator.DesignateSingleCell(cell);
                            placed++;
                        }
                    }

                    var sb = new StringBuilder();
                    var label = isFloor ? terrainDef!.label : def!.label;
                    sb.Append(isRange
                        ? $"已放置 {placed} 个 {label}（{thingDefName}），范围 ({minX},{minZ})~({maxX},{maxZ})"
                        : $"已放置 {placed} 个 {label}（{thingDefName}），坐标 ({posX},{posY})");

                    var details = new System.Collections.Generic.List<string>();
                    if (skippedBlocked > 0) details.Add($"不可放置 {skippedBlocked} 格");
                    if (skippedFog > 0) details.Add($"迷雾 {skippedFog} 格");
                    if (skippedPlan > 0) details.Add($"非规划区 {skippedPlan} 格");
                    if (details.Count > 0) sb.Append("（跳过：" + string.Join("，", details) + "）");
                    sb.Append("。");

                    if (resourceWarning != null) sb.Append($"\n\n{resourceWarning}");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"批量建造失败: {ex.Message}"); }
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
