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

            // ★ 设备覆盖层规则 — 使用游戏 UI 绘制覆盖层同源 API（几何 + 硬编码语义）
            BuildOverlayRules(sb, thing);
        }

        // ============== 覆盖层规则查询（get_device_overlay 专用）==============
        //
        // 设计要点：
        // - 覆盖层是纯绘制抽象，无运行时语义元数据。effect 字段为本工具硬编码的语义知识库
        //   （非游戏读取），按"类型→语义"映射表分发。未知类型降级为 unknown。
        // - 规则优先于坐标：Room.Cells 可能数十到上百格，只输出 anchor/offset/radius/room_id/
        //   cell_count，让 LLM 按需用 get_tile_grid 查具体布局。
        // - 数据源与游戏绘制同源：Thing.DrawExtraSelectionOverlays / PlaceWorker.DrawGhost。

        /// <summary>构建设备覆盖层的原生计算规则（几何 + 硬编码语义），数据源与 DrawExtraSelectionOverlays / PlaceWorker.DrawGhost 同源。</summary>
        internal static void BuildOverlayRules(StringBuilder sb, Thing thing)
        {
            var map = thing.Map;
            if (map == null)
            {
                sb.AppendLine("- (设备未 Spawn，无覆盖层)");
                return;
            }
            var pos = thing.Position;
            var rot = thing.Rotation;
            int rotInt = rot.AsInt;
            var facing = IntVec3.North.RotatedBy(rot); // 箭头朝向（= 制热侧 offset）
            string facingDesc = facing.z > 0 ? "+z(北)"
                : facing.z < 0 ? "-z(南)"
                : facing.x > 0 ? "+x(东)"
                : "-x(西)";
            int idx = 0;

            // 1. 温度控制 — Building_Cooler（有向，两侧房间）
            //    源码 Building_Cooler.cs:17-18 + PlaceWorker_Cooler.cs:26-43
            if (thing is Building_Cooler)
            {
                sb.AppendLine($"- rotation: {rotInt} ({RotName(rotInt)})，箭头朝向: {facingDesc}");
                sb.AppendLine("- 一句话规则: 制热侧 = 箭头同方向相邻格 → 该格所在房间；制冷侧 = 箭头反方向相邻格 → 该格所在房间");

                var coldOffset = IntVec3.South.RotatedBy(rot); // = -FacingCell = 制冷侧
                var hotOffset = facing;                         // = +FacingCell = 制热侧
                var coldCell = pos + coldOffset;
                var hotCell = pos + hotOffset;
                var coldRoom = coldCell.GetRoom(map);
                var hotRoom = hotCell.GetRoom(map);

                AppendTempRoomBlock(sb, ref idx, "制冷侧（蓝色，吸热）", "cool（降温）",
                    coldCell, coldOffset, "箭头反方向", coldRoom);
                AppendTempRoomBlock(sb, ref idx, "制热侧（红色，放热）", "heat（放热，能量 = 制冷能量 × 1.25）",
                    hotCell, hotOffset, "箭头同方向", hotRoom);

                if (coldRoom != null && hotRoom != null
                    && coldRoom.ID == hotRoom.ID
                    && !coldRoom.UsesOutdoorTemperature)
                {
                    int sharedRoomId = coldRoom.ID;
                    sb.AppendLine($"- same_room_warning: 是（两侧 anchor 同处密闭房间 room_id={sharedRoomId}，冷却无效、热直接回流，游戏画黄色警告）");
                }
                else
                {
                    string coldId = RoomIdOr(coldRoom);
                    string hotId = RoomIdOr(hotRoom);
                    sb.AppendLine($"- same_room_warning: 否（cold.room_id={coldId} ≠ hot.room_id={hotId}）");
                }
            }
            // 2. Building_Heater（无向，整房间加热）— 源码 PlaceWorker_Heater.cs:14-18
            else if (thing is Building_Heater)
            {
                AppendTempRoomBlock(sb, ref idx, "加热（红色，整房间）", "heat（整房间加热）",
                    pos, IntVec3.Zero, "设备自身所在房间", pos.GetRoom(map));
            }
            // 3. 单侧制冷 PlaceWorker_CoolerSimple（设备自身房间）— 源码 PlaceWorker_CoolerSimple.cs:14-19
            else if (HasPlaceWorker(thing, "PlaceWorker_CoolerSimple"))
            {
                AppendTempRoomBlock(sb, ref idx, "制冷（蓝色，整房间）", "cool（整房间冷却）",
                    pos, IntVec3.Zero, "设备自身所在房间", pos.GetRoom(map));
            }

            // 4. 轨道贸易信标（圆形半径 7.9 + 区域连通）— 源码 Building_OrbitalTradeBeacon.cs:55-79
            if (thing is Building_OrbitalTradeBeacon)
            {
                sb.AppendLine($"### 覆盖层 #{++idx}: 贸易投送区（白色圆形）");
                sb.AppendLine("- type: trade_radius");
                sb.AppendLine("- effect: trade（轨道贸易可投送货物的格子，物品须在此范围内才能被贸易识别）");
                sb.AppendLine($"- geometry: center=({pos.x},{pos.z}), radius=7.9 格");
                sb.AppendLine("- shape: 以信标为圆心、水平距离 ≤ 7.9 的圆形区域；且必须与信标所在 Region 连通（遇门阻断，最大深度 16）");
                sb.AppendLine("- source: Building_OrbitalTradeBeacon.TradeableCellsAround");
            }

            // 5. 半径环 def.specialDisplayRadius（太阳灯等）— Thing.DrawExtraSelectionOverlays 基类
            if (thing.def.specialDisplayRadius > 0.1f)
            {
                sb.AppendLine($"### 覆盖层 #{++idx}: 作用半径环");
                sb.AppendLine("- type: radius_ring");
                string effect = thing.def.defName == "SunLamp"
                    ? "grow（提供光照用于植物生长，圈内为有效种植照明区）"
                    : "display_radius（通用作用半径环，语义参考 def 设计意图）";
                sb.AppendLine($"- effect: {effect}");
                sb.AppendLine($"- geometry: center=({pos.x},{pos.z}), radius={thing.def.specialDisplayRadius:F1} 格");
                sb.AppendLine("- shape: 圆形（水平距离 ≤ radius 的所有 cell）");
                sb.AppendLine("- source: Thing.DrawExtraSelectionOverlays 基类 + def.specialDisplayRadius");
            }

            // 6. CompGlower 光照半径（仅参考，非覆盖层本身）
            var glower = thing.TryGetComp<CompGlower>();
            if (glower != null)
            {
                sb.AppendLine($"### 覆盖层 #{++idx}: 光照范围（参考）");
                sb.AppendLine("- type: light_radius");
                sb.AppendLine("- effect: light（声明光照半径，影响 map.glowGrid 光照强度；非覆盖层本身）");
                sb.AppendLine($"- geometry: center=({pos.x},{pos.z}), radius={glower.GlowRadius:F1} 格");
                sb.AppendLine("- note: 玩家选中看到的覆盖环通常是 specialDisplayRadius 种植环，不是此值；GlowGrid 是全局累积渲染值，混合多光源无法归因到单设备");
            }

            // 7. 炮塔射程圆环 — 源码 Building_TurretGun.cs:585-597
            if (thing is Building_TurretGun turret)
            {
                var verb = turret.AttackVerb;
                if (verb != null)
                {
                    float max = verb.EffectiveRange;
                    float min = verb.verbProps.EffectiveMinRange(true);
                    sb.AppendLine($"### 覆盖层 #{++idx}: 射程圆环");
                    sb.AppendLine("- type: attack_range");
                    sb.AppendLine("- effect: attack_range（炮塔可攻击的目标区域）");
                    sb.AppendLine($"- geometry: center=({pos.x},{pos.z}), min_range={min:F1}, max_range={max:F1}");
                    sb.AppendLine(min > 0.1f
                        ? "- shape: 环形（min_range ≤ 水平距离 ≤ max_range）；内圈为射击死角安全区"
                        : "- shape: 圆形（水平距离 ≤ max_range）");
                    sb.AppendLine("- source: Building_TurretGun.DrawExtraSelectionOverlays → GenDraw.DrawRadiusRing × 2");
                }
            }

            // 8. 噪声半径 — CompNoiseSource.PostDrawExtraSelectionOverlays
            var noise = thing.TryGetComp<CompNoiseSource>();
            if (noise != null)
            {
                sb.AppendLine($"### 覆盖层 #{++idx}: 噪声半径");
                sb.AppendLine("- type: noise_radius");
                sb.AppendLine("- effect: noise（噪声影响周边殖民者心情/睡眠）");
                sb.AppendLine($"- geometry: center=({pos.x},{pos.z}), radius={noise.Props.radius:F1} 格");
                sb.AppendLine("- source: CompNoiseSource.PostDrawExtraSelectionOverlays");
            }

            // 9. 植物杀伤半径（动态扩大）— CompPlantHarmRadius.CurrentRadius
            var plantHarm = thing.TryGetComp<CompPlantHarmRadius>();
            if (plantHarm != null)
            {
                sb.AppendLine($"### 覆盖层 #{++idx}: 植物杀伤半径（动态）");
                sb.AppendLine("- type: plant_harm_radius");
                sb.AppendLine("- effect: plant_harm（杀死范围内的植物）");
                sb.AppendLine($"- geometry: center=({pos.x},{pos.z}), current_radius={plantHarm.CurrentRadius:F1} 格");
                sb.AppendLine("- shape: 圆形，半径随 AgeDays 按 radiusPerDayCurve 动态扩大");
                sb.AppendLine("- source: CompPlantHarmRadius.CurrentRadius");
            }

            // 10. 风道风力机 — WindTurbineUtility.CalculateWindCells
            if (thing.TryGetComp<CompPowerPlantWind>() != null)
            {
                AppendWindTurbineBlock(sb, ref idx, thing);
            }

            if (idx == 0)
            {
                sb.AppendLine("- (本设备没有任何原版覆盖层，可对齐 Thing.DrawExtraSelectionOverlays / PlaceWorker.DrawGhost)");
            }
        }

        private static string RotName(int rotInt)
        {
            switch (rotInt)
            {
                case 0: return "North";
                case 1: return "East";
                case 2: return "South";
                case 3: return "West";
                default: return "Unknown(" + rotInt.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        private static string RoomIdOr(Room? room) =>
            room == null ? "null" : room.ID.ToString(CultureInfo.InvariantCulture);

        private static string RoomDesc(Room? room)
        {
            if (room == null) return "null";
            bool outdoor = room.UsesOutdoorTemperature;
            return $"room_id={room.ID.ToString(CultureInfo.InvariantCulture)}, cell_count={room.CellCount.ToString(CultureInfo.InvariantCulture)}, is_outdoor={(outdoor ? "是（触及地图边缘或 ≥25% 露天，不画房间级覆盖）" : "否")}";
        }

        private static bool HasPlaceWorker(Thing thing, string placeWorkerTypeName)
        {
            var workers = thing.def.PlaceWorkers;
            if (workers == null) return false;
            for (int i = 0; i < workers.Count; i++)
            {
                var pw = workers[i];
                if (pw != null && pw.GetType().Name == placeWorkerTypeName) return true;
            }
            return false;
        }

        private static void AppendTempRoomBlock(StringBuilder sb, ref int idx, string title, string effect,
            IntVec3 anchor, IntVec3 offset, string directionDesc, Room? room)
        {
            sb.AppendLine($"### 覆盖层 #{++idx}: {title}");
            sb.AppendLine("- type: temp_room");
            sb.AppendLine($"- effect: {effect}");
            sb.AppendLine($"- geometry: anchor=({anchor.x.ToString(CultureInfo.InvariantCulture)},{anchor.z.ToString(CultureInfo.InvariantCulture)}), offset=({offset.x.ToString(CultureInfo.InvariantCulture)},{offset.z.ToString(CultureInfo.InvariantCulture)})={directionDesc}；范围 = anchor 所在 Room 全部 cells（非固定矩形，是整个房间）");
            sb.AppendLine($"- room: {RoomDesc(room)}");
        }

        private static void AppendWindTurbineBlock(StringBuilder sb, ref int idx, Thing thing)
        {
            var pos = thing.Position;
            var rot = thing.Rotation;
            // 直接调用原版绘制同源 API 取真实 cell 列表，按轴向左/右分组算包围盒，不列坐标
            var cells = WindTurbineUtility.CalculateWindCells(pos, rot, thing.def.size).ToList();
            if (cells.Count == 0) return;

            bool horiz = rot.IsHorizontal;
            List<IntVec3> sideA, sideB;
            string axisNameA, axisNameB;
            if (horiz)
            {
                sideA = cells.Where(c => c.x > pos.x).ToList();
                sideB = cells.Where(c => c.x < pos.x).ToList();
                axisNameA = "+x(东)"; axisNameB = "-x(西)";
            }
            else
            {
                sideA = cells.Where(c => c.z > pos.z).ToList();
                sideB = cells.Where(c => c.z < pos.z).ToList();
                axisNameA = "+z(北)"; axisNameB = "-z(南)";
            }

            sb.AppendLine($"### 覆盖层 #{++idx}: 风道风力区");
            sb.AppendLine("- type: wind_tunnel");
            sb.AppendLine("- effect: wind（矩形内的遮挡物如树/墙/山会降低实际发电效率）");
            sb.AppendLine($"- rotation: {rot.AsInt.ToString(CultureInfo.InvariantCulture)} ({RotName(rot.AsInt)}) [{(horiz ? "水平朝向" : "垂直朝向")}]");
            sb.AppendLine($"- 总风道格数: {cells.Count.ToString(CultureInfo.InvariantCulture)}");
            AppendWindSideRect(sb, axisNameA, sideA);
            AppendWindSideRect(sb, axisNameB, sideB);
            sb.AppendLine("- source: WindTurbineUtility.CalculateWindCells");
        }

        private static void AppendWindSideRect(StringBuilder sb, string axisName, List<IntVec3> side)
        {
            if (side.Count == 0)
            {
                sb.AppendLine($"- 侧（{axisName}）: 无");
                return;
            }
            int minX = side.Min(c => c.x);
            int maxX = side.Max(c => c.x);
            int minZ = side.Min(c => c.z);
            int maxZ = side.Max(c => c.z);
            int w = maxX - minX + 1;
            int h = maxZ - minZ + 1;
            sb.AppendLine($"- 侧（{axisName}）: 包围盒 minX={minX.ToString(CultureInfo.InvariantCulture)},maxX={maxX.ToString(CultureInfo.InvariantCulture)} / minZ={minZ.ToString(CultureInfo.InvariantCulture)},maxZ={maxZ.ToString(CultureInfo.InvariantCulture)}（{w.ToString(CultureInfo.InvariantCulture)}×{h.ToString(CultureInfo.InvariantCulture)} 格，共 {side.Count.ToString(CultureInfo.InvariantCulture)} 格）");
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
