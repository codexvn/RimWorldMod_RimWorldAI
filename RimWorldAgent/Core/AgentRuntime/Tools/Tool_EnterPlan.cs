using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_EnterPlan : IInternalTool
    {
        public string Name => "enter_plan";
        public string Description => "进入 Plan 阶段，暂停游戏进行思考规划。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                speed = new { type = "string", description = "Plan 阶段游戏速度: paused, normal, fast, superfast, ultrafast（默认 paused）" },
                reason = new { type = "string", description = "规划原因（可选，日志用）" }
            }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var speed = "paused";
            if (args?.TryGetProperty("speed", out var speedEl) == true)
                speed = speedEl.GetString() ?? "paused";
            var reason = args?.TryGetProperty("reason", out var reasonEl) == true ? reasonEl.GetString() ?? "" : "";

            if (AgentOrchestrator.PaceController == null || AgentOrchestrator.SessionMcp == null)
                return ("无法进入 Plan 阶段：内部会话状态异常（PaceController/SessionMcp 未就绪），请稍后重试或检查 Agent 日志。", false);

            AgentOrchestrator.EnterPlanPhase();
            await AgentOrchestrator.PaceController.PauseForPlanning(AgentOrchestrator.SessionMcp, speed);
            return ($"已进入 Plan 阶段，游戏速度: {speed}。{reason}\n\n可使用 get_skills 和 active_skill 工具获取领域知识。", false);
        }
    }
}
