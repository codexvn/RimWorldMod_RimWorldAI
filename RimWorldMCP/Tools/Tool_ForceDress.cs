using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ForceDress : ITool, IRequiresAdvanceTick
    {
        public string Name => "force_dress";
        public string Description => "批量强制殖民者拿取衣物给另一位殖民者穿上。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                equipments = new
                {
                    type = "array",
                    description = "穿戴指令列表（批量模式，优先）",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            doer_id = new { type = "integer", description = "执行者 ID（去拿衣物的人）" },
                            target_id = new { type = "integer", description = "穿戴者 ID（被穿的人）" },
                            thing_id = new { type = "integer", description = "衣物 ID" }
                        },
                        required = new[] { "doer_id", "target_id", "thing_id" }
                    }
                },
                doer_id = new { type = "integer", description = "执行穿戴操作的殖民者 ID（单个模式）" },
                target_id = new { type = "integer", description = "目标殖民者 ID（单个模式）" },
                thing_id = new { type = "integer", description = "衣物唯一 ID（单个模式）" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            // 批量模式
            if (args.Value.TryGetProperty("equipments", out var jEqs) && jEqs.ValueKind == JsonValueKind.Array)
                return await McpCommandQueue.DispatchAsync(() => ExecuteBatch(jEqs));

            // 单个模式（兼容旧调用）
            if (!args.Value.TryGetProperty("doer_id", out var jDid) || !jDid.TryGetInt32(out var doerId))
                return ToolResult.Error("缺少必填参数: doer_id 或 equipments");
            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return ToolResult.Error("缺少必填参数: target_id 或 equipments");
            if (!args.Value.TryGetProperty("thing_id", out var jIid) || !jIid.TryGetInt32(out var thingId))
                return ToolResult.Error("缺少必填参数: thing_id 或 equipments");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var (ok, msg) = ExecuteOne(doerId, targetId, thingId, null, null);
                return ok ? ToolResult.Success(msg) : ToolResult.Error(msg);
            });
        }

        private ToolResult ExecuteBatch(JsonElement jEqs)
        {
            var successList = new List<string>();
            var failList = new List<string>();
            var pawnCache = new Dictionary<int, Pawn>();

            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            if (colonists == null || colonists.Count == 0) return ToolResult.Error("当前没有自由殖民者。");
            Map map = Find.CurrentMap;
            if (map == null) return ToolResult.Error("没有当前地图。");

            foreach (var je in jEqs.EnumerateArray())
            {
                try
                {
                    if (!je.TryGetProperty("doer_id", out var jD) || !jD.TryGetInt32(out var did))
                    { failList.Add("缺少 doer_id"); continue; }
                    if (!je.TryGetProperty("target_id", out var jT) || !jT.TryGetInt32(out var tid))
                    { failList.Add($"d={did}: 缺少 target_id"); continue; }
                    if (!je.TryGetProperty("thing_id", out var jI) || !jI.TryGetInt32(out var iid))
                    { failList.Add($"d={did}: 缺少 thing_id"); continue; }

                    Pawn getPawn(int id) { if (!pawnCache.TryGetValue(id, out var p)) { p = colonists.FirstOrDefault(c => c.thingIDNumber == id); if (p != null) pawnCache[id] = p; } return p; }

                    var (ok, msg) = ExecuteOne(did, tid, iid, getPawn(did), getPawn(tid));
                    if (ok) successList.Add(msg);
                    else failList.Add(msg);
                }
                catch (Exception ex) { failList.Add($"异常: {FormatExceptionChain(ex)}"); }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"已发送 {successList.Count}/{successList.Count + failList.Count} 个穿戴指令: {string.Join(", ", successList)}");
            if (failList.Count > 0) sb.Append($"。失败: {string.Join("; ", failList)}");
            return ToolResult.Success(sb.ToString());
        }

        private static (bool ok, string msg) ExecuteOne(int doerId, int targetId, int thingId, Pawn? cachedDoer, Pawn? cachedTarget)
        {
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            Pawn pawn = cachedDoer ?? colonists.FirstOrDefault(c => c.thingIDNumber == doerId);
            if (pawn == null) return (false, $"doer={doerId}: 找不到");
            Pawn targetPawn = cachedTarget ?? colonists.FirstOrDefault(c => c.thingIDNumber == targetId);
            if (targetPawn == null) return (false, $"target={targetId}: 找不到");

            if (pawn == targetPawn) return (false, $"{pawn.LabelShort}: 不能给自己穿，用equip_pawn");

            Map map = Find.CurrentMap;
            if (map == null) return (false, $"{pawn.LabelShort}: 无地图");

            var t = FindThingById(map, thingId);
            if (t == null) return (false, $"{pawn.LabelShort}: 找不到物品{thingId}");
            Apparel? apparel = t as Apparel;
            if (apparel == null) return (false, $"{pawn.LabelShort}: {t.Label}不是衣物");

            if (!pawn.CanReach(apparel, PathEndMode.ClosestTouch, Danger.Deadly))
                return (false, $"{pawn.LabelShort}: 无法到达{apparel.Label}");
            if (apparel.IsBurning())
                return (false, $"{pawn.LabelShort}: {apparel.Label}燃烧中");

            if (targetPawn.apparel == null) return (false, $"{targetPawn.LabelShort}: 无衣物管理器");
            if (!EquipmentUtility.CanEquip(apparel, targetPawn, out string reason, true))
                return (false, $"{pawn.LabelShort}→{targetPawn.LabelShort}: {reason}");

            apparel.SetForbidden(false, true);
            Job job = JobMaker.MakeJob(JobDefOf.ForceTargetWear, targetPawn, apparel);
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                return (false, $"{pawn.LabelShort}→{targetPawn.LabelShort}: 被阻塞");

            return (true, $"{pawn.LabelShort}→衣→{targetPawn.LabelShort}({apparel.Label})");
        }

        private static Thing? FindThingById(Map map, int id)
        {
            foreach (var t in map.listerThings.AllThings)
                if (t.thingIDNumber == id) return t;
            return null;
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;

            int? rangeMinX = null, rangeMinZ = null, rangeMaxX = null, rangeMaxZ = null;
            if (args.Value.TryGetProperty("equipments", out var jEqs) && jEqs.ValueKind == JsonValueKind.Array)
            {
                foreach (var je in jEqs.EnumerateArray())
                {
                    if (je.TryGetProperty("doer_id", out var jD) && jD.TryGetInt32(out var doerId))
                        IncludeThing(CameraHelper.FindPawnById(map, doerId), ref rangeMinX, ref rangeMinZ, ref rangeMaxX, ref rangeMaxZ);
                    if (je.TryGetProperty("target_id", out var jT) && jT.TryGetInt32(out var targetId))
                        IncludeThing(CameraHelper.FindPawnById(map, targetId), ref rangeMinX, ref rangeMinZ, ref rangeMaxX, ref rangeMaxZ);
                    if (je.TryGetProperty("thing_id", out var jI) && jI.TryGetInt32(out var thingId))
                        IncludeThing(CameraHelper.FindThingById(map, thingId), ref rangeMinX, ref rangeMinZ, ref rangeMaxX, ref rangeMaxZ);
                }
                return rangeMinX.HasValue && rangeMinZ.HasValue && rangeMaxX.HasValue && rangeMaxZ.HasValue
                    ? (rangeMinX.Value, rangeMinZ.Value, rangeMaxX.Value, rangeMaxZ.Value)
                    : ((int, int, int, int)?)null;
            }

            if (!args.Value.TryGetProperty("doer_id", out var jSingleDoer) || !jSingleDoer.TryGetInt32(out var singleDoerId)) return null;
            if (!args.Value.TryGetProperty("target_id", out var jSingleTarget) || !jSingleTarget.TryGetInt32(out var singleTargetId)) return null;
            if (!args.Value.TryGetProperty("thing_id", out var jSingleThing) || !jSingleThing.TryGetInt32(out var singleThingId)) return null;
            var doer = CameraHelper.FindPawnById(map, singleDoerId);
            var target = CameraHelper.FindPawnById(map, singleTargetId);
            var thing = CameraHelper.FindThingById(map, singleThingId);
            if (doer == null || target == null || thing == null) return null;
            int minX = Math.Min(Math.Min(doer.Position.x, target.Position.x), thing.Position.x);
            int maxX = Math.Max(Math.Max(doer.Position.x, target.Position.x), thing.Position.x);
            int minZ = Math.Min(Math.Min(doer.Position.z, target.Position.z), thing.Position.z);
            int maxZ = Math.Max(Math.Max(doer.Position.z, target.Position.z), thing.Position.z);
            return (minX, minZ, maxX, maxZ);
        }

        private static void IncludeThing(Thing? thing, ref int? minX, ref int? minZ, ref int? maxX, ref int? maxZ)
        {
            if (thing == null) return;
            minX = minX.HasValue ? Math.Min(minX.Value, thing.Position.x) : thing.Position.x;
            minZ = minZ.HasValue ? Math.Min(minZ.Value, thing.Position.z) : thing.Position.z;
            maxX = maxX.HasValue ? Math.Max(maxX.Value, thing.Position.x) : thing.Position.x;
            maxZ = maxZ.HasValue ? Math.Max(maxZ.Value, thing.Position.z) : thing.Position.z;
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }
}
