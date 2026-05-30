using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    /// <summary>殖民者被困检测器 — 周期性扫描殖民者状态，检测倒地/路径阻断/极端温度/精神崩溃</summary>
    public static class TrappedColonistTracker
    {
        // 被困状态去重：key = "{pawnId}:{trapType}"
        private static readonly HashSet<string> _activeTrappedStates = new();

        // 当前被困列表缓存
        private static readonly List<TrappedColonistInfo> _currentTrapped = new();

        // 扫描间隔控制
        private static int _lastScanTick = -200;
        private const int ScanIntervalTicks = 200; // ~3.3s @1x speed

        // 通知冷却：两次通知之间最少间隔 3000 tick（~50s @1x speed）
        private static int _lastNotifyTick = -3000;
        private const int NotifyCooldownTicks = 3000;

        /// <summary>被困殖民者数据结构</summary>
        public class TrappedColonistInfo
        {
            public int PawnId { get; set; }
            public string Name { get; set; } = "";
            /// <summary>"Downed", "PathBlocked", "ExtremeTemp", "MentalBreak"</summary>
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

            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            var newDetections = new List<TrappedColonistInfo>();
            var currentKeys = new HashSet<string>();

            foreach (var pawn in colonists)
            {
                if (pawn.Map != map) continue;

                int id = pawn.thingIDNumber;
                string name = pawn.Name.ToStringShort;
                var pos = pawn.Position;

                // === 检测 1: 倒地 ===
                if (pawn.Downed)
                {
                    string key = $"{id}:Downed";
                    currentKeys.Add(key);
                    if (_activeTrappedStates.Add(key))
                    {
                        newDetections.Add(new TrappedColonistInfo
                        {
                            PawnId = id, Name = name, TrapType = "Downed",
                            Detail = BuildDownedDetail(pawn),
                            PosX = pos.x, PosZ = pos.z, DetectedTick = tick
                        });
                    }
                }

                // === 检测 2: 精神崩溃 ===
                if (pawn.InMentalState)
                {
                    string key = $"{id}:MentalBreak";
                    currentKeys.Add(key);
                    if (_activeTrappedStates.Add(key))
                    {
                        string severity = ClassifyMentalBreak(pawn.MentalState.def);
                        string stateLabel = pawn.MentalState.InspectLine ?? pawn.MentalState.def.label;
                        newDetections.Add(new TrappedColonistInfo
                        {
                            PawnId = id, Name = name, TrapType = "MentalBreak",
                            Detail = $"{stateLabel} (严重度: {severity})",
                            PosX = pos.x, PosZ = pos.z, DetectedTick = tick
                        });
                    }
                }

                // === 检测 3: 极端温度 ===
                float ambient = pawn.AmbientTemperature;
                var comfortRange = pawn.ComfortableTemperatureRange();
                if (ambient < comfortRange.min - 5f || ambient > comfortRange.max + 5f)
                {
                    string direction = ambient < comfortRange.min ? "过冷" : "过热";
                    string key = $"{id}:ExtremeTemp";
                    currentKeys.Add(key);
                    if (_activeTrappedStates.Add(key))
                    {
                        newDetections.Add(new TrappedColonistInfo
                        {
                            PawnId = id, Name = name, TrapType = "ExtremeTemp",
                            Detail = $"{direction} {ambient:F0}°C (舒适范围 {comfortRange.min:F0}~{comfortRange.max:F0}°C)",
                            PosX = pos.x, PosZ = pos.z, DetectedTick = tick
                        });
                    }
                }

                // === 检测 4: 路径阻断 ===
                // 倒地/精神崩溃的殖民者本身已无法移动，跳过
                if (!pawn.Downed && !pawn.InMentalState)
                {
                    if (IsPathBlocked(pawn, map))
                    {
                        string key = $"{id}:PathBlocked";
                        currentKeys.Add(key);
                        if (_activeTrappedStates.Add(key))
                        {
                            newDetections.Add(new TrappedColonistInfo
                            {
                                PawnId = id, Name = name, TrapType = "PathBlocked",
                                Detail = BuildPathBlockedDetail(pawn, map),
                                PosX = pos.x, PosZ = pos.z, DetectedTick = tick
                            });
                        }
                    }
                }
            }

            // 清除已解除的被困状态
            _activeTrappedStates.RemoveWhere(k => !currentKeys.Contains(k));

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
            _activeTrappedStates.Clear();
            _currentTrapped.Clear();
            _lastScanTick = -200;
            _lastNotifyTick = -3000;
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

        private static string BuildDownedDetail(Pawn pawn)
        {
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            int rescuers = 0;
            foreach (var other in colonists)
            {
                if (other == pawn || other.Downed) continue;
                if (other.CanReach(pawn, PathEndMode.Touch, Danger.Some))
                    rescuers++;
            }
            string rescueInfo = rescuers > 0 ? $"附近有{rescuers}人可达" : "无人可达！";
            return $"在({pawn.Position.x},{pawn.Position.z})倒地，{rescueInfo}";
        }

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

        private static string ClassifyMentalBreak(MentalStateDef def)
        {
            if (def == MentalStateDefOf.Berserk
                || def.defName.Contains("MurderousRage")
                || def.defName.Contains("SadisticRage"))
                return "严重";
            if (def == MentalStateDefOf.PanicFlee
                || def.defName.Contains("Binging")
                || def.defName.Contains("WanderPsychotic"))
                return "中等";
            return "轻度";
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
                SimpleMspServer.McpServiceHost.Instance?.SendEvent("game/trapped", payload);
            }
            catch (Exception ex) { Log.Warning($"[TrappedColonistTracker] SSE 推送失败: {ex.Message}"); }
        }
    }
}
