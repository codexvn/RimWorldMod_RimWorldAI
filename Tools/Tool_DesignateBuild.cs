using System;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;
using RimWorldMCP.Helpers;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateBuild : ITool
    {
        public string Name => "designate_build";
        public string Description => "在指定地图坐标放置建造蓝图。可用于建造墙体、门、地板、家具、工作台等。相邻房间可共用已有墙体，无需重复建造。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thingDef_name = new { type = "string", description = "要建造的物品 DefName。常用: Wall(墙), Door(门), TableSmithy(锻造台), WoodFloor(木地板), Bed(床), StandingLamp(立灯)" },
                pos_x = new { type = "integer", description = "X 坐标" },
                pos_y = new { type = "integer", description = "Y 坐标" },
                rotation = new { type = "string", description = "旋转方向", @enum = new[] { "North", "East", "South", "West" } },
                stuff_defName = new { type = "string", description = "建筑材料 DefName（可选）", @enum = BuildingMaterialHelper.GetStuffEnum() },
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

            string rotationStr = "North";
            if (args.Value.TryGetProperty("rotation", out var jRot))
                rotationStr = jRot.GetString() ?? "North";

            string stuffDefName = "";
            if (args.Value.TryGetProperty("stuff_defName", out var jStuff))
                stuffDefName = jStuff.GetString() ?? "";

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    if (Find.CurrentMap == null)
                        return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    ThingDef def = DefDatabase<ThingDef>.GetNamed(thingDefName, false);
                    if (def == null)
                        return ToolResult.Error($"找不到 ThingDef: {thingDefName}。请确认 DefName 拼写正确。");
                    if (!(def is BuildableDef))
                        return ToolResult.Error($"{thingDefName} 不是可建造的类型。");

                    Rot4 rot = rotationStr switch
                    {
                        "North" => Rot4.North,
                        "East" => Rot4.East,
                        "South" => Rot4.South,
                        "West" => Rot4.West,
                        _ => Rot4.North
                    };

                    ThingDef? stuff = null;
                    if (!string.IsNullOrEmpty(stuffDefName))
                    {
                        stuff = DefDatabase<ThingDef>.GetNamed(stuffDefName, false);
                        if (stuff == null)
                            return ToolResult.Error($"找不到材料 ThingDef: {stuffDefName}");
                    }
                    else if (def.MadeFromStuff)
                    {
                        stuff = ThingDef.Named("Steel");
                    }

                    if (stuff != null && !def.MadeFromStuff)
                        return ToolResult.Error($"{def.label} ({thingDefName}) 不支持材料选择，请勿指定 stuff_defName。");

                    IntVec3 pos = new IntVec3(posX, 0, posY);

                    // 复用游戏原生 Designator_Build 放置逻辑
                    var designator = new Designator_Build(def);
                    if (stuff != null)
                        designator.SetStuffDef(stuff);
                    if (rot != Rot4.North)
                    {
                        typeof(Designator_Place).GetField("placingRot",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            ?.SetValue(designator, rot);
                    }

                    // 资源检查
                    {
                        var needed = ResourceCheckHelper.CalculateCost(def, stuff);
                        if (needed.Count > 0)
                        {
                            var shortage = ResourceCheckHelper.CheckResources(Find.CurrentMap, needed);
                            if (shortage != null)
                                return ToolResult.Error($"建造 {def.label} ({thingDefName}) 资源不足:\n{shortage}");
                        }
                    }

                    // 验证可放置性
                    var canPlace = designator.CanDesignateCell(pos);
                    if (!canPlace)
                        return ToolResult.Error($"无法在 ({posX}, {posY}) 放置 {def.label}：{canPlace.Reason}");

                    designator.DesignateSingleCell(pos);

                    string stuffInfo = stuff != null ? $"（材料: {stuff.label}）" : "";
                    return ToolResult.Success($"已成功在坐标 ({posX}, {posY}) 放置 {def.label} ({thingDefName}){stuffInfo}，朝向: {rotationStr}。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"建造失败: {ex.Message}");
                }
            });
        }
    }
}
