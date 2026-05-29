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
        public string Description => "派系级虚空交易。传 sell/buy 执行交易，不传则仅预览生成的货物。先用 list_faction_traders 查看可用派系和类型。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                faction_name = new { type = "string", description = "派系名称" },
                trader_kind = new { type = "string", description = "商船类型（可选，不传用第一个），如 作战物资商/奴隶商" },
                sell = new
                {
                    type = "array",
                    description = "出售物品列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            item = new { type = "string", description = "物品名称（模糊匹配 Label/defName）" },
                            count = new { type = "integer", description = "数量" }
                        }
                    }
                },
                buy = new
                {
                    type = "array",
                    description = "购买物品列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            item = new { type = "string", description = "物品名称（模糊匹配）" },
                            count = new { type = "integer", description = "数量" }
                        }
                    }
                }
            },
            required = new[] { "faction_name" }
        });

        private static readonly PropertyInfo _costForSource = typeof(Tradeable)
            .GetProperty("CurTotalCurrencyCostForSource")!;
        private static readonly PropertyInfo _allTradeablesProp = typeof(TradeDeal)
            .GetProperty("AllTradeables")!;

        // 虚拟商船缓存：key = "factionName|traderKindDefName"，TTL 15 秒实时（约 1 游戏小时/超快）
        private static readonly Dictionary<string, (TradeShip ship, int cachedTick)> _vcache = new();
        private const int CacheTTLTicks = 37500; // 约 15 秒实时 = 1 游戏小时

        /// <summary>获取或创建缓存的虚拟商船，TTL 内复用避免重复生成</summary>
        private static TradeShip GetOrCreateVirtualShip(Map map, string key, TraderKindDef kind, Faction faction)
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (_vcache.TryGetValue(key, out var entry))
            {
                if (now - entry.cachedTick < CacheTTLTicks && !entry.ship.Departed)
                {
                    _vcache[key] = (entry.ship, now); // 刷新 tick 防过期
                    return entry.ship;
                }
                // 过期 → 清理旧船
                entry.ship.Depart();
                map.passingShipManager.RemoveShip(entry.ship);
                _vcache.Remove(key);
            }
            var ship = new TradeShip(kind, faction);
            ship.GenerateThings();
            map.passingShipManager.AddShip(ship);
            _vcache[key] = (ship, now);
            return ship;
        }

        /// <summary>清理所有缓存的虚拟商船</summary>
        public static void ClearCache()
        {
            foreach (var kv in _vcache)
            {
                kv.Value.ship.Depart();
                Find.CurrentMap?.passingShipManager.RemoveShip(kv.Value.ship);
            }
            _vcache.Clear();
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("faction_name", out var jFn))
                return ToolResult.Error("缺少必填参数: faction_name");
            string factionName = jFn.GetString() ?? "";

            string traderKindFilter = "";
            if (args.Value.TryGetProperty("trader_kind", out var jTk))
                traderKindFilter = jTk.GetString() ?? "";

            var sellList = ParseItemList(args.Value, "sell");
            var buyList = ParseItemList(args.Value, "buy");
            bool previewOnly = sellList.Count == 0 && buyList.Count == 0;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                TradeShip? virtShip = null;
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    var faction = Find.FactionManager.AllFactionsVisible
                        .FirstOrDefault(f => f.Name.ToLowerInvariant().Contains(factionName.ToLowerInvariant())
                            && !f.IsPlayer && !f.temporary);
                    if (faction == null)
                        return ToolResult.Error($"找不到派系: {factionName}");
                    if (faction.PlayerRelationKind == FactionRelationKind.Hostile)
                        return ToolResult.Error($"{faction.Name} 与你敌对，无法贸易");

                    var allKinds = (faction.def.baseTraderKinds ?? new List<TraderKindDef>()).ToList();
                    if (allKinds.Count == 0)
                        return ToolResult.Error($"{faction.Name} 没有可用的非皇家商船类型");

                    TraderKindDef traderKind;
                    if (!string.IsNullOrWhiteSpace(traderKindFilter))
                    {
                        traderKind = allKinds.FirstOrDefault(k =>
                            (k.label?.ToLowerInvariant().Contains(traderKindFilter.ToLowerInvariant()) ?? false)
                            || (k.defName?.ToLowerInvariant().Contains(traderKindFilter.ToLowerInvariant()) ?? false));
                        if (traderKind == null)
                            return ToolResult.Error($"找不到 '{traderKindFilter}'。可用: {string.Join(", ", allKinds.Select(k => k.label))}");
                    }
                    else
                    {
                        traderKind = allKinds[0];
                    }

                    var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .FirstOrDefault(p => p.health.capacities.CapableOf(PawnCapacityDefOf.Talking));
                    if (pawn == null) return ToolResult.Error("没有能说话的殖民者");

                    // 虚拟商船：从缓存获取或新建，TTL 内同一批货不变
                    string cacheKey = $"{faction.def.defName}|{traderKind.defName}";
                    virtShip = GetOrCreateVirtualShip(map, cacheKey, traderKind, faction);

                    TradeSession.trader = virtShip;
                    TradeSession.playerNegotiator = pawn;
                    var deal = new TradeDeal();
                    var tradeables = (List<Tradeable>)_allTradeablesProp.GetValue(deal);

                    if (previewOnly)
                    {
                        var preview = new StringBuilder();
                        preview.AppendLine($"预览: {faction.Name} / {traderKind.label}");
                        preview.AppendLine($"缓存 TTL: {CacheTTLTicks / 2500:F1} 游戏小时\n");

                        int shown = 0;
                        foreach (var t in tradeables.OrderByDescending(t => t.AnyThing?.MarketValue ?? 0f))
                        {
                            if (t.IsCurrency) continue;
                            if (t.CountToTransferToDestination <= 0) continue;
                            shown++;
                            float price = t.AnyThing?.MarketValue ?? 0f;
                            preview.AppendLine($"- {t.Label} x{t.CountToTransferToDestination} ({price * t.CountToTransferToDestination:F0} 银)");
                        }
                        if (shown == 0)
                            preview.AppendLine("（该商船类型无可购商品——可能不交易实物，或是纯收购型）");

                        preview.AppendLine($"\n用 trade_execute(faction_name, trader_kind, buy/sell) 执行交易。缓存期内同批货不变。");
                        return ToolResult.Success(preview.ToString().TrimEnd());
                    }

                    var log = new StringBuilder();
                    log.AppendLine($"交易: {faction.Name} / {traderKind.label}");
                    int sellValue = 0, buyValue = 0;

                    foreach (var (itemName, count) in sellList)
                    {
                        var t = FindTradeable(tradeables, itemName, true);
                        if (t == null) { log.AppendLine($"- 找不到可售物品: {itemName}"); continue; }
                        int actual = Math.Min(count, t.CountToTransferToSource);
                        if (actual <= 0) { log.AppendLine($"- {itemName}: 无可售库存"); continue; }
                        t.ForceToSource(actual);
                        int v = (int)Math.Round((float)_costForSource.GetValue(t));
                        sellValue += v;
                        log.AppendLine($"- 售 {t.Label} x{actual} → +{v} 银");
                    }

                    foreach (var (itemName, count) in buyList)
                    {
                        var t = FindTradeable(tradeables, itemName, false);
                        if (t == null) { log.AppendLine($"- 商船没有: {itemName}"); continue; }
                        int actual = Math.Min(count, t.CountToTransferToDestination);
                        if (actual <= 0) { log.AppendLine($"- {itemName}: 库存不足"); continue; }
                        t.ForceToDestination(actual);
                        int v = (int)Math.Round((float)_costForSource.GetValue(t));
                        buyValue += Math.Abs(v);
                        log.AppendLine($"- 购 {t.Label} x{actual} → -{Math.Abs(v)} 银");
                    }

                    log.AppendLine($"净额: {sellValue - buyValue:+0;-#} 银");

                    if (sellValue == 0 && buyValue == 0)
                        return ToolResult.Error("没有成功匹配到任何物品");

                    if (!deal.TryExecute(out bool actuallyTraded))
                        return ToolResult.Error("交易执行失败");
                    if (!actuallyTraded)
                        return ToolResult.Error("交易未产生实际交换（可能资金不足）");

                    return ToolResult.Success(log.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"交易失败: {ex.Message}"); }
                finally
                {
                    TradeSession.trader = null!;
                    TradeSession.playerNegotiator = null!;
                    // 缓存的商船不销毁，留给后续复用
                }
            });
        }

        private static List<(string item, int count)> ParseItemList(JsonElement root, string key)
        {
            var list = new List<(string, int)>();
            if (root.TryGetProperty(key, out var jArr) && jArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in jArr.EnumerateArray())
                {
                    string s = el.TryGetProperty("item", out var i) ? i.GetString() ?? "" : "";
                    int c = el.TryGetProperty("count", out var ct) ? ct.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(s) && c > 0) list.Add((s, c));
                }
            }
            return list;
        }

        private static Tradeable? FindTradeable(List<Tradeable> list, string name, bool playerOwned)
        {
            foreach (var t in list)
            {
                if (!t.TraderWillTrade) continue;
                var label = (t.Label?.ToLowerInvariant() ?? "");
                var defName = (t.ThingDef?.defName?.ToLowerInvariant() ?? "");
                var nl = name.ToLowerInvariant();
                if (!label.Contains(nl) && !defName.Contains(nl)) continue;
                if (playerOwned && t.CountToTransferToSource > 0) return t;
                if (!playerOwned && t.CountToTransferToDestination > 0) return t;
            }
            return null;
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
