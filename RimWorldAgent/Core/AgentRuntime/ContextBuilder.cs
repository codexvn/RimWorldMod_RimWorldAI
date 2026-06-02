using System.Text;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>为 Agent 构建 Prompt。固定段落顺序保证 Prompt Cache 命中。</summary>
    public class ContextBuilder
    {
        private readonly Mcp.McpClient _mcp;

        public ContextBuilder(Mcp.McpClient mcp) { _mcp = mcp; }

        /// <summary>构建 Agent 完整 Prompt。</summary>
        public async Task<string> BuildAsync(bool isInterrupted = false)
        {
            var sb = new StringBuilder();

            // 中断通知注入（如有）
            if (isInterrupted && !string.IsNullOrEmpty(AgentOrchestrator.InterruptSummary))
            {
                sb.AppendLine(AgentOrchestrator.InterruptPromptPrefix);
                sb.AppendLine(AgentOrchestrator.InterruptSummary);
                sb.AppendLine(AgentOrchestrator.InterruptPromptSuffix);
                sb.AppendLine();
                AgentOrchestrator.InterruptSummary = "";
            }

            // Layer 1: World Summary (via MCP get_world_summary)
            sb.AppendLine(await BuildWorldSummaryAsync());
            sb.AppendLine();

            // Layer 6: Runtime info
            sb.AppendLine("## 运行信息");
            sb.AppendLine($"- Day: {AgentOrchestrator.GameDay}");

            // Layer 7: 当前模式指引
            var phaseHint = AgentOrchestrator.CurrentPhase switch
            {
                GamePhase.Plan => "## 当前模式: PLAN\n游戏已暂停。你现在可以：查询状态、制定计划、查看知识。\n**不能执行任何游戏操作**（建造/装备/征召/advance_tick/生产单据等）。\n制定计划后用 task_create 创建任务清单，用 task_list 检查现有任务。计划完成后调用 enter_act() 进入 ACT 模式执行。",
                GamePhase.Act => "## 当前模式: ACT\n游戏运行中。你现在可以执行所有操作。\n执行中及时用 task_update 更新任务状态，完成阶段性工作后用 task_list 检查进度。完成后如需重新规划调用 enter_plan()。",
                _ => null
            };
            if (phaseHint != null)
            {
                sb.AppendLine();
                sb.AppendLine(phaseHint);
            }

            return sb.ToString().TrimEnd();
        }

        private async Task<string> BuildWorldSummaryAsync()
        {
            try
            {
                var result = await _mcp.CallTool("get_world_summary");
                return result.Length > 0 ? result : "## 殖民地状态\n（无可用地图）";
            }
            catch
            {
                return "## 殖民地状态\n（MCP 连接不可用）";
            }
        }
    }
}
