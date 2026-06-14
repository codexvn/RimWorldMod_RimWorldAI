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
    public class Tool_CheckStockpileFor : ITool
    {
        public string Name => "check_stockpile_for";
        public string Description => "检查指定物品符合哪些存储区的条件。输入物品名称（中文/defName），列出地图上所有存储区的允许状态和优先级。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                item = new { type = "string", description = "物品名称，中文 label 或 defName 均可，如 \"钢铁\" 或 \"Steel\"" }
            },
            required = new[] { "item" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("item", out var jItem) || string.IsNullOrEmpty(jItem.GetString()))
                return ToolResult.Error("缺少必填参数: item");

            var itemName = jItem.GetString()!;

            return await Task.Run(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。请先加载游戏存档。");

                    // 解析物品
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(itemName);
                    if (def == null)
                    {
                        foreach (var d in DefDatabase<ThingDef>.AllDefs)
                        {
                            if (d.label != null && string.Equals(d.label, itemName, StringComparison.OrdinalIgnoreCase))
                            { def = d; break; }
                        }
                    }
                    if (def == null)
                        return ToolResult.Error($"未找到物品: {itemName}。请使用 search_thing_def 查询可用物品名。");

                    // 遍历所有存储区
                    var zones = map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList();
                    if (zones.Count == 0)
                        return ToolResult.Success($"地图上没有存储区。{def.label} ({def.defName}) 无处可存——请先用 create_stockpile 创建。");

                    var sb = new StringBuilder();
                    sb.AppendLine($"物品: {def.label} ({def.defName})");
                    sb.AppendLine("──────────────────────────");
                    int acceptsCount = 0;

                    foreach (var zone in zones.OrderByDescending(z => (int)z.settings.Priority))
                    {
                        var cells = zone.Cells.ToList();
                        int minX = cells.Min(c => c.x), maxX = cells.Max(c => c.x);
                        int minZ = cells.Min(c => c.z), maxZ = cells.Max(c => c.z);
                        bool accepts = zone.settings.AllowedToAccept(def);
                        if (accepts) acceptsCount++;

                        var prio = zone.settings.Priority switch
                        {
                            StoragePriority.Low => "低",
                            StoragePriority.Normal => "普通",
                            StoragePriority.Preferred => "优选",
                            StoragePriority.Important => "重要",
                            StoragePriority.Critical => "关键",
                            _ => "?"
                        };

                        sb.AppendLine($"{(accepts ? "✔" : "✘")} ({minX},{minZ})-({maxX},{maxZ}) | 优先级={prio}");
                    }

                    if (acceptsCount == 0)
                        sb.AppendLine($"\n⚠ 所有 {zones.Count} 个存储区都不接收 {def.label}，请用 manage_stockpile_filter 调整筛选条件或 create_stockpile 新建。");
                    else
                        sb.AppendLine($"\n{acceptsCount}/{zones.Count} 个存储区可接收");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"查询失败: {ex.Message}"); }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
