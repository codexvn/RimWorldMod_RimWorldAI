using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using Verse;

namespace RimWorldAgent
{
    /// <summary>事件转发器 — 旧 BridgeLifecycle 核心逻辑：L3 暂停/晨报/弹框检测/空闲兜底/暂停提醒</summary>
    public static class EventForwarder
    {
        private static int _nextCCEventTick;
        private const int CCEventCheckInterval = 120;
        private static int _nextCCFallbackMs;
        private const int CCFallbackIntervalMs = 5000;

        private static int _lastSendRealMs;
        private const int IdleOverviewIntervalMs = 120000;
        private static int _dailyReportDay = -1;
        private static int _lastColonistCount = -1;
        private static int _lastNoColonistsSendMs;
        private const int NoColonistsResendMs = 60000;
        private static int _lastDialogCount;
        private static string _lastDialogKey = "";

        private static readonly List<string> _dailyEventLog = new();
        private const int MaxDailyEventLog = 100;

        private static int _pauseStartRealMs;
        private static int _lastPauseRemindMs;
        private const int PauseRemindFirstMs = 30000;
        private const int PauseRemindRepeatMs = 60000;

        // L1+L2 非高危通知计数
        private static int _pendingLevel12Count;

        // 依赖注入
        private static CcbWebSocket? _ccbWs;

        public static bool DangerPaused { get; private set; }
        public static string DangerSummary { get; private set; } = "";
        public static int PendingLevel12Count => _pendingLevel12Count;
        private static bool _dangerShouldResume;
        private static TimeSpeed _savedSpeed;

        public static void ResetPendingLevel12Count() => _pendingLevel12Count = 0;

        /// <summary>设置 WebSocket 客户端引用</summary>
        public static void SetCcbSocket(CcbWebSocket? ws) => _ccbWs = ws;

        /// <summary>首次游戏连接时调用，触发 Agent 开始工作</summary>
        public static void SendGameConnected()
        {
            if (_ccbWs == null || !_ccbWs.IsReady) return;
            SendCCMessage("GameConnected", "游戏已连接，MCP 通信就绪。请用 get_skills 查看可用技能，用 get_game_context 获取当前状态，开始管理殖民地。");
        }

