using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;
using RimWorld;
using RimWorldMCP.Helpers;

namespace RimWorldMCP
{
    /// <summary>游戏上下文构建工具——生成殖民地状态摘要文本</summary>
    public static class GameContextProvider
    {
        public static string BuildPauseStatus()
        {
            var tm = Find.TickManager;
            if (tm == null) return "游戏速度未知";
            if (!tm.Paused) return "游戏运行中";

            var sb = new StringBuilder();
            sb.Append("游戏已暂停");
            if (tm.ForcePaused)
            {
                var reasons = new List<string>();
                var ws = Find.WindowStack;
                if (ws != null)
                {
                    for (int i = 0; i < ws.Count; i++)
                    {
                        var w = ws[i];
                        if (w.forcePause) reasons.Add($"窗口\"{w.GetType().Name}\"锁定");
                    }
                }
                if (LongEventHandler.ForcePause) reasons.Add("长事件处理中");
                if (Find.TilePicker?.Active == true) reasons.Add("地块选择器激活");
                if (reasons.Count > 0)
                    sb.Append($"（强制暂停: {string.Join("; ", reasons)}）");
                else sb.Append("（强制暂停）");
            }
            else sb.Append("（手动暂停）");
            return sb.ToString();
        }

        public static string BuildGameContext()
        {
            var sb = new StringBuilder();
            var map = Find.CurrentMap;
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            var tickManager = Find.TickManager;
            int ticksAbs = tickManager?.TicksAbs ?? 0;
            int ticksGame = tickManager?.TicksGame ?? 0;
            int day = ticksGame / 60000;

            sb.AppendLine("## 殖民地概况");
            sb.AppendLine($"- 地图: {map?.Tile ?? -1} | 大小: {map?.Size.x ?? 0}x{map?.Size.z ?? 0} | 时间: 第{day / 15 + 1}年 第{day % 15 + 1}天");
            sb.AppendLine($"- 总 Tick: {ticksAbs} | 游戏 Tick: {ticksGame}");

            int freeColonists = colonists.Count;
            var prisoners = PawnsFinder.AllMaps_PrisonersOfColony;
            sb.AppendLine($"- 自由殖民者: {freeColonists}人 | 囚犯: {prisoners.Count}人");

            var animals = PawnsFinder.AllMaps_Spawned
                .Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Animal).ToList();
            if (animals.Count > 0)
            {
                var animalGroups = animals.GroupBy(a => a.def.label).Select(g => $"{g.Key} x{g.Count()}");
                sb.AppendLine($"- 动物: {string.Join(", ", animalGroups)}");
            }

