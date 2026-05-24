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
    public class Tool_AttackPawn : ITool
    {
        public string Name => "attack_pawn";
        public string Description => "命令殖民者攻击指定目标（自动征召，使用 AttackMelee 追击）。目标用 thingIDNumber 指定（来自 find_enemies 或 get_tile_detail）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 thingIDNumber（来自 get_colonists）" },
                target_id = new { type = "integer", description = "目标 thingIDNumber（来自 find_enemies）" }
            },
            required = new[] { "colonist_id", "target_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");
            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return ToolResult.Error("缺少必填参数: target_id");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    Pawn target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);
                    if (target == null)
                        return ToolResult.Error($"找不到目标 ID={targetId}");

                    if (target.Dead || target.Destroyed)
                        return ToolResult.Error($"目标 {target.LabelShort} 已死亡或被销毁。");

                    // 自动征召
                    if (pawn.drafter != null && !pawn.Drafted)
                    {
                        pawn.drafter.Drafted = true;
                    }

                    if (!pawn.Drafted)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法被征召。");

                    if (!pawn.CanReach(target, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达目标 {target.LabelShort}。");

                    Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行攻击指令，目标可能已被预约。");

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往攻击 {target.LabelShort} (ID={targetId})。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"攻击命令失败: {ex.Message}");
                }
            });
        }
    }
}
