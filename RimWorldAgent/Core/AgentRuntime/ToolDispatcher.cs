using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：内部 Tool → 本地处理，外部 Tool → 转发 MCP。</summary>
    public static class ToolDispatcher
    {
        public static int ActPauseRemindThreshold = 5;
        private static int _actPauseCheckCount;

        public static int NotifCheckThreshold = 5;
        private static int _notifReceivedCount;

        public static void ResetActPauseCount() => _actPauseCheckCount = 0;
        public static void ResetNotifCount() => _notifReceivedCount = 0;
        public static void MarkNotifReceived() => _notifReceivedCount++;

        public static async Task HandleAsync(
            CcbWebSocket ccbWs, McpClient mcp,
            string toolId, string toolName, JsonElement? input,
            Action<string> log)
        {
            var sw = Stopwatch.StartNew();

            // 通知工具被调用时重置计数
            if (toolName is "get_notifications" or "dismiss_notification")
                _notifReceivedCount = 0;

            // 内部 Tool → 直接本地处理
            if (InternalToolRegistry.Instance.IsInternal(toolName))
            {
                try
                {
                    log($"工具调用: {toolName}");
                    var (result, shouldExit) = await InternalToolRegistry.Instance.ExecuteInternalAsync(toolName, input);
                    sw.Stop();
                    log($"工具完成: {toolName} 用时 {sw.ElapsedMilliseconds}ms");
                    var suffix = BuildModeSuffix();
                    await ccbWs.SendToolResult(toolId, result + suffix);

                    // 同步推送当前 agent 角色和阶段
                    try
                    {
                        if (ccbWs.IsReady)
                            await ccbWs.SendEvent("agent.status", new { text = AgentOrchestrator.StatusText });
                    }
                    catch (Exception ex) { log($"推送状态失败: {ex.Message}"); }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    log($"工具失败: {toolName} 用时 {sw.ElapsedMilliseconds}ms — {ex.GetType().Name}: {ex.Message}");
                    await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}{BuildModeSuffix()}", true);
                }
                return;
            }

            // 外部 Tool → 转发 MCP
            try
            {
                log($"工具调用: {toolName}");
                var args = input != null
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.Value.GetRawText())
                    : null;
                var result = await mcp.CallTool(toolName, args);
                sw.Stop();
                log($"工具完成: {toolName} 用时 {sw.ElapsedMilliseconds}ms");
                var suffix = BuildModeSuffix();
                await ccbWs.SendToolResult(toolId, result + suffix);

                // 同步推送当前 agent 角色和阶段
                try
                {
                    if (ccbWs.IsReady)
                        await ccbWs.SendEvent("agent.status", new { text = AgentOrchestrator.StatusText });
                }
                catch (Exception ex) { log($"推送状态失败: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                sw.Stop();
                log($"工具失败: {toolName} 用时 {sw.ElapsedMilliseconds}ms — {ex.Message}");
                await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}{BuildModeSuffix()}", true);
            }
        }

        private static string BuildModeSuffix()
        {
            var phase = AgentOrchestrator.CurrentPhase switch
            {
                GamePhase.Plan => "PLAN",
                GamePhase.Act => "ACT",
                _ => AgentOrchestrator.IsRunning ? "ACT（未设定 enter_act）" : "就绪"
            };

            // ACT 阶段暂停过久提醒
            var actPauseRemind = "";
            if (AgentOrchestrator.CurrentPhase == GamePhase.Act
                && AgentOrchestrator.PaceController?.IsPaused == true)
            {
                _actPauseCheckCount++;
                if (_actPauseCheckCount > ActPauseRemindThreshold)
                {
                    actPauseRemind = "\n\n<system-reminder>\n游戏仍处于暂停状态！你在 ACT 阶段，只有恢复游戏速度后才能执行实际操作。请调用 enter_act(speed=\"superfast\") 恢复游戏。\n</system-reminder>";
                }
            }
            else { _actPauseCheckCount = 0; }

            // 通知堆积提醒
            var notifRemind = "";
            if (_notifReceivedCount > NotifCheckThreshold)
            {
                notifRemind = "\n\n<system-reminder>\n你有未处理的通知，请用 get_notifications 查看并处理。用 dismiss_notification 关闭不需要的通知。\n</system-reminder>";
            }

            return $"\n\n---\n当前模式: {phase}{actPauseRemind}{notifRemind}";
        }
    }
}
