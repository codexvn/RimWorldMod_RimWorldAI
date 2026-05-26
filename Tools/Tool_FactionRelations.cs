using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_FactionRelations : ITool
    {
        public string Name => "get_faction_relations";
        public string Description => "列出所有派系好感度及其可贸易定居点。用于评估贸易对象和外交状态。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                faction_name = new { type = "string", description = "派系名称（可选，过滤特定派系）" }
            }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string filter = "";
            if (args != null && args.Value.TryGetProperty("faction_name", out var jFn))
                filter = jFn.GetString() ?? "";

            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var factions = Find.FactionManager.AllFactionsVisible
                        .Where(f => !f.IsPlayer && !f.temporary)
                        .Where(f => string.IsNullOrEmpty(filter) ||
                            f.Name.ToLowerInvariant().Contains(filter.ToLowerInvariant()))
                        .OrderBy(f => f.PlayerRelationKind)
                        .ThenByDescending(f => f.PlayerGoodwill)
                        .ToList();

                    if (factions.Count == 0)
                        return ToolResult.Success(string.IsNullOrEmpty(filter) ? "没有可见派系。" : $"找不到派系: {filter}");

                    var sb = new StringBuilder();
                    sb.AppendLine("## 派系关系\n");

                    foreach (var faction in factions)
                    {
                        string relation = faction.PlayerRelationKind switch
                        {
                            FactionRelationKind.Ally => "盟友",
                            FactionRelationKind.Neutral => "中立",
                            FactionRelationKind.Hostile => "敌对",
                            _ => "?"
                        };
                        sb.AppendLine($"### {faction.Name}");
                        sb.AppendLine($"- 关系: {relation} | 好感度: {faction.PlayerGoodwill}");
                        sb.AppendLine($"- 科技: {faction.def.techLevel}");

                        // 可贸易定居点
                        var settlements = Find.World.worldObjects.Settlements
                            .Where(s => s.Faction == faction && s.CanTradeNow)
                            .ToList();

                        if (settlements.Count > 0)
                        {
                            sb.AppendLine($"- 可贸易定居点 ({settlements.Count}):");
                            foreach (var s in settlements)
                            {
                                string stockInfo = "";
                                try
                                {
                                    var stock = s.Goods?.ToList();
                                    if (stock != null)
                                    {
                                        int kinds = stock.Select(t => t.def).Distinct().Count();
                                        int total = stock.Sum(t => t.stackCount);
                                        stockInfo = $" [{kinds}种, {total}件]";
                                    }
                                }
                                catch { stockInfo = " [库存未生成]"; }
                                sb.AppendLine($"  - {s.Name} ({s.Tile}){stockInfo}");
                            }
                        }
                        else
                        {
                            sb.AppendLine("- 无可贸易定居点");
                        }
                        sb.AppendLine();
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"查询失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
