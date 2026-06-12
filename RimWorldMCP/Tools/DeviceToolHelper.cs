using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldMCP.Tools
{
    internal static class DeviceToolHelper
    {
        internal static readonly string[] AdapterActionIds =
        {
            "set_power",
            "set_target_temp",
            "adjust_target_temp",
            "set_target_fuel_level",
            "set_auto_refuel",
            "eject_fuel",
            "flick",
            "set_auto_build_transport_pod",
            "toggle_auto_build_transport_pod"
        };

        internal sealed class ResolvedTargets
        {
            public List<Thing> Things { get; } = new();
            public List<int> MissingThingIds { get; } = new();
            public string Source { get; set; } = "";
        }

        internal sealed class DeviceCommand
        {
            public int Index { get; set; }
            public Thing Thing { get; set; } = null!;
            public Gizmo Gizmo { get; set; } = null!;
            public Command? Command { get; set; }
            public string ActionId { get; set; } = "";
            public string StableActionId { get; set; } = "";
            public string Kind { get; set; } = "gizmo";
            public string Label { get; set; } = "";
            public string Description { get; set; } = "";
            public bool Disabled { get; set; }
            public string DisabledReason { get; set; } = "";
            public bool? IsActive { get; set; }
            public bool Executable { get; set; }
            public string ExecuteNote { get; set; } = "";
        }

        internal static ResolvedTargets ResolveTargets(Map map, JsonElement args)
        {
            var result = new ResolvedTargets();

            if (args.TryGetProperty("thing_ids", out var jIds) && jIds.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in jIds.EnumerateArray())
                {
                    if (!item.TryGetInt32(out var id))
                        throw new InvalidOperationException("thing_ids 只能包含整数。");
                    var thing = CameraHelper.FindThingById(map, id);
                    if (thing != null && !thing.Destroyed)
                    {
                        if (!result.Things.Contains(thing))
                            result.Things.Add(thing);
                    }
                    else
                    {
                        result.MissingThingIds.Add(id);
                    }
                }

                result.Source = "thing_ids";
                return result;
            }

            if (args.TryGetProperty("thing_id", out var jId) && jId.TryGetInt32(out var thingId))
            {
                var thing = CameraHelper.FindThingById(map, thingId);
                if (thing != null && !thing.Destroyed)
                    result.Things.Add(thing);
                else
                    result.MissingThingIds.Add(thingId);
                result.Source = "thing_id";
                return result;
            }

            if (args.TryGetProperty("pos_x", out var jx) && jx.TryGetInt32(out var posX)
                && args.TryGetProperty("pos_y", out var jy) && jy.TryGetInt32(out var posY))
            {
                var cell = new IntVec3(posX, 0, posY);
                if (!cell.InBounds(map))
                    throw new InvalidOperationException($"坐标 ({posX},{posY}) 超出地图范围。");
                if (cell.Fogged(map))
                    throw new InvalidOperationException($"坐标 ({posX},{posY}) 在迷雾中，无法查看。");

                var things = cell.GetThingList(map);
                var device = things.FirstOrDefault(IsDeviceLike) ?? things.FirstOrDefault(t => t is Building) ?? things.FirstOrDefault();
                if (device != null && !device.Destroyed)
                    result.Things.Add(device);
                result.Source = "pos";
                return result;
            }

            throw new InvalidOperationException("需要 thing_id、thing_ids 或 pos_x/pos_y 定位设备。");
        }

        internal static string GetMissingThingIdsMessage(ResolvedTargets targets)
        {
            if (targets.MissingThingIds.Count == 0) return "";
            return $"以下 thing_id 不存在或已销毁: {string.Join(", ", targets.MissingThingIds)}";
        }

        internal static void SelectTargets(IEnumerable<Thing> things)
        {
            Find.Selector.ClearSelection();
            foreach (var thing in things)
            {
                if (thing == null || thing.Destroyed) continue;
                Find.Selector.Select(thing, false, true);
            }
        }

        internal static List<DeviceCommand> GetDeviceCommands(List<Thing> things)
        {
            SelectTargets(things);
            var commands = new List<DeviceCommand>();
            var index = 0;

            foreach (var thing in things)
            {
                try
                {
                    foreach (var gizmo in thing.GetGizmos() ?? Enumerable.Empty<Gizmo>())
                    {
                        if (gizmo == null || !gizmo.Visible) continue;
                        var command = gizmo as Command;
                        var label = SafeCommandText(() => command?.LabelCap ?? command?.Label ?? gizmo.GetType().Name);
                        var desc = SafeCommandText(() => command?.Desc ?? "");
                        if (IsDebugCommand(label, desc)) continue;

                        var kind = GetKind(gizmo);
                        var actionId = kind switch
                        {
                            "toggle" => $"ui_toggle:{index}",
                            "action" => $"ui_action:{index}",
                            _ => $"ui_gizmo:{index}"
                        };
                        var stableActionId = BuildStableActionId(kind, thing, gizmo, label, desc);

                        bool? active = null;
                        var toggle = gizmo as Command_Toggle;
                        if (toggle != null)
                        {
                            try { active = toggle.isActive?.Invoke(); }
                            catch (Exception ex) { McpLog.Warn($"[DeviceTool] 读取 Toggle 状态失败: {ex.GetType().Name}: {ex.Message}"); }
                        }

                        var safeAction = kind == "action" && IsSafeCommandAction(thing, label, desc);
                        commands.Add(new DeviceCommand
                        {
                            Index = index,
                            Thing = thing,
                            Gizmo = gizmo,
                            Command = command,
                            ActionId = actionId,
                            StableActionId = stableActionId,
                            Kind = kind,
                            Label = label,
                            Description = desc,
                            Disabled = gizmo.Disabled,
                            DisabledReason = command?.disabledReason ?? gizmo.disabledReason ?? "",
                            IsActive = active,
                            Executable = kind == "toggle" || safeAction,
                            ExecuteNote = kind == "toggle"
                                ? "可执行：调用原版 Command_Toggle.toggleAction"
                                : safeAction
                                    ? "可执行：安全 Command_Action allowlist"
                                    : "仅展示：复杂按钮需专用 adapter"
                        });
                        index++;
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[DeviceTool] 枚举 {thing.LabelShort} 的 Gizmo 失败: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return commands;
        }

        internal static bool MatchesActionId(DeviceCommand command, string actionId)
        {
            return command.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase)
                || command.StableActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase);
        }

        internal static IEnumerable<Thing> EnumerateDevices(Map map)
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing == null || thing.Destroyed || thing.Fogged()) continue;
                if (thing is Blueprint || thing is Frame) continue;
                if (IsDeviceLike(thing)) yield return thing;
            }
        }

        internal static bool HasAdapterAction(Thing thing, string actionId)
        {
            return GetAdapterActions(thing).Any(a => a.id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool HasCompNamed(Thing thing, string compName)
        {
            var withComps = thing as ThingWithComps;
            if (withComps?.AllComps == null) return false;
            return withComps.AllComps.Any(c => c.GetType().Name.Equals(compName, StringComparison.OrdinalIgnoreCase));
        }

        internal static string GetCompSummary(Thing thing)
        {
            var parts = new List<string>();
            // 细分电源类型
            if (thing.TryGetComp<CompPowerPlantWind>() != null) parts.Add("风力");
            else if (thing.TryGetComp<CompPowerPlantSolar>() != null) parts.Add("太阳能");
            else if (thing.TryGetComp<CompPowerTrader>() != null) parts.Add("电力");
            if (thing.TryGetComp<CompTempControl>() != null) parts.Add("温控");
            if (thing.TryGetComp<CompRefuelable>() != null) parts.Add("燃料");
            if (thing.TryGetComp<CompFlickable>() != null) parts.Add("开关");
            if (thing.TryGetComp<CompTransporter>() != null) parts.Add("运输器");
            if (thing.TryGetComp<CompLaunchable>() != null) parts.Add("发射");
            if (thing.TryGetComp<CompShuttle>() != null) parts.Add("穿梭机");
            if (thing is Building_PodLauncher) parts.Add("发射台");
            if (thing.TryGetComp<CompAutoCutWindTurbine>() != null) parts.Add("自动砍树");
            if (thing.TryGetComp<CompStunnable>() != null) parts.Add("可眩晕");
            return parts.Count == 0 ? "建筑" : string.Join(",", parts);
        }

        internal static string FormatDeviceSummary(Thing thing)
        {
            var actions = GetAdapterActions(thing).Select(a => a.id).ToList();
            var actionText = actions.Count == 0 ? "-" : string.Join(",", actions);
            return $"| {thing.thingIDNumber} | {Escape(thing.LabelCap)} | `{thing.def.defName}` | ({thing.Position.x},{thing.Position.z}) | {Escape(GetCompSummary(thing))} | {Escape(actionText)} |";
        }

        internal static bool IsSafeCommandAction(DeviceCommand command)
        {
            return command.Gizmo is Command_Action && IsSafeCommandAction(command.Thing, command.Label, command.Description);
        }

        private static bool IsSafeCommandAction(Thing thing, string label, string desc)
        {
            var text = ((label ?? "") + " " + (desc ?? "")).ToLowerInvariant();

            if (thing.TryGetComp<CompTempControl>() != null && LooksLikeTemperatureCommand(text))
                return true;

            if (thing is Building_PodLauncher && LooksLikeBuildTransportPodCommand(text))
                return true;

            if (thing.TryGetComp<CompTransporter>() != null && LooksLikeTransporterSelectionCommand(text))
                return true;

            return false;
        }

        private static bool LooksLikeTemperatureCommand(string text)
        {
            return text.Contains("温度")
                || text.Contains("temperature")
                || text.Contains("temp")
                || text.Contains("°")
                || text.Contains("℃");
        }

        private static bool LooksLikeBuildTransportPodCommand(string text)
        {
            var podLabel = ThingDefOf.TransportPod.label?.ToLowerInvariant() ?? "";
            return (!string.IsNullOrEmpty(podLabel) && text.Contains(podLabel))
                || text.Contains("transport pod")
                || text.Contains("运输舱");
        }

        private static bool LooksLikeTransporterSelectionCommand(string text)
        {
            var isSelection = text.Contains("select") || text.Contains("选择");
            var isTransporter = text.Contains("transporter") || text.Contains("运输器") || text.Contains("运输舱") || text.Contains("shuttle") || text.Contains("穿梭机");
            return isSelection && isTransporter;
        }

        internal static string FormatTransporterDetails(CompTransporter transporter)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"- 运输器状态: groupID={transporter.groupID}，装载/待发射={YesNo(transporter.LoadingInProgressOrReadyToLaunch)}，剩余装载={YesNo(transporter.AnythingLeftToLoad)}，组内剩余装载={YesNo(transporter.AnyInGroupHasAnythingLeftToLoad)}，超重={YesNo(transporter.OverMassCapacity)}，质量 {transporter.MassUsage:F1}/{transporter.MassCapacity:F1}kg");
            sb.AppendLine($"- 可搬运状态: 当前可继续装载={YesNo(transporter.AnyPawnCanLoadAnythingNow)}，组内无法继续装载提示={YesNo(transporter.AnyInGroupNotifiedCantLoadMore)}");

            var group = transporter.TransportersInGroup(transporter.Map);
            sb.AppendLine($"- 运输器组: {(group == null ? 0 : group.Count)} 个");

            var launchable = transporter.Launchable;
            if (launchable != null)
            {
                var canLaunch = launchable.CanLaunch(null);
                sb.AppendLine($"- 发射状态: 燃料 {launchable.FuelLevel:F0}/{launchable.MaxFuelLevel:F0}，可发射={YesNo(canLaunch.Accepted)}{(canLaunch.Accepted ? "" : $" ({canLaunch.Reason})")}");
            }

            var shuttle = transporter.Shuttle;
            if (shuttle != null)
                sb.AppendLine($"- 穿梭机: 自动装载={YesNo(shuttle.Autoload)}，显示装载按钮={YesNo(shuttle.ShowLoadingGizmos)}，需求已装载={YesNo(shuttle.AllRequiredThingsLoaded)}，组大小={shuttle.TransportersInGroup.Count}");

            sb.AppendLine("#### 已装载");
            if (transporter.innerContainer == null || transporter.innerContainer.Count == 0)
            {
                sb.AppendLine("(无)");
            }
            else
            {
                sb.AppendLine("| ID | 名称 | defName | 数量 | 质量 | 位置 | ");
                sb.AppendLine("|---:|---|---|---:|---:|---|");
                foreach (Thing thing in transporter.innerContainer)
                    sb.AppendLine($"| {thing.thingIDNumber} | {Escape(thing.LabelCap)} | `{thing.def.defName}` | {thing.stackCount} | {thing.GetStatValue(StatDefOf.Mass):F1} | 容器内 |");
            }

            sb.AppendLine("#### 待装载");
            if (transporter.leftToLoad == null || transporter.leftToLoad.Count == 0)
            {
                sb.AppendLine("(无)");
            }
            else
            {
                sb.AppendLine("| 名称 | defName | 数量 | 样本ID | ");
                sb.AppendLine("|---|---|---:|---:|");
                foreach (var transferable in transporter.leftToLoad)
                {
                    if (transferable.CountToTransfer <= 0 || !transferable.HasAnyThing) continue;
                    var sample = transferable.AnyThing;
                    sb.AppendLine($"| {Escape(sample.LabelCap)} | `{sample.def.defName}` | {transferable.CountToTransfer} | {sample.thingIDNumber} |");
                }
            }

            return sb.ToString();
        }

        internal static void InterruptTransporterHaulers(Map map, int groupId)
        {
            if (groupId < 0) return;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                try
                {
                    if (pawn.CurJobDef == JobDefOf.HaulToTransporter && pawn.jobs?.curDriver is JobDriver_HaulToTransporter haulDriver && haulDriver.Transporter?.groupID == groupId)
                        pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced, false, true);
                    else if (pawn.CurJobDef == JobDefOf.EnterTransporter && pawn.jobs?.curDriver is JobDriver_EnterTransporter enterDriver && enterDriver.Transporter?.groupID == groupId)
                        pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced, false, true);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[DeviceTool] 中断运输器搬运任务失败: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        internal static bool TryGetBool(JsonElement args, out bool value)
        {
            value = false;
            if (!args.TryGetProperty("value", out var jValue)) return false;
            if (jValue.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (jValue.ValueKind == JsonValueKind.False) { value = false; return true; }
            if (jValue.ValueKind == JsonValueKind.String && bool.TryParse(jValue.GetString(), out var parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        internal static bool TryGetFloat(JsonElement args, out float value)
        {
            value = 0f;
            if (!args.TryGetProperty("value", out var jValue)) return false;
            if (jValue.ValueKind == JsonValueKind.Number)
            {
                value = (float)jValue.GetDouble();
                return true;
            }
            if (jValue.ValueKind == JsonValueKind.String
                && float.TryParse(jValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        internal static string GetSupportedActions(Thing thing)
        {
            var ids = GetAdapterActions(thing).Select(a => a.id).ToList();
            return ids.Count == 0 ? "(无 adapter 操作，请查看 UI 命令)" : string.Join(", ", ids);
        }

        internal static List<(string id, string value, string description)> GetAdapterActions(Thing thing)
        {
            var actions = new List<(string id, string value, string description)>();
            if (thing.TryGetComp<CompPowerTrader>() != null)
                actions.Add(("set_power", "boolean", "设置设备电源开/关"));
            if (thing.TryGetComp<CompTempControl>() != null)
            {
                actions.Add(("set_target_temp", "number", "设置目标温度（°C）"));
                actions.Add(("adjust_target_temp", "number", "增减目标温度，负数降温、正数升温"));
            }
            var refuelable = thing.TryGetComp<CompRefuelable>();
            if (refuelable != null)
            {
                actions.Add(("set_target_fuel_level", "number", "设置目标燃料量"));
                actions.Add(("set_auto_refuel", "boolean", "设置自动添加燃料"));
                if (refuelable.CanEjectFuel().Accepted)
                    actions.Add(("eject_fuel", "none", "排出所有燃料"));
            }
            if (thing.TryGetComp<CompFlickable>() != null)
                actions.Add(("flick", "boolean?", "立即切换或设置开关状态"));
            if (thing is Building_PodLauncher)
            {
                actions.Add(("set_auto_build_transport_pod", "boolean", "设置自动建造运输舱"));
                actions.Add(("toggle_auto_build_transport_pod", "none", "切换自动建造运输舱"));
            }
            return actions;
        }

        internal static (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (args.Value.TryGetProperty("pos_x", out var jx) && jx.TryGetInt32(out var px)
                && args.Value.TryGetProperty("pos_y", out var jy) && jy.TryGetInt32(out var py))
                return (px, py, px, py);

            var map = Find.CurrentMap;
            if (map == null) return null;

            var ids = new List<int>();
            if (args.Value.TryGetProperty("thing_ids", out var jIds) && jIds.ValueKind == JsonValueKind.Array)
            {
                ids.AddRange(jIds.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()));
            }
            else if (args.Value.TryGetProperty("thing_id", out var jId) && jId.TryGetInt32(out var id))
            {
                ids.Add(id);
            }

            var positions = ids.Select(id => CameraHelper.FindThingById(map, id))
                .Where(t => t != null && !t.Destroyed)
                .Select(t => t!.Position)
                .ToList();
            if (positions.Count == 0) return null;
            return (positions.Min(p => p.x), positions.Min(p => p.z), positions.Max(p => p.x), positions.Max(p => p.z));
        }

        internal static string FormatBasicThing(Thing thing)
        {
            var faction = thing.Faction?.Name ?? "无";
            var hp = thing.def.useHitPoints ? $"{thing.HitPoints}/{thing.MaxHitPoints}" : "无";
            return $"| {thing.thingIDNumber} | {Escape(thing.LabelCap)} | `{thing.def.defName}` | ({thing.Position.x},{thing.Position.z}) | {Escape(faction)} | {hp} |";
        }

        internal static void AppendComponentInfo(StringBuilder sb, Thing thing)
        {
            var withComps = thing as ThingWithComps;
            if (withComps?.AllComps != null && withComps.AllComps.Count > 0)
                sb.AppendLine($"- 全部 Comp: {string.Join(", ", withComps.AllComps.Select(c => c.GetType().Name))}");
            else
                sb.AppendLine("- 全部 Comp: (无)");

            // ★ 游戏 UI 等价检测信息 — 复用 ThingWithComps.GetInspectString() 链
            var inspectStr = thing.GetInspectString();
            if (!string.IsNullOrEmpty(inspectStr))
            {
                foreach (var line in inspectStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    sb.AppendLine($"- {Escape(line.TrimEnd('\r'))}");
            }

            // ★ 设备作用范围 — 使用游戏 UI 绘制覆盖层同源 API
            AppendRangeInfo(sb, thing);
        }

        /// <summary>获取设备作用范围，数据源与 DrawExtraSelectionOverlays / PlaceWorker.DrawGhost 同源。</summary>
        private static void AppendRangeInfo(StringBuilder sb, Thing thing)
        {
            // 风道范围 — PlaceWorker_WindTurbine.DrawGhost 用 WindTurbineUtility.CalculateWindCells
            if (thing.TryGetComp<CompPowerPlantWind>() != null)
            {
                var cells = WindTurbineUtility.CalculateWindCells(
                    thing.Position, thing.Rotation, thing.def.size).ToList();
                sb.AppendLine($"- 风道范围: {cells.Count} cells");
            }

            // 炮塔射程 — Building_TurretGun.DrawExtraSelectionOverlays 用 AttackVerb
            var turret = thing as Building_TurretGun;
            if (turret != null)
            {
                var verb = turret.AttackVerb;
                if (verb != null)
                {
                    float max = verb.EffectiveRange;
                    float min = verb.verbProps.EffectiveMinRange(true);
                    sb.Append($"- 射程: {max:F1}");
                    if (min > 0.1f) sb.Append($", 最小射程: {min:F1}");
                    sb.AppendLine();
                }
            }

            // 太阳灯 — Thing.DrawExtraSelectionOverlays 用 def.specialDisplayRadius
            if (thing.def.defName == "SunLamp")
            {
                var glower = thing.TryGetComp<CompGlower>();
                if (glower != null)
                    sb.AppendLine($"- 光照半径: {glower.GlowRadius:F1}");
                if (thing.def.specialDisplayRadius > 0.1f)
                    sb.AppendLine($"- 种植半径: {thing.def.specialDisplayRadius:F1}");
            }

            // 贸易信标 — PlaceWorker_ShowTradeBeaconRadius.DrawGhost 用 TradeableCellsAround
            var beacon = thing as Building_OrbitalTradeBeacon;
            if (beacon != null && thing.Map != null)
            {
                var cells = Building_OrbitalTradeBeacon.TradeableCellsAround(thing.Position, thing.Map);
                sb.AppendLine($"- 贸易范围: 半径 7.9, 覆盖 {cells.Count} cells");
            }

            // 噪声源 — CompNoiseSource.PostDrawExtraSelectionOverlays 用 Props.radius
            var noise = thing.TryGetComp<CompNoiseSource>();
            if (noise != null)
                sb.AppendLine($"- 噪声半径: {noise.Props.radius:F1}");

            // 植物杀手 — PlaceWorker_ShowPlantHarmRadius.DrawGhost 用 CurrentRadius
            var plantHarm = thing.TryGetComp<CompPlantHarmRadius>();
            if (plantHarm != null)
                sb.AppendLine($"- 植物杀伤半径: {plantHarm.CurrentRadius:F1}");

            // 地形泵 (CompTerrainPump) 和地形改造器 (CompTerraformer) 范围由 GetInspectString 覆盖

            // 通用: specialDisplayRadius 覆盖层 — Thing.DrawExtraSelectionOverlays 基类
            if (thing.def.specialDisplayRadius > 0.1f && thing.def.defName != "SunLamp")
                sb.AppendLine($"- 作用半径: {thing.def.specialDisplayRadius:F1}");
        }

        internal static string YesNo(bool value) => value ? "是" : "否";
        internal static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        internal static bool IsDeviceLike(Thing thing)
        {
            if (thing is Building) return true;
            return thing.TryGetComp<CompPowerTrader>() != null
                || thing.TryGetComp<CompTempControl>() != null
                || thing.TryGetComp<CompRefuelable>() != null
                || thing.TryGetComp<CompFlickable>() != null
                || thing.TryGetComp<CompTransporter>() != null
                || thing.TryGetComp<CompLaunchable>() != null
                || thing.TryGetComp<CompShuttle>() != null;
        }

        private static string BuildStableActionId(string kind, Thing thing, Gizmo gizmo, string label, string desc)
        {
            var prefix = kind switch
            {
                "toggle" => "ui_toggle",
                "action" => "ui_action",
                _ => "ui_gizmo"
            };
            var signature = $"{gizmo.GetType().Name}|{label}|{desc}";
            return $"{prefix}:{thing.thingIDNumber}:{gizmo.GetType().Name}:{StableHash(signature)}";
        }

        private static string StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var ch in text ?? "")
                {
                    hash ^= ch;
                    hash *= 16777619;
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static string GetKind(Gizmo gizmo)
        {
            if (gizmo is Command_Toggle) return "toggle";
            if (gizmo is Command_Action) return "action";
            if (gizmo is Command) return "command";
            return "gizmo";
        }

        private static string SafeCommandText(Func<string> getter)
        {
            try { return getter() ?? ""; }
            catch (Exception ex)
            {
                McpLog.Warn($"[DeviceTool] 读取命令文本失败: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        private static bool IsDebugCommand(string label, string desc)
        {
            var text = (label + " " + desc).Trim();
            return text.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase)
                || text.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("开发者", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
