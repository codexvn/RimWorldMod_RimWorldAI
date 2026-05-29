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
    public class Tool_PlanAdd : ITool
    {
        public string Name => "plan_add";
        public string Description => "在地图上添加规划标记（彩色半透明方块）。用于建造前画草图查看布局，不生成实际建造蓝图。支持矩形区域（给 end_x/end_y）和单格（不给 end_x/end_y）。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "起始 X 坐标（左上角）" },
                pos_y = new { type = "integer", description = "起始 Y 坐标（Z轴，左上角）" },
                end_x = new { type = "integer", description = "结束 X 坐标（可选，不提供则单格）" },
                end_y = new { type = "integer", description = "结束 Y 坐标（可选，不提供则单格）" },
                color = new { type = "string", description = "规划颜色", @enum = new[] { "白","红","绿","蓝","黄","紫","青","橙","灰","棕" } },
                label = new { type = "string", description = "规划标签（可选，如：厨房区）" },
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

            // Normalize to min/max
            int minX = Math.Min(posX, endX);
            int maxX = Math.Max(posX, endX);
            int minZ = Math.Min(posY, endY);
            int maxZ = Math.Max(posY, endY);

            string colorName = "白";
            if (args.Value.TryGetProperty("color", out var jColor))
                colorName = jColor.GetString() ?? "白";

            string label = "";
            if (args.Value.TryGetProperty("label", out var jLabel))
                label = (jLabel.GetString() ?? "").Trim();

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前无地图");

                // Resolve ColorDef
                var targetDefName = s_colorMap.TryGetValue(colorName, out var mapped)
                    ? mapped : colorName;

                var colorDef = Designator_Plan_Add.Colors
                    .FirstOrDefault(c =>
                        string.Equals(c.defName, targetDefName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.defName, "Plan" + targetDefName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.LabelCap, colorName, StringComparison.OrdinalIgnoreCase));

                if (colorDef == null)
                {
                    var available = string.Join(", ", Designator_Plan_Add.Colors.Select(c => c.defName));
                    return ToolResult.Error($"不支持的颜色 '{colorName}'。可用颜色: {available}");
                }

                // Find or create plan
                var planManager = map.planManager;
                Plan? plan = null;
                if (!string.IsNullOrEmpty(label))
                {
                    plan = planManager.AllPlans
                        .FirstOrDefault(p => p.RenamableLabel == label && p.Color == colorDef);
                }
                if (plan == null)
                {
                    plan = new Plan(colorDef, planManager);
                    if (!string.IsNullOrEmpty(label))
                        plan.RenamableLabel = label;
                }

                int added = 0, skipped = 0;
                var cells = new List<IntVec3>();
                for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    var pos = new IntVec3(x, 0, z);
                    if (!pos.InBounds(map))
                    {
                        skipped++;
                        continue;
                    }
                    var existing = planManager.PlanAt(pos);
                    if (existing == plan)
                    {
                        skipped++; // Already in this plan
                        continue;
                    }
                    if (existing != null && existing != plan)
                    {
                        skipped++; // Occupied by another plan
                        continue;
                    }
                    cells.Add(pos);
                }

                foreach (var c in cells)
                {
                    plan.AddCell(c);
                    added++;
                }

                if (cells.Count > 0)
                    plan.CheckContiguous();

                var labelStr = string.IsNullOrEmpty(label) ? plan.RenamableLabel : label;
                var sb = new StringBuilder();
                sb.Append($"已添加规划 '{labelStr}'({colorName})");
                sb.Append($" 范围({minX},{minZ})~({maxX},{maxZ})");
                if (added > 0) sb.Append($"，新增{added}格");
                if (skipped > 0) sb.Append($"，跳过{skipped}格（已存在/边界外）");
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
