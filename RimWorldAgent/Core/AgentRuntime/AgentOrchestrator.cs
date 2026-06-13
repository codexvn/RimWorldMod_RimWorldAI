using System;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.models;

namespace RimWorldAgent.Core.AgentRuntime
{
    public enum GamePhase { None, Plan, Act, Advance }

    public static class AgentOrchestrator
    {
        /// <summary>中断通知模板 — 送入 SDK 的 prompt 格式</summary>
        public const string InterruptPromptPrefix = "## 事件通知";
        public const string InterruptPromptSuffix = "以上是游戏内发生的新事件，请关注并根据当前优先级自行决定处理时机。\n\n<system-reminder>\n在处理前，请先使用 get_skills 查看可用领域知识，必要时用 active_skill 激活相关 Skill 获取详细指导。\n</system-reminder>\n现在继续: ";
        /// <summary>中断时注入的 Skill 提示（已合并到 InterruptPromptSuffix，保留供 OnChat 打断消息使用）</summary>
        public const string InterruptSkillHint = "\n\n<system-reminder>\n在处理前，请先使用 get_skills 查看可用领域知识，必要时用 active_skill 激活相关 Skill 获取详细指导。\n</system-reminder>";

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
        public static CcbWebSocket? CcbWs { get; set; }

        /// <summary>状态变化时触发</summary>
        public static event Action<string>? OnStatusChanged;

        /// <summary>是否有中断请求（所有通知触发）</summary>
        public static volatile bool InterruptRequested;

        /// <summary>中断通知摘要</summary>
        public static string InterruptSummary { get; set; } = "";

        /// <summary>上次执行 PLAN 的游戏日</summary>
        public static int LastPlanDay { get; set; } = -1;

        /// <summary>当前是否有活跃的 AI 会话（AgentEngine 用，防止重复启动）</summary>
        public static volatile bool IsRunning;

        /// <summary>advance_tick 正在推进游戏时间（守护线程在推进期间跳过）</summary>
        public static volatile bool IsAdvancing;

        /// <summary>SDK 是否正在执行上下文压缩（由 CcbWebSocket 在收到 system status 消息时更新）</summary>
        public static bool IsCompacting { get; set; }

        public static string StatusText
            => CurrentPhase switch { GamePhase.Plan => "PLAN / 暂停", GamePhase.Act => "ACT / 暂停", GamePhase.Advance => "ADVANCE / 运行", _ => "空闲" };

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

        /// <summary>统一中断入口：标记 + 摘要 + 立即 abort → 立即发送通知 prompt</summary>
        public static void RequestInterrupt(string summary)
        {
            InterruptRequested = true;
            // 多次中断累积摘要（而非覆盖），确保 SDK 收到完整上下文
            InterruptSummary = string.IsNullOrEmpty(InterruptSummary)
                ? summary
                : InterruptSummary + "\n" + summary;
            CoreLog.Info($"[AgentOrchestrator] 中断请求: {summary}");
            if (CcbWs?.IsReady == true)
            {
                _ = CcbWs.SendAbort();
                // abort 后立即发送通知到 SDK（companion 缓冲 → 新 session 回放）
                var prompt = $"{InterruptPromptPrefix}\n{summary}\n{InterruptPromptSuffix}";
                _ = CcbWs.SendChat(ChatChannel.Bus, prompt);
            }
        }

        /// <summary>向 Agent 注入通知。写入本地 suffix 缓冲，下次工具调用结果自动拼接。</summary>
        public static void NotisAgent(string notification)
        {
            if (string.IsNullOrEmpty(notification)) return;
            ToolDispatcher.EnqueueNotifSuffix(notification);
            CoreLog.Info($"[NotisAgent] suffix 入队 ({notification.Length} 字符)");
        }
    }

    /// <summary>事件等级，与 MCP 侧 EventLevel 对齐</summary>
    public enum EventLevel
    {
        Silent = 0,
        Info = 1,
        Warning = 2,
        Critical = 3
    }

    public class ColonyEvent
    {
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Summary { get; set; } = "";
        public object? Payload { get; set; }
        public int Tick { get; set; }
        public string Method { get; set; } = "";
        public EventLevel Level { get; set; } = EventLevel.Warning; // 缺省 Warning，向后兼容
        public int? LetterId { get; set; }
    }
}
