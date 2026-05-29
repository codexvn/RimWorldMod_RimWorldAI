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
    public class Tool_GetAvailableSurgeries : ITool
    {
        public string Name => "get_available_surgeries";
        public string Description => "列出指定殖民者当前可用的所有手术，包含兼容的身体部位、产物、致死率和技能要求。先查此工具再调 schedule_operation。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 thingIDNumber" },
                search = new { type = "string", description = "正则过滤配方名/defName，如 .*仿生.*" },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认10，最大50", @default = 10 }
            },
            required = new[] { "colonist_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数: colonist_id");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");

            var search = "";
            int page = 1, pageSize = 10;
            if (args.Value.TryGetProperty("search", out var s))
                search = s.GetString() ?? "";
            if (args.Value.TryGetProperty("page", out var jp)) page = Math.Max(1, jp.GetInt32());
            if (args.Value.TryGetProperty("page_size", out var jps)) pageSize = Math.Max(1, Math.Min(50, jps.GetInt32()));

            Regex? searchRegex = null;
            if (!string.IsNullOrEmpty(search))
            {
                try { searchRegex = new Regex(search, RegexOptions.IgnoreCase); }
                catch { return ToolResult.Error($"无效正则: {search}"); }
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                    .FirstOrDefault(c => c.thingIDNumber == colonistId);
                if (pawn == null)
                    return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                var recipes = DefDatabase<RecipeDef>.AllDefs
                    .Where(r => r.IsSurgery && r.AvailableNow);

                if (searchRegex != null)
                    recipes = recipes.Where(r =>
                        (r.label != null && searchRegex.IsMatch(r.label)) ||
                        (r.defName != null && searchRegex.IsMatch(r.defName)));

                var recipeList = recipes
                    .OrderBy(r => r.workerClass?.Name ?? "")
                    .ThenBy(r => r.label ?? "")
                    .ToList();
                if (recipeList.Count == 0)
                    return ToolResult.Success(searchRegex != null
                        ? $"没有匹配 '{search}' 的可用手术。"
                        : $"{pawn.Name.ToStringShort} 当前没有可用手术。");

                // 第一遍：收集所有通过可用性检查的条目
                var entries = new List<(string category, string row, bool targetsBodyPart)>();
                foreach (var recipe in recipeList)
                {
                    var availReport = recipe.Worker.AvailableReport(pawn, null);
                    if (!availReport.Accepted) continue;

                    if (recipe.targetsBodyPart)
                    {
                        var parts = recipe.Worker.GetPartsToApplyOn(pawn, recipe)
                            .Where(p => recipe.AvailableOnNow(pawn, p))
                            .ToList();
                        if (parts.Count == 0) continue;

                        var category = recipe.workerClass?.Name ?? "Other";
                        string partsStr = string.Join(", ", parts.Select(p => p.Label));
                        string product = recipe.addsHediff?.label
                            ?? recipe.ProducedThingDef?.label
                            ?? (recipe.removesHediff != null ? $"移除{recipe.removesHediff.label}" : "-");
                        string deathChance = recipe.deathOnFailedSurgeryChance > 0f
                            ? $"{recipe.deathOnFailedSurgeryChance * 100:F0}%"
                            : "无";
                        string skills = recipe.skillRequirements != null && recipe.skillRequirements.Count > 0
                            ? string.Join(", ", recipe.skillRequirements.Select(sr =>
                                $"{(sr.skill?.label ?? "?")}{sr.minLevel}+"))
                            : "-";

                        entries.Add((category,
                            $"| {recipe.label} | `{recipe.defName}` | {partsStr} | {product} | {deathChance} | {skills} |",
                            true));
                    }
                    else
                    {
                        if (pawn.health?.hediffSet?.HasHediff(recipe.addsHediff, false) == true)
                            continue;

                        var category = recipe.workerClass?.Name ?? "Other";
                        string desc = recipe.addsHediff != null
                            ? $"添加 {recipe.addsHediff.label}"
                            : recipe.removesHediff != null
                                ? $"移除 {recipe.removesHediff.label}"
                                : recipe.description ?? "-";

                        entries.Add((category,
                            $"| {recipe.label} | `{recipe.defName}` | {desc} |",
                            false));
                    }
                }

                if (entries.Count == 0)
                    return ToolResult.Success($"{pawn.Name.ToStringShort} 当前没有可用手术。");

                // 分页
                int total = entries.Count;
                var pagedEntries = entries.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                // 第二遍：构建输出
                var sb = new StringBuilder();
                sb.AppendLine($"## {pawn.Name.ToStringShort} 可用手术");
                sb.AppendLine();

                string outputCategory = "";
                bool outputHasParts = false;
                foreach (var (category, row, targetsBodyPart) in pagedEntries)
                {
                    if (outputCategory != category || (outputCategory == category && outputHasParts != targetsBodyPart))
                    {
                        if (outputCategory != "")
                            sb.AppendLine();
                        outputCategory = category;
                        outputHasParts = targetsBodyPart;
                        sb.AppendLine($"### {category}");
                        if (targetsBodyPart)
                        {
                            sb.AppendLine("| 配方 | defName | 部位 | 产物 | 致死率 | 技能 |");
                            sb.AppendLine("|------|---------|------|------|--------|------|");
                        }
                        else
                        {
                            sb.AppendLine("| 配方 | defName | 说明 |");
                            sb.AppendLine("|------|---------|------|");
                        }
                    }
                    sb.AppendLine(row);
                }

                // 分页信息
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

                return ToolResult.Success(sb.ToString().TrimEnd());
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
