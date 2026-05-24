using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ListRecipes : ITool
    {
        public string Name => "list_recipes";
        public string Description => "列出当前可用的制造配方（已研究解锁的）。可按工作台类型和关键词过滤。返回配方的 defName、产物名称、所需材料、技能要求和工作量。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                search = new { type = "string", description = "搜索关键词，模糊匹配配方名称或产物名称" },
                workbench_type = new { type = "string", description = "工作台类型 defName 过滤" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var search = "";
            var workbenchFilter = "";
            if (args != null)
            {
                if (args.Value.TryGetProperty("search", out var s)) search = s.GetString() ?? "";
                if (args.Value.TryGetProperty("workbench_type", out var w)) workbenchFilter = w.GetString() ?? "";
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var allRecipes = DefDatabase<RecipeDef>.AllDefs;
                var filtered = allRecipes.AsEnumerable();

                // 只显示当前可用的配方（研究解锁 + 意识形态）
                filtered = filtered.Where(r => r.AvailableNow);

                // 排除手术配方（可选：由前端决定，默认全部列出）
                // 按工作台类型过滤
                if (!string.IsNullOrEmpty(workbenchFilter))
                {
                    filtered = filtered.Where(r =>
                        r.recipeUsers != null &&
                        r.recipeUsers.Any(u => u.defName != null &&
                            u.defName.IndexOf(workbenchFilter, StringComparison.OrdinalIgnoreCase) >= 0));
                }

                // 按关键词搜索
                if (!string.IsNullOrEmpty(search))
                {
                    filtered = filtered.Where(r =>
                        (r.label != null && r.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (r.defName != null && r.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (r.ProducedThingDef?.label != null && r.ProducedThingDef.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
                }

                var list = filtered.ToList();
                if (list.Count == 0)
                    return ToolResult.Success("没有匹配的配方。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 可用配方 ({list.Count} 个)");
                sb.AppendLine();
                sb.AppendLine("| 配方 | defName | 产物 | 工作台 | 技能要求 | 工作量 | 手术");
                sb.AppendLine("|------|---------|------|--------|----------|--------|-----|");

                foreach (var recipe in list)
                {
                    var label = recipe.label ?? recipe.defName ?? "???";
                    var defName = recipe.defName ?? "???";
                    var producedThing = recipe.ProducedThingDef?.label ?? "-";
                    var isSurgery = recipe.IsSurgery ? "是" : "否";

                    // 工作台
                    var workbenches = "-";
                    if (recipe.recipeUsers != null && recipe.recipeUsers.Count > 0)
                        workbenches = string.Join(", ", recipe.recipeUsers.Select(u => u.label ?? u.defName ?? "???"));

                    // 技能要求
                    var skillReq = "-";
                    if (recipe.skillRequirements != null && recipe.skillRequirements.Count > 0)
                    {
                        var skillParts = new List<string>();
                        foreach (var sr in recipe.skillRequirements)
                        {
                            var skillLabel = sr.skill?.label ?? "???";
                            skillParts.Add($"{skillLabel} {sr.minLevel}+");
                        }
                        skillReq = string.Join(", ", skillParts);
                    }

                    // 材料
                    var materials = "-";
                    if (recipe.ingredients != null && recipe.ingredients.Count > 0)
                    {
                        var matParts = new List<string>();
                        foreach (var ing in recipe.ingredients)
                        {
                            var count = ing.GetBaseCount();
                            var filterSummary = ing.filter?.Summary ?? "???";
                            matParts.Add($"{filterSummary} x{count}");
                        }
                        materials = string.Join(", ", matParts);
                    }

                    var workAmount = recipe.workAmount;

                    sb.AppendLine($"| {label} | `{defName}` | {producedThing} | {workbenches} | {skillReq} | {workAmount} | {isSurgery} |");
                }

                sb.AppendLine();
                sb.AppendLine($"**统计**: 共 {list.Count} 个配方匹配当前过滤条件。");

                return ToolResult.Success(sb.ToString());
            });
        }
    }
}
