using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP.Helpers;

namespace RimWorldMCP.Tools
{
    public class Tool_GetResearchSpeed : ITool
    {
        public string Name => "get_research_speed";
        public string Description => "获取研究效率详情：当前正在研究的人员、研究速度、研究台系数、全局倍率和预计剩余时间。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前没有可用地图。");

                var rm = Find.ResearchManager;
                if (rm == null) return ToolResult.Error("ResearchManager 不可用。");

                var sb = new StringBuilder();
                sb.AppendLine("## 研究效率");

                // 当前项目
                var curProj = rm.GetProject();
                if (curProj == null)
                {
                    sb.AppendLine();
                    sb.AppendLine("- 当前研究: 无");
                    sb.AppendLine("- 提示: 使用 `set_research_project` 设置研究项目后，殖民者会自动开始研究");
                }
                else
                {
                    float progressReal = rm.GetProgress(curProj);
                    float cost = curProj.Cost;
                    float pct = curProj.ProgressPercent;
                    sb.AppendLine();
                    sb.AppendLine($"- 当前项目: **{curProj.label}** ({curProj.defName})");
                    sb.AppendLine($"- 进度: {progressReal:F0} / {cost:F0} ({(int)(pct * 100f)}%)");

                    // 查找正在研究的所有殖民者
                    var researchers = map.mapPawns.FreeColonistsSpawned
                        .Where(p => p.CurJobDef == JobDefOf.Research)
                        .OrderBy(p => p.thingIDNumber)
                        .ToList();

                    if (researchers.Count > 0)
                    {
                        float totalDailyProgress = 0f;
                        sb.AppendLine();
                        sb.AppendLine($"### 研究人员 ({researchers.Count} 人)");
                        sb.AppendLine();
                        sb.AppendLine("| 殖民者 | 研究速度 | 智力 | 所在研究台 | 台系数 | 每日产出 |");
                        sb.AppendLine("|--------|----------|------|-----------|--------|----------|");

                        foreach (var pawn in researchers)
                        {
                            string name = pawn.Name?.ToStringShort ?? pawn.LabelShortCap;
                            float speed = pawn.GetStatValue(StatDefOf.ResearchSpeed, true);
                            int intellectLevel = pawn.skills?.GetSkill(SkillDefOf.Intellectual)?.Level ?? 0;

                            // 查找 pawn 正在使用的研究台
                            var bench = pawn.CurJob?.GetTarget(TargetIndex.A).Thing as Building_ResearchBench;
                            float benchFactor = bench?.GetStatValue(StatDefOf.ResearchSpeedFactor, true) ?? 0f;
                            string benchName = bench?.def?.label ?? "—";
                            string benchFactorStr = bench != null ? $"{benchFactor * 100f:F0}%" : "—";

                            // 计算每日产出
                            float researchPerTick = speed * benchFactor * 0.00825f;
                            float dailyOutput = researchPerTick * GenDate.TicksPerDay;
                            totalDailyProgress += dailyOutput;

                            sb.AppendLine($"| {name} | {speed * 100f:F0}% | {intellectLevel} | {benchName} | {benchFactorStr} | {dailyOutput:F0} 点/天 |");
                        }

                        sb.AppendLine();

                        // 全局系数
                        float difficultyFactor = Find.Storyteller?.difficulty?.researchSpeedFactor ?? 1f;
                        float techLevelPenalty = curProj.CostFactor(Faction.OfPlayer?.def?.techLevel ?? TechLevel.Industrial);
                        float netDailyProgress = totalDailyProgress * difficultyFactor / techLevelPenalty;

                        sb.AppendLine("### 全局系数");
                        sb.AppendLine($"- 难度系数: ×{difficultyFactor * 100f:F0}% ({Find.Storyteller?.difficultyDef?.label ?? "?"})");
                        sb.AppendLine($"- 科技等级修正: ÷{techLevelPenalty:F2} (研究者 {Faction.OfPlayer?.def?.techLevel.ToString() ?? "?"} → 项目 {curProj.techLevel})");
                        sb.AppendLine($"- 净每日产出: {netDailyProgress:F0} 点/天");

                        // 预计剩余时间
                        if (cost > 0 && netDailyProgress > 0)
                        {
                            float remainingWork = cost - progressReal;
                            float remainingDays = remainingWork / netDailyProgress;
                            sb.AppendLine();
                            sb.AppendLine("### 预计完成");
                            sb.AppendLine($"- 剩余工作量: {remainingWork:F0}");
                            sb.AppendLine($"- 预计: {remainingDays:F1} 天 ({GameTimeHelper.CurrentTime()})");
                        }
                    }
                    else
                    {
                        sb.AppendLine();
                        sb.AppendLine("### 研究人员");
                        sb.AppendLine("- 无（没有殖民者正在执行研究任务）");

                        // 检查是否有 pawn 分配了研究但没工作起来
                        var intellectuals = map.mapPawns.FreeColonistsSpawned
                            .Where(p => !p.WorkTagIsDisabled(WorkTags.Intellectual))
                            .OrderBy(p => p.thingIDNumber)
                            .ToList();
                        if (intellectuals.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"- 可用智力型殖民者 ({intellectuals.Count} 人): {string.Join(", ", intellectuals.Select(p => p.Name?.ToStringShort ?? "?"))}");
                            sb.AppendLine("- 原因可能是工作优先级未设置智力，或不满足研究条件");
                        }
                    }
                }

                // 研究台清单
                var benches = map.listerBuildings.AllBuildingsColonistOfClass<Building_ResearchBench>()
                    .OrderBy(b => b.def.defName)
                    .ToList();
                if (benches.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("### 研究设施");
                    foreach (var bench in benches)
                    {
                        float factor = bench.GetStatValue(StatDefOf.ResearchSpeedFactor, true);
                        bool hasPower = bench.GetComp<CompPowerTrader>()?.PowerOn ?? true;
                        string powerTag = hasPower ? "" : " (断电)";
                        sb.AppendLine($"- {bench.def.label}: ×{factor * 100f:F0}%{powerTag}");
                    }
                }

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
