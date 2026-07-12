using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentTransport;
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

        /// <summary>Agent 会话 ID（从 ACP session 建立/更新捕获），用于 Scribe 持久化。</summary>
        public static string? AgentSessionId { get; set; }

        /// <summary>sessionId 变更回调（AgentEngine 订阅以同步到 MCP set_session_id）</summary>
        public static event Action<string>? OnSessionIdChanged;

        /// <summary>由 ACP projector/session 调用，触发 MCP 同步</summary>
        internal static void RaiseSessionIdChanged(string sid) => OnSessionIdChanged?.Invoke(sid);

        /// <summary>启动后是否已发送过消息（冷启检测：false 时触发首次问候）</summary>
        public static bool HasEverSent { get; set; }

        /// <summary>工具耗时暂存（toolId → ms），OnToolUse 写，OnToolResultRecorded 读+清理</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _toolDurations = new();
        private static readonly object _wireLock = new();
        private static IAgentSession? _wiredSession;
        private static bool _uiHandlersWired;

        /// <summary>读取工具耗时（不删除，用于 ACP projector 读取并推送到 UI）</summary>
        internal static double? PeekToolDuration(string toolId)
        {
            return _toolDurations.TryGetValue(toolId, out var d) ? d : (double?)null;
        }

        /// <summary>AgentSession ↔ UIMessageBus 双向中继：ACP→UiMessage 转换在 AgentCore 完成</summary>
        public static void WireUIMessageBus(IAgentSession session)
        {
            lock (_wireLock)
            {
                _wiredSession = session;

                if (_uiHandlersWired) return;
                _uiHandlersWired = true;
            }

            // 客户端 chat → 中断当前会话 + 预算检查 + 回显 + ACP prompt
            UIMessageBus.OnChat += async (text, thinking) =>
            {
                CoreLog.Debug($"[AgentLoop] OnChat len={text.Length}");
                var currentSession = GetWiredSession();
                if (currentSession == null || !currentSession.IsReady)
                {
                    UIMessageBus.PushUiMessage(UiMessage.Error("ACP session 未就绪，无法发送消息。"));
                    return;
                }
                if (BudgetLimit > 0 && TokenUsageTracker.TotalAllTokens >= BudgetLimit)
                {
                    UIMessageBus.PushUiMessage(UiMessage.Error($"Token 预算已用尽 ({TokenUsageTracker.TotalAllTokens}/{BudgetLimit})"));
                    return;
                }
                ConversationStore?.RecordUserMessage(text);
                await currentSession.CancelAsync(CancellationToken.None);
                // 打断运行中的会话时追加 Skill 提示
                if (AgentOrchestrator.IsRunning)
                    text += AgentOrchestrator.InterruptSkillHint;
                UIMessageBus.PushUiMessage(UiMessage.User(text));
                await currentSession.PromptAsync(text, CancellationToken.None);
            };

            // 客户端 abort → ACP cancel（只中断，不清空上下文）
            UIMessageBus.OnAbort += async () =>
            {
                var currentSession = GetWiredSession();
                if (currentSession != null && currentSession.IsReady)
                    await currentSession.CancelAsync(CancellationToken.None);
            };

            // 客户端 clear_context → ACP close + new session
            UIMessageBus.OnClearContext += () =>
            {
                AgentSessionId = null;
                var currentSession = GetWiredSession();
                if (currentSession != null && currentSession.IsReady)
                    _ = currentSession.ClearAsync(CancellationToken.None);
            };

        // 新客户端连接 → 推送初始状态
            UIMessageBus.OnClientConnected += socket =>
            {
                try
                {
                    var status = AgentOrchestrator.StatusText;
                    socket.Send(UiMessage.AgentStatus(status).ToJson());
                }
                catch (Exception ex) { CoreLog.Warn($"[AgentLoop] 推送 agent-status 失败: {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    socket.Send(UiMessage.BudgetStatus(
                        TokenUsageTracker.TotalAllTokens, BudgetLimit, "Idle",
                        TokenUsageTracker.TotalCacheReadTokens, TokenUsageTracker.TotalInputTokens + TokenUsageTracker.TotalCacheReadTokens,
                         TokenUsageTracker.TotalCacheCreateTokens, TokenUsageTracker.CurrentContextWindow,
                         TokenUsageTracker.CurrentInputTokens,
                         TokenUsageTracker.CurrentCacheReadTokens, TokenUsageTracker.CurrentCacheCreateTokens,
                         TokenUsageTracker.CurrentContextUsedTokens).ToJson());
                }
                catch (Exception ex) { CoreLog.Warn($"[AgentLoop] 推送 budget_status 失败: {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    socket.Send(UiMessage.CompactionStatus(AgentOrchestrator.IsCompacting).ToJson());
                }
                catch (Exception ex) { CoreLog.Warn($"[AgentLoop] 推送 compaction-status 失败: {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    if (UIMessageBus.LastSessionInit != null)
                        socket.Send(UIMessageBus.LastSessionInit.ToJson());
                }
                catch (Exception ex) { CoreLog.Warn($"[AgentLoop] 推送 session_init 失败: {ex.GetType().Name}: {ex.Message}"); }
                // 新客户端 → 推送当前任务列表
                UIMessageBus.PushSdkTasks();
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
                    try { socket.Send(UiMessage.Error($"历史查询失败: {ex.Message}").ToJson()); }
                    catch (Exception sendEx) { CoreLog.Warn($"[AgentLoop] 历史查询错误回写失败: {FormatExceptionChain(sendEx)}"); }
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
                    try { socket.Send(UiMessage.Error($"历史翻页失败: {ex.Message}").ToJson()); }
                    catch (Exception sendEx) { CoreLog.Warn($"[AgentLoop] 历史翻页错误回写失败: {FormatExceptionChain(sendEx)}"); }
                }
            };

            // 客户端 tool_stats → 返回工具调用统计
            UIMessageBus.OnToolStats += (socket, fromDay, toDay) =>
            {
                try
                {
                    var store = ConversationStore;
                    if (store == null) return;
                    var stats = store.GetToolDailyStats(fromDay, toDay);
                    var json = UiHistoryFormatter.FormatToolStatsResponse(stats, AgentOrchestrator.GameDay);
                    socket.Send(json);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"[AgentLoop] 工具统计查询失败: {ex.Message}");
                    try { socket.Send(UiMessage.Error($"工具统计查询失败: {ex.Message}").ToJson()); }
                    catch (Exception sendEx) { CoreLog.Warn($"[AgentLoop] 工具统计错误回写失败: {FormatExceptionChain(sendEx)}"); }
                }
            };

            // SDK assistant 完整内容 → 录制
            UIMessageBus.OnAssistantContent += (text, thinking, runId, agentType) =>
            {
                ConversationStore?.RecordAssistantMessage(text, thinking, runId, agentType);
            };

            // SDK 工具调用/结果 → 录制（网关工具提取内层 action 名）
            UIMessageBus.OnToolCallRecorded += (toolId, name, input, permissionToolName) =>
            {
                var innerName = ToolDispatcher.ExtractInnerAction(name, input);
                ConversationStore?.RecordToolCall(toolId, innerName, input, permissionToolName);
            };
            // tool_result 录制 — 合并 OnToolUse 耗时 + SDK echo 输出
            UIMessageBus.OnToolResultRecorded += (toolId, isError, content) =>
            {
                var dur = _toolDurations.TryRemove(toolId, out var d) ? d : 0;
                ConversationStore?.RecordToolResult(toolId, isError, dur, content);
            };
        }

        static AgentLoop()
        {
            TokenUsageTracker.OnUsageRecorded += () =>
            {
                UIMessageBus.PushUiMessage(UiMessage.BudgetStatus(
                    TokenUsageTracker.TotalAllTokens, BudgetLimit, "Block",
                    TokenUsageTracker.TotalCacheReadTokens, TokenUsageTracker.TotalInputTokens + TokenUsageTracker.TotalCacheReadTokens, TokenUsageTracker.TotalCacheCreateTokens,
                    TokenUsageTracker.CurrentContextWindow, TokenUsageTracker.CurrentInputTokens,
                    TokenUsageTracker.CurrentCacheReadTokens, TokenUsageTracker.CurrentCacheCreateTokens,
                    TokenUsageTracker.CurrentContextUsedTokens));
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
                catch (Exception ex)
                {
                    CoreLog.Warn($"[AgentLoop] 显示消息录制失败: {FormatExceptionChain(ex)}");
                }
            };
        }

        private static IAgentSession? GetWiredSession()
        {
            lock (_wireLock)
                return _wiredSession;
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
            var db = TokenUsageTracker.Db;
            if (db == null) return; // 静态构造器可能在 Db 注入前被触发（beforefieldinit），静默跳过
            UIMessageBus.PushUiMessage(UiMessage.BudgetStatus(
                db.TotalAllTokens, BudgetLimit, "Idle",
                db.TotalCacheReadTokens, db.TotalInputTokens,
                db.TotalCacheCreateTokens, 0, TokenUsageTracker.CurrentInputTokens,
                TokenUsageTracker.CurrentCacheReadTokens, TokenUsageTracker.CurrentCacheCreateTokens,
                TokenUsageTracker.CurrentContextUsedTokens));
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }

        /// <summary>MCP 游戏事件 → 按级别分流：Critical 中断，Warning/Info/Silent 仅 suffix</summary>
        public static void WireEvents(McpClient mcp)
        {
            if (mcp == null)
                throw new ArgumentNullException(nameof(mcp));

            // tick 事件 → 更新游戏 tick
            mcp.OnGameTick += tick => AgentOrchestrator.GameTick = tick;

            // 游戏事件 → 按级别分流
            mcp.OnGameEvent += evt =>
            {
                CoreLog.Info($"[event] {evt.Method}: {evt.Category}/{evt.Severity}(L{(int)evt.Level}): {evt.Summary}");
                var cleanSummary = StripRichTags(evt.Summary);
                var summary = evt.LetterId.HasValue
                    ? $"[{evt.Severity}/{evt.Category}] {cleanSummary} (ID:{evt.LetterId.Value}) — 可用 dismiss_notification(letter_id={evt.LetterId.Value}) 关闭"
                    : $"[{evt.Severity}/{evt.Category}] {cleanSummary}";

                // 所有级别都注入 suffix（AI 下次工具调用可见）
                AgentOrchestrator.NotisAgent(summary);
                ToolDispatcher.MarkNotifReceived();

                if (evt.Level >= EventLevel.Critical)
                {
                    // Critical → 立即中断 + UI + DB
                    CoreLog.Info($"[event] 触发 RequestInterrupt: {summary}");
                    AgentOrchestrator.RequestInterrupt(summary);
                    var notifyText = $"{AgentOrchestrator.InterruptPromptPrefix}\n{summary}\n{AgentOrchestrator.InterruptPromptSuffix}";
                    UIMessageBus.PushUiMessage(UiMessage.User(notifyText));
                    ConversationStore?.RecordUserMessage(notifyText);
                }
                // Warning / Info / Silent → 仅 suffix，不打断当前会话
            };
        }

        /// <summary>执行一次 Agent 会话：发送 prompt → Tool Loop → 写 Memory</summary>
        public static async Task RunSessionAsync(string prompt, McpClient mcp, IAgentSession session)
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

            async Task OnToolUse(string toolId, string toolName, string input)
            {
                NoteActivity();
                Interlocked.Increment(ref pendingTools);
                try
                {
                    await ToolDispatcher.HandleAsync(toolId, toolName, input);
                }
                catch (Exception ex)
                {
                    CoreLog.Error($"[commander] Tool 执行异常: {FormatExceptionChain(ex)}");
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
                CoreLog.Info("[AgentLoop] ACP 确认中断，结束会话");
                tcs.TrySetResult(true);
            }

            InternalToolRegistry.OnExitRequested += OnExit;
            session.OnResult += OnResult;
            session.OnToolUse += OnToolUse;
            session.OnAborted += OnAborted;
            session.OnActivity += NoteActivity;
            try
            {
                // 先回显到 UI/会话历史，再发 ACP prompt。
                // 否则整轮 agent 结束前，IPC 里已发出的殖民地状态 prompt 不会出现在游戏内 UI。
                ConversationStore?.RecordSystemMessage("[System Prompt] " + prompt);
                UIMessageBus.PushUiMessage(UiMessage.System("[System Prompt] " + prompt));
                await session.PromptAsync(prompt, CancellationToken.None);
                HasEverSent = true;
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
                session.OnAborted -= OnAborted;
                InternalToolRegistry.OnExitRequested -= OnExit;
                session.OnResult -= OnResult;
                session.OnToolUse -= OnToolUse;
                session.OnActivity -= NoteActivity;

                // 所有阶段均强制暂停，Plan/Act/None 无差别
                var phase = AgentOrchestrator.CurrentPhase;
                if (phase == GamePhase.Act || phase == GamePhase.None)
                    await paceController.EnsurePaused(mcp);
                // 不清除阶段 — enter_plan/enter_act 设置的状态在会话间保持
                AgentOrchestrator.PaceController = null;
                AgentOrchestrator.SessionMcp = null;
                paceController.Dispose();
            }
        }

    }
}
