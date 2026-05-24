using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateRoom : ITool
    {
        public string Name => "designate_room";
        public string Description => "快速建造一个矩形房间（自动放置四面墙）。尺寸不包括墙体本身（内部空间）。例如 13x13 的房间会建造 15x15 的外墙范围。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                center_x = new { type = "integer", description = "房间中心的 X 坐标" },
                center_y = new { type = "integer", description = "房间中心的 Y 坐标" },
                width = new { type = "integer", description = "房间内部宽度（不含墙），默认 13", @default = 13 },
                height = new { type = "integer", description = "房间内部高度（不含墙），默认 13", @default = 13 },
                wall_defName = new { type = "string", description = "墙体材料 DefName，默认 Steel", @default = "Steel" },
                door_positions = new { type = "string", description = "门的位置，多个用逗号分隔。可选: top, bottom, left, right, center_top, center_bottom, center_left, center_right" },
                door_defName = new { type = "string", description = "门的 DefName，默认 Door", @default = "Door" },
                floor_defName = new { type = "string", description = "地板 DefName，可选" }
            },
            required = new[] { "center_x", "center_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("center_x", out var jX) || !jX.TryGetInt32(out var centerX))
                return ToolResult.Error("缺少必填参数: center_x");
            if (!args.Value.TryGetProperty("center_y", out var jY) || !jY.TryGetInt32(out var centerY))
                return ToolResult.Error("缺少必填参数: center_y");

            int width = 13, height = 13;
            if (args.Value.TryGetProperty("width", out var jW) && jW.TryGetInt32(out var wv) && wv > 0) width = wv;
            if (args.Value.TryGetProperty("height", out var jH) && jH.TryGetInt32(out var hv) && hv > 0) height = hv;

            string wallDefName = "Steel";
            if (args.Value.TryGetProperty("wall_defName", out var jWall)) wallDefName = jWall.GetString() ?? "Steel";

            string doors = "";
            if (args.Value.TryGetProperty("door_positions", out var jDoors)) doors = jDoors.GetString() ?? "";

            string doorDefName = "Door";
            if (args.Value.TryGetProperty("door_defName", out var jDoor)) doorDefName = jDoor.GetString() ?? "Door";

            string floorDefName = "";
            if (args.Value.TryGetProperty("floor_defName", out var jFloor)) floorDefName = jFloor.GetString() ?? "";

            // 计算房间几何（不涉及游戏状态，可在任意线程执行）
            int roomW = width + 2;
            int roomH = height + 2;
            int startX = centerX - roomW / 2;
            int startY = centerY - roomH / 2;
            int endX = startX + roomW - 1;
            int endY = startY + roomH - 1;

            // 计算墙体位置（矩形四条边）
            var wallPositions = new List<(int x, int y)>();
            for (int x = startX; x <= endX; x++)
            {
                wallPositions.Add((x, startY)); // 上边
                wallPositions.Add((x, endY));   // 下边
            }
            for (int y = startY + 1; y < endY; y++)
            {
                wallPositions.Add((startX, y)); // 左边
                wallPositions.Add((endX, y));   // 右边
            }

            // 解析门的位置
            var doorPosSet = new HashSet<(int x, int y)>();
            if (!string.IsNullOrEmpty(doors))
            {
                foreach (string posStr in doors.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = posStr.Trim();
                    (int x, int y)? doorPoint = trimmed switch
                    {
                        "top" => (centerX, startY),
                        "bottom" => (centerX, endY),
                        "left" => (startX, centerY),
                        "right" => (endX, centerY),
                        "center_top" => (centerX, startY),
                        "center_bottom" => (centerX, endY),
                        "center_left" => (startX, centerY),
                        "center_right" => (endX, centerY),
                        _ => null
                    };
                    if (doorPoint != null)
                        doorPosSet.Add(doorPoint.Value);
                }
            }

            // 计算地板位置（内部区域）
            var floorPositions = new List<(int x, int y)>();
            if (!string.IsNullOrEmpty(floorDefName))
            {
                for (int x = startX + 1; x < endX; x++)
                {
                    for (int y = startY + 1; y < endY; y++)
                    {
                        floorPositions.Add((x, y));
                    }
                }
            }

            int wallCount = wallPositions.Count;
            int doorCount = doorPosSet.Count;
            int floorCount = floorPositions.Count;

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    // 查找 Def
                    ThingDef wallDef = DefDatabase<ThingDef>.GetNamed(wallDefName, false);
                    if (wallDef == null)
                        return ToolResult.Error($"找不到墙体 ThingDef: {wallDefName}。请确认 DefName 拼写正确。");

                    ThingDef? doorDef = null;
                    if (doorCount > 0)
                    {
                        doorDef = DefDatabase<ThingDef>.GetNamed(doorDefName, false);
                        if (doorDef == null)
                            return ToolResult.Error($"找不到门 ThingDef: {doorDefName}。请确认 DefName 拼写正确。");
                    }

                    ThingDef? floorDef = null;
                    if (floorCount > 0)
                    {
                        floorDef = DefDatabase<ThingDef>.GetNamed(floorDefName, false);
                        if (floorDef == null)
                            return ToolResult.Error($"找不到地板 ThingDef: {floorDefName}。请确认 DefName 拼写正确。");
                    }

                    int placedWalls = 0, placedDoors = 0, placedFloors = 0;
                    var errors = new List<string>();

                    // 地图边界检查
                    int mapW = map.Size.x, mapH = map.Size.z;
                    if (startX < 0 || startY < 0 || endX >= mapW || endY >= mapH)
                        return ToolResult.Error($"房间范围 ({startX}~{endX}, {startY}~{endY}) 超出地图边界 (0~{mapW - 1}, 0~{mapH - 1})。请选择更靠内的中心点或缩小房间。");

                    if (Faction.OfPlayer == null)
                        return ToolResult.Error("玩家派系不存在");

                    // 材料默认
                    var wallStuff = (wallDef.MadeFromStuff) ? ThingDef.Named("Steel") : null;
                    var doorStuff = (doorDef?.MadeFromStuff == true) ? ThingDef.Named("Steel") : null;

                    // 放置墙体（在门位置处替换为门）
                    foreach (var (wx, wy) in wallPositions)
                    {
                        var ipos = new IntVec3(wx, 0, wy);
                        if (doorPosSet.Contains((wx, wy)))
                        {
                            var bdef = (BuildableDef)doorDef!;
                            if (!GenConstruct.CanPlaceBlueprintAt(bdef, ipos, Rot4.North, map, false, null, null, doorStuff))
                            {
                                // 门放不了，退化为墙
                                if (GenConstruct.CanPlaceBlueprintAt((BuildableDef)wallDef, ipos, Rot4.North, map, false, null, null, wallStuff))
                                {
                                    GenSpawn.WipeExistingThings(ipos, Rot4.North, wallDef.blueprintDef, map, DestroyMode.Deconstruct);
                                    GenConstruct.PlaceBlueprintForBuild((BuildableDef)wallDef, ipos, map, Rot4.North, Faction.OfPlayer, wallStuff);
                                    placedWalls++;
                                }
                                continue;
                            }
                            try
                            {
                                GenSpawn.WipeExistingThings(ipos, Rot4.North, doorDef!.blueprintDef, map, DestroyMode.Deconstruct);
                                GenConstruct.PlaceBlueprintForBuild(bdef, ipos, map, Rot4.North, Faction.OfPlayer, doorStuff);
                                placedDoors++;
                            }
                            catch (Exception ex) { errors.Add($"门({wx},{wy}): {ex.Message}"); }
                        }
                        else
                        {
                            if (!GenConstruct.CanPlaceBlueprintAt((BuildableDef)wallDef, ipos, Rot4.North, map, false, null, null, wallStuff))
                                continue;
                            try
                            {
                                GenSpawn.WipeExistingThings(ipos, Rot4.North, wallDef.blueprintDef, map, DestroyMode.Deconstruct);
                                GenConstruct.PlaceBlueprintForBuild((BuildableDef)wallDef, ipos, map, Rot4.North, Faction.OfPlayer, wallStuff);
                                placedWalls++;
                            }
                            catch (Exception ex) { errors.Add($"墙({wx},{wy}): {ex.Message}"); }
                        }
                    }

                    var floorStuff = (floorDef?.MadeFromStuff == true) ? ThingDef.Named("Steel") : null;
                    // 放置地板
                    if (floorCount > 0 && floorDef != null)
                    {
                        foreach (var (fx, fy) in floorPositions)
                        {
                            var fpos = new IntVec3(fx, 0, fy);
                            if (!GenConstruct.CanPlaceBlueprintAt((BuildableDef)floorDef, fpos, Rot4.North, map, false, null, null, floorStuff))
                                continue;
                            try
                            {
                                GenSpawn.WipeExistingThings(fpos, Rot4.North, floorDef.blueprintDef, map, DestroyMode.Deconstruct);
                                GenConstruct.PlaceBlueprintForBuild((BuildableDef)floorDef, fpos, map, Rot4.North, Faction.OfPlayer, floorStuff);
                                placedFloors++;
                            }
                            catch (Exception ex) { errors.Add($"地板({fx},{fy}): {ex.Message}"); }
                        }
                    }

                    // 构建返回文本
                    var sb = new StringBuilder();
                    sb.AppendLine($"房间建造蓝图规划完成:");
                    sb.AppendLine($"- 范围: ({startX}, {startY}) ~ ({endX}, {endY})，中心 ({centerX}, {centerY})");
                    sb.AppendLine($"- 外墙: {placedWalls} 格 {wallDef.label} ({wallDefName})");
                    if (placedDoors > 0)
                        sb.AppendLine($"- 门: {placedDoors} 扇 {doorDef?.label ?? doorDefName}");
                    if (placedFloors > 0)
                        sb.AppendLine($"- 地板: {placedFloors} 格 {floorDef?.label ?? floorDefName}");
                    sb.AppendLine($"- 内部空间: {width}x{height} = {width * height} 格");
                    if (errors.Count > 0)
                        sb.AppendLine($"- 部分失败 ({errors.Count} 处): {string.Join("; ", errors)}");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"房间建造失败: {ex.Message}");
                }
            });
        }
    }
}
