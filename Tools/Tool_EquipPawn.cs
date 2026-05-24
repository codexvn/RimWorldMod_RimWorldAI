using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_EquipPawn : ITool
    {
        public string Name => "equip_pawn";
        public string Description => "给指定殖民者装备武器或衣物。从地图库存中找到匹配的物品并强制装备。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_name = new { type = "string", description = "殖民者名称" },
                thing_label = new { type = "string", description = "装备标签/名称，模糊匹配" },
                thing_defName = new { type = "string", description = "装备 DefName，精确匹配" },
                equip_type = new { type = "string", description = "装备类型", @enum = new[] { "weapon", "apparel" } }
            },
            required = new[] { "colonist_name" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_name", out var jName))
                return ToolResult.Error("缺少必填参数: colonist_name");

            string colonistName = jName.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(colonistName))
                return ToolResult.Error("colonist_name 不能为空");

            string thingLabel = "";
            if (args.Value.TryGetProperty("thing_label", out var jLabel))
                thingLabel = jLabel.GetString() ?? "";

            string thingDefName = "";
            if (args.Value.TryGetProperty("thing_defName", out var jDef))
                thingDefName = jDef.GetString() ?? "";

            string equipType = "weapon";
            if (args.Value.TryGetProperty("equip_type", out var jType))
            {
                equipType = jType.GetString() ?? "weapon";
                if (equipType != "weapon" && equipType != "apparel")
                    return ToolResult.Error($"不支持的装备类型: {equipType}，请使用 weapon 或 apparel");
            }

            if (string.IsNullOrEmpty(thingLabel) && string.IsNullOrEmpty(thingDefName))
                return ToolResult.Error("需要提供 thing_label 或 thing_defName 来指定要装备的物品");

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    // 查找殖民者
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

                    // 根据装备类型搜索地图上的物品
                    List<Thing> candidates;
                    string itemTypeLabel;
                    if (equipType == "weapon")
                    {
                        candidates = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon);
                        itemTypeLabel = "武器";
                    }
                    else
                    {
                        candidates = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel);
                        itemTypeLabel = "衣物";
                    }

                    if (candidates == null || candidates.Count == 0)
                        return ToolResult.Error($"地图上没有任何可用的{itemTypeLabel}。");

                    // 匹配物品
                    Thing? matched = FindMatchingThing(candidates, thingLabel, thingDefName);
                    if (matched == null)
                    {
                        string searchKey = !string.IsNullOrEmpty(thingLabel) ? thingLabel : thingDefName;
                        // 列出可用物品帮助用户
                        var available = candidates
                            .Select(t => $"{t.Label} ({t.def.defName})")
                            .Take(10).ToArray();
                        return ToolResult.Error($"在地图上找不到匹配 '{searchKey}' 的{itemTypeLabel}。" +
                                               $"可用物品示例: {string.Join(", ", available)}");
                    }

                    // 获取品质信息
                    string qualityStr = "";
                    try
                    {
                        var compQuality = matched.TryGetComp<CompQuality>();
                        if (compQuality != null)
                        {
                            var qc = compQuality.Quality;
                            qualityStr = $"（品质: {qc.GetLabel()}）";
                        }
                    }
                    catch { /* 部分物品可能没有品质组件 */ }

                    // 执行装备
                    if (equipType == "weapon")
                    {
                        var weapon = matched as ThingWithComps;
                        if (weapon == null)
                            return ToolResult.Error($"{matched.Label} 无法作为武器装备。");
                        if (pawn.equipment == null)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有装备管理器（可能不是人类）。");

                        // 验证是否可以装备
                        if (!EquipmentUtility.CanEquip(weapon, pawn, out string equipReason, false))
                            return ToolResult.Error($"无法装备 {weapon.Label}：{equipReason}");

                        // 检查暴力工作标签
                        if (pawn.WorkTagIsDisabled(WorkTags.Violent))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止从事暴力工作，无法装备武器。");

                        // 检查射击工作标签（远程武器）
                        if (weapon.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 被禁止从事射击工作，无法装备远程武器。");

                        // 卸下当前主武器腾出空间
                        pawn.equipment.MakeRoomFor(weapon);
                        pawn.equipment.AddEquipment(weapon);

                        // 获取装备后的武器名称
                        var newWeapon = pawn.equipment.Primary;
                        string equippedName = newWeapon?.Label ?? matched.Label;
                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已装备武器: {equippedName} ({matched.def.defName}){qualityStr}。");
                    }
                    else
                    {
                        var apparel = matched as Apparel;
                        if (apparel == null)
                            return ToolResult.Error($"{matched.Label} 无法作为衣物穿戴。");
                        if (pawn.apparel == null)
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有衣物管理器（可能不是人类）。");

                        // 检查是否有合适的身体部位穿戴
                        if (!ApparelUtility.HasPartsToWear(pawn, apparel.def))
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有适合穿戴 {apparel.Label} 的身体部位。");

                        if (!EquipmentUtility.CanEquip(apparel, pawn, out string wearReason, false))
                            return ToolResult.Error($"无法穿戴 {apparel.Label}：{wearReason}");

                        pawn.apparel.Wear(apparel);

                        return ToolResult.Success($"{pawn.Name.ToStringShort} 已穿戴衣物: {matched.Label} ({matched.def.defName}){qualityStr}。");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"装备操作失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 在地图物品列表中按 label（模糊）或 defName（精确）查找匹配的物品。
        /// 优先使用 defName 精确匹配，其次使用 label 模糊匹配。
        /// </summary>
        private static Thing? FindMatchingThing(List<Thing> things, string label, string defName)
        {
            if (things == null || things.Count == 0) return null;

            // 优先: defName 精确匹配
            if (!string.IsNullOrEmpty(defName))
            {
                var exact = things.FirstOrDefault(t => t.def.defName == defName);
                if (exact != null) return exact;
            }

            // 其次: 物品 Label 模糊匹配
            if (!string.IsNullOrEmpty(label))
            {
                var byItemLabel = things.FirstOrDefault(t =>
                    t.Label != null && t.Label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
                if (byItemLabel != null) return byItemLabel;

                // 再次: def.label 模糊匹配
                var byDefLabel = things.FirstOrDefault(t =>
                    t.def.label != null && t.def.label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
                if (byDefLabel != null) return byDefLabel;
            }

            return null;
        }
    }
}
