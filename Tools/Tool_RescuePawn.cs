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
    public class Tool_RescuePawn : ITool
    {
        public string Name => "rescue_pawn";
        public string Description => "救援倒地受伤的殖民者/盟友。需要可用的医疗床。利用游戏 Job 系统（Rescue）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doer_id = new { type = "integer", description = "执行救援的殖民者 ID（来自 get_colonists）" },
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
                        return ToolResult.Error($"找不到执行救援的殖民者 ID={doerId}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    // 在全部地图 Pawn 中查找目标
                    Pawn? target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);

                    if (target == null)
                        return ToolResult.Error($"找不到目标 ID={targetId}");

                    // 验证 —— 对齐 FloatMenuOptionProvider_RescuePawn
                    if (!HealthAIUtility.CanRescueNow(pawn, target, true))
                        return ToolResult.Error("无法救援该目标。");

                    if (target.mindState.WillJoinColonyIfRescued)
                        return ToolResult.Error("该目标救援后会加入，由其他流程处理。");

                    if (target.IsPrisonerOfColony || target.IsSlaveOfColony || target.IsColonyMech)
                        return ToolResult.Error("该目标已是俘虏/奴隶/已方机械体，无需救援。");

                    if (target.Faction != null && target.Faction.HostileTo(Faction.OfPlayer))
                        return ToolResult.Error("敌对目标，无法救援。");

                    if (!HealthAIUtility.ShouldSeekMedicalRest(target) && !target.ageTracker.CurLifeStage.alwaysDowned)
                        return ToolResult.Error("该目标无需医疗救援。");

                    if (target.playerSettings?.medCare == MedicalCareCategory.NoCare)
                        return ToolResult.Error("该目标医疗设置为无，无法救援。");

                    // 查找医疗床，先找非囚犯床再找囚犯床
                    Building_Bed? bed = RestUtility.FindBedFor(target, pawn, false, false, null) as Building_Bed;
                    if (bed == null)
                        bed = RestUtility.FindBedFor(target, pawn, false, true, null) as Building_Bed;

                    if (bed == null)
                        return ToolResult.Error("没有可用的医疗床。");

                    // 执行救援 Job
                    Job job = JobMaker.MakeJob(JobDefOf.Rescue, target, bed);
                    job.count = 1;
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行救援（目标可能已被占用或当前任务无法中断）。");

                    return ToolResult.Success($"小人已前往救援: {target.Name}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"救援失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
