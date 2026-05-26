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
    /// <summary>
    /// 获取指定殖民者的推荐武器列表，按科技等级从高到低排列。
    /// 支持按武器类型（近战/远程）过滤。
    /// </summary>
    public class Tool_GetRecommendedWeapon : ITool
    {
        public string Name => "get_recommended_weapon";
        public string Description => "获取指定殖民者的推荐武器列表，按科技等级从高到低排列。输入殖民者 thingIDNumber 和武器类型（ranged=远程/melee=近战），返回地图上所有可用武器的排名。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 thingIDNumber（来自 get_colonists）" },
                weapon_type = new { type = "string", description = "武器类型: ranged（远程） / melee（近战）" }
            },
            required = new[] { "colonist_id", "weapon_type" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            if (!args.Value.TryGetProperty("colonist_id", out var jId) || !jId.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少 colonist_id 参数");

            if (!args.Value.TryGetProperty("weapon_type", out var jType) || jType.ValueKind != JsonValueKind.String)
                return ToolResult.Error("缺少 weapon_type 参数（ranged 或 melee）");

            var weaponType = jType.GetString()?.ToLowerInvariant();
            if (weaponType != "ranged" && weaponType != "melee")
                return ToolResult.Error("weapon_type 必须是 ranged 或 melee");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null) return ToolResult.Error($"未找到殖民者 ID={colonistId}");

                    bool wantRanged = weaponType == "ranged";

                    // 当前已装备的武器
                    var currentPrimary = pawn.equipment?.Primary;
                    int currentEquippedId = currentPrimary?.thingIDNumber ?? -1;

                    // 收集地图上所有符合条件的武器
                    var candidates = new List<Thing>();
                    foreach (var t in map.listerThings.AllThings)
                    {
                        if (t is Blueprint || t is Frame) continue;
                        if (t.IsBurning()) continue;

                        // 类型过滤
                        bool isTargetType = wantRanged ? t.def.IsRangedWeapon : t.def.IsMeleeWeapon;
                        if (!isTargetType) continue;

                        // 检查是否可装备
                        if (!EquipmentUtility.CanEquip(t, pawn)) continue;

                        candidates.Add(t);
                    }

                    if (candidates.Count == 0)
                    {
                        string typeName = wantRanged ? "远程" : "近战";
                        return ToolResult.Success($"地图上没有 {pawn.LabelShort} 可用的推荐{typeName}武器。");
                    }

                    // 按科技等级降序 → 品质降序 → DPS 降序
                    candidates.Sort((a, b) =>
                    {
                        int tlComp = b.def.techLevel.CompareTo(a.def.techLevel);
                        if (tlComp != 0) return tlComp;

                        var qa = a.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
                        var qb = b.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
                        int qComp = qb.CompareTo(qa);
                        if (qComp != 0) return qComp;

                        float dpsA = GetApproxDPS(a, wantRanged);
                        float dpsB = GetApproxDPS(b, wantRanged);
                        return dpsB.CompareTo(dpsA);
                    });

                    // 构建输出
                    var sb = new StringBuilder();
                    string typeLabel = wantRanged ? "远程" : "近战";
                    sb.AppendLine($"## {pawn.LabelShort} 推荐{typeLabel}武器（按科技等级降序）");
                    sb.AppendLine();

                    if (currentPrimary != null)
                    {
                        string curType = currentPrimary.def.IsRangedWeapon ? "远程" : "近战";
                        sb.AppendLine($"当前装备: **{currentPrimary.Label}** ({curType}, {FormatQuality(currentPrimary)}, 科技: {currentPrimary.def.techLevel.ToStringHuman()})");
                    }
                    else
                    {
                        sb.AppendLine("当前装备: **无**");
                    }
                    sb.AppendLine();

                    if (wantRanged)
                    {
                        sb.AppendLine("| 排名 | ID | 名称 | 品质 | 科技等级 | 射程 | 伤害 | 预热 | 冷却 | 位置 |");
                        sb.AppendLine("|------|-----|------|------|----------|------|------|------|------|------|");
                        int rank = 0;
                        TechLevel prevTech = TechLevel.Undefined;
                        QualityCategory prevQuality = QualityCategory.Awful;
                        foreach (var t in candidates)
                        {
                            var tl = t.def.techLevel;
                            var quality = t.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
                            if (tl != prevTech || quality != prevQuality)
                                rank++;
                            prevTech = tl;
                            prevQuality = quality;

                            float range = 0, warmup = 0, cooldown = 0, avgDamage = 0;
                            if (t.def.Verbs != null && t.def.Verbs.Count > 0)
                            {
                                var v = t.def.Verbs[0];
                                range = v.range;
                                warmup = v.warmupTime;
                                cooldown = t.GetStatValue(StatDefOf.RangedWeapon_Cooldown, true, -1);
                                if (v.defaultProjectile?.projectile?.damageDef != null)
                                    avgDamage = v.defaultProjectile.projectile.damageDef.defaultDamage;
                                if (v.burstShotCount > 1)
                                    avgDamage *= v.burstShotCount;
                            }
                            var pos = t.Position;

                            string label = t.Label;
                            if (t.thingIDNumber == currentEquippedId)
                                label += " [已装备]";

                            sb.AppendLine($"| {rank} | {t.thingIDNumber} | {label} | {FormatQuality(t)} | {tl.ToStringHuman()} | {range:F0} | {avgDamage:F0} | {warmup:F1}s | {cooldown:F1}s | ({pos.x},{pos.z}) |");
                        }
                    }
                    else
                    {
                        sb.AppendLine("| 排名 | ID | 名称 | 品质 | 科技等级 | 伤害 | 冷却 | DPS | 穿甲 | 位置 |");
                        sb.AppendLine("|------|-----|------|------|----------|------|------|-----|------|------|");
                        int rank = 0;
                        TechLevel prevTech = TechLevel.Undefined;
                        QualityCategory prevQuality = QualityCategory.Awful;
                        foreach (var t in candidates)
                        {
                            var tl = t.def.techLevel;
                            var quality = t.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
                            if (tl != prevTech || quality != prevQuality)
                                rank++;
                            prevTech = tl;
                            prevQuality = quality;

                            float dmg = 0, cooldown = 0, armorPen = 0;
                            if (t.def.tools != null && t.def.tools.Count > 0)
                            {
                                var tool = t.def.tools[0];
                                dmg = tool.power;
                                cooldown = tool.cooldownTime;
                                armorPen = tool.armorPenetration;
                            }
                            float dps = cooldown > 0 ? dmg / cooldown : 0;
                            var pos = t.Position;

                            string label = t.Label;
                            if (t.thingIDNumber == currentEquippedId)
                                label += " [已装备]";

                            sb.AppendLine($"| {rank} | {t.thingIDNumber} | {label} | {FormatQuality(t)} | {tl.ToStringHuman()} | {dmg:F1} | {cooldown:F1}s | {dps:F1} | {armorPen:P0} | ({pos.x},{pos.z}) |");
                        }
                    }

                    sb.AppendLine();
                    int candidateCount = candidates.Count;
                    sb.Append($"共 {candidateCount} 件可用{typeLabel}武器");

                    if (candidates.Count > 0)
                    {
                        var best = candidates[0];
                        string action = best.thingIDNumber == currentEquippedId
                            ? "当前已装备最佳"
                            : $"装备: equip_pawn(colonist_id={colonistId}, thing_id={best.thingIDNumber})";
                        sb.Append($" | 推荐: {best.Label} ({best.def.techLevel.ToStringHuman()}, ID={best.thingIDNumber})");
                        sb.Append($" | {action}");
                    }

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"推荐武器失败: {ex.Message}");
                }
            });
        }

        private static float GetApproxDPS(Thing weapon, bool isRanged)
        {
            if (isRanged)
            {
                if (weapon.def.Verbs == null || weapon.def.Verbs.Count == 0) return 0;
                var v = weapon.def.Verbs[0];
                float damage = v.defaultProjectile?.projectile?.damageDef?.defaultDamage ?? 0;
                float warmup = v.warmupTime;
                float cooldown = weapon.GetStatValue(StatDefOf.RangedWeapon_Cooldown, true, -1);
                float cycleTime = warmup + cooldown;
                if (v.burstShotCount > 1)
                {
                    float burstTicks = (v.burstShotCount - 1) * v.ticksBetweenBurstShots / 60f;
                    cycleTime += burstTicks;
                    damage *= v.burstShotCount;
                }
                return cycleTime > 0 ? damage / cycleTime : 0;
            }
            else
            {
                if (weapon.def.tools == null || weapon.def.tools.Count == 0) return 0;
                var tool = weapon.def.tools[0];
                return tool.cooldownTime > 0 ? tool.power / tool.cooldownTime : 0;
            }
        }

        private static string FormatQuality(Thing t)
        {
            var comp = t.TryGetComp<CompQuality>();
            return comp != null ? comp.Quality.GetLabel() : "-";
        }

        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
