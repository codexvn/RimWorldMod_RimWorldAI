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
    public class Tool_ForceEquip : ITool
    {
        public string Name => "force_equip";
        public string Description => "强制殖民者去拾取并装备武器或衣物（走过去自然拾取）。通过物品唯一 ID 定位；先用 get_tile_detail 查看物品 ID。";
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

            if (!args.Value.TryGetProperty("thing_id", out var jId) || !jId.TryGetInt32(out var thingId))
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
                        return ToolResult.Error($"找不到 ID={colonistId} 的殖民者。");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    Thing? thing = FindThingById(map, thingId);
                    if (thing == null)
                        return ToolResult.Error($"找不到 ID={thingId} 的物品。");

                    string qualityStr = "";
                    try
                    {
                        var compQuality = thing.TryGetComp<CompQuality>();
                        if (compQuality != null)
                            qualityStr = $"（品质: {compQuality.Quality.GetLabel()}）";
                    }
                    catch { }

                    bool isWeapon = thing.def.IsWeapon || thing.HasComp<CompEquippable>();
                    if (isWeapon)
                    {
                        if (pawn.WorkTagIsDisabled(WorkTags.Violent))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止暴力，无法装备武器。");

                        if (thing.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止射击，无法装备远程武器。");

                        if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有操作能力，无法装备。");

                        if (!EquipmentUtility.CanEquip(thing, pawn, out string reason, false))
                            return ToolResult.Error($"无法装备 {thing.Label}: {reason}");

                        if (EquipmentUtility.AlreadyBondedToWeapon(thing, pawn))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 已与另一把灵能武器绑定。");
                    }
                    else
                    {
                        Apparel? apparel = thing as Apparel;
                        if (apparel == null)
                            return ToolResult.Error($"{thing.Label} 不是可穿戴的衣物。");

                        if (pawn.apparel == null)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有衣物管理器。");

                        if (!ApparelUtility.HasPartsToWear(pawn, apparel.def))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有适合穿戴 {apparel.Label} 的身体部位。");

                        if (pawn.IsMutant && pawn.mutant.Def.disableApparel)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 是变异体，无法穿戴衣物。");

                        if (pawn.apparel.WouldReplaceLockedApparel(apparel))
                            return ToolResult.Error($"穿戴 {apparel.Label} 会替换已锁定的衣物。");

                        if (!EquipmentUtility.CanEquip(apparel, pawn, out string reason, true))
                            return ToolResult.Error($"无法穿戴 {apparel.Label}: {reason}");
                    }

                    if (!pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {thing.Label}。");

                    if (thing.IsBurning())
                        return ToolResult.Error($"{thing.Label} 正在燃烧，无法装备。");

                    thing.SetForbidden(false, true);
                    Job job = JobMaker.MakeJob(isWeapon ? JobDefOf.Equip : JobDefOf.Wear, thing);
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);

                    string typeLabel = isWeapon ? "武器" : "衣物";
                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往拾取并装备{typeLabel}: {thing.Label}{qualityStr}。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"强制装备失败: {ex.Message}");
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
    }
}
