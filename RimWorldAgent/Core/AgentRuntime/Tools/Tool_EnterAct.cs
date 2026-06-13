using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_EnterAct : IInternalTool
    {
        public string Name => "enter_act";
        public string Description => "进入 Act 阶段，游戏保持冻结（仅 advance_tick 可推进）。在此阶段执行操作、调用工具。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                reason = new { type = "string", description = "执行原因（可选，日志用）" }
            }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var reason = args?.TryGetProperty("reason", out var reasonEl) == true ? reasonEl.GetString() ?? "" : "";

            if (AgentOrchestrator.PaceController == null || AgentOrchestrator.SessionMcp == null)
                return ("无法进入 Act 阶段：内部会话状态异常（PaceController/SessionMcp 未就绪），请稍后重试或检查 Agent 日志。", false);

            AgentOrchestrator.EnterActPhase();
            await AgentOrchestrator.PaceController.PauseForAction(AgentOrchestrator.SessionMcp);
            return ($"已进入 Act 阶段，游戏已冻结。{reason}\n\n在此阶段执行操作（如分配工作、建造蓝图等），需要用 advance_tick 推进游戏时间让操作生效。可使用 get_skills 和 active_skill 获取领域知识。", false);
        }
    }
}
