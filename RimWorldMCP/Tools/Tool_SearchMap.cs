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
    public class Tool_SearchMap : ITool
    {
        public string Name => "search_map";
        public string Description => "在地图上搜索物品、生物、建筑。支持关键字模糊匹配和 defName 精确过滤，返回位置和 ID。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                keyword = new { type = "string", description = "模糊匹配关键字（匹配 Label 或 defName）" },
                defName = new { type = "string", description = "精确 defName 过滤" },
                pos_x = new { type = "integer", description = "搜索中心 X 坐标（可选，与 pos_y 和 radius 配合）" },
                pos_y = new { type = "integer", description = "搜索中心 Y 坐标（可选）" },
                radius = new { type = "number", description = "搜索半径（格），不填则全图搜索" },
                category = new
                {
                    type = "string",
                    description = "过滤类型",
                    @enum = new[] { "item", "pawn", "building", "all" },
                    @default = "all"
                },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认20，最大50", @default = 20 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string keyword = "";
            if (args != null && args.Value.TryGetProperty("keyword", out var kw))
                keyword = kw.GetString() ?? "";

            string defName = "";
            if (args != null && args.Value.TryGetProperty("defName", out var dn))
                defName = dn.GetString() ?? "";

            int cx = -1, cz = -1;
            float radius = -1f;
            bool hasCenter = args != null
                && args.Value.TryGetProperty("pos_x", out var px) && px.TryGetInt32(out cx)
                && args.Value.TryGetProperty("pos_y", out var py) && py.TryGetInt32(out cz);
            if (hasCenter && args!.Value.TryGetProperty("radius", out var r))
                r.TryGetSingle(out radius);

            string category = "all";
            if (args != null && args.Value.TryGetProperty("category", out var cat))
                category = cat.GetString() ?? "all";

            int page = 1, pageSize = 20;
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(50, jps.GetInt32()));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    // 收集候选
                    var results = new List<(string line, int distSq, string typeLabel)>();

                    // 物品 + 建筑
                    if (category == "all" || category == "item" || category == "building")
                    {
                        foreach (var t in map.listerThings.AllThings)
                        {
                            if (t.Fogged()) continue;
                            if (category == "item" && t is Building) continue;
                            if (category == "building" && !(t is Building)) continue;
                            if (t is Blueprint || t is Frame) continue; // 过滤蓝图

                            if (!string.IsNullOrEmpty(defName) && t.def.defName != defName) continue;

                            if (!string.IsNullOrEmpty(keyword))
                            {
                                bool matchLabel = t.def.label?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                                bool matchDef = t.def.defName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                                if (!matchLabel && !matchDef) continue;
                            }

                            string typeLabel = t is Building ? "建筑" : "物品";
                            int distSq = hasCenter ? t.Position.DistanceToSquared(new IntVec3(cx, 0, cz)) : 0;
                            float rSq = radius * radius;
                            if (hasCenter && radius > 0 && distSq > rSq) continue;

                            string label = $"[{t.Position.x},{t.Position.z}] {t.Label} ({t.def.defName}, ID:{t.thingIDNumber}) {typeLabel}";
                            results.Add((label, distSq, typeLabel));
                        }
                    }

                    // 生物
                    if (category == "all" || category == "pawn")
                    {
                        foreach (var p in map.mapPawns.AllPawnsSpawned)
                        {
                            if (p.Fogged()) continue;
                            if (!string.IsNullOrEmpty(defName) && p.def.defName != defName) continue;

                            if (!string.IsNullOrEmpty(keyword))
                            {
                                bool matchLabel = p.def.label?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                                bool matchDef = p.def.defName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                                bool matchName = p.Name?.ToStringShort?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                                              || p.Name?.ToStringFull?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                                if (!matchLabel && !matchDef && !matchName) continue;
                            }

                            int distSq = hasCenter ? p.Position.DistanceToSquared(new IntVec3(cx, 0, cz)) : 0;
                            float rSq = radius * radius;
                            if (hasCenter && radius > 0 && distSq > rSq) continue;

                            string factionLabel = p.Faction != null ? $" ({p.Faction.Name})" : " (野生)";
                            string label = $"[{p.Position.x},{p.Position.z}] {p.LabelShort} ({p.def.defName}, ID:{p.thingIDNumber}) 生物{factionLabel}";
                            results.Add((label, distSq, "生物"));
                        }
                    }

                    if (results.Count == 0)
                    {
                        string hint = !string.IsNullOrEmpty(keyword)
                            ? $"未找到匹配 \"{keyword}\" 的结果。"
                            : "地图上没有任何匹配的结果。";
                        return ToolResult.Success(hint);
                    }

                    // 排序：有中心点时按距离排序，否则按类型分组
                    if (hasCenter && radius > 0)
                        results = results.OrderBy(r => r.distSq).ToList();

                    int total = results.Count;
                    var paged = results.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                    var sb = new StringBuilder();
                    string filterDesc = "";
                    if (!string.IsNullOrEmpty(keyword)) filterDesc += $"关键词: {keyword}";
                    if (!string.IsNullOrEmpty(defName)) filterDesc += (filterDesc.Length > 0 ? ", " : "") + $"defName: {defName}";
                    if (hasCenter) filterDesc += (filterDesc.Length > 0 ? ", " : "") + $"中心 ({cx},{cz}) 半径 {radius}";
                    sb.AppendLine($"## 搜索结果 {(filterDesc.Length > 0 ? "(" + filterDesc + ")" : "")} 共 {total} 条");

                    foreach (var (line, _, typeLabel) in paged)
                        sb.AppendLine($"- {line}");

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

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"搜索失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
