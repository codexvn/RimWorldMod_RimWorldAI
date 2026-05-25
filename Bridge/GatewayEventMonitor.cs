using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;
using RimWorld;
using RimWorldMCP.Helpers;
using RimWorldMCP.Harmony;

namespace RimWorldMCP
{
    public static class GatewayEventMonitor
    {
        private static int _nextCheckTick;
        private const int CheckIntervalTicks = 120;
        private static int _lastColonistCount = -1;
        private const int IdleTimeoutMs = 120000; // 2 分钟真实时间
        private static int _lastDialogCount;
        private static string _lastDialogKey = "";

        public static void Reset()
        {
            NotificationBus.Reset();
            _lastDialogCount = 0;
            _lastDialogKey = "";
        }

        public static void Tick()
        {
            if (!GatewayClient.IsConnected) return;

            // === 每帧：高危通知即时处理 ===
            if (NotificationBus.HighDangerPending && GatewayClient.IsReady)
            {
                NotificationBus.HighDangerPending = false;
                var emergencyList = NotificationBus.Drain();
                if (emergencyList.Count > 0)
                {
                    var emMap = Find.CurrentMap;
                    if (emMap != null)
                    {
                        var emColonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        var emLines = new List<string>();
                        bool hasEmergency = false;

                        foreach (var n in emergencyList)
                        {
                            if (NotificationBus.IsHighDanger(n.Type, n.DangerLabel, n.Priority))
                            {
                                PushEmergency(n, emMap, emColonists, emColonists.Count);
                                hasEmergency = true;
                            }
                            else
                            {
                                AddNotifyLine(n, emLines);
                            }
                        }

                        if (hasEmergency && emLines.Count > 0)
                        {
                            var sb = new StringBuilder("插入一些通知：\n");
                            foreach (var line in emLines)
                                sb.AppendLine($"- {line}");
                            sb.Append("现在继续处理。");
                            GatewayMessageQueue.Enqueue(MessageCategory.Alert, sb.ToString());
                        }
                    }
                }
            }

            // === 120 tick 定时：普通通知 + 殖民者 + 早报 ===
            var tick = Find.TickManager?.TicksGame ?? 0;
            if (tick < _nextCheckTick) return;
            _nextCheckTick = tick + CheckIntervalTicks;

            var map = Find.CurrentMap;
            if (map == null) return;

            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            int colonistCount = colonists.Count;

            // === 1. 收集 Harmony Patch 拦截的通知 ===
            var notifications = NotificationBus.Drain();
            bool hasNotifications = notifications.Count > 0;

            // 格式化通知文本 + 高危事件即时推送
            var notifyLines = new List<string>();
            foreach (var n in notifications)
            {
                if (NotificationBus.IsHighDanger(n.Type, n.DangerLabel, n.Priority))
                {
                    PushEmergency(n, map, colonists, colonistCount);
                    hasNotifications = true;
                }
                AddNotifyLine(n, notifyLines);
            }

            // === 2. 殖民者数量变化 ===
            bool countChanged = colonistCount != _lastColonistCount && _lastColonistCount >= 0;
            if (countChanged)
            {
                int diff = colonistCount - _lastColonistCount;
                notifyLines.Add($"殖民者 {_lastColonistCount}→{colonistCount} ({(diff > 0 ? "+" : "")}{diff})");
                hasNotifications = true;
            }
            _lastColonistCount = colonistCount;

            // === 3. 推送综合警报 ===
            if (hasNotifications)
            {
                var sb = new StringBuilder("插入一些通知：\n");
                foreach (var line in notifyLines)
                    sb.AppendLine($"- {line}");
                sb.Append("现在继续处理。");
                GatewayMessageQueue.Enqueue(MessageCategory.Alert, sb.ToString());
            }

            // === 4. 弹框检测：检测 FloatMenu / Dialog 出现 ===
            if (GatewayClient.IsReady)
            {
                var dialogs = RimWorldMCP.Helpers.DialogHelper.GetInteractableDialogs();
                if (dialogs.Count > 0 || _lastDialogCount > 0)
                {
                    int dialogCount = dialogs.Count;
                    string dialogKey = "";
                    foreach (var w in dialogs)
                    {
                        if (w is FloatMenu)
                        {
                            var options = RimWorldMCP.Helpers.DialogHelper.FloatMenuOptionsField?.GetValue(w) as List<FloatMenuOption>;
                            if (options != null)
                            {
                                dialogKey = "fm:" + string.Join("|", options.Take(10).Select(o => o.Label).OrderBy(s => s));
                            }
                        }
                        else
                        {
                            dialogKey += w.GetType().Name;
                        }
                    }

                    if (dialogCount > 0 && (dialogCount != _lastDialogCount || dialogKey != _lastDialogKey))
                    {
                        _lastDialogCount = dialogCount;
                        _lastDialogKey = dialogKey;

                        var dsb = new StringBuilder();
                        dsb.AppendLine("## 弹框提示");
                        dsb.AppendLine($"当前有 {dialogCount} 个弹框需要选择。");
                        dsb.AppendLine("使用 get_open_dialogs 查看选项，select_dialog_option 选择。");
                        dsb.Append(BuildColonySummary(map, colonists, colonistCount));
                        GatewayMessageQueue.Enqueue(MessageCategory.DialogPrompt, dsb.ToString().TrimEnd());
                    }
                    else if (dialogCount == 0 && _lastDialogCount > 0)
                    {
                        _lastDialogCount = 0;
                        _lastDialogKey = "";
                    }
                }
            }

            // === 5. 空闲兜底：长时间无交互时推送概览（真实时间，跳过活跃会话）
            if (GatewayMessageQueue.LastSendRealMs > 0
                && Environment.TickCount - GatewayMessageQueue.LastSendRealMs > IdleTimeoutMs
                && !ChatDisplayState.IsBusy)
            {
                var overview = BuildColonyOverview(map, colonists, colonistCount);
                GatewayMessageQueue.Enqueue(MessageCategory.Alert, overview);
            }

            // === 5. 早报（游戏时间每天早上 6 点） ===
            int hour = GenLocalDate.HourOfDay(map);
            int day = tick / 60000;
            if (hour == 6 && !GatewayMessageQueue.WasDailySentToday(day))
            {
                GatewayMessageQueue.MarkDailySent(day);
                var msg = BuildDailyOverview(map, colonists, colonistCount, tick);
                GatewayMessageQueue.Enqueue(MessageCategory.DailyMorning, msg);
            }
        }

