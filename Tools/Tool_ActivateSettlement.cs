using System;
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
    public class Tool_ActivateSettlement : ITool
    {
        public string Name => "activate_settlement_goods";
        public string Description => "激活定居点商品库存缓存。无参数时激活最近未激活定居点。传 deactivate 可删除缓存释放内存，下次访问会重新生成。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                settlement_name = new { type = "string", description = "定居点名称（可选）" },
                faction_name = new { type = "string", description = "派系名称（可选）" },
                deactivate = new { type = "boolean", description = "设为 true 删除缓存而非激活（释放内存）", @default = false }
            }
        });

        private static readonly FieldInfo _stockField = typeof(Settlement_TraderTracker)
            .GetField("stock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("stock field not found");

        private static bool IsActivated(Settlement s)
        {
            try
            {
                var trader = s.trader;
                if (trader == null) return false;
                return (_stockField.GetValue(trader) as ThingOwner<Thing>)?.Count > 0;
            }
            catch { return false; }
        }

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string? sn = null, fn = null;
            bool deactivate = false;
            if (args != null)
            {
                if (args.Value.TryGetProperty("settlement_name", out var jSn))
                    sn = jSn.GetString();
                if (args.Value.TryGetProperty("faction_name", out var jFn))
                    fn = jFn.GetString();
                if (args.Value.TryGetProperty("deactivate", out var jDe) && jDe.ValueKind == JsonValueKind.True)
                    deactivate = true;
            }

            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var all = Find.World.worldObjects.Settlements
                        .Where(s => s.CanTradeNow)
                        .ToList();

                    // 删除缓存
                    if (deactivate)
                    {
                        if (!string.IsNullOrWhiteSpace(sn))
                        {
                            var t = all.FirstOrDefault(s => s.Name.ToLowerInvariant().Contains(sn.ToLowerInvariant()));
                            if (t == null) return ToolResult.Error($"找不到定居点: {sn}");
                            if (!IsActivated(t)) return ToolResult.Success($"{t.Name} 没有缓存，无需删除");
                            t.trader.TryDestroyStock();
                            return ToolResult.Success($"{t.Name} 缓存已删除");
                        }
                        var cached = all.Where(IsActivated).ToList();
                        if (cached.Count == 0) return ToolResult.Success("没有已缓存的定居点");
                        var nearest = cached.OrderBy(s =>
                            Find.WorldGrid.TraversalDistanceBetween(Find.CurrentMap?.Tile ?? 0, s.Tile, false, int.MaxValue))
                            .First();
                        nearest.trader.TryDestroyStock();
                        return ToolResult.Success($"{nearest.Name} 缓存已删除（共 {cached.Count} 个已缓存定居点，最近优先）");
                    }

                    Settlement? target;
                    string info;

                    if (!string.IsNullOrWhiteSpace(sn))
                    {
                        // 指定定居点：强制激活（即使已激活）
                        target = all.FirstOrDefault(s => s.Name.ToLowerInvariant().Contains(sn.ToLowerInvariant()));
                        if (target == null)
                            return ToolResult.Error($"找不到定居点: {sn}");
                        info = IsActivated(target) ? "（已激活，刷新库存）" : "";
                    }
                    else
                    {
                        // 只找未激活的
                        var candidates = all.Where(s => !IsActivated(s));

                        if (!string.IsNullOrWhiteSpace(fn))
                        {
                            var faction = Find.FactionManager.AllFactionsVisible
                                .FirstOrDefault(f => f.Name.ToLowerInvariant().Contains(fn.ToLowerInvariant()) && !f.IsPlayer && !f.temporary);
                            if (faction == null) return ToolResult.Error($"找不到派系: {fn}");
                            candidates = candidates.Where(s => s.Faction == faction);
                        }

                        int homeTile = Find.CurrentMap?.Tile ?? -1;
                        if (homeTile < 0)
                            target = candidates.FirstOrDefault();
                        else
                            target = candidates.OrderBy(s =>
                                Find.WorldGrid.TraversalDistanceBetween(homeTile, s.Tile, false, int.MaxValue))
                                .FirstOrDefault();

                        if (target == null)
                        {
                            string scope = string.IsNullOrWhiteSpace(fn) ? "殖民地周围" : fn;
                            return ToolResult.Success($"没有未激活的定居点（{scope}）。用 get_faction_relations 查看已激活列表。");
                        }
                        info = "";
                    }

                    var goods = target.Goods?.ToList(); // 触发生成
                    if (goods == null || goods.Count == 0)
                        return ToolResult.Error($"{target.Name} 激活后无商品");

                    int kinds = goods.Select(t => t.def).Distinct().Count();
                    int total = goods.Sum(t => t.stackCount);
                    var sb = new StringBuilder();
                    sb.AppendLine($"{target.Name} ({target.Faction?.Name}) 库存已激活{info}:");
                    sb.AppendLine($"- {goods.Count} 项, {kinds} 种, {total} 件");
                    sb.AppendLine($"- TraderKind: {target.TraderKind?.label ?? "?"}");
                    sb.AppendLine($"- 用 get_settlement_goods(settlement_name: \"{target.Name}\") 查看详情");
                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"激活失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
