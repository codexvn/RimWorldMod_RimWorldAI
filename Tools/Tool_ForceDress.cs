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
    public class Tool_ForceDress : ITool
    {
        public string Name => "force_dress";
        public string Description => "强制殖民者去拿取衣物给另一位殖民者穿上。通过衣物唯一 ID 定位；先用 get_tile_detail 查看衣物 ID。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doer_id = new { type = "integer", description = "执行穿戴操作的殖民者 ID（去拿衣物的人，来自 get_colonists）" },
                target_id = new { type = "integer", description = "目标殖民者 ID（被穿者，来自 get_colonists）" },
                thing_id = new { type = "integer", description = "衣物唯一 ID（来自 get_tile_detail）" },
                queue = new { type = "boolean", description = "加入任务队列末尾而非立即执行（默认 true）", @default = true }
            },
            required = new[] { "doer_id", "target_id", "thing_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("doer_id", out var jDid) || !jDid.TryGetInt32(out var doerId))
                return ToolResult.Error("缺少必填参数: doer_id");
            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return ToolResult.Error("缺少必填参数: target_id");

            if (!args.Value.TryGetProperty("thing_id", out var jId) || !jId.TryGetInt32(out var thingId))
                return ToolResult.Error("缺少必填参数: thing_id");

            string thingDefName = "";
            if (args.Value.TryGetProperty("thing_defName", out var jDef))
                thingDefName = jDef.GetString() ?? "";

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
                        return ToolResult.Error($"找不到执行穿戴的殖民者 ID={doerId}");

                    Pawn targetPawn = colonists.FirstOrDefault(c => c.thingIDNumber == targetId);
                    if (targetPawn == null)
                        return ToolResult.Error($"找不到目标殖民者 ID={targetId}");

                    if (pawn == targetPawn)
                        return ToolResult.Error("执行者和目标不能是同一人，请使用 equip_pawn 自行装备。");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    // 查找衣物
                    var t = FindThingById(map, thingId);
                    if (t == null)
                        return ToolResult.Error($"找不到 ID={thingId} 的物品。");
                    Apparel? apparel = t as Apparel;
                    if (apparel == null)
                        return ToolResult.Error($"ID={thingId} ({t.Label}) 不是衣物。");

                    if (!pawn.CanReach(apparel, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {apparel.Label}。");

                    if (apparel.IsBurning())
                        return ToolResult.Error($"{apparel.Label} 正在燃烧，无法穿戴。");

                    if (targetPawn.apparel == null)
                        return ToolResult.Error($"{targetPawn.Name.ToStringShort} 没有衣物管理器，无法穿戴。");

                    if (targetPawn.apparel.WouldReplaceLockedApparel(apparel))
                        return ToolResult.Error($"穿戴 {apparel.Label} 会替换 {targetPawn.Name.ToStringShort} 已锁定的衣物。");

                    if (targetPawn.IsMutant && targetPawn.mutant.Def.disableApparel)
                        return ToolResult.Error($"{targetPawn.Name.ToStringShort} 是变异体，无法穿戴衣物。");

                    if (!ApparelUtility.HasPartsToWear(targetPawn, apparel.def))
                        return ToolResult.Error($"{targetPawn.Name.ToStringShort} 没有适合穿戴 {apparel.Label} 的身体部位。");

                    if (!EquipmentUtility.CanEquip(apparel, targetPawn, out string reason, true))
                        return ToolResult.Error($"无法给 {targetPawn.Name.ToStringShort} 穿戴 {apparel.Label}：{reason}");

                    apparel.SetForbidden(false, true);
                    Job job = JobMaker.MakeJob(JobDefOf.ForceTargetWear, targetPawn, apparel);
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, capQueue))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行强制穿戴（目标或物品可能已被占用）。");

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往 ({apparel.Position.x},{apparel.Position.z}) 拿取衣物并给 {targetPawn.Name} 穿戴: {apparel.Label}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"强制穿戴失败: {ex.Message}");
                }
            });
        }

        private static Thing? FindThingById(Map map, int id)
        {
            foreach (var t in map.listerThings.AllThings)
                if (t.thingIDNumber == id)
                    return t;
            return null;
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
