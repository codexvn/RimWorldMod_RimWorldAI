using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_AdviseAgent : IInternalTool
    {
        public string Name => "advise_agent";
        public string Description => "给其他 Agent 提供建议。切换到该 Agent 时，建议会自动附加在 Prompt 中。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                role = new { type = "string", description = "目标 Agent 角色: overseer / economy / combat / medic" },
                advice = new { type = "string", description = "建议内容" }
            },
            required = new[] { "role", "advice" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null
                || !args.Value.TryGetProperty("role", out var roleEl) || string.IsNullOrWhiteSpace(roleEl.GetString())
                || !args.Value.TryGetProperty("advice", out var adviceEl) || string.IsNullOrWhiteSpace(adviceEl.GetString()))
                return Task.FromResult(("参数 role 和 advice 不能为空。", false));
            var role = roleEl.GetString()!.ToLower();
            var advice = adviceEl.GetString()!;
            if (role != "overseer" && role != "economy" && role != "combat" && role != "medic")
                return Task.FromResult(($"未知角色: {role}。可选: overseer, economy, combat, medic", false));
            if (string.IsNullOrWhiteSpace(advice))
                return Task.FromResult(("建议内容不能为空。", false));
            AgentOrchestrator.AddAdvice(role, advice);
            return Task.FromResult(($"已给 {role} 提供建议: {advice}", false));
        }
    }
}
