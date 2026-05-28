using System;
using System.IO;
using System.Text;
using RimWorld;
using Verse;

namespace RimWorldMCP.AgentRuntime
{
    /// <summary>
    /// 殖民地每日报告生成器。Agent 每天结束时调用，持久化到 session 目录供长期趋势分析。
    /// </summary>
    public static class DailyReport
    {
        public static void Generate()
        {
            var map = Find.CurrentMap;
            if (map == null) return;

            var dir = TaskBoard.SessionDir;
            if (string.IsNullOrEmpty(dir)) return;

            int day = Find.TickManager.TicksGame / 60000;
            var season = GenLocalDate.Season(map).ToString();
            var dateStr = $"{GenLocalDate.Year(map)}-{season}-Day{day}";
            var path = Path.Combine(dir, $"report-day{day}.json");

            var sb = new StringBuilder();
            sb.AppendLine($"## 殖民地日报 — Day {day}, {dateStr}");
            sb.AppendLine();

            // 殖民者统计
            int colonists = PawnsFinder.AllMaps_FreeColonistsSpawned.Count;
            int idle = 0;
            foreach (var p in PawnsFinder.AllMaps_FreeColonistsSpawned)
                if (p.mindState?.IsIdle == true) idle++;
            sb.AppendLine($"- 殖民者: {colonists} (空闲: {idle})");

            // 财富
            if (map.wealthWatcher != null)
                sb.AppendLine($"- 殖民地财富: {map.wealthWatcher.WealthTotal:n0}");

            // 食物
            float foodDays = 0f;
            foreach (var kv in map.resourceCounter.AllCountedAmounts)
                if (kv.Key.IsNutritionGivingIngestible && kv.Key.ingestible?.HumanEdible == true)
                    foodDays += kv.Value * kv.Key.ingestible.CachedNutrition / (colonists > 0 ? colonists : 1) * 1.6f;
            sb.AppendLine($"- 食物: {foodDays:F1} 天");

            // 研究
            var proj = Find.ResearchManager.GetProject();
            if (proj != null)
            {
                float prog = Find.ResearchManager.GetProgress(proj);
                sb.AppendLine($"- 研究: {proj.LabelCap} ({prog / proj.baseCost * 100f:F0}%)");
            }

            // TaskBoard 摘要
            var tasks = TaskBoard.AllTasks;
            int running = 0, blocked = 0, completed = 0;
            foreach (var t in tasks)
            {
                if (t.State == TaskState.Running) running++;
                if (t.State == TaskState.Blocked) blocked++;
                if (t.State == TaskState.Completed) completed++;
            }
            sb.AppendLine($"- TaskBoard: {tasks.Count} 任务 (执行中:{running}, 受阻:{blocked}, 完成:{completed})");

            try
            {
                var reportDir = Path.Combine(dir, "reports");
                Directory.CreateDirectory(reportDir);
                File.WriteAllText(Path.Combine(reportDir, $"day{day:D4}.md"), sb.ToString());
                McpLog.Info($"[daily-report] Day {day} 报告已生成");
            }
            catch (Exception ex)
            {
                McpLog.Error($"[daily-report] 保存失败: {ex.Message}");
            }
        }
    }
}
