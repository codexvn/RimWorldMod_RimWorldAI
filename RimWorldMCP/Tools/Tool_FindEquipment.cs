using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_FindEquipment : ITool
    {
        public string Name => "find_equipment";
        public string Description => "搜索地图上所有可用的武器和衣物装备，按类型和品质分组，用于装备升级决策。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                type = new { type = "string", description = "装备类型: all / ranged / melee / apparel / armor" },
                min_quality = new { type = "string", description = "最低品质: Awful / Poor / Normal / Good / Excellent / Masterwork / Legendary" },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认30，最大50", @default = 30 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var equipType = "all";
            var minQuality = QualityCategory.Awful;
            int page = 1, pageSize = 30;

            if (args != null)
            {
                if (args.Value.TryGetProperty("type", out var jType) && jType.ValueKind == JsonValueKind.String)
                    equipType = jType.GetString()?.ToLowerInvariant() ?? "all";
                if (args.Value.TryGetProperty("min_quality", out var jQ) && jQ.ValueKind == JsonValueKind.String)
                    Enum.TryParse(jQ.GetString(), true, out minQuality);
            }
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(50, jps.GetInt32()));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    // 收集已装备物品 ID
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    var equippedIds = new HashSet<int>();
                    foreach (var pawn in colonists)
                    {
                        if (pawn.equipment?.Primary != null)
                            equippedIds.Add(pawn.equipment.Primary.thingIDNumber);
                        if (pawn.apparel?.WornApparel != null)
                            foreach (var ap in pawn.apparel.WornApparel)
                                equippedIds.Add(ap.thingIDNumber);
                    }

                    // 收集可用装备
                    var items = new List<Thing>();
                    foreach (var t in map.listerThings.AllThings)
                    {
                        if (t.Fogged()) continue;
                        if (t is Blueprint || t is Frame) continue;
                        if (equippedIds.Contains(t.thingIDNumber)) continue;
                        if (t.IsBurning()) continue;

                        bool isWeapon = t.def.IsWeapon || t.HasComp<CompEquippable>();
                        bool isApparel = t.def.IsApparel;

                        if (!isWeapon && !isApparel) continue;

                        // 类型过滤
                        if (equipType == "ranged" && !t.def.IsRangedWeapon) continue;
                        if (equipType == "melee" && !t.def.IsMeleeWeapon) continue;
                        if (equipType == "apparel" && !isApparel) continue;
                        if (equipType == "armor" && !isApparel) continue;

                        // 品质过滤
                        var compQuality = t.TryGetComp<CompQuality>();
                        if (compQuality != null && compQuality.Quality < minQuality) continue;

                        items.Add(t);
                    }

                    if (items.Count == 0)
                        return ToolResult.Success("地图上没有符合条件的可用装备。");

                    // 按品质降序排列，同品质按 thingIDNumber 稳定排序
                    items.Sort((a, b) =>
                    {
                        var qa = a.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
                        var qb = b.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
                        var qComp = qb.CompareTo(qa);
                        return qComp != 0 ? qComp : a.thingIDNumber.CompareTo(b.thingIDNumber);
                    });

                    // 分页
                    int total = items.Count;
                    var paged = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                    // 分组输出
                    var sb = new StringBuilder();
                    sb.AppendLine("## 可用装备");
                    sb.AppendLine();

                    var rangedWeapons = new List<Thing>();
                    var meleeWeapons = new List<Thing>();
                    var apparelItems = new List<Thing>();

                    foreach (var t in paged)
                    {
                        if (t.def.IsRangedWeapon)
                            rangedWeapons.Add(t);
                        else if (t.def.IsMeleeWeapon)
                            meleeWeapons.Add(t);
                        else if (t.def.IsApparel)
                            apparelItems.Add(t);
                        else if (t.HasComp<CompEquippable>())
                            meleeWeapons.Add(t);
                    }

                    // 远程武器
                    if (rangedWeapons.Count > 0 && (equipType == "all" || equipType == "ranged"))
                    {
                        sb.AppendLine("### 远程武器");
                        sb.AppendLine();
                        sb.AppendLine("| ID | 名称 | 品质 | 射程 | 伤害 | 预热 | 冷却 | 位置 |");
                        sb.AppendLine("|----|------|------|------|------|------|------|------|");
                        foreach (var t in rangedWeapons)
                        {
                            var quality = FormatQuality(t);
                            var pos = t.Position;
                            float range = 0, warmup = 0, cooldown = 0;
                            float avgDamage = 0;
                            if (t.def.Verbs != null && t.def.Verbs.Count > 0)
                            {
                                var v = t.def.Verbs[0];
                                range = v.range;
                                warmup = v.warmupTime;
                                cooldown = t.GetStatValue(StatDefOf.RangedWeapon_Cooldown, true, -1);
                                if (v.defaultProjectile?.projectile?.damageDef != null)
                                    avgDamage = v.defaultProjectile.projectile.damageDef.defaultDamage;
                            }
                            sb.AppendLine($"| {t.thingIDNumber} | {t.Label} | {quality} | {range:F0} | {avgDamage:F0} | {warmup:F1}s | {cooldown:F1}s | ({pos.x},{pos.z}) |");
                        }
                        sb.AppendLine();
                    }

                    // 近战武器
                    if (meleeWeapons.Count > 0 && (equipType == "all" || equipType == "melee"))
                    {
                        sb.AppendLine("### 近战武器");
                        sb.AppendLine();
                        sb.AppendLine("| ID | 名称 | 品质 | 伤害 | 冷却 | DPS | 穿甲 | 位置 |");
                        sb.AppendLine("|----|------|------|------|------|-----|------|------|");
                        foreach (var t in meleeWeapons)
                        {
                            var quality = FormatQuality(t);
                            var pos = t.Position;
                            float dmg = 0, cooldown = 0, dps = 0, armorPen = 0;
                            if (t.def.tools != null && t.def.tools.Count > 0)
                            {
                                var tool = t.def.tools[0];
                                dmg = tool.power;
                                cooldown = tool.cooldownTime;
                                dps = cooldown > 0 ? dmg / cooldown : 0;
                                armorPen = tool.armorPenetration;
                            }
                            sb.AppendLine($"| {t.thingIDNumber} | {t.Label} | {quality} | {dmg:F1} | {cooldown:F1}s | {dps:F1} | {armorPen:P0} | ({pos.x},{pos.z}) |");
                        }
                        sb.AppendLine();
                    }

                    // 护甲/衣物
                    if (apparelItems.Count > 0 && (equipType == "all" || equipType == "apparel" || equipType == "armor"))
                    {
                        sb.AppendLine("### 护甲/衣物");
                        sb.AppendLine();
                        sb.AppendLine("| ID | 名称 | 品质 | 利刃 | 钝击 | 热能 | 覆盖 | 层 | 位置 |");
                        sb.AppendLine("|----|------|------|------|------|------|------|----|------|");
                        foreach (var t in apparelItems)
                        {
                            var quality = FormatQuality(t);
                            var pos = t.Position;
                            float sharp = t.GetStatValue(StatDefOf.ArmorRating_Sharp, true, -1);
                            float blunt = t.GetStatValue(StatDefOf.ArmorRating_Blunt, true, -1);
                            float heat = t.GetStatValue(StatDefOf.ArmorRating_Heat, true, -1);
                            string coverage = t.def.apparel?.HumanBodyCoverage != null
                                ? $"{t.def.apparel.HumanBodyCoverage:P0}"
                                : "-";
                            string layer = t.def.apparel?.LastLayer?.label ?? "-";
                            sb.AppendLine($"| {t.thingIDNumber} | {t.Label} | {quality} | {sharp:P0} | {blunt:P0} | {heat:P0} | {coverage} | {layer} | ({pos.x},{pos.z}) |");
                        }
                        sb.AppendLine();
                    }

                    // 总结
                    sb.AppendLine("### 装备升级建议");
                    sb.AppendLine();
                    sb.AppendLine($"共找到 {total} 件可用装备：远程 {rangedWeapons.Count} / 近战 {meleeWeapons.Count} / 护甲 {apparelItems.Count}");
                    sb.AppendLine("使用 `equip_pawn(colonist_id=N, thing_id=<ID>)` 逐人装备，优先给无武器的殖民者配最高品质装备。");

                    if (total > pageSize)
                    {
                        int totalPages = (int)Math.Ceiling((double)total / pageSize);
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.Append($"第 {page}/{totalPages} 页，共 {total} 条");
                        if (page < totalPages) sb.Append($" | page={page + 1} 下一页");
                        if (page > 1) sb.Append($" | page={page - 1} 上一页");
                        sb.AppendLine();
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"搜索装备失败: {ex.Message}"); }
            });
        }

        private static string FormatQuality(Thing t)
        {
            var comp = t.TryGetComp<CompQuality>();
            return comp != null ? comp.Quality.GetLabel() : "-";
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