        // ========== 消息构建 ==========

        private static string BuildDailyOverview(Map map, List<Pawn> colonists, int colonistCount, int ticksGame)
        {
            var sb = new StringBuilder();
            int day = ticksGame / 60000;
            int year = day / 15 + 1;
            int dayOfYear = day % 15 + 1;

            var season = GenLocalDate.Season(map);
            string seasonStr = season switch
            {
                Season.Spring => "春", Season.Summer => "夏",
                Season.Fall => "秋", Season.Winter => "冬", _ => "?"
            };
            sb.AppendLine($"## 每早汇报 第{year}年 {seasonStr}季 第{dayOfYear}天");

            // 暂停状态
            sb.AppendLine(BuildPauseStatus());

            // 天气
            var weather = map.weatherManager?.curWeather;
            float temp = map.mapTemperature?.OutdoorTemp ?? 0f;
            sb.AppendLine($"天气: {weather?.label ?? "?"}, 室外 {temp:F0}°C");

            // 殖民者
            float avgMood = colonists.Count > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f
                : 0f;
            sb.AppendLine($"殖民者: {colonistCount} 人 | 平均心情 {avgMood:F0}%");

            // 资源
            int steel = GetCountByDefName(map, "Steel");
            int wood = GetCountByDefName(map, "WoodLog");
            int components = GetCountByDefName(map, "ComponentIndustrial");
            int silver = GetCountByDefName(map, "Silver");
            int foodDays = NativeAlertHelper.CalcFoodDays(map, colonistCount);
            sb.AppendLine($"资源: 钢{steel} 木{wood} 零件{components} 银{silver} | 食物约{foodDays}天");

            // 电力
            float generated = 0, used = 0, stored = 0;
            foreach (var net in map.powerNetManager?.AllNetsListForReading ?? new List<PowerNet>())
            {
                foreach (var comp in net.powerComps)
                {
                    if (!comp.PowerOn) continue;
                    float rate = comp.EnergyOutputPerTick;
                    if (rate > 0) generated += rate; else used += -rate;
                }
                stored += net.CurrentStoredEnergy();
            }
            string powerLabel = generated >= used ? "盈余" : "赤字";
            sb.AppendLine($"电力: 发{generated / 1000f:F0}kW 用{used / 1000f:F0}kW 储{stored / 1000f:F0}kWd ({powerLabel})");

            // 研究
            var rm = Find.ResearchManager;
            var curProj = rm?.GetProject();
            if (curProj != null)
                sb.AppendLine($"研究: {curProj.label} ({rm!.GetProgress(curProj) * 100f:F0}%)");
            else
                sb.AppendLine("研究: 无");

            // 财富
            float wealth = map.wealthWatcher?.WealthTotal ?? 0f;
            sb.AppendLine($"财富: {wealth:N0}");

            // 警报（复用原生 Alert 系统）
            var nativeLines = NativeAlertHelper.BuildAlertLines(NativeAlertHelper.GetActiveAlerts());
            if (nativeLines.Count > 0)
            {
                sb.AppendLine("警报:");
                foreach (var a in nativeLines)
                    sb.AppendLine($"  - {a}");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>构建完整游戏上下文（与 get_game_context 工具共用）</summary>
        internal static string BuildGameContext()
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
            int prisonerCount = prisoners.Count;
            sb.AppendLine($"- 自由殖民者: {freeColonists}人 | 囚犯: {prisonerCount}人");

            var animals = PawnsFinder.AllMaps_Spawned.Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Animal).ToList();
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
                        float dailyNeed = colonistCount * 1.6f;
                        int daysWorth = (int)(totalFoodNutrition / dailyNeed);
                        sb.AppendLine($"- 食物储备: 约 {daysWorth} 天");
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
            else { sb.AppendLine("- 无电网数据"); }

            sb.AppendLine();
            sb.AppendLine("## 研究进度");
            var researchManager = Find.ResearchManager;
            if (researchManager != null)
            {
                var currentProj = researchManager.GetProject();
                if (currentProj != null)
                {
                    float progress = researchManager.GetProgress(currentProj);
                    sb.AppendLine($"- 当前: {currentProj.label} ({(int)(progress * 100f)}%)");
                }
                else sb.AppendLine("- 当前: 无");
                try
                {
                    var allProjects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                    int completedCount = allProjects.Count(p => p.IsFinished);
                    sb.AppendLine($"- 已完成: {completedCount}项 / {allProjects.Count}项");
                    sb.AppendLine($"- 完成率: {(int)(completedCount * 100f / allProjects.Count)}%");
                }
                catch (Exception) { }
            }

            sb.AppendLine();
            sb.AppendLine("## 威胁与财富");
            if (map != null)
            {
                try { sb.AppendLine($"- 殖民地总财富: {map.wealthWatcher?.WealthTotal ?? 0f:N0}"); }
                catch (Exception) { }
                try
                {
                    var storyteller = Find.Storyteller;
                    if (storyteller != null)
                    {
                        sb.AppendLine($"- 威胁点数倍率: {Find.StoryWatcher?.watcherAdaptation?.TotalThreatPointsFactor ?? 0f:F2}");
                        sb.AppendLine($"- 难度: {storyteller.difficultyDef?.label ?? "未知"}");
                        sb.AppendLine($"- 叙事者: {storyteller.def?.label ?? "未知"}");
                    }
                }
                catch (Exception) { }
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
                        if (weather.windSpeedFactor > 0.5f) sb.AppendLine($"- 风速: 高 ({weather.windSpeedFactor * 100f:F0}%)");
                    }
                    var season = GenLocalDate.Season(map);
                    if (season != Season.Undefined)
                    {
                        string seasonLabel = season switch
                        {
                            Season.Spring => "春天", Season.Summer => "夏天",
                            Season.Fall => "秋天", Season.Winter => "冬天",
                            _ => season.ToString()
                        };
                        sb.AppendLine($"- 季节: {seasonLabel}");
                    }
                }
                catch (Exception) { sb.AppendLine("- 无法读取天气数据"); }
            }

