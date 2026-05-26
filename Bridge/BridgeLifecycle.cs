using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Verse;
using RimWorld;
using RimWorldMCP.Harmony;
using RimWorldMCP.Helpers;
using RimWorldMCP.Tools;

namespace RimWorldMCP
{
    /// <summary>CC 桥接连接生命周期管理</summary>
    public static class BridgeLifecycle
    {
        private static int _nextCCEventTick;
        private const int CCEventCheckInterval = 120;
        private static int _nextCCFallbackMs;
        private const int CCFallbackIntervalMs = 5000;
        private static Process? _companionProcess;
        private static IntPtr _jobHandle = IntPtr.Zero;
        private static string _currentSessionId = "";

        // 空闲兜底 + 早报 + 殖民者追踪 + 弹框检测
        private static int _lastSendRealMs;
        private const int IdleOverviewIntervalMs = 120000;
        private static int _dailyReportDay = -1;
        private static int _lastColonistCount = -1;
        private static int _lastDialogCount;
        private static string _lastDialogKey = "";

        // 每日事件日志 — 晨报时汇总，之后清空
        private static List<string> _dailyEventLog = new List<string>();
        private const int MaxDailyEventLog = 100;

        // 暂停过久提醒
        private static int _pauseStartRealMs;
        private static int _lastPauseRemindMs;
        private const int PauseRemindFirstMs = 30000;   // 首次提醒：暂停 30 秒后
        private const int PauseRemindRepeatMs = 60000;  // 重复提醒：每隔 60 秒

        public static async Task StartAsync(string sessionId)
        {
            _currentSessionId = sessionId;
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null) return;

            if (settings.CCBAutoStart)
            {
                McpLog.Info("[bridge] CCBAutoStart=开启");
                McpLog.Info("[bridge] 步骤1: 停止当前进程...");
                StopCompanionProcess();
                McpLog.Info("[bridge] 步骤2: 清理残留 PID 文件...");
                KillStaleByPidFile();
                McpLog.Info("[bridge] 步骤3: 启动 Companion 进程...");
                StartCompanionProcess(settings.CCBHost, settings.CCBPort, settings.CCBAuthToken, sessionId);
                await Task.Delay(2000);
            }
            else
            {
                McpLog.Info("[bridge] CCBAutoStart=关闭，仅连接远程 Companion");
            }

            var ccbUrl = $"ws://{settings.CCBHost}:{settings.CCBPort}";
            await CCClient.Connect(ccbUrl, settings.CCBAuthToken);
            if (CCClient.IsReady)
            {
                McpLog.Info($"[bridge] 已连接到 Claude Code: {ccbUrl}");
            }
            else
            {
                McpLog.Error($"[bridge] Claude Code 连接失败: {ccbUrl}");
            }
        }

        public static void Tick()
        {
            // Companion 进程健康监控——崩溃时自动重启
            if (_companionProcess != null && _companionProcess.HasExited)
            {
                McpLog.Error($"[cc] Companion 进程意外退出 (退出码: {_companionProcess.ExitCode})，正在重启...");
                StopCompanionProcess();
                KillStaleByPidFile();
                var settings = RimWorldMCPMod.Instance?.Settings;
                if (settings?.CCBAutoStart == true)
                {
                    StartCompanionProcess(settings.CCBHost, settings.CCBPort, settings.CCBAuthToken, _currentSessionId);
                    var ccbUrl = $"ws://{settings.CCBHost}:{settings.CCBPort}";
                    _ = CCClient.Connect(ccbUrl, settings.CCBAuthToken);
                }
            }

            CCClient.Tick();
            CCEventTick();
        }

        public static void Stop()
        {
            CCClient.Disconnect();
            StopCompanionProcess();
        }

        // ========== CC 事件转发 ==========

