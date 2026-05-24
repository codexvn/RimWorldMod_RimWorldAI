using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_CreateStockpile : ITool
    {
        public string Name => "create_stockpile";
        public string Description => "创建物品储藏区并配置筛选规则。支持预设类型（食物/原料/武器等）和优先级。提供 end_x/end_y 可划定矩形范围。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上 X 坐标" },
                pos_y = new { type = "integer", description = "左上 Y 坐标" },
                end_x = new { type = "integer", description = "右下 X 坐标（可选，与 end_y 配对划定矩形范围）" },
                end_y = new { type = "integer", description = "右下 Y 坐标（可选，与 end_x 配对划定矩形范围）" },
                preset = new
                {
                    type = "string",
                    description = "存储预设类型",
                    @enum = new[] { "default", "dumping", "corpse", "food", "raw_resources", "manufactured", "weapons", "apparel", "chunks" },
                    @default = "default"
                },
                priority = new
                {
                    type = "string",
                    description = "存储优先级",
                    @enum = new[] { "low", "normal", "preferred", "important", "critical" },
                    @default = "normal"
                }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        private static readonly Dictionary<string, StorageSettingsPreset> PresetMap = new()
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

            string presetStr = "default";
            if (args.Value.TryGetProperty("preset", out var jP))
                presetStr = jP.GetString() ?? "default";

            string priorityStr = "normal";
            if (args.Value.TryGetProperty("priority", out var jPr))
                priorityStr = jPr.GetString() ?? "normal";

            if (!PriorityMap.TryGetValue(priorityStr, out var storagePriority))
                return ToolResult.Error($"未知优先级: {priorityStr}。可选: low, normal, preferred, important, critical");

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

                    // 创建存储区
                    Zone_Stockpile zone;
                    if (PresetMap.TryGetValue(presetStr, out var stockPreset))
                    {
                        zone = new Zone_Stockpile(stockPreset, map.zoneManager);
                    }
                    else
                    {
                        zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                        ConfigureCustomPreset(zone, presetStr);
                    }

                    zone.settings.Priority = storagePriority;
                    map.zoneManager.RegisterZone(zone);

                    int added = 0, skipped = 0;
                    foreach (IntVec3 cell in area)
                    {
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

                    var sb = new StringBuilder();
                    sb.Append(isRange
                        ? $"已创建存储区 ({minX},{minZ})~({maxX},{maxZ})：{added} 格"
                        : $"已创建存储区 ({posX}, {posY})：{added} 格");
                    if (skipped > 0) sb.Append($"（跳过 {skipped} 格）");
                    sb.Append($" | 预设={presetStr}，优先级={priorityStr}");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"创建存储区失败: {ex.Message}"); }
            });
        }

        private static void ConfigureCustomPreset(Zone_Stockpile zone, string preset)
        {
            var filter = zone.settings.filter;
            filter.SetAllowAll(null, false);

            switch (preset)
            {
                case "food":
                    filter.SetAllow(ThingCategoryDefOf.Foods, true);
                    filter.SetAllow(ThingCategoryDefOf.PlantFoodRaw, true);
                    break;
                case "raw_resources":
                    filter.SetAllow(ThingCategoryDefOf.ResourcesRaw, true);
                    filter.SetAllow(ThingCategoryDefOf.Chunks, true);
                    break;
                case "manufactured":
                    filter.SetAllow(ThingCategoryDefOf.Manufactured, true);
                    break;
                case "weapons":
                    filter.SetAllow(ThingCategoryDefOf.Weapons, true);
                    break;
                case "apparel":
                    filter.SetAllow(ThingCategoryDefOf.Apparel, true);
                    break;
                case "chunks":
                    filter.SetAllow(ThingCategoryDefOf.Chunks, true);
                    filter.SetAllow(ThingCategoryDefOf.StoneBlocks, true);
                    break;
            }
        }
    }
}
