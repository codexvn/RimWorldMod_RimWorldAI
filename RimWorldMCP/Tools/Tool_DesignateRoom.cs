using System;
using System.Collections.Generic;
using System.Text;
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
    public class Tool_DesignateRoom : ITool
    {
        public string Name => "designate_room";
        public string Description => "快速建造一个矩形房间。指定左上角和右下角坐标，在矩形边界放置墙体。已有墙体的格子会自动跳过（可共用墙），不会重复建造。⚠ 调用前应先使用 get_structure_layout 查看当前布局。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上角 X 坐标" },
                pos_y = new { type = "integer", description = "左上角 Y 坐标" },
                end_x = new { type = "integer", description = "右下角 X 坐标" },
                end_y = new { type = "integer", description = "右下角 Y 坐标" },
                wall_stuff = new { type = "string", description = "墙体材料 DefName（可选，默认 Steel），先用 search_thing_def(keyword=\"花岗岩\", flags=\"stuff\") 查可用材料" },
                door_positions = new { type = "string", description = "门的位置，多个用逗号分隔。可选: top, bottom, left, right, center_top, center_bottom, center_left, center_right" },
                door_defName = new { type = "string", description = "门的 DefName，默认 Door", @default = "Door" },
                floor_defName = new { type = "string", description = "地板 DefName，可选" },
                ignore_unreachable = new { type = "boolean", description = "跳过可达性检测（默认 false）" },
                ignore_overwrite = new { type = "boolean", description = "跳过内部人造墙体冲突检测（默认 false）。默认会检查房间内部是否有其他人造墙体（坐标交叉错误），检测到则拒绝建造。设为 true 可强制覆盖。天然岩壁始终忽略。" }
            },
            required = new[] { "pos_x", "pos_y", "end_x", "end_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jSx) || !jSx.TryGetInt32(out var rawStartX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jSz) || !jSz.TryGetInt32(out var rawStartZ))
                return ToolResult.Error("缺少必填参数: pos_y");
            if (!args.Value.TryGetProperty("end_x", out var jEx) || !jEx.TryGetInt32(out var rawEndX))
                return ToolResult.Error("缺少必填参数: end_x");
            if (!args.Value.TryGetProperty("end_y", out var jEz) || !jEz.TryGetInt32(out var rawEndZ))
                return ToolResult.Error("缺少必填参数: end_y");

            string wallStuffName = "";
            if (args.Value.TryGetProperty("wall_stuff", out var jWall)) wallStuffName = jWall.GetString() ?? "";

            string doors = "";
            if (args.Value.TryGetProperty("door_positions", out var jDoors)) doors = jDoors.GetString() ?? "";

            string doorDefName = "Door";
            if (args.Value.TryGetProperty("door_defName", out var jDoor)) doorDefName = jDoor.GetString() ?? "Door";

            string floorDefName = "";
            if (args.Value.TryGetProperty("floor_defName", out var jFloor)) floorDefName = jFloor.GetString() ?? "";

            bool ignore_unreachable = false;
            if (args.Value.TryGetProperty("ignore_unreachable", out var jIgnore) && jIgnore.ValueKind == JsonValueKind.True)
                ignore_unreachable = true;
            bool ignore_overwrite = false;
            if (args.Value.TryGetProperty("ignore_overwrite", out var jIgnoreOver) && jIgnoreOver.ValueKind == JsonValueKind.True)
                ignore_overwrite = true;

            // 计算房间几何（不涉及游戏状态，可在任意线程执行）
            int minX = Math.Min(rawStartX, rawEndX);
            int maxX = Math.Max(rawStartX, rawEndX);
            int minZ = Math.Min(rawStartZ, rawEndZ);
            int maxZ = Math.Max(rawStartZ, rawEndZ);

            int roomWidth = maxX - minX + 1;
            int roomHeight = maxZ - minZ + 1;

            // 计算墙体位置（矩形四条边）
            var wallPositions = new List<(int x, int y)>();
            for (int x = minX; x <= maxX; x++)
            {
                wallPositions.Add((x, minZ)); // 上边
                wallPositions.Add((x, maxZ)); // 下边
            }
            for (int z = minZ + 1; z < maxZ; z++)
            {
                wallPositions.Add((minX, z)); // 左边
                wallPositions.Add((maxX, z)); // 右边
            }

            // 门的居中位置取边界中点
            int midX = (minX + maxX) / 2;
            int midZ = (minZ + maxZ) / 2;

            // 解析门的位置
            var doorPosSet = new HashSet<(int x, int y)>();
            if (!string.IsNullOrEmpty(doors))
            {
                foreach (string posStr in doors.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = posStr.Trim();
                    (int x, int y)? doorPoint = trimmed switch
                    {
                        "top" => (midX, minZ),
                        "bottom" => (midX, maxZ),
                        "left" => (minX, midZ),
                        "right" => (maxX, midZ),
                        "center_top" => (midX, minZ),
                        "center_bottom" => (midX, maxZ),
                        "center_left" => (minX, midZ),
                        "center_right" => (maxX, midZ),
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
                for (int x = minX + 1; x < maxX; x++)
                {
                    for (int z = minZ + 1; z < maxZ; z++)
                    {
                        floorPositions.Add((x, z));
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

                    // 查找 Def — 墙体就是 Wall，材料通过 wall_stuff 指定
                    ThingDef wallDef = DefDatabase<ThingDef>.GetNamed("Wall", false);
                    if (wallDef == null)
                        return ToolResult.Error("找不到墙体定义 'Wall'。");

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

                    int placedWalls = 0, placedDoors = 0, placedFloors = 0, skippedSharedWalls = 0;
                    var errors = new List<string>();

                    // 地图边界检查
                    int mapW = map.Size.x, mapH = map.Size.z;
                    if (minX < 0 || minZ < 0 || maxX >= mapW || maxZ >= mapH)
                        return ToolResult.Error($"房间范围 ({minX}~{maxX}, {minZ}~{maxZ}) 超出地图边界 (0~{mapW - 1}, 0~{mapH - 1})。请调整坐标。");

                    // 检查房间整体区域内是否存在人造墙体冲突（默认启用，ignore_overwrite=true 跳过）
                    if (!ignore_overwrite)
                    {
                        var conflictWalls = new List<string>();
                        // 扫描整个矩形区域（包含边界和内部），找人造墙体
                        for (int x = minX; x <= maxX; x++)
                        {
                            for (int z = minZ; z <= maxZ; z++)
                            {
                                var cell = new IntVec3(x, 0, z);
                                Building edifice = cell.GetEdifice(map);
                                if (edifice == null) continue;
                                // 自然岩壁忽略（isNaturalRock=true 或可开采）
                                if (edifice.def.building?.isNaturalRock == true) continue;
                                if (edifice.def.mineable) continue;
                                // 人造墙体才纳入冲突检测
                                if (!edifice.def.IsWall) continue;

                                bool onPerimeter = (z == minZ || z == maxZ || x == minX || x == maxX);
                                if (onPerimeter) continue; // 边界上的墙可复用（共用墙），不算冲突

                                conflictWalls.Add($"({x},{z}) {edifice.def.label}");
                            }
                        }
                        if (conflictWalls.Count > 0)
                        {
                            var sb2 = new StringBuilder();
                            sb2.AppendLine($"⚠ 房间区域内发现 {conflictWalls.Count} 处人造墙体，坐标可能与已有建筑交叉：");
                            foreach (var w in conflictWalls.Take(8))
                                sb2.AppendLine($"  - {w}");
                            if (conflictWalls.Count > 8)
                                sb2.AppendLine($"  ... 及其他 {conflictWalls.Count - 8} 处");
                            sb2.Append("传 ignore_overwrite=true 可跳过检测强制建造。");
                            return ToolResult.Error(sb2.ToString().TrimEnd());
                        }
                    }

                    if (Faction.OfPlayer == null)
                        return ToolResult.Error("玩家派系不存在");

                    // 材料 — wall_stuff 指定，默认 Steel
                    ThingDef? wallStuff = null;
                    if (wallDef.MadeFromStuff)
                    {
                        if (!string.IsNullOrEmpty(wallStuffName))
                        {
                            wallStuff = DefDatabase<ThingDef>.GetNamed(wallStuffName, false);
                            if (wallStuff == null)
                                return ToolResult.Error($"找不到材料: {wallStuffName}。请用 list_building_materials 查看可用材料。");
                        }
                        else wallStuff = ThingDef.Named("Steel");
                    }
                    var doorStuff = (doorDef?.MadeFromStuff == true) ? ThingDef.Named("Steel") : null;
                    var floorStuff = (floorDef?.MadeFromStuff == true) ? ThingDef.Named("Steel") : null;

                    // 资源检查（聚合墙体 + 门 + 地板，仅警告不阻断）
                    string? resourceWarning = null;
                    {
                        var aggregate = new Dictionary<ThingDef, int>();
                        void AddToAggregate(BuildableDef bdef, ThingDef? stuff, int multiplier)
                        {
                            var perUnit = ResourceCheckHelper.CalculateCost(bdef, stuff);
                            if (perUnit.Count == 0) return;
                            foreach (var kv in perUnit)
                            {
                                if (aggregate.ContainsKey(kv.Key))
                                    aggregate[kv.Key] += kv.Value * multiplier;
                                else
                                    aggregate[kv.Key] = kv.Value * multiplier;
                            }
                        }
                        AddToAggregate(wallDef, wallStuff, wallCount);
                        if (doorDef != null && doorCount > 0)
                            AddToAggregate(doorDef, doorStuff, doorCount);
                        if (floorDef != null && floorCount > 0)
                            AddToAggregate(floorDef, floorStuff, floorCount);
                        if (aggregate.Count > 0)
                        {
                            var shortage = ResourceCheckHelper.CheckResources(map, aggregate);
                            if (shortage != null)
                                resourceWarning = $"⚠ 资源不足警告（蓝图已放置，但建造需要以下资源）:\n{shortage}";
                        }
                    }

                    if (wallDef.graphicData == null)
                        return ToolResult.Error($"Wall 缺少 graphicData 图形定义，无法创建墙体设计器。");

                    if (doorDef != null && doorDef.graphicData == null)
                        return ToolResult.Error($"{doorDefName} 缺少 graphicData 图形定义，无法创建门设计器。");

                    if (floorDef != null && floorDef.graphicData == null)
                        return ToolResult.Error($"{floorDefName} 缺少 graphicData 图形定义，无法创建地板设计器。");

                    // 复用游戏原生 Designator_Build
                    var wallDes = new Designator_Build(wallDef);
                    if (wallStuff != null) wallDes.SetStuffDef(wallStuff);

                    Designator_Build? doorDes = null;
                    if (doorDef != null)
                    {
                        doorDes = new Designator_Build(doorDef);
                        if (doorStuff != null) doorDes.SetStuffDef(doorStuff);
                    }

                    Designator_Build? floorDes = null;
                    if (floorDef != null)
                    {
                        floorDes = new Designator_Build(floorDef);
                        if (floorStuff != null) floorDes.SetStuffDef(floorStuff);
                    }

                    // 验证殖民者可达
                    if (!ignore_unreachable)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        bool anyReachable = wallPositions.Any(wp => colonists.Any(c => c.CanReach(new IntVec3(wp.x, 0, wp.y), PathEndMode.ClosestTouch, Danger.Deadly)));
                        if (!anyReachable)
                            return ToolResult.Error("殖民者无法到达房间建造位置，无法放置蓝图。请确保有路径连通或传 ignore_unreachable=true。");
                    }

                    // 放置墙体（已有墙体处跳过——共用墙，在门位置处替换为门）
                    foreach (var (wx, wy) in wallPositions)
                    {
                        var ipos = new IntVec3(wx, 0, wy);
                        if (ipos.Fogged(map)) { errors.Add($"雾({wx},{wy}): 迷雾中不可见"); continue; }
                        bool isDoorPos = doorPosSet.Contains((wx, wy)) && doorDes != null;

                        // 非门位置 + 已有墙体 → 共用墙，跳过
                        if (!isDoorPos && RoomGenUtility.IsWallAt(ipos, map))
                        {
                            skippedSharedWalls++;
                            continue;
                        }

                        if (isDoorPos)
                        {
                            if (!doorDes!.CanDesignateCell(ipos).Accepted)
                            {
                                // 门放不了，退化为墙
                                if (wallDes.CanDesignateCell(ipos).Accepted)
                                {
                                    wallDes.DesignateSingleCell(ipos);
                                    placedWalls++;
                                }
                                continue;
                            }
                            try
                            {
                                doorDes.DesignateSingleCell(ipos);
                                placedDoors++;
                            }
                            catch (Exception ex) { errors.Add($"门({wx},{wy}): {ex.Message}"); }
                        }
                        else
                        {
                            if (!wallDes.CanDesignateCell(ipos).Accepted)
                                continue;
                            try
                            {
                                wallDes.DesignateSingleCell(ipos);
                                placedWalls++;
                            }
                            catch (Exception ex) { errors.Add($"墙({wx},{wy}): {ex.Message}"); }
                        }
                    }

                    // 放置地板
                    if (floorDes != null)
                    {
                        foreach (var (fx, fy) in floorPositions)
                        {
                            var fpos = new IntVec3(fx, 0, fy);
                            if (fpos.Fogged(map)) { errors.Add($"雾({fx},{fy}): 迷雾中不可见"); continue; }
                            if (!floorDes.CanDesignateCell(fpos).Accepted)
                                continue;
                            try
                            {
                                floorDes.DesignateSingleCell(fpos);
                                placedFloors++;
                            }
                            catch (Exception ex) { errors.Add($"地板({fx},{fy}): {ex.Message}"); }
                        }
                    }

                    // 构建返回文本
                    int materialSaved = skippedSharedWalls * 20; // 每段墙约 20 钢铁/石块
                    var sb = new StringBuilder();
                    sb.AppendLine($"房间建造蓝图规划完成:");
                    sb.AppendLine($"- 范围: ({minX}, {minZ}) ~ ({maxX}, {maxZ})，共 {roomWidth}x{roomHeight} 格");
                    sb.AppendLine($"- 外墙: {placedWalls} 格 {wallDef.label}（材料: {wallStuff?.label}）");
                    if (skippedSharedWalls > 0)
                        sb.AppendLine($"- 共用墙: 跳过 {skippedSharedWalls} 格已有墙体，节约约 {materialSaved} 材料");
                    if (placedDoors > 0)
                        sb.AppendLine($"- 门: {placedDoors} 扇 {doorDef?.label ?? doorDefName}");
                    if (placedFloors > 0)
                        sb.AppendLine($"- 地板: {placedFloors} 格 {floorDef?.label ?? floorDefName}");
                    sb.AppendLine($"- 内部空间: {roomWidth - 2}x{roomHeight - 2} = {(roomWidth - 2) * (roomHeight - 2)} 格");
                    if (errors.Count > 0)
                        sb.AppendLine($"- 部分失败 ({errors.Count} 处): {string.Join("; ", errors)}");
                    if (resourceWarning != null)
                        sb.AppendLine($"\n{resourceWarning}");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"房间建造失败: {ex.Message}");
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
