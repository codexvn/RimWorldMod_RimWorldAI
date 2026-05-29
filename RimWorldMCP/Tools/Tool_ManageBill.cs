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
    public class Tool_ManageBill : ITool
    {
        public string Name => "manage_bill";
        public string Description => "管理现有的制造工作单：暂停、恢复、删除、提高/降低优先级。bill_index 从 get_bills 的输出中方括号内获取。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                bill_index = new { type = "integer", description = "工作单索引" },
                action = new { type = "string", description = "操作类型", @enum = new[] { "pause", "resume", "delete", "increase_priority", "decrease_priority" } }
            },
            required = new[] { "bill_index", "action" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("bill_index", out var idx) || !idx.TryGetInt32(out var billIndex))
                return ToolResult.Error("缺少或无效的 bill_index");
            if (!args.Value.TryGetProperty("action", out var act))
                return ToolResult.Error("缺少 action");

            var action = act.GetString() ?? "";
            if (billIndex < 0)
                return ToolResult.Error("bill_index 不能为负数。");

            var validActions = new HashSet<string> { "pause", "resume", "delete", "increase_priority", "decrease_priority" };
            if (!validActions.Contains(action))
                return ToolResult.Error($"未知操作: {action}。可用: {string.Join(", ", validActions)}");

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("当前没有可用地图。");

                    var tables = map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>().ToList();
                    if (tables.Count == 0)
                        return ToolResult.Error("当前殖民地没有任何工作台。");

                    // 建立全局索引 -> (工作台, 单据在本工作台的索引) 映射
                    var globalIndex = 0;
                    Building_WorkTable? foundTable = null;
                    int foundLocalIndex = -1;
                    Bill? foundBill = null;

                    foreach (var table in tables)
                    {
                        var bills = table.billStack?.Bills;
                        if (bills == null || bills.Count == 0)
                            continue;

                        for (int i = 0; i < bills.Count; i++)
                        {
                            if (globalIndex == billIndex)
                            {
                                foundTable = table;
                                foundLocalIndex = i;
                                foundBill = bills[i];
                                break;
                            }
                            globalIndex++;
                        }

                        if (foundTable != null)
                            break;
                    }

                    if (foundTable == null || foundBill == null)
                        return ToolResult.Error($"未找到工作单 [{billIndex}]。总单据数: {globalIndex}。请用 get_bills 确认。");

                    var tableLabel = foundTable.def?.label ?? foundTable.def?.defName ?? "工作台";
                    var billLabel = foundBill.Label ?? "???";

                    // 执行操作
                    switch (action)
                    {
                        case "pause":
                            foundBill.suspended = true;
                            break;
                        case "resume":
                            foundBill.suspended = false;
                            break;
                        case "delete":
                            foundTable!.billStack!.Delete(foundBill);
                            break;
                        case "increase_priority":
                            foundTable!.billStack!.Reorder(foundBill, -1);
                            break;
                        case "decrease_priority":
                            foundTable!.billStack!.Reorder(foundBill, +1);
                            break;
                    }

                    var actionText = action switch
                    {
                        "pause" => "已暂停",
                        "resume" => "已恢复",
                        "delete" => "已删除",
                        "increase_priority" => "优先级已提高",
                        "decrease_priority" => "优先级已降低",
                        _ => action
                    };

                    var sb = new StringBuilder();
                    sb.AppendLine($"{actionText}工作单 [{billIndex}]");
                    sb.AppendLine($"- 工作台: {tableLabel}");
                    sb.AppendLine($"- 单据: {billLabel}");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"管理工作单失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
