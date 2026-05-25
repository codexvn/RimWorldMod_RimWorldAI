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
    public class Tool_SetResearchProject : ITool
    {
        public string Name => "set_research_project";
        public string Description => "设置当前研究项目。project_defName 需从枚举中选择（已动态列出所有可用项目）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                project_defName = new
                {
                    type = "string",
                    description = "研究项目 defName",
                    @enum = GetResearchProjectEnum()
                }
            },
            required = new[] { "project_defName" }
        });

        private static string[] GetResearchProjectEnum()
        {
            try
            {
                var projects = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                    .Select(d => d.defName)
                    .OrderBy(n => n)
                    .ToArray();
                return projects.Length > 0 ? projects : new[] { "Please_call_list_research_projects_first" };
            }
            catch
            {
                return new[] { "Please_call_list_research_projects_first" };
            }
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("project_defName", out var defNameProp))
                return ToolResult.Error("缺少 project_defName");

            var projectDefName = defNameProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(projectDefName))
                return ToolResult.Error("project_defName 不能为空。");

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    // 查找研究项目
                    var project = DefDatabase<ResearchProjectDef>.GetNamed(projectDefName, false);
                    if (project == null)
                        return ToolResult.Error($"未知研究项目: {projectDefName}。请从 project_defName 枚举中选择可用项目。");

                    var researchManager = Find.ResearchManager;
                    if (researchManager == null)
                        return ToolResult.Error("ResearchManager 不可用。");

                    // 使用 CanStartNow 完整校验（覆盖 8 项条件）
                    if (!project.CanStartNow)
                    {
                        var reasons = new List<string>();
                        if (project.IsFinished)
                            reasons.Add("研究项目已完成");
                        if (!project.PrerequisitesCompleted)
                        {
                            var unmet = project.prerequisites?.Where(p => !p.IsFinished)
                                .Select(p => p.label ?? p.defName).ToList();
                            if (unmet != null && unmet.Count > 0)
                                reasons.Add($"前置项目未完成: {string.Join(", ", unmet)}");
                            else
                                reasons.Add("前置条件未满足");
                        }
                        if (!project.TechprintRequirementMet)
                            reasons.Add($"科技蓝图要求未满足 (已应用 {project.TechprintsApplied}/{project.TechprintCount})");
                        if (project.requiredResearchBuilding != null && !project.PlayerHasAnyAppropriateResearchBench)
                            reasons.Add($"缺少必要研究设施: {project.requiredResearchBuilding.label}");
                        if (!project.PlayerMechanitorRequirementMet)
                            reasons.Add("需要机械师");
                        if (!project.AnalyzedThingsRequirementsMet)
                            reasons.Add("分析物要求未满足");
                        if (project.IsHidden)
                            reasons.Add("项目被隐藏（需要异常 DLC 或实体分析解锁）");
                        if (!project.InspectionRequirementsMet)
                            reasons.Add("检查要求未满足");
                        if (reasons.Count == 0)
                            reasons.Add("未知原因（CanStartNow 返回 false）");
                        return ToolResult.Error(
                            $"无法开始研究 {project.label} ({projectDefName})。原因: {string.Join("; ", reasons)}");
                    }

                    // 检查是否有有效的研究成本（baseCost/knowledgeCost 均为 0 时 SetCurrentProject 无效）
                    if (project.baseCost <= 0f && project.knowledgeCost <= 0f)
                        return ToolResult.Error($"研究项目 {project.label} ({projectDefName}) 无有效研究成本（baseCost 和 knowledgeCost 均为 0），SetCurrentProject 不会生效。");

                    var projLabel = project.label ?? projectDefName;

                    // 设置当前研究项目
                    researchManager.SetCurrentProject(project);

                    var sb = new StringBuilder();
                    sb.AppendLine($"已将研究项目设为: {projLabel} ({projectDefName})");

                    // 显示附加信息
                    if (project.baseCost > 0)
                        sb.AppendLine($"- 研究工作量: {project.baseCost:N0}");

                    if (project.requiredResearchBuilding != null)
                        sb.AppendLine($"- 需要研究设施: {project.requiredResearchBuilding.label}");

                    if (project.techLevel > 0)
                        sb.AppendLine($"- 科技等级: {project.techLevel}");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"设置研究项目失败: {ex.Message}");
                }
            });
        }
    }
}
