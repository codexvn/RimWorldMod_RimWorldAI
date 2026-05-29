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
    public class Tool_ListResearchProjects : ITool
    {
        public string Name => "list_research_projects";
        public string Description => "列出所有研究项目，可按状态过滤（available 可研究, completed 已完成, all 全部）和关键词搜索。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                filter = new { type = "string", description = "过滤状态", @enum = new[] { "available", "completed", "all" } },
                search = new { type = "string", description = "搜索关键词" },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认10，最大50", @default = 10 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var filter = "available";
            var search = "";
            if (args != null)
            {
                if (args.Value.TryGetProperty("filter", out var f)) filter = f.GetString() ?? "available";
                if (args.Value.TryGetProperty("search", out var s)) search = s.GetString() ?? "";
            }

            int page = 1, pageSize = 10;
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(jps.GetInt32(), 50));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var allProjects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                if (allProjects == null || allProjects.Count == 0)
                    return ToolResult.Success("没有可用的研究项目。");

                var filtered = allProjects.AsEnumerable();

                // 按状态过滤
                switch (filter)
                {
                    case "available":
                        filtered = filtered.Where(p => p.CanStartNow);
                        break;
                    case "completed":
                        filtered = filtered.Where(p => p.IsFinished);
                        break;
                    // "all" 不过滤
                }

                // 按关键词搜索
                if (!string.IsNullOrEmpty(search))
                {
                    filtered = filtered.Where(p =>
                        (p.label != null && p.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (p.defName != null && p.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
                }

                var list = filtered.OrderBy(r => r.defName ?? "").ToList();
                if (list.Count == 0)
                    return ToolResult.Success("没有匹配的研究项目。");

                int totalItems = list.Count;
                var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"## 研究项目 ({totalItems} 个)");
                sb.AppendLine();
                sb.AppendLine("| 状态 | 项目名称 | defName | 工作量 | 科技等级 | 研究设施 | 前置项目 |");
                sb.AppendLine("|------|----------|---------|--------|----------|----------|----------|");

                foreach (var proj in paged)
                {
                    var status = proj.IsFinished ? "已完成" : "可研究";
                    var label = proj.label ?? proj.defName ?? "???";
                    var defName = proj.defName ?? "???";
                    var costApparent = proj.CostApparent;
                    var techLevel = proj.techLevel.ToStringSafe();
                    var requiredBuilding = proj.requiredResearchBuilding?.label ?? "-";
                    var prereqs = "";
                    if (proj.prerequisites != null && proj.prerequisites.Count > 0)
                        prereqs = string.Join(", ", proj.prerequisites.Select(p => p.label ?? p.defName));
                    else
                        prereqs = "无";

                    sb.AppendLine($"| {status} | {label} | `{defName}` | {costApparent} | {techLevel} | {requiredBuilding} | {prereqs} |");
                }

                // 附加快照统计
                var total = allProjects.Count;
                var finished = allProjects.Count(p => p.IsFinished);
                var availableCount = total - finished;
                sb.AppendLine();
                sb.AppendLine($"**统计**: 总计 {total} 项 | 已完成 {finished} 项 | 未完成 {availableCount} 项");

                int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
                if (totalItems > pageSize)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.Append($"第 {page}/{totalPages} 页，共 {totalItems} 条");
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
