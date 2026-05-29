using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_FactionTraders : ITool
    {
        public string Name => "list_faction_traders";
        public string Description => "列出所有可虚空贸易的派系，包括好感度、可用商船类型、价格修正。派系级贸易不需要激活定居点。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { faction_name = new { type = "string", description = "派系名称过滤（可选）" } }
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
                        return ToolResult.Success(string.IsNullOrEmpty(filter) ? "没有可见派系。" : $"找不到: {filter}");

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 可贸易派系（{factions.Count} 个）\n");

                    foreach (var faction in factions)
                    {
                        string relation = faction.PlayerRelationKind switch
                        {
                            FactionRelationKind.Ally => "盟友",
                            FactionRelationKind.Neutral => "中立",
                            FactionRelationKind.Hostile => "敌对",
                            _ => "?"
                        };

                        // 好感 → 折扣：每 10 好感约 1% 折扣，盟友额外 10%
                        int discount = Math.Max(0, faction.PlayerGoodwill / 10 + (faction.PlayerRelationKind == FactionRelationKind.Ally ? 10 : 0));
                        string discountStr = discount > 0 ? $"（约 {discount}% 折扣）" : "（无折扣）";

                        sb.AppendLine($"\n### {faction.Name}");
                        sb.AppendLine($"- 关系: {relation} | 好感: {faction.PlayerGoodwill} {discountStr}");
                        sb.AppendLine($"- 科技: {faction.def.techLevel}");

                        // TraderKinds
                        var kinds = faction.def.baseTraderKinds
                            ?? new List<TraderKindDef>();
                        if (kinds.Count == 0)
                        {
                            sb.AppendLine("- 无可用的非皇家商船类型");
                        }
                        else
                        {
                            sb.AppendLine($"- 商船类型 ({kinds.Count} 种):");
                            foreach (var tk in kinds)
                            {
                                string titleReq = tk.TitleRequiredToTrade != null
                                    ? $" [需头衔: {tk.TitleRequiredToTrade.GetLabelCapForBothGenders()}]"
                                    : "";
                                string currency = tk.tradeCurrency == TradeCurrency.Favor ? " (荣誉)" : " (白银)";
                                sb.AppendLine($"  - {tk.label}{currency}{titleReq}");
                            }
                        }
                    }

                    sb.AppendLine($"\n用 trade_execute(faction_name: \"派系名称\", trader_kind: \"类型\", buy/sell: [...]) 虚空交易。");
                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"查询失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
