using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Data;
using RimWorldAgent.Core.Mcp;
using RimWorldAgent.Core.models;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>EXE / MOD 共享的 Agent 主循环逻辑</summary>
    public static class AgentLoop
    {
        public static long BudgetLimit { get; set; }

        /// <summary>会话存储 — 由 EXE/MOD 在 WireUIMessageBus 前注入</summary>
        public static IConversationStore? ConversationStore { get; set; }

        /// <summary>启动后是否已发送过消息（冷启检测：false 时触发首次问候）</summary>
        public static bool HasEverSent { get; set; }

        /// <summary>CCB ↔ UIMessageBus 双向中继：SDK↔UiMessage 转换在 AgentCore 完成</summary>
        public static void WireUIMessageBus(CcbWebSocket ws)
        {
            // SDK 消息 → UiMessage → UIMessageBus 广播
            ws.OnSdkMessage += msg =>
            {
                var messages = SdkMessageParser.ParseToUiMessages(msg);
                if (messages.Count > 0) UIMessageBus.PushUiMessages(messages);
            };

            // 客户端 chat → 中断当前会话 + 预算检查 + 回显 + CCB
            UIMessageBus.OnChat += async (text, thinking) =>
            {
                CoreLog.Info($"[CCGUI_DEBUG] AgentLoop.OnChat 触发 text=\"{text.Substring(0, Math.Min(text.Length, 60))}\"");
                if (BudgetLimit > 0 && TokenUsageTracker.TotalAllTokens >= BudgetLimit)
                {
                    UIMessageBus.PushUiMessage(UiMessage.Error($"Token 预算已用尽 ({TokenUsageTracker.TotalAllTokens}/{BudgetLimit})"));
                    return;
                }
                // 用户消息本地录制 + 推送（C# 统一处理，SDK 不回传 user echo）
                ConversationStore?.RecordUserMessage(text);
                CoreLog.Info($"[CCGUI_DEBUG] AgentLoop.OnChat 调用 SendAbort...");
                await ws.SendAbort();
                UIMessageBus.PushUiMessage(UiMessage.User(text));
                CoreLog.Info($"[CCGUI_DEBUG] AgentLoop.OnChat SendAbort done, 调用 SendChat...");
                await ws.SendChat(ChatChannel.Bus, text, thinking);
                CoreLog.Info($"[CCGUI_DEBUG] AgentLoop.OnChat SendChat done");
            };

            // 客户端 abort → CCB
            UIMessageBus.OnAbort += async () => await ws.SendAbort();

            // 新客户端连接 → 推送初始状态
            UIMessageBus.OnClientConnected += socket =>
            {
                try
                {
                    var status = AgentOrchestrator.StatusText;
                    socket.Send(UiMessage.AgentStatus(status).ToJson());
                }
                catch { }
                try
                {
                    socket.Send(UiMessage.BudgetStatus(
                        TokenUsageTracker.TotalAllTokens, BudgetLimit, "Idle",
                        TokenUsageTracker.TotalCacheReadTokens, TokenUsageTracker.TotalInputTokens,
                        TokenUsageTracker.TotalCacheCreateTokens).ToJson());
                }
                catch { }
            };

            // 客户端 history → 返回历史消息
            UIMessageBus.OnHistory += (socket, n) =>
            {
                try
                {
                    var store = ConversationStore;
                    if (store == null) return;
                    var entries = store.GetRecent(n);
                    var json = UiHistoryFormatter.FormatResponse(entries);
                    socket.Send(json);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[AgentLoop] 历史查询失败: {ex.Message}");
                    try { socket.Send(UiMessage.Error($"历史查询失败: {ex.Message}").ToJson()); } catch { }
                }
            };

            // 客户端 history_before → 返回更早的消息（向上滚动加载）
            UIMessageBus.OnHistoryBefore += (socket, beforeId, n) =>
            {
                try
                {
                    var store = ConversationStore;
                    if (store == null) return;
                    var entries = store.GetBefore(beforeId, n);
                    var hasMore = entries.Count >= n;
                    var json = UiHistoryFormatter.FormatBeforeResponse(entries, hasMore);
                    socket.Send(json);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[AgentLoop] 历史翻页失败: {ex.Message}");
                    try { socket.Send(UiMessage.Error($"历史翻页失败: {ex.Message}").ToJson()); } catch { }
                }
            };

            // SDK assistant 完整内容 → 录制
            UIMessageBus.OnAssistantContent += (text, thinking, runId, agentType) =>
            {
                ConversationStore?.RecordAssistantMessage(text, thinking, runId, agentType);
            };

            // SDK 工具调用/结果 → 录制
            UIMessageBus.OnToolCallRecorded += (toolId, name, input) =>
            {
                ConversationStore?.RecordToolCall(toolId, name, input);
            };
            UIMessageBus.OnToolResultRecorded += (toolId, isError, content) =>
            {
                ConversationStore?.RecordToolResult(toolId, isError, 0, content);
            };

        }

        static AgentLoop()
        {
            TokenUsageTracker.OnUsageRecorded += () =>
            {
                UIMessageBus.PushUiMessage(UiMessage.BudgetStatus(
                    TokenUsageTracker.TotalAllTokens, BudgetLimit, "Block",
                    TokenUsageTracker.TotalCacheReadTokens, TokenUsageTracker.TotalInputTokens, TokenUsageTracker.TotalCacheCreateTokens));
            };

            // 初始推送：budget 起始状态
            PushInitialBudget();

            // 阶段变化 → agent-status 广播
            AgentOrchestrator.OnStatusChanged += status =>
            {
                UIMessageBus.PushUiMessage(UiMessage.AgentStatus(status));
            };

            // 系统/错误消息 → 录制
            UIMessageBus.OnDisplayMessage += json =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type == "system")
                    {
                        var txt = root.TryGetProperty("text", out var st) ? st.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(txt))
                            ConversationStore?.RecordSystemMessage(txt);
                    }
                    else if (type == "error")
                    {
                        var err = root.TryGetProperty("error", out var er) ? er.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(err))
                            ConversationStore?.RecordSystemMessage(err);
                    }
                }
                catch { /* best-effort，不影响显示管线 */ }
            };
        }

        /// <summary>剥离 RimWorld 富文本标签（color/size/b/i），保留纯文本。
        /// 例如 &lt;color=#FF3333FF&gt;哈瓦恩沃希&lt;/color&gt; → 哈瓦恩沃希</summary>
        private static string StripRichTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            // 移除闭合标签: </color>, </size>, </b>, </i>
            var s = System.Text.RegularExpressions.Regex.Replace(text, "</?(?:color|size|b|i|material)[^>]*>", "");
            return s;
        }

        private static void PushInitialBudget()
        {
            UIMessageBus.PushUiMessage(UiMessage.BudgetStatus(
                TokenUsageTracker.TotalAllTokens, BudgetLimit, "Idle",
                TokenUsageTracker.TotalCacheReadTokens, TokenUsageTracker.TotalInputTokens,
                TokenUsageTracker.TotalCacheCreateTokens));
        }

        /// <summary>MCP 游戏事件 → 所有通知都触发中断</summary>
        public static void WireEvents(McpClient mcp)
        {
            // tick 事件 → 更新游戏 tick
            mcp.OnGameTick += tick => AgentOrchestrator.GameTick = tick;

            // 游戏事件 → 所有事件触发中断 + 双工通知
            mcp.OnGameEvent += evt =>
            {
                CoreLog.Info($"[event] {evt.Method}: {evt.Category}/{evt.Severity}: {evt.Summary}");
                var cleanSummary = StripRichTags(evt.Summary);
                var summary = $"[{evt.Severity}/{evt.Category}] {cleanSummary}";
                AgentOrchestrator.RequestInterrupt(summary);
                _ = AgentOrchestrator.NotisAgent(summary);
                ToolDispatcher.MarkNotifReceived();
            };
        }

        /// <summary>执行一次 Agent 会话：发送 prompt → Tool Loop → 写 Memory</summary>
        public static async Task RunSessionAsync(string prompt, McpClient mcp, CcbWebSocket ccbWs)
        {
            // 复用已在外部暂停的 PaceController（每日 PLAN 等），无则创建
            var paceController = AgentOrchestrator.PaceController;
            if (paceController == null)
            {
                paceController = new GamePaceController();
                AgentOrchestrator.PaceController = paceController;
            }
            AgentOrchestrator.SessionMcp = mcp;
            // 会话默认 ACT 阶段（首次进入时未调用 enter_act 也显示 ACT）
            if (AgentOrchestrator.CurrentPhase == GamePhase.None)
                AgentOrchestrator.EnterActPhase();

            var tcs = new TaskCompletionSource<bool>();
            var pendingTools = 0;
            var resultReceived = false;
            var lastActivityTicks = DateTime.UtcNow.Ticks;
            const long inactivityTimeoutTicks = 120000 * TimeSpan.TicksPerMillisecond;

            void NoteActivity() => Volatile.Write(ref lastActivityTicks, DateTime.UtcNow.Ticks);

            void OnResult(string subtype, string? _)
            {
                NoteActivity();
                var pending = Volatile.Read(ref pendingTools);
                if (AgentOrchestrator.InterruptRequested)
                {
                    CoreLog.Info("[AgentLoop] 检测到中断请求，结束会话");
                    tcs.TrySetResult(true);
                    return;
                }
                CoreLog.Debug($"[commander] 回合结束: {subtype} (pendingTools={pending})");
                if (subtype == "success" && pending == 0)
                    tcs.TrySetResult(true);
                else if (subtype == "success")
                    Volatile.Write(ref resultReceived, true);
            }

            async void OnToolUse(string toolId, string toolName, JsonElement? input)
            {
                NoteActivity();
                Interlocked.Increment(ref pendingTools);
                try
                {
                    await ToolDispatcher.HandleAsync(ccbWs, toolId, toolName, input);
                }
                catch (Exception ex)
                {
                    CoreLog.Error($"[commander] Tool 执行异常: {ex.Message}");
                }
                finally
                {
                    var remaining = Interlocked.Decrement(ref pendingTools);
                    if (remaining == 0 && resultReceived)
                        tcs.TrySetResult(true);
                }
            }

            void OnExit()
            {
                CoreLog.Debug("[commander] 内部工具请求退出会话");
                tcs.TrySetResult(true);
            }

            void OnAborted()
            {
                CoreLog.Info("[AgentLoop] Companion 确认中断，结束会话");
                tcs.TrySetResult(true);
            }

            InternalToolRegistry.OnExitRequested += OnExit;
            ccbWs.OnResult += OnResult;
            ccbWs.OnToolUse += OnToolUse;
            ccbWs.OnAborted += OnAborted;
            try
            {
                await ccbWs.SendChat(ChatChannel.System, prompt);
                HasEverSent = true;
                ConversationStore?.RecordSystemMessage("[System Prompt] " + prompt);
                UIMessageBus.PushUiMessage(UiMessage.System("[System Prompt] " + prompt));
                // 活动感知超时：每次 tool_use / result 重置计时器，避免长对话被误杀
                while (!tcs.Task.IsCompleted)
                {
                    var elapsedTicks = DateTime.UtcNow.Ticks - Volatile.Read(ref lastActivityTicks);
                    if (elapsedTicks >= inactivityTimeoutTicks)
                    {
                        CoreLog.Info("[AgentLoop] 会话超时 (120s 无活动)");
                        break;
                    }
                    var pollMs = (int)Math.Min((inactivityTimeoutTicks - elapsedTicks) / TimeSpan.TicksPerMillisecond, 1000);
                    await Task.WhenAny(tcs.Task, Task.Delay(pollMs));
                }
            }
            finally
            {
                ccbWs.OnAborted -= OnAborted;
                InternalToolRegistry.OnExitRequested -= OnExit;
                ccbWs.OnResult -= OnResult;
                ccbWs.OnToolUse -= OnToolUse;

                // 确保游戏恢复 + 清除阶段状态
                await paceController.EnsureResumed(mcp);
                AgentOrchestrator.ClearPhase();
                AgentOrchestrator.PaceController = null;
                AgentOrchestrator.SessionMcp = null;
                paceController.Dispose();
            }
        }

    }
}
