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
                search = new { type = "string", description = "搜索关键词" }
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

                var list = filtered.ToList();
                if (list.Count == 0)
                    return ToolResult.Success("没有匹配的研究项目。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 研究项目 ({list.Count} 个)");
                sb.AppendLine();
                sb.AppendLine("| 状态 | 项目名称 | defName | 工作量 | 科技等级 | 研究设施 | 前置项目 |");
                sb.AppendLine("|------|----------|---------|--------|----------|----------|----------|");

                foreach (var proj in list)
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

                return ToolResult.Success(sb.ToString());
            });
        }
    }
}