        private static void CCEventTick()
        {
            if (!CCClient.IsReady) return;

            var map = Find.CurrentMap;
            if (map == null) return;
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            int colonistCount = colonists.Count;
            int nowMs = Environment.TickCount;

            // === 第1层：高危事件 — 每帧立即推送，不受暂停/Tick 影响 ===
            if (NotificationBus.HighDangerPending)
            {
                NotificationBus.HighDangerPending = false;
                var emergencyList = NotificationBus.Drain();
                if (emergencyList.Count > 0)
                {
                    // 高危单独推送，非高危合并到定期批次
                    var highList = new List<Notification>();
                    var lowLines = new List<string>();
                    foreach (var n in emergencyList)
                    {
                        if (NotificationBus.IsHighDanger(n.Type, n.DangerLabel, n.Priority))
                            highList.Add(n);
                        else
                            AddNotifyLine(n, lowLines);
                    }
                    if (highList.Count > 0)
                    {
                        foreach (var n in highList)
                            AddDailyEvent($"[高危] {n.Label}");
                        SendCCEvents(highList);
                    }
                    if (lowLines.Count > 0)
                    {
                        foreach (var line in lowLines) AddDailyEvent(line);
                        var sb = new StringBuilder("## 通知\n");
                        foreach (var line in lowLines) sb.AppendLine($"- {line}");
                        SendCCMessage("AlertStart", sb.ToString().TrimEnd());
                    }
                }
            }

            var tick = Find.TickManager?.TicksGame ?? 0;
            bool tickElapsed = tick >= _nextCCEventTick;
            if (tickElapsed)
                _nextCCEventTick = tick + CCEventCheckInterval;

            bool fallbackElapsed = unchecked((uint)(nowMs - _nextCCFallbackMs) >= CCFallbackIntervalMs);
            if (fallbackElapsed)
                _nextCCFallbackMs = nowMs;

            // === 第2层：定期轮询（游戏 Tick + wall clock 兜底） ===
            if (tickElapsed || fallbackElapsed)
            {
                var notifications = NotificationBus.Drain();

                // 殖民者数量变化
                bool countChanged = colonistCount != _lastColonistCount && _lastColonistCount >= 0;
                var lines = new List<string>();
                foreach (var n in notifications)
                {
                    if (!NotificationBus.IsHighDanger(n.Type, n.DangerLabel, n.Priority))
                        AddNotifyLine(n, lines);
                }
                if (countChanged)
                {
                    int diff = colonistCount - _lastColonistCount;
                    var countLine = $"殖民者 {_lastColonistCount}→{colonistCount} ({(diff > 0 ? "+" : "")}{diff})";
                    lines.Add(countLine);
                    AddDailyEvent(countLine);
                }
                _lastColonistCount = colonistCount;

                if (lines.Count > 0)
                {
                    foreach (var line in lines) AddDailyEvent(line);
                    var sb = new StringBuilder("## 通知\n");
                    foreach (var line in lines) sb.AppendLine($"- {line}");
                    SendCCMessage("AlertStart", sb.ToString().TrimEnd());
                }

                // 弹框检测
                CheckDialogs(nowMs, map, colonists, colonistCount);

                // 物品腐坏/耐久降低检测
                var deteriorationMsg = DeteriorationTracker.CheckAndNotify(map);
                if (deteriorationMsg != null)
                    SendCCMessage("DeteriorationWarning", deteriorationMsg);

                // 每日早报（游戏时间 6 点）
                int day = tick / 60000;
                int hour = GenLocalDate.HourOfDay(map);
                if (hour == 6 && _dailyReportDay != day)
                {
                    _dailyReportDay = day;
                    // 自动暂停游戏，让 AI 有充足时间做全面评估和规划
                    if (Find.TickManager != null && !Find.TickManager.Paused)
                    {
                        Find.TickManager.TogglePaused();
                        McpLog.Info("[cc] 晨报时间，自动暂停游戏以待 AI 评估规划");
                    }
                    var dailyText = BuildDailyBriefing(map, colonists, colonistCount);
                    SendCCMessage("DailyMorning", dailyText, BuildColonyStats(map, colonists));
                    _dailyEventLog.Clear();
                }
            }

            // === 第3层：空闲兜底 — 长时间无交互推送殖民地概览 ===
            if (_lastSendRealMs > 0
                && unchecked((uint)(nowMs - _lastSendRealMs) >= IdleOverviewIntervalMs)
                && !ChatDisplayState.IsBusy)
            {
                var overview = GameContextProvider.BuildColonyOverview(map, colonists, colonistCount);
                SendCCMessage("IdleDetected", overview, BuildColonyStats(map, colonists));
            }

            // === 第4层：暂停过久提醒 ===
            var paused = Find.TickManager?.Paused ?? false;
            if (paused)
            {
                if (_pauseStartRealMs == 0)
                {
                    _pauseStartRealMs = nowMs;
                    _lastPauseRemindMs = 0;
                }
                else if (_lastPauseRemindMs == 0
                    && unchecked((uint)(nowMs - _pauseStartRealMs) >= PauseRemindFirstMs))
                {
                    _lastPauseRemindMs = nowMs;
                    var status = GameContextProvider.BuildPauseStatus();
                    SendCCMessage("PauseRemind", $"游戏已暂停较长时间（{(nowMs - _pauseStartRealMs) / 1000}秒）\n{status}\n\n请检查是否需要继续游戏。");
                }
                else if (_lastPauseRemindMs > 0
                    && unchecked((uint)(nowMs - _lastPauseRemindMs) >= PauseRemindRepeatMs))
                {
                    _lastPauseRemindMs = nowMs;
                    var status = GameContextProvider.BuildPauseStatus();
                    SendCCMessage("PauseRemind", $"游戏仍在暂停中（共 {(nowMs - _pauseStartRealMs) / 1000} 秒）\n{status}\n\n如需继续请使用 toggle_pause 恢复游戏。");
                }
            }
            else
            {
                _pauseStartRealMs = 0;
                _lastPauseRemindMs = 0;
            }
        }

