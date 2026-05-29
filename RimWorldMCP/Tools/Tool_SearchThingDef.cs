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
    public class Tool_SearchThingDef : ITool
    {
        public string Name => "search_thing_def";
        public string Description => "Wiki 式搜索所有 ThingDef，按 label/defName/描述模糊匹配，支持类别和类型标记过滤。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                keyword = new { type = "string", description = "模糊匹配关键字（label / defName / description）" },
                category = new
                {
                    type = "string",
                    description = "ThingCategory 过滤",
                    @enum = new[] { "item", "building", "plant", "pawn", "all" },
                    @default = "all"
                },
                flags = new
                {
                    type = "string",
                    description = "类型标记过滤，逗号分隔。可选值: weapon, apparel, medicine, drug, food, ranged_weapon, melee_weapon, stuff, craftable, research_prerequisite, haulable"
                },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认20，最大50", @default = 20 }
            },
            required = new[] { "keyword" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("keyword", out var jKw))
                return ToolResult.Error("缺少必填参数: keyword");

            string keyword = jKw.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(keyword))
                return ToolResult.Error("keyword 不能为空");

            string category = "all";
            if (args.Value.TryGetProperty("category", out var jCat))
                category = jCat.GetString() ?? "all";

            var flags = new HashSet<string>();
            if (args.Value.TryGetProperty("flags", out var jFl) && !string.IsNullOrWhiteSpace(jFl.GetString()))
            {
                foreach (var f in jFl.GetString()!.Split(','))
                {
                    var trimmed = f.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(trimmed)) flags.Add(trimmed);
                }
            }

            int page = 1, pageSize = 20;
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(50, jps.GetInt32()));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var allDefs = DefDatabase<ThingDef>.AllDefsListForReading;

                    // 关键字匹配
                    var matched = allDefs.Where(d =>
                    {
                        if (d.label?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (d.defName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (!d.description.NullOrEmpty() && d.description.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        return false;
                    }).ToList();

                    // 类别过滤
                    matched = category switch
                    {
                        "item" => matched.Where(d => d.category == ThingCategory.Item).ToList(),
                        "building" => matched.Where(d => d.category == ThingCategory.Building).ToList(),
                        "plant" => matched.Where(d => d.category == ThingCategory.Plant).ToList(),
                        "pawn" => matched.Where(d => d.category == ThingCategory.Pawn).ToList(),
                        _ => matched
                    };

                    // 类型标记过滤
                    foreach (var flag in flags)
                    {
                        matched = flag switch
                        {
                            "weapon" => matched.Where(d => d.IsWeapon).ToList(),
                            "apparel" => matched.Where(d => d.IsApparel).ToList(),
                            "medicine" => matched.Where(d => d.IsMedicine).ToList(),
                            "drug" => matched.Where(d => d.IsDrug).ToList(),
                            "food" => matched.Where(d => d.IsNutritionGivingIngestible).ToList(),
                            "ranged_weapon" => matched.Where(d => d.IsRangedWeapon).ToList(),
                            "melee_weapon" => matched.Where(d => d.IsMeleeWeapon).ToList(),
                            "stuff" => matched.Where(d => d.IsStuff).ToList(),
                            "craftable" => matched.Where(d => d.recipeMaker != null).ToList(),
                            "research_prerequisite" => matched.Where(d => d.researchPrerequisites != null && d.researchPrerequisites.Count > 0).ToList(),
                            "haulable" => matched.Where(d => d.EverHaulable).ToList(),
                            _ => matched
                        };
                    }

                    if (matched.Count == 0)
                        return ToolResult.Success($"未找到匹配 \"{keyword}\" 的 Def。");

                    // 排序：有市场价值的优先，再按字母
                    matched = matched
                        .OrderByDescending(d => d.BaseMarketValue > 0 ? 1 : 0)
                        .ThenBy(d => d.label ?? d.defName)
                        .ToList();

                    int total = matched.Count;
                    var paged = matched.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine($"## search_thing_def: \"{keyword}\" 共 {total} 条");
                    sb.AppendLine();

                    foreach (var d in paged)
                    {
                        // 类型标签
                        var tags = new List<string>();
                        if (d.IsWeapon) tags.Add(d.IsRangedWeapon ? "远程武器" : (d.IsMeleeWeapon ? "近战武器" : "武器"));
                        if (d.IsApparel) tags.Add("衣物");
                        if (d.IsMedicine) tags.Add("药物");
                        if (d.IsDrug) tags.Add("成瘾品");
                        if (d.IsNutritionGivingIngestible) tags.Add("食物");
                        if (d.IsStuff) tags.Add("材料");
                        if (d.recipeMaker != null) tags.Add("可制造");
                        if (d.researchPrerequisites?.Count > 0) tags.Add("需研究");

                        string tagStr = tags.Count > 0 ? $"[{string.Join("|", tags)}] " : "";
                        string priceStr = d.BaseMarketValue > 0 ? $", ${d.BaseMarketValue:F0}" : "";
                        string massStr = d.BaseMass > 0 ? $", {d.BaseMass:F2}kg" : "";
                        sb.AppendLine($"- {tagStr}{d.label} (`{d.defName}`) — {d.category}{priceStr}{massStr}");
                    }

                    if (total > pageSize)
                    {
                        int totalPages = (int)Math.Ceiling((double)total / pageSize);
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.Append($"第 {page}/{totalPages} 页，共 {total} 条");
                        if (page < totalPages) sb.Append($" | page={page + 1} 下一页");
                        if (page > 1) sb.Append($" | page={page - 1} 上一页");
                        sb.AppendLine();
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"搜索 Def 失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