        /// <summary>每游戏 Tick 调用（由 GameComponent 驱动）</summary>
        public static void Tick()
        {
            if (_ccbWs == null || !_ccbWs.IsReady) return;
            var map = Find.CurrentMap;
            if (map == null) return;

            AutoPauseGuard();
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            int colonistCount = colonists.Count;
            int nowMs = Environment.TickCount;

            // === 第1层：紧急事件 ===
            var settings = RimWorldAgentMod.Instance?.Settings;
            if (settings == null) return;

            var tick = Find.TickManager?.TicksGame ?? 0;
            bool tickElapsed = tick >= _nextCCEventTick;
            if (tickElapsed) _nextCCEventTick = tick + CCEventCheckInterval;
            bool fallbackElapsed = unchecked((uint)(nowMs - _nextCCFallbackMs) >= CCFallbackIntervalMs);
            if (fallbackElapsed) _nextCCFallbackMs = nowMs;

            if (tickElapsed || fallbackElapsed)
            {
                // 殖民者数量变化
                bool countChanged = colonistCount != _lastColonistCount && _lastColonistCount >= 0;
                if (countChanged)
                {
                    int diff = colonistCount - _lastColonistCount;
                    AddDailyEvent($"殖民者 {_lastColonistCount}→{colonistCount} ({(diff > 0 ? "+" : "")}{diff})");
                }
                _lastColonistCount = colonistCount;

                // 殖民者全灭
                if (colonistCount == 0 && _lastColonistCount >= 0)
                {
                    bool firstTime = _lastNoColonistsSendMs == 0;
                    bool cooldownElapsed = unchecked((uint)(nowMs - _lastNoColonistsSendMs) >= NoColonistsResendMs);
                    if (firstTime || cooldownElapsed)
                    {
                        _lastNoColonistsSendMs = nowMs;
                        SendCCMessage("NoColonists", "所有殖民者已死亡，殖民地覆灭。请调用 `regenerate_map` 重开游戏（需 `i_know_danger=true`）。");
                    }
                }
                else _lastNoColonistsSendMs = 0;

                // 弹框检测
                CheckDialogs(nowMs, map, colonistCount);

                // 每日早报（6 点）
                int day = tick / 60000;
                int hour = GenLocalDate.HourOfDay(map);
                if (hour == 6 && _dailyReportDay != day)
                {
                    _dailyReportDay = day;
                    if (Find.TickManager != null && !Find.TickManager.Paused)
                        Find.TickManager.TogglePaused();
                    var dailyText = BuildDailyBriefing(map, colonists, colonistCount);
                    SendCCMessage("DailyMorning", dailyText);
                    _dailyEventLog.Clear();
                }
            }

            // === 第3层：空闲兜底 ===
            if (_lastSendRealMs > 0
                && unchecked((uint)(nowMs - _lastSendRealMs) >= IdleOverviewIntervalMs))
            {
                var overview = BuildColonyOverview(map, colonists, colonistCount);
                SendCCMessage("IdleDetected", overview);
            }

            // === 第4层：暂停过久提醒 ===
            var paused = Find.TickManager?.Paused ?? false;
            if (paused)
            {
                if (_pauseStartRealMs == 0) _pauseStartRealMs = nowMs;
                if (_lastPauseRemindMs == 0
                    && unchecked((uint)(nowMs - _pauseStartRealMs) >= PauseRemindFirstMs))
                {
                    _lastPauseRemindMs = nowMs;
                    SendCCMessage("PauseRemind", $"游戏已暂停 {(nowMs - _pauseStartRealMs) / 1000} 秒，请检查是否需要继续。");
                }
                else if (_lastPauseRemindMs > 0
                    && unchecked((uint)(nowMs - _lastPauseRemindMs) >= PauseRemindRepeatMs))
                {
                    _lastPauseRemindMs = nowMs;
                    SendCCMessage("PauseRemind", $"游戏仍在暂停中 (共 {(nowMs - _pauseStartRealMs) / 1000} 秒)，请用 toggle_pause 恢复。");
                }
            }
            else { _pauseStartRealMs = 0; _lastPauseRemindMs = 0; }
        }

        private static void AutoPauseGuard()
        {
            if (!DangerPaused) return;
            DangerPaused = false;
            DangerSummary = "";
            if (_dangerShouldResume && Find.TickManager != null)
            {
                Find.TickManager.CurTimeSpeed = _savedSpeed;
                _dangerShouldResume = false;
            }
        }

        private static void SendCCMessage(string category, string text)
        {
            // Token 预算检查
            var settings = RimWorldAgentMod.Instance?.Settings;
            if (settings != null && settings.TokenBudgetLimit > 0)
            {
                var status = TokenUsageTracker.CheckBudget(settings.TokenBudgetLimit);
                if (status == BudgetStatus.Exceeded)
                {
                    Find.TickManager?.Pause();
                    return;
                }
            }

            _lastSendRealMs = Environment.TickCount;
            var formatted = FormatGameEvent(category, text);

            // 发送到 Companion（带 colony stats）
            var stats = TryBuildColonyStats();
            _ = _ccbWs!.SendEvent("rimworld.chat", new
            {
                category,
                text = formatted,
                severity = category is "RaidStart" or "NoColonists" ? "high"
                    : category is "AlertStart" or "DailyMorning" ? "medium" : "low",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                colonyStats = stats
            });
        }

        // ========== 每日早报 ==========

