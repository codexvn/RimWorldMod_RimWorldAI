using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_StopResearch : ITool
    {
        public string Name => "stop_research";
        public string Description => "停止当前研究项目。停止后可重新分配研究人员到其他工作。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var rm = Find.ResearchManager;
                if (rm == null)
                    return ToolResult.Error("ResearchManager 不可用。");

                var curProj = rm.GetProject();
                if (curProj == null)
                    return ToolResult.Success("当前没有研究项目在进行中。");

                var projLabel = curProj.label ?? curProj.defName;
                float progress = Math.Min(1f, rm.GetProgress(curProj));

                rm.StopProject(curProj);

                var sb = new StringBuilder();
                sb.AppendLine($"已停止研究: {projLabel} ({curProj.defName})");
                sb.AppendLine($"- 停止前进度: {(int)(progress * 100f)}%");

                // 提示下一步
                var nextAvailable = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                    .FirstOrDefault(p => p.CanStartNow && p != curProj);
                if (nextAvailable != null)
                    sb.AppendLine($"- 可开始: {nextAvailable.label} ({nextAvailable.defName}) — 使用 set_research_project");
                else
                    sb.AppendLine("- 无其他可开始的研究项目");

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
