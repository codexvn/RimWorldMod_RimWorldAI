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
    public class Tool_IngestItem : ITool
    {
        public string Name => "ingest_item";
        public string Description => "强制殖民者食用/饮用/服用指定物品（食物、药物、饮料）。利用游戏 Job 系统（Ingest），小人将自动走过去使用。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                thing_id = new { type = "integer", description = "物品唯一 ID（来自 get_tile_detail）" }
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

                    // 在地图可食用物品中查找目标
                    var candidates = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSource);
                    if (candidates == null || candidates.Count == 0)
                        return ToolResult.Error("地图上没有任何可食用物品。");

                    Thing? thing = candidates.FirstOrDefault(t => t.thingIDNumber == thingId);
                    if (thing == null)
                        return ToolResult.Error($"找不到匹配 ID={thingId} 的可食用物品。");

                    // 验证 —— 对齐 FloatMenuOptionProvider_Ingest
                    if (thing.def.ingestible == null || !thing.def.ingestible.showIngestFloatOption)
                        return ToolResult.Error($"{thing.Label} 不可食用。");

                    if (!thing.IngestibleNow || !pawn.RaceProps.CanEverEat(thing.def))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法食用 {thing.Label}。");

                    if (!thing.def.IsDrug && !pawn.FoodIsSuitable(thing.def))
                        return ToolResult.Error($"{thing.Label} 对该殖民者不适宜。");

                    if (thing.def.IsDrug && !pawn.DrugIsSuitable(thing.def))
                        return ToolResult.Error($"{thing.Label} 对该殖民者不适宜（药物耐受/成瘾问题）。");

                    if (thing.def.IsNonMedicalDrug && !pawn.CanTakeDrug(thing.def))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 受戒酒/禁药特性影响，无法使用 {thing.Label}。");

                    if (!pawn.CanReach(thing, PathEndMode.OnCell, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {thing.Label}。");

                    // 计算服食数量
                    int willIngestCount;
                    if (thing.def.IsDrug)
                    {
                        willIngestCount = 1;
                    }
                    else
                    {
                        float nutrition = FoodUtility.NutritionForEater(pawn, thing);
                        willIngestCount = FoodUtility.WillIngestStackCountOf(pawn, thing.def, nutrition);
                    }

                    int maxAmount = FoodUtility.GetMaxAmountToPickup(thing, pawn, willIngestCount);
                    if (maxAmount <= 0)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法拾取 {thing.Label}（携带能力不足或物品不可用）。");

                    // 执行服食 Job
                    thing.SetForbidden(false, true);
                    Job job = JobMaker.MakeJob(JobDefOf.Ingest, thing);
                    job.count = maxAmount;
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行服食（物品可能已被占用或当前任务无法中断）。");

                    return ToolResult.Success($"小人已前往服食: {thing.Label} x{maxAmount}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"服食失败: {ex.Message}");
                }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
