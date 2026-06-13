using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_EnterPlan : IInternalTool
    {
        public string Name => "enter_plan";
        public string Description => "进入 Plan 阶段，游戏完全冻结（仅 advance_tick 可推进）。在此阶段分析殖民地状态、制定行动计划。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                reason = new { type = "string", description = "规划原因（可选，日志用）" }
            }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var reason = args?.TryGetProperty("reason", out var reasonEl) == true ? reasonEl.GetString() ?? "" : "";

            if (AgentOrchestrator.PaceController == null || AgentOrchestrator.SessionMcp == null)
                return ("无法进入 Plan 阶段：内部会话状态异常（PaceController/SessionMcp 未就绪），请稍后重试或检查 Agent 日志。", false);

            AgentOrchestrator.EnterPlanPhase();
            await AgentOrchestrator.PaceController.PauseForPlanning(AgentOrchestrator.SessionMcp);
            return ($"已进入 Plan 阶段，游戏已完全冻结。{reason}\n\n只有 advance_tick 可以推进游戏时间。可使用 get_skills 和 active_skill 工具获取领域知识。", false);
        }
    }
}
