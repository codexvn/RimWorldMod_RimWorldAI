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
        private const float DurationSec = 0.8f;

        private class Observation
        {
            public Plan Plan;
            public float ExpireRealTime;
        }

        private static readonly Dictionary<Map, List<Observation>> _active = new Dictionary<Map, List<Observation>>();

        /// <summary>
        /// 在地图上显示 AI 观察标记，~0.8 秒后自动删除（实时钟，不受暂停影响）。
        /// 颜色默认 Cyan（青色），与用户常用的白/红/蓝区分。
        /// </summary>
        public static void Show(Map map, CellRect rect, string label, string? colorName = null)
        {
            if (map == null) return;

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
                ExpireRealTime = UnityEngine.Time.realtimeSinceStartup + DurationSec
            });
        }

        /// <summary>每帧调用，清理过期的观察标记（纯实时钟，不受暂停影响）</summary>
        public static void Tick(Map? map)
        {
            if (map == null) return;
            if (!_active.TryGetValue(map, out var list)) return;

            var nowReal = UnityEngine.Time.realtimeSinceStartup;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (nowReal < list[i].ExpireRealTime) continue;

                try
                {
                    var cells = new List<IntVec3>();
                    foreach (var c in list[i].Plan)
                        cells.Add(c);
                    foreach (var c in cells)
                        list[i].Plan.RemoveCell(c);
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
