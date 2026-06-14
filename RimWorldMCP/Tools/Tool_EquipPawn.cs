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
    public class Tool_EquipPawn : ITool, IRequiresAdvanceTick
    {
        public string Name => "equip_pawn";
        public string Description => "批量强制殖民者去拾取并装备武器或衣物。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                equipments = new
                {
                    type = "array",
                    description = "装备指令列表（批量模式，优先）",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            colonist_id = new { type = "integer", description = "殖民者 ID" },
                            thing_id = new { type = "integer", description = "装备物品 ID" }
                        },
                        required = new[] { "colonist_id", "thing_id" }
                    }
                },
                colonist_id = new { type = "integer", description = "殖民者 ID（单个模式，来自 get_colonists）" },
                thing_id = new { type = "integer", description = "装备物品 ID（单个模式，来自 get_tile_detail）" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            // 批量模式
            if (args.Value.TryGetProperty("equipments", out var jEqs) && jEqs.ValueKind == JsonValueKind.Array)
                return await McpCommandQueue.DispatchAsync(() => ExecuteBatch(jEqs));

            // 单个模式（兼容旧调用）
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id 或 equipments");
            if (!args.Value.TryGetProperty("thing_id", out var jTid) || !jTid.TryGetInt32(out var thingId))
                return ToolResult.Error("缺少必填参数: thing_id 或 equipments");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var (ok, msg) = ExecuteOne(colonistId, thingId, null);
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

            foreach (var je in jEqs.EnumerateArray())
            {
                try
                {
                    if (!je.TryGetProperty("colonist_id", out var jC) || !jC.TryGetInt32(out var cid))
                    { failList.Add("缺少 colonist_id"); continue; }
                    if (!je.TryGetProperty("thing_id", out var jT) || !jT.TryGetInt32(out var tid))
                    { failList.Add($"col={cid}: 缺少 thing_id"); continue; }

                    if (!pawnCache.TryGetValue(cid, out var pawn))
                    {
                        pawn = colonists.FirstOrDefault(c => c.thingIDNumber == cid);
                        if (pawn != null) pawnCache[cid] = pawn;
                    }

                    var (ok, msg) = ExecuteOne(cid, tid, pawn);
                    if (ok) successList.Add(msg);
                    else failList.Add(msg);
                }
                catch (Exception ex) { failList.Add($"异常: {FormatExceptionChain(ex)}"); }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"已发送 {successList.Count}/{successList.Count + failList.Count} 个装备指令: {string.Join(", ", successList)}");
            if (failList.Count > 0) sb.Append($"。失败: {string.Join("; ", failList)}");
            return ToolResult.Success(sb.ToString());
        }

        private static (bool ok, string msg) ExecuteOne(int colonistId, int thingId, Pawn? cachedPawn)
        {
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            Pawn pawn = cachedPawn ?? colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
            if (pawn == null) return (false, $"col={colonistId}: 找不到");

            Map map = Find.CurrentMap;
            if (map == null) return (false, $"col={colonistId}: 无地图");

            Thing? thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing == null) return (false, $"{pawn.LabelShort}: 找不到物品ID={thingId}");

            string qualityStr = "";
            try { var cq = thing.TryGetComp<CompQuality>(); if (cq != null) qualityStr = $"({cq.Quality.GetLabel()})"; }
            catch (Exception ex)
            {
                McpLog.Warn($"[equip_pawn] 读取物品品质失败 thingID={thingId}: {FormatExceptionChain(ex)}");
            }

            bool isWeapon = thing.def.IsWeapon || thing.HasComp<CompEquippable>();
            string err;
            if (isWeapon)
            {
                if (pawn.equipment == null) return (false, $"{pawn.LabelShort}: 无装备管理器");
                if (pawn.WorkTagIsDisabled(WorkTags.Violent)) return (false, $"{pawn.LabelShort}: 禁止暴力");
                if (!EquipmentUtility.CanEquip(thing as ThingWithComps, pawn, out err, false)) return (false, $"{pawn.LabelShort}: {err}");
            }
            else
            {
                if (pawn.apparel == null) return (false, $"{pawn.LabelShort}: 无衣物管理器");
                if (!EquipmentUtility.CanEquip(thing as Apparel, pawn, out err, true)) return (false, $"{pawn.LabelShort}: {err}");
            }

            if (!pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                return (false, $"{pawn.LabelShort}: 无法到达{thing.Label}");

            thing.SetForbidden(false, true);
            Job job = JobMaker.MakeJob(isWeapon ? JobDefOf.Equip : JobDefOf.Wear, thing);
            // Front: 可能连装多件，MCP 指令优先排到队首
            if (!JobQueueHelper.TryTake(pawn, job, QueueMode.Front))
                return (false, $"{pawn.LabelShort}: 无法执行");

            return (true, $"{pawn.LabelShort}→{thing.Label}{qualityStr}");
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;

            int? minX = null, minZ = null, maxX = null, maxZ = null;
            if (args.Value.TryGetProperty("equipments", out var jEqs) && jEqs.ValueKind == JsonValueKind.Array)
            {
                foreach (var je in jEqs.EnumerateArray())
                {
                    if (je.TryGetProperty("colonist_id", out var jC) && jC.TryGetInt32(out var colonistId))
                        IncludeThing(CameraHelper.FindPawnById(map, colonistId), ref minX, ref minZ, ref maxX, ref maxZ);
                    if (je.TryGetProperty("thing_id", out var jT) && jT.TryGetInt32(out var thingId))
                        IncludeThing(CameraHelper.FindThingById(map, thingId), ref minX, ref minZ, ref maxX, ref maxZ);
                }
                return minX.HasValue && minZ.HasValue && maxX.HasValue && maxZ.HasValue
                    ? (minX.Value, minZ.Value, maxX.Value, maxZ.Value)
                    : ((int, int, int, int)?)null;
            }

            if (!args.Value.TryGetProperty("colonist_id", out var jA) || !jA.TryGetInt32(out var idA)) return null;
            if (!args.Value.TryGetProperty("thing_id", out var jB) || !jB.TryGetInt32(out var idB)) return null;
            var a = CameraHelper.FindPawnById(map, idA);
            var b = CameraHelper.FindThingById(map, idB);
            if (a == null || b == null) return null;
            return (Math.Min(a.Position.x, b.Position.x), Math.Min(a.Position.z, b.Position.z),
                    Math.Max(a.Position.x, b.Position.x), Math.Max(a.Position.z, b.Position.z));
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
