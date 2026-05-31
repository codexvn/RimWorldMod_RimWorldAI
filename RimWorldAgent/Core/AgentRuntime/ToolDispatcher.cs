using System;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：状态推送 + 模式提醒后缀。</summary>
    public static class ToolDispatcher
    {
        public static int ActPauseRemindThreshold = 5;
        public static int PlanRemindThreshold = 20;
        private static int _actPauseCheckCount;
        private static int _planCheckCount;

        public static int NotifCheckThreshold = 5;
        private static int _notifReceivedCount;

        public static void ResetActPauseCount() => _actPauseCheckCount = 0;
        public static void ResetNotifCount() => _notifReceivedCount = 0;
        public static void MarkNotifReceived() => _notifReceivedCount++;

        public static async Task HandleAsync(
            CcbWebSocket ccbWs,
            string toolId, string toolName)
        {
            if (!AgentOrchestrator.IsRunning)
            {
                await ccbWs.SendToolResult(toolId, "Error: Agent 会话已结束，请重新唤醒。", true);
                return;
            }

            if (toolName is "get_notifications" or "dismiss_notification")
                _notifReceivedCount = 0;

            try
            {
                if (ccbWs.IsReady)
                    await ccbWs.SendEvent("agent.status", new { text = AgentOrchestrator.StatusText });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CoreLog.Info($"[ToolDispatcher] 推送状态失败: {ex.Message}"); }
        }

        /// <summary>生成工具结果后缀，每次通过 MCP 获取真实游戏暂停状态。</summary>
        public static async Task<string> BuildModeSuffixAsync()
        {
            var phase = AgentOrchestrator.CurrentPhase switch
            {
                GamePhase.Plan => "PLAN",
                GamePhase.Act => "ACT",
                _ => AgentOrchestrator.IsRunning ? "ACT" : "就绪"
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

            // 通知堆积提醒
            var notifRemind = "";
            if (_notifReceivedCount > NotifCheckThreshold)
            {
                notifRemind = "\n\n<system-reminder>\n你有未处理的通知，请用 get_notifications 查看并处理。用 dismiss_notification 关闭不需要的通知。\n</system-reminder>";
            }

            return $"\n\n---\n当前模式: {phase}{planPauseRemind}{actPauseRemind}{notifRemind}";
        }
    }
}
