using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_SwitchAgent : IInternalTool
    {
        public string Name => "switch_agent";
        public string Description => "切换当前活跃 Agent 角色。当前 Agent 休眠，目标 Agent 唤醒并消费其队列中的事件。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                role = new { type = "string", description = "目标 Agent 角色: overseer / economy / combat / medic" }
            },
            required = new[] { "role" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null || !args.Value.TryGetProperty("role", out var roleEl) || string.IsNullOrWhiteSpace(roleEl.GetString()))
                return Task.FromResult(("角色名称不能为空。", false));
            var role = roleEl.GetString()!.ToLower();
            if (role != "overseer" && role != "economy" && role != "combat" && role != "medic")
                return Task.FromResult(($"未知角色: {role}。可选: overseer, economy, combat, medic", false));
            if (AgentOrchestrator.ActiveAgent == role)
                return Task.FromResult(($"当前已是 {role} 角色，无需切换。", false));
            AgentOrchestrator.NextAgentRequest = role;
            return Task.FromResult(($"正在切换到 {role}，当前会话将结束。", true));
        }
    }
}
