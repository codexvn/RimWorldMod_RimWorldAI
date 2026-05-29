using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    /// <summary>物品腐坏/耐久降低追踪器 — 周期性扫描地图物品，通知 AI 处理危险物品</summary>
    public static class DeteriorationTracker
    {
        // 三个阈值的已通知物品 ID Set
        private static readonly HashSet<int> _threshold20 = new();
        private static readonly HashSet<int> _threshold50 = new();
        private static readonly HashSet<int> _threshold70 = new();

        // 当前最危险的 10 个物品缓存
        private static List<DeterioratingItem> _topDangerous = new();

        // 扫描间隔控制
        private static int _lastScanTick = -6000;
        private const int ScanIntervalTicks = 6000;

        // 通知冷却：两次恶化警告之间最少间隔 15000 tick（约 4 分钟/1x 速度）
        private static int _lastNotifiedTick = -15000;
        private const int NotifyCooldownTicks = 15000;

        /// <summary>恶化物品数据结构</summary>
        public class DeterioratingItem
        {
            public int ThingID { get; set; }
            public string Label { get; set; } = "";
            public int PosX { get; set; }
            public int PosZ { get; set; }
            /// <summary>恶化百分比 0~100</summary>
            public double DegradationPct { get; set; }
            /// <summary>"腐烂" 或 "耐久降低"</summary>
            public string DangerType { get; set; } = "";
            /// <summary>额外细节（如 HP 信息）</summary>
            public string Detail { get; set; } = "";
        }

        /// <summary>
        /// 周期性检查：扫描地图物品，跨阈值时返回通知文本，否则返回 null。
        /// 必须在主线程调用。
        /// </summary>
        public static string? CheckAndNotify(Map map)
        {
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (tick - _lastScanTick < ScanIntervalTicks)
                return null;
            _lastScanTick = tick;

            var allItems = ScanDeterioratingItems(map);

            // 更新 Top10 缓存
            _topDangerous = allItems.OrderByDescending(i => i.DegradationPct).Take(10).ToList();

            // 检查是否有物品跨过新阈值
            var newNotified = new List<DeterioratingItem>();
            foreach (var item in allItems)
            {
                if (item.DegradationPct >= 70.0 && _threshold70.Add(item.ThingID))
                    newNotified.Add(item);
                else if (item.DegradationPct >= 50.0 && _threshold50.Add(item.ThingID))
                    newNotified.Add(item);
                else if (item.DegradationPct >= 20.0 && _threshold20.Add(item.ThingID))
                    newNotified.Add(item);
            }

            // 清理已不存在的物品 ID
            var existingIds = new HashSet<int>(allItems.Select(i => i.ThingID));
            CleanThreshold(_threshold20, existingIds);
            CleanThreshold(_threshold50, existingIds);
            CleanThreshold(_threshold70, existingIds);

            if (newNotified.Count == 0)
                return null;

            // 冷却期内不重复推送
            if (tick - _lastNotifiedTick < NotifyCooldownTicks)
                return null;
            _lastNotifiedTick = tick;

            var sb = new StringBuilder();
            sb.AppendLine("## 物品恶化警告");
            foreach (var item in newNotified.OrderByDescending(i => i.DegradationPct))
            {
                sb.AppendLine($"- {item.Label} ({item.DangerType}: {item.DegradationPct:F0}%) 位置({item.PosX},{item.PosZ}) ID={item.ThingID}");
                if (!string.IsNullOrEmpty(item.Detail))
                    sb.AppendLine($"  {item.Detail}");
            }
            sb.AppendLine("\n请检查问题物品并及时处理：放入仓库、建屋顶、屠宰或丢弃。可使用 get_deteriorating_items 查看详情。");
            return sb.ToString().TrimEnd();
        }

        /// <summary>获取缓存中的最危险物品列表</summary>
        public static List<DeterioratingItem> GetTopDangerous(int count = 10)
        {
            return _topDangerous.Take(count).ToList();
        }

        /// <summary>清空所有状态（新游戏/加载存档时调用）</summary>
        public static void Reset()
        {
            _threshold20.Clear();
            _threshold50.Clear();
            _threshold70.Clear();
            _topDangerous.Clear();
            _lastScanTick = -6000;
            _lastNotifiedTick = -15000;
        }

        /// <summary>扫描地图，找出所有正在腐烂或露天掉耐久的物品</summary>
        private static List<DeterioratingItem> ScanDeterioratingItems(Map map)
        {
            var results = new List<DeterioratingItem>();

            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.Fogged()) continue;
                // === 腐烂检测 ===
                var compRottable = thing.TryGetComp<CompRottable>();
                if (compRottable != null && compRottable.Active && compRottable.Stage == RotStage.Fresh)
                {
                    double pct = compRottable.RotProgressPct * 100.0;
                    if (pct > 0.001)
                    {
                        results.Add(new DeterioratingItem
                        {
                            ThingID = thing.thingIDNumber,
                            Label = thing.LabelCap,
                            PosX = thing.Position.x,
                            PosZ = thing.Position.z,
                            DegradationPct = pct,
                            DangerType = "腐烂",
                            Detail = $"新鲜度剩余约 {100.0 - pct:F0}%"
                        });
                        continue;
                    }
                }

                // === 露天耐久降低检测 ===
                if (thing.def.CanEverDeteriorate && thing.HitPoints > 0 && thing.HitPoints < thing.MaxHitPoints)
                {
                    var pos = thing.Position;
                    if (!pos.Roofed(map))
                    {
                        double pct = (1.0 - (double)thing.HitPoints / thing.MaxHitPoints) * 100.0;
                        results.Add(new DeterioratingItem
                        {
                            ThingID = thing.thingIDNumber,
                            Label = thing.LabelCap,
                            PosX = pos.x,
                            PosZ = pos.z,
                            DegradationPct = pct,
                            DangerType = "耐久降低",
                            Detail = $"HP: {thing.HitPoints}/{thing.MaxHitPoints}"
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>从阈值 Set 中移除已不存在的物品 ID</summary>
        private static void CleanThreshold(HashSet<int> threshold, HashSet<int> existingIds)
        {
            if (threshold.Count == 0) return;
            threshold.RemoveWhere(id => !existingIds.Contains(id));
        }
    }
}
