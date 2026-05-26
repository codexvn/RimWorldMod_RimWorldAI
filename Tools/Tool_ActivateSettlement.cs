using System;
using System.Collections.Generic;
using System.Linq;
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
        public string Description => "激活定居点的商品库存缓存。首次调用触发生成（RegenerateStock），之后反复读取不重复生成。支持按名称激活或从殖民地按距离扩散。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                settlement_name = new { type = "string", description = "定居点名称（与 faction_name / radius 三选一）" },
                faction_name = new { type = "string", description = "派系名称（激活该派系所有定居点，与 settlement_name / radius 三选一）" },
                radius = new { type = "integer", description = "距殖民地半径（tile，与 settlement_name / faction_name 三选一，激活范围内所有定居点）" },
                max_count = new { type = "integer", description = "最大激活数量（配合 radius 使用），默认 10", @default = 10 }
            }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string? sn = null, fn = null;
            int radius = 0, maxCount = 10;
            if (args != null)
            {
                if (args.Value.TryGetProperty("settlement_name", out var jSn))
                    sn = jSn.GetString();
                if (args.Value.TryGetProperty("faction_name", out var jFn))
                    fn = jFn.GetString();
                if (args.Value.TryGetProperty("radius", out var jR) && jR.TryGetInt32(out var r))
                    radius = r;
                if (args.Value.TryGetProperty("max_count", out var jMc) && jMc.TryGetInt32(out var mc) && mc > 0)
                    maxCount = mc;
            }

            if (string.IsNullOrWhiteSpace(sn) && string.IsNullOrWhiteSpace(fn) && radius <= 0)
                return Task.FromResult(ToolResult.Error("需要 settlement_name / faction_name / radius 之一"));

            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var settlements = Find.World.worldObjects.Settlements
                        .Where(s => s.CanTradeNow)
                        .ToList();

                    IEnumerable<Settlement> targets;

                    if (!string.IsNullOrWhiteSpace(sn))
                    {
                        targets = settlements.Where(s => s.Name.ToLowerInvariant().Contains(sn.ToLowerInvariant())).ToList();
                        if (!targets.Any())
                            return ToolResult.Error($"找不到定居点: {sn}");
                    }
                    else if (!string.IsNullOrWhiteSpace(fn))
                    {
                        var faction = Find.FactionManager.AllFactionsVisible
                            .FirstOrDefault(f => f.Name.ToLowerInvariant().Contains(fn.ToLowerInvariant()) && !f.IsPlayer && !f.temporary);
                        if (faction == null) return ToolResult.Error($"找不到派系: {fn}");
                        targets = settlements.Where(s => s.Faction == faction).ToList();
                    }
                    else
                    {
                        int homeTile = Find.CurrentMap?.Tile ?? -1;
                        if (homeTile < 0) return ToolResult.Error("当前无殖民地地图");
                        int capR = radius;
                        targets = settlements
                            .Where(s => Find.WorldGrid.TraversalDistanceBetween(homeTile, s.Tile, false, capR) <= capR)
                            .OrderBy(s => Find.WorldGrid.TraversalDistanceBetween(homeTile, s.Tile, false, int.MaxValue))
                            .Take(maxCount)
                            .ToList();
                    }

                    if (!targets.Any()) return ToolResult.Error("没有匹配的定居点");

                    var sb = new StringBuilder();
                    int activated = 0;
                    foreach (var s in targets)
                    {
                        var goods = s.Goods?.ToList(); // 触发生成
                        if (goods != null && goods.Count > 0)
                        {
                            int kinds = goods.Select(t => t.def).Distinct().Count();
                            int total = goods.Sum(t => t.stackCount);
                            sb.AppendLine($"- {s.Name} ({s.Faction?.Name}): {goods.Count} 项, {kinds} 种, {total} 件");
                            activated++;
                        }
                        else
                        {
                            sb.AppendLine($"- {s.Name}: 无商品");
                        }
                    }

                    sb.Insert(0, $"已激活 {activated}/{targets.Count()} 个定居点:\n");
                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"激活失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
