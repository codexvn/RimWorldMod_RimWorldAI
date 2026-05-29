using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetDefenseStatus : ITool
    {
        public string Name => "get_defense_status";
        public string Description => "获取殖民地防御状态报告：所有殖民者的武器装备、护甲覆盖、征召状态和战斗力评估。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null)
                    return ToolResult.Error("当前没有可用地图。");

                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                var sb = new StringBuilder();
                sb.AppendLine("## 防御状态报告");
                sb.AppendLine();

                // === 武器装备 ===
                sb.AppendLine("### 武器装备");
                sb.AppendLine();
                sb.AppendLine("| 殖民者 | 射击 | 近战 | 主武器 | 护甲 | 征召 |");
                sb.AppendLine("|--------|------|------|--------|------|------|");

                int rangedCount = 0;
                int meleeCount = 0;
                int unarmedCount = 0;
                int armoredCount = 0;
                int combatIncapableCount = 0;
                int meleeCapableCount = 0;

                foreach (var pawn in colonists)
                {
                    var name = pawn.Name?.ToStringShort ?? pawn.LabelShortCap;
                    var drafted = pawn.Drafted;
                    var draftedText = drafted ? "已征召" : "未征召";

                    // 战斗能力检查
                    bool isCombatIncapable = pawn.WorkTagIsDisabled(WorkTags.Violent);
                    if (isCombatIncapable)
                    {
                        name += " (非战斗人员)";
                        combatIncapableCount++;
                    }

                    // 射击和近战技能
                    string shootingSkill = "-";
                    string meleeSkill = "-";
                    if (pawn.skills != null)
                    {
                        try
                        {
                            var shooting = pawn.skills.GetSkill(SkillDefOf.Shooting);
                            shootingSkill = shooting != null ? shooting.Level.ToString() : "-";
                        }
                        catch (Exception) { }
                        try
                        {
                            var melee = pawn.skills.GetSkill(SkillDefOf.Melee);
                            meleeSkill = melee != null ? melee.Level.ToString() : "-";
                        }
                        catch (Exception) { }
                    }

                    // 近战适配判定：格斗者特性 / 高近战低射击
                    bool isBrawler = pawn.story?.traits?.HasTrait(TraitDefOf.Brawler) ?? false;
                    int meleeLevel = 0;
                    int shootingLevel = 0;
                    try { meleeLevel = pawn.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0; } catch { }
                    try { shootingLevel = pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0; } catch { }
                    bool isMeleeSuitable = isBrawler || (meleeLevel >= 8 && shootingLevel < 6);
                    if (isMeleeSuitable && !isCombatIncapable)
                    {
                        meleeCapableCount++;
                        if (isBrawler) name += " [格斗者]";
                    }

                    // 主武器
                    var weapon = "-";
                    var equipment = pawn.equipment;
                    if (equipment != null && equipment.Primary != null)
                    {
                        var primary = equipment.Primary;
                        weapon = primary.def?.label ?? primary.def?.defName ?? "???";
                        var quality = primary.TryGetComp<CompQuality>();
                        if (quality != null)
                            weapon += $" ({quality.Quality.GetLabel()})";

                        // 判断远程还是近战（仅战斗人员计入统计）
                        if (!isCombatIncapable)
                        {
                            if (primary.def?.IsRangedWeapon == true)
                                rangedCount++;
                            else if (primary.def?.IsMeleeWeapon == true)
                                meleeCount++;
                        }
                    }
                    else
                    {
                        if (!isCombatIncapable)
                            unarmedCount++;
                    }

                    // 护甲
                    var armorParts = new List<string>();
                    var apparel = pawn.apparel;
                    if (apparel != null)
                    {
                        var wornApparel = apparel.WornApparel;
                        if (wornApparel != null)
                        {
                            foreach (var ap in wornApparel)
                            {
                                var apLabel = ap.def?.label ?? ap.def?.defName ?? "???";
                                var apQuality = ap.TryGetComp<CompQuality>();
                                var qualitySuffix = apQuality != null ? $" ({apQuality.Quality.GetLabel()})" : "";
                                armorParts.Add($"{apLabel}{qualitySuffix}");
                            }
                        }
                    }

                    if (armorParts.Count > 0)
                        armoredCount++;

                    var armorText = armorParts.Count > 0 ? string.Join(", ", armorParts) : "无";

                    sb.AppendLine($"| {name} | {shootingSkill} | {meleeSkill} | {weapon} | {armorText} | {draftedText} |");
                }

                // === 护甲防御力 ===
                sb.AppendLine();
                sb.AppendLine("### 护甲防御力");
                sb.AppendLine();
                sb.AppendLine("| 殖民者 | 利刃 | 钝击 | 热能 |");
                sb.AppendLine("|--------|------|------|------|");

                foreach (var pawn in colonists)
                {
                    var name = pawn.Name?.ToStringShort ?? pawn.LabelShortCap;
                    try
                    {
                        float sharp = pawn.GetStatValue(StatDefOf.ArmorRating_Sharp, true, -1);
                        float blunt = pawn.GetStatValue(StatDefOf.ArmorRating_Blunt, true, -1);
                        float heat = pawn.GetStatValue(StatDefOf.ArmorRating_Heat, true, -1);
                        sb.AppendLine($"| {name} | {sharp:P0} | {blunt:P0} | {heat:P0} |");
                    }
                    catch (Exception)
                    {
                        sb.AppendLine($"| {name} | - | - | - |");
                    }
                }

                // === 战斗力评估 ===
                sb.AppendLine();
                sb.AppendLine("### 战斗力评估");
                sb.AppendLine();
                sb.AppendLine($"- 远程火力: {rangedCount} 人");
                sb.AppendLine($"- 近战单位: {meleeCount} 人");
                sb.AppendLine($"- 可配近战: {meleeCapableCount} 人 (格斗者/高近战)");
                sb.AppendLine($"- 无武器: {unarmedCount} 人");
                sb.AppendLine($"- 有护甲: {armoredCount} 人");
                if (combatIncapableCount > 0)
                    sb.AppendLine($"- 非战斗人员: {combatIncapableCount} 人");

                // === 阵地设施 ===
                sb.AppendLine();
                sb.AppendLine("### 阵地设施");
                sb.AppendLine();

                // 统计炮塔
                var turrets = map.listerBuildings.AllBuildingsColonistOfClass<Building_Turret>()
                    .Where(t => t.GetComp<CompPowerTrader>() != null)
                    .ToList();

                var poweredTurrets = turrets.Count(t =>
                {
                    var comp = t.GetComp<CompPowerTrader>();
                    return comp != null && comp.PowerOn;
                });
                var totalTurrets = turrets.Count;

                // 统计迫击炮
                var mortars = map.listerBuildings.AllBuildingsColonistOfClass<Building_TurretGun>()
                    .Where(t => t.def.building.IsMortar)
                    .ToList();
                int totalMortars = mortars.Count;

                // 统计陷阱
                var traps = map.listerBuildings.AllBuildingsColonistOfClass<Building_Trap>();
                var totalTraps = traps?.Count() ?? 0;

                // 统计沙袋
                var sandbags = map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.Sandbags);
                var totalSandbags = sandbags?.Count ?? 0;

                sb.AppendLine($"- 炮塔: {totalTurrets} 座 (已供电: {poweredTurrets})");
                sb.AppendLine($"- 迫击炮: {totalMortars} 座");
                sb.AppendLine($"- 陷阱: {totalTraps} 个");
                sb.AppendLine($"- 沙袋: {totalSandbags} 格");

                // === 防御建议 ===
                sb.AppendLine();
                sb.AppendLine("### 防御建议");
                sb.AppendLine();

                int combatCapableCount = colonists.Count - combatIncapableCount;

                var recommendations = new List<string>();

                if (unarmedCount > 0)
                {
                    if (meleeCapableCount > 0 && meleeCount == 0)
                        recommendations.Add($"有 {meleeCapableCount} 名殖民者适合近战（格斗者/高近战技能），请为其配备近战武器用于堵门。");
                    if (unarmedCount > meleeCapableCount || meleeCapableCount == 0)
                        recommendations.Add($"有 {unarmedCount} 名殖民者未装备武器，建议配备基础远程武器。");
                }

                if (meleeCapableCount > 0 && meleeCount > 0 && meleeCount < meleeCapableCount)
                    recommendations.Add($"还有 {meleeCapableCount - meleeCount} 名近战适配者未装备近战武器，建议配备。");

                if (combatCapableCount > 0 && rangedCount < combatCapableCount * 0.6f)
                    recommendations.Add("远程火力覆盖不足，建议提升远程战斗人员比例。");

                if (combatCapableCount > 0 && armoredCount < combatCapableCount * 0.5f)
                    recommendations.Add("多数殖民者缺少护甲防护，建议制作简易头盔和防弹背心。");

                if (totalTurrets == 0)
                    recommendations.Add("当前无炮塔防御，建议在阵地部署至少2-3座炮塔。");

                if (totalMortars == 0)
                    recommendations.Add("当前无迫击炮，建议建造至少1座迫击炮用于远程轰击。");

                if (totalTraps == 0)
                    recommendations.Add("当前无陷阱，建议在关键通道布设陷阱减缓敌人推进。");

                if (recommendations.Count == 0)
                    sb.AppendLine("防御状态良好，暂无紧急建议。");
                else
                {
                    for (int i = 0; i < recommendations.Count; i++)
                        sb.AppendLine($"{i + 1}. {recommendations[i]}");
                }

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
