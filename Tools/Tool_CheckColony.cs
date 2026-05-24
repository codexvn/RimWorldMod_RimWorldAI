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
    public class Tool_CheckColony : ITool
    {
        public string Name => "check_colony";
        public string Description =>
            "获取殖民地当前需关注的提醒。应在完成操作后或等待期间定期调用此工具检查是否有新问题。" +
            "返回内容包括空闲殖民者、资源短缺、崩溃风险、受伤、威胁等。如无问题则返回简短确认。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        // 上次快照——用于对比变化
        private static int _lastIdleCount = -1;
        private static int _lastBreakRiskCount = -1;
        private static int _lastBleederCount = -1;
        private static int _lastFoodDays = -1;

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    int cCount = colonists?.Count ?? 0;
                    var sb = new StringBuilder();

                    // 空闲殖民者
                    var idle = colonists?.Where(c =>
                        (c.CurJob?.def?.defName == "Wait_MaintainPosture" || c.CurJob == null)
                        && !c.Downed && !c.Deathresting).ToList();
                    int idleCount = idle?.Count ?? 0;

                    // 崩溃风险
                    var breakRisk = colonists?.Where(c => (c.needs?.mood?.CurLevelPercentage ?? 1f) < 0.2f).ToList();
                    int breakCount = breakRisk?.Count ?? 0;

                    // 流血
                    var bleed = colonists?.Where(c => c.health?.hediffSet?.BleedRateTotal > 0.3f).ToList();
                    int bleedCount = bleed?.Count ?? 0;

                    // 食物天数
                    int foodDays = 99;
                    if (cCount > 0)
                    {
                        float totalFood = 0f;
                        foreach (var kvp in map.resourceCounter?.AllCountedAmounts ?? new())
                        {
                            var def = kvp.Key;
                            if (def.IsNutritionGivingIngestible && def.ingestible?.HumanEdible == true
                                && (def.ingestible?.foodType & FoodTypeFlags.Tree) == 0)
                                totalFood += kvp.Value * def.ingestible.CachedNutrition;
                        }
                        foodDays = (int)(totalFood / (cCount * 1.6f));
                    }

                    // 决定是否详细汇报——有变化或首次调用时详细
                    bool hasNewIssue =
                        idleCount != _lastIdleCount ||
                        breakCount != _lastBreakRiskCount ||
                        bleedCount != _lastBleederCount ||
                        foodDays != _lastFoodDays;

                    _lastIdleCount = idleCount;
                    _lastBreakRiskCount = breakCount;
                    _lastBleederCount = bleedCount;
                    _lastFoodDays = foodDays;

                    bool anythingWrong = idleCount > 0 || breakCount > 0 || bleedCount > 0
                        || foodDays < 3;

                    if (!anythingWrong)
                    {
                        // 一切正常，简短回复
                        _lastIdleCount = -1; _lastBreakRiskCount = -1;
                        _lastBleederCount = -1; _lastFoodDays = -1;
                        sb.AppendLine($"一切正常 —— {cCount} 名殖民者，食物够 {foodDays} 天。");
                        sb.AppendLine("建议等待几秒后再次调用本工具检查。");
                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }

                    if (!hasNewIssue)
                    {
                        // 问题没变，简短重复
                        sb.AppendLine($"状态不变: 空闲 {idleCount}, 崩溃风险 {breakCount}, 流血 {bleedCount}, 食物 {foodDays}天");
                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }

                    // === 以下仅在首次出现或状态变化时详细报告 ===

                    sb.AppendLine("## ⚠ 殖民地提醒");
                    sb.AppendLine();

                    if (idleCount > 0 && idle != null)
                    {
                        sb.AppendLine($"### 空闲殖民者 ({idleCount})");
                        foreach (var c in idle.Take(5))
                            sb.AppendLine($"- {c.Name.ToStringShort} ({c.Position.x},{c.Position.z})");
                        if (idleCount > 5) sb.AppendLine($"- ... 另有 {idleCount - 5} 人");
                        sb.AppendLine();
                    }

                    if (breakCount > 0 && breakRisk != null)
                    {
                        sb.AppendLine($"### 崩溃风险 ({breakCount})");
                        foreach (var c in breakRisk)
                        {
                            var thoughtList = new List<Thought>();
                            c.needs!.mood!.thoughts.GetAllMoodThoughts(thoughtList);
                            var worst = thoughtList.OrderBy(t => t.MoodOffset()).FirstOrDefault();
                            sb.Append($"- {c.Name.ToStringShort}: 心情 {c.needs.mood.CurLevelPercentage * 100f:F0}%");
                            if (worst != null) sb.Append($" — {worst.LabelCap} ({worst.MoodOffset():+0;-0})");
                            sb.AppendLine();
                        }
                        sb.AppendLine();
                    }

                    if (bleedCount > 0 && bleed != null)
                    {
                        sb.AppendLine($"### 严重流血 ({bleedCount})");
                        foreach (var c in bleed)
                            sb.AppendLine($"- {c.Name.ToStringShort}: 失血 {c.health!.hediffSet.BleedRateTotal * 100f:F0}%/天");
                        sb.AppendLine();
                    }

                    if (foodDays < 3)
                    {
                        sb.AppendLine($"### ⚠ 食物不足: 仅 {foodDays} 天储备");
                        sb.AppendLine();
                    }

                    // 防御检查
                    int turrets = map.listerBuildings.AllBuildingsColonistOfClass<Building_Turret>().Count();
                    int traps = map.listerBuildings.AllBuildingsColonistOfClass<Building_Trap>().Count();
                    float wealth = map.wealthWatcher?.WealthTotal ?? 0f;
                    if (turrets == 0 && traps == 0 && wealth > 15000)
                    {
                        sb.AppendLine("### ⚠ 无防御工事");
                        sb.AppendLine($"- 财富 {wealth:N0}，无炮塔/陷阱");
                        sb.AppendLine();
                    }

                    // 床铺
                    int beds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>()
                        .Count(b => !b.ForPrisoners && !b.Medical);
                    if (cCount > beds)
                    {
                        sb.AppendLine($"### ⚠ 缺床: {cCount}人仅{beds}张床");
                        sb.AppendLine();
                    }

                    sb.AppendLine($"---\n上次检查无异常，现在出现 {idleCount + breakCount + bleedCount + (foodDays < 3 ? 1 : 0) + (turrets == 0 && traps == 0 && wealth > 15000 ? 1 : 0) + (cCount > beds ? 1 : 0)} 项提醒。");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"警报检查失败: {ex.Message}");
                }
            });
        }
    }
}
