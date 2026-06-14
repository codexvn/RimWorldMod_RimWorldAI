using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_ForceBedRest : ITool, IRequiresAdvanceTick
    {
        public string Name => "force_bed_rest";
        public string Description => "强制殖民者前往病床卧床休养（一次性任务，痊愈后自动起身）。复用游戏 Building_Bed.GetBedRestFloatMenuOption 右键菜单逻辑。";
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
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
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
                        return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    // 对齐 Building_Bed.GetBedRestFloatMenuOption 前置检查
                    if (!pawn.RaceProps.Humanlike)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 非人类，无法使用医疗床。");

                    if (pawn.Drafted)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 已征召，请先解除征召。");

                    if (!HealthAIUtility.ShouldSeekMedicalRest(pawn))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 当前无需医疗休养。");

                    // 查找床：优先医疗床，无则普通床
                    Building_Bed? bed = RestUtility.FindBedFor(pawn, pawn, false, false, null) as Building_Bed;
                    if (bed == null)
                        bed = RestUtility.FindBedFor(pawn, pawn, false, true, null) as Building_Bed;

                    if (bed == null)
                        return ToolResult.Error("没有可用的床。");

                    if (!pawn.CanReserveAndReach(bed, PathEndMode.ClosestTouch, Danger.Deadly, bed.SleepingSlotsCount, -1, null, true))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达或床位已被占用: {bed.Label}");

                    // 对齐游戏逻辑：已在此床上 → 直接设为 restUntilHealed
                    if (pawn.CurJobDef == JobDefOf.LayDown && pawn.CurJob.GetTarget(TargetIndex.A).Thing == bed)
                    {
                        pawn.CurJob.restUntilHealed = true;
                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已设置为卧床休养至痊愈（已在床上，医疗床={bed.Medical}）。");
                    }

                    // restUntilHealed 不依赖医疗床，普通床也生效
                    // ThinkNode_ConditionalMustKeepLyingDown: pawn.CurJob.restUntilHealed && ShouldSeekMedicalRest → 保持躺下
                    // 痊愈后 ShouldSeekMedicalRest 返回 false → 自动起身
                    Job job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
                    job.restUntilHealed = true;
                    // Front: 卧床休养，MCP 优先
                    if (!JobQueueHelper.TryTake(pawn, job, QueueMode.Front))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法前往卧床（当前任务无法中断）。");

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往卧床休养至痊愈（医疗床={bed.Medical}），痊愈后自动起身。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"强制卧床失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var id)) return null;
            var pawn = CameraHelper.FindPawnById(map, id);
            return pawn == null ? null : (pawn.Position.x, pawn.Position.z, pawn.Position.x, pawn.Position.z);
        }
    }
}
