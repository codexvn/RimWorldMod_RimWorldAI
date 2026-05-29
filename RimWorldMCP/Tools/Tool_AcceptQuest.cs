using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_AcceptQuest : ITool
    {
        public string Name => "accept_quest";
        public string Description => "接受指定任务。需要传入任务 ID（来自 list_quests）。可选指定接受者殖民者 ID，不传则自动选第一个可接受的殖民者。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                quest_id = new { type = "integer", description = "任务 ID（来自 list_quests）" },
                colonist_id = new { type = "integer", description = "接受者殖民者 ID（可选，不传自动选择）" }
            },
            required = new[] { "quest_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("quest_id", out var jQid) || !jQid.TryGetInt32(out var questId))
                return ToolResult.Error("缺少必填参数: quest_id");

            int? colonistId = null;
            if (args.Value.TryGetProperty("colonist_id", out var jCid) && jCid.TryGetInt32(out var cid))
                colonistId = cid;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var quest = Find.QuestManager.QuestsListForReading
                    .FirstOrDefault(q => q.id == questId);
                if (quest == null)
                    return ToolResult.Error($"找不到任务 ID={questId}");

                if (quest.State != QuestState.NotYetAccepted)
                    return ToolResult.Error($"任务 [{quest.name}] 状态为 {quest.State}，无法接受。只有待接受（NotYetAccepted）的任务可以接受。");

                // 确定接受者
                Pawn accepter;
                if (colonistId.HasValue)
                {
                    accepter = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .FirstOrDefault(c => c.thingIDNumber == colonistId.Value);
                    if (accepter == null)
                        return ToolResult.Error($"找不到殖民者 ID={colonistId}");
                }
                else
                {
                    accepter = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .FirstOrDefault(c => QuestUtility.CanPawnAcceptQuest(c, quest));
                    if (accepter == null)
                        return ToolResult.Error("没有殖民者可以接受此任务。请检查任务要求。");
                }

                // 验证可接受
                if (!QuestUtility.CanPawnAcceptQuest(accepter, quest))
                {
                    var report = QuestUtility.CanAcceptQuest(quest);
                    var reason = report.Reason ?? "不符合接受条件";
                    return ToolResult.Error($"{accepter.LabelShort} 无法接受 [{quest.name}]: {reason}");
                }

                quest.Accept(accepter);
                return ToolResult.Success($"{accepter.LabelShort} 已接受任务: {quest.name}");
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
