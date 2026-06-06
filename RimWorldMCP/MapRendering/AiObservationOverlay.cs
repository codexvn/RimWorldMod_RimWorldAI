using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// AI 观察覆盖层 — 在地图上短暂显示半透明彩色标记，告知玩家 AI 正在关注这些区域。
    /// 复用 RimWorld 原版 Plan 渲染体系，通过 [AI] 前缀标签与用户规划标记区分。
    /// </summary>
    public static class AiObservationOverlay
    {
        private const string LabelPrefix = "[AI]";
        private const int DefaultDurationTicks = 300;

        private class Observation
        {
            public Plan Plan;
            public int ExpireTick;
            public float ExpireRealTime; // 暂停时走实时钟兜底
        }

        private static readonly Dictionary<Map, List<Observation>> _active = new Dictionary<Map, List<Observation>>();
        private const float RealTimeFallbackSec = 10f; // 暂停超过此时长强制执行游戏 tick 清理

        /// <summary>
        /// 在地图上显示 AI 观察标记，expireTicks 后自动删除。
        /// 颜色默认 Cyan（青色），与用户常用的白/红/蓝区分。
        /// </summary>
        public static void Show(Map map, CellRect rect, string label, string? colorName = null, int expireTicks = DefaultDurationTicks)
        {
            if (map == null) return;

            // 解析颜色
            ColorDef? colorDef = null;
            if (!string.IsNullOrEmpty(colorName))
            {
                colorDef = Designator_Plan_Add.Colors.FirstOrDefault(c =>
                    string.Equals(c.defName, "Plan" + colorName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.defName, colorName, StringComparison.OrdinalIgnoreCase));
            }
            colorDef ??= Designator_Plan_Add.Colors.FirstOrDefault(c => c.defName == "PlanCyan")
                         ?? Designator_Plan_Add.Colors.First();

            var planManager = map.planManager;
            var plan = new Plan(colorDef, planManager);
            plan.RenamableLabel = $"{LabelPrefix} {label}";

            foreach (var cell in rect)
                plan.AddCell(cell);

            if (!_active.ContainsKey(map))
                _active[map] = new List<Observation>();
            _active[map].Add(new Observation
            {
                Plan = plan,
                ExpireTick = Find.TickManager.TicksGame + Math.Max(1, expireTicks),
                ExpireRealTime = UnityEngine.Time.realtimeSinceStartup + RealTimeFallbackSec
            });
        }

        /// <summary>每帧调用，清理过期的观察标记（游戏 tick 优先，暂停时走实时钟兜底）</summary>
        public static void Tick(Map? map)
        {
            if (map == null) return;
            if (!_active.TryGetValue(map, out var list)) return;

            var tick = Find.TickManager.TicksGame;
            var nowReal = UnityEngine.Time.realtimeSinceStartup;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var obs = list[i];
                // 游戏 tick 到期，或暂停超时（实时钟兜底）
                if (tick < obs.ExpireTick && nowReal < obs.ExpireRealTime) continue;

                // 只在暂停导致的实时钟清理时才打日志
                if (tick < obs.ExpireTick && nowReal >= obs.ExpireRealTime)
                    McpLog.Info($"[AiObservationOverlay] 暂停中实时清理: {obs.Plan.RenamableLabel}");

                // 清理：逐格移除 → 最后一格触发 Deregister
                try
                {
                    var cells = new List<IntVec3>();
                    foreach (var c in obs.Plan)
                        cells.Add(c);
                    foreach (var c in cells)
                        obs.Plan.RemoveCell(c);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[AiObservationOverlay] 清理标记失败: {ex.Message}");
                }
                list.RemoveAt(i);
            }

            if (list.Count == 0)
                _active.Remove(map);
        }
    }
}
