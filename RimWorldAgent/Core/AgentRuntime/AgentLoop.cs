using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>EXE / MOD 共享的 Agent 主循环逻辑</summary>
    public static class AgentLoop
    {
        /// <summary>从 get_world_summary Markdown 文本解析 SchedulerInput</summary>
        public static SchedulerInput ParseSchedulerInput(string text)
        {
            var input = new SchedulerInput { CurrentTick = Environment.TickCount };
            if (string.IsNullOrEmpty(text)) return input;

            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.Contains("殖民者") && t.Contains("|")) input.ColonistCount = ParseInt(t);
                else if (t.Contains("空闲")) input.IdleCount = ParseInt(t);
                else if (t.Contains("食物") && t.Contains("天")) input.FoodDays = ParseFloat(t);
                else if (t.Contains("敌人")) input.EnemyCount = ParseInt(t);
                else if (t.Contains("药品")) input.MedicineCount = ParseInt(t);
            }
            return input;
        }

        private static CcbWebSocket? _statusWs;
        private static long _budgetLimit;

        /// <summary>CCB WebSocket → Agent 状态推送到 Web 页面（幂等，仅保留最新连接）</summary>
        public static void WireCcbStatus(CcbWebSocket ccbWs)
        {
            _statusWs = ccbWs;
            _budgetLimit = ccbWs.BudgetLimit;
            AgentOrchestrator.CcbWs = ccbWs;

            if (ccbWs.IsReady)
            {
                _ = ccbWs.SendEvent("agent.status", new { text = AgentOrchestrator.StatusText });
                PushBudgetUpdate(ccbWs);
            }
        }

        static AgentLoop()
        {
            AgentOrchestrator.OnStatusChanged += status =>
            {
                if (_statusWs?.IsReady == true)
                    _ = _statusWs.SendEvent("agent.status", new { text = status });
            };

            TokenUsageTracker.OnUsageRecorded += () =>
            {
                if (_statusWs?.IsReady == true)
                    PushBudgetUpdate(_statusWs);
            };
        }

        private static void PushBudgetUpdate(CcbWebSocket ws)
        {
            _ = ws.SendEvent("budget-update", new
            {
                used = TokenUsageTracker.TotalAllTokens,
                limit = _budgetLimit,
                action = "Block",
                cacheRead = TokenUsageTracker.TotalCacheReadTokens,
                totalInput = TokenUsageTracker.TotalInputTokens,
            });
        }

        /// <summary>MCP 游戏事件 → 所有通知都触发中断</summary>
        public static void WireEvents(McpClient mcp)
        {
            // tick 事件 → 更新游戏 tick
            mcp.OnGameTick += tick => AgentOrchestrator.GameTick = tick;

            // 世界状态 → 更新 Scheduler
            mcp.OnWorldState += input => Scheduler.Tick(input);

            // 游戏事件 → 所有事件触发中断 + 双工通知
            mcp.OnGameEvent += evt =>
            {
                CoreLog.Info($"[event] {evt.Method}: {evt.Category}/{evt.Severity}: {evt.Summary}");
                var summary = $"[{evt.Severity}/{evt.Category}] {evt.Summary}";
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
            var normalExit = false;
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
                { normalExit = true; tcs.TrySetResult(true); }
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
                await ccbWs.SendChat(prompt);
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
                if (!normalExit)
                    _ = ccbWs.SendAbort();

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

        private static int ParseInt(string s)
        {
            foreach (var p in s.Split('|'))
                if (int.TryParse(p.Trim(), out var v)) return v;
            return 0;
        }

        private static float ParseFloat(string s)
        {
            foreach (var p in s.Split('|'))
                if (float.TryParse(p.Trim(), out var v)) return v;
            return 0f;
        }
    }
}