        /// <summary>统一发送入口 — 追踪最后发送时间，写入聊天窗并转发 AI</summary>
        private static void SendCCMessage(string category, string text, object? colonyStats = null)
        {
            _lastSendRealMs = Environment.TickCount;
            var formatted = FormatGameEvent(category, text);
            ChatDisplayState.AddSystemMessage(formatted);
            _ = CCClient.SendEventText("rimworld.chat", category, formatted, colonyStats);
        }

        /// <summary>每日早报</summary>
        private static string BuildDailyBriefing(Map map, List<Pawn> colonists, int colonistCount)
        {
            var tick = Find.TickManager?.TicksGame ?? 0;
            int day = tick / 60000;
            int year = day / 15 + 1;
            int dayOfSeason = day % 15 + 1;
            var season = GenLocalDate.Season(map);
            string seasonStr = season switch
            {
                Season.Spring => "春", Season.Summer => "夏",
                Season.Fall => "秋", Season.Winter => "冬", _ => "?"
            };
            var weather = map.weatherManager?.curWeather;
            float temp = map.mapTemperature?.OutdoorTemp ?? 0f;

            float avgMood = colonists.Count > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f : 0f;

            int steel = GetResourceCount(map, "Steel");
            int wood = GetResourceCount(map, "WoodLog");
            int components = GetResourceCount(map, "ComponentIndustrial");
            int foodDays = CalcFoodDays(map, colonistCount);

            float generated = 0, used = 0;
            foreach (var net in map.powerNetManager?.AllNetsListForReading ?? new List<PowerNet>())
                foreach (var comp in net.powerComps)
                    if (comp.PowerOn)
                    {
                        float rate = comp.EnergyOutputPerTick;
                        if (rate > 0) generated += rate; else used += -rate;
                    }
            string powerLabel = generated >= used ? "盈余" : "赤字";

            var rm = Find.ResearchManager;
            var curProj = rm?.GetProject();
            float wealth = map.wealthWatcher?.WealthTotal ?? 0f;

            var sb = new StringBuilder();
            sb.AppendLine($"## 每早汇报 第{year}年 {seasonStr}季 第{dayOfSeason}天");
            sb.AppendLine(GameContextProvider.BuildPauseStatus());
            sb.AppendLine();

            // === 基础概况 ===
            sb.AppendLine("### 基础概况");
            sb.AppendLine($"天气: {weather?.label ?? "?"}, 室外 {temp:F0}°C");
            sb.AppendLine($"殖民者: {colonistCount} 人 | 平均心情 {avgMood:F0}%");
            sb.AppendLine($"资源: 钢{steel} 木{wood} 零件{components} | 食物约{foodDays}天");
            sb.AppendLine($"电力: 发{generated / 1000f:F0}kW 用{used / 1000f:F0}kW ({powerLabel})");
            if (curProj != null)
                sb.AppendLine($"研究: {curProj.label} ({rm!.GetProgress(curProj) * 100f:F0}%)");
            else
                sb.AppendLine("研究: 无进行中项目");
            sb.AppendLine($"财富: {wealth:N0}");
            sb.AppendLine();

            // === 殖民者详情 ===
            if (colonistCount > 0)
            {
                sb.AppendLine("### 殖民者详情");
                foreach (var c in colonists)
                {
                    var healthIssues = new List<string>();
                    foreach (var h in c.health?.hediffSet?.hediffs ?? new List<Hediff>())
                        if (h.Visible && !h.IsPermanent())
                            healthIssues.Add(h.LabelCap);
                    string healthStr = healthIssues.Count > 0 ? $" | 伤势: {string.Join(", ", healthIssues.Take(3))}" : "";
                    string equipStr = c.equipment?.Primary?.LabelCap ?? "无武器";
                    sb.AppendLine($"- {c.LabelShort}: 心情{(c.needs?.mood?.CurLevelPercentage * 100f ?? 0):F0}% | {equipStr}{healthStr}");
                }
                sb.AppendLine();
            }

            // === 待办事项 ===
            var todos = TodoManager.Query("pending");
            if (todos.Count > 0)
            {
                sb.AppendLine("### 待办事项");
                foreach (var t in todos.Take(10))
                    sb.AppendLine($"- [P{t.Priority}] {t.Description}");
                sb.AppendLine();
            }

            // === 昨日事件 ===
            if (_dailyEventLog.Count > 0)
            {
                sb.AppendLine("### 昨日事件回顾");
                var recentEvents = _dailyEventLog
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct()
                    .Take(20);
                foreach (var evt in recentEvents)
                    sb.AppendLine($"- {evt}");
                sb.AppendLine();
            }

            // === 警报 ===
            var alertLines = NativeAlertHelper.BuildAlertLines(NativeAlertHelper.GetActiveAlerts());
            if (alertLines.Count > 0)
            {
                sb.AppendLine("### 当前警报");
                foreach (var a in alertLines) sb.AppendLine($"  - {a}");
                sb.AppendLine();
            }

            // === AI 行动指令 ===
            sb.AppendLine("### 请按以下步骤执行");
            sb.AppendLine("1. **全面检查**: 调用 `get_game_context` + `get_colonists` + `check_colony` 获取最新状态");
            sb.AppendLine("2. **总结经验**: 回顾昨日事件，记录重要经验教训（什么做得好、什么需要改进）");
            sb.AppendLine("3. **评估现状**: 分析当前资源缺口、威胁等级、殖民者状态、研究进度");
            sb.AppendLine("4. **制定计划**: 确定今日优先事项，用 `todo_add` 添加待办任务");
            sb.AppendLine("5. **恢复游戏**: 完成评估和规划后，调用 `toggle_pause` 恢复游戏运行");

            return sb.ToString().TrimEnd();
        }

