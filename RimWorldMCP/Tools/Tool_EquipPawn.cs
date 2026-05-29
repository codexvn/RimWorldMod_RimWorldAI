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
    public class Tool_EquipPawn : ITool
    {
        public string Name => "equip_pawn";
        public string Description => "强制殖民者去拾取并装备武器或衣物（走过去自然拾取）。通过 thing_id 定位物品，由唯一 ID 精确定位。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                thing_id = new { type = "integer", description = "装备物品 ID（来自 get_tile_detail）" },
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

                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到 ID={colonistId} 的殖民者。");

                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    Thing? thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
                    if (thing == null)
                        return ToolResult.Error($"找不到 ID={thingId} 的物品。");

                    string qualityStr = "";
                    try
                    {
                        var compQuality = thing.TryGetComp<CompQuality>();
                        if (compQuality != null) qualityStr = $"（品质: {compQuality.Quality.GetLabel()}）";
                    }
                    catch { }

                    bool isWeapon = thing.def.IsWeapon || thing.HasComp<CompEquippable>();
                    if (isWeapon)
                    {
                        if (pawn.equipment == null)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有装备管理器。");
                        ThingWithComps? equipmentThing = thing as ThingWithComps;
                        if (equipmentThing == null)
                            return ToolResult.Error($"{thing.Label} 不是可装备物品。");

                        if (pawn.WorkTagIsDisabled(WorkTags.Violent))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止暴力，无法装备武器。");

                        if (thing.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止射击，无法装备远程武器。");

                        if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有操作能力，无法装备。");

                        if (!EquipmentUtility.CanEquip(equipmentThing, pawn, out string r, false))
                            return ToolResult.Error($"无法装备: {r}");

                        if (EquipmentUtility.AlreadyBondedToWeapon(thing, pawn))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 已与另一把灵能武器绑定。");
                    }
                    else
                    {
                        Apparel? apparel = thing as Apparel;
                        if (apparel == null) return ToolResult.Error($"{thing.Label} 不是衣物。");
                        if (pawn.apparel == null) return ToolResult.Error($"{pawn.Name.ToStringShort} 没有衣物管理器。");

                        if (!ApparelUtility.HasPartsToWear(pawn, apparel.def))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有适合穿戴 {apparel.Label} 的身体部位。");

                        if (pawn.IsMutant && pawn.mutant.Def.disableApparel)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 是变异体，无法穿戴衣物。");

                        if (pawn.apparel.WouldReplaceLockedApparel(apparel))
                            return ToolResult.Error($"穿戴 {apparel.Label} 会替换已锁定的衣物。");

                        if (!EquipmentUtility.CanEquip(apparel, pawn, out string r, true))
                            return ToolResult.Error($"无法穿戴: {r}");
                    }

                    if (!pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {thing.Label}。");

                    if (thing.IsBurning())
                        return ToolResult.Error($"{thing.Label} 正在燃烧，无法装备。");

                    thing.SetForbidden(false, true);
                    Job job = JobMaker.MakeJob(isWeapon ? JobDefOf.Equip : JobDefOf.Wear, thing);
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, capQueue))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行操作（物品可能已被占用或当前任务无法中断）。");

                    string typeLabel = isWeapon ? "武器" : "衣物";
                    string queueLabel = capQueue ? "（已加入队列）" : "";
                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往拾取并装备{typeLabel}: {thing.Label}{qualityStr}。{queueLabel}");
                }
                catch (Exception ex) { return ToolResult.Error($"装备操作失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("colonist_id", out var jA) || !jA.TryGetInt32(out var idA)) return null;
            if (!args.Value.TryGetProperty("thing_id", out var jB) || !jB.TryGetInt32(out var idB)) return null;
            var a = CameraHelper.FindPawnById(map, idA);
            var b = CameraHelper.FindThingById(map, idB);
            if (a == null || b == null) return null;
            return (Math.Min(a.Position.x, b.Position.x), Math.Min(a.Position.z, b.Position.z),
                    Math.Max(a.Position.x, b.Position.x), Math.Max(a.Position.z, b.Position.z));
        }
    }
}
