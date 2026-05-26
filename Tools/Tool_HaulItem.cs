using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_HaulItem : ITool
    {
        public string Name => "haul_item";
        public string Description => "搬运物品：指定殖民者拾取物品并搬运到目标位置。不提供目标坐标则自动寻找最佳存储区。相当于游戏中的\"优先搬运\"右键操作。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                thing_id = new { type = "integer", description = "物品唯一 ID（来自 get_tile_detail 或 find_thing）" },
                pos_x = new { type = "integer", description = "目标 X 坐标（可选，与 pos_y 配对。不填则自动放入最佳存储区）" },
                pos_y = new { type = "integer", description = "目标 Y 坐标（可选，与 pos_x 配对。不填则自动放入最佳存储区）" },
                count = new { type = "integer", description = "搬运数量（可选，默认全部）" },
                queue = new { type = "boolean", description = "加入任务队列末尾而非立即执行（默认 true）", @default = true }
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

            // 解析目标坐标（可选）
            int destX = 0, destY = 0;
            bool hasDest = false;
            if (args.Value.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out var dx)
                && args.Value.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out var dy))
            {
                destX = dx; destY = dy; hasDest = true;
            }

            int? userCount = null;
            if (args.Value.TryGetProperty("count", out var jCount))
                userCount = jCount.GetInt32();

            bool queue = true;
            if (args.Value.TryGetProperty("queue", out var jQueue) && jQueue.ValueKind == JsonValueKind.False)
                queue = false;

            // 捕获本地变量供 lambda 使用
            var capDestX = destX; var capDestY = destY; var capHasDest = hasDest; var capQueue = queue;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    // 查找殖民者
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                    // 查找物品（全局搜索）
                    Thing thing = map.listerThings.AllThings
                        .FirstOrDefault(t => t.thingIDNumber == thingId);
                    if (thing == null)
                        return ToolResult.Error($"找不到物品 ID={thingId}");

                    // 验证：在物品栏/已被搬运
                    if (thing.Map == null || !thing.Spawned)
                        return ToolResult.Error($"{thing.Label} 不在可搬运状态（可能已被拾取或销毁）");

                    // 验证：可搬运
                    if (!thing.def.EverHaulable)
                        return ToolResult.Error($"{thing.Label} ({thing.def.defName}) 不可搬运");

                    // 验证：未被禁止
                    if (thing.IsForbidden(Faction.OfPlayer))
                        return ToolResult.Error($"{thing.Label} 已被禁止互动，请先使用 allow_all_items 允许");

                    // 验证：可达
                    if (!pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {thing.Label}");

                    // 验证：搬运能力
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, true))
                    {
                        var carried = pawn.carryTracker?.CarriedThing;
                        if (carried != null && carried != thing)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 已经在搬运 {carried.Label}，无法同时搬运多件物品");
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法搬运 {thing.Label}");
                    }

                    // 确定数量
                    int finalCount = userCount.HasValue ? userCount.Value : thing.stackCount;
                    if (finalCount <= 0) return ToolResult.Error("搬运数量必须大于 0");
                    if (finalCount > thing.stackCount) finalCount = thing.stackCount;

                    // 解除禁止
                    thing.SetForbidden(false, false);

                    Job job;
                    string destInfo;

                    if (capHasDest)
                    {
                        // 搬运到指定位置
                        IntVec3 destCell = new IntVec3(capDestX, 0, capDestY);
                        if (!destCell.InBounds(map))
                            return ToolResult.Error($"目标坐标 ({capDestX}, {capDestY}) 超出地图范围");

                        // 检查目标格是否可以放置物品
                        if (!DropCellFinder.IsGoodDropSpot(destCell, map, false, true))
                            return ToolResult.Error($"目标坐标 ({capDestX}, {capDestY}) 无法放置物品（被占用或不可通行）");

                        job = HaulAIUtility.HaulToCellStorageJob(pawn, thing, destCell, false);
                        if (job == null)
                            return ToolResult.Error($"无法为 {thing.Label} 创建搬运到 ({capDestX}, {capDestY}) 的任务");

                        destInfo = $"搬运到 ({capDestX}, {capDestY})";
                    }
                    else
                    {
                        // 自动寻找最佳存储区
                        job = HaulAIUtility.HaulToStorageJob(pawn, thing, true);
                        if (job == null)
                            return ToolResult.Error($"没有可用存储区放置 {thing.Label}。请先创建存储区或指定目标坐标");

                        destInfo = "搬运到最佳存储区";
                    }

                    // 如有需要覆盖数量
                    if (finalCount != thing.stackCount)
                    {
                        if (job is { def: not null })
                            job.count = finalCount;
                    }

                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, capQueue))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法开始搬运（物品可能已被占用或当前任务无法中断）。");

                    string queueLabel = capQueue ? "（已加入队列）" : "";
                    return ToolResult.Success($"{pawn.Name.ToStringShort} 开始搬运: {thing.Label} ({thing.def.defName}) x{finalCount} → {destInfo}{queueLabel}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"搬运失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
