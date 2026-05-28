using System.Text;
using RimWorld;
using Verse;

namespace RimWorldMCP.AgentRuntime
{
    /// <summary>
    /// 为每个 Agent 构建 Prompt。固定段落顺序保证 Prompt Cache 命中。
    /// </summary>
    public static class ContextBuilder
    {
        /// <summary>构建 Agent 的完整 Prompt 文本。</summary>
        public static string Build(AgentConfig config)
        {
            var sb = new StringBuilder();

            // --- Layer 1: System Prompt (cached) ---
            sb.AppendLine(config.SystemPrompt.Trim());
            sb.AppendLine();

            // --- Layer 2: Memory (cached) ---
            var memory = MemoryManager.GetMemoryText(config.Name);
            if (!string.IsNullOrEmpty(memory))
            {
                sb.AppendLine(memory);
                sb.AppendLine();
            }

            // --- Layer 3: World Summary (cached, built fresh each round) ---
            sb.AppendLine(BuildWorldSummary());
            sb.AppendLine();

            // --- Layer 4: Active Alerts (per-round) ---
            var alerts = AgentOrchestrator.DrainEvents(config.Name);
            if (!string.IsNullOrEmpty(alerts))
            {
                sb.AppendLine(alerts);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("## 最近事件\n（无）\n");
            }

            // --- Layer 5: TaskBoard (per-round) ---
            sb.AppendLine(TaskBoard.ToMarkdown());
            sb.AppendLine();

            // --- Layer 6: Agent 运行信息 ---
            sb.AppendLine($"## 运行信息");
            sb.AppendLine($"- 当前 Load: {Scheduler.LoadScore} ({Scheduler.Mode})");
            sb.AppendLine($"- 游戏时间: Day {AgentOrchestrator.CurrentDay}");
            sb.AppendLine($"- 可见工具数: {config.ToolCategories.Count} 类");
            sb.AppendLine();

            // Chunks + Feedback 在 SD K每轮 query 时动态追加，此处只构建基础层。

            return sb.ToString().TrimEnd();
        }

        private static string BuildWorldSummary()
        {
            var map = Find.CurrentMap;
            if (map == null) return "## 殖民地状态\n（无可用地图）";

            var sb = new StringBuilder();
            int day = Find.TickManager.TicksGame / 60000;
            int hour = (Find.TickManager.TicksGame / 2500) % 24;
            var season = GenLocalDate.Season(map).ToString();

            sb.AppendLine($"## 殖民地状态: {season}, Day {day}, {hour:D2}:00");
            sb.AppendLine();

            // 资源
            int totalColonists = PawnsFinder.AllMaps_FreeColonistsSpawned.Count;
            float foodDays = 0f;
            int steel = 0, components = 0, meds = 0;
            foreach (var kv in map.resourceCounter.AllCountedAmounts)
            {
                if (kv.Key.IsNutritionGivingIngestible && kv.Key.ingestible?.HumanEdible == true)
                    foodDays += kv.Value * kv.Key.ingestible.CachedNutrition / ((totalColonists > 0 ? totalColonists : 1) * 1.6f);
                if (kv.Key == ThingDefOf.Steel) steel = kv.Value;
                if (kv.Key == ThingDefOf.ComponentIndustrial || kv.Key == ThingDefOf.ComponentSpacer) components += kv.Value;
                if (kv.Key.IsMedicine) meds += kv.Value;
            }

            sb.AppendLine($"| 项目 | 值 |");
            sb.AppendLine($"|------|-----|");
            sb.AppendLine($"| 殖民者 | {totalColonists} |");
            sb.AppendLine($"| 食物储备 | {foodDays:F1} 天 |");
            sb.AppendLine($"| 钢 | {steel} |");
            sb.AppendLine($"| 零件 | {components} |");
            sb.AppendLine($"| 药品 | {meds} |");

            // 威胁
            int enemies = 0;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (pawn.Faction != null && pawn.Faction.HostileTo(Faction.OfPlayer) && !pawn.Downed)
                    enemies++;
            if (enemies > 0) sb.AppendLine($"| ⚠ 活跃敌人 | {enemies} |");

            // 研究
            var curProj = Find.ResearchManager.GetProject();
            if (curProj != null)
            {
                float progress = Find.ResearchManager.GetProgress(curProj);
                float pct = curProj.baseCost > 0 ? progress / curProj.baseCost * 100f : 0;
                sb.AppendLine($"| 研究 | {curProj.LabelCap} ({pct:F0}%) |");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
