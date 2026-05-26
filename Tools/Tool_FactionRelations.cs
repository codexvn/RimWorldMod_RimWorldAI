using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        public string Description => "列出派系好感度及定居点缓存状态。仅显示已缓存库存的定居点详情，避免触发大量 RegenerateStock。用 activate_settlement_goods 按需激活。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                faction_name = new { type = "string", description = "派系名称（可选，过滤）" }
            }
        });

        // 反射访问 Settlement_TraderTracker 的私有 stock 字段，避免触发 RegenerateStock
        private static readonly FieldInfo _stockField = typeof(Settlement_TraderTracker)
            .GetField("stock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Settlement_TraderTracker.stock field not found");

        /// <summary>安全检查是否有已缓存的库存（不触发生成）</summary>
        private static bool HasCachedStock(Settlement s)
        {
            try
            {
                var trader = s.trader;
                if (trader == null) return false;
                var stock = _stockField.GetValue(trader) as ThingOwner<Thing>;
                return stock?.Count > 0;
            }
            catch { return false; }
        }

        /// <summary>获取已缓存库存摘要（不触发生成）</summary>
        private static string SafeStockSummary(Settlement s)
        {
            try
            {
                var trader = s.trader;
                if (trader == null) return "";
                var stock = _stockField.GetValue(trader) as ThingOwner<Thing>;
                if (stock == null || stock.Count == 0) return "";
                int kinds = stock.Select(t => t.def).Distinct().Count();
                int total = stock.Sum(t => t.stackCount);
                return $" [{kinds}种, {total}件]";
            }
            catch { return ""; }
        }

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

                        var settlements = Find.World.worldObjects.Settlements
                            .Where(s => s.Faction == faction && s.CanTradeNow)
                            .ToList();

                        if (settlements.Count > 0)
                        {
                            var cached = settlements.Where(HasCachedStock).ToList();
                            var uncached = settlements.Count - cached.Count;
                            sb.AppendLine($"- 定居点: {settlements.Count} 个 (已缓存: {cached.Count}, 未激活: {uncached})");

                            foreach (var s in cached)
                            {
                                var summary = SafeStockSummary(s);
                                sb.AppendLine($"  - {s.Name} (Tile {s.Tile}){summary}");
                            }
                            if (uncached > 0 && uncached <= 10)
                            {
                                var ucNames = settlements.Where(s => !HasCachedStock(s))
                                    .Select(s => s.Name).ToList();
                                sb.AppendLine($"  未激活: {string.Join(", ", ucNames)}");
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
