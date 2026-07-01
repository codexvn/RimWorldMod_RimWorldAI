using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_ListResearchProjects : ITool, INoMapRequired
    {
        public string Name => "list_research_projects";
        public string Description => "列出所有研究项目及每个科技解锁的内容。支持状态过滤和关键词搜索。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                filter = new { type = "string", description = "过滤状态: available(可研究) / completed(已完成) / all(全部)", @enum = new[] { "available", "completed", "all" }, @default = "available" },
                search = new { type = "string", description = "搜索关键词（匹配项目名或 defName）" },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认5，最大20", @default = 5 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var filter = "available";
            var search = "";
            int page = 1, pageSize = 5;

            if (args != null)
            {
                if (args.Value.TryGetProperty("filter", out var f)) filter = f.GetString() ?? "available";
                if (args.Value.TryGetProperty("search", out var s)) search = s.GetString() ?? "";
                if (args.Value.TryGetProperty("page", out var jp)) page = Math.Max(1, jp.GetInt32());
                if (args.Value.TryGetProperty("page_size", out var jps)) pageSize = Math.Max(1, Math.Min(jps.GetInt32(), 20));
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var allProjects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                if (allProjects == null || allProjects.Count == 0)
                    return ToolResult.Success("没有可用的研究项目。");

                var filtered = allProjects.AsEnumerable();

                switch (filter)
                {
                    case "available":
                        filtered = filtered.Where(p => p.CanStartNow);
                        break;
                    case "completed":
                        filtered = filtered.Where(p => p.IsFinished);
                        break;
                }

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
                int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
                var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"## 研究项目 ({totalItems} 个)");
                sb.AppendLine();

                var total = allProjects.Count;
                var finished = allProjects.Count(p => p.IsFinished);
                sb.AppendLine($"总计 {total} 项 | 已完成 {finished} 项 | 未完成 {total - finished} 项");
                sb.AppendLine();

                var idx = (page - 1) * pageSize;
                foreach (var proj in paged)
                {
                    idx++;
                    var status = proj.IsFinished ? "[已完成]" : "[可研究]";
                    var label = proj.label ?? proj.defName ?? "???";
                    var costStr = proj.CostApparent > 0 ? $"工作量 {proj.CostApparent:F0}" : $"知识点 {proj.knowledgeCost:F0}";

                    sb.AppendLine($"### {idx}. {status} {label} (`{proj.defName}`) — {costStr}");

                    var unlocks = proj.UnlockedDefs;
                    if (unlocks != null && unlocks.Count > 0)
                    {
                        // 分类
                        var buildings = unlocks.OfType<ThingDef>().Where(d => d.IsBuildingArtificial && !d.IsApparel && !d.IsWeapon).ToList();
                        var weapons = unlocks.OfType<ThingDef>().Where(d => d.IsWeapon).ToList();
                        var apparels = unlocks.OfType<ThingDef>().Where(d => d.IsApparel).ToList();
                        var plants = unlocks.OfType<ThingDef>().Where(d => d.plant != null).ToList();
                        var terrains = unlocks.OfType<TerrainDef>().ToList();
                        var surgeryRecipes = unlocks.OfType<RecipeDef>().Where(r => r.IsSurgery).ToList();
                        var rituals = unlocks.OfType<PsychicRitualDef>().ToList();
                        var other = unlocks.Where(d =>
                            !(d is ThingDef td && (td.IsBuildingArtificial || td.IsWeapon || td.IsApparel || td.plant != null)) &&
                            !(d is TerrainDef) && !(d is RecipeDef) && !(d is PsychicRitualDef)
                        ).ToList();

                        if (buildings.Count > 0)
                            sb.AppendLine($"  **建筑**: {string.Join(", ", buildings.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");

                        if (weapons.Count > 0)
                            sb.AppendLine($"  **武器**: {string.Join(", ", weapons.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");

                        if (apparels.Count > 0)
                            sb.AppendLine($"  **装备**: {string.Join(", ", apparels.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");

                        if (plants.Count > 0)
                            sb.AppendLine($"  **植物**: {string.Join(", ", plants.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");

                        if (terrains.Count > 0)
                            sb.AppendLine($"  **地形**: {string.Join(", ", terrains.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");

                        if (surgeryRecipes.Count > 0)
                            sb.AppendLine($"  **手术**: {string.Join(", ", surgeryRecipes.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");

                        if (rituals.Count > 0)
                            sb.AppendLine($"  **仪式**: {string.Join(", ", rituals.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");

                        if (other.Count > 0)
                            sb.AppendLine($"  **其他**: {string.Join(", ", other.Select(d => $"{d.label ?? d.defName}（`{d.defName}`）"))}");
                    }
                    else
                    {
                        sb.AppendLine("  *无解锁内容*");
                    }

                    sb.AppendLine();
                }

                if (totalItems > pageSize)
                {
                    sb.AppendLine("---");
                    sb.Append($"第 {page}/{totalPages} 页，共 {totalItems} 条");
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
