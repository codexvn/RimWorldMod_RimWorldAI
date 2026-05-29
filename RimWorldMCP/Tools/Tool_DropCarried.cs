using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_DropCarried : ITool
    {
        public string Name => "drop_carried";
        public string Description => "让殖民者放下手中正在搬运的物品。可指定放下位置（小人会走过去放下）或就地放下。可指定数量只放下部分堆叠。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                count = new { type = "integer", description = "放下数量（可选，默认全部放下）" },
                pos_x = new { type = "integer", description = "放下位置 X（可选，与 pos_y 配对。不填则就地放下）" },
                pos_y = new { type = "integer", description = "放下位置 Y（可选，与 pos_x 配对。不填则就地放下）" },
                queue = new { type = "boolean", description = "加入任务队列末尾而非立即执行（默认 true）", @default = true }
            },
            required = new[] { "colonist_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");

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

            // 捕获本地变量供 lambda 使用
            var capDestX = destX; var capDestY = destY; var capHasDest = hasDest;

            bool queue = true;
            if (args.Value.TryGetProperty("queue", out var jQueue) && jQueue.ValueKind == JsonValueKind.False)
                queue = false;
            var capQueue = queue;

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

                    // 检查是否正在搬运
                    var carrier = pawn.carryTracker;
                    if (carrier == null)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法搬运物品");

                    Thing carried = carrier.CarriedThing;
                    if (carried == null)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 手中没有正在搬运的物品");

                    // 确定数量
                    int finalCount = userCount.HasValue ? userCount.Value : carried.stackCount;
                    if (finalCount <= 0) return ToolResult.Error("放下数量必须大于 0");
                    if (finalCount > carried.stackCount) finalCount = carried.stackCount;

                    string thingLabel = carried.Label;
                    string thingDefName = carried.def.defName;

                    if (capHasDest)
                    {
                        // 走到指定位置再放下
                        IntVec3 destCell = new IntVec3(capDestX, 0, capDestY);
                        if (!destCell.InBounds(map))
                            return ToolResult.Error($"目标坐标 ({capDestX}, {capDestY}) 超出地图范围");

                        if (!pawn.CanReach(destCell, PathEndMode.OnCell, Danger.Deadly))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 ({capDestX}, {capDestY})");

                        // 创建搬运任务：把手中物品搬到目标格
                        Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, carried, destCell);
                        job.haulMode = HaulMode.ToCellStorage;
                        job.count = finalCount;
                        if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, capQueue))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行搬运放下任务（当前任务无法中断）。");

                        return ToolResult.Success($"{pawn.Name.ToStringShort} 将把 {thingLabel} x{finalCount} 搬到 ({capDestX}, {capDestY}) 放下");
                    }
                    else
                    {
                        // 就地放下
                        IntVec3 dropLoc = pawn.Position;
                        Thing dropped;
                        bool ok = carrier.TryDropCarriedThing(dropLoc, finalCount, ThingPlaceMode.Near, out dropped, null);

                        if (!ok || dropped == null)
                            return ToolResult.Error($"放下 {thingLabel} 失败（目标位置可能被占用）");

                        string countInfo = finalCount < carried.stackCount ? $" x{finalCount}" : "";
                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已放下: {thingLabel} ({thingDefName}){countInfo} → ({dropLoc.x}, {dropLoc.z})");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"放下物品失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("colonist_id", out var jC) || !jC.TryGetInt32(out var id)) return null;
            var pawn = CameraHelper.FindPawnById(map, id);
            if (pawn == null) return null;
            int tx = pawn.Position.x, tz = pawn.Position.z;
            if (args.Value.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out var px)) tx = px;
            if (args.Value.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out var py)) tz = py;
            return (Math.Min(pawn.Position.x, tx), Math.Min(pawn.Position.z, tz),
                    Math.Max(pawn.Position.x, tx), Math.Max(pawn.Position.z, tz));
        }
    }
}