        private static string BuildDailyBriefing(Map map, List<Pawn> colonists, int colonistCount)
        {
            var tick = Find.TickManager?.TicksGame ?? 0;
            int day = tick / 60000;
            int year = day / 15 + 1;
            int dayOfSeason = day % 15 + 1;
            var season = GenLocalDate.Season(map);
            string seasonStr = season switch
            {
                Season.Spring => "春", Season.Summer => "夏", Season.Fall => "秋", Season.Winter => "冬", _ => "?"
            };
            var weather = map.weatherManager?.curWeather;
            float temp = map.mapTemperature?.OutdoorTemp ?? 0f;
            float avgMood = colonistCount > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f : 0f;
            int steel = GetResourceCount(map, "Steel");
            int wood = GetResourceCount(map, "WoodLog");
            int components = GetResourceCount(map, "ComponentIndustrial");
            int foodDays = CalcFoodDays(map, colonistCount);

            float generated = 0, used = 0;
            foreach (var net in map.powerNetManager?.AllNetsListForReading ?? new List<PowerNet>())
                foreach (var comp in net.powerComps)
                    if (comp.PowerOn)
                    { float rate = comp.EnergyOutputPerTick; if (rate > 0) generated += rate; else used += -rate; }

            var rm = Find.ResearchManager;
            var curProj = rm?.GetProject();

            var sb = new StringBuilder();
            sb.AppendLine($"## 每早汇报 第{year}年 {seasonStr}季 第{dayOfSeason}天");
            sb.AppendLine();
            sb.AppendLine("### 基础概况");
            sb.AppendLine($"天气: {weather?.label ?? "?"}, 室外 {temp:F0}°C");
            sb.AppendLine($"殖民者: {colonistCount} 人 | 平均心情 {avgMood:F0}%");
            sb.AppendLine($"资源: 钢{steel} 木{wood} 零件{components} | 食物约{foodDays}天");
            sb.AppendLine($"电力: 发{generated / 1000f:F0}kW 用{used / 1000f:F0}kW");
            if (curProj != null) sb.AppendLine($"研究: {curProj.label} ({rm!.GetProgress(curProj) * 100f:F0}%)");
            else sb.AppendLine("研究: 无进行中项目");

            if (colonistCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### 殖民者详情");
                foreach (var c in colonists)
                {
                    var issues = new List<string>();
                    foreach (var h in c.health?.hediffSet?.hediffs ?? new List<Hediff>())
                        if (h.Visible && !h.IsPermanent()) issues.Add(h.LabelCap);
                    string hs = issues.Count > 0 ? $" | 伤势: {string.Join(", ", issues.Take(3))}" : "";
                    sb.AppendLine($"- {c.LabelShort}: 心情{(c.needs?.mood?.CurLevelPercentage * 100f ?? 0):F0}% | {c.equipment?.Primary?.LabelCap ?? "无武器"}{hs}");
                }
            }

            if (_dailyEventLog.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### 昨日事件回顾");
                foreach (var evt in _dailyEventLog.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().Take(20))
                    sb.AppendLine($"- {evt}");
            }

            sb.AppendLine();
            sb.AppendLine("### 请按以下步骤执行");
            sb.AppendLine("1. 用 `get_game_context` + `get_colonists` + `check_colony` 获取最新状态");
            sb.AppendLine("2. 用 `add_memory` 记录关键经验教训");
            sb.AppendLine("3. 分析当前资源缺口、威胁等级、殖民者状态");
            sb.AppendLine("4. 确定今日优先事项，恢复游戏（`toggle_pause`）或继续操作");

            return sb.ToString().TrimEnd();
        }

        // ========== 弹框检测 ==========

        private static bool IsBlockingDialog(Window w)
            => w is FloatMenu || w is Dialog_MessageBox || w is Dialog_NodeTree
            || w is Dialog_GiveName || w is Dialog_Confirm || w is Dialog_Slider;

