using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_GetWorkPriorities : ITool
    {
        public string Name => "get_work_priorities";
        public string Description => "获取所有殖民者的完整工作优先级表 (0-4)。0=不分配, 1=最高, 2=高, 3=普通, 4=最低。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                if (colonists == null || colonists.Count == 0)
                    return ToolResult.Success("## 工作优先级\n\n暂无自由殖民者。");

                var allWorkTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading
                    .Where(wt => wt.visible)
                    .OrderBy(wt => wt.defName ?? "")
                    .ToList();

                if (allWorkTypes.Count == 0)
                    return ToolResult.Success("## 工作优先级\n\n没有可用的工作类型。");

                var sb = new StringBuilder();
                sb.AppendLine("## 工作优先级");
                sb.AppendLine();

                // 表头
                sb.Append("| 殖民者");
                foreach (var wt in allWorkTypes)
                    sb.Append($" | {wt.labelShort ?? wt.defName}");
                sb.AppendLine(" |");

                // 分隔行
                sb.Append("|------");
                foreach (var wt in allWorkTypes)
                    sb.Append("|-----:");
                sb.AppendLine("|");

                // 每个殖民者一行
                foreach (var pawn in colonists)
                {
                    string name = pawn.Name.ToStringShort;
                    sb.Append($"| {name} (ID:{pawn.thingIDNumber})");

                    var workSettings = pawn.workSettings;
                    foreach (var wt in allWorkTypes)
                    {
                        if (workSettings == null)
                        {
                            sb.Append(" | -");
                            continue;
                        }

                        int priority = workSettings.GetPriority(wt);
                        sb.Append(priority > 0 ? $" | {priority}" : " | -");
                    }
                    sb.AppendLine(" |");
                }

                sb.AppendLine();
                sb.AppendLine("优先级: 1=最高 2=高 3=普通 4=最低 -=未分配");

                if (!Current.Game.playSettings.useWorkPriorities)
                    sb.AppendLine("\n[提示] 手动优先级未启用，当前显示的是默认分配（所有已启用工作类型为 3）。可通过游戏内\"工作\"标签页启用手动优先级。");

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
