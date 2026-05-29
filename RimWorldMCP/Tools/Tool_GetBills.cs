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
    public class Tool_GetBills : ITool
    {
        public string Name => "get_bills";
        public string Description => "查看当前所有制造工作单的状态。返回中每个工作单前的方括号数字是 bill_index，供 manage_bill 使用。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                workbench_filter = new { type = "string", description = "按工作台类型 defName 或名称过滤" },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认10，最大50", @default = 10 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var filter = "";
            if (args != null && args.Value.TryGetProperty("workbench_filter", out var f))
                filter = f.GetString() ?? "";

            int page = 1, pageSize = 10;
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(jps.GetInt32(), 50));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null)
                    return ToolResult.Error("当前没有可用地图。");

                var tables = map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()
                    .OrderBy(t => t.def?.defName ?? "").ThenBy(t => t.thingIDNumber).ToList();
                if (tables.Count == 0)
                    return ToolResult.Success("当前殖民地没有任何工作台。");

                // 先收集所有匹配的工作单（扁平列表）
                var billEntries = new List<(Building_WorkTable table, int globalIndex, Bill bill)>();
                int idx = 0;

                foreach (var table in tables)
                {
                    var tableLabel = table.def?.label ?? table.def?.defName ?? "???";
                    var tableDefName = table.def?.defName ?? "???";

                    var bills = table.billStack?.Bills;
                    if (bills == null || bills.Count == 0)
                        continue;

                    // 按工作台过滤
                    if (!string.IsNullOrEmpty(filter))
                    {
                        if (tableLabel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            tableDefName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }

                    foreach (var bill in bills)
                    {
                        billEntries.Add((table, idx, bill));
                        idx++;
                    }
                }

                if (billEntries.Count == 0)
                {
                    if (!string.IsNullOrEmpty(filter))
                        return ToolResult.Success($"没有匹配过滤条件 \"{filter}\" 的工作单。");
                    else
                        return ToolResult.Success("当前没有工作单。");
                }

                int totalBills = billEntries.Count;
                var pagedBills = billEntries.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                var groupedByTable = pagedBills.GroupBy(e => e.table.thingIDNumber);

                var sb = new StringBuilder();
                sb.AppendLine("## 当前工作单");
                sb.AppendLine();

                foreach (var group in groupedByTable)
                {
                    var first = group.First();
                    var table = first.table;
                    var tableLabel = table.def?.label ?? table.def?.defName ?? "???";
                    var tableDefName = table.def?.defName ?? "???";

                    sb.AppendLine($"### {tableLabel} (`{tableDefName}`)");
                    sb.AppendLine();
                    sb.AppendLine("| 索引 | 单据 | 模式 | 详情 | 状态 |");
                    sb.AppendLine("|------|------|------|------|------|");

                    foreach (var entry in group)
                    {
                        var bill = entry.bill;
                        var globalIdx = entry.globalIndex;
                        var label = bill.Label ?? "???";
                        var suspended = bill.suspended;
                        var statusText = suspended ? "暂停" : "运行中";

                        var modeText = "-";
                        var detailText = "-";

                        if (bill is Bill_Production prod)
                        {
                            if (prod.repeatMode == BillRepeatModeDefOf.Forever)
                            {
                                modeText = "永久重复";
                            }
                            else if (prod.repeatMode == BillRepeatModeDefOf.RepeatCount)
                            {
                                modeText = "重复指定次数";
                                var target = prod.targetCount;
                                var current = prod.repeatCount;
                                detailText = $"目标 {target} 次, 已完成 {current} 次";
                            }
                            else if (prod.repeatMode == BillRepeatModeDefOf.TargetCount)
                            {
                                modeText = "目标数量";
                                var target = prod.targetCount;
                                detailText = $"目标 {target} 件";
                            }
                            else
                            {
                                modeText = prod.repeatMode?.label ?? "单次";
                            }

                            if (prod.paused)
                                statusText = "已暂停(手动)";
                        }

                        sb.AppendLine($"| [{globalIdx}] | {label} | {modeText} | {detailText} | {statusText} |");
                    }

                    sb.AppendLine();
                }

                sb.AppendLine($"**统计**: 共 {totalBills} 个工作单");

                int totalPages = (int)Math.Ceiling((double)totalBills / pageSize);
                if (totalBills > pageSize)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.Append($"第 {page}/{totalPages} 页，共 {totalBills} 条");
                    if (page < totalPages) sb.Append($" | page={page + 1} 下一页");
                    if (page > 1) sb.Append($" | page={page - 1} 上一页");
                    sb.AppendLine();
                }

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
