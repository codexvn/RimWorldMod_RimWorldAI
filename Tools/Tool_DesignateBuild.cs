using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;
using RimWorldMCP.Helpers;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateBuild : ITool
    {
        public string Name => "designate_build";
        public string Description => "在指定地图坐标放置建造蓝图。可用于建造墙体、门、地板、家具、工作台等。相邻房间可共用已有墙体，无需重复建造。⚠ 调用前应先使用 get_structure_layout 查看当前布局。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thingDef_name = new { type = "string", description = "要建造的物品 DefName。常用: Wall(墙), Door(门), TableSmithy(锻造台), WoodFloor(木地板), Bed(床), StandingLamp(立灯)" },
                pos_x = new { type = "integer", description = "X 坐标" },
                pos_y = new { type = "integer", description = "Y 坐标" },
                rotation = new { type = "string", description = "旋转方向", @enum = new[] { "North", "East", "South", "West" } },
                stuff_defName = new { type = "string", description = "建筑材料 DefName（可选），先用 search_thing_def(keyword=\"花岗岩\", flags=\"stuff\") 查可用材料" },
                ignore_unreachable = new { type = "boolean", description = "跳过可达性检测（默认 false）" },
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

            bool ignore_unreachable = false;
            if (args.Value.TryGetProperty("ignore_unreachable", out var jIgnore) && jIgnore.ValueKind == JsonValueKind.True)
                ignore_unreachable = true;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    if (Find.CurrentMap == null)
                        return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    ThingDef def = DefDatabase<ThingDef>.GetNamed(thingDefName, false);
                    if (def == null)
                        return ToolResult.Error($"找不到 ThingDef: {thingDefName}。请确认 DefName 拼写正确。\n\n💡 提示: 用 search_thing_def(keyword=\"{thingDefName}\", category=\"building\") 查找可用建筑。");
                    if (!(def is BuildableDef))
                        return ToolResult.Error($"{thingDefName} 不是可建造的类型。\n\n💡 提示: 用 search_thing_def(keyword=\"{thingDefName}\", category=\"building\") 查找可用建筑。");

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

                    if (def.graphicData == null)
                        return ToolResult.Error($"{thingDefName} 缺少 graphicData 图形定义，无法创建设计器。\n\n💡 提示: 用 search_thing_def(keyword=\"{thingDefName}\", category=\"building\") 查找类似可用建筑。");

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

                    // 资源检查（仅警告不阻断）
                    string? resourceWarning = null;
                    {
                        var needed = ResourceCheckHelper.CalculateCost(def, stuff);
                        if (needed.Count > 0)
                        {
                            var shortage = ResourceCheckHelper.CheckResources(Find.CurrentMap, needed);
                            if (shortage != null)
                                resourceWarning = $"⚠ 资源不足警告（蓝图已放置，但建造需要以下资源）:\n{shortage}";
                        }
                    }

                    // 迷雾检查
                    if (pos.Fogged(Find.CurrentMap))
                        return ToolResult.Error($"目标位置 ({posX}, {posY}) 被迷雾覆盖，无法建造。请先探索该区域。");

                    // 验证可放置性
                    var canPlace = designator.CanDesignateCell(pos);
                    if (!canPlace)
                        return ToolResult.Error($"无法在 ({posX}, {posY}) 放置 {def.label}：{canPlace.Reason}");

                    if (!ignore_unreachable)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        if (!colonists.Any(c => c.CanReach(pos, PathEndMode.ClosestTouch, Danger.Deadly)))
                            return ToolResult.Error($"殖民者无法到达目标位置 ({posX}, {posY})，无法放置蓝图。请确保有路径连通或传 ignore_unreachable=true。");
                    }

                    designator.DesignateSingleCell(pos);

                    string stuffInfo = stuff != null ? $"（材料: {stuff.label}）" : "";
                    string result = $"已成功在坐标 ({posX}, {posY}) 放置 {def.label} ({thingDefName}){stuffInfo}，朝向: {rotationStr}。";
                    if (resourceWarning != null)
                        result += $"\n\n{resourceWarning}";
                    return ToolResult.Success(result);
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"建造失败: {ex.Message}");
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