            sb.AppendLine();
            sb.AppendLine("## 资源库存概要");
            if (map != null)
            {
                var resources = map.resourceCounter?.AllCountedAmounts;
                if (resources != null)
                {
                    var keyDefs = new[] { "Steel", "WoodLog", "Plasteel", "ComponentIndustrial",
                        "ComponentSpacer", "Silver", "Gold", "Uranium", "Chemfuel" };
                    foreach (var defName in keyDefs)
                        foreach (var kv in resources)
                            if (kv.Key.defName == defName && kv.Value > 0)
                            { sb.AppendLine($"- {kv.Key.label}: {kv.Value}"); break; }

                    var foodTotal = resources.Where(kv =>
                        kv.Key.IsNutritionGivingIngestible || kv.Key.ingestible?.foodType != null).Sum(kv => kv.Value);
                    if (foodTotal > 0) sb.AppendLine($"- 食物总计: {foodTotal}份");

                    float totalFoodNutrition = 0f;
                    foreach (var kvp in resources)
                    {
                        var def = kvp.Key;
                        if (def.IsNutritionGivingIngestible && def.ingestible?.HumanEdible == true
                            && def.ingestible?.foodType != FoodTypeFlags.Tree)
                            totalFoodNutrition += kvp.Value * (def.ingestible?.CachedNutrition ?? 0f);
                    }
                    int colonistCount = colonists.Count;
                    if (colonistCount > 0 && totalFoodNutrition > 0)
                    {
                        float dailyNeed = colonistCount * 1.6f;
                        sb.AppendLine($"- 食物储备: 约 {(int)(totalFoodNutrition / dailyNeed)} 天");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 电力");
            if (map?.powerNetManager?.AllNetsListForReading != null)
            {
                float totalGenerated = 0f, totalUsed = 0f, totalStored = 0f, totalStoredMax = 0f;
                foreach (var net in map.powerNetManager.AllNetsListForReading)
                {
                    foreach (var comp in net.powerComps)
                        if (comp.PowerOutput > 0) totalGenerated += comp.PowerOutput;
                        else if (comp.PowerOutput < 0) totalUsed += -comp.PowerOutput;
                    foreach (var batt in net.batteryComps)
                    { totalStored += batt.StoredEnergy; totalStoredMax += batt.Props.storedEnergyMax; }
                }
                sb.AppendLine($"- 发电: {totalGenerated / 1000f:F1} kW | 用电: {totalUsed / 1000f:F1} kW");
                if (totalStoredMax > 0)
                    sb.AppendLine($"- 储电: {totalStored / 1000f:F1} / {totalStoredMax / 1000f:F1} kWd ({totalStored / totalStoredMax * 100f:F0}%)");
                sb.AppendLine($"- 平衡: {(totalGenerated >= totalUsed ? "盈余" : "赤字")} {Math.Abs(totalGenerated - totalUsed) / 1000f:F1} kW");
            }
            else sb.AppendLine("- 无电网数据");

            sb.AppendLine();
            sb.AppendLine("## 研究进度");
            var rm = Find.ResearchManager;
            if (rm != null)
            {
                var curProj = rm.GetProject();
                if (curProj != null) sb.AppendLine($"- 当前: {curProj.label} ({(int)(rm.GetProgress(curProj) * 100f)}%)");
                else sb.AppendLine("- 当前: 无");
                try
                {
                    var allProjects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                    int completedCount = allProjects.Count(p => p.IsFinished);
                    sb.AppendLine($"- 已完成: {completedCount}项 / {allProjects.Count}项");
                }
                catch { }
            }

            sb.AppendLine();
            sb.AppendLine("## 威胁与财富");
            if (map != null)
            {
                try { sb.AppendLine($"- 殖民地财富: {map.wealthWatcher?.WealthTotal ?? 0f:N0}"); } catch { }
                try
                {
                    var st = Find.Storyteller;
                    if (st != null)
                    {
                        sb.AppendLine($"- 威胁倍率: {Find.StoryWatcher?.watcherAdaptation?.TotalThreatPointsFactor ?? 0f:F2}");
                        sb.AppendLine($"- 难度: {st.difficultyDef?.label ?? "?"} | 叙事者: {st.def?.label ?? "?"}");
                    }
                }
                catch { }
            }

            sb.AppendLine();
            sb.AppendLine("## 天气与环境");
            if (map != null)
            {
                try
                {
                    sb.AppendLine($"- 室外温度: {map.mapTemperature.OutdoorTemp:F1}°C");
                    var weather = map.weatherManager?.curWeather;
                    if (weather != null)
                    {
                        sb.AppendLine($"- 天气: {weather.label}");
                        if (weather.rainRate > 0) sb.AppendLine($"- 降雨: {weather.rainRate * 100f:F0}%");
                        if (weather.snowRate > 0) sb.AppendLine($"- 降雪: {weather.snowRate * 100f:F0}%");
                    }
                    var season = GenLocalDate.Season(map);
                    if (season != Season.Undefined)
                    {
                        string sl = season switch { Season.Spring => "春", Season.Summer => "夏",
                            Season.Fall => "秋", Season.Winter => "冬", _ => season.ToString() };
                        sb.AppendLine($"- 季节: {sl}");
                    }
                }
                catch { }
            }

            sb.AppendLine();
            sb.AppendLine("## 活跃警报");
            try
            {
                var alerts = NativeAlertHelper.GetActiveAlerts();
                if (alerts.Count == 0) sb.AppendLine("- 无");
                else
                    foreach (var a in alerts.OrderByDescending(a => a.Priority))
                    {
                        string prio = a.Priority switch { 2 => "!!", 1 => "! ", _ => "  " };
                        sb.AppendLine($"- [{prio}] {a.Label}");
                    }
            }
            catch { }

            sb.AppendLine();
            sb.AppendLine("## 当前工作单");
            if (map != null)
            {
                try
                {
                    var tables = map.listerBuildings?.AllBuildingsColonistOfClass<Building_WorkTable>()
                        ?? Enumerable.Empty<Building_WorkTable>();
                    bool hasBills = false;
                    foreach (var table in tables)
                    {
                        var bills = table.billStack?.Bills;
                        if (bills != null && bills.Count > 0)
                        {
                            hasBills = true;
                            sb.AppendLine($"### {table.def.label}");
                            foreach (var bill in bills)
                            {
                                string status = bill.suspended ? "(暂停)" : "";
                                sb.AppendLine($"- {bill.Label} {status}");
                            }
                        }
                    }
                    if (!hasBills) sb.AppendLine("- 暂无");
                }
                catch { }
            }

            return sb.ToString().TrimEnd();
        }

        public static string BuildColonyOverview(Map map, List<Pawn> colonists, int colonistCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine(BuildPauseStatus());
            sb.AppendLine();
            sb.Append(BuildGameContext());
            return sb.ToString().TrimEnd();
        }
    }
}
