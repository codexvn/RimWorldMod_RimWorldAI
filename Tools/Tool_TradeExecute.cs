using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_TradeExecute : ITool
    {
        public string Name => "trade_execute";
        public string Description => "与商船执行交易。先用 trade_with_ship 列出商船，再调用本工具买卖物品。定价使用游戏内置交易系统。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                ship_name = new { type = "string", description = "商船名称（匹配 trade_with_ship 返回的名称）" },
                sell = new
                {
                    type = "array",
                    description = "出售给商船的物品列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            item = new { type = "string", description = "物品名称（匹配 Label/defName，模糊匹配）" },
                            count = new { type = "integer", description = "出售数量" }
                        }
                    }
                },
                buy = new
                {
                    type = "array",
                    description = "从商船购买的物品列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            item = new { type = "string", description = "物品名称（匹配 Label/defName，模糊匹配）" },
                            count = new { type = "integer", description = "购买数量" }
                        }
                    }
                }
            },
            required = new[] { "ship_name" }
        });

        private static readonly PropertyInfo _costForSource = typeof(Tradeable).GetProperty("CurTotalCurrencyCostForSource")
            ?? throw new Exception("CurTotalCurrencyCostForSource not found");
        private static readonly PropertyInfo _allTradeablesProp = typeof(TradeDeal).GetProperty("AllTradeables")
            ?? throw new Exception("AllTradeables not found");

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("ship_name", out var jSn))
                return ToolResult.Error("缺少必填参数: ship_name");
            string shipName = jSn.GetString() ?? "";

            var sellList = new List<(string item, int count)>();
            if (args.Value.TryGetProperty("sell", out var jSell) && jSell.ValueKind == JsonValueKind.Array)
                foreach (var si in jSell.EnumerateArray())
                {
                    string s = si.TryGetProperty("item", out var si1) ? si1.GetString() ?? "" : "";
                    int c = si.TryGetProperty("count", out var si2) ? si2.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(s) && c > 0) sellList.Add((s, c));
                }

            var buyList = new List<(string item, int count)>();
            if (args.Value.TryGetProperty("buy", out var jBuy) && jBuy.ValueKind == JsonValueKind.Array)
                foreach (var bi in jBuy.EnumerateArray())
                {
                    string s = bi.TryGetProperty("item", out var bi1) ? bi1.GetString() ?? "" : "";
                    int c = bi.TryGetProperty("count", out var bi2) ? bi2.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(s) && c > 0) buyList.Add((s, c));
                }

            if (sellList.Count == 0 && buyList.Count == 0)
                return ToolResult.Error("至少需要 sell 或 buy 之一");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    var ship = map.passingShipManager.passingShips.OfType<TradeShip>()
                        .FirstOrDefault(s => !s.Departed && s.CanTradeNow
                            && s.name.ToLowerInvariant().Contains(shipName.ToLowerInvariant()));
                    if (ship == null)
                        return ToolResult.Error($"找不到商船: {shipName}");

                    var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .FirstOrDefault(p => p.health.capacities.CapableOf(PawnCapacityDefOf.Talking));
                    if (pawn == null) return ToolResult.Error("没有能说话的殖民者");

                    TradeSession.trader = ship;
                    TradeSession.playerNegotiator = pawn;
                    var deal = new TradeDeal();
                    var tradeables = (List<Tradeable>)_allTradeablesProp.GetValue(deal);

                    var log = new StringBuilder();
                    int sellValue = 0, buyValue = 0;

                    foreach (var (itemName, count) in sellList)
                    {
                        var t = FindTradeable(tradeables, itemName, true);
                        if (t == null) { log.AppendLine($"找不到可售物品: {itemName}"); continue; }
                        int max = t.CountToTransferToSource;
                        if (max <= 0) { log.AppendLine($"{itemName}: 无可售库存"); continue; }
                        int actual = Math.Min(count, max);
                        t.ForceToSource(actual);
                        int v = (int)Math.Round((float)_costForSource.GetValue(t));
                        sellValue += v;
                        log.AppendLine($"售 {t.Label} x{actual} → +{v} 银");
                    }

                    foreach (var (itemName, count) in buyList)
                    {
                        var t = FindTradeable(tradeables, itemName, false);
                        if (t == null) { log.AppendLine($"商船没有: {itemName}"); continue; }
                        int max = t.CountToTransferToDestination;
                        if (max <= 0) { log.AppendLine($"{itemName}: 商船库存不足"); continue; }
                        int actual = Math.Min(count, max);
                        t.ForceToDestination(actual);
                        int v = (int)Math.Round((float)_costForSource.GetValue(t));
                        buyValue += Math.Abs(v);
                        log.AppendLine($"购 {t.Label} x{actual} → -{Math.Abs(v)} 银");
                    }

                    int net = sellValue - buyValue;
                    log.AppendLine($"净额: {net:+0;-#} 银");

                    if (sellValue == 0 && buyValue == 0)
                    {
                        ResetSession();
                        return ToolResult.Error("没有成功匹配到任何物品");
                    }

                    bool ok = deal.TryExecute(out bool actuallyTraded);
                    ResetSession();

                    if (!ok) return ToolResult.Error("交易执行失败");
                    if (!actuallyTraded) return ToolResult.Error("交易未产生实际交换（可能资金不足）");
                    return ToolResult.Success(log.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    ResetSession();
                    return ToolResult.Error($"交易失败: {ex.Message}");
                }
            });
        }

        private static void ResetSession()
        {
            TradeSession.trader = null!;
            TradeSession.playerNegotiator = null!;
        }

        private static Tradeable? FindTradeable(List<Tradeable> list, string name, bool playerOwned)
        {
            foreach (var t in list)
            {
                if (!t.TraderWillTrade) continue;
                var label = (t.Label?.ToLowerInvariant() ?? "");
                var defName = (t.ThingDef?.defName?.ToLowerInvariant() ?? "");
                var nl = name.ToLowerInvariant();
                if (label.Contains(nl) || defName.Contains(nl))
                {
                    // 检查方向：playerOwned → 玩家有库存可卖；!playerOwned → 商船有库存可买
                    if (playerOwned && t.CountToTransferToSource > 0) return t;
                    if (!playerOwned && t.CountToTransferToDestination > 0) return t;
                }
            }
            return null;
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
