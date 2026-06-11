using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_EnterAct : IInternalTool
    {
        public string Name => "enter_act";
        public string Description => "进入 Act 阶段，恢复游戏执行操作。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                speed = new { type = "string", description = "游戏速度: paused, normal, fast, superfast, ultrafast" },
                reason = new { type = "string", description = "执行原因（可选，日志用）" }
            },
            required = new[] { "speed" }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var speed = "superfast";
            if (args?.TryGetProperty("speed", out var speedEl) == true)
                speed = speedEl.GetString() ?? "superfast";
            var reason = args?.TryGetProperty("reason", out var reasonEl) == true ? reasonEl.GetString() ?? "" : "";

            if (AgentOrchestrator.PaceController == null || AgentOrchestrator.SessionMcp == null)
                return ("无法进入 Act 阶段：内部会话状态异常（PaceController/SessionMcp 未就绪），请稍后重试或检查 Agent 日志。", false);

            AgentOrchestrator.EnterActPhase();
            await AgentOrchestrator.PaceController.ResumeForAction(AgentOrchestrator.SessionMcp, speed);
            return ($"已进入 Act 阶段，游戏速度: {speed}。{reason}\n\n可使用 get_skills 和 active_skill 工具获取领域知识。", false);
        }
    }
}