        /// <summary>弹框检测 — FloatMenu/Dialog 出现时提示 AI</summary>
        private static void CheckDialogs(int nowMs, Map map, List<Pawn> colonists, int colonistCount)
        {
            var dialogs = DialogHelper.GetInteractableDialogs();
            int dialogCount = dialogs.Count;
            string dialogKey = "";
            foreach (var w in dialogs)
            {
                if (w is FloatMenu)
                {
                    var options = DialogHelper.FloatMenuOptionsField?.GetValue(w) as List<FloatMenuOption>;
                    if (options != null)
                        dialogKey = "fm:" + string.Join("|", options.Take(10).Select(o => o.Label).OrderBy(s => s));
                }
                else dialogKey += w.GetType().Name;
            }

            if (dialogCount > 0 && (dialogCount != _lastDialogCount || dialogKey != _lastDialogKey))
            {
                _lastDialogCount = dialogCount;
                _lastDialogKey = dialogKey;

                int steel = GetResourceCount(map, "Steel");
                int comps = GetResourceCount(map, "ComponentIndustrial");

                var sb = new StringBuilder();
                sb.AppendLine("## 弹框提示");
                sb.AppendLine($"当前有 {dialogCount} 个弹框需要选择。");
                sb.AppendLine("使用 get_open_dialogs 查看选项，select_dialog_option 选择。");
                sb.AppendLine($"---");
                sb.AppendLine($"殖民者: {colonistCount}人 | 食物: {CalcFoodDays(map, colonistCount)}天 | 钢{steel} 零件{comps}");
                SendCCMessage("AlertStart", sb.ToString().TrimEnd());
            }
            else if (dialogCount == 0 && _lastDialogCount > 0)
            {
                _lastDialogCount = 0;
                _lastDialogKey = "";
            }
        }

        /// <summary>通知格式化为文本行</summary>
        private static void AddNotifyLine(Notification n, List<string> lines)
        {
            switch (n.Type)
            {
                case NotificationType.Letter:
                    var ll = $"[{n.DangerLabel}] {n.Label}";
                    if (!string.IsNullOrEmpty(n.Text)) ll += $" — {n.Text}";
                    lines.Add(ll);
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
                    lines.Add($"[{n.Label} 已解除]");
                    break;
            }
        }

        /// <summary>记录每日事件到日志，晨报时汇总</summary>
        private static void AddDailyEvent(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (_dailyEventLog.Count >= MaxDailyEventLog) return;
            _dailyEventLog.Add(line.Trim());
        }

        private static int GetResourceCount(Map map, string defName)
        {
            var resources = map.resourceCounter?.AllCountedAmounts;
            if (resources == null) return 0;
            foreach (var kv in resources)
                if (kv.Key.defName == defName) return kv.Value;
            return 0;
        }

        /// <summary>构建结构化殖民地统计（push 到前端统计条）</summary>
        private static object BuildColonyStats(Map map, List<Pawn> colonists)
        {
            int colonistCount = colonists.Count;
            float avgMood = colonistCount > 0
                ? colonists.Average(c => c.needs?.mood?.CurLevelPercentage ?? 0.5f) * 100f : 0f;
            int foodDays = CalcFoodDays(map, colonistCount);
            string colonyName = Find.World?.info?.name ?? "?";
            return new
            {
                colonistCount,
                avgMood = (int)Math.Round(avgMood),
                foodDays,
                colonyName
            };
        }

        private static int CalcFoodDays(Map map, int colonistCount)
        {
            if (colonistCount <= 0) return 0;
            float totalNutrition = 0f;
            var resources = map.resourceCounter?.AllCountedAmounts;
            if (resources != null)
            {
                foreach (var kv in resources)
                {
                    var def = kv.Key;
                    if (def.IsNutritionGivingIngestible && def.ingestible?.HumanEdible == true
                        && def.ingestible?.foodType != FoodTypeFlags.Tree)
                        totalNutrition += kv.Value * (def.ingestible?.CachedNutrition ?? 0f);
                }
            }
            return (int)(totalNutrition / (colonistCount * 1.6f));
        }

