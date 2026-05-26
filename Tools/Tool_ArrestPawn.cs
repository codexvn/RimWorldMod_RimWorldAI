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
    public class Tool_ArrestPawn : ITool
    {
        public string Name => "arrest_pawn";
        public string Description => "逮捕目标殖民者/访客。需要可用的囚犯床。利用游戏 Job 系统（Arrest）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doer_id = new { type = "integer", description = "执行逮捕的殖民者 ID（来自 get_colonists）" },
                target_id = new { type = "integer", description = "目标 ID（来自 get_tile_detail）" }
            },
            required = new[] { "doer_id", "target_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("doer_id", out var jDid) || !jDid.TryGetInt32(out var doerId))
                return ToolResult.Error("缺少必填参数: doer_id");

            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return ToolResult.Error("缺少必填参数: target_id");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == doerId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到执行逮捕的殖民者 ID={doerId}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    // 查找目标：按 thingIDNumber 在所有活体 Pawn 中搜索
                    Pawn clickedPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);

                    if (clickedPawn == null)
                        return ToolResult.Error($"找不到目标 ID={targetId}");

                    // 验证：是否可被捕
                    if (!clickedPawn.CanBeArrestedBy(pawn))
                        return ToolResult.Error($"{clickedPawn.Name} 无法被逮捕。");

                    // 验证：倒地有罪目标请使用俘虏
                    if (clickedPawn.Downed && clickedPawn.guilt.IsGuilty)
                        return ToolResult.Error($"{clickedPawn.Name} 倒地且有罪，请使用俘虏操作（capture），而非逮捕。");

                    // 验证：需要征召状态（野人除外）
                    if (!pawn.Drafted && !clickedPawn.IsWildMan())
                        return ToolResult.Error("需要征召状态才能逮捕。");

                    // 验证：同派系
                    if (pawn.InSameExtraFaction(clickedPawn, ExtraFactionType.HomeFaction, null) ||
                        pawn.InSameExtraFaction(clickedPawn, ExtraFactionType.MiniFaction, null))
                        return ToolResult.Error($"无法逮捕同派系目标: {clickedPawn.Name}");

                    // 验证：可达性
                    if (!pawn.CanReach(clickedPawn, PathEndMode.OnCell, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {clickedPawn.Name}。");

                    // 验证：可用的囚犯床
                    Building_Bed bed = RestUtility.FindBedFor(clickedPawn, pawn, false, false, GuestStatus.Prisoner);
                    if (bed == null)
                        bed = RestUtility.FindBedFor(clickedPawn, pawn, false, true, GuestStatus.Prisoner);
                    if (bed == null)
                        return ToolResult.Error("没有可用的囚犯床。");

                    // 执行逮捕
                    Job job = JobMaker.MakeJob(JobDefOf.Arrest, clickedPawn, bed);
                    job.count = 1;
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行逮捕（目标可能已被占用或当前任务无法中断）。");

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往逮捕: {clickedPawn.Name}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"逮捕失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
