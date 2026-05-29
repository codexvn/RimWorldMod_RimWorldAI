using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ListRecipes : ITool
    {
        public string Name => "list_recipes";
        public string Description => "列出当前可用的制造配方（已研究解锁的）。搜索用正则，如 .*仿生.*（含仿生）、^Install（安装类）、.*（全部）。可筛选仅手术配方。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                search = new { type = "string", description = "正则搜索，匹配配方名/defName/产物名。.* 匹配全部" },
                workbench_type = new { type = "string", description = "工作台类型 defName 过滤" },
                surgery_only = new { type = "boolean", description = "仅返回手术配方", @default = false },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认10，最大50", @default = 10 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var search = "";
            var workbenchFilter = "";
            var surgeryOnly = false;
            int page = 1, pageSize = 10;
            if (args != null)
            {
                if (args.Value.TryGetProperty("search", out var s)) search = s.GetString() ?? "";
                if (args.Value.TryGetProperty("workbench_type", out var w)) workbenchFilter = w.GetString() ?? "";
                if (args.Value.TryGetProperty("surgery_only", out var so) && so.ValueKind == JsonValueKind.True) surgeryOnly = true;
                if (args.Value.TryGetProperty("page", out var jp)) page = Math.Max(1, jp.GetInt32());
                if (args.Value.TryGetProperty("page_size", out var jps)) pageSize = Math.Max(1, Math.Min(50, jps.GetInt32()));
            }

            Regex? searchRegex = null;
            if (!string.IsNullOrEmpty(search))
            {
                try { searchRegex = new Regex(search, RegexOptions.IgnoreCase); }
                catch (ArgumentException)
                {
                    return ToolResult.Error($"无效正则: {search}");
                }
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var allRecipes = DefDatabase<RecipeDef>.AllDefs;
                var filtered = allRecipes.AsEnumerable();

                // 只显示当前可用的配方（研究解锁 + 意识形态）
                filtered = filtered.Where(r => r.AvailableNow);

                // 仅手术
                if (surgeryOnly) filtered = filtered.Where(r => r.IsSurgery);
                if (!string.IsNullOrEmpty(workbenchFilter))
                {
                    filtered = filtered.Where(r =>
                        r.recipeUsers != null &&
                        r.recipeUsers.Any(u => u.defName != null &&
                            u.defName.IndexOf(workbenchFilter, StringComparison.OrdinalIgnoreCase) >= 0));
                }

                // 按正则搜索
                if (searchRegex != null)
                {
                    filtered = filtered.Where(r =>
                        (r.label != null && searchRegex.IsMatch(r.label)) ||
                        (r.defName != null && searchRegex.IsMatch(r.defName)) ||
                        (r.ProducedThingDef?.label != null && searchRegex.IsMatch(r.ProducedThingDef.label)));
                }

                var list = filtered.OrderBy(r => r.defName ?? "").ToList();
                if (list.Count == 0)
                    return ToolResult.Success("没有匹配的配方。");

                int total = list.Count;
                var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"## 可用配方 ({paged.Count} / {total} 个)");
                sb.AppendLine();
                sb.AppendLine("| 配方 | defName | 产物 | 工作台 | 技能要求 | 工作量 | 手术");
                sb.AppendLine("|------|---------|------|--------|----------|--------|-----|");

                foreach (var recipe in paged)
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
                sb.AppendLine($"**统计**: 共 {total} 个配方匹配当前过滤条件。");

                int totalPages = (int)Math.Ceiling((double)total / pageSize);
                if (total > pageSize)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.Append($"第 {page}/{totalPages} 页，共 {total} 条");
                    if (page < totalPages) sb.Append($" | page={page + 1} 下一页");
                    if (page > 1) sb.Append($" | page={page - 1} 上一页");
                    sb.AppendLine();
                }

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
