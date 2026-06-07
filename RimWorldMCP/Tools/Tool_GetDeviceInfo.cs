using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_GetDeviceInfo : ITool
    {
        public string Name => "get_device_info";
        public string Description => "获取设备/设备组的状态、全部组件、当前 UI/Gizmo 命令和可执行 action_id。支持 thing_id、thing_ids 或坐标定位。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "设备 thingIDNumber（与 thing_ids、坐标三选一）" },
                thing_ids = new { type = "array", items = new { type = "integer" }, description = "设备 thingIDNumber 数组，模拟 RimWorld 多选 UI" },
                pos_x = new { type = "integer", description = "设备 X 坐标（与 thing_id、thing_ids 三选一）" },
                pos_y = new { type = "integer", description = "设备 Y 坐标（与 thing_id、thing_ids 三选一）" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var targets = DeviceToolHelper.ResolveTargets(map, args.Value);
                    if (targets.Things.Count == 0)
                        return ToolResult.Error("没有找到目标设备。请检查 thing_id、thing_ids 或坐标。");

                    var commands = DeviceToolHelper.GetDeviceCommands(targets.Things);
                    var sb = new StringBuilder();
                    sb.AppendLine(targets.Things.Count == 1
                        ? $"## 设备信息: {targets.Things[0].LabelCap}"
                        : $"## 设备组信息: {targets.Things.Count} 个设备");
                    sb.AppendLine($"- 定位方式: {targets.Source}");
                    sb.AppendLine("- 选择上下文: 已将目标设备设为当前选择，用于生成真实 UI/Gizmo");
                    sb.AppendLine();

                    sb.AppendLine("### 设备列表");
                    sb.AppendLine("| ID | 名称 | defName | 位置 | 派系 | 耐久 |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    foreach (var thing in targets.Things)
                        sb.AppendLine(DeviceToolHelper.FormatBasicThing(thing));
                    sb.AppendLine();

                    sb.AppendLine("### 组件状态");
                    foreach (var thing in targets.Things)
                    {
                        sb.AppendLine($"#### {DeviceToolHelper.Escape(thing.LabelCap)} (ID:{thing.thingIDNumber})");
                        DeviceToolHelper.AppendComponentInfo(sb, thing);
                        var adapters = DeviceToolHelper.GetAdapterActions(thing);
                        sb.AppendLine(adapters.Count > 0
                            ? $"- adapter 操作: {string.Join(", ", adapters.Select(a => a.id))}"
                            : "- adapter 操作: (无)");
                    }
                    sb.AppendLine();

                    sb.AppendLine("### UI/Gizmo 命令");
                    if (commands.Count == 0)
                    {
                        sb.AppendLine("(当前选择上下文没有可见 UI/Gizmo 命令)");
                    }
                    else
                    {
                        sb.AppendLine("| action_id | 类型 | 设备ID | 名称 | 状态 | 可执行 | 说明 |");
                        sb.AppendLine("|---|---|---:|---|---|---|---|");
                        foreach (var command in commands)
                        {
                            var state = command.IsActive.HasValue ? $"active={DeviceToolHelper.YesNo(command.IsActive.Value)}" : "-";
                            if (command.Disabled)
                                state = $"禁用: {DeviceToolHelper.Escape(command.DisabledReason)}";
                            sb.AppendLine($"| `{command.ActionId}`<br>`{command.StableActionId}` | {command.Kind} | {command.Thing.thingIDNumber} | {DeviceToolHelper.Escape(command.Label)} | {DeviceToolHelper.Escape(state)} | {DeviceToolHelper.YesNo(command.Executable && !command.Disabled)} | {DeviceToolHelper.Escape(command.ExecuteNote)} |");
                        }
                    }
                    sb.AppendLine();

                    sb.AppendLine("### adapter 操作说明");
                    sb.AppendLine("| action_id | 参数 | 说明 |");
                    sb.AppendLine("|---|---|---|");
                    var allAdapters = targets.Things.SelectMany(DeviceToolHelper.GetAdapterActions)
                        .GroupBy(a => a.id)
                        .Select(g => g.First())
                        .ToList();
                    if (allAdapters.Count == 0)
                    {
                        sb.AppendLine("| - | - | 当前设备无 adapter 操作，可使用上方 UI Toggle | ");
                    }
                    else
                    {
                        foreach (var action in allAdapters)
                            sb.AppendLine($"| `{action.id}` | {action.value} | {action.description} |");
                    }

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[GetDeviceInfo] 获取设备信息失败: {ex.GetType().Name}: {ex.Message}");
                    return ToolResult.Error($"获取设备信息失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
            => DeviceToolHelper.GetTargetRange(args);
    }
}
