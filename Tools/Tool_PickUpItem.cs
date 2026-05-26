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
    public class Tool_PickUpItem : ITool
    {
        public string Name => "pick_up_item";
        public string Description => "强制殖民者拾取指定物品到背包。通过游戏 Job 系统（TakeInventory），小人将自动走过去拾取。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                thing_id = new { type = "integer", description = "物品唯一 ID（来自 get_tile_detail）" },
                count = new { type = "integer", description = "拾取数量（可选，默认全部）" }
            },
            required = new[] { "colonist_id", "thing_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");

            if (!args.Value.TryGetProperty("thing_id", out var jTid) || !jTid.TryGetInt32(out var thingId))
                return ToolResult.Error("缺少必填参数: thing_id");

            int? userCount = null;
            if (args.Value.TryGetProperty("count", out var jCount))
                userCount = jCount.GetInt32();

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

                    // 查找可拾取物品
                    var candidates = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
                    if (candidates == null || candidates.Count == 0)
                        return ToolResult.Error("地图上没有可拾取的物品。");

                    Thing thing = candidates.FirstOrDefault(t => t.thingIDNumber == thingId);
                    if (thing == null)
                        return ToolResult.Error($"找不到匹配 ID={thingId} 的物品。");

                    // 验证：物品类别
                    if (thing.def.category != ThingCategory.Item)
                        return ToolResult.Error($"{thing.Label} 不是物品类别，无法拾取。");

                    // 验证：可搬运且可拾取
                    if (!thing.def.EverHaulable || !PawnUtility.CanPickUp(pawn, thing.def))
                        return ToolResult.Error($"{thing.Label} 无法被拾取（不可搬运或背包容量不足）。");

                    // 验证：可达性
                    if (!pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {thing.Label}。");

                    // 确定实际拾取数量
                    int finalCount = userCount.HasValue ? userCount.Value : thing.stackCount;
                    if (finalCount <= 0)
                        return ToolResult.Error("拾取数量必须大于 0。");

                    // 限制最大值：不超过物品堆叠数
                    if (finalCount > thing.stackCount)
                        finalCount = thing.stackCount;

                    // 检查 orderedTakeGroup 上限
                    int maxAllowed = PawnUtility.GetMaxAllowedToPickUp(pawn, thing.def);
                    if (maxAllowed == 0)
                        return ToolResult.Error($"无法拾取 {thing.Label}：已达到最大允许拾取数量限制。");
                    if (finalCount > maxAllowed)
                        finalCount = maxAllowed;

                    // 超重检查（仅拾取全部时）
                    if (finalCount == thing.stackCount && MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, finalCount))
                        return ToolResult.Error($"拾取 {thing.Label} x{finalCount} 会导致超重。");

                    // 执行拾取
                    thing.SetForbidden(false, false);
                    Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, thing);
                    job.count = finalCount;
                    job.checkEncumbrance = true;
                    job.takeInventoryDelay = 120;
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法拾取物品（物品可能已被占用或当前任务无法中断）。");

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往拾取: {thing.Label} ({thing.def.defName}) x{finalCount}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"拾取物品失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