        private static string FormatGameEvent(string category, string rawText)
        {
            var icon = category switch
            {
                "RaidStart" => "⚠️ [紧急]",
                "RaidEnd" => "✅",
                "PawnDeath" => "💀 [紧急]",
                "NegativeEvent" => "⚠️",
                "AlertStart" => "⚠️",
                "DailyMorning" => "🌅",
                "IdleDetected" => "⏳",
                "DeteriorationWarning" => "⚠️",
                _ => "📢"
            };
            var instruction = category switch
            {
                "RaidStart" => "\n请立即评估威胁并指挥防御。",
                "PawnDeath" => "\n请检查殖民地状态并评估影响。",
                "DailyMorning" => "\n游戏已自动暂停。请按简报中的步骤执行：全面检查 → 总结经验 → 评估现状 → 制定计划 → 恢复游戏。",
                "NegativeEvent" => "\n请评估严重程度并给出应对建议。",
                "AlertStart" => "\n请检查并处理此警报。",
                "IdleDetected" => "\n请检查是否有待分配的工作。",
                "DeteriorationWarning" => "\n请检查问题物品并及时处理。",
                _ => ""
            };
            return $"{icon} {rawText}{instruction}";
        }

        private static void SendCCEvents(List<Notification> notifications)
        {
            if (notifications.Count == 0) return;

            var sb = new StringBuilder();
            string primaryCategory = "AlertStart";

            for (int i = 0; i < notifications.Count; i++)
            {
                var n = notifications[i];
                if (!TryFormatNotification(n, out var category, out var rawText)) continue;

                if (i == 0) primaryCategory = category;
                sb.AppendLine(FormatGameEvent(category, rawText));
            }

            var text = sb.ToString().TrimEnd();
            if (text.Length > 0)
            {
                _lastSendRealMs = Environment.TickCount;
                ChatDisplayState.AddSystemMessage(text);
                _ = CCClient.SendEventText("rimworld.chat", primaryCategory, text);
            }
        }

        private static bool TryFormatNotification(Notification n, out string category, out string rawText)
        {
            switch (n.Type)
            {
                case NotificationType.Letter:
                    category = n.DangerLabel switch
                    {
                        "大威胁" => "RaidStart", "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath", "负面" => "NegativeEvent",
                        _ => "AlertStart"
                    };
                    rawText = string.IsNullOrEmpty(n.Text) ? n.Label : $"{n.Label} — {n.Text}";
                    return true;

                case NotificationType.Message:
                    category = n.DangerLabel switch
                    {
                        "大威胁" or "小威胁" => "RaidStart",
                        "死亡" => "PawnDeath", "负面" => "NegativeEvent",
                        _ => "AlertStart"
                    };
                    rawText = n.Text ?? n.Label;
                    return true;

                case NotificationType.AlertStart:
                    category = "AlertStart";
                    rawText = n.Culprits != null && n.Culprits.Count > 0
                        ? $"{n.Label}: {string.Join(", ", n.Culprits.Take(5))}"
                        : n.Label;
                    return true;

                default:
                    category = rawText = "";
                    return false;
            }
        }

        // ========== 公开 API（设置 UI 调用） ==========

        public static bool IsCompanionInstalled()
        {
            var dir = FindCompanionDir();
            if (dir == null) return false;
            return Directory.Exists(Path.Combine(dir, "node_modules"));
        }

        public static bool IsInstalling { get; private set; }
        public static string InstallStatus { get; private set; } = "";

