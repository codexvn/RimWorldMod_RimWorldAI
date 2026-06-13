using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_FindPawn : ITool
    {
        public string Name => "find_pawn";
        public string Description => "按名称搜索地图上的殖民者、敌人、动物、访客、囚犯、奴隶等所有生物，返回位置和 ID。支持正则表达式（如 .* 匹配所有）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "搜索名称（模糊匹配 LabelShort/Name/defName），如\"张三\"、\"Muffalo\"、\"海盗\"" },
                kind = new
                {
                    type = "string",
                    description = "类型过滤",
                    @enum = new[] { "colonist", "enemy", "animal", "visitor", "prisoner", "slave", "all" },
                    @default = "all"
                },
                max_results = new { type = "integer", description = "最大返回数，默认 10", @default = 10 },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认10，最大50", @default = 10 }
            },
            required = new[] { "name" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("name", out var jName))
                return ToolResult.Error("缺少必填参数: name");

            string searchName = jName.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(searchName))
                return ToolResult.Error("name 不能为空");

            string kind = "all";
            if (args.Value.TryGetProperty("kind", out var jKind))
                kind = jKind.GetString() ?? "all";

            int maxResults = 10;
            if (args.Value.TryGetProperty("max_results", out var jMax) && jMax.TryGetInt32(out var m))
                maxResults = Math.Max(1, Math.Min(m, 50));

            int page = 1, pageSize = 10;
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(jps.GetInt32(), 50));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var allPawns = map.mapPawns.AllPawnsSpawned.Where(p => !p.Fogged()).ToList();

                    // 名称匹配（支持正则）
                    var matched = nameMatchesRegex(searchName)
                        ? allPawns.Where(p =>
                        {
                            try
                            {
                                if (p.Name != null)
                                {
                                    if (Regex.IsMatch(p.Name.ToStringShort, searchName, RegexOptions.IgnoreCase)) return true;
                                    if (Regex.IsMatch(p.Name.ToStringFull, searchName, RegexOptions.IgnoreCase)) return true;
                                }
                                if (Regex.IsMatch(p.LabelShort, searchName, RegexOptions.IgnoreCase)) return true;
                                if (Regex.IsMatch(p.def.defName, searchName, RegexOptions.IgnoreCase)) return true;
                                if (Regex.IsMatch(p.KindLabel, searchName, RegexOptions.IgnoreCase)) return true;
                            }
                            catch { /* regex 无效则降级 */ }
                            return false;
                        }).ToList()
                        : allPawns.Where(p =>
                        {
                            // 退化为子串匹配（原行为）
                            if (p.Name != null)
                            {
                                if (p.Name.ToStringShort.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                                if (p.Name.ToStringFull.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            }
                            if (p.LabelShort.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            if (p.def.defName.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            if (p.KindLabel.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            return false;
                        }).ToList();

                    // 种类过滤
                    if (kind != "all")
                    {
                        matched = kind switch
                        {
                            "colonist" => matched.Where(p => p.IsColonist).ToList(),
                            "enemy" => matched.Where(p => p.HostileTo(Faction.OfPlayer)).ToList(),
                            "animal" => matched.Where(p => p.RaceProps?.Animal == true).ToList(),
                            "visitor" => matched.Where(p => p.IsColonist == false && p.HostileTo(Faction.OfPlayer) == false && p.Faction != null).ToList(),
                            "prisoner" => matched.Where(p => p.IsPrisonerOfColony).ToList(),
                            "slave" => matched.Where(p => p.IsSlaveOfColony).ToList(),
                            _ => matched
                        };
                    }

                    if (matched.Count == 0)
                        return ToolResult.Success($"未找到匹配 \"{searchName}\" 的生物。");

                    int total = matched.Count;
                    var paged = matched.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine($"## find_pawn: \"{searchName}\" 共 {total} 条");

                    foreach (var p in paged)
                    {
                        string factionStr = p.Faction != null ? $", {p.Faction.Name}" : "";
                        string stateStr = "";
                        if (p.Downed) stateStr = ", 倒地";
                        else if (p.InMentalState) stateStr = $", {p.MentalState?.def?.label ?? "精神异常"}";
                        else if (p.Dead) stateStr = ", 已死亡";

                        string revengeStr = "";
                        if (kind == "animal")
                        {
                            try
                            {
                                var wf = typeof(RaceProperties).GetField("wildness");
                                if (wf != null && p.RaceProps != null)
                                    revengeStr = $"，反击概率 {(float)wf.GetValue(p.RaceProps) * 100:F0}%";
                            }
                            catch (Exception ex) { McpLog.Info($"[find_pawn] 读取野性失败: {ex.Message}"); }
                        }
                        sb.AppendLine($"- [{p.Position.x},{p.Position.z}] {p.LabelShort} ({p.KindLabel}) ID:{p.thingIDNumber}{factionStr}{stateStr}{revengeStr}");
                    }

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
                }
                catch (Exception ex) { return ToolResult.Error($"搜索失败: {ex.Message}"); }
            });
        }
        /// <summary>检测 name 是否为正则表达式（含 . * + ? ^ $ | [ ] ( ) { } \ 等元字符）</summary>
        private static bool nameMatchesRegex(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
                if ("*+?^$|[](){}\\.-".IndexOf(c) >= 0)
                    return true;
            return false;
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
