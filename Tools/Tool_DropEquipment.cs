using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DropEquipment : ITool
    {
        public string Name => "drop_equipment";
        public string Description => "强制殖民者丢弃当前装备的主武器。通过 ID 精确定位殖民者。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" }
            },
            required = new[] { "colonist_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jId) || !jId.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到 ID={colonistId} 的殖民者。");

                    if (pawn.equipment == null || pawn.equipment.Primary == null)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 没有装备武器。");

                    ThingWithComps weapon = pawn.equipment.Primary;
                    Job job = JobMaker.MakeJob(JobDefOf.DropEquipment, weapon);
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法丢弃武器（当前任务无法中断）。");

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 将丢弃武器: {weapon.Label}");
                }
                catch (Exception ex) { return ToolResult.Error($"丢弃武器失败: {ex.Message}"); }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
