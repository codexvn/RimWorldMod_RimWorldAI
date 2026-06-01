using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：状态推送 + 模式提醒后缀 + 任务追踪。</summary>
    public static class ToolDispatcher
    {
        // ===== SDK 任务追踪 =====

        public class TaskItem
        {
            public string Id { get; set; } = "";
            public string Subject { get; set; } = "";
            public string Status { get; set; } = "pending";
        }

        private static readonly List<TaskItem> _tasks = new();
        public static int PendingTaskCount => _tasks.Count;

        public static List<TaskItem> TasksSnapshot()
        {
            lock (_tasks) return new List<TaskItem>(_tasks);
        }

        public static void ResetTaskCount() { lock (_tasks) _tasks.Clear(); }
        public static void ResetNotifCount() => _notifReceivedCount = 0;
        public static void ResetActPauseCount()
        {
            _actPauseRemind!.Count = 0;
            _actTurnRemind!.Count = 0;
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
        public static int ActPauseThreshold = 5;
        public static int PlanStayThreshold = 10;
        public static int ActTurnThreshold = 10;
        public static int NotifThreshold = 5;
        public static int TaskCheckInterval = 10;
        public static int TaskToolRemindInterval = 15;
        public static int ToolOutputRemindInterval = 20;
        public static int WorldSummaryRefreshInterval = 30;

        private static int _notifReceivedCount;

        private static readonly List<Reminder> _reminders = new();
        public static IReadOnlyList<Reminder> Reminders => _reminders.AsReadOnly();

        private static bool _lastIsPaused;
        private static Reminder? _actPauseRemind;
        private static Reminder? _actTurnRemind;
        private static Reminder? _planStayRemind;
        private static Reminder? _taskRemind;
        private static Reminder? _taskToolRemind;
        private static Reminder? _toolOutputRemind;
        private static Reminder? _worldSummaryRemind;
        private static Reminder? _notifRemind;

        static ToolDispatcher()
        {
            _actPauseRemind = new Reminder("ACT 暂停", ActPauseThreshold,
                () => "\n\n<system-reminder>\n游戏仍处于暂停状态！你在 ACT 阶段，如需推进工作进度请调用 enter_act(speed=\"暂停/正常/高速/极速\") 恢复游戏。\n</system-reminder>",
                () => AgentOrchestrator.CurrentPhase == GamePhase.Act && _lastIsPaused);

            _actTurnRemind = new Reminder("ACT 执行过久", ActTurnThreshold,
                () => $"\n\n<system-reminder>\n你已在 ACT 阶段连续执行 {ActTurnThreshold}+ 轮工具调用。建议调用 enter_plan() 暂停游戏、审视进度、更新计划，然后再继续执行。\n</system-reminder>",
                () => AgentOrchestrator.CurrentPhase == GamePhase.Act);

            _planStayRemind = new Reminder("PLAN 停留过久", PlanStayThreshold,
                () => "\n\n<system-reminder>\n你已在 PLAN 阶段停留较久。制定计划后请调用 enter_act() 恢复游戏执行操作，不要让游戏长时间冻结。\n</system-reminder>",
                () => AgentOrchestrator.CurrentPhase == GamePhase.Plan);

            _notifRemind = new Reminder("通知堆积", NotifThreshold,
                () => "\n\n<system-reminder>\n你有未处理的通知，请用 get_notifications 查看并处理。用 dismiss_notification 关闭不需要的通知。\n</system-reminder>",
                () => _notifReceivedCount > NotifThreshold);

            _taskRemind = new Reminder("任务未完成", TaskCheckInterval,
                () =>
                {
                    List<TaskItem> copy;
                    lock (_tasks) copy = new List<TaskItem>(_tasks);
                    var lines = new List<string>();
                    foreach (var t in copy)
                    {
                        lines.Add($"  [{t.Status}] {t.Subject}");
                        if (lines.Count >= 5) break;
                    }
                    return $"\n\n<system-reminder>\n当前 {copy.Count} 个任务未完成：\n{string.Join("\n", lines)}\n完成的任务请用 TaskUpdate 更新状态。\n</system-reminder>";
                },
                () => { lock (_tasks) return _tasks.Count > 0; });

            _taskToolRemind = new Reminder("未使用 task 工具", TaskToolRemindInterval,
                () => $"\n\n<system-reminder>\n你已连续 {TaskToolRemindInterval}+ 轮工具调用未使用 TaskCreate / TaskUpdate。建议调用 TaskCreate 制定任务计划、跟踪执行进度。完成后用 TaskUpdate(status=\"completed\") 标记。\n</system-reminder>",
                () => true,
                () => { });  // 无额外动作，计数在 BuildModeSuffixAsync 中清零

            _toolOutputRemind = new Reminder("工具输出", ToolOutputRemindInterval,
                () => "\n\n<system-reminder>\n你已连续调用工具而未输出文本分析。请用自然语言总结当前观察和下一步计划，帮助用户了解进展。\n</system-reminder>",
                () => true);

            _worldSummaryRemind = new Reminder("世界摘要刷新", WorldSummaryRefreshInterval,
                () => "\n\n<system-reminder>\n距上次获取殖民地概况已有一段时间。建议调用 get_world_summary 重新了解全局状态。\n</system-reminder>",
                () => true);

            _reminders.AddRange(new[] { _actPauseRemind, _actTurnRemind, _planStayRemind,
                _notifRemind, _taskRemind, _taskToolRemind, _toolOutputRemind, _worldSummaryRemind });
        }

        public static void TrackToolUse(string toolName, string inputJson)
        {
            if (string.IsNullOrEmpty(inputJson)) return;
            try
            {
                using var doc = JsonDocument.Parse(inputJson);
                var input = doc.RootElement;

                if (toolName.EndsWith("TaskCreate"))
                {
                    _taskToolRemind!.Count = 0;
                    var subj = input.TryGetProperty("subject", out var s) ? s.GetString() ?? "?" : "?";
                    lock (_tasks)
                    {
                        var id = (_tasks.Count + 1).ToString();
                        _tasks.Add(new TaskItem { Id = id, Subject = subj, Status = "pending" });
                    }
                }
                else if (toolName.EndsWith("TaskUpdate"))
                {
                    _taskToolRemind!.Count = 0;
                    var tid = input.TryGetProperty("taskId", out var ti) ? ti.GetString() ?? "" : "";
                    var st = input.TryGetProperty("status", out var ts) ? ts.GetString() ?? "" : "";
                    if (st == "completed" || st == "deleted")
                        lock (_tasks) _tasks.RemoveAll(t => t.Id == tid);
                    else
                        lock (_tasks) { foreach (var t in _tasks) if (t.Id == tid) { t.Status = st; break; } }
                }

                // 工具输出提醒：非只读工具视为"有效产出"，清零计数器
                if (!toolName.StartsWith("get_") && !toolName.StartsWith("read_") && !toolName.StartsWith("list_"))
                    _toolOutputRemind!.Count = 0;

                // 世界摘要刷新提醒
                if (toolName == "get_world_summary")
                    _worldSummaryRemind!.Count = 0;
            }
            catch (Exception ex) { CoreLog.Info($"[ToolDispatcher] 任务追踪解析失败 ({toolName}): {ex.Message}"); }
        }

        public static async Task HandleAsync(
            CcbWebSocket ccbWs,
            string toolId, string toolName, string input)
        {
            if (toolName is "get_notifications" or "dismiss_notification")
                _notifReceivedCount = 0;

            TrackToolUse(toolName, input);
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
                    var speed = await AgentOrchestrator.SessionMcp.CallTool("get_game_speed");
                    _lastIsPaused = speed != null && speed.IndexOf("已暂停", StringComparison.Ordinal) >= 0;
                }
                catch (Exception ex) { CoreLog.Info($"[ToolDispatcher] 查询游戏速度失败: {ex.Message}"); }
            }

            // 所有提醒 tick
            foreach (var r in _reminders) r.Tick();

            // 组装后缀
            var suffix = new System.Text.StringBuilder();
            suffix.Append($"\n\n---\n当前模式: {phase}");
            foreach (var r in _reminders)
            {
                var text = r.GetSuffix();
                if (!string.IsNullOrEmpty(text)) suffix.Append(text);
            }
            return suffix.ToString();
        }
    }
}
