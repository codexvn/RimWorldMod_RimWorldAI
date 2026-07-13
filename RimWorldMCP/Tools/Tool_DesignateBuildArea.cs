using System;
using System.Collections.Generic;
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
        public string Description => "矩形范围批量放置建造蓝图（墙、地板、家具、电线等）。支持 shape：fill=实心(默认)、perimeter=边框、line=直线。电线/导管必须用 line 或 perimeter，禁止 fill 整片铺设。跳过已占用/障碍/迷雾。单格+旋转用 designate_build。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thingDef_name = new { type = "string", description = "要建造的物品 DefName。例如 Wall(墙), WoodFloor(木地板), Concrete(混凝土), Door(门), PowerConduit(电线)" },
                pos_x = new { type = "integer", description = "起点/左下 X 坐标" },
                pos_y = new { type = "integer", description = "起点/左下 Y 坐标" },
                end_x = new { type = "integer", description = "终点/右上 X 坐标（可选，不传=单格）" },
                end_y = new { type = "integer", description = "终点/右上 Y 坐标（可选，不传=单格）" },
                stuff_defName = new { type = "string", description = "建筑材料 DefName（可选），如 Granite, Steel" },
                rotation = new { type = "string", description = "旋转方向（默认 North）", @enum = new[] { "North", "East", "South", "West" } },
                shape = new { type = "string", description = "放置形状（默认 fill）。fill=实心矩形；perimeter=仅矩形四边（围墙/环形电线）；line=水平或竖直直线（点对点布线，pos 与 end 必须共线）。电线/导管禁止 fill。", @enum = new[] { "fill", "perimeter", "line" }, @default = "fill" },
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

            string shape = "fill";
            if (args.Value.TryGetProperty("shape", out var jShape) && jShape.ValueKind == JsonValueKind.String)
            {
                shape = (jShape.GetString() ?? "fill").Trim().ToLowerInvariant();
                if (shape != "fill" && shape != "perimeter" && shape != "line")
                    return ToolResult.Error($"不支持的 shape: {shape}。可选: fill, perimeter, line。");
            }

            bool ignore_unreachable = false;
            if (args.Value.TryGetProperty("ignore_unreachable", out var jIgnore) && jIgnore.ValueKind == JsonValueKind.True)
                ignore_unreachable = true;
            bool checkPlan = true;
            if (args.Value.TryGetProperty("check_plan", out var jCP) && jCP.ValueKind == JsonValueKind.False)
                checkPlan = false;

            int rawPosX = posX, rawPosY = posY, rawEndX = endX, rawEndY = endY;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

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

                    if (IsConduitLike(thingDefName, def) && shape == "fill" && isRange
                        && (Math.Abs(rawEndX - rawPosX) > 0 && Math.Abs(rawEndY - rawPosY) > 0))
                    {
                        return ToolResult.Error(
                            "电线/导管禁止 shape=fill 整片铺设（会浪费钢材）。" +
                            "请用 shape=line（点对点直线）或 shape=perimeter（矩形边框）。");
                    }

                    if (shape == "line" && isRange
                        && rawPosX != rawEndX && rawPosY != rawEndY)
                    {
                        return ToolResult.Error(
                            $"shape=line 要求起点与终点共线（同 X 或同 Y）。" +
                            $"当前 ({rawPosX},{rawPosY})~({rawEndX},{rawEndY}) 不共线；请改用 line 分段，或 perimeter/fill。");
                    }

                    int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
                    int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);
                    CellRect area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    area.ClipInsideMap(map);
                    if (area.IsEmpty)
                        return ToolResult.Error($"范围 ({minX},{minZ})~({maxX},{maxZ}) 完全在地图外。");

                    if (!ignore_unreachable)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        var sampleCells = area.Cells.Where(c => MatchesShape(c, area, shape)).Take(20).ToList();
                        if (sampleCells.Count == 0)
                            sampleCells = area.Cells.Take(20).ToList();
                        if (!sampleCells.Any(cell => colonists.Any(c => c.CanReach(cell, PathEndMode.ClosestTouch, Danger.Deadly))))
                            return ToolResult.Error("殖民者无法到达目标区域，请确保有路径连通或传 ignore_unreachable=true。");
                    }

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

                    int placed = 0, skippedFog = 0, skippedBlocked = 0, skippedPlan = 0, skippedShape = 0;

                    if (isFloor)
                    {
                        var floorDes = new Designator_Build(terrainDef);
                        foreach (IntVec3 cell in area)
                        {
                            if (!MatchesShape(cell, area, shape)) { skippedShape++; continue; }
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
                            if (!MatchesShape(cell, area, shape)) { skippedShape++; continue; }
                            if (cell.Fogged(map)) { skippedFog++; continue; }
                            if (!designator.CanDesignateCell(cell).Accepted) { skippedBlocked++; continue; }
                            if (checkPlan && map.planManager.PlanAt(cell) == null) { skippedPlan++; continue; }
                            designator.DesignateSingleCell(cell);
                            placed++;
                        }
                    }

                    var sb = new StringBuilder();
                    var label = isFloor ? terrainDef!.label : def!.label;
                    string shapeLabel = shape switch
                    {
                        "perimeter" => "边框",
                        "line" => "直线",
                        _ => "实心"
                    };
                    sb.Append(isRange
                        ? $"已放置 {placed} 个 {label}（{thingDefName}），shape={shape}({shapeLabel})，范围 ({minX},{minZ})~({maxX},{maxZ})"
                        : $"已放置 {placed} 个 {label}（{thingDefName}），坐标 ({posX},{posY})");

                    var details = new List<string>();
                    if (skippedBlocked > 0) details.Add($"不可放置 {skippedBlocked} 格");
                    if (skippedFog > 0) details.Add($"迷雾 {skippedFog} 格");
                    if (skippedPlan > 0) details.Add($"非规划区 {skippedPlan} 格");
                    if (skippedShape > 0 && shape != "fill") details.Add($"非形状区 {skippedShape} 格");
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

        /// <summary>判断格子是否属于当前 shape。</summary>
        private static bool MatchesShape(IntVec3 cell, CellRect area, string shape)
        {
            if (shape == "fill" || string.IsNullOrEmpty(shape))
                return true;

            if (shape == "line")
            {
                // line 经校验后 area 本身已是 1 宽直线，全部保留
                return area.Width == 1 || area.Height == 1;
            }

            if (shape == "perimeter")
            {
                // 1×N / N×1 时整条都是边
                if (area.Width <= 1 || area.Height <= 1)
                    return true;
                return cell.x == area.minX || cell.x == area.maxX
                    || cell.z == area.minZ || cell.z == area.maxZ;
            }

            return true;
        }

        private static bool IsConduitLike(string thingDefName, ThingDef? def)
        {
            if (string.Equals(thingDefName, "PowerConduit", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(thingDefName, "PowerConduitHidden", StringComparison.OrdinalIgnoreCase))
                return true;
            if (def == null) return false;
            if (def.building != null && def.building.isPowerConduit)
                return true;
            return def.defName.IndexOf("Conduit", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
