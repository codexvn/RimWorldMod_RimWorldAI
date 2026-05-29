using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetResearchProgress : ITool
    {
        public string Name => "get_research_progress";
        public string Description => "获取当前研究进度：当前正在研究的项目、完成百分比、所有项目的完成状态。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var researchManager = Find.ResearchManager;
                if (researchManager == null)
                    return ToolResult.Error("ResearchManager 不可用。");

                var sb = new StringBuilder();
                sb.AppendLine("## 研究进度报告");
                sb.AppendLine();

                // 当前研究
                var currentProj = researchManager.GetProject();
                if (currentProj != null)
                {
                    try
                    {
                        float progressReal = researchManager.GetProgress(currentProj);
                        float cost = currentProj.Cost;
                        float pct = currentProj.ProgressPercent;

                        sb.AppendLine("### 当前研究");
                        sb.AppendLine($"- **{currentProj.label}** ({currentProj.defName})");
                        sb.AppendLine($"- 进度: {progressReal:F0} / {cost:F0} ({(int)(pct * 100f)}%)");

                        // 进度条
                        string bar = BuildProgressBar(pct);
                        sb.AppendLine($"- {bar} {(int)(pct * 100f)}%");

                        // 研究工作量估算
                        if (cost > 0 && pct < 1f)
                        {
                            float remainingWork = cost - progressReal;
                            sb.AppendLine($"- 剩余: {remainingWork:F0}");
                        }

                        // 前置条件
                        if (currentProj.prerequisites != null && currentProj.prerequisites.Count > 0)
                        {
                            var prereqNames = currentProj.prerequisites.Select(p => p.label);
                            sb.AppendLine($"- 前置: {string.Join(", ", prereqNames)}");
                        }

                        // 解锁内容
                        if (currentProj.UnlockedDefs != null && currentProj.UnlockedDefs.Count > 0)
                        {
                            var unlocks = currentProj.UnlockedDefs.Take(5).Select(d => d.label ?? d.defName);
                            string more = currentProj.UnlockedDefs.Count > 5 ? $" 等{currentProj.UnlockedDefs.Count}项" : "";
                            sb.AppendLine($"- 解锁: {string.Join(", ", unlocks)}{more}");
                        }

                        // 研究台需求
                        if (currentProj.requiredResearchFacilities != null && currentProj.requiredResearchFacilities.Count > 0)
                        {
                            var facilities = currentProj.requiredResearchFacilities.Select(f => f.label);
                            sb.AppendLine($"- 需要研究设施: {string.Join(", ", facilities)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("### 当前研究");
                        sb.AppendLine($"- 项目: {currentProj.defName}");
                        sb.AppendLine($"- 进度获取失败: {ex.Message}");
                    }
                }
                else
                {
                    sb.AppendLine("### 当前研究");
                    sb.AppendLine("- 无（未设置研究项目）");
                    sb.AppendLine();
                    sb.AppendLine("提示: 使用 `list_research_projects` 查看可研究项目，使用 `set_research_project` 设置研究项目。");
                }

                // 所有项目状态
                sb.AppendLine();
                sb.AppendLine("### 全部项目状态");

                try
                {
                    var allProjects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                    if (allProjects != null && allProjects.Count > 0)
                    {
                        var completedList = new List<ResearchProjectDef>();
                        var inProgress = new List<(ResearchProjectDef proj, float progress)>();
                        var available = new List<ResearchProjectDef>();
                        var locked = new List<ResearchProjectDef>();

                        foreach (var proj in allProjects)
                        {
                            if (proj.IsFinished)
                                completedList.Add(proj);
                            else if (proj == currentProj)
                                continue; // already shown above
                            else
                            {
                                // Check if prerequisites are met
                                bool prereqMet = proj.PrerequisitesCompleted;
                                if (prereqMet)
                                {
                                    // Check if research was started
                                    float p = researchManager.GetProgress(proj); // 实际点数
                                    if (p > 0f)
                                        inProgress.Add((proj, p));
                                    else
                                        available.Add(proj);
                                }
                                else
                                {
                                    locked.Add(proj);
                                }
                            }
                        }

                        // 统计
                        int total = allProjects.Count;
                        int completed = completedList.Count;
                        int started = inProgress.Count;
                        int availCount = available.Count;
                        int lockedCount = locked.Count;
                        bool hasCurrent = currentProj != null;
                        sb.AppendLine($"- 总计: {total}项 | 已完成: {completed}项 | 当前: {(hasCurrent ? 1 : 0)}项");
                        sb.AppendLine($"- 有进度: {started}项 | 可开始: {availCount}项 | 未解锁: {lockedCount}项");
                        sb.AppendLine($"- 总完成率: {(int)(completed * 100f / total)}%");

                        // 已完成列表
                        if (completedList.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"### 已完成 ({completedList.Count} 项)");
                            foreach (var proj in completedList)
                            {
                                sb.AppendLine($"- ✅ {proj.label} ({proj.defName})");
                            }
                        }

                        // 进行中的其他项目
                        if (inProgress.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"### 有进度但未选为当前 ({inProgress.Count} 项)");
                            foreach (var (proj, p) in inProgress.OrderByDescending(x => x.progress))
                            {
                                float pct = proj.Cost > 0 ? p / proj.Cost : 0f;
                                sb.AppendLine($"- {proj.label} ({proj.defName}) — {p:F0} / {proj.Cost:F0} ({(int)(pct * 100f)}%)");
                            }
                        }

                        // 可开始的项目（取前 10）
                        if (available.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine($"### 可开始研究 ({available.Count} 项，显示前 10)");
                            foreach (var proj in available.Take(12))
                            {
                                sb.AppendLine($"- ⬜ {proj.label} ({proj.defName}) | 工作量: {proj.Cost:F0}");
                            }
                            if (available.Count > 12)
                                sb.AppendLine($"- ... 还有 {available.Count - 12} 项");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"- 项目列表获取失败: {ex.Message}");
                }

                // 研究速度
                try
                {
                    float researchSpeed = Find.Storyteller.difficulty.researchSpeedFactor;
                    sb.AppendLine();
                    sb.AppendLine("### 研究效率");
                    sb.AppendLine($"- 研究速度因数: {researchSpeed:P1}");
                }
                catch (Exception) { }

                return ToolResult.Success(sb.ToString());
            });
        }

        private static string BuildProgressBar(float pct)
        {
            int filled = (int)Math.Round(pct * 20);
            filled = Math.Max(0, Math.Min(20, filled));
            return $"[{new string('#', filled)}{new string('_', 20 - filled)}]";
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
