using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace RimworkAgent.Core.AgentRuntime
{
    /// <summary>殖民地每日报告生成器。Agent 每天结束时调用，持久化到 session 目录。</summary>
    public class DailyReport
    {
        private readonly Mcp.McpClient _mcp;

        public DailyReport(Mcp.McpClient mcp) { _mcp = mcp; }

        public async Task GenerateAsync()
        {
            var dir = TaskBoard.SessionDir;
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                var summary = await _mcp.CallTool("get_world_summary");
                var day = AgentOrchestrator.GameDay;
                var reportDir = Path.Combine(dir, "reports");
                Directory.CreateDirectory(reportDir);

                var sb = new StringBuilder();
                sb.AppendLine($"# 殖民地日报 — Day {day}");
                sb.AppendLine();
                sb.AppendLine(summary);
                sb.AppendLine();
                sb.AppendLine("## TaskBoard 状态");
                sb.AppendLine(TaskBoard.ToMarkdown());

                File.WriteAllText(Path.Combine(reportDir, $"day{day:D4}.md"), sb.ToString());
                CoreLog.Info($"[DailyReport] Day {day} 报告已生成");
            }
            catch (Exception ex) { CoreLog.Error($"[DailyReport] 生成失败: {ex.Message}"); }
        }
    }
}
