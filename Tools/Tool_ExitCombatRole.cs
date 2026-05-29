using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.AgentRuntime;
using Verse;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// Combat Agent 主动退出战斗角色，触发收尾流程。
    /// 由 AI 自主调用，不是 Runtime 替它判断战斗是否结束。
    /// </summary>
    public class Tool_ExitCombatRole : ITool, IHasAgentAffinity
    {
        public string Name => "exit_combat_role";
        public string Description => "退出战斗指挥官角色，恢复游戏速度，结束当前 Combat session。确保已处理完伤员和俘虏后再调用。";
        public AgentAffinity AgentAffinity => AgentAffinity.Combat;

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                summary = new
                {
                    type = "string",
                    description = "可选，战斗总结文本。简要描述: 敌人类型/数量、我方伤亡、俘虏数、战利品概要"
                }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string summary = "";
            if (args != null && args.Value.TryGetProperty("summary", out var jSummary))
                summary = jSummary.GetString() ?? "";

            return await McpCommandQueue.DispatchAsync(() =>
            {
                // 恢复游戏速度（如果处于暂停）
                var tm = Find.TickManager;
                if (tm != null && tm.Paused)
                {
                    tm.CurTimeSpeed = TimeSpeed.Normal;
                }

                var result = "战斗指挥官角色已退出。";
                if (!string.IsNullOrEmpty(summary))
                    result += $"\n\n战斗总结:\n{summary}";
                else
                    result += "\n（无战斗总结）";

                return ToolResult.Success(result);
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
