using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetThingDef : ITool
    {
        public string Name => "get_thing_def";
        public string Description => "查询物品的 Def 元数据（defName、类别、属性、组件等），支持 thing_id 反查或 defName 直接查询。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "物品唯一 ID（来自 get_tile_detail），从地图物品反查 Def" },
                defName = new { type = "string", description = "Def 名称。thing_id 和 defName 二选一" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数，请提供 thing_id 或 defName。");

            int? thingId = null;
            if (args.Value.TryGetProperty("thing_id", out var jTid) && jTid.TryGetInt32(out var tid))
                thingId = tid;

            string? defName = null;
            if (args.Value.TryGetProperty("defName", out var jDef))
                defName = jDef.GetString();

            if (thingId == null && string.IsNullOrWhiteSpace(defName))
                return ToolResult.Error("请提供 thing_id 或 defName。");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    ThingDef def;

                    if (thingId.HasValue)
                    {
                        Map map = Find.CurrentMap;
                        if (map == null) return ToolResult.Error("没有当前地图。");
                        var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId.Value);
                        if (thing == null)
                            return ToolResult.Error($"找不到 ID={thingId} 的物品。");
                        def = thing.def;
                        defName = def.defName;
                    }
                    else
                    {
                        def = DefDatabase<ThingDef>.GetNamed(defName!, false);
                        if (def == null)
                            return ToolResult.Error($"找不到 Def: {defName}");
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine($"## {def.label ?? def.defName} ({def.defName})");
                    sb.AppendLine();

                    // 基本分类
                    sb.AppendLine($"- **defName**: `{def.defName}`");
                    sb.AppendLine($"- **label**: {def.label ?? "(无)"}");
                    sb.AppendLine($"- **category**: {def.category}");
                    sb.AppendLine($"- **thingClass**: `{def.thingClass?.Name ?? "Thing"}`");

                    if (!def.description.NullOrEmpty())
                    {
                        var desc = def.description.Length > 200 ? def.description.Substring(0, 200) + "..." : def.description;
                        sb.AppendLine($"- **description**: {desc}");
                    }

                    // 类型标记
                    sb.Append("- **flags**: ");
                    var flags = new System.Collections.Generic.List<string>();
                    if (def.IsWeapon) flags.Add("武器");
                    if (def.IsApparel) flags.Add("衣物");
                    if (def.IsMedicine) flags.Add("药物");
                    if (def.IsDrug) flags.Add("成瘾品");
                    if (def.IsNutritionGivingIngestible) flags.Add("食物");
                    if (def.IsRangedWeapon) flags.Add("远程武器");
                    if (def.IsMeleeWeapon) flags.Add("近战武器");
                    if (def.MadeFromStuff) flags.Add("可使用多种材料");
                    if (def.EverHaulable) flags.Add("可搬运");
                    if (def.destroyOnDrop) flags.Add("丢弃即毁");
                    if (def.useHitPoints) flags.Add("有耐久度");
                    if (def.designationCategory != null) flags.Add($"建造类别: {def.designationCategory.label}");
                    sb.AppendLine(flags.Count > 0 ? string.Join(" / ", flags) : "(无)");

                    // 基础属性
                    sb.AppendLine($"- **baseMarketValue**: {def.BaseMarketValue:F2}");
                    sb.AppendLine($"- **baseMass**: {def.BaseMass:F2} kg");
                    sb.AppendLine($"- **baseMaxHitPoints**: {def.BaseMaxHitPoints}");

                    // 材料信息
                    if (def.MadeFromStuff && def.stuffProps != null)
                    {
                        if (!def.stuffProps.stuffAdjective.NullOrEmpty())
                            sb.AppendLine($"- **stuffAdjective**: {def.stuffProps.stuffAdjective}");
                        if (def.stuffProps.categories != null && def.stuffProps.categories.Count > 0)
                        {
                            var cats = def.stuffProps.categories.Select(c => c.label).ToArray();
                            sb.AppendLine($"- **stuffCategories**: {string.Join(", ", cats)}");
                        }
                    }

                    if (def.costList != null && def.costList.Count > 0)
                    {
                        var costs = def.costList.Select(c => $"{c.thingDef.label} x{c.count}").ToArray();
                        sb.AppendLine($"- **costList**: {string.Join(", ", costs)}");
                    }
                    if (def.costStuffCount > 0)
                        sb.AppendLine($"- **costStuffCount**: {def.costStuffCount}");

                    // statBases（含护甲、隔热等）
                    if (def.statBases != null && def.statBases.Count > 0)
                    {
                        var stats = def.statBases.Select(s => $"{s.stat.label ?? s.stat.defName}={s.value}").ToArray();
                        sb.AppendLine($"- **statBases**: {string.Join(", ", stats)}");
                    }

                    // Comps
                    if (def.comps != null && def.comps.Count > 0)
                    {
                        var comps = def.comps.Select(c => c.compClass?.Name ?? c.GetType().Name).ToArray();
                        sb.AppendLine($"- **comps**: {string.Join(", ", comps)}");
                    }

                    // thingCategories（物品存储分类）
                    try
                    {
                        if (def.thingCategories != null && def.thingCategories.Count > 0)
                        {
                            var cats = def.thingCategories.Select(c => c.label).ToArray();
                            sb.AppendLine($"- **thingCategories**: {string.Join(", ", cats)}");
                        }
                    }
                    catch { }

                    // Apparel 特有
                    if (def.IsApparel && def.apparel != null)
                    {
                        try
                        {
                            sb.AppendLine($"- **apparelLayer**: {def.apparel.LastLayer.label}");
                            if (def.apparel.bodyPartGroups != null && def.apparel.bodyPartGroups.Count > 0)
                            {
                                var bps = def.apparel.bodyPartGroups.Select(b => b.label).ToArray();
                                sb.AppendLine($"- **covers**: {string.Join(", ", bps)}");
                            }
                            sb.AppendLine($"- **wearPerDay**: {def.apparel.wearPerDay:F2}");
                        }
                        catch { }
                    }

                    // Weapon 特有
                    if (def.IsWeapon && def.Verbs != null && def.Verbs.Count > 0)
                    {
                        var verbProps = def.Verbs[0];
                        if (verbProps.defaultProjectile != null)
                            sb.AppendLine($"- **projectile**: {verbProps.defaultProjectile.label}");
                        sb.AppendLine($"- **range**: {verbProps.range:F0}");
                        sb.AppendLine($"- **warmupTime**: {verbProps.warmupTime:F2}s");
                    }

                    // 制作/研究需求
                    if (def.researchPrerequisites != null && def.researchPrerequisites.Count > 0)
                    {
                        var tech = def.researchPrerequisites.Select(r => r.label).ToArray();
                        sb.AppendLine($"- **researchPrerequisites**: {string.Join(", ", tech)}");
                    }
                    if (def.recipeMaker != null)
                    {
                        var stationLabel = def.recipeMaker.recipeUsers?.FirstOrDefault()?.label ?? "通用";
                        sb.AppendLine($"- **craftable**: 是（可在 {stationLabel} 工作站制作）");
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"查询 Def 失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
