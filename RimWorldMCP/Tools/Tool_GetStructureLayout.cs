using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_GetStructureLayout : ITool
    {
        public string Name => "get_structure_layout";
        public string Description => "获取地图建筑结构布局。无参数时输出全图。包含: 空间网格(墙/门/建筑), 房间列表(类型/面积/评分), 门列表, 墙段RLE。用于基地规划与建筑分析。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "查询区域左上角 X（可选，默认0）" },
                pos_y = new { type = "integer", description = "查询区域左上角 Y（可选，默认0）" },
                end_x = new { type = "integer", description = "查询区域右下角 X（可选，默认地图右边界）" },
                end_y = new { type = "integer", description = "查询区域右下角 Y（可选，默认地图下边界）" },
                include_natural_rock = new { type = "boolean", description = "是否包含天然岩壁（默认false，过滤山体/岩壁RLE减少噪音）" }
            }
        });

        private const int MaxGridWidth = 80;
        private const int MaxGridHeight = 60;
        private const int MaxRooms = 30;

        // 预定义建筑图标：defName 精确匹配
        private static readonly Dictionary<string, (char Symbol, string Label)> KnownBuildingIcons = new()
        {
            ["Sandbags"] = ('▦', "沙袋"),
            ["Barricade"] = ('▦', "路障"),
            ["SpikeTrap"] = ('▼', "陷阱"),
            ["TrapIED_HighExplosive"] = ('▼', "IED"),
            ["TrapIED_Incendiary"] = ('▼', "IED燃烧"),
            ["TrapIED_EMP"] = ('▼', "IEDEMP"),
            ["PowerConduit"] = ('┄', "电缆"),
            ["Battery"] = ('⚡', "电池"),
            ["WoodFiredGenerator"] = ('⚡', "发电机"),
            ["WindTurbine"] = ('⚡', "风电"),
            ["SolarGenerator"] = ('⚡', "太阳能"),
            ["GeothermalGenerator"] = ('⚡', "地热"),
            ["WatermillGenerator"] = ('⚡', "水电"),
            ["ChemfuelPoweredGenerator"] = ('⚡', "燃油发电"),
            ["PlantPot"] = (';', "花盆"),
            ["HydroponicsBasin"] = (';', "水培"),
            ["NutrientPasteDispenser"] = ('%', "营养机"),
            ["Hopper"] = ('○', "料斗"),
        };

        // 建筑类型匹配规则（按优先级，首次命中即返回）
        private static readonly List<(Func<Building, bool> Match, char Symbol, string Label)> BuildingTypeRules = new()
        {
            (b => b is Building_TurretGun,                                 '☈', "迫击炮"),
            (b => b is Building_Turret,                                    '☈', "炮塔"),
            (b => b is Building_WorkTable,                                 '⊞', "工作台"),
            (b => b is Building_ResearchBench,                             '⊞', "研究台"),
            (b => b is Building_Bed,                                       '◻', "床"),
            (b => b is Building_CommsConsole,                              '◆', "通讯台"),
            (b => b is Building_OrbitalTradeBeacon,                        '◆', "信标"),
            (b => b is Building_TempControl,                               '○', "温控"),
            (b => b is Building_Art,                                       '★', "雕塑"),
            (b => b is Building_SteamGeyser,                               '~', "喷泉"),
        };

        // 确定性兜底字符池（62 个，同 defName 始终分配同一字符，不同 defName 不重叠）
        private const string FallbackPool = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        // 固定图例条目（墙/门/空地/区域，始终显示）
        private static readonly Dictionary<char, string> FixedLegend = new()
        {
            ['#'] = "玩家墙",
            ['R'] = "自然岩壁",
            ['+'] = "门",
            ['='] = "种植区",
            ['S'] = "储存区",
            ['.'] = "空地",
            ['█'] = "迷雾"
        };

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    int mapW = map.Size.x;
                    int mapH = map.Size.z;

                    int minX = 0, minY = 0;
                    int maxX = mapW - 1, maxY = mapH - 1;

                    if (args != null)
                    {
                        if (args.Value.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out var px))
                            minX = px;
                        if (args.Value.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out var py))
                            minY = py;
                        if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex))
                            maxX = ex;
                        if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey))
                            maxY = ey;
                    }

                    if (minX > maxX) { int t = minX; minX = maxX; maxX = t; }
                    if (minY > maxY) { int t = minY; minY = maxY; maxY = t; }

                    bool includeNaturalRock = false;
                    if (args != null && args.Value.TryGetProperty("include_natural_rock", out var jNR))
                        includeNaturalRock = jNR.ValueKind == JsonValueKind.True;

                    minX = Math.Max(0, Math.Min(minX, mapW - 1));
                    maxX = Math.Max(0, Math.Min(maxX, mapW - 1));
                    minY = Math.Max(0, Math.Min(minY, mapH - 1));
                    maxY = Math.Max(0, Math.Min(maxY, mapH - 1));

                    int w = maxX - minX + 1;
                    int h = maxY - minY + 1;
                    bool showGrid = w <= MaxGridWidth && h <= MaxGridHeight;

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 建筑结构布局 ({minX},{minY}) ~ ({maxX},{maxY})  [{w}x{h}]");

                    if (!showGrid)
                        sb.AppendLine($"> 全图模式：字符网格已省略（范围 {w}x{h} 超过 {MaxGridWidth}x{MaxGridHeight} 上限），使用墙组件+房间+门+区域表达结构。");

                    sb.AppendLine();

                    // 1. 空间网格（仅小范围）
                    HashSet<char> usedSymbols = new();
                    Dictionary<char, string> legendEntries = new();
                    if (showGrid)
                    {
                        var gridResult = BuildGrid(sb, minX, minY, maxX, maxY, map, includeNaturalRock);
                        usedSymbols = gridResult.Used;
                        legendEntries = gridResult.Legend;
                        sb.AppendLine();
                    }

                    // 2. 房间
                    int roomCount = BuildRoomList(sb, minX, minY, maxX, maxY, map);
                    if (roomCount > 0) sb.AppendLine();

                    // 3. 门
                    int doorCount = BuildDoorList(sb, minX, minY, maxX, maxY, map);
                    if (doorCount > 0) sb.AppendLine();

                    // 4. 区域（种植区/储存区）
                    int zoneCount = BuildZoneList(sb, minX, minY, maxX, maxY, map);
                    if (zoneCount > 0) sb.AppendLine();

                    // 5. 墙（连通分量）
                    BuildWallComponents(sb, minX, minY, maxX, maxY, map, includeNaturalRock);

                    // 6. 图例（仅小范围模式）
                    if (showGrid)
                    {
                        sb.AppendLine();
                        BuildLegend(sb, usedSymbols, legendEntries);
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"结构布局生成失败: {ex.Message}");
                }
            });
        }

        private static bool IsWallCell(Building ed, out bool isNatural)
        {
            isNatural = false;
            if (ed?.def == null) return false;

            var def = ed.def;

            if (def.IsDoor) return false;

            if (def.building != null && def.building.isNaturalRock)
            {
                isNatural = true;
                return true;
            }

            if (def.Fillage == FillCategory.Full)
                return true;

            if (def.building != null && def.building.isWall)
                return true;

            if (def.passability == Traversability.Impassable)
                return true;

            return false;
        }

        private static char GetGridChar(IntVec3 c, Map map,
            Dictionary<string, char> fallbackMap, ref int fallbackIdx, Dictionary<char, string> legend,
            bool includeNaturalRock)
        {
            if (c.Fogged(map)) return '█';

            var ed = c.GetEdifice(map);
            if (ed == null)
            {
                var zone = map.zoneManager?.ZoneAt(c);
                if (zone is Zone_Growing) return '=';
                if (zone is Zone_Stockpile) return 'S';
                return '.';
            }

            if (ed is Building_Door) return '+';

            if (IsWallCell(ed, out bool isNatural))
            {
                if (isNatural && !includeNaturalRock) return '.';
                return isNatural ? 'R' : '#';
            }

            // 预定义 defName 精确匹配
            if (KnownBuildingIcons.TryGetValue(ed.def.defName, out var known))
            {
                legend[known.Symbol] = known.Label;
                return known.Symbol;
            }

            // 类型规则匹配
            foreach (var (match, symbol, label) in BuildingTypeRules)
            {
                if (match(ed))
                {
                    legend[symbol] = label;
                    return symbol;
                }
            }

            // 兜底：确定性分配（同 defName 始终同字符，不同 defName 不重叠）
            if (!fallbackMap.TryGetValue(ed.def.defName, out char fb))
            {
                fb = fallbackIdx < FallbackPool.Length ? FallbackPool[fallbackIdx++] : '?';
                fallbackMap[ed.def.defName] = fb;
            }
            legend[fb] = ed.def.defName;
            return fb;
        }

        private static (HashSet<char> Used, Dictionary<char, string> Legend) BuildGrid(
            StringBuilder sb, int minX, int minY, int maxX, int maxY, Map map, bool includeNaturalRock)
        {
            var used = new HashSet<char>();
            var legend = new Dictionary<char, string>();
            var fallbackMap = new Dictionary<string, char>();
            int fallbackIdx = 0;

            // 固定图例条目预填充（天然岩壁仅在开启时加入图例）
            foreach (var kv in FixedLegend)
            {
                if (kv.Key == 'R' && !includeNaturalRock) continue;
                legend[kv.Key] = kv.Value;
            }

            sb.AppendLine("### 空间网格");

            for (int z = minY; z <= maxY; z++)
            {
                sb.Append($"z{z}: ");
                for (int x = minX; x <= maxX; x++)
                {
                    char ch = GetGridChar(new IntVec3(x, 0, z), map, fallbackMap, ref fallbackIdx, legend,
                        includeNaturalRock);
                    sb.Append(ch);
                    used.Add(ch);
                }
                sb.AppendLine();
            }

            return (used, legend);
        }

        private static void BuildLegend(StringBuilder sb, HashSet<char> usedSymbols,
            Dictionary<char, string> legendEntries)
        {
            sb.Append("### 图例");
            sb.AppendLine();
            var parts = new List<string>();
            foreach (var ch in usedSymbols.OrderBy(c => c))
            {
                if (legendEntries.TryGetValue(ch, out var label))
                    parts.Add($"{ch}={label}");
            }
            if (parts.Count > 0)
                sb.AppendLine(string.Join("  ", parts));
        }

        private static int BuildRoomList(StringBuilder sb, int minX, int minY, int maxX, int maxY, Map map)
        {
            var regionGrid = map.regionGrid;
            if (regionGrid == null) return 0;

            var allRooms = regionGrid.AllRooms;
            if (allRooms == null || allRooms.Count == 0) return 0;

            var matchedRooms = new List<Room>();
            int outdoorCellCount = 0;

            foreach (var room in allRooms)
            {
                if (room == null || room.CellCount == 0) continue;

                // 室外房间先分流（免遍历巨量 Cells），合并统计
                if (room.TouchesMapEdge && room.UsesOutdoorTemperature)
                {
                    outdoorCellCount += room.CellCount;
                    continue;
                }

                // 仅对室内房间做精确范围交集检测
                if (!RoomIntersectsRange(room, minX, minY, maxX, maxY))
                    continue;

                // 跳过完全在迷雾中的房间（取首尾两格抽样判断）
                var roomCells = room.Cells;
                if (roomCells.Count() > 0)
                {
                    var first = roomCells.First();
                    if (first.Fogged(map)) continue;
                }

                matchedRooms.Add(room);
            }

            // 按面积降序排列，取前 MaxRooms
            matchedRooms.Sort((a, b) => b.CellCount.CompareTo(a.CellCount));
            int total = matchedRooms.Count;
            int shown = Math.Min(total, MaxRooms);
            matchedRooms = matchedRooms.Take(MaxRooms).ToList();

            sb.AppendLine($"### 房间 ({shown})");

            for (int i = 0; i < matchedRooms.Count; i++)
            {
                var room = matchedRooms[i];
                string role = room.Role?.label ?? "未分类";
                int area = room.CellCount;
                float impressiveness = room.GetStat(RoomStatDefOf.Impressiveness);
                int beds = room.ContainedBeds?.Count() ?? 0;
                string env = room.UsesOutdoorTemperature ? "室外" : "室内";

                // 取房间第一个Cell作为代表坐标
                var loc = room.Cells.FirstOrDefault();
                string coord = $"({loc.x},{loc.z})";

                sb.AppendLine($"[{i + 1}] {role}  面积={area}  印象={impressiveness:F1}  床位={beds}  {env}  {coord}");
            }

            // 室外区域
            if (outdoorCellCount > 0)
                sb.AppendLine($"[室外] 面积={outdoorCellCount}  室外区域");

            // 截断提示
            if (total > MaxRooms)
                sb.AppendLine($"> 另有 {total - MaxRooms} 个房间未列出（按面积截断）");

            return shown;
        }

        private static bool RoomIntersectsRange(Room room, int minX, int minY, int maxX, int maxY)
        {
            // 快速检测：用 ExtentsClose 边界框
            var ext = room.ExtentsClose;
            if (ext.maxX < minX || ext.minX > maxX || ext.maxZ < minY || ext.minZ > maxY)
                return false;

            // 精确检测：遍历 Cells（房间格数通常不大）
            foreach (var cell in room.Cells)
            {
                if (cell.x >= minX && cell.x <= maxX && cell.z >= minY && cell.z <= maxY)
                    return true;
            }

            return false;
        }

        private static int BuildDoorList(StringBuilder sb, int minX, int minY, int maxX, int maxY, Map map)
        {
            var doors = new List<Building_Door>();

            for (int z = minY; z <= maxY; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var door = new IntVec3(x, 0, z).GetDoor(map);
                    if (door != null && !doors.Contains(door))
                        doors.Add(door);
                }
            }

            if (doors.Count == 0) return 0;

            sb.AppendLine($"### 门 ({doors.Count})");

            for (int i = 0; i < doors.Count; i++)
            {
                var door = doors[i];
                float hpPct = (float)door.HitPoints / door.MaxHitPoints * 100f;
                string material = door.Stuff?.label ?? "默认材质";
                string status = door.Open ? "开启" : (door.HoldOpen ? "保持开启" : "关闭");
                sb.AppendLine($"[{i + 1}] ({door.Position.x},{door.Position.z}) {door.def.label}  {material}  {hpPct:F0}%  {status}");
            }

            return doors.Count;
        }

        private static int BuildZoneList(StringBuilder sb, int minX, int minY, int maxX, int maxY, Map map)
        {
            var zoneManager = map.zoneManager;
            if (zoneManager == null) return 0;

            var allZones = zoneManager.AllZones;
            if (allZones == null) return 0;

            var matched = new List<(Zone Zone, int MinX, int MinZ, int MaxX, int MaxZ, int Cells, int CellsInRange)>();
            foreach (var zone in allZones)
            {
                if (zone == null) continue;

                int zMinX = int.MaxValue, zMinZ = int.MaxValue, zMaxX = int.MinValue, zMaxZ = int.MinValue;
                int cellCount = 0, inRange = 0;
                bool intersects = false;

                foreach (var cell in zone.Cells)
                {
                    zMinX = Math.Min(zMinX, cell.x);
                    zMinZ = Math.Min(zMinZ, cell.z);
                    zMaxX = Math.Max(zMaxX, cell.x);
                    zMaxZ = Math.Max(zMaxZ, cell.z);
                    cellCount++;
                    if (cell.x >= minX && cell.x <= maxX && cell.z >= minY && cell.z <= maxY)
                    {
                        inRange++;
                        intersects = true;
                    }
                }

                if (!intersects || cellCount == 0) continue;
                matched.Add((zone, zMinX, zMinZ, zMaxX, zMaxZ, cellCount, inRange));
            }

            if (matched.Count == 0) return 0;

            sb.AppendLine($"### 区域 ({matched.Count})");

            for (int i = 0; i < matched.Count; i++)
            {
                var (zone, zMinX, zMinZ, zMaxX, zMaxZ, cellCount, inRange) = matched[i];
                string typeLabel = zone is Zone_Growing ? "种植区" : (zone is Zone_Stockpile ? "储存区" : "区域");
                string areaInfo = inRange == cellCount ? $"{cellCount}格" : $"{inRange}/{cellCount}格(在范围内)";

                sb.AppendLine($"[{i + 1}] {typeLabel} ({zMinX},{zMinZ})~({zMaxX},{zMaxZ})  {areaInfo}");
            }

            return matched.Count;
        }

        private struct WallComponent
        {
            public int Id;
            public int MinX, MinY, MaxX, MaxY;
            public int CellCount;
        }

        private static int BuildWallComponents(StringBuilder sb, int minX, int minY, int maxX, int maxY, Map map,
            bool includeNaturalRock)
        {
            var visited = new HashSet<IntVec3>();
            var components = new List<WallComponent>();
            int globalId = 0;

            for (int z = minY; z <= maxY; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var pos = new IntVec3(x, 0, z);
                    if (visited.Contains(pos)) continue;

                    var ed = pos.GetEdifice(map);
                    if (ed == null || !IsWallCell(ed, out bool isNatural)) continue;

                    if (isNatural && !includeNaturalRock) continue;

                    // BFS 连通分量
                    var queue = new Queue<IntVec3>();
                    queue.Enqueue(pos);
                    visited.Add(pos);
                    var comp = new WallComponent
                    {
                        Id = ++globalId,
                        MinX = x, MaxX = x,
                        MinY = z, MaxY = z,
                        CellCount = 0
                    };

                    while (queue.Count > 0)
                    {
                        var cur = queue.Dequeue();
                        comp.MinX = Math.Min(comp.MinX, cur.x);
                        comp.MaxX = Math.Max(comp.MaxX, cur.x);
                        comp.MinY = Math.Min(comp.MinY, cur.z);
                        comp.MaxY = Math.Max(comp.MaxY, cur.z);
                        comp.CellCount++;

                        foreach (var dir in GenAdj.CardinalDirections)
                        {
                            var nb = cur + dir;
                            if (visited.Contains(nb)) continue;
                            if (nb.x < minX || nb.x > maxX || nb.z < minY || nb.z > maxY) continue;
                            var nbEd = nb.GetEdifice(map);
                            if (nbEd == null || !IsWallCell(nbEd, out bool nbNatural)) continue;
                            if (nbNatural != isNatural) continue;
                            visited.Add(nb);
                            queue.Enqueue(nb);
                        }
                    }

                    components.Add(comp);
                }
            }

            // 按面积降序输出
            components.Sort((a, b) => b.CellCount.CompareTo(a.CellCount));

            sb.AppendLine($"### 墙 ({components.Count})");
            if (components.Count == 0) return 0;

            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                int w = c.MaxX - c.MinX + 1;
                int h = c.MaxY - c.MinY + 1;
                sb.Append($"[{i + 1}] ({c.MinX},{c.MinY})~({c.MaxX},{c.MaxY})  {c.CellCount}格");

                if (w > 1 && h > 1)
                    sb.Append($"  {w}x{h}区域");
                else if (w > 1)
                    sb.Append($"  水平");
                else if (h > 1)
                    sb.Append($"  垂直");

                sb.AppendLine();
            }

            return components.Count;
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
