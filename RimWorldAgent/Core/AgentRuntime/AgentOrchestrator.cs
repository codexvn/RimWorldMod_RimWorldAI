using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    public enum GamePhase { None, Plan, Act }

    public static class AgentOrchestrator
    {
        /// <summary>真实游戏 TicksGame，由 SSE tick 事件自动更新</summary>
        public static int GameTick { get; set; }

        /// <summary>游戏天数，从 GameTick 计算</summary>
        public static int GameDay => GameTick / 60000;

        /// <summary>当前游戏阶段（Plan/Act/None）</summary>
        public static GamePhase CurrentPhase { get; private set; }

        /// <summary>当前会话的 GamePaceController 实例（由 RunSessionAsync 设置）</summary>
        public static GamePaceController? PaceController { get; set; }

        /// <summary>当前会话的 McpClient（供内部 Tool 调用 MCP）</summary>
        public static Mcp.McpClient? SessionMcp { get; set; }

        /// <summary>CcbWebSocket 引用（供 NotisAgent 直接发送通知）</summary>
        public static CcbManager.CcbWebSocket? CcbWs { get; set; }

        /// <summary>状态变化时触发</summary>
        public static event Action<string>? OnStatusChanged;

        /// <summary>是否有中断请求（所有通知触发）</summary>
        public static volatile bool InterruptRequested;

        /// <summary>中断通知摘要</summary>
        public static string InterruptSummary { get; set; } = "";

        /// <summary>Agent 会话是否活跃</summary>
        public static volatile bool IsRunning;

        /// <summary>上次执行 PLAN 的游戏日</summary>
        public static int LastPlanDay { get; set; } = -1;

        public static string StatusText
            => CurrentPhase switch { GamePhase.Plan => "PLAN / 暂停", GamePhase.Act => "ACT / 运行", _ => "就绪" };

        /// <summary>到了晨报/PLAN 时间？（新的一天，用于 EventForwarder 和 AgentEngine 统一调用）</summary>
        public static bool ShouldMorningReport()
        {
            int day = GameDay;
            if (day > LastPlanDay) { LastPlanDay = day; return true; }
            return false;
        }

        public static void BeginSession()
        {
            IsRunning = true;
            ToolDispatcher.ResetActPauseCount();
            ToolDispatcher.ResetNotifCount();
            OnStatusChanged?.Invoke(StatusText);
        }

        public static void EndSession()
        {
            IsRunning = false;
            OnStatusChanged?.Invoke(StatusText);
        }

        public static void EnterPlanPhase()
        {
            CurrentPhase = GamePhase.Plan;
            OnStatusChanged?.Invoke(StatusText);
        }

        public static void EnterActPhase()
        {
            CurrentPhase = GamePhase.Act;
            OnStatusChanged?.Invoke(StatusText);
        }

        public static void ClearPhase()
        {
            CurrentPhase = GamePhase.None;
            OnStatusChanged?.Invoke(StatusText);
        }

        /// <summary>统一中断入口：标记 + 摘要 + 立即 abort CCB 会话</summary>
        public static void RequestInterrupt(string summary)
        {
            InterruptRequested = true;
            InterruptSummary = summary;
            CoreLog.Info($"[AgentOrchestrator] 中断请求: {summary}");
            // 如果 Agent 正在运行，发 abort 立即打断当前 CCB 会话
            if (IsRunning && CcbWs?.IsReady == true)
            {
                _ = CcbWs.SendAbort();
                CoreLog.Info("[AgentOrchestrator] 已发送 abort 到 Companion");
            }
        }

        /// <summary>向 Agent 注入通知。运行中 → tool result suffix；否则 → 直接发送到 Companion。</summary>
        public static async Task NotisAgent(string notification)
        {
            if (string.IsNullOrEmpty(notification)) return;

            // Agent 运行中 + MCP 可用 → suffix 注入（AI 下次工具调用时看到）
            if (SessionMcp != null && IsRunning)
            {
                try
                {
                    var args = new System.Collections.Generic.Dictionary<string, JsonElement>
                    {
                        ["suffix"] = JsonSerializer.SerializeToElement(notification)
                    };
                    await SessionMcp.CallTool("set_tool_result_suffix", args);
                    CoreLog.Info($"[NotisAgent] suffix 注入 ({notification.Length} 字符)");
                    return;
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[NotisAgent] suffix 注入失败，降级为直接发送: {ex.Message}");
                }
            }

            // Agent 休眠或 suffix 失败 → 直接发送到 Companion
            if (CcbWs?.IsReady == true)
            {
                _ = CcbWs.SendEvent("rimworld.chat", new
                {
                    category = "Notification",
                    text = notification,
                    severity = "medium",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                CoreLog.Info($"[NotisAgent] 直接发送 ({notification.Length} 字符)");
            }
            else
            {
                CoreLog.Warn($"[NotisAgent] CcbWs 不可用，通知丢弃 ({notification.Length} 字符)");
            }
        }
    }

    public class ColonyEvent
    {
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Summary { get; set; } = "";
        public object? Payload { get; set; }
        public int Tick { get; set; }
        public string Method { get; set; } = "";
    }
}
