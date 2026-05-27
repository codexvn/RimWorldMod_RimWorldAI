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
    public class Tool_StripPawn : ITool
    {
        public string Name => "strip_pawn";
        public string Description => "强制殖民者剥除目标（尸体或活体）的所有衣物和装备。利用游戏 Job 系统（Strip）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doer_id = new { type = "integer", description = "执行剥除的殖民者 ID（来自 get_colonists）" },
                target_id = new { type = "integer", description = "目标 ID（活体或尸体，来自 get_tile_detail）" },
                queue = new { type = "boolean", description = "加入任务队列末尾而非立即执行（默认 true）", @default = true }
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

            bool queue = true;
            if (args.Value.TryGetProperty("queue", out var jQueue) && jQueue.ValueKind == JsonValueKind.False)
                queue = false;
            var capQueue = queue;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == doerId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到殖民者 ID={doerId}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    // 查找目标：先在所有活体 Pawn 中搜索，再查尸体
                    Thing target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);

                    if (target == null)
                    {
                        // 搜索尸体
                        var corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
                        if (corpses != null && corpses.Count > 0)
                        {
                            target = corpses.FirstOrDefault(c => c.thingIDNumber == targetId);
                        }
                    }

                    if (target == null)
                        return ToolResult.Error($"找不到目标 ID={targetId}");

                    // 验证：是否可以剥除
                    if (!StrippableUtility.CanBeStrippedByColony(target))
                        return ToolResult.Error($"{target.Label} 无法被剥除。");

                    // 验证：可达性
                    if (!pawn.CanReach(target, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {target.Label}。");

                    // 验证：任务相关（仅活体）
                    Pawn? targetPawn = target as Pawn;
                    if (targetPawn != null && targetPawn.HasExtraHomeFaction((Quest)null!))
                        return ToolResult.Error($"{target.Label} 与任务相关，无法剥除。");

                    // 执行剥除
                    target.SetForbidden(false, false);
                    StrippableUtility.CheckSendStrippingImpactsGoodwillMessage(target);
                    Job job = JobMaker.MakeJob(JobDefOf.Strip, target);
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, capQueue))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行剥除（目标可能已被占用或当前任务无法中断）。");

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往剥除: {target.Label}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"剥除失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("doer_id", out var jD) || !jD.TryGetInt32(out var doerId)) return null;
            if (!args.Value.TryGetProperty("target_id", out var jT) || !jT.TryGetInt32(out var targetId)) return null;
            var doer = CameraHelper.FindPawnById(map, doerId);
            if (doer == null) return null;
            var target = (Thing?)CameraHelper.FindPawnById(map, targetId) ?? CameraHelper.FindThingById(map, targetId);
            if (target == null) return null;
            return (Math.Min(doer.Position.x, target.Position.x), Math.Min(doer.Position.z, target.Position.z),
                    Math.Max(doer.Position.x, target.Position.x), Math.Max(doer.Position.z, target.Position.z));
        }
    }
}
