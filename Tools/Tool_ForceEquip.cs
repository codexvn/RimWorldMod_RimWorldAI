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
        public string Description => "强制殖民者去拾取并装备指定武器或衣物。通过游戏 Job 系统让小人自然走过去拾取，不同于 equip_pawn 的即时装备。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_name = new { type = "string", description = "殖民者名称" },
                thing_defName = new { type = "string", description = "物品 DefName，精确匹配" },
                equip_type = new { type = "string", description = "装备类型", @enum = new[] { "weapon", "apparel" } }
            },
            required = new[] { "colonist_name", "thing_defName" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_name", out var jName))
                return ToolResult.Error("缺少必填参数: colonist_name");

            string colonistName = jName.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(colonistName))
                return ToolResult.Error("colonist_name 不能为空");

            string thingDefName = "";
            if (args.Value.TryGetProperty("thing_defName", out var jDef))
                thingDefName = jDef.GetString() ?? "";

            string equipType = "weapon";
            if (args.Value.TryGetProperty("equip_type", out var jType))
            {
                equipType = jType.GetString() ?? "weapon";
                if (equipType != "weapon" && equipType != "apparel")
                    return ToolResult.Error($"不支持的装备类型: {equipType}");
            }

            if (string.IsNullOrEmpty(thingDefName))
                return ToolResult.Error("需要提供 thing_defName");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c =>
                        c.Name.ToStringShort.IndexOf(colonistName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        c.Name.ToStringFull.IndexOf(colonistName, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (pawn == null)
                        return ToolResult.Error($"找不到殖民者: {colonistName}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    // 查找物品
                    var group = equipType == "weapon" ? ThingRequestGroup.Weapon : ThingRequestGroup.Apparel;
                    var candidates = map.listerThings.ThingsInGroup(group);
                    if (candidates == null || candidates.Count == 0)
                        return ToolResult.Error($"地图上没有任何{(equipType == "weapon" ? "武器" : "衣物")}。");

                    Thing? thing = candidates.FirstOrDefault(t => t.def.defName == thingDefName);
                    if (thing == null)
                    {
                        var available = candidates.Take(10).Select(t => $"{t.Label} ({t.def.defName})").ToArray();
                        return ToolResult.Error($"找不到匹配 '{thingDefName}' 的{(equipType == "weapon" ? "武器" : "衣物")}。" +
                                               $"可用: {string.Join(", ", available)}");
                    }

                    // 获取品质信息
                    string qualityStr = "";
                    try
                    {
                        var compQuality = thing.TryGetComp<CompQuality>();
                        if (compQuality != null)
                            qualityStr = $"（品质: {compQuality.Quality.GetLabel()}）";
                    }
                    catch { }

                    if (equipType == "weapon")
                    {
                        // 验证 —— 对齐 FloatMenuOptionProvider_Equip
                        if (!thing.HasComp<CompEquippable>())
                            return ToolResult.Error($"{thing.Label} 无法作为武器（无 CompEquippable）。");

                        if (pawn.WorkTagIsDisabled(WorkTags.Violent))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止暴力，无法装备武器。");

                        if (thing.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止射击，无法装备远程武器。");

                        if (!pawn.CanReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {thing.Label}。");

                        if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有操作能力，无法装备。");

                        if (thing.IsBurning())
                            return ToolResult.Error($"{thing.Label} 正在燃烧，无法装备。");

                        if (pawn.IsQuestLodger() && !EquipmentUtility.QuestLodgerCanEquip(thing, pawn))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 是任务旅居者，无法装备 {thing.Label}。");

                        if (!EquipmentUtility.CanEquip(thing, pawn, out string canEquipReason, false))
                            return ToolResult.Error($"无法装备 {thing.Label}：{canEquipReason}");

                        if (EquipmentUtility.AlreadyBondedToWeapon(thing, pawn))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 已与另一把灵能武器绑定。");

                        thing.SetForbidden(false, true);
                        Job job = JobMaker.MakeJob(JobDefOf.Equip, thing);
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);

                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已开始前往拾取并装备武器: {thing.Label} ({thing.def.defName}){qualityStr}。");
                    }
                    else
                    {
                        // 验证 —— 对齐 FloatMenuOptionProvider_Wear
                        Apparel apparel = thing as Apparel;
                        if (apparel == null)
                            return ToolResult.Error($"{thing.Label} 无法作为衣物穿戴。");

                        if (pawn.apparel == null)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有衣物管理器。");

                        if (!pawn.CanReach(apparel, PathEndMode.ClosestTouch, Danger.Deadly))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 {apparel.Label}。");

                        if (apparel.IsBurning())
                            return ToolResult.Error($"{apparel.Label} 正在燃烧，无法穿戴。");

                        if (pawn.apparel.WouldReplaceLockedApparel(apparel))
                            return ToolResult.Error($"穿戴 {apparel.Label} 会替换已锁定的衣物。");

                        if (pawn.IsMutant && pawn.mutant.Def.disableApparel)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 是变异体，无法穿戴衣物。");

                        if (!ApparelUtility.HasPartsToWear(pawn, apparel.def))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有适合穿戴 {apparel.Label} 的身体部位。");

                        if (!EquipmentUtility.CanEquip(apparel, pawn, out string canWearReason, true))
                            return ToolResult.Error($"无法穿戴 {apparel.Label}：{canWearReason}");

                        apparel.SetForbidden(false, true);
                        Job job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);

                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已开始前往拾取并穿戴衣物: {thing.Label} ({thing.def.defName}){qualityStr}。");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"强制装备失败: {ex.Message}");
                }
            });
        }
    }
}
