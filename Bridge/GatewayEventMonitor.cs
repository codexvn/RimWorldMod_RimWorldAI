using System;
using System.Collections.Concurrent;
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

        /// <summary>近期通知（供 Tool 执行后附加到响应中）。</summary>
        public static readonly ConcurrentQueue<string> RecentNotifications = new();

        public static void Reset()
        {
            NotificationBus.Reset();
            while (RecentNotifications.TryDequeue(out _)) { }
        }

        public static void Tick()
        {
            if (!GatewayClient.IsConnected) return;
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

            // 格式化通知文本
            var notifyLines = new List<string>();
            foreach (var n in notifications)
            {
                switch (n.Type)
                {
                    case NotificationType.Letter:
                        var letterSb = new StringBuilder();
                        letterSb.Append($"[{n.DangerLabel}] {n.Label}");
                        if (!string.IsNullOrEmpty(n.Text))
                            letterSb.Append($" — {n.Text}");
                        notifyLines.Add(letterSb.ToString());
                        // 大威胁立即发送
                        if (n.DangerLabel == "大威胁")
                        {
                            var alertSb = new StringBuilder();
                            alertSb.AppendLine($"## [{n.DangerLabel}] {n.Label}");
                            if (!string.IsNullOrEmpty(n.Text))
                                alertSb.AppendLine(n.Text);
                            alertSb.Append(BuildColonySummary(map, colonists, colonistCount));
                            GatewayMessageQueue.SendNow(MessageCategory.RaidStart, alertSb.ToString().TrimEnd());
                            hasNotifications = true;
                        }
                        break;
                    case NotificationType.Message:
                        notifyLines.Add($"[{n.DangerLabel}] {n.Text}");
                        RecentNotifications.Enqueue($"[{n.DangerLabel}] {n.Text}");
                        break;
                    case NotificationType.AlertStart:
                        var culprits = n.Culprits != null && n.Culprits.Count > 0
                            ? $": {string.Join(", ", n.Culprits.Take(5))}" : "";
                        notifyLines.Add($"[{n.PriorityLabel}] {n.Label}{culprits}");
                        break;
                    case NotificationType.AlertEnd:
                        notifyLines.Add($"   [{n.Label} 已解除]");
                        break;
                }
            }

            // === 2. 殖民者数量变化 ===
            bool countChanged = colonistCount != _lastColonistCount && _lastColonistCount >= 0;
            if (countChanged)
            {
                int diff = colonistCount - _lastColonistCount;
                notifyLines.Add($"殖民者数量: {_lastColonistCount} → {colonistCount} ({(diff > 0 ? "+" : "")}{diff})");
                hasNotifications = true;
            }
            _lastColonistCount = colonistCount;

            // === 3. 推送综合警报 ===
            if (hasNotifications)
            {
                var sb = new StringBuilder();
                sb.AppendLine("## ⚠ 殖民地警报");
                foreach (var line in notifyLines)
                    sb.AppendLine($"- {line}");
                sb.Append(BuildColonySummary(map, colonists, colonistCount));
                GatewayMessageQueue.Enqueue(MessageCategory.Alert, sb.ToString().TrimEnd());
            }

            // === 4. 早报（游戏时间每天早上 6 点） ===
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

        private static int GetCountByDefName(Map map, string defName)
        {
            var resources = map.resourceCounter?.AllCountedAmounts;
            if (resources == null) return 0;
            foreach (var kv in resources)
                if (kv.Key.defName == defName)
                    return kv.Value;
            return 0;
        }
    }
}
