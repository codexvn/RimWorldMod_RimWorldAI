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
    public class Tool_ExpandZone : ITool
    {
        public string Name => "expand_zone";
        public string Description => "扩展现有区域（储存区/种植区）。在 zone_pos 处定位区域，将矩形范围 (pos_x,pos_y)→(end_x,end_y) 内有效格子加入。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                zone_pos_x = new { type = "integer", description = "区域内任意一格的 X 坐标（用于定位目标区域）" },
                zone_pos_y = new { type = "integer", description = "区域内任意一格的 Y 坐标（用于定位目标区域）" },
                pos_x = new { type = "integer", description = "扩展范围左上角 X" },
                pos_y = new { type = "integer", description = "扩展范围左上角 Y" },
                end_x = new { type = "integer", description = "扩展范围右下角 X（不传则仅扩展单个格子）" },
                end_y = new { type = "integer", description = "扩展范围右下角 Y（不传则仅扩展单个格子）" }
            },
            required = new[] { "zone_pos_x", "zone_pos_y", "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            if (!args.Value.TryGetProperty("zone_pos_x", out var jzx) || !jzx.TryGetInt32(out var zoneX))
                return ToolResult.Error("缺少 zone_pos_x");
            if (!args.Value.TryGetProperty("zone_pos_y", out var jzy) || !jzy.TryGetInt32(out var zoneY))
                return ToolResult.Error("缺少 zone_pos_y");
            if (!args.Value.TryGetProperty("pos_x", out var jpx) || !jpx.TryGetInt32(out var startX))
                return ToolResult.Error("缺少 pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jpy) || !jpy.TryGetInt32(out var startY))
                return ToolResult.Error("缺少 pos_y");

            int endX = startX, endY = startY;
            if (args.Value.TryGetProperty("end_x", out var jex) && jex.TryGetInt32(out var ex))
                endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jey) && jey.TryGetInt32(out var ey))
                endY = ey;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前没有可用地图。");

                // 定位目标区域
                var zoneCell = new IntVec3(zoneX, 0, zoneY);
                var zone = map.zoneManager?.ZoneAt(zoneCell);
                if (zone == null)
                    return ToolResult.Error($"({zoneX},{zoneY}) 处没有区域。请使用 get_structure_layout 确认区域位置。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 扩展区域: {zone.label ?? zone.GetType().Name}");
                sb.AppendLine($"- 类型: {zone.GetType().Name.Replace("Zone_", "").Replace("_", " ")}");
                sb.AppendLine($"- 原大小: {zone.Cells.Count()} 格");

                // 收集扩展范围内的有效格子
                int minX = Math.Min(startX, endX);
                int maxX = Math.Max(startX, endX);
                int minZ = Math.Min(startY, endY);
                int maxZ = Math.Max(startY, endY);

                int added = 0, skipped = 0;
                var errors = new List<string>();

                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var cell = new IntVec3(x, 0, z);

                        // 边界检查
                        if (!cell.InBounds(map))
                        {
                            skipped++;
                            continue;
                        }

                        // 已在区域内
                        if (zone.Cells.Contains(cell))
                        {
                            skipped++;
                            continue;
                        }

                        // 已有其他区域
                        if (map.zoneManager?.ZoneAt(cell) != null)
                        {
                            skipped++;
                            continue;
                        }

                        // 种植区特殊检查：不能有屋顶
                        if (zone is Zone_Growing && cell.Roofed(map))
                        {
                            skipped++;
                            if (errors.Count < 3)
                                errors.Add($"({x},{z}) 有屋顶，不能加入种植区");
                            continue;
                        }

                        try
                        {
                            zone.AddCell(cell);
                            added++;
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            if (errors.Count < 3)
                                errors.Add($"({x},{z}) {ex.Message}");
                        }
                    }
                }

                sb.AppendLine($"- 新增: {added} 格 | 跳过: {skipped} 格");
                if (added > 0)
                    sb.AppendLine($"- 新大小: {zone.Cells.Count()} 格");

                if (errors.Count > 0)
                {
                    sb.AppendLine();
                    foreach (var e in errors)
                        sb.AppendLine($"- ⚠ {e}");
                }

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("zone_pos_x", out var jzx) || !jzx.TryGetInt32(out var zoneX)) return null;
            if (!args.Value.TryGetProperty("zone_pos_y", out var jzy) || !jzy.TryGetInt32(out var zoneY)) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jpx) || !jpx.TryGetInt32(out var startX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jpy) || !jpy.TryGetInt32(out var startY)) return null;
            int endX = startX, endY = startY;
            if (args.Value.TryGetProperty("end_x", out var jex) && jex.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jey) && jey.TryGetInt32(out var ey)) endY = ey;
            int minX = Math.Min(zoneX, Math.Min(startX, endX));
            int maxX = Math.Max(zoneX, Math.Max(startX, endX));
            int minZ = Math.Min(zoneY, Math.Min(startY, endY));
            int maxZ = Math.Max(zoneY, Math.Max(startY, endY));
            return (minX, minZ, maxX, maxZ);
        }
    }
}
