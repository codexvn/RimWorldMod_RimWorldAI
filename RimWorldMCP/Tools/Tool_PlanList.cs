using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_PlanList : ITool
    {
        public string Name => "plan_list";
        public string Description => "列出地图上所有规划标记（色块区）。返回 ID、标签、颜色、大小和范围。注意：这只是草图区域，不含具体建筑蓝图；未完成建造请用 list_construction，已建成请用 get_structure_layout/list_devices。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new System.Collections.Generic.Dictionary<string, object>(),
            required = new string[] { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前无地图");

                var plans = map.planManager.AllPlans
                    .Where(p => !(p.RenamableLabel?.StartsWith("[AI]") ?? false))
                    .ToList();
                if (plans.Count == 0)
                    return ToolResult.Success("## 规划\n当前无规划标记。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 规划 ({plans.Count})");
                sb.AppendLine("| ID | 标签 | 颜色 | 格数 | 范围 |");
                sb.AppendLine("|----|------|------|------|------|");

                for (int i = 0; i < plans.Count; i++)
                {
                    var plan = plans[i];

                    // Compute bounding box by iterating cells (avoids Cells getter shuffle)
                    int minX = int.MaxValue, maxX = int.MinValue;
                    int minZ = int.MaxValue, maxZ = int.MinValue;
                    foreach (var c in plan)
                    {
                        if (c.x < minX) minX = c.x;
                        if (c.x > maxX) maxX = c.x;
                        if (c.z < minZ) minZ = c.z;
                        if (c.z > maxZ) maxZ = c.z;
                    }

                    // Resolve Chinese color name
                    var colorName = ResolveColorName(plan.Color);
                    var label = plan.RenamableLabel;
                    var range = (minX == maxX && minZ == maxZ)
                        ? $"({minX},{minZ})"
                        : $"({minX},{minZ})~({maxX},{maxZ})";

                    sb.AppendLine($"| {i} | {label} | {colorName} | {plan.CellCount} | {range} |");
                }

                return ToolResult.Success(sb.ToString());
            });
        }

        private static string ResolveColorName(ColorDef colorDef)
        {
            var name = colorDef.defName;
            if (name.StartsWith("Plan")) name = name.Substring(4);
            return name switch
            {
                "White" => "白", "Red" => "红", "Green" => "绿",
                "Blue" => "蓝", "Yellow" => "黄", "Purple" => "紫",
                "Cyan" => "青", "Orange" => "橙", "Gray" => "灰",
                "Brown" => "棕",
                _ => colorDef.LabelCap,
            };
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