        private static void CheckDialogs(int nowMs, Map map, int colonistCount)
        {
            var ws = Find.WindowStack;
            var blocking = new List<Window>();
            if (ws != null)
                for (int i = 0; i < ws.Count; i++)
                    if (IsBlockingDialog(ws[i])) blocking.Add(ws[i]);

            int count = blocking.Count;
            string key = string.Join("|", blocking.Select(w => w.GetType().Name));

            if (count > 0 && (count != _lastDialogCount || key != _lastDialogKey))
            {
                _lastDialogCount = count; _lastDialogKey = key;
                int steel = GetResourceCount(map, "Steel");
                int foodDays = CalcFoodDays(map, colonistCount);
                SendCCMessage("AlertStart",
                    $"## 弹框提示\n当前有 {count} 个弹框需要选择。\n使用 get_open_dialogs 查看选项，select_dialog_option 选择。\n---\n殖民者: {colonistCount}人 | 食物: {foodDays}天 | 钢{steel}");
            }
            else if (count == 0 && _lastDialogCount > 0)
            { _lastDialogCount = 0; _lastDialogKey = ""; }
        }

        // ========== 辅助 ==========

        private static string BuildColonyOverview(Map map, List<Pawn> colonists, int count)
        {
            int steel = GetResourceCount(map, "Steel");
            int foodDays = CalcFoodDays(map, count);
            float avgMood = count > 0 ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f : 0f;
            return $"殖民地概览: {count}人 | 心情 {avgMood:F0}% | 食物 {foodDays}天 | 钢{steel} | 空闲...";
        }

        private static object? TryBuildColonyStats()
        {
            try
            {
                var map = Find.CurrentMap;
                if (map == null) return null;
                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                int count = colonists.Count;
                float avgMood = count > 0 ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f : 0f;
                return new { colonistCount = count, avgMood = (int)Math.Round(avgMood), foodDays = CalcFoodDays(map, count), colonyName = Find.World?.info?.name ?? "?" };
            }
            catch (Exception ex) { CoreLog.Info($"[EventForwarder] GetColonyStats 失败: {ex.Message}"); return null; }
        }

        private static int GetResourceCount(Map map, string defName)
        {
            var resources = map.resourceCounter?.AllCountedAmounts;
            if (resources == null) return 0;
            foreach (var kv in resources)
                if (kv.Key.defName == defName) return kv.Value;
            return 0;
        }

        private static int CalcFoodDays(Map map, int colonistCount)
        {
            if (colonistCount <= 0) return 0;
            float total = 0f;
            var resources = map.resourceCounter?.AllCountedAmounts;
            if (resources != null)
                foreach (var kv in resources)
                    if (kv.Key.IsNutritionGivingIngestible && kv.Key.ingestible?.HumanEdible == true && kv.Key.ingestible?.foodType != FoodTypeFlags.Tree)
                        total += kv.Value * (kv.Key.ingestible?.CachedNutrition ?? 0f);
            return (int)(total / (colonistCount * 1.6f));
        }

        private static void AddDailyEvent(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || _dailyEventLog.Count >= MaxDailyEventLog) return;
            _dailyEventLog.Add(line.Trim());
        }

        private static string FormatGameEvent(string category, string rawText)
        {
            var icon = category switch
            {
                "RaidStart" => "⚠️ [紧急]", "PawnDeath" => "💀 [紧急]",
                "DailyMorning" => "🌅", "IdleDetected" => "⏳", "NoColonists" => "💀 [覆灭]",
                "PauseRemind" => "⏸", "GameConnected" => "🎮", _ => "📢"
            };
            var instruction = category switch
            {
                "RaidStart" => "\n请先用 get_skills 查看可用技能，用 active_skill 获取相关知识后评估威胁。",
                "DailyMorning" => "\n游戏已自动暂停。请按简报步骤执行：检查 → 总结 → 评估 → 规划 → 恢复。",
                "IdleDetected" => "\n殖民者可能空闲。请用 get_skills 查看可用技能后分配工作。",
                "NoColonists" => "\n所有殖民者已死亡，请调用 regenerate_map 重开游戏 (i_know_danger=true)。",
                _ => ""
            };
            return $"{icon} {rawText}{instruction}";
        }
    }
}