            sb.AppendLine();
            sb.AppendLine("## 活跃警报");
            try
            {
                var activeAlerts = NativeAlertHelper.GetActiveAlerts();
                if (activeAlerts.Count == 0) sb.AppendLine("- 无活跃警报");
                else
                {
                    foreach (var a in activeAlerts.OrderByDescending(a => a.Priority))
                    {
                        string prio = a.Priority switch { 2 => "!!", 1 => "! ", _ => "  " };
                        sb.AppendLine($"- [{prio}] {a.Label}");
                    }
                }
            }
            catch (Exception) { sb.AppendLine("- 无法读取警报"); }

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
                                    if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount) repeatInfo = $" x{bp.repeatCount}";
                                    else if (bp.repeatMode == BillRepeatModeDefOf.TargetCount) repeatInfo = $" 保持{bp.targetCount}";
                                    else if (bp.repeatMode == BillRepeatModeDefOf.Forever) repeatInfo = " xForever";
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

            return sb.ToString().TrimEnd();
        }

        /// <summary>殖民地概览（空闲兜底推送，复用 BuildGameContext）</summary>
        internal static string BuildColonyOverview(Map map, List<Pawn> colonists, int colonistCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine(BuildPauseStatus());
            sb.AppendLine();
            sb.Append(BuildGameContext());
            return sb.ToString().TrimEnd();
        }

        /// <summary>殖民地概要（附加在消息末尾）</summary>
        private static string BuildColonySummary(Map map, List<Pawn> colonists, int colonistCount)
        {
            var sb = new StringBuilder();
            int foodDays = NativeAlertHelper.CalcFoodDays(map, colonistCount);
            float avgMood = colonists.Count > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f
                : 0f;

            sb.AppendLine($"---");
            sb.AppendLine($"殖民者: {colonistCount}人 | 心情: {avgMood:F0}% | 食物: {foodDays}天");

            int steel = GetCountByDefName(map, "Steel");
            int components = GetCountByDefName(map, "ComponentIndustrial");
            sb.AppendLine($"钢{steel} | 零件{components}");

            return sb.ToString();
        }

        // ========== 工具方法 ==========

        /// <summary>高危通知即时推送</summary>
        private static void PushEmergency(Notification n, Map map, List<Pawn> colonists, int count)
        {
            string label, detail;
            switch (n.Type)
            {
                case NotificationType.Letter:
                    label = $"[{n.DangerLabel}]{n.Label}";
                    detail = n.Text ?? "";
                    break;
                case NotificationType.Message:
                    label = $"[{n.DangerLabel}]消息";
                    detail = n.Text ?? "";
                    break;
                case NotificationType.AlertStart:
                    label = n.Label;
                    detail = n.Culprits != null && n.Culprits.Count > 0
                        ? string.Join(", ", n.Culprits.Take(5)) : "";
                    break;
                default:
                    return;
            }

            var text = !string.IsNullOrEmpty(detail)
                ? $"插入一些通知：{label}（{detail}）\n现在继续处理。"
                : $"插入一些通知：{label}\n现在继续处理。";
            GatewayMessageQueue.SendNow(MessageCategory.RaidStart, text);
        }

        /// <summary>通知格式化为告警行</summary>
        private static void AddNotifyLine(Notification n, List<string> lines)
        {
            switch (n.Type)
            {
                case NotificationType.Letter:
                    var letterLine = new StringBuilder();
                    letterLine.Append($"[{n.DangerLabel}] {n.Label}");
                    if (!string.IsNullOrEmpty(n.Text))
                        letterLine.Append($" — {n.Text}");
                    lines.Add(letterLine.ToString());
                    break;
                case NotificationType.Message:
                    lines.Add($"[{n.DangerLabel}] {n.Text}");
                    break;
                case NotificationType.AlertStart:
                    var culprits = n.Culprits != null && n.Culprits.Count > 0
                        ? $": {string.Join(", ", n.Culprits.Take(5))}" : "";
                    lines.Add($"[{n.PriorityLabel}] {n.Label}{culprits}");
                    break;
                case NotificationType.AlertEnd:
                    lines.Add($"   [{n.Label} 已解除]");
                    break;
            }
        }

        private static int GetCountByDefName(Map map, string defName)
        {
            var resources = map.resourceCounter?.AllCountedAmounts;
            if (resources == null) return 0;
            foreach (var kv in resources)
                if (kv.Key.defName == defName)
                    return kv.Value;
            return 0;
        }

        /// <summary>构建暂停状态描述（含暂停原因）</summary>
        internal static string BuildPauseStatus()
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
                        if (w.forcePause)
                            reasons.Add($"窗口\"{w.GetType().Name}\"锁定");
                    }
                }
                if (LongEventHandler.ForcePause) reasons.Add("长事件处理中");
                if (Find.TilePicker?.Active == true) reasons.Add("地块选择器激活");

                if (reasons.Count > 0)
                    sb.Append($"（强制暂停: {string.Join("; ", reasons)}）");
                else
                    sb.Append("（强制暂停）");
            }
            else
            {
                sb.Append("（手动暂停）");
            }

            return sb.ToString();
        }
    }
}
