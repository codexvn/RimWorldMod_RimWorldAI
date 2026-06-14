using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_ManageStockpileFilter : ITool, IRequiresAdvanceTick
    {
        public string Name => "manage_stockpile_filter";
        public string Description => "修改存储区设置，通过 ops 数组批量操作。支持改优先级、添加/移除物品或分类、腐烂/新鲜/易腐开关。item 自动匹配分类或物品名（中文/defName均可）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "存储区范围内任意格 X 坐标" },
                pos_y = new { type = "integer", description = "存储区范围内任意格 Y 坐标" },
                ops = new
                {
                    type = "array",
                    description = "操作列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            op = new
                            {
                                type = "string",
                                description = "操作类型",
                                @enum = new[] { "priority", "allow", "disallow", "allow_rotten", "allow_fresh", "allow_perishable" }
                            },
                            value = new { description = "值（priority/allow_rotten/allow_fresh/allow_perishable 使用）" },
                            item = new { type = "string", description = "物品/分类名称（allow/disallow 使用），中文/defName，自动匹配分类或物品" }
                        },
                        required = new[] { "op" }
                    }
                }
            },
            required = new[] { "pos_x", "pos_y", "ops" }
        });

        private static readonly Dictionary<string, StoragePriority> PriorityMap = new()
        {
            { "low", StoragePriority.Low }, { "normal", StoragePriority.Normal },
            { "preferred", StoragePriority.Preferred }, { "important", StoragePriority.Important },
            { "critical", StoragePriority.Critical },
        };

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");
            if (!args.Value.TryGetProperty("ops", out var jOps) || jOps.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("缺少必填参数: ops（操作数组）");

            var ops = new List<(string op, JsonElement value)>();
            foreach (var jo in jOps.EnumerateArray())
            {
                if (!jo.TryGetProperty("op", out var jOp)) continue;
                ops.Add((jOp.GetString() ?? "", jo));
            }
            if (ops.Count == 0) return ToolResult.Error("ops 数组为空");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");
                    var cell = new IntVec3(posX, 0, posY);
                    if (!cell.InBounds(map))
                        return ToolResult.Error($"坐标 ({posX}, {posY}) 超出地图范围");
                    var zone = map.zoneManager.ZoneAt(cell) as Zone_Stockpile;
                    if (zone == null)
                        return ToolResult.Error($"({posX}, {posY}) 处没有存储区");

                    var filter = zone.settings.filter;
                    var results = new List<string>();

                    foreach (var (op, jo) in ops)
                    {
                        switch (op)
                        {
                            case "priority":
                            {
                                var val = jo.TryGetProperty("value", out var jv) ? jv.GetString() ?? "" : "";
                                if (PriorityMap.TryGetValue(val, out var prio))
                                {
                                    zone.settings.Priority = prio;
                                    results.Add($"优先级→{val}");
                                }
                                else results.Add($"未知优先级: {val}");
                                break;
                            }
                            case "allow":
                            case "disallow":
                            {
                                var item = jo.TryGetProperty("item", out var ji) ? ji.GetString() ?? "" : "";
                                if (string.IsNullOrEmpty(item)) { results.Add($"{op}: 缺少 item"); break; }
                                bool allow = op == "allow";
                                // 先匹配分类
                                var cat = ResolveCategory(item);
                                if (cat != null)
                                {
                                    filter.SetAllow(cat, allow);
                                    results.Add($"{(allow ? "允许" : "禁止")}分类「{cat.label}」");
                                }
                                else
                                {
                                    var def = ResolveThingDef(item);
                                    if (def != null)
                                    {
                                        filter.SetAllow(def, allow);
                                        results.Add($"{(allow ? "允许" : "禁止")}物品「{def.label}」");
                                    }
                                    else results.Add($"未找到匹配的分类或物品: {item}");
                                }
                                break;
                            }
                            case "allow_rotten":
                            case "allow_fresh":
                            {
                                var val = jo.TryGetProperty("value", out var jv) && jv.ValueKind == JsonValueKind.True;
                                var sfDef = op == "allow_rotten"
                                    ? null  // rotten = disallow AllowFresh
                                    : SpecialThingFilterDefOf.AllowFresh;
                                if (op == "allow_rotten")
                                    filter.SetAllow(SpecialThingFilterDefOf.AllowFresh, !val);
                                else
                                    filter.SetAllow(sfDef!, val);
                                results.Add($"{(val ? "允许" : "禁止")}{(op == "allow_rotten" ? "腐烂" : "新鲜")}");
                                break;
                            }
                            case "allow_perishable":
                            {
                                var val = jo.TryGetProperty("value", out var jv) && jv.ValueKind == JsonValueKind.True;
                                int count = 0;
                                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                                {
                                    if (def.HasComp(typeof(CompRottable)))
                                    {
                                        filter.SetAllow(def, val);
                                        count++;
                                    }
                                }
                                results.Add($"{(val ? "允许" : "禁止")}{count} 种易腐物品");
                                break;
                            }
                        }
                    }

                    return ToolResult.Success($"存储区 ({posX},{posY}) 已更新: {string.Join("; ", results)}");
                }
                catch (Exception ex) { return ToolResult.Error($"管理存储区失败: {ex.Message}"); }
            });
        }

        private static ThingCategoryDef? ResolveCategory(string name)
        {
            foreach (var cat in DefDatabase<ThingCategoryDef>.AllDefs)
            {
                if (string.Equals(cat.defName, name, StringComparison.OrdinalIgnoreCase)
                    || (cat.label != null && string.Equals(cat.label, name, StringComparison.OrdinalIgnoreCase)))
                    return cat;
            }
            return null;
        }

        private static ThingDef? ResolveThingDef(string name)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def != null) return def;
            foreach (var d in DefDatabase<ThingDef>.AllDefs)
            {
                if (d.label != null && string.Equals(d.label, name, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            return (posX, posY, posX, posY);
        }
    }
}
