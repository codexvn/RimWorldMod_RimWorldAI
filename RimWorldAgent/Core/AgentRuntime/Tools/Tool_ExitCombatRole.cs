using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_ExitCombatRole : IInternalTool
    {
        public string Name => "exit_combat_role";
        public string Description => "退出战斗指挥官角色，恢复游戏速度。确保已处理完伤员和俘虏后再调用。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                summary = new { type = "string", description = "战斗总结文本" }
            }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var summary = "";
            if (args != null && args.Value.TryGetProperty("summary", out var s))
                summary = s.GetString() ?? "";
            AgentOrchestrator.NextAgentRequest = "overseer";
            var msg = string.IsNullOrEmpty(summary)
                ? "战斗指挥官角色已退出，回到总督。"
                : $"战斗指挥官已退出，回到总督。\n总结: {summary}";
            return Task.FromResult((msg, true));
        }
    }
}
