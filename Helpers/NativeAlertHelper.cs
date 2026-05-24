using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using RimWorldMCP.Harmony;

namespace RimWorldMCP.Helpers
{
    /// <summary>
    /// 警报类别（与原生 Alert_* 子类的大致映射）。
    /// </summary>
    public enum AlertCategory
    {
        Idle,
        CrashRisk,
        Bleeding,
        Fleeing,
        FoodShortage,
        NoDefense,
        BedShortage,
        General
    }

    public static class NativeAlertHelper
    {
        // ========== 访问器（从 NotificationBus 自有镜像读取） ==========

        /// <summary>获取当前活跃警报的快照（拷贝自 NotificationBus 镜像，不持游戏对象引用）。</summary>
        public static IReadOnlyList<AlertInfo> GetActiveAlerts()
        {
            return NotificationBus.GetActiveAlerts();
        }

        // ========== 警报分类 ==========

        private static readonly Dictionary<AlertCategory, string[]> CategoryPatterns = new()
        {
            { AlertCategory.Idle, new[] { "Idle" } },
            { AlertCategory.CrashRisk, new[] { "Mood", "Boredom", "MentalBreak" } },
            { AlertCategory.Bleeding, new[] { "Tending", "Treatment", "Bleeding", "NeedsTreatment" } },
            { AlertCategory.Fleeing, new[] { "PanicFlee", "Berserk" } },
            { AlertCategory.FoodShortage, new[] { "Food", "Starving", "Starvation" } },
            { AlertCategory.NoDefense, new[] { "Defense", "NoDefense", "Undefended" } },
            { AlertCategory.BedShortage, new[] { "Bed", "NeedColonistBeds" } },
        };

        public static AlertCategory ClassifyAlert(AlertInfo alert)
        {
            foreach (var kv in CategoryPatterns)
            {
                foreach (var pat in kv.Value)
                {
                    if (alert.Key.Contains(pat))
                        return kv.Key;
                }
            }
            return AlertCategory.General;
        }

        public static Dictionary<AlertCategory, int> GetAlertCountsByCategory(IReadOnlyList<AlertInfo> alerts)
        {
            var dict = new Dictionary<AlertCategory, int>();
            foreach (var a in alerts)
            {
                var cat = ClassifyAlert(a);
                dict[cat] = dict.GetValueOrDefault(cat, 0) + 1;
            }
            return dict;
        }

        // ========== 格式化 ==========

        /// <summary>将活跃警报格式化为文本行（每行一条）。</summary>
        public static List<string> BuildAlertLines(IReadOnlyList<AlertInfo> alerts)
        {
            var lines = new List<string>(alerts.Count);
            foreach (var a in alerts)
            {
                string prio = a.Priority switch
                {
                    2 => "CRITICAL",
                    1 => "HIGH",
                    _ => "MEDIUM"
                };
                string label = string.IsNullOrEmpty(a.Label) ? a.Key : a.Label;
                if (a.Culprits.Length > 0)
                {
                    var names = a.Culprits.Take(3).Where(n => n != null);
                    lines.Add($"[{prio}] {label}: {string.Join(", ", names)}");
                }
                else
                {
                    lines.Add($"[{prio}] {label}");
                }
            }
            return lines;
        }

        /// <summary>紧凑摘要，用于 get_game_context 等快照场景。</summary>
        public static string GetAlertSummary(IReadOnlyList<AlertInfo> alerts)
        {
            if (alerts.Count == 0) return "无活跃警报";
            var counts = GetAlertCountsByCategory(alerts);
            var parts = new List<string>();
            foreach (var kv in counts.OrderByDescending(kv => kv.Key == AlertCategory.General ? 0 : 1))
            {
                string catName = kv.Key switch
                {
                    AlertCategory.Idle => "空闲",
                    AlertCategory.CrashRisk => "崩溃风险",
                    AlertCategory.Bleeding => "流血/需要治疗",
                    AlertCategory.Fleeing => "逃跑",
                    AlertCategory.FoodShortage => "食物不足",
                    AlertCategory.NoDefense => "无防御",
                    AlertCategory.BedShortage => "缺床",
                    _ => "其他"
                };
                parts.Add($"{catName}×{kv.Value}");
            }
            return $"{alerts.Count} 项警报: {string.Join(", ", parts)}";
        }

        // ========== 共享工具方法 ==========

        public static int CalcFoodDays(Map map, int colonistCount)
        {
            if (colonistCount <= 0) return 999;
            float total = 0f;
            var resources = map?.resourceCounter?.AllCountedAmounts;
            if (resources == null) return 999;
            foreach (var kv in resources)
            {
                var def = kv.Key;
                if (def.IsNutritionGivingIngestible && def.ingestible?.HumanEdible == true
                    && (def.ingestible?.foodType & FoodTypeFlags.Tree) == 0)
                {
                    total += kv.Value * (def.ingestible?.CachedNutrition ?? 0f);
                }
            }
            return (int)(total / (colonistCount * 1.6f));
        }

        // ========== 工具扩展方法 ==========

        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
            where TKey : notnull
        {
            return dict.TryGetValue(key, out var val) ? val : defaultValue;
        }
    }
}
