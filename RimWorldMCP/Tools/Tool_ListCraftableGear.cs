using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_ListCraftableGear : ITool, INoMapRequired
    {
        public string Name => "list_craftable_gear";
        public string Description => "分页查询当前可制造的武器和装备配方。含科技前置、技能要求、材料清单，不同材质属性对比。category: weapons/apparel/all。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                category = new { type = "string", description = "装备类别: weapons(武器) / apparel(装备) / all(全部)", @enum = new[] { "weapons", "apparel", "all" }, @default = "all" },
                search = new { type = "string", description = "正则搜索配方名/defName/产物名，不传时返回全部" },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认8，最大30", @default = 8 }
            }
        });

        private static readonly string[] WeaponStuffs = { "Steel", "Plasteel", "Uranium" };
        private static readonly string[] ApparelStuffs = { "Cloth", "DevilstrandCloth", "Hyperweave", "Thrumbofur" };

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var category = "all";
            var search = "";
            int page = 1, pageSize = 8;

            if (args != null)
            {
                if (args.Value.TryGetProperty("category", out var jCat)) category = jCat.GetString()?.ToLowerInvariant() ?? "all";
                if (args.Value.TryGetProperty("search", out var jSearch)) search = jSearch.GetString() ?? "";
                if (args.Value.TryGetProperty("page", out var jp)) page = Math.Max(1, jp.GetInt32());
                if (args.Value.TryGetProperty("page_size", out var jps)) pageSize = Math.Max(1, Math.Min(30, jps.GetInt32()));
            }

            Regex? searchRegex = null;
            if (!string.IsNullOrEmpty(search))
            {
                try { searchRegex = new Regex(search, RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return ToolResult.Error($"无效正则: {search}"); }
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var allRecipes = DefDatabase<RecipeDef>.AllDefs;
                    var filtered = allRecipes.AsEnumerable();

                    filtered = filtered.Where(r => r.AvailableNow);
                    filtered = filtered.Where(r => !r.IsSurgery);
                    filtered = filtered.Where(r => r.ProducedThingDef != null);

                    switch (category)
                    {
                        case "weapons":
                            filtered = filtered.Where(r => r.ProducedThingDef!.IsWeapon);
                            break;
                        case "apparel":
                            filtered = filtered.Where(r => r.ProducedThingDef!.IsApparel);
                            break;
                        default:
                            filtered = filtered.Where(r => r.ProducedThingDef!.IsWeapon || r.ProducedThingDef!.IsApparel);
                            break;
                    }

                    if (searchRegex != null)
                    {
                        filtered = filtered.Where(r =>
                            (r.label != null && searchRegex.IsMatch(r.label)) ||
                            (r.defName != null && searchRegex.IsMatch(r.defName)) ||
                            (r.ProducedThingDef?.label != null && searchRegex.IsMatch(r.ProducedThingDef.label)));
                    }

                    var list = filtered.OrderBy(r => r.defName ?? "").ToList();
                    if (list.Count == 0)
                    {
                        var catLabel = category switch { "weapons" => "武器", "apparel" => "装备", _ => "武器和装备" };
                        return ToolResult.Success($"当前没有可制造的{catLabel}配方。");
                    }

                    int totalPages = (int)Math.Ceiling((double)list.Count / pageSize);
                    var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                    var weaponStuffsCache = WeaponStuffs
                        .Select(n => DefDatabase<ThingDef>.GetNamed(n, errorOnFail: false))
                        .Where(d => d != null).Cast<ThingDef>().ToList();
                    var apparelStuffsCache = ApparelStuffs
                        .Select(n => DefDatabase<ThingDef>.GetNamed(n, errorOnFail: false))
                        .Where(d => d != null).Cast<ThingDef>().ToList();

                    var sb = new StringBuilder();
                    var catLabel2 = category switch { "weapons" => "武器", "apparel" => "装备", _ => "武器和装备" };
                    sb.AppendLine($"## 可制造{catLabel2}配方 (第 {page}/{totalPages} 页, 共 {list.Count} 个)");
                    sb.AppendLine();

                    // --- 远程武器 ---
                    var rangedPaged = paged.Where(r => r.ProducedThingDef!.IsRangedWeapon).ToList();
                    if (rangedPaged.Count > 0)
                    {
                        sb.AppendLine("### 远程武器");
                        sb.AppendLine();
                        sb.AppendLine("| # | 配方 | defName | 伤害 | 射程 | 热身 | 冷却 | 所需科技 | 技能 | 工作台 | 工作量 |");
                        sb.AppendLine("|---|------|---------|------|------|------|------|----------|------|--------|--------|");

                        var idx = (page - 1) * pageSize;
                        foreach (var recipe in rangedPaged)
                        {
                            idx++;
                            var td = recipe.ProducedThingDef!;
                            var label = recipe.label ?? recipe.defName ?? "???";
                            var defName = recipe.defName ?? "???";

                            float range = 0, warmup = 0, avgDmg = 0;
                            if (td.Verbs != null && td.Verbs.Count > 0)
                            {
                                var v = td.Verbs[0];
                                range = v.range;
                                warmup = v.warmupTime;
                                avgDmg = v.defaultProjectile?.projectile?.damageDef?.defaultDamage ?? 0;
                            }
                            float cooldown = td.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown);

                            sb.AppendLine($"| {idx} | {label} | `{defName}` | {avgDmg:F0} | {range:F0}格 | {warmup:F1}s | {cooldown:F1}s | {FormatTechReq(recipe)} | {FormatSkillReq(recipe)} | {FormatWorkbenches(recipe)} | {(recipe.workAmount > 0 ? recipe.workAmount.ToString("F0") : "-")} |");

                            AppendStuffRows(sb, recipe, td, weaponStuffsCache, "ranged");
                        }
                        sb.AppendLine();
                    }

                    // --- 近战武器 ---
                    var meleePaged = paged.Where(r => r.ProducedThingDef!.IsMeleeWeapon).ToList();
                    if (meleePaged.Count > 0)
                    {
                        sb.AppendLine("### 近战武器");
                        sb.AppendLine();
                        sb.AppendLine("| # | 配方 | defName | 伤害 | 冷却 | DPS | 穿甲 | 所需科技 | 技能 | 工作台 | 工作量 |");
                        sb.AppendLine("|---|------|---------|------|------|-----|------|----------|------|--------|--------|");

                        var idx = (page - 1) * pageSize;
                        foreach (var recipe in meleePaged)
                        {
                            idx++;
                            var td = recipe.ProducedThingDef!;
                            var label = recipe.label ?? recipe.defName ?? "???";
                            var defName = recipe.defName ?? "???";

                            float dmg = 0, cooldown = 0, dps = 0, armorPen = 0;
                            if (td.tools != null && td.tools.Count > 0)
                            {
                                var tool = td.tools[0];
                                dmg = tool.power;
                                cooldown = tool.cooldownTime;
                                dps = cooldown > 0 ? dmg / cooldown : 0;
                                armorPen = tool.armorPenetration;
                            }

                            sb.AppendLine($"| {idx} | {label} | `{defName}` | {dmg:F1} | {cooldown:F1}s | {dps:F1} | {armorPen:P0} | {FormatTechReq(recipe)} | {FormatSkillReq(recipe)} | {FormatWorkbenches(recipe)} | {(recipe.workAmount > 0 ? recipe.workAmount.ToString("F0") : "-")} |");

                            AppendStuffRows(sb, recipe, td, weaponStuffsCache, "melee");
                        }
                        sb.AppendLine();
                    }

                    // --- 装备 ---
                    var apparelPaged = paged.Where(r => r.ProducedThingDef!.IsApparel).ToList();
                    if (apparelPaged.Count > 0)
                    {
                        sb.AppendLine("### 装备");
                        sb.AppendLine();
                        sb.AppendLine("| # | 配方 | defName | 利刃 | 钝击 | 热能 | 覆盖 | 层 | 所需科技 | 技能 | 工作台 | 工作量 |");
                        sb.AppendLine("|---|------|---------|------|------|------|------|----|----------|------|--------|--------|");

                        var idx = (page - 1) * pageSize;
                        foreach (var recipe in apparelPaged)
                        {
                            idx++;
                            var td = recipe.ProducedThingDef!;
                            var label = recipe.label ?? recipe.defName ?? "???";
                            var defName = recipe.defName ?? "???";

                            float sharp = td.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp);
                            float blunt = td.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt);
                            float heat = td.GetStatValueAbstract(StatDefOf.ArmorRating_Heat);
                            var coverage = td.apparel?.HumanBodyCoverage != null ? $"{td.apparel.HumanBodyCoverage:P0}" : "-";
                            var layer = td.apparel?.LastLayer?.label ?? "-";

                            sb.AppendLine($"| {idx} | {label} | `{defName}` | {sharp:P0} | {blunt:P0} | {heat:P0} | {coverage} | {layer} | {FormatTechReq(recipe)} | {FormatSkillReq(recipe)} | {FormatWorkbenches(recipe)} | {(recipe.workAmount > 0 ? recipe.workAmount.ToString("F0") : "-")} |");

                            AppendStuffRows(sb, recipe, td, apparelStuffsCache, "apparel");
                        }
                        sb.AppendLine();
                    }

                    if (list.Count > pageSize)
                    {
                        sb.AppendLine("---");
                        sb.Append($"第 {page}/{totalPages} 页");
                        if (page < totalPages) sb.Append($"  → page={page + 1} 下一页");
                        if (page > 1) sb.Append($"  ← page={page - 1} 上一页");
                        sb.AppendLine();
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"查询可制造装备失败: {ex.Message}");
                }
            });
        }

        private static void AppendStuffRows(StringBuilder sb, RecipeDef recipe, ThingDef td, List<ThingDef> commonStuffs, string gearType)
        {
            if (!td.MadeFromStuff || td.stuffCategories == null || td.stuffCategories.Count == 0)
                return;

            var validStuffs = commonStuffs.Where(s =>
                s.stuffProps?.categories != null &&
                td.stuffCategories.Any(sc => s.stuffProps.categories.Contains(sc)) &&
                RecipeAllowsStuff(recipe, s)
            ).ToList();

            if (validStuffs.Count == 0) return;

            // 材料行用紧凑格式：  材料A: 值1 | 材料B: 值2 ...
            var parts = new List<string>();
            foreach (var stuff in validStuffs)
            {
                var stuffLabel = stuff.label ?? stuff.defName ?? "???";
                if (gearType == "melee")
                {
                    var dps = td.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS, stuff);
                    parts.Add($"{stuffLabel} DPS:{dps:F2}");
                }
                else if (gearType == "ranged")
                {
                    var dmgMul = td.GetStatValueAbstract(StatDefOf.RangedWeapon_DamageMultiplier, stuff);
                    var penMul = td.GetStatValueAbstract(StatDefOf.RangedWeapon_ArmorPenetrationMultiplier, stuff);
                    var acc = td.GetStatValueAbstract(StatDefOf.AccuracyTouch, stuff);
                    parts.Add($"{stuffLabel} 伤害x{dmgMul:F1} 穿甲x{penMul:F1} 精度{acc:P0}");
                }
                else
                {
                    float sharp = td.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff);
                    float blunt = td.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff);
                    float heat = td.GetStatValueAbstract(StatDefOf.ArmorRating_Heat, stuff);
                    parts.Add($"{stuffLabel} 利刃:{sharp:P0} 钝击:{blunt:P0} 热能:{heat:P0}");
                }
            }
            sb.AppendLine($"|   └ 材质对比 | | | {string.Join(" | ", parts)} | | | | | | | |");
        }

        private static bool RecipeAllowsStuff(RecipeDef recipe, ThingDef stuff)
        {
            if (recipe.fixedIngredientFilter.Allows(stuff)) return true;
            if (recipe.defaultIngredientFilter != null && recipe.defaultIngredientFilter.Allows(stuff)) return true;
            if (recipe.ingredients != null)
            {
                foreach (var ing in recipe.ingredients)
                {
                    if (ing.filter.Allows(stuff)) return true;
                }
            }
            return false;
        }

        private static string FormatTechReq(RecipeDef recipe)
        {
            var names = new List<string>();
            if (recipe.researchPrerequisite != null)
                names.Add(recipe.researchPrerequisite.label ?? recipe.researchPrerequisite.defName ?? "???");
            if (recipe.researchPrerequisites != null)
            {
                foreach (var rp in recipe.researchPrerequisites)
                    names.Add(rp.label ?? rp.defName ?? "???");
            }
            return names.Count > 0 ? string.Join(", ", names.Distinct()) : "-";
        }

        private static string FormatSkillReq(RecipeDef recipe)
        {
            if (recipe.skillRequirements == null || recipe.skillRequirements.Count == 0) return "-";
            return string.Join(", ", recipe.skillRequirements.Select(sr =>
            {
                var label = sr.skill?.label ?? "???";
                return $"{label}{sr.minLevel}+";
            }));
        }

        private static string FormatWorkbenches(RecipeDef recipe)
        {
            if (recipe.recipeUsers == null || recipe.recipeUsers.Count == 0) return "-";
            var actual = recipe.recipeUsers
                .Where(u => u.thingClass != null && typeof(Building).IsAssignableFrom(u.thingClass))
                .ToList();
            return actual.Count > 0 ? string.Join(", ", actual.Select(u => u.label ?? u.defName ?? "???")) : "-";
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
