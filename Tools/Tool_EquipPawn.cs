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
        public string Description => "给指定殖民者即时装备武器或衣物（直接装备，不需走路）。通过 thing_id 定位物品，由唯一 ID 精确定位。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                thing_id = new { type = "integer", description = "装备物品 ID（来自 get_tile_detail）" }
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
                        return ToolResult.Error($"找不到 ID={colonistId} 的殖民者。");

                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    Thing? thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
                    if (thing == null)
                        return ToolResult.Error($"找不到 ID={thingId} 的物品。");

                    if (!pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {thing.Label}。");

                    if (thing.IsBurning())
                        return ToolResult.Error($"{thing.Label} 正在燃烧，无法装备。");

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
                        if (!EquipmentUtility.CanEquip(equipmentThing, pawn, out string r, false))
                            return ToolResult.Error($"无法装备: {r}");

                        thing.SetForbidden(false, true);
                        equipmentThing.holdingOwner?.Remove(equipmentThing);
                        if (equipmentThing.Spawned)
                            equipmentThing.DeSpawn(DestroyMode.Vanish);
                        pawn.equipment.MakeRoomFor(equipmentThing);
                        pawn.equipment.AddEquipment(equipmentThing);
                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已装备武器: {thing.Label}{qualityStr}。");
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

                        thing.SetForbidden(false, true);
                        apparel.holdingOwner?.Remove(apparel);
                        pawn.apparel.Wear(apparel);
                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已穿戴: {thing.Label}{qualityStr}。");
                    }
                }
                catch (Exception ex) { return ToolResult.Error($"装备操作失败: {ex.Message}"); }
            });
        }
    }
}
