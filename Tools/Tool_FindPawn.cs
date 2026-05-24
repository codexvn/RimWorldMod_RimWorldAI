using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_FindPawn : ITool
    {
        public string Name => "find_pawn";
        public string Description => "按名称搜索地图上的殖民者、敌人、动物、访客等所有生物，返回位置和 ID。";
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
                    @enum = new[] { "colonist", "enemy", "animal", "visitor", "all" },
                    @default = "all"
                },
                max_results = new { type = "integer", description = "最大返回数，默认 10", @default = 10 }
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

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var allPawns = map.mapPawns.AllPawnsSpawned;

                    // 名称匹配
                    var matched = allPawns.Where(p =>
                    {
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
                            _ => matched
                        };
                    }

                    if (matched.Count == 0)
                        return ToolResult.Success($"未找到匹配 \"{searchName}\" 的生物。");

                    var take = matched.Take(maxResults).ToList();
                    var sb = new StringBuilder();
                    sb.AppendLine($"## find_pawn: \"{searchName}\" 共 {matched.Count} 条");

                    foreach (var p in take)
                    {
                        string factionStr = p.Faction != null ? $", {p.Faction.Name}" : "";
                        string stateStr = "";
                        if (p.Downed) stateStr = ", 倒地";
                        else if (p.InMentalState) stateStr = $", {p.MentalState?.def?.label ?? "精神异常"}";
                        else if (p.Dead) stateStr = ", 已死亡";

                        sb.AppendLine($"- [{p.Position.x},{p.Position.z}] {p.LabelShort} ({p.KindLabel}) ID:{p.thingIDNumber}{factionStr}{stateStr}");
                    }

                    if (matched.Count > maxResults)
                        sb.AppendLine($"... 还有 {matched.Count - maxResults} 条");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"搜索失败: {ex.Message}"); }
            });
        }
    }
}
