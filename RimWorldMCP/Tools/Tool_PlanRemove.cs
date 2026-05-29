using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_PlanRemove : ITool
    {
        public string Name => "plan_remove";
        public string Description => "删除地图上的规划标记。支持矩形范围删除（给 end_x/end_y）和单格删除。可按标签和颜色过滤只删除特定规划。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "起始 X 坐标（左上角）" },
                pos_y = new { type = "integer", description = "起始 Y 坐标（Z轴，左上角）" },
                end_x = new { type = "integer", description = "结束 X 坐标（可选，不提供则单格）" },
                end_y = new { type = "integer", description = "结束 Y 坐标（可选，不提供则单格）" },
                color = new { type = "string", description = "只删除指定颜色的规划（可选）", @enum = new[] { "白","红","绿","蓝","黄","紫","青","橙","灰","棕" } },
                label = new { type = "string", description = "只删除指定标签的规划（可选）" },
            },
            required = new[] { "pos_x", "pos_y" }
        });

        private static readonly Dictionary<string, string> s_colorMap = new()
        {
            { "白", "PlanWhite" }, { "红", "PlanRed" }, { "绿", "PlanGreen" },
            { "蓝", "PlanBlue" }, { "黄", "PlanYellow" }, { "紫", "PlanPurple" },
            { "青", "PlanCyan" }, { "橙", "PlanOrange" }, { "灰", "PlanGray" },
            { "棕", "PlanBrown" },
        };

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEndX))
                jEndX.TryGetInt32(out endX);
            if (args.Value.TryGetProperty("end_y", out var jEndY))
                jEndY.TryGetInt32(out endY);

            int minX = Math.Min(posX, endX);
            int maxX = Math.Max(posX, endX);
            int minZ = Math.Min(posY, endY);
            int maxZ = Math.Max(posY, endY);

            string? colorName = null;
            if (args.Value.TryGetProperty("color", out var jColor))
                colorName = jColor.GetString();

            string? labelFilter = null;
            if (args.Value.TryGetProperty("label", out var jLabel))
                labelFilter = (jLabel.GetString() ?? "").Trim();

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前无地图");

                // Resolve ColorDef filter
                ColorDef? colorDef = null;
                if (!string.IsNullOrEmpty(colorName))
                {
                    var targetDefName = s_colorMap.TryGetValue(colorName!, out var mapped)
                        ? mapped : colorName!;
                    colorDef = Designator_Plan_Add.Colors
                        .FirstOrDefault(c =>
                            string.Equals(c.defName, targetDefName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.defName, "Plan" + targetDefName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.LabelCap, colorName, StringComparison.OrdinalIgnoreCase));
                }

                var planManager = map.planManager;
                var affectedPlans = new HashSet<Plan>();
                int removed = 0;

                for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    var pos = new IntVec3(x, 0, z);
                    if (!pos.InBounds(map)) continue;

                    var plan = planManager.PlanAt(pos);
                    if (plan == null) continue;

                    // Apply filters
                    if (colorDef != null && plan.Color != colorDef) continue;
                    if (!string.IsNullOrEmpty(labelFilter) &&
                        !string.Equals(plan.RenamableLabel, labelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    plan.RemoveCell(pos);
                    removed++;
                    affectedPlans.Add(plan);
                }

                if (removed == 0)
                    return ToolResult.Success("范围内未找到匹配的规划标记");

                // Re-check contiguity for affected plans that still have cells
                foreach (var p in affectedPlans)
                {
                    if (p.CellCount > 0)
                        p.CheckContiguous();
                }

                var sb = new StringBuilder();
                sb.Append($"已删除规划标记: 移除{removed}格");
                sb.Append($" 范围({minX},{minZ})~({maxX},{maxZ})");
                if (!string.IsNullOrEmpty(labelFilter))
                    sb.Append($"，标签={labelFilter}");
                if (colorDef != null)
                    sb.Append($"，颜色={colorName}");
                return ToolResult.Success(sb.ToString());
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return null;

            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEndX))
                jEndX.TryGetInt32(out endX);
            if (args.Value.TryGetProperty("end_y", out var jEndY))
                jEndY.TryGetInt32(out endY);

            return (
                Math.Min(posX, endX), Math.Min(posY, endY),
                Math.Max(posX, endX), Math.Max(posY, endY)
            );
        }
    }
}
