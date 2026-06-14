using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_CreateStockpile : ITool, IRequiresAdvanceTick
    {
        public string Name => "create_stockpile";
        public string Description => "创建物品储藏区并配置筛选规则。支持预设数组合并。室外预设(dumping/corpse/default)无需房间。提供 end_x/end_y 可划定矩形范围。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左下 X 坐标" },
                pos_y = new { type = "integer", description = "左下 Y 坐标" },
                end_x = new { type = "integer", description = "右上 X 坐标（可选，与 end_y 配对划定矩形范围）" },
                end_y = new { type = "integer", description = "右上 Y 坐标（可选，与 end_x 配对划定矩形范围）" },
                preset = new
                {
                    description = "存储预设，支持字符串数组如 [\"default\",\"dumping\"] 合并筛选条件。单值 \"dumping\" 也可。可选: default, dumping, corpse",
                    @default = "dumping"
                },
                priority = new
                {
                    type = "string",
                    description = "存储优先级",
                    @enum = new[] { "low", "normal", "preferred", "important", "critical" },
                    @default = "normal"
                },
                skip_room_check = new { type = "boolean", description = "跳过房间校验（默认 false）" },
                ignore_unreachable = new { type = "boolean", description = "跳过可达性检测（默认 false）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        private static readonly Dictionary<string, StorageSettingsPreset> PresetNameMap = new()
        {
            { "default", StorageSettingsPreset.DefaultStockpile },
            { "dumping", StorageSettingsPreset.DumpingStockpile },
            { "corpse", StorageSettingsPreset.CorpseStockpile },
        };

        private static readonly Dictionary<string, StoragePriority> PriorityMap = new()
        {
            { "low", StoragePriority.Low },
            { "normal", StoragePriority.Normal },
            { "preferred", StoragePriority.Preferred },
            { "important", StoragePriority.Important },
            { "critical", StoragePriority.Critical },
        };

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            int endX = posX, endY = posY;
            bool isRange = args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out endX)
                        && args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out endY);

            // 解析 preset — 支持字符串数组或单个字符串
            var presetNames = new List<string>();
            if (args.Value.TryGetProperty("preset", out var jP))
            {
                if (jP.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in jP.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s)) presetNames.Add(s);
                    }
                }
                else
                {
                    var s = jP.GetString();
                    if (!string.IsNullOrEmpty(s)) presetNames.Add(s);
                }
            }
            if (presetNames.Count == 0) presetNames.Add("dumping");

            string priorityStr = "normal";
            if (args.Value.TryGetProperty("priority", out var jPr))
                priorityStr = jPr.GetString() ?? "normal";

            if (!PriorityMap.TryGetValue(priorityStr, out var storagePriority))
                return ToolResult.Error($"未知优先级: {priorityStr}。可选: low, normal, preferred, important, critical");

            bool skipRoomCheck = false;
            if (args.Value.TryGetProperty("skip_room_check", out var jSkipRoom) && jSkipRoom.ValueKind == JsonValueKind.True)
                skipRoomCheck = true;
            bool ignore_unreachable = false;
            if (args.Value.TryGetProperty("ignore_unreachable", out var jIgnore) && jIgnore.ValueKind == JsonValueKind.True)
                ignore_unreachable = true;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    int minX = Math.Min(posX, endX);
                    int maxX = Math.Max(posX, endX);
                    int minZ = Math.Min(posY, endY);
                    int maxZ = Math.Max(posY, endY);

                    CellRect area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    area.ClipInsideMap(map);

                    if (area.IsEmpty)
                        return ToolResult.Error($"指定范围 ({minX},{minZ})~({maxX},{maxZ}) 完全在地图外");

                    // 房间校验
                    if (!skipRoomCheck)
                    {
                        bool hasDefault = presetNames.Contains("default");
                        if (hasDefault)
                        {
                            if (!IsAreaInRoom(area, map))
                                return ToolResult.Error("存储区必须在室内！default 预设需要房间。请先建造房间，或传 skip_room_check=true 跳过此检查");
                        }
                    }

                    // 创建存储区：第一个预设初始化，后续预设直接追加分类
                    var firstPreset = PresetNameMap.TryGetValue(presetNames[0], out var p) ? p : StorageSettingsPreset.DefaultStockpile;
                    var zone = new Zone_Stockpile(firstPreset, map.zoneManager);

                    // 后续预设：直接用 filter.SetAllow 追加，不走 SetFromPreset（避免非纯追加行为）
                    for (int i = 1; i < presetNames.Count; i++)
                    {
                        switch (presetNames[i])
                        {
                            case "corpse":
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Corpses, true);
                                break;
                            case "dumping":
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Corpses, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Chunks, true);
                                if (ModsConfig.BiotechActive)
                                    zone.settings.filter.SetAllow(ThingDefOf.Wastepack, true);
                                break;
                            case "default":
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Foods, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Manufactured, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.ResourcesRaw, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Items, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Buildings, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Weapons, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.Apparel, true);
                                zone.settings.filter.SetAllow(ThingCategoryDefOf.BodyParts, true);
                                if (ModsConfig.BiotechActive)
                                    zone.settings.filter.SetAllow(ThingDefOf.Wastepack, false);
                                break;
                        }
                    }

                    zone.settings.Priority = storagePriority;
                    map.zoneManager.RegisterZone(zone);

                    int added = 0, skipped = 0;
                    foreach (IntVec3 cell in area)
                    {
                        if (cell.Fogged(map)) { skipped++; continue; }
                        if (zone.Cells.Contains(cell)) { skipped++; continue; }
                        if (map.zoneManager.ZoneAt(cell) != null) { skipped++; continue; }
                        var things = cell.GetThingList(map);
                        if (things.Any(t => !t.def.CanOverlapZones)) { skipped++; continue; }
                        zone.AddCell(cell);
                        added++;
                    }

                    if (zone.Cells.Count == 0)
                    {
                        map.zoneManager.DeregisterZone(zone);
                        return ToolResult.Error("指定区域的所有单元格已被其他存储区占用");
                    }

                    zone.CheckContiguous();

                    if (!ignore_unreachable)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        bool reachable = zone.Cells.Any(cell => colonists.Any(c => c.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)));
                        if (!reachable)
                        {
                            map.zoneManager.DeregisterZone(zone);
                            return ToolResult.Error("殖民者无法到达此存储区（被墙壁/障碍物完全阻隔），请确保有门连通或传 ignore_unreachable=true。");
                        }
                    }

                    var joinedPresets = string.Join("+", presetNames);
                    var sb = new StringBuilder();
                    sb.Append(isRange
                        ? $"已创建存储区 ({minX},{minZ})~({maxX},{maxZ})：{added} 格"
                        : $"已创建存储区 ({posX}, {posY})：{added} 格");
                    if (skipped > 0) sb.Append($"（跳过 {skipped} 格）");
                    sb.Append($" | 预设={joinedPresets}，优先级={priorityStr}");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"创建存储区失败: {ex.Message}"); }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var endX)
                && args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var endY))
                return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
            return (posX, posY, posX, posY);
        }

        public static bool IsAreaInRoom(CellRect area, Map map)
        {
            foreach (var cell in area.Cells)
            {
                var room = cell.GetRoom(map);
                if (room == null || room.TouchesMapEdge)
                    return false;
            }
            return true;
        }
    }
}
