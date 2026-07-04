using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 获取物品/建筑/装备/武器的完整属性面板 — 复用游戏 StatsReportUtility 统计管线，
    /// 与游戏内 "i" 信息卡输出完全一致。
    /// </summary>
    public class Tool_GetThingStats : ITool
    {
        public string Name => "get_thing_stats";
        public string Description => "获取指定物品的完整属性面板（与游戏内 'i' 信息卡一致）。支持武器/装备/建筑/物品等任意 Thing。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "物品 thingIDNumber（来自 get_tile_detail / search_map）" }
            },
            required = new[] { "thing_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("thing_id", out var jTid) || !jTid.TryGetInt32(out var thingId))
                return ToolResult.Error("缺少必填参数: thing_id");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    // 查找物品：先搜全图
                    Thing thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId)
                        ?? (Thing)map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == thingId);

                    if (thing == null)
                        return ToolResult.Error($"找不到 ID 为 {thingId} 的物品。");

                    // 如果是微缩建筑，展开为内部物品
                    if (thing is MinifiedThing minified)
                        thing = minified.InnerThing;

                    var sb = new StringBuilder();
                    var label = thing.LabelCap ?? thing.def?.label ?? "???";
                    var defName = thing.def?.defName ?? "???";

                    // 品质
                    var quality = thing.TryGetComp<CompQuality>();
                    var qualityText = quality != null ? $" ({quality.Quality.GetLabel()})" : "";

                    sb.AppendLine($"## {label}{qualityText}");
                    sb.AppendLine($"defName: `{defName}` | 类别: {thing.def?.category.ToStringSafe() ?? "?"} | ID: {thing.thingIDNumber}");
                    sb.AppendLine();

                    // 复用游戏 StatsReportUtility 逻辑：遍历全部 StatDef，收集应显示的属性
                    var req = StatRequest.For(thing);
                    var entries = new List<StatDrawEntry>();

                    foreach (StatDef st in DefDatabase<StatDef>.AllDefs)
                    {
                        try
                        {
                            if (!st.Worker.ShouldShowFor(req)) continue;
                            if (st.Worker.IsDisabledFor(thing)) continue;

                            float val = thing.GetStatValue(st, true, -1);
                            if (!st.showOnDefaultValue)
                            {
                                // 跳过等于默认值的属性（与 UI 行为一致）
                                float baseVal = thing.def.GetStatValueAbstract(st, null);
                                if (Math.Abs(val - baseVal) < 0.0001f) continue;
                            }

                            entries.Add(new StatDrawEntry(st.category, st, val, req));
                        }
                        catch (Exception ex)
                        {
                            // 个别属性计算异常不阻塞整体
                            McpLog.Warn($"[get_thing_stats] {st.defName} 计算失败: {ex.Message}");
                        }
                    }

                    // 按分类分组（手动分组避免 LINQ 排序比较异常）
                    var grouped = new Dictionary<string, List<StatDrawEntry>>();
                    foreach (var entry in entries)
                    {
                        var catName = entry.category?.LabelCap ?? "其他";
                        if (!grouped.ContainsKey(catName))
                            grouped[catName] = new List<StatDrawEntry>();
                        grouped[catName].Add(entry);
                    }

                    foreach (var kv in grouped.OrderBy(kv => kv.Key))
                    {
                        sb.AppendLine($"### {kv.Key}");
                        sb.AppendLine();

                        foreach (var entry in kv.Value)
                        {
                            sb.AppendLine($"- **{entry.LabelCap}**: {entry.ValueString}");
                        }
                        sb.AppendLine();
                    }

                    if (entries.Count == 0)
                        sb.AppendLine("（无可用属性）");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"获取属性失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
