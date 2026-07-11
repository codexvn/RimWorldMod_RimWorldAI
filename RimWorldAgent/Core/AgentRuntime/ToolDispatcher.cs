using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：状态推送 + 模式提醒后缀 + 任务追踪。</summary>
    public static class ToolDispatcher
    {
        // ===== Agent 任务追踪（通过内部工具 task_create/task_update/task_list/task_get 实现）=====

        public static int PendingTaskCount => TaskStore.PendingCount;

        public static List<TaskItem> TasksSnapshot() => TaskStore.GetAll();

        public static void ResetTaskCount() => TaskStore.Clear();
        public static void ResetNotifCount() => _notifReceivedCount = 0;
        public static void ResetActPauseCount()
        {
            /* _actPauseRemind 和 _actTurnRemind 已关闭
            _actPauseRemind!.Count = 0;
            _actTurnRemind!.Count = 0;
            */
            _taskToolRemind!.Count = 0;
            _toolOutputRemind!.Count = 0;
        }
        public static void MarkNotifReceived() => _notifReceivedCount++;

        // ===== 提醒类 =====

        public class Reminder
        {
            public string Name;
            public int Threshold;
            public Func<string> Template;
            public Func<bool>? Condition;
            public Action? OnFire;
            internal int Count;
            internal bool Fired;
            internal string? LastText;

            public Reminder(string name, int threshold, Func<string> template, Func<bool>? condition = null, Action? onFire = null)
            {
                Name = name; Threshold = threshold; Template = template; Condition = condition; OnFire = onFire;
            }

            public void Tick()
            {
                if (Condition != null && !Condition()) { Count = 0; Fired = false; LastText = null; return; }
                Count++;
                if (Count > Threshold)
                {
                    Count = 0;
                    Fired = true;
                    OnFire?.Invoke();
                    LastText = Template();
                }
                else { Fired = false; LastText = null; }
            }

            public string? GetSuffix() => LastText;
        }

        // 公共阈值
        // ActPauseThreshold 已关闭（Act 本就该暂停）
        public static int PlanStayThreshold = 10;
        public static int ActTurnThreshold = 20;
        public static int NotifThreshold = 5;
        public static int TaskCheckInterval = 10;
        public static int TaskToolRemindInterval = 15;
        public static int ToolOutputRemindInterval = 20;
        public static int WorldSummaryRefreshInterval = 30;
        public static int ActNoAdvanceThreshold = 10;

        private static int _notifReceivedCount;
        private static readonly ConcurrentQueue<string> _notifSuffixes = new ConcurrentQueue<string>();

        private static readonly List<Reminder> _reminders = new();
        public static IReadOnlyList<Reminder> Reminders => _reminders.AsReadOnly();

        private static bool _lastIsPaused;
        private static int _lastWindowsOpen;
        private static string? _lastWindowsNames;
        private static string? _lastSpeedLabel;
        private static int _lastIdleCount;
        private static int _lastSleepingCount;
        /* 节奏提醒已关闭
        private static Reminder? _actPauseRemind;
        private static Reminder? _actTurnRemind;
        private static Reminder? _planStayRemind;
        */
        private static Reminder? _taskRemind;
        private static Reminder? _taskToolRemind;
        private static Reminder? _toolOutputRemind;
        private static Reminder? _worldSummaryRemind;
        private static Reminder? _notifRemind;
        private static Reminder? _windowOpenRemind;

        static ToolDispatcher()
        {
            /* 节奏提醒已关闭
            _actPauseRemind = new Reminder("ACT 暂停", ActPauseThreshold,
                () => "\n\n<system-reminder>\n⚠️ 游戏仍处于暂停状态！你在 ACT 阶段但游戏时间不推进。请立即调用 enter_act() 恢复游戏。\n</system-reminder>",
                () => AgentOrchestrator.CurrentPhase == GamePhase.Act && _lastIsPaused);

            _actTurnRemind = new Reminder("ACT 执行过久", ActTurnThreshold,
                () => $"\n\n<system-reminder>\n⚠️ 你已在 ACT 阶段连续执行 {ActTurnThreshold}+ 轮工具调用。请调用 enter_plan() 暂停游戏、审视进度、更新计划后再继续。\n</system-reminder>",
                () => AgentOrchestrator.CurrentPhase == GamePhase.Act);

            _planStayRemind = new Reminder("PLAN 停留过久", PlanStayThreshold,
                () => "\n\n<system-reminder>\n⚠️ 你已在 PLAN 阶段停留较久，游戏已长时间冻结。请尽快制定计划并调用 enter_act() 恢复游戏。\n</system-reminder>",
                () => AgentOrchestrator.CurrentPhase == GamePhase.Plan);
            */

            _notifRemind = new Reminder("通知堆积", NotifThreshold,
                () => "\n\n<system-reminder>\n⚠️ 你有未处理的通知堆积。请立即用 get_notifications 查看，用 dismiss_notification 关闭不需要的通知。\n</system-reminder>",
                () => _notifReceivedCount > NotifThreshold);

            _windowOpenRemind = new Reminder("窗口打开", 0,
                () => $"\n\n<system-reminder>\n⚠️ 当前有 {_lastWindowsOpen} 个打开的对话框（{_lastWindowsNames}）。请先使用 get_open_dialogs 查看，再用 select_dialog_option 处理关闭。\n</system-reminder>",
                () => _lastWindowsOpen > 0);

            _taskRemind = new Reminder("任务未完成", TaskCheckInterval,
                () =>
                {
                    var copy = TaskStore.GetPending();
                    var lines = new List<string>();
                    foreach (var t in copy)
                    {
                        lines.Add($"  [{t.Status}] {t.Subject}");
                        if (lines.Count >= 5) break;
                    }
                    return $"\n\n<system-reminder>\n⚠️ 当前 {copy.Count} 个任务未完成：\n{string.Join("\n", lines)}\n完成的任务请用 task_update 标记为 completed。\n</system-reminder>";
                },
                () => TaskStore.PendingCount > 0);

            _taskToolRemind = new Reminder("未使用 task 工具", TaskToolRemindInterval,
                () => $"\n\n<system-reminder>\n⚠️ 你已连续 {TaskToolRemindInterval}+ 轮工具调用未使用 task_create / task_update。请立即创建任务计划跟踪进度，完成后用 task_update(status=\"completed\") 标记。\n</system-reminder>",
                () => true,
                () => { });  // 无额外动作，计数在 BuildModeSuffixAsync 中清零

            _toolOutputRemind = new Reminder("工具输出", ToolOutputRemindInterval,
                () => "\n\n<system-reminder>\n⚠️ 你已连续调用工具而未输出文本分析。请用自然语言总结当前观察和下一步计划。\n</system-reminder>",
                () => true);

            _worldSummaryRemind = new Reminder("世界摘要刷新", WorldSummaryRefreshInterval,
                () => "\n\n<system-reminder>\n⚠️ 距上次获取殖民地概况已有一段时间。请调用 get_world_summary 刷新全局状态，确保决策基于最新信息。\n</system-reminder>",
                () => true);

            _reminders.AddRange(new[] { /* _actPauseRemind, _actTurnRemind, _planStayRemind 已关闭, */
                _notifRemind, _taskRemind, _taskToolRemind, _toolOutputRemind, _worldSummaryRemind, _windowOpenRemind });
        }

        /// <summary>从网关工具 input JSON 提取内层 action 名；非网关工具直接返回原名。</summary>
        public static string ExtractInnerAction(string toolName, string inputJson)
        {
            // 网关工具：从 input JSON 中提取内层 action 名
            if (toolName.EndsWith("execute_tool"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(inputJson);
                    if (doc.RootElement.TryGetProperty("action", out var a))
                        return a.GetString() ?? toolName;
                }
                catch (Exception ex) { CoreLog.Debug($"[ToolDispatcher] ExtractInnerAction JSON 解析失败 ({toolName}): {ex.GetType().Name}: {ex.Message}"); }
                return toolName;
            }
            // 内部工具：去掉 Agent MCP 前缀 mcp__agent__
            return toolName.Replace("mcp__agent__", "");
        }

        public static void TrackToolUse(string toolName, string inputJson)
        {
            try
            {
                // 网关工具：提取内层 action 名
                toolName = ExtractInnerAction(toolName, inputJson);

                // 内部任务工具使用 → 重置提醒计数器（实际任务操作由 TaskStore 处理）
                if (toolName is "task_create" or "task_update" or "task_list" or "task_get")
                    _taskToolRemind!.Count = 0;

                // 工具输出提醒：非只读工具视为"有效产出"，清零计数器
                if (!toolName.StartsWith("get_") && !toolName.StartsWith("read_") && !toolName.StartsWith("list_"))
                    _toolOutputRemind!.Count = 0;

                // 世界摘要刷新提醒
                if (toolName == "get_world_summary")
                    _worldSummaryRemind!.Count = 0;
            }
            catch (Exception ex) { CoreLog.Info($"[ToolDispatcher] 工具追踪异常 ({toolName}): {ex.Message}"); }
        }

        public static Task HandleAsync(string toolId, string toolName, string input)
        {
            // 网关工具：提取内层 action 名用于追踪
            var effectiveName = ExtractInnerAction(toolName, input);

            if (effectiveName is "get_notifications" or "dismiss_notification")
                _notifReceivedCount = 0;

            TrackToolUse(effectiveName, input);
            return Task.CompletedTask;
        }

        /// <summary>添加通知 suffix（本地操作，无 MCP 往返）</summary>
        public static void EnqueueNotifSuffix(string suffix)
        {
            if (!string.IsNullOrEmpty(suffix))
                _notifSuffixes.Enqueue(suffix);
        }

        /// <summary>添加一次性 system-reminder suffix，用于非抢占式状态变化提示。</summary>
        public static void EnqueueSystemReminderSuffix(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _notifSuffixes.Enqueue($"<system-reminder>\n{text}\n</system-reminder>");
        }

        /// <summary>消费所有待注入通知 suffix</summary>
        private static string DrainNotifSuffixes()
        {
            var sb = new StringBuilder();
            while (_notifSuffixes.TryDequeue(out var s))
                sb.Append("\n\n").Append(s);
            return sb.ToString();
        }

        public static async Task<string> BuildModeSuffixAsync()
        {
            var phase = AgentOrchestrator.CurrentPhase switch
            {
                GamePhase.Plan => "PLAN",
                GamePhase.Act => "ACT",
                _ => "ACT"
            };

            if (AgentOrchestrator.SessionMcp != null)
            {
                try
                {
                    var json = await AgentOrchestrator.SessionMcp.CallTool("get_game_speed");
                    if (!string.IsNullOrEmpty(json))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            _lastIsPaused = root.TryGetProperty("paused", out var p) && p.GetBoolean();
                            _lastWindowsOpen = root.TryGetProperty("windows_open", out var wo) ? wo.GetInt32() : 0;
                            _lastWindowsNames = root.TryGetProperty("windows_names", out var wn) ? wn.GetString() : null;
                            _lastSpeedLabel = root.TryGetProperty("speed", out var sp) ? sp.GetString() : null;
                            _lastIdleCount = root.TryGetProperty("idle_count", out var ic) ? ic.GetInt32() : 0;
                            _lastSleepingCount = root.TryGetProperty("sleeping_count", out var sc) ? sc.GetInt32() : 0;
                        }
                        catch (JsonException)
                        {
                            // 兼容旧版文本格式
                            _lastIsPaused = json.IndexOf("已暂停", StringComparison.Ordinal) >= 0;
                            _lastWindowsOpen = 0;
                            _lastWindowsNames = null;
                        }
                    }
                }
                catch (Exception ex) { CoreLog.Debug($"[ToolDispatcher] 查询游戏速度失败: {ex.GetType().Name}: {ex.Message}"); }
            }

            // 所有提醒 tick
            foreach (var r in _reminders) r.Tick();

            // 组装后缀
            var suffix = new StringBuilder();
            // 通知 suffix（一次性，无 MCP 往返）
            var notifSuffix = DrainNotifSuffixes();
            if (notifSuffix.Length > 0)
                suffix.Append(notifSuffix);
            suffix.Append($"\n\n---\n当前模式: {phase}");
            if (!string.IsNullOrEmpty(_lastSpeedLabel))
                suffix.Append($" | {_lastSpeedLabel}");
            if (_lastIdleCount > 0 || _lastSleepingCount > 0)
                suffix.Append($" | 空闲{_lastIdleCount}人 睡眠{_lastSleepingCount}人");
            foreach (var r in _reminders)
            {
                var text = r.GetSuffix();
                if (!string.IsNullOrEmpty(text)) suffix.Append(text);
            }
            return suffix.ToString();
        }
    }
}
