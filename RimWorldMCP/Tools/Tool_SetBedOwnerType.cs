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
    public class Tool_SetBedOwnerType : ITool
    {
        public string Name => "set_bed_owner_type";
        public string Description => "设置床的归属类型（殖民者/俘虏/奴隶）。切换为俘虏时会触发同房间级联转换。传 force=true 可跳过安全校验。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "床的 X 坐标" },
                pos_y = new { type = "integer", description = "床的 Y 坐标" },
                owner_type = new { type = "string", description = "归属类型: colonist, prisoner, slave(仅Ideology)" },
                force = new { type = "boolean", description = "跳过安全校验强制切换（默认 false）", @default = false }
            },
            required = new[] { "pos_x", "pos_y", "owner_type" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(ToolResult.Error("缺少参数"));

            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return Task.FromResult(ToolResult.Error("缺少必填参数: pos_x"));
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return Task.FromResult(ToolResult.Error("缺少必填参数: pos_y"));
            if (!args.Value.TryGetProperty("owner_type", out var jOt) || jOt.GetString() is not string ownerType)
                return Task.FromResult(ToolResult.Error("缺少必填参数: owner_type"));

            ownerType = ownerType.ToLowerInvariant();

            bool force = false;
            if (args.Value.TryGetProperty("force", out var jForce))
                force = jForce.ValueKind == JsonValueKind.True;

            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    var pos = new IntVec3(posX, 0, posY);
                    var bed = pos.GetThingList(map).OfType<Building_Bed>().FirstOrDefault();
                    if (bed == null)
                        return ToolResult.Error($"({posX}, {posY}) 位置没有找到床。");

                    if (!bed.def.building.bed_humanlike)
                        return ToolResult.Error("该床不是人形床，无法设置归属类型。");

                    BedOwnerType targetType;
                    switch (ownerType)
                    {
                        case "colonist": targetType = BedOwnerType.Colonist; break;
                        case "prisoner": targetType = BedOwnerType.Prisoner; break;
                        case "slave":
                            if (!ModsConfig.IdeologyActive)
                                return ToolResult.Error("Slave 类型需要 Ideology DLC。");
                            targetType = BedOwnerType.Slave;
                            break;
                        default:
                            return ToolResult.Error($"无效的 owner_type: {ownerType}。可选: colonist, prisoner, slave");
                    }

                    if (bed.ForOwnerType == targetType)
                        return ToolResult.Success($"该床已经是 {OwnerTypeLabel(targetType)} 类型，无需更改。");

                    var oldType = bed.ForOwnerType;

                    // ==================== 房间分析（变更前） ====================
                    var room = bed.GetRoom(RegionType.Set_All);
                    var district = bed.GetDistrict(RegionType.Set_Passable);
                    string oldRoomRole = room?.Role?.label ?? "无";

                    // 1. TouchesMapEdge 检测（切换为俘虏时 —— 硬约束）
                    if (targetType == BedOwnerType.Prisoner && room != null && room.TouchesMapEdge)
                        return ToolResult.Error("该房间接触地图边缘，无法设为俘虏床。请先用墙壁封闭地图边缘再试。");

                    // 2. 非封闭/超大房间检测
                    if (targetType == BedOwnerType.Prisoner && room != null && (!room.ProperRoom || room.IsHuge))
                        return ToolResult.Error("该房间不是有效的封闭房间（露天或太大），无法设为俘虏床。");

                    // 3. 收集同房所有床信息
                    int otherBedsTotal = 0;
                    int bedsToAutoConvert = 0;    // 会被自动转为俘虏的床数
                    int otherBedsSameType = 0;     // 同房同类型的床数
                    string otherBedsSummary = "";
                    var allAffectedPawnIds = new List<int>(); // 变更前所有床位持有者 thingIDNumber

                    if (room != null)
                    {
                        var allBeds = room.ContainedBeds?.ToList() ?? new List<Building_Bed>();
                        otherBedsTotal = allBeds.Count(b => b != bed && b.def.building.bed_humanlike);
                        otherBedsSameType = allBeds.Count(b => b != bed && b.ForOwnerType == targetType && b.def.building.bed_humanlike);

                        // 切换为俘虏时：同房非俘虏床会被自动转换
                        if (targetType == BedOwnerType.Prisoner)
                            bedsToAutoConvert = allBeds.Count(b => b != bed && b.ForOwnerType != BedOwnerType.Prisoner && b.def.building.bed_humanlike);

                        // 收集所有受影响角色 thingIDNumber
                        foreach (var b in allBeds)
                        {
                            if (b.def.building.bed_humanlike)
                                foreach (var owner in b.OwnersForReading ?? Enumerable.Empty<Pawn>())
                                {
                                    if (!allAffectedPawnIds.Contains(owner.thingIDNumber))
                                        allAffectedPawnIds.Add(owner.thingIDNumber);
                                }
                        }

                        if (otherBedsTotal > 0)
                        {
                            var typeSummary = string.Join(", ", allBeds
                                .Where(b => b != bed && b.def.building.bed_humanlike)
                                .GroupBy(b => b.ForOwnerType)
                                .Select(g => $"{OwnerTypeLabel(g.Key)}×{g.Count()}"));
                            otherBedsSummary = $"同房还有 {otherBedsTotal} 张床: {typeSummary}。";
                        }
                    }
                    else
                    {
                        // 无房间（室外）
                        foreach (var owner in bed.OwnersForReading ?? Enumerable.Empty<Pawn>())
                            allAffectedPawnIds.Add(owner.thingIDNumber);
                    }

                    // ==================== 安全校验 ====================
                    bool hasBedWarning = false;
                    var warningMessages = new List<string>();

                    // 殖民者床保护（仅 Colonist → 非Colonist 时）
                    if (oldType == BedOwnerType.Colonist && targetType != BedOwnerType.Colonist)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        if (colonists != null && colonists.Count > 0)
                        {
                            var allColonistBeds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>()
                                .Where(b => b != bed && b.ForOwnerType == BedOwnerType.Colonist && b.def.building.bed_humanlike)
                                .ToList();
                            int otherColonistBeds = allColonistBeds.Count;

                            var owners = bed.OwnersForReading?.ToList() ?? new List<Pawn>();
                            var colonistOwners = owners.Where(o => o.IsColonist).ToList();

                            // 硬错误：地图上唯一的殖民者床
                            if (!force && otherColonistBeds == 0)
                            {
                                if (colonistOwners.Count > 0)
                                    return ToolResult.Error(
                                        $"不允许切换：这是地图上唯一的殖民者床，以下殖民者会失去床位: {string.Join(", ", colonistOwners.Select(o => o.Name.ToStringShort))}。请先建造新的殖民者床。");
                                if (colonists.Count > 0)
                                    return ToolResult.Error(
                                        $"不允许切换：这是地图上唯一的殖民者床，{colonists.Count} 名殖民者将无床可用。请先建造新的殖民者床。");
                            }

                            // 硬错误：分配到此床的殖民者无其他床
                            if (!force)
                            {
                                foreach (var owner in colonistOwners)
                                {
                                    bool hasOtherBed = allColonistBeds.Any(b =>
                                        b != bed && (b.OwnersForReading == null || b.OwnersForReading.Count() < b.SleepingSlotsCount ||
                                                    b.AnyUnownedSleepingSlot));
                                    if (!hasOtherBed && otherColonistBeds == 0)
                                        return ToolResult.Error(
                                            $"不允许切换：殖民者 {owner.Name.ToStringShort} 无其他可用殖民者床。");
                                }
                            }

                            // 警告：殖民者失去床位
                            if (colonistOwners.Count > 0)
                            {
                                hasBedWarning = true;
                                warningMessages.Add($"以下殖民者失去了床位分配: {string.Join(", ", colonistOwners.Select(o => o.Name.ToStringShort))}");
                                if (otherColonistBeds > 0)
                                    warningMessages.Add($"地图上仍有 {otherColonistBeds} 张殖民者床可用。");
                            }
                        }
                    }

                    // ==================== 执行切换 ====================
                    bed.ForOwnerType = targetType;

                    // ==================== 触发房间级联（关键修复） ====================
                    if (district != null)
                    {
                        district.Notify_RoomShapeOrContainedBedsChanged();
                        district.Room?.Notify_RoomShapeChanged();
                    }

                    // ==================== 构建返回消息 ====================
                    var result = new StringBuilder();
                    result.AppendLine($"床已切换为 {OwnerTypeLabel(targetType)}。");

                    // 房间角色变化
                    string newRoomRole = room?.Role?.label ?? "无";
                    if (oldRoomRole != newRoomRole)
                        result.AppendLine($"房间角色变更: {oldRoomRole} → {newRoomRole}");

                    // 级联信息
                    if (targetType == BedOwnerType.Prisoner)
                    {
                        // 重新统计变更后的同房床类型
                        int prisonerBedsAfter = 0, colonistBedsAfter = 0;
                        if (room != null)
                        {
                            foreach (var b in room.ContainedBeds ?? Enumerable.Empty<Building_Bed>())
                            {
                                if (b.def.building.bed_humanlike)
                                {
                                    if (b.ForPrisoners) prisonerBedsAfter++;
                                    else colonistBedsAfter++;
                                }
                            }
                        }
                        if (prisonerBedsAfter > 1)
                            result.AppendLine($"同房共 {prisonerBedsAfter} 张床已转为俘虏用床。");
                        if (colonistBedsAfter > 0)
                            result.AppendLine($"注意：同房仍有 {colonistBedsAfter} 张殖民者床，可能不符合囚室要求。");
                    }
                    else if (targetType == BedOwnerType.Colonist)
                    {
                        int prisonerBedsAfter = 0;
                        if (room != null)
                        {
                            foreach (var b in room.ContainedBeds ?? Enumerable.Empty<Building_Bed>())
                                if (b.def.building.bed_humanlike && b.ForPrisoners)
                                    prisonerBedsAfter++;
                        }
                        if (prisonerBedsAfter > 0)
                            result.AppendLine($"注意：同房仍有 {prisonerBedsAfter} 张俘虏床，房间仍为囚室。");
                    }

                    // 其他床概要
                    if (!string.IsNullOrEmpty(otherBedsSummary))
                        result.AppendLine(otherBedsSummary);

                    // 安全警告
                    if (hasBedWarning)
                    {
                        result.AppendLine();
                        foreach (var w in warningMessages)
                            result.AppendLine($"⚠ {w}");
                    }

                    // 已失床的（变更前持有床 thingIDNumber — 变更后不持有）
                    var stillOwnedIds = new HashSet<int>();
                    if (room != null)
                    {
                        foreach (var b in room.ContainedBeds ?? Enumerable.Empty<Building_Bed>())
                            foreach (var owner in b.OwnersForReading ?? Enumerable.Empty<Pawn>())
                                stillOwnedIds.Add(owner.thingIDNumber);
                    }
                    else
                    {
                        foreach (var owner in bed.OwnersForReading ?? Enumerable.Empty<Pawn>())
                            stillOwnedIds.Add(owner.thingIDNumber);
                    }
                    var lostBedIds = allAffectedPawnIds.Where(id => !stillOwnedIds.Contains(id)).ToList();
                    if (lostBedIds.Count > 0)
                    {
                        var lostBedPawnNames = PawnsFinder.AllMaps_FreeColonistsSpawned
                            .Where(p => lostBedIds.Contains(p.thingIDNumber))
                            .Select(p => p.Name.ToStringShort)
                            .ToList();
                        if (lostBedPawnNames.Count > 0)
                            result.AppendLine($"已解除床位分配: {string.Join(", ", lostBedPawnNames)}");
                    }

                    return ToolResult.Success(result.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"设置床类型失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var px)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var py)) return null;
            return (px, py, px, py);
        }

        private static string OwnerTypeLabel(BedOwnerType t) => t switch
        {
            BedOwnerType.Colonist => "殖民者",
            BedOwnerType.Prisoner => "俘虏",
            BedOwnerType.Slave => "奴隶",
            _ => t.ToString()
        };
    }
}
