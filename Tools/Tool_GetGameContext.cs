using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorld.Planet;
using RimWorldMCP;
using RimWorldMCP.Helpers;

namespace RimWorldMCP.Tools
{
    public class Tool_GetGameContext : ITool
    {
        public string Name => "get_game_context";
        public string Description => "获取 RimWorld 当前游戏的完整状态上下文，包括殖民地概况、资源库存、研究进度、威胁信息、当前工作单等。应在执行任何操作前先调用此工具了解局势。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, required = Array.Empty<string>() });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var sb = new StringBuilder();

                // 殖民地概况
                var map = Find.CurrentMap;
                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                var tickManager = Find.TickManager;
                int ticksAbs = tickManager?.TicksAbs ?? 0;
                int ticksGame = tickManager?.TicksGame ?? 0;
                int day = ticksGame / 60000; // 1 day = 60000 ticks

                sb.AppendLine("## 游戏状态");
                sb.AppendLine($"- {GatewayEventMonitor.BuildPauseStatus()}");
                sb.AppendLine($"- 地图: {map?.Tile ?? -1} | 大小: {map?.Size.x ?? 0}x{map?.Size.z ?? 0} | 时间: 第{day / 15 + 1}年 第{day % 15 + 1}天");
                sb.AppendLine($"- 总 Tick: {ticksAbs} | 游戏 Tick: {ticksGame}");

                // 殖民者
                int freeColonists = colonists.Count;
                var prisoners = PawnsFinder.AllMaps_PrisonersOfColony;
                int prisonerCount = prisoners.Count;
                sb.AppendLine($"- 自由殖民者: {freeColonists}人 | 囚犯: {prisonerCount}人");

                // 动物
                var animals = PawnsFinder.AllMaps_Spawned.Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Animal).ToList();
                if (animals.Count > 0)
                {
                    var animalGroups = animals.GroupBy(a => a.def.label).Select(g => $"{g.Key} x{g.Count()}");
                    sb.AppendLine($"- 动物: {string.Join(", ", animalGroups)}");
                }

                // 资源库存概要
                sb.AppendLine();
                sb.AppendLine("## 资源库存概要");
                if (map != null)
                {
                    var resources = map.resourceCounter?.AllCountedAmounts;
                    if (resources != null)
                    {
                        // 关键资源摘要
                        var keyDefs = new[] { "Steel", "WoodLog", "Plasteel", "ComponentIndustrial", "ComponentSpacer",
                            "Silver", "Gold", "Uranium", "Chemfuel" };
                        foreach (var defName in keyDefs)
                        {
                            foreach (var kv in resources)
                            {
                                if (kv.Key.defName == defName && kv.Value > 0)
                                {
                                    sb.AppendLine($"- {kv.Key.label}: {kv.Value}");
                                    break;
                                }
                            }
                        }
                        var foodTotal = resources.Where(kv => kv.Key.IsNutritionGivingIngestible || kv.Key.ingestible?.foodType != null).Sum(kv => kv.Value);
                        if (foodTotal > 0) sb.AppendLine($"- 食物总计: {foodTotal}份");

                        // 食物储备天数估算
                        float totalFoodNutrition = 0f;
                        foreach (var kvp in resources)
                        {
                            var def = kvp.Key;
                            if (def.IsNutritionGivingIngestible && def.ingestible?.HumanEdible == true && def.ingestible?.foodType != FoodTypeFlags.Tree)
                                totalFoodNutrition += kvp.Value * (def.ingestible?.CachedNutrition ?? 0f);
                        }
                        int colonistCount = PawnsFinder.AllMaps_FreeColonistsSpawned?.Count ?? 0;
                        if (colonistCount > 0 && totalFoodNutrition > 0)
                        {
                            float dailyNeed = colonistCount * 1.6f; // pawns need ~1.6 nutrition per day
                            int daysWorth = (int)(totalFoodNutrition / dailyNeed);
                            sb.AppendLine($"- 食物储备: 约 {daysWorth} 天");
                        }
                    }
                }

                // 电力数据
                sb.AppendLine();
                sb.AppendLine("## 电力");
                if (map?.powerNetManager?.AllNetsListForReading != null)
                {
                    float totalGenerated = 0f, totalUsed = 0f, totalStored = 0f, totalStoredMax = 0f;
                    foreach (var net in map.powerNetManager.AllNetsListForReading)
                    {
                        foreach (var comp in net.powerComps)
                        {
                            if (comp.PowerOutput > 0) totalGenerated += comp.PowerOutput;
                            else if (comp.PowerOutput < 0) totalUsed += -comp.PowerOutput;
                        }
                        foreach (var batt in net.batteryComps)
                        {
                            totalStored += batt.StoredEnergy;
                            totalStoredMax += batt.Props.storedEnergyMax;
                        }
                    }
                    sb.AppendLine($"- 发电: {totalGenerated / 1000f:F1} kW");
                    sb.AppendLine($"- 用电: {totalUsed / 1000f:F1} kW");
                    if (totalStoredMax > 0)
                        sb.AppendLine($"- 储电: {totalStored / 1000f:F1} / {totalStoredMax / 1000f:F1} kWd ({totalStored / totalStoredMax * 100f:F0}%)");
                    sb.AppendLine($"- 电力平衡: {(totalGenerated - totalUsed >= 0 ? "盈余" : "赤字")} {Math.Abs(totalGenerated - totalUsed) / 1000f:F1} kW");
                }
                else
                {
                    sb.AppendLine("- 无电网数据");
                }

