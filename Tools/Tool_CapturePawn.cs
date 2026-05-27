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
    public class Tool_CapturePawn : ITool
    {
        public string Name => "capture_pawn";
        public string Description => "俘虏倒地敌人。需要可用的囚犯床。利用游戏 Job 系统（Capture）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doer_id = new { type = "integer", description = "执行俘虏的殖民者 ID（来自 get_colonists）" },
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
                        return ToolResult.Error($"找不到执行俘虏的殖民者 ID={doerId}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    // 在全部地图 Pawn 中查找目标
                    Pawn? target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);

                    if (target == null)
                        return ToolResult.Error($"找不到目标 ID={targetId}");

                    // 验证 —— 对齐 FloatMenuOptionProvider_CapturePawn
                    if (!target.CanBeCaptured())
                        return ToolResult.Error("该目标无法被俘虏。");

                    if (!HealthAIUtility.CanRescueNow(pawn, target, true))
                        return ToolResult.Error("无法到达该目标进行俘虏。");

                    // 查找囚犯床
                    Building_Bed? bed = RestUtility.FindBedFor(target, pawn, false, false, GuestStatus.Prisoner) as Building_Bed;
                    if (bed == null)
                        bed = RestUtility.FindBedFor(target, pawn, false, true, GuestStatus.Prisoner) as Building_Bed;

                    if (bed == null)
                        return ToolResult.Error("没有可用的囚犯床。");

                    // 执行俘虏 Job
                    Job job = JobMaker.MakeJob(JobDefOf.Capture, target, bed);
                    job.count = 1;
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行俘虏（目标可能已被占用或当前任务无法中断）。");

                    // 演示俘虏概念（首次俘获时触发教程，无副作用）
                    PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.Capturing, KnowledgeAmount.Total);

                    return ToolResult.Success($"小人已前往俘虏: {target.Name}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"俘虏失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("doer_id", out var jA) || !jA.TryGetInt32(out var idA)) return null;
            if (!args.Value.TryGetProperty("target_id", out var jB) || !jB.TryGetInt32(out var idB)) return null;
            var a = CameraHelper.FindPawnById(map, idA);
            var b = CameraHelper.FindPawnById(map, idB);
            if (a == null || b == null) return null;
            return (Math.Min(a.Position.x, b.Position.x), Math.Min(a.Position.z, b.Position.z),
                    Math.Max(a.Position.x, b.Position.x), Math.Max(a.Position.z, b.Position.z));
        }
    }
}
