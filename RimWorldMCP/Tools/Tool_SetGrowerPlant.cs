using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_SetGrowerPlant : ITool
    {
        public string Name => "set_grower_plant";
        public string Description => "设置水栽培盆或已有种植区的植物类型。自动识别坐标处的种植器（水栽培盆/种植区）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "种植器所在 X 坐标" },
                pos_y = new { type = "integer", description = "种植器所在 Y 坐标（映射到网格垂直轴 Z）" },
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
                    if (plantDef.plant?.sowTags == null || plantDef.plant.sowTags.Count == 0)
                        return ToolResult.Error($"{plantDef.label} ({plantDefName}) 不允许种植");

                    IntVec3 pos = new IntVec3(posX, 0, posY);

                    // 查找 IPlantToGrowSettable
                    // 优先找种植区,其次找建筑(水栽培盆等)
                    IPlantToGrowSettable? settable = null;
                    string targetLabel;

                    var zone = map.zoneManager.ZoneAt(pos) as Zone_Growing;
                    if (zone != null)
                    {
                        settable = zone;
                        targetLabel = $"种植区 (ID: {zone.ID})";
                    }
                    else
                    {
                        var building = pos.GetEdifice(map) as Building_PlantGrower;
                        if (building != null)
                        {
                            settable = building;
                            targetLabel = $"{building.def.label} ({building.def.defName})";
                        }
                        else
                        {
                            return ToolResult.Error($"坐标 ({posX}, {posY}) 处没有种植器（水栽培盆或种植区）");
                        }
                    }

                    // 验证植物兼容性
                    if (!PlantUtility.CanSowOnGrower(plantDef, settable))
                    {
                        string reason = settable is Zone_Growing
                            ? $"{plantDef.label} 不支持在地面种植区种植"
                            : $"{plantDef.label} 与该种植器不兼容（sowTag 不匹配）";
                        return ToolResult.Error(reason);
                    }

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

                    // 设置植物
                    var previousPlant = settable.GetPlantDefToGrow();
                    settable.SetPlantDefToGrow(plantDef);

                    string previousInfo = previousPlant != null ? $"（之前: {previousPlant.label}）" : "";
                    return ToolResult.Success($"已将 {targetLabel} 的种植植物设为 {plantDef.label} ({plantDefName}) {previousInfo}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"设置种植植物失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            return (posX, posY, posX, posY);
        }
    }
}
