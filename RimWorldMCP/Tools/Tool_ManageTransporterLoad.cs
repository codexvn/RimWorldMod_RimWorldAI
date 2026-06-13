using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_ManageTransporterLoad : ITool, IRequiresAdvanceTick
    {
        public string Name => "manage_transporter_load";
        public string Description => "管理运输器/运输舱装载。首版支持 status、clear_left_to_load、cancel_load。支持 thing_id 或 thing_ids。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "运输器 thingIDNumber（与 thing_ids 二选一）" },
                thing_ids = new { type = "array", items = new { type = "integer" }, description = "运输器 thingIDNumber 数组" },
                action = new { type = "string", description = "操作: status, clear_left_to_load, cancel_load", @enum = new[] { "status", "clear_left_to_load", "cancel_load" } }
            },
            required = new[] { "action" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("action", out var jAction) || string.IsNullOrWhiteSpace(jAction.GetString()))
                return ToolResult.Error("缺少必填参数: action");

            var action = jAction.GetString()!.Trim().ToLowerInvariant();

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var targets = DeviceToolHelper.ResolveTargets(map, args.Value);
                    var missingIds = DeviceToolHelper.GetMissingThingIdsMessage(targets);
                    if (!string.IsNullOrEmpty(missingIds))
                        return ToolResult.Error(missingIds);
                    if (targets.Things.Count == 0)
                        return ToolResult.Error("没有找到目标运输器。");

                    var transporters = new List<CompTransporter>();
                    var nonTransporters = new List<Thing>();
                    foreach (var thing in targets.Things)
                    {
                        var transporter = thing.TryGetComp<CompTransporter>();
                        if (transporter == null) nonTransporters.Add(thing);
                        else transporters.Add(transporter);
                    }

                    if (nonTransporters.Count > 0)
                        return ToolResult.Error($"以下目标不是运输器: {string.Join(", ", nonTransporters.Select(t => $"ID:{t.thingIDNumber} {t.LabelCap}"))}");
                    if (transporters.Count == 0)
                        return ToolResult.Error("目标中没有运输器。");

                    DeviceToolHelper.SelectTargets(transporters.Select(t => t.parent));

                    switch (action)
                    {
                        case "status":
                            return Status(transporters);
                        case "clear_left_to_load":
                            return ClearLeftToLoad(map, transporters);
                        case "cancel_load":
                            return CancelLoad(transporters);
                        default:
                            return ToolResult.Error($"未知 action: {action}。可选: status, clear_left_to_load, cancel_load");
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[ManageTransporterLoad] 管理运输器装载失败: {ex.GetType().Name}: {ex.Message}");
                    return ToolResult.Error($"管理运输器装载失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
            => DeviceToolHelper.GetTargetRange(args);

        private static ToolResult Status(List<CompTransporter> transporters)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 运输器状态 ({transporters.Count})");
            foreach (var transporter in transporters)
            {
                sb.AppendLine($"### {transporter.parent.LabelCap} (ID:{transporter.parent.thingIDNumber})");
                sb.Append(DeviceToolHelper.FormatTransporterDetails(transporter));
                sb.AppendLine();
            }
            return ToolResult.Success(sb.ToString());
        }

        private static ToolResult ClearLeftToLoad(Map map, List<CompTransporter> transporters)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 已清空运输器待装载清单");
            foreach (var transporter in transporters)
            {
                var before = transporter.leftToLoad?.Where(t => t.CountToTransfer > 0).Sum(t => t.CountToTransfer) ?? 0;
                DeviceToolHelper.InterruptTransporterHaulers(map, transporter.groupID);
                transporter.leftToLoad?.Clear();
                sb.AppendLine($"- ID:{transporter.parent.thingIDNumber} {transporter.parent.LabelCap}: 清空 {before} 个待装载数量（已装载物保留在容器内）");
            }
            return ToolResult.Success(sb.ToString());
        }

        private static ToolResult CancelLoad(List<CompTransporter> transporters)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 已取消运输器装载");
            sb.AppendLine("注意：原版 CancelLoad 会将已装载内容丢出到运输器附近，并清空 leftToLoad。 ");
            foreach (var transporter in transporters)
            {
                var hadLoad = transporter.LoadingInProgressOrReadyToLaunch;
                var contained = transporter.innerContainer?.Count ?? 0;
                var canceled = transporter.CancelLoad();
                sb.AppendLine($"- ID:{transporter.parent.thingIDNumber} {transporter.parent.LabelCap}: 原状态={DeviceToolHelper.YesNo(hadLoad)}，容器物品={contained}，取消结果={DeviceToolHelper.YesNo(canceled)}");
            }
            return ToolResult.Success(sb.ToString());
        }
    }
}
