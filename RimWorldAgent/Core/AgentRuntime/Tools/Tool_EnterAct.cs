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
                reason = new { type = "string", description = "执行原因（可选，日志用）" }
            }
        });

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var reason = args?.TryGetProperty("reason", out var reasonEl) == true ? reasonEl.GetString() ?? "" : "";
            AgentOrchestrator.EnterActPhase();
            var pace = AgentOrchestrator.PaceController;
            var mcp = AgentOrchestrator.SessionMcp;
            if (pace != null && mcp != null) await pace.ResumeForAction(mcp);
            return ($"已进入 Act 阶段，游戏已恢复。{reason}", false);
        }
    }
}
