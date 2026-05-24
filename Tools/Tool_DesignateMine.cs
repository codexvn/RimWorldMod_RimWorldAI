using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateMine : ITool
    {
        public string Name => "designate_mine";
        public string Description => "标记指定区域的岩石/矿物以供开采。提供 end_x/end_y 可划定矩形范围，不提供则仅标记单格。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "起点 X 坐标（水平）" },
                pos_y = new { type = "integer", description = "起点 Y 坐标（垂直）" },
                end_x = new { type = "integer", description = "终点 X 坐标（可选，与 end_y 配对划定矩形范围）" },
                end_y = new { type = "integer", description = "终点 Y 坐标（可选，与 end_x 配对划定矩形范围）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

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

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    int minX = Math.Min(posX, endX);
                    int maxX = Math.Max(posX, endX);
                    int minZ = Math.Min(posY, endY);
                    int maxZ = Math.Max(posY, endY);

                    CellRect area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    area.ClipInsideMap(map);

                    if (area.IsEmpty)
                        return ToolResult.Error($"指定范围 ({minX},{minZ})~({maxX},{maxZ}) 完全在地图外。");

                    int designated = 0, skipped = 0, fogged = 0, noMineable = 0;
                    var productSummary = new System.Collections.Generic.Dictionary<string, int>();

                    foreach (IntVec3 cell in area)
                    {
                        if (cell.Fogged(map)) { fogged++; continue; }
                        if (map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null)
                        { skipped++; continue; }

                        Mineable mineable = cell.GetFirstMineable(map);
                        if (mineable == null) { noMineable++; continue; }

                        map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.Mine, null));
                        map.designationManager.TryRemoveDesignation(cell, DesignationDefOf.SmoothWall);
                        if (DesignationDefOf.MineVein != null)
                            map.designationManager.TryRemoveDesignation(cell, DesignationDefOf.MineVein);

                        string? product = mineable.def.building?.mineableThing?.label;
                        if (!string.IsNullOrEmpty(product))
                        {
                            productSummary.TryGetValue(product!, out int cnt);
                            productSummary[product!] = cnt + 1;
                        }
                        designated++;
                    }

                    var sb = new StringBuilder();
                    sb.Append(isRange
                        ? $"已标记采矿范围 ({minX},{minZ})~({maxX},{maxZ})：{designated} 格"
                        : $"已标记采矿坐标 ({posX}, {posY})：{designated} 格");
                    if (designated > 0 && productSummary.Count > 0)
                    {
                        sb.Append("，预期产出: ");
                        bool first = true;
                        foreach (var kv in productSummary)
                        {
                            if (!first) sb.Append(", ");
                            sb.Append($"{kv.Value}x {kv.Key}");
                            first = false;
                        }
                    }
                    sb.Append($"。（跳过: 迷雾 {fogged}, 无矿物 {noMineable}, 已有标记 {skipped}）");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"标记采矿失败: {ex.Message}"); }
            });
        }
    }
}