        public static void InstallCompanion()
        {
            var dir = FindCompanionDir();
            if (dir == null) { InstallStatus = "找不到 cc-companion 目录"; return; }
            if (IsInstalling) return;

            IsInstalling = true;
            InstallStatus = "正在安装...";
            McpLog.Info($"[cc] 开始安装 Companion 依赖...");
            _ = Task.Run(() =>
            {
                var log = new System.Text.StringBuilder();
                try
                {
                    var npmPath = FindNpmPath();
                    if (npmPath == null)
                    {
                        InstallStatus = "找不到 npm，请确保已安装 Node.js (https://nodejs.org)";
                        IsInstalling = false;
                        return;
                    }
                    McpLog.Info($"[cc] 使用 npm: {npmPath}");
                    // ⚠ Process.Start(UseShellExecute=false) 通过 CreateProcess 直接启动，
                    // 只支持 .exe，不能运行 .cmd 或无扩展名的脚本文件。
                    // 检测到非 .exe 时用 cmd /c 包装（兼容 Scoop 等无扩展名路径）。
                    ProcessStartInfo psi;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        && !npmPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        psi = new ProcessStartInfo("cmd", $"/c \"{npmPath}\" install")
                        {
                            WorkingDirectory = dir,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                        };
                    }
                    else
                    {
                        psi = new ProcessStartInfo(npmPath, "install")
                        {
                            WorkingDirectory = dir,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                        };
                    }
                    using var proc = Process.Start(psi);
                    if (proc == null) { InstallStatus = "无法启动 npm install"; IsInstalling = false; return; }
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        { log.AppendLine(e.Data); InstallStatus = e.Data; McpLog.Info($"[npm] {e.Data}"); }
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        { log.AppendLine(e.Data); McpLog.Warn($"[npm] {e.Data}"); }
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit(120000);
                    if (proc.ExitCode == 0)
                    { InstallStatus = "安装完成"; McpLog.Info("[cc] Companion 依赖安装完成"); }
                    else
                    { InstallStatus = $"npm install 失败，退出码: {proc.ExitCode}"; McpLog.Error($"[cc] {InstallStatus}"); }
                }
                catch (Exception ex)
                { InstallStatus = $"安装失败: {ex.Message}"; McpLog.Error($"[cc] {InstallStatus}"); }
                finally { IsInstalling = false; }
            });
        }

        public static void UninstallCompanion()
        {
            var dir = FindCompanionDir();
            if (dir == null) return;
            var nodeModules = Path.Combine(dir, "node_modules");
            if (!Directory.Exists(nodeModules)) { McpLog.Info("[cc] node_modules 不存在，无需卸载"); return; }

            McpLog.Info($"[cc] 删除 {nodeModules} ...");
            try { Directory.Delete(nodeModules, true); McpLog.Info("[cc] Companion 依赖已卸载"); }
            catch (Exception ex) { McpLog.Error($"[cc] 卸载失败: {ex.Message}"); }
        }

        // ========== Companion 目录/Node.js 查找 ==========

        private static string? FindModRoot()
        {
            try
            {
                var asmPath = typeof(BridgeLifecycle).Assembly.Location;
                if (string.IsNullOrEmpty(asmPath)) return null;
                var asmDir = Path.GetDirectoryName(asmPath);
                if (asmDir == null) return null;
                return Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
            }
            catch { return null; }
        }

        public static string? FindNodeExe()
        {
            // 1. Try bare "node" via PATH (existing logic)
            {
                var node = TryFindNode("node");
                if (node != null) return node;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 2. Try "node.exe" explicitly (Windows PATHEXT workaround)
                {
                    var node = TryFindNode("node.exe");
                    if (node != null) return node;
                }

                // 3. Check common install paths
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var commonPaths = new[]
                {
                    Path.Combine(programFiles, "nodejs", "node.exe"),
                    Path.Combine(programFilesX86, "nodejs", "node.exe"),
                };

                // 4. Scan nvm directory: %APPDATA%\nvm\<version>\node.exe
                var nvmPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");
                if (Directory.Exists(nvmPath))
                {
                    try
                    {
                        var nvmVersions = Directory.GetDirectories(nvmPath)
                            .Select(d => Path.Combine(d, "node.exe"))
                            .Where(File.Exists)
                            .OrderByDescending(v => v) // latest version first
                            .ToArray();
                        foreach (var v in nvmVersions)
                        {
                            var node = TryFindNode(v);
                            if (node != null) return node;
                        }
                    }
                    catch { }
                }

                // 5. Check common paths via file existence
                foreach (var path in commonPaths)
                {
                    if (File.Exists(path))
                    {
                        var node = TryFindNode(path);
                        if (node != null) return node;
                    }
                }
            }

            // 6. Windows: cmd /c where node（shell 级 PATH 搜索，最可靠）
            var whereNode = TryFindWithWhere("node");
            if (whereNode != null) return whereNode;

            McpLog.Error("[cc] 未找到 Node.js，请确保已安装并加入 PATH (https://nodejs.org)");
            return null;
        }

        /// <summary>用 cmd /c where 查找可执行文件（Windows，支持 .cmd/.bat/.exe）</summary>
        private static string? TryFindWithWhere(string name)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            try
            {
                var psi = new ProcessStartInfo("cmd", "/c where " + name)
                { UseShellExecute = false, RedirectStandardOutput = true,
                  RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                proc.WaitForExit(5000);
                if (proc.ExitCode != 0) return null;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                if (string.IsNullOrEmpty(output)) return null;
                var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                return File.Exists(firstLine) ? firstLine : null;
            }
            catch { return null; }
        }

        /// <summary>尝试执行 node --version 验证给定路径的 node 可执行文件</summary>
        private static string? TryFindNode(string candidate)
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version")
                { UseShellExecute = false, RedirectStandardOutput = true,
                  RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc != null) { proc.WaitForExit(3000); if (proc.ExitCode == 0) return candidate; }
            }
            catch { }
            return null;
        }

        /// <summary>查找 npm 可执行文件路径，基于 node 路径推导 + where npm + PATH 兜底</summary>
        public static string? FindNpmPath()
        {
            // 1. 基于 node.exe 路径推导同目录下的 npm
            var nodeExe = FindNodeExe();
            if (nodeExe != null)
            {
                var nodeDir = Path.GetDirectoryName(nodeExe);
                if (!string.IsNullOrEmpty(nodeDir))
                {
                    var npmName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "npm.cmd" : "npm";
                    var npmCandidate = Path.Combine(nodeDir, npmName);
                    if (File.Exists(npmCandidate))
                    {
                        McpLog.Info($"[cc] 找到 npm: {npmCandidate} (基于 node 路径)");
                        return npmCandidate;
                    }
                }
            }

            // 2. Windows: cmd /c where npm（shell 级 PATH 搜索，支持 .cmd/.bat/.exe）
            var whereNpm = TryFindWithWhere("npm");
            if (whereNpm != null)
            {
                McpLog.Info($"[cc] 找到 npm: {whereNpm} (where npm)");
                return whereNpm;
            }

            // 3. Fallback: 直接 Process.Start PATH 查找
            //    ⚠ CreateProcess(UseShellExecute=false) 只搜索 PATH 中的 .exe,
            //    不搜索 .cmd/.bat（对 npm.cmd 无效）。保留作为兜底以防 npm.exe 存在。
            try
            {
                var psi = new ProcessStartInfo("npm", "--version")
                { UseShellExecute = false, RedirectStandardOutput = true,
                  RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc != null) { proc.WaitForExit(3000); if (proc.ExitCode == 0) { McpLog.Info("[cc] 找到 npm: npm (PATH)"); return "npm"; } }
            }
            catch { }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var psi = new ProcessStartInfo("npm.cmd", "--version")
                    { UseShellExecute = false, RedirectStandardOutput = true,
                      RedirectStandardError = true, CreateNoWindow = true };
                    using var proc = Process.Start(psi);
                    if (proc != null) { proc.WaitForExit(3000); if (proc.ExitCode == 0) { McpLog.Info("[cc] 找到 npm: npm.cmd (PATH)"); return "npm.cmd"; } }
                }
                catch { }
            }

            McpLog.Error("[cc] 未找到 npm，请确保 Node.js 安装正确 (https://nodejs.org)");
            return null;
        }

        public static string? FindCompanionDir()
        {
            var modRoot = FindModRoot();
            if (modRoot == null) return null;
            var dir = Path.Combine(modRoot, "cc-companion");
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "companion", "companion.ts")))
            { McpLog.Info($"[cc] Companion 目录: {dir}"); return dir; }
            McpLog.Error("[cc] 找不到 cc-companion 目录");
            return null;
        }

        // ========== 进程管理 ==========

        /// <summary>验证进程是否为 CC Companion（node + companion 路径标识）</summary>
        private static bool IsCCBProcess(System.Diagnostics.Process proc)
        {
            try
            {
                // 1. 进程名必须是 node
                var name = proc.ProcessName.ToLowerInvariant();
                if (name != "node" && name != "node.exe") return false;

                // 2. 检查主模块路径是否含 nodejs / node（如 Scoop 的可能在 node 目录）
                try
                {
                    var modulePath = proc.MainModule?.FileName ?? "";
                    if (modulePath.IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                catch
                {
                    // 无权限访问 MainModule（系统进程或其他用户）
                    // 进程名是 node，但无法确认路径 → 拒绝 kill
                    McpLog.Warn($"[cc] 无法获取 PID={proc.Id} 的模块路径，拒绝 kill（进程名={proc.ProcessName})");
                    return false;
                }

                McpLog.Warn($"[cc] PID={proc.Id} 模块路径不含 node 标识，拒绝 kill（路径={proc.MainModule?.FileName})");
                return false;
            }
            catch { return false; }
        }

        private static void KillStaleByPidFile()
        {
            var dir = FindCompanionDir();
            if (dir == null) return;
            var pidFile = Path.Combine(dir, ".pid");
            if (!File.Exists(pidFile)) { McpLog.Info("[cc] 无残留 PID 文件，跳过清理"); return; }

            try
            {
                var pidText = File.ReadAllText(pidFile).Trim();
                McpLog.Info($"[cc] 发现残留 PID 文件: {pidFile} (PID={pidText})");
                if (int.TryParse(pidText, out int pid))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        if (!IsCCBProcess(proc))
                        {
                            McpLog.Warn($"[cc] 残留 PID={pid} ({proc.ProcessName}) 不是 CC Companion 进程，跳过");
                            return;
                        }
                        McpLog.Info($"[cc] 正在杀死残留进程 PID={pid} ({proc.ProcessName})");
                        proc.Kill(); proc.WaitForExit(3000);
                        McpLog.Info($"[cc] 残留进程 PID={pid} 已终止");
                    }
                    catch (ArgumentException) { McpLog.Info($"[cc] PID={pid} 进程已不存在，仅清理 PID 文件"); }
                }
            }
            catch (Exception ex) { McpLog.Warn($"[cc] 读取/清理 PID 文件失败: {ex.Message}"); }
            finally { try { File.Delete(pidFile); } catch { } }
        }

        private static void StartCompanionProcess(string host, int port, string token, string sessionId)
        {
            var nodeExe = FindNodeExe(); if (nodeExe == null) return;
            var companionDir = FindCompanionDir(); if (companionDir == null) return;

            var modRoot = FindModRoot();
            var baseSessionsDir = modRoot != null
                ? Path.Combine(modRoot, "claude-sessions")
                : Path.Combine(companionDir, "..", "claude-sessions");
            var settings = RimWorldMCPMod.Instance?.Settings;
            var mcpPort = settings?.McpPort ?? 9877;

            // 共享目录（跨存档共享 SDK 记忆/checkpoints）
            Directory.CreateDirectory(baseSessionsDir);
            McpLog.Info($"[cc] 会话目录 (共享): {baseSessionsDir}");

            // 从设置读 ProjectSettingsJson 模板，空则用默认值
            var template = settings?.CCBProjectSettingsJson;
            if (string.IsNullOrWhiteSpace(template))
                template = BuildProjectSettingsJson(mcpPort);

            // C# 写出 .claude/settings.json（project 层）
            var settingsDir = Path.Combine(baseSessionsDir, ".claude");
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(Path.Combine(settingsDir, "settings.json"), template, Encoding.UTF8);

            var args = $"--import tsx/esm companion/companion.ts"
                + $" --idle-timeout 30000";
            if (!string.IsNullOrEmpty(settings?.CCBModelName))
                args += $" --model-name \"{EscapeJsonForArg(settings.CCBModelName)}\"";

            try
            {
                McpLog.Info($"[cc] 启动 Companion: {nodeExe} {args}");
                var psi = new ProcessStartInfo
                {
                    FileName = nodeExe, Arguments = args, WorkingDirectory = companionDir,
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true,
                };
                // 通过环境变量传递（替代 CLI args）
                psi.Environment["RIMWORLD_PROJECT_PATH"] = baseSessionsDir;
                psi.Environment["CCB_HOST"] = host;
                psi.Environment["CCB_PORT"] = port.ToString();
                if (!string.IsNullOrEmpty(token)) psi.Environment["CCB_AUTH_TOKEN"] = token;

                _companionProcess = Process.Start(psi);
                if (_companionProcess == null) { McpLog.Error("[cc] 无法启动 Companion 进程"); return; }

                // Windows: JobObject 绑定，主进程退出时 OS 自动杀子进程
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (_jobHandle != IntPtr.Zero) { CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
                    AttachToJobObject(_companionProcess);
                }

                _companionProcess.EnableRaisingEvents = true;
                _companionProcess.Exited += (_, _) =>
                {
                    var exitCode = _companionProcess?.ExitCode;
                    McpLog.Warn($"[cc] Companion 进程已退出 (PID: {_companionProcess?.Id}, 退出码: {exitCode})");
                };
                _companionProcess.OutputDataReceived += (_, e) =>
                { if (!string.IsNullOrEmpty(e.Data)) McpLog.Info($"[js] {e.Data}"); };
                _companionProcess.ErrorDataReceived += (_, e) =>
                { if (!string.IsNullOrEmpty(e.Data)) McpLog.Warn($"[js] {e.Data}"); };
                _companionProcess.BeginOutputReadLine();
                _companionProcess.BeginErrorReadLine();

                McpLog.Info($"[cc] Companion 进程已启动 (PID: {_companionProcess.Id}, CWD: {companionDir})");
            }
            catch (Exception ex) { McpLog.Error($"[cc] 启动 Companion 进程失败: {ex.Message}"); }
        }

        internal static string BuildProjectSettingsJson(int mcpPort)
        {
            var obj = new Dictionary<string, object?>
            {
                ["mcpServers"] = new Dictionary<string, object>
                {
                    ["rimworld"] = new Dictionary<string, string>
                    {
                        ["type"] = "http",
                        ["url"] = $"http://localhost:{mcpPort}/mcp"
                    }
                }
            };
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>JSON 字符串转义，使其可安全用于 CLI 双引号参数</summary>
        private static string EscapeJsonForArg(string json)
        {
            return json.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void StopCompanionProcess()
        {
            if (_companionProcess == null) { McpLog.Info("[cc] 无需停止：无当前进程引用"); return; }
            try
            {
                if (!_companionProcess.HasExited)
                {
                    McpLog.Info($"[cc] 正在停止 Companion 进程 (PID: {_companionProcess.Id})...");
                    _companionProcess.Kill(); _companionProcess.WaitForExit(5000);
                    McpLog.Info("[cc] Companion 进程已停止");
                }
                else McpLog.Info($"[cc] Companion 进程已退出 (PID: {_companionProcess.Id})");
            }
            catch (Exception ex) { McpLog.Warn($"[cc] 停止 Companion 进程失败: {ex.Message}"); }
            finally
            {
                _companionProcess.Dispose(); _companionProcess = null;
                if (_jobHandle != IntPtr.Zero) { CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
            }
        }

        // ========== Windows Job Object：父进程死 → OS 自动杀子进程 ==========

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, int jobObjectInfoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static void AttachToJobObject(Process proc)
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero)
            {
                McpLog.Warn("[cc] 无法创建 JobObject（非管理员？不影响运行，但强杀 RimWorld 时 companion 不会自动退出）");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            var size = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ref info, size))
            {
                McpLog.Warn($"[cc] 无法设置 JobObject: {Marshal.GetLastWin32Error()}");
                return;
            }

            if (!AssignProcessToJobObject(_jobHandle, proc.Handle))
            {
                McpLog.Warn($"[cc] 无法将 Companion 加入 JobObject: {Marshal.GetLastWin32Error()}");
                return;
            }

            McpLog.Info("[cc] Companion 已绑定到 JobObject，RimWorld 退出时自动终止");
        }
    }
}
