using System;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignatePlantsCut : ITool
    {
        public string Name => "designate_plants_cut";
        public string Description => "标记指定位置的植物/树木以供砍伐。殖民者会在工作完成后执行砍伐。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "X 坐标" },
                pos_y = new { type = "integer", description = "Y 坐标" },
                pos_z = new { type = "integer", description = "Z 坐标" },
                plant_defName = new { type = "string", description = "植物 defName 过滤（可选）" }
            },
            required = new[] { "pos_x", "pos_y", "pos_z" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");
            if (!args.Value.TryGetProperty("pos_z", out var jZ) || !jZ.TryGetInt32(out var posZ))
                return ToolResult.Error("缺少必填参数: pos_z");

            string plantDefName = "";
            if (args.Value.TryGetProperty("plant_defName", out var jPlant))
                plantDefName = jPlant.GetString() ?? "";

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    IntVec3 pos = new IntVec3(posX, posY, posZ);
                    if (!pos.InBounds(map))
                        return ToolResult.Error($"坐标 ({posX}, {posY}, {posZ}) 超出地图边界。");
                    if (pos.Fogged(map))
                        return ToolResult.Error($"坐标 ({posX}, {posY}, {posZ}) 处于迷雾中，无法标记砍伐。");

                    Plant plant = pos.GetPlant(map);
                    if (plant == null)
                        return ToolResult.Error($"坐标 ({posX}, {posY}, {posZ}) 没有植物。");
                    if (plant.def.plant == null)
                        return ToolResult.Error($"{plant.def.label} 不是可砍伐的植物。");

                    if (plant.TryGetComp<CompPlantPreventCutting>(out var comp) && comp.PreventCutting)
                        return ToolResult.Error($"{plant.def.label} 被禁止砍伐。");

                    if (!string.IsNullOrEmpty(plantDefName))
                    {
                        bool match = plant.def.defName.Equals(plantDefName, StringComparison.OrdinalIgnoreCase)
                                     || (plant.def.label != null && plant.def.label.IndexOf(plantDefName, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!match)
                            return ToolResult.Error($"坐标 ({posX}, {posY}, {posZ}) 的植物是 {plant.def.label} ({plant.def.defName})，与指定过滤 {plantDefName} 不匹配。");
                    }

                    if (map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null)
                        return ToolResult.Success($"植物 {plant.def.label} ({plant.def.defName}) 已被标记为砍伐，无需重复操作。");

                    map.designationManager.RemoveAllDesignationsOn(plant, false);
                    map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant, null));
                    if (DesignationDefOf.ExtractTree != null)
                        map.designationManager.TryRemoveDesignationOn(plant, DesignationDefOf.ExtractTree);

                    int yield = plant.YieldNow();
                    string yieldInfo = yield > 0 ? $"，预期产出: {yield}" : "";
                    return ToolResult.Success($"已标记 {plant.def.label} ({plant.def.defName}) 在坐标 ({posX}, {posY}, {posZ}) 以供砍伐{yieldInfo}。");
                }
                catch (Exception ex) { return ToolResult.Error($"标记砍伐失败: {ex.Message}"); }
            });
        }
    }
}
