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

        /// <summary>轻量殖民地概览（空闲兜底推送）</summary>
        internal static string BuildColonyOverview(Map map, List<Pawn> colonists, int colonistCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 殖民地概览");

            sb.AppendLine(BuildPauseStatus());
            sb.AppendLine();

            var weather = map.weatherManager?.curWeather;
            float temp = map.mapTemperature?.OutdoorTemp ?? 0f;
            sb.AppendLine($"天气: {weather?.label ?? "?"}, 室外 {temp:F0}°C");

            float avgMood = colonists.Count > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f
                : 0f;
            sb.AppendLine($"殖民者: {colonistCount} 人 | 平均心情 {avgMood:F0}%");

            sb.Append(BuildColonySummary(map, colonists, colonistCount));

            var nativeLines = NativeAlertHelper.BuildAlertLines(NativeAlertHelper.GetActiveAlerts());
            if (nativeLines.Count > 0)
            {
                sb.AppendLine("警报:");
                foreach (var a in nativeLines)
                    sb.AppendLine($"  - {a}");
            }

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