                // 研究进度
                sb.AppendLine();
                sb.AppendLine("## 研究进度");
                var researchManager = Find.ResearchManager;
                if (researchManager != null)
                {
                    var currentProj = researchManager.GetProject();
                    if (currentProj != null)
                    {
                        float progress = researchManager.GetProgress(currentProj);
                        var pct = (int)(progress * 100f);
                        sb.AppendLine($"- 当前: {currentProj.label} ({pct}%)");
                    }
                    else
                    {
                        sb.AppendLine("- 当前: 无");
                    }

                    try
                    {
                        var allProjects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                        int completedCount = allProjects.Count(p => p.IsFinished);
                        sb.AppendLine($"- 已完成: {completedCount}项 / {allProjects.Count}项");
                        sb.AppendLine($"- 完成率: {(int)(completedCount * 100f / allProjects.Count)}%");
                    }
                    catch (Exception) { /* ResearchProjectDef DB access may fail in certain contexts */ }
                }

                // 威胁信息
                sb.AppendLine();
                sb.AppendLine("## 威胁与财富");
                if (map != null)
                {
                    try
                    {
                        float wealth = map.wealthWatcher?.WealthTotal ?? 0f;
                        sb.AppendLine($"- 殖民地总财富: {wealth:N0}");
                    }
                    catch (Exception) { }
                    try
                    {
                        var storyteller = Find.Storyteller;
                        if (storyteller != null)
                        {
                            var adaptationFactor = Find.StoryWatcher?.watcherAdaptation?.TotalThreatPointsFactor ?? 0f;
                            sb.AppendLine($"- 威胁点数倍率: {adaptationFactor:F2}");
                            sb.AppendLine($"- 难度: {storyteller.difficultyDef?.label ?? "未知"}");
                            sb.AppendLine($"- 叙事者: {storyteller.def?.label ?? "未知"}");
                        }
                    }
                    catch (Exception) { }
                }

                // 天气与温度
                sb.AppendLine();
                sb.AppendLine("## 天气与环境");
                if (map != null)
                {
                    try
                    {
                        float outdoorTemp = map.mapTemperature.OutdoorTemp;
                        sb.AppendLine($"- 室外温度: {outdoorTemp:F1}°C");
                        var weather = map.weatherManager?.curWeather;
                        if (weather != null)
                        {
                            sb.AppendLine($"- 天气: {weather.label}");
                            if (weather.rainRate > 0) sb.AppendLine($"- 降雨: {weather.rainRate * 100f:F0}%");
                            if (weather.snowRate > 0) sb.AppendLine($"- 降雪: {weather.snowRate * 100f:F0}%");
                            if (weather.windSpeedFactor > 0.5f) sb.AppendLine($"- 风速: 高 ({weather.windSpeedFactor * 100f:F0}%)");
                        }
                        var season = GenLocalDate.Season(map);
                        if (season != Season.Undefined)
                        {
                            string seasonLabel = season switch
                            {
                                Season.Spring => "春天",
                                Season.Summer => "夏天",
                                Season.Fall => "秋天",
                                Season.Winter => "冬天",
                                _ => season.ToString()
                            };
                            sb.AppendLine($"- 季节: {seasonLabel}");
                        }
                    }
                    catch (Exception) { sb.AppendLine("- 无法读取天气数据"); }
                }

                // 活跃警报（复用原生 Alert 系统）
                sb.AppendLine();
                sb.AppendLine("## 活跃警报");
                try
                {
                    var activeAlerts = NativeAlertHelper.GetActiveAlerts();
                    if (activeAlerts.Count == 0)
                    {
                        sb.AppendLine("- 无活跃警报");
                    }
                    else
                    {
                        foreach (var a in activeAlerts.OrderByDescending(a => a.Priority))
                        {
                            string prio = a.Priority switch
                            {
                                2 => "!!",
                                1 => "! ",
                                _ => "  "
                            };
                            sb.AppendLine($"- [{prio}] {a.Label}");
                        }
                    }
                }
                catch (Exception) { sb.AppendLine("- 无法读取警报"); }

                // 当前工作单
                sb.AppendLine();
                sb.AppendLine("## 当前工作单");
                if (map != null)
                {
                    try
                    {
                        var tables = map.listerBuildings?.AllBuildingsColonistOfClass<Building_WorkTable>() ?? Enumerable.Empty<Building_WorkTable>();
                        foreach (var table in tables)
                        {
                            var bills = table.billStack?.Bills;
                            if (bills != null && bills.Count > 0)
                            {
                                sb.AppendLine($"### {table.def.label} ({table.Label})");
                                foreach (var bill in bills)
                                {
                                    string status = bill.suspended ? "(暂停)" : "(进行中)";
                                    var bp = bill as Bill_Production;
                                    string repeatInfo = "";
                                    if (bp != null)
                                    {
                                        if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount)
                                            repeatInfo = $" x{bp.repeatCount}";
                                        else if (bp.repeatMode == BillRepeatModeDefOf.TargetCount)
                                            repeatInfo = $" 保持{bp.targetCount}";
                                        else if (bp.repeatMode == BillRepeatModeDefOf.Forever)
                                            repeatInfo = " xForever";
                                    }
                                    sb.AppendLine($"- {bill.Label}{repeatInfo} {status}");
                                }
                            }
                        }
                        if (!tables.Any(t => t.billStack?.Bills?.Count > 0))
                            sb.AppendLine("- 暂无工作单");
                    }
                    catch (Exception) { sb.AppendLine("- 无法读取工作单"); }
                }

                return ToolResult.Success(sb.ToString());
            });
        }
    }
}
