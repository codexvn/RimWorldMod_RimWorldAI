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
        public static int ActPauseRemindThreshold = 5;
        public static int PlanRemindThreshold = 20;
        private static int _actPauseCheckCount;
        private static int _planCheckCount;

        public static int NotifCheckThreshold = 5;
        private static int _notifReceivedCount;
        private static int _taskCheckCount;

        // ===== SDK 任务追踪 =====

        public class TaskItem
        {
            public string Id { get; set; } = "";
            public string Subject { get; set; } = "";
            public string Status { get; set; } = "pending";
        }

        public static void ResetActPauseCount() => _actPauseCheckCount = 0;
        public static void ResetNotifCount() => _notifReceivedCount = 0;
        public static void MarkNotifReceived() => _notifReceivedCount++;

        private static readonly List<TaskItem> _tasks = new();
        public static int PendingTaskCount => _tasks.Count;

        /// <summary>线程安全快照，供游戏 UI 调用。</summary>
        public static List<TaskItem> TasksSnapshot()
        {
            lock (_tasks) return new List<TaskItem>(_tasks);
        }

        public static void ResetTaskCount()
        {
            lock (_tasks) _tasks.Clear();
        }

        public static void TrackToolUse(string toolName, JsonElement? input)
        {
            if (input == null) return;
            try
            {
                if (toolName.EndsWith("TaskCreate"))
                {
                    var subj = input.Value.TryGetProperty("subject", out var s) ? s.GetString() ?? "?" : "?";
                    lock (_tasks)
                    {
                        var id = (_tasks.Count + 1).ToString();
                        _tasks.Add(new TaskItem { Id = id, Subject = subj, Status = "pending" });
                    }
                }
                else if (toolName.EndsWith("TaskUpdate"))
                {
                    var tid = input.Value.TryGetProperty("taskId", out var ti) ? ti.GetString() ?? "" : "";
                    var st = input.Value.TryGetProperty("status", out var ts) ? ts.GetString() ?? "" : "";
                    if (st == "completed" || st == "deleted")
                    {
                        lock (_tasks) _tasks.RemoveAll(t => t.Id == tid);
                    }
                    else
                    {
                        lock (_tasks)
                        {
                            foreach (var t in _tasks)
                                if (t.Id == tid) { t.Status = st; break; }
                        }
                    }
                }
            }
            catch (Exception ex) { CoreLog.Info($"[ToolDispatcher] 任务追踪解析失败 ({toolName}): {ex.Message}"); }
        }

        public static async Task HandleAsync(
            CcbWebSocket ccbWs,
            string toolId, string toolName, JsonElement? input)
        {
            if (toolName is "get_notifications" or "dismiss_notification")
                _notifReceivedCount = 0;

            // 追踪 SDK 任务状态
            TrackToolUse(toolName, input);

        }

        /// <summary>生成工具结果后缀，每次通过 MCP 获取真实游戏暂停状态。</summary>
        public static async Task<string> BuildModeSuffixAsync()
        {
            var phase = AgentOrchestrator.CurrentPhase switch
            {
                GamePhase.Plan => "PLAN",
                GamePhase.Act => "ACT",
                _ => "ACT"
            };

            // 通过 MCP 获取真实暂停状态
            var isGamePaused = false;
            if (AgentOrchestrator.SessionMcp != null)
            {
                try
                {
                    var speed = await AgentOrchestrator.SessionMcp.CallTool("get_game_speed");
                    isGamePaused = speed != null && speed.IndexOf("已暂停", StringComparison.Ordinal) >= 0;
                }
                catch (Exception ex) { CoreLog.Info($"[ToolDispatcher] 查询游戏速度失败: {ex.Message}"); }
            }

            // ACT 阶段 + 游戏暂停提醒
            var actPauseRemind = "";
            if (AgentOrchestrator.CurrentPhase == GamePhase.Act && isGamePaused)
            {
                _actPauseCheckCount++;
                if (_actPauseCheckCount > ActPauseRemindThreshold)
                {
                    actPauseRemind = "\n\n<system-reminder>\n游戏仍处于暂停状态！你在 ACT 阶段，只有恢复游戏速度后才能执行实际操作。请调用 enter_act(speed=\"superfast\") 恢复游戏。\n</system-reminder>";
                }
            }
            else { _actPauseCheckCount = 0; }

            // PLAN 阶段停留过久提醒
            var planPauseRemind = "";
            if (AgentOrchestrator.CurrentPhase == GamePhase.Plan)
            {
                _planCheckCount++;
                if (_planCheckCount > PlanRemindThreshold)
                {
                    planPauseRemind = "\n\n<system-reminder>\n你已在 PLAN 阶段停留较久。制定计划后请调用 enter_act() 恢复游戏执行操作，不要让游戏长时间冻结。\n</system-reminder>";
                }
            }
            else { _planCheckCount = 0; }

            // 未完成任务提醒（每 10 次工具调用检查一次）
            var taskRemind = "";
            List<TaskItem> tasksCopy;
            lock (_tasks) tasksCopy = new List<TaskItem>(_tasks);
            var pending = tasksCopy.Count;
            if (pending > 0)
            {
                _taskCheckCount++;
                if (_taskCheckCount > 10)
                {
                    _taskCheckCount = 0;
                    var lines = new List<string>();
                    foreach (var t in tasksCopy)
                    {
                        lines.Add($"  [{t.Status}] {t.Subject}");
                        if (lines.Count >= 5) break;
                    }
                    var list = string.Join("\n", lines);
                    taskRemind = $"\n\n<system-reminder>\n当前 {pending} 个任务未完成：\n{list}\n完成的任务请用 TaskUpdate 更新状态。\n</system-reminder>";
                }
            }
            else { _taskCheckCount = 0; }

            // 通知堆积提醒
            var notifRemind = "";
            if (_notifReceivedCount > NotifCheckThreshold)
            {
                notifRemind = "\n\n<system-reminder>\n你有未处理的通知，请用 get_notifications 查看并处理。用 dismiss_notification 关闭不需要的通知。\n</system-reminder>";
            }

            return $"\n\n---\n当前模式: {phase}{taskRemind}{planPauseRemind}{actPauseRemind}{notifRemind}";
        }
    }
}
