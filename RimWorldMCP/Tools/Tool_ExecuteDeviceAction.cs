using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_ExecuteDeviceAction : ITool
    {
        public string Name => "execute_device_action";
        public string Description => "对设备/设备组执行 action_id。先调用 get_device_info 获取 UI/Gizmo action_id 或 adapter action_id。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "设备 thingIDNumber（与 thing_ids、坐标三选一）" },
                thing_ids = new { type = "array", items = new { type = "integer" }, description = "设备 thingIDNumber 数组，模拟 RimWorld 多选 UI" },
                pos_x = new { type = "integer", description = "设备 X 坐标（与 thing_id、thing_ids 三选一）" },
                pos_y = new { type = "integer", description = "设备 Y 坐标（与 thing_id、thing_ids 三选一）" },
                action_id = new { type = "string", description = "操作ID，来自 get_device_info。支持 ui_toggle:N、稳定 UI action_id 或 adapter action_id" },
                value = new { description = "操作参数：boolean 或 number。无参数操作可省略" },
                force_partial = new { type = "boolean", description = "批量操作时允许跳过不支持 action 的设备，默认 false", @default = false }
            },
            required = new[] { "action_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("action_id", out var jAction) || string.IsNullOrWhiteSpace(jAction.GetString()))
                return ToolResult.Error("缺少必填参数: action_id");

            var actionId = jAction.GetString()!.Trim();

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var targets = DeviceToolHelper.ResolveTargets(map, args.Value);
                    if (targets.Things.Count == 0)
                        return ToolResult.Error("没有找到目标设备。请检查 thing_id、thing_ids 或坐标。");

                    if (actionId.StartsWith("ui_toggle:", StringComparison.OrdinalIgnoreCase))
                        return ExecuteUiToggle(targets.Things, actionId, args.Value);
                    if (actionId.StartsWith("ui_action:", StringComparison.OrdinalIgnoreCase))
                        return ExecuteUiAction(targets.Things, actionId);

                    return ExecuteAdapter(targets, actionId, args.Value);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[ExecuteDeviceAction] 执行设备操作失败: {ex.GetType().Name}: {ex.Message}");
                    return ToolResult.Error($"执行设备操作失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
            => DeviceToolHelper.GetTargetRange(args);

        private static ToolResult ExecuteUiToggle(System.Collections.Generic.List<Thing> things, string actionId, JsonElement args)
        {
            var commands = DeviceToolHelper.GetDeviceCommands(things);
            var command = commands.FirstOrDefault(c => DeviceToolHelper.MatchesActionId(c, actionId));
            if (command == null) return ToolResult.Error($"找不到 UI Toggle: {actionId}。请重新调用 get_device_info 获取最新 action_id。");
            if (command.Disabled) return ToolResult.Error($"UI Toggle 已禁用: {command.DisabledReason}");
            if (command.Gizmo is not Command_Toggle toggle) return ToolResult.Error($"{actionId} 不是 Command_Toggle。");

            bool? target = null;
            if (DeviceToolHelper.TryGetBool(args, out var boolValue)) target = boolValue;
            var before = SafeIsActive(toggle);
            if (!target.HasValue || before != target.Value)
                toggle.toggleAction();
            var after = SafeIsActive(toggle);

            var sb = new StringBuilder();
            sb.AppendLine("## UI Toggle 执行完成");
            sb.AppendLine($"- 设备: {command.Thing.LabelCap} (ID:{command.Thing.thingIDNumber})");
            sb.AppendLine($"- 操作: {command.Label}");
            sb.AppendLine($"- 状态: {DeviceToolHelper.YesNo(before)} → {DeviceToolHelper.YesNo(after)}");
            return ToolResult.Success(sb.ToString());
        }

        private static ToolResult ExecuteUiAction(System.Collections.Generic.List<Thing> things, string actionId)
        {
            var commands = DeviceToolHelper.GetDeviceCommands(things);
            var command = commands.FirstOrDefault(c => DeviceToolHelper.MatchesActionId(c, actionId));
            if (command == null) return ToolResult.Error($"找不到 UI Action: {actionId}。请重新调用 get_device_info 获取最新 action_id。");
            if (command.Disabled) return ToolResult.Error($"UI Action 已禁用: {command.DisabledReason}");

            return ToolResult.Error($"为避免误打开弹窗/目标选择，首版不直接执行 Command_Action: {command.Label}。请使用 get_device_info 输出的 adapter action_id，或为该按钮新增安全 adapter。");
        }

        private static ToolResult ExecuteAdapter(DeviceToolHelper.ResolvedTargets targets, string actionId, JsonElement args)
        {
            var forcePartial = args.TryGetProperty("force_partial", out var jPartial) && jPartial.ValueKind == JsonValueKind.True;
            var unsupported = targets.Things
                .Where(t => !DeviceToolHelper.GetAdapterActions(t).Any(a => a.id.Equals(actionId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unsupported.Count > 0 && !forcePartial)
            {
                var details = string.Join("; ", unsupported.Select(t => $"ID:{t.thingIDNumber} {t.LabelCap} 支持: {DeviceToolHelper.GetSupportedActions(t)}"));
                return ToolResult.Error($"不是所有目标都支持 action_id={actionId}。{details}");
            }

            var skipNote = forcePartial && unsupported.Count > 0
                ? $"force_partial 跳过 {unsupported.Count} 个不支持设备: {string.Join("; ", unsupported.Select(t => $"ID:{t.thingIDNumber} {t.LabelCap}"))}"
                : "";
            var effectiveTargets = new DeviceToolHelper.ResolvedTargets { Source = string.IsNullOrEmpty(skipNote) ? targets.Source : skipNote };
            foreach (var thing in targets.Things.Where(t => !unsupported.Contains(t)))
                effectiveTargets.Things.Add(thing);
            if (effectiveTargets.Things.Count == 0)
                return ToolResult.Error($"没有目标支持 action_id={actionId}。");

            switch (actionId)
            {
                case "set_power":
                    if (!DeviceToolHelper.TryGetBool(args, out var powerOn)) return ToolResult.Error("set_power 需要 boolean value。");
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var comp = thing.TryGetComp<CompPowerTrader>();
                        var before = comp.PowerOn;
                        comp.PowerOn = powerOn;
                        return $"ID:{thing.thingIDNumber} {thing.LabelCap}: 电源 {DeviceToolHelper.YesNo(before)} → {DeviceToolHelper.YesNo(comp.PowerOn)}";
                    });

                case "set_target_temp":
                    if (!DeviceToolHelper.TryGetFloat(args, out var temp)) return ToolResult.Error("set_target_temp 需要 number value（目标温度 °C）。");
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var comp = thing.TryGetComp<CompTempControl>();
                        var before = comp.TargetTemperature;
                        comp.TargetTemperature = Mathf.Clamp(temp, -273.15f, 1000f);
                        return $"ID:{thing.thingIDNumber} {thing.LabelCap}: 目标温度 {before:F0}°C → {comp.TargetTemperature:F0}°C";
                    });

                case "adjust_target_temp":
                    if (!DeviceToolHelper.TryGetFloat(args, out var delta)) return ToolResult.Error("adjust_target_temp 需要 number value（温度增量 °C，负数降温）。");
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var comp = thing.TryGetComp<CompTempControl>();
                        var before = comp.TargetTemperature;
                        comp.TargetTemperature = Mathf.Clamp(before + delta, -273.15f, 1000f);
                        return $"ID:{thing.thingIDNumber} {thing.LabelCap}: 目标温度 {before:F0}°C → {comp.TargetTemperature:F0}°C";
                    });

                case "set_target_fuel_level":
                    if (!DeviceToolHelper.TryGetFloat(args, out var fuelLevel)) return ToolResult.Error("set_target_fuel_level 需要 number value（目标燃料量）。");
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var comp = thing.TryGetComp<CompRefuelable>();
                        var before = comp.TargetFuelLevel;
                        comp.TargetFuelLevel = Mathf.Clamp(fuelLevel, 0f, comp.Props.fuelCapacity);
                        return $"ID:{thing.thingIDNumber} {thing.LabelCap}: 目标燃料 {before:F0} → {comp.TargetFuelLevel:F0}";
                    });

                case "set_auto_refuel":
                    if (!DeviceToolHelper.TryGetBool(args, out var autoRefuel)) return ToolResult.Error("set_auto_refuel 需要 boolean value。");
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var comp = thing.TryGetComp<CompRefuelable>();
                        var before = comp.allowAutoRefuel;
                        comp.allowAutoRefuel = autoRefuel;
                        return $"ID:{thing.thingIDNumber} {thing.LabelCap}: 自动加燃料 {DeviceToolHelper.YesNo(before)} → {DeviceToolHelper.YesNo(comp.allowAutoRefuel)}";
                    });

                case "eject_fuel":
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var comp = thing.TryGetComp<CompRefuelable>();
                        var canEject = comp.CanEjectFuel();
                        if (!canEject.Accepted)
                            throw new InvalidOperationException($"ID:{thing.thingIDNumber} {thing.LabelCap} 无法排出燃料: {canEject.Reason}");
                        var before = comp.Fuel;
                        comp.EjectFuel();
                        return $"ID:{thing.thingIDNumber} {thing.LabelCap}: 已排出燃料 {before:F0}";
                    });

                case "flick":
                    var hasTarget = DeviceToolHelper.TryGetBool(args, out var targetSwitch);
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var comp = thing.TryGetComp<CompFlickable>();
                        var before = comp.SwitchIsOn;
                        if (!hasTarget || before != targetSwitch)
                            comp.DoFlick();
                        return $"ID:{thing.thingIDNumber} {thing.LabelCap}: 开关 {DeviceToolHelper.YesNo(before)} → {DeviceToolHelper.YesNo(comp.SwitchIsOn)}";
                    });

                case "set_auto_build_transport_pod":
                    if (!DeviceToolHelper.TryGetBool(args, out var autoBuild)) return ToolResult.Error("set_auto_build_transport_pod 需要 boolean value。");
                    return ForEachThing(effectiveTargets, actionId, thing => SetAutoBuildTransportPod((Building_PodLauncher)thing, autoBuild));

                case "toggle_auto_build_transport_pod":
                    return ForEachThing(effectiveTargets, actionId, thing =>
                    {
                        var launcher = (Building_PodLauncher)thing;
                        return SetAutoBuildTransportPod(launcher, !launcher.autoPlacePods);
                    });

                default:
                    return ToolResult.Error($"未知 action_id: {actionId}。可用 adapter: {string.Join(", ", DeviceToolHelper.AdapterActionIds)}；或使用 get_device_info 返回的 ui_toggle:N。 ");
            }
        }

        private static ToolResult ForEachThing(DeviceToolHelper.ResolvedTargets targets, string actionId, Func<Thing, string> action)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 设备操作完成");
            sb.AppendLine($"- 操作: {actionId}");
            if (!string.IsNullOrEmpty(targets.Source))
                sb.AppendLine($"- 备注: {targets.Source}");
            sb.AppendLine();
            foreach (var thing in targets.Things)
                sb.AppendLine("- " + action(thing));
            return ToolResult.Success(sb.ToString());
        }

        private static bool SafeIsActive(Command_Toggle toggle)
        {
            try { return toggle.isActive?.Invoke() ?? false; }
            catch (Exception ex)
            {
                McpLog.Warn($"[ExecuteDeviceAction] 读取 Toggle 状态失败: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static string SetAutoBuildTransportPod(Building_PodLauncher launcher, bool value)
        {
            var before = launcher.autoPlacePods;
            launcher.autoPlacePods = value;
            var placed = false;
            string note = "";

            if (value)
            {
                var fuelingPortCell = FuelingPortUtility.GetFuelingPortCell(launcher);
                var report = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.TransportPod, fuelingPortCell, ThingDefOf.TransportPod.defaultPlacingRot, launcher.Map, false, null, null, null, false, false, false);
                if (report.Accepted)
                {
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.TransportPod, fuelingPortCell, launcher.Map, ThingDefOf.TransportPod.defaultPlacingRot, Faction.OfPlayer, null, null, null, true);
                    placed = true;
                }
                else
                {
                    note = $"（未立即放置蓝图: {report.Reason}）";
                }
            }

            return $"ID:{launcher.thingIDNumber} {launcher.LabelCap}: 自动建造运输舱 {DeviceToolHelper.YesNo(before)} → {DeviceToolHelper.YesNo(launcher.autoPlacePods)}{(placed ? "，已放置运输舱蓝图" : note)}";
        }
    }
}
