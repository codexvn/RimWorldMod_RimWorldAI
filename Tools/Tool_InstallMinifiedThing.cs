using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_InstallMinifiedThing : ITool
    {
        public string Name => "install_minified_thing";
        public string Description => "将微缩物品（已拆卸的建筑）安装到指定坐标。放置安装蓝图后殖民者将自动搬运并安装。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "微缩物品的唯一 ID（来自 get_tile_detail，defName 含 Minified）" },
                pos_x = new { type = "integer", description = "目标 X 坐标（水平网格）" },
                pos_y = new { type = "integer", description = "目标 Y 坐标（垂直网格）" },
                rotation = new { type = "string", description = "旋转方向", @enum = new[] { "North", "East", "South", "West" } }
            },
            required = new[] { "thing_id", "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("thing_id", out var jTid) || !jTid.TryGetInt32(out var thingId))
                return ToolResult.Error("缺少必填参数: thing_id");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            string rotationStr = "North";
            if (args.Value.TryGetProperty("rotation", out var jRot))
                rotationStr = jRot.GetString() ?? "North";

            Rot4 rot = rotationStr switch
            {
                "North" => Rot4.North,
                "East" => Rot4.East,
                "South" => Rot4.South,
                "West" => Rot4.West,
                _ => Rot4.North
            };

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    MinifiedThing? minifiedThing = map.listerThings.AllThings
                        .OfType<MinifiedThing>()
                        .FirstOrDefault(t => t.thingIDNumber == thingId);
                    if (minifiedThing == null)
                        return ToolResult.Error($"找不到可安装的微缩物品 ID={thingId}。请确认这是已拆卸的微缩物品而非普通建筑。");

                    Thing innerThing = minifiedThing.InnerThing;
                    if (innerThing == null)
                        return ToolResult.Error("微缩物品内部为空，无法安装。");

                    IntVec3 targetCell = new IntVec3(posX, 0, posY);
                    if (!targetCell.InBounds(map))
                        return ToolResult.Error($"目标坐标 ({posX}, {posY}) 在地图范围外。");

                    // 验证放置合法性
                    var canPlace = GenConstruct.CanPlaceBlueprintAt(innerThing.def, targetCell, rot, map,
                        false, minifiedThing, innerThing, null, false, false, false);
                    if (!canPlace.Accepted)
                        return ToolResult.Error($"无法在 ({posX}, {posY}) 安装 {innerThing.Label}：{canPlace.Reason}");

                    // 取消已有蓝图
                    InstallBlueprintUtility.CancelBlueprintsFor(minifiedThing);

                    // 放置安装蓝图
                    GenConstruct.PlaceBlueprintForInstall(minifiedThing, targetCell, map, rot, Faction.OfPlayer);

                    return ToolResult.Success($"已在 ({posX}, {posY}) 放置 {innerThing.Label} 安装蓝图，朝向: {rotationStr}。殖民者将前往搬运并安装。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"安装失败: {ex.Message}");
                }
            });
        }
    }
}
