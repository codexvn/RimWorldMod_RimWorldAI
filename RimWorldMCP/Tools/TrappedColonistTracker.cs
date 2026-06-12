using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RimWorldMCP.Constants;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    /// <summary>殖民者路径阻断检测器 — 周期性扫描殖民者是否能到达床或食物</summary>
    public static class TrappedColonistTracker
    {
        private static readonly HashSet<string> _activeTrapped = new();
        private static readonly List<TrappedColonistInfo> _currentTrapped = new();
        private static int _lastScanTick = -200;
        private const int ScanIntervalTicks = 200;
        private const int NotifyCooldownTicks = 3000;
        private static int _lastNotifyTick = -3000;

        public class TrappedColonistInfo
        {
            public int PawnId { get; set; }
            public string Name { get; set; } = "";
            public string TrapType { get; set; } = "";
            public string Detail { get; set; } = "";
            public int PosX { get; set; }
            public int PosZ { get; set; }
            public int DetectedTick { get; set; }
        }

        /// <summary>
        /// 周期性检查：扫描殖民者被困状态，有新检测时返回列表，否则返回 null。
        /// 必须在主线程调用。
        /// </summary>
        public static List<TrappedColonistInfo>? CheckAndNotify(Map map)
        {
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (tick - _lastScanTick < ScanIntervalTicks)
                return null;
            _lastScanTick = tick;

            // 拷贝快照，防止同帧内其他 Harmony Patch 修改殖民者列表导致枚举异常
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned.ToList();
            var newDetections = new List<TrappedColonistInfo>();
            var currentKeys = new HashSet<string>();

            foreach (var pawn in colonists)
            {
                if (pawn.Map != map) continue;

                int id = pawn.thingIDNumber;
                string name = pawn.Name.ToStringShort;
                var pos = pawn.Position;

                // 倒地/精神崩溃的殖民者本身已无法移动，排除
                if (pawn.Downed || pawn.InMentalState) continue;

                // 只检测路径阻断：无法到达任何床或食物
                if (!IsPathBlocked(pawn, map)) continue;

                string key = $"{id}:PathBlocked";
                currentKeys.Add(key);
                if (_activeTrapped.Add(key))
                {
                    newDetections.Add(new TrappedColonistInfo
                    {
                        PawnId = id, Name = name, TrapType = "PathBlocked",
                        Detail = BuildPathBlockedDetail(pawn, map),
                        PosX = pos.x, PosZ = pos.z, DetectedTick = tick
                    });
                }
            }

            // 清除已解除的被困状态
            _activeTrapped.RemoveWhere(k => !currentKeys.Contains(k));

            // 密封房间检测（与 PathBlocked 并行）
            var newSealed = CheckSealedRooms(map, colonists);
            foreach (var s in newSealed)
            {
                newDetections.Add(s);
                _activeTrapped.Add($"{s.PawnId}:{s.TrapType}");
            }

            // 更新当前被困列表
            _currentTrapped.Clear();
            _currentTrapped.AddRange(newDetections);

            if (newDetections.Count == 0)
                return null;

            // 推送 SSE 事件
            PushTrappedEvent(newDetections);

            // 冷却期内不重复推送通知
            if (tick - _lastNotifyTick < NotifyCooldownTicks)
                return null;
            _lastNotifyTick = tick;

            return newDetections;
        }

        /// <summary>获取当前被困殖民者列表</summary>
        public static List<TrappedColonistInfo> GetCurrentTrapped()
        {
            return _currentTrapped.ToList();
        }

        /// <summary>清空所有状态（新游戏/加载存档时调用）</summary>
        public static void Reset()
        {
            _activeTrapped.Clear();
            _currentTrapped.Clear();
            _lastScanTick = -200;
            _lastNotifyTick = -3000;
        }

        // ========== 密封房间检测 ==========

        private static List<TrappedColonistInfo> CheckSealedRooms(Map map, List<Pawn> colonists)
        {
            var results = new List<TrappedColonistInfo>();
            int tick = Find.TickManager?.TicksGame ?? 0;

            // 收集候选房间: 有床/工作台/存储区的 ProperRoom
            var candidateRooms = new HashSet<Room>();
            foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
            {
                var room = bed.GetRoom(RegionType.Set_All);
                if (room != null && room.ProperRoom && !room.IsHuge && !room.TouchesMapEdge)
                    candidateRooms.Add(room);
            }
            foreach (var bench in map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>())
            {
                var room = bench.GetRoom(RegionType.Set_All);
                if (room != null && room.ProperRoom && !room.IsHuge && !room.TouchesMapEdge)
                    candidateRooms.Add(room);
            }
            foreach (var zone in map.zoneManager.AllZones)
            {
                if (zone is Zone_Stockpile || zone is Zone_Growing)
                {
                    var room = zone.Cells.FirstOrDefault().GetRoom(map);
                    if (room != null && room.ProperRoom && !room.IsHuge && !room.TouchesMapEdge)
                        candidateRooms.Add(room);
                }
            }

            foreach (var room in candidateRooms)
            {
                var cells = room.Cells.ToList();
                if (cells.Count == 0) continue;

                bool accessible = false;
                foreach (var c in colonists)
                {
                    if (c.Map != map) continue;
                    if (c.Downed || c.InMentalState) continue;
                    if (c.CanReach(cells[0], PathEndMode.OnCell, Danger.Some))
                    {
                        accessible = true;
                        break;
                    }
                }
                if (accessible) continue;

                var center = cells[cells.Count / 2];
                string contents = BuildRoomContents(room, map);
                string key = $"Room:{center.x},{center.z}:SealedRoom";
                if (_activeTrapped.Contains(key)) continue;

                results.Add(new TrappedColonistInfo
                {
                    PawnId = room.ID,
                    Name = $"密封房间 ({center.x},{center.z})",
                    TrapType = "SealedRoom",
                    Detail = $"在({center.x},{center.z})周围，{contents}，无殖民者可到达的入口",
                    PosX = center.x,
                    PosZ = center.z,
                    DetectedTick = tick
                });
            }
            return results;
        }

        private static string BuildRoomContents(Room room, Map map)
        {
            var parts = new List<string>();
            int bedCount = 0, benchCount = 0, stockpileCount = 0, growingCount = 0;

            foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
                if (bed.GetRoom(RegionType.Set_All) == room) bedCount++;
            foreach (var bench in map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>())
                if (bench.GetRoom(RegionType.Set_All) == room) benchCount++;
            foreach (var zone in map.zoneManager.AllZones)
            {
                if (zone.Cells.Any(c => c.GetRoom(map) == room))
                {
                    if (zone is Zone_Stockpile) stockpileCount++;
                    else if (zone is Zone_Growing) growingCount++;
                }
            }

            if (bedCount > 0) parts.Add($"床×{bedCount}");
            if (benchCount > 0) parts.Add($"工作台×{benchCount}");
            if (stockpileCount > 0) parts.Add($"存储区×{stockpileCount}");
            if (growingCount > 0) parts.Add($"种植区×{growingCount}");
            return parts.Count > 0 ? string.Join(", ", parts) : "内容物";
        }

        // ========== 路径阻断检测 ==========

        private static bool IsPathBlocked(Pawn pawn, Map map)
        {
            // 检查是否能到达任意一张床
            var beds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>();
            foreach (var bed in beds)
            {
                if (!bed.Spawned || bed.Fogged()) continue;
                if (pawn.CanReach(bed, PathEndMode.OnCell, Danger.Some))
                    return false; // 能到达床，未被困
            }

            // 检查是否能到达任意食物源
            var foodThings = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSource);
            foreach (var food in foodThings)
            {
                if (!food.Spawned || food.Fogged()) continue;
                if (pawn.CanReach(food, PathEndMode.OnCell, Danger.Some))
                    return false; // 能到达食物，未被困
            }

            // 无法到达床和食物 = 被困
            return true;
        }

        // ========== 辅助方法 ==========

        private static string BuildPathBlockedDetail(Pawn pawn, Map map)
        {
            var pos = pawn.Position;
            // 检查周围是否有门被锁/墙被封
            for (int i = 0; i < 4; i++)
            {
                var adj = pos + GenAdj.CardinalDirections[i];
                if (!adj.InBounds(map)) continue;
                var building = adj.GetEdifice(map);
                if (building is Building_Door door && !door.Open)
                    return $"在({pos.x},{pos.z})被困，门已关闭";
            }
            return $"在({pos.x},{pos.z})被困，无法到达任何床铺或食物";
        }

        private static void PushTrappedEvent(List<TrappedColonistInfo> detections)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "trapped_colonist",
                    colonists = detections.Select(d => new
                    {
                        id = d.PawnId,
                        name = d.Name,
                        trapType = d.TrapType,
                        detail = d.Detail,
                        pos_x = d.PosX,
                        pos_z = d.PosZ
                    })
                });
                McpServiceManager.Host?.SendEvent(McpChannels.GameTrapped, payload);
            }
            catch (Exception ex) { McpLog.Warn($"[TrappedColonistTracker] SSE 推送失败: {ex.Message}"); }
        }
    }
}
