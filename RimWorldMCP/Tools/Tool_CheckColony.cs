using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;
using RimWorldMCP.Helpers;

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

        // 上次状态哈希——用于对比变化
        private static int _lastCategoryHash = -1;

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

                    // 获取原生警报
                    var nativeAlerts = NativeAlertHelper.GetActiveAlerts();
                    var categoryCounts = NativeAlertHelper.GetAlertCountsByCategory(nativeAlerts);
                    int foodDays = NativeAlertHelper.CalcFoodDays(map, cCount);

                    // 各类别计数
                    int idleCount = categoryCounts.GetValueOrDefault(AlertCategory.Idle, 0);
                    int breakCount = categoryCounts.GetValueOrDefault(AlertCategory.CrashRisk, 0);
                    int bleedCount = categoryCounts.GetValueOrDefault(AlertCategory.Bleeding, 0);
                    int fleeCount = categoryCounts.GetValueOrDefault(AlertCategory.Fleeing, 0);
                    int defenseCount = categoryCounts.GetValueOrDefault(AlertCategory.NoDefense, 0);
                    int bedCount = categoryCounts.GetValueOrDefault(AlertCategory.BedShortage, 0);
                    int foodAlertCount = categoryCounts.GetValueOrDefault(AlertCategory.FoodShortage, 0);
                    int generalCount = categoryCounts.GetValueOrDefault(AlertCategory.General, 0);

                    // 计算变化哈希
                    int currentHash = ComputeCategoryHash(categoryCounts);
                    if (foodDays < 3) currentHash ^= (foodDays * 997);

                    bool anythingWrong = idleCount > 0 || breakCount > 0 || bleedCount > 0
                        || fleeCount > 0 || foodDays < 3 || defenseCount > 0 || bedCount > 0
                        || foodAlertCount > 0 || generalCount > 0;

                    if (!anythingWrong)
                    {
                        _lastCategoryHash = -1;
                        sb.AppendLine($"一切正常 —— {cCount} 名殖民者，食物够 {foodDays} 天。");
                        sb.AppendLine("建议等待几秒后再次调用本工具检查。");
                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }

                    bool hasChanged = currentHash != _lastCategoryHash;
                    _lastCategoryHash = currentHash;

                    if (!hasChanged)
                    {
                        sb.AppendLine($"状态不变: 空闲 {idleCount}, 崩溃风险 {breakCount}, 流血 {bleedCount}, 逃跑 {fleeCount}, 食物 {foodDays}天");
                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }

                    // === 以下仅在首次出现或状态变化时详细报告 ===

                    sb.AppendLine("## ⚠ 殖民地提醒");
                    sb.AppendLine();

                    // 按类别分组输出原生警报
                    var grouped = nativeAlerts
                        .GroupBy(a => NativeAlertHelper.ClassifyAlert(a))
                        .OrderBy(g => g.Key);

                    foreach (var group in grouped)
                    {
                        string catLabel = group.Key switch
                        {
                            AlertCategory.Idle => "空闲殖民者",
                            AlertCategory.CrashRisk => "崩溃风险",
                            AlertCategory.Bleeding => "流血/需要治疗",
                            AlertCategory.Fleeing => "逃跑中",
                            AlertCategory.FoodShortage => "食物不足",
                            AlertCategory.NoDefense => "无防御",
                            AlertCategory.BedShortage => "缺床",
                            _ => "其他警报"
                        };
                        int count = group.Count();
                        sb.AppendLine($"### {catLabel} ({count})");

                        foreach (var alert in group)
                        {
                            string prio = alert.Priority switch { 2 => "CRITICAL", 1 => "HIGH", _ => "MEDIUM" };
                            sb.Append($"- [{prio}] {alert.Label}");
                            if (alert.Culprits.Length > 0)
                            {
                                var names = alert.Culprits.Where(n => n != null).Take(5);
                                sb.Append($": {string.Join(", ", names)}");
                            }
                            sb.AppendLine();
                        }
                        sb.AppendLine();
                    }

                    // 食物天数补充（原生 Alert 只告知不足，不告知天数）
                    if (foodDays < 3)
                    {
                        sb.AppendLine($"### ⚠ 食物不足: 仅 {foodDays} 天储备");
                        sb.AppendLine();
                    }

                    int totalIssues = idleCount + breakCount + bleedCount + fleeCount
                        + defenseCount + bedCount + foodAlertCount + generalCount
                        + (foodDays < 3 && foodAlertCount == 0 ? 1 : 0);
                    sb.AppendLine($"---\n上次检查无异常或状态变化，当前 {totalIssues} 项提醒。");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"警报检查失败: {ex.Message}");
                }
            });
        }

        private static int ComputeCategoryHash(Dictionary<AlertCategory, int> counts)
        {
            int hash = 0;
            foreach (var kv in counts)
            {
                if (kv.Value > 0)
                    hash ^= ((int)kv.Key * 397) ^ kv.Value;
            }
            return hash;
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
