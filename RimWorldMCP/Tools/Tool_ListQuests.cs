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
    public class Tool_ListQuests : ITool
    {
        public string Name => "list_quests";
        public string Description => "列出当前所有任务。可按状态过滤：available（可接受）、ongoing（进行中）、all（全部）。分页返回。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                filter = new { type = "string", @enum = new[] { "available", "ongoing", "all" }, description = "过滤状态", @default = "all" },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认10，最大50", @default = 10 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var filter = "all";
            int page = 1, pageSize = 10;
            if (args != null)
            {
                if (args.Value.TryGetProperty("filter", out var f)) filter = f.GetString() ?? "all";
                if (args.Value.TryGetProperty("page", out var jp)) page = Math.Max(1, jp.GetInt32());
                if (args.Value.TryGetProperty("page_size", out var jps)) pageSize = Math.Max(1, Math.Min(50, jps.GetInt32()));
            }

            var capFilter = filter; var capPage = page; var capPageSize = pageSize;
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var all = Find.QuestManager.QuestsListForReading;

                IEnumerable<Quest> filtered = capFilter switch
                {
                    "available" => all.Where(q => q.State == QuestState.NotYetAccepted),
                    "ongoing" => all.Where(q => q.State == QuestState.Ongoing),
                    _ => all
                };

                var list = filtered.OrderBy(q => q.State).ThenBy(q => q.id).ToList();
                if (list.Count == 0)
                    return ToolResult.Success("没有匹配的任务。");

                int total = list.Count;
                var paged = list.Skip((capPage - 1) * capPageSize).Take(capPageSize).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"## 任务列表 ({paged.Count} / {total} 个)");
                sb.AppendLine();
                sb.AppendLine("| ID | 名称 | 状态 | 剩余时间 | 可接受者");
                sb.AppendLine("|----|------|------|----------|--------|");

                foreach (var q in paged)
                {
                    var stateLabel = q.State switch
                    {
                        QuestState.NotYetAccepted => "待接受",
                        QuestState.Ongoing => "进行中",
                        QuestState.EndedSuccess => "✓ 完成",
                        QuestState.EndedFailed => "✗ 失败",
                        QuestState.EndedOfferExpired => "已过期",
                        QuestState.EndedUnknownOutcome => "已结束",
                        QuestState.EndedInvalid => "无效",
                        _ => q.State.ToString()
                    };

                    var timeLeft = q.State == QuestState.NotYetAccepted
                        ? FormatTicks(q.TicksUntilExpiry)
                        : q.State == QuestState.Ongoing
                            ? FormatTicks(q.TicksSinceAccepted) + " (已进行)"
                            : "-";

                    var accepter = q.State <= QuestState.Ongoing && q.AccepterPawn != null
                        ? q.AccepterPawn.LabelShort
                        : q.State == QuestState.NotYetAccepted
                            ? FindAvailableAccepter(q)
                            : "-";

                    sb.AppendLine($"| {q.id} | {q.name} | {stateLabel} | {timeLeft} | {accepter} |");
                }

                sb.AppendLine();
                int totalPages = (int)Math.Ceiling((double)total / capPageSize);
                if (total > capPageSize)
                {
                    sb.AppendLine("---");
                    sb.Append($"第 {capPage}/{totalPages} 页，共 {total} 条");
                    if (capPage < totalPages) sb.Append($" | page={capPage + 1} 下一页");
                    if (capPage > 1) sb.Append($" | page={capPage - 1} 上一页");
                    sb.AppendLine();
                }

                return ToolResult.Success(sb.ToString());
            });
        }

        private static string FindAvailableAccepter(Quest quest)
        {
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            foreach (var c in colonists)
            {
                if (QuestUtility.CanPawnAcceptQuest(c, quest))
                    return c.LabelShort;
            }
            return "无人可接受";
        }

        private static string FormatTicks(int ticks)
        {
            if (ticks <= 0) return "-";
            float hours = ticks / 2500f;
            if (hours < 24) return $"{hours:F1} 小时";
            float days = hours / 24f;
            if (days < 60) return $"{days:F1} 天";
            return $"{days / 15f:F1} 季度";
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
