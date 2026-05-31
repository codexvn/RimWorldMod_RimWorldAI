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
                speed = new { type = "string", description = "游戏速度: paused, normal, fast, superfast, ultrafast" }
            },
            required = new[] { "speed" }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var speed = "superfast";
            if (args?.TryGetProperty("speed", out var speedEl) == true)
                speed = speedEl.GetString() ?? "superfast";

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (AgentOrchestrator.PaceController == null || AgentOrchestrator.SessionMcp == null)
            {
                if (DateTime.UtcNow > deadline)
                    return ("无法进入 Act 阶段：会话状态未就绪，请稍后重试。", false);
                await Task.Delay(100);
            }

            AgentOrchestrator.EnterActPhase();
            await AgentOrchestrator.PaceController.ResumeForAction(AgentOrchestrator.SessionMcp, speed);
            return ($"已进入 Act 阶段，游戏速度: {speed}。", false);
        }
    }
}
