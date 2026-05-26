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
    public class Tool_SettlementGoods : ITool
    {
        public string Name => "get_settlement_goods";
        public string Description => "列出世界定居点的缓存商品库存。首次调用触发库存生成并缓存。用于在 trade_execute 前了解可购物品。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                settlement_name = new { type = "string", description = "定居点名称（模糊匹配）" },
                page = new { type = "integer", description = "页码（1 起始），默认 1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认 20", @default = 20 }
            },
            required = new[] { "settlement_name" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return Task.FromResult(ToolResult.Error("缺少参数"));
            if (!args.Value.TryGetProperty("settlement_name", out var jSn))
                return Task.FromResult(ToolResult.Error("缺少必填参数: settlement_name"));
            string name = jSn.GetString() ?? "";
            int page = 1, pageSize = 20;
            if (args.Value.TryGetProperty("page", out var jP) && jP.TryGetInt32(out var p) && p > 0) page = p;
            if (args.Value.TryGetProperty("page_size", out var jPs) && jPs.TryGetInt32(out var ps) && ps > 0 && ps <= 50) pageSize = ps;

            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var settlement = Find.World.worldObjects.Settlements
                        .FirstOrDefault(s => s.Name.ToLowerInvariant().Contains(name.ToLowerInvariant()));
                    if (settlement == null)
                        return ToolResult.Error($"找不到定居点: {name}");

                    if (!settlement.CanTradeNow)
                        return ToolResult.Error($"{settlement.Name} 当前不可贸易");

                    var goods = settlement.Goods?.ToList();
                    if (goods == null || goods.Count == 0)
                        return ToolResult.Error($"{settlement.Name} 没有可贸易商品");

                    var sb = new StringBuilder();
                    sb.AppendLine($"## {settlement.Name} 商品库存");
                    sb.AppendLine($"- 派系: {settlement.Faction?.Name}");
                    sb.AppendLine($"- 位置: Tile {settlement.Tile}");
                    sb.AppendLine($"- TraderKind: {settlement.TraderKind?.label ?? "?"}");
                    sb.AppendLine($"- 商品: {goods.Count} 项\n");

                    // 分页
                    int total = goods.Count;
                    int totalPages = (total + pageSize - 1) / pageSize;
                    if (page > totalPages) page = totalPages;
                    var pageItems = goods.Skip((page - 1) * pageSize).Take(pageSize);

                    sb.AppendLine("| # | 物品 | 数量 | 市场价 | 分类 |");
                    sb.AppendLine("|---|------|------|--------|------|");
                    int idx = (page - 1) * pageSize;
                    foreach (var t in pageItems)
                    {
                        idx++;
                        float price = t.MarketValue;
                        string cat = t.def.FirstThingCategory?.LabelCap ?? t.def.category.ToString();
                        sb.AppendLine($"| {idx} | {t.LabelCap} | {t.stackCount} | {price * t.stackCount:F0} 银 | {cat} |");
                    }

                    sb.AppendLine($"\n第 {page}/{totalPages} 页，共 {total} 项");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"查询失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
