using System;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;
using RimWorldAgent;

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

            // 连接后立即推送当前状态
            if (ccbWs.IsReady)
            {
                _ = ccbWs.SendEvent("agent.status", new { text = AgentOrchestrator.AgentRoleDisplay });
                PushBudgetUpdate(ccbWs);
            }
        }

        static AgentLoop()
        {
            AgentOrchestrator.OnStatusChanged += role =>
            {
                if (_statusWs?.IsReady == true)
                    _ = _statusWs.SendEvent("agent.status", new { text = role });
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

        /// <summary>MCP 游戏事件 → AgentOrchestrator 路由</summary>
        public static void WireEvents(McpClient mcp)
        {
            // tick 事件 → 更新游戏 tick
            mcp.OnGameTick += tick => AgentOrchestrator.GameTick = tick;

            // 世界状态 → 更新 Scheduler
            mcp.OnWorldState += input => Scheduler.Tick(input);

            // 游戏事件 → Agent 侧智能路由（不再依赖 MCP 侧 Route 字段）
            mcp.OnGameEvent += evt =>
            {
                CoreLog.Info($"[event] {evt.Category}/{evt.Severity}: {evt.Summary}");
                var route = AgentOrchestrator.RouteEvent(evt.Category, evt.Severity);
                AgentOrchestrator.DispatchEvent(evt, route);
            };
        }

        /// <summary>执行一次 Agent 回合：发送 prompt → Tool Loop → 写 Memory</summary>
        public static async Task RunSessionAsync(AgentConfig config, string prompt, McpClient mcp, CcbWebSocket ccbWs)
        {
            var paceController = new GamePaceController();
            AgentOrchestrator.PaceController = paceController;
            AgentOrchestrator.SessionMcp = mcp;

            var tcs = new TaskCompletionSource<bool>();

            void OnResult(string subtype, string? _)
            {
                CoreLog.Info($"[{config.Name}] 回合结束: {subtype}");
                tcs.TrySetResult(true);
            }

            async void OnToolUse(string toolId, string toolName, JsonElement? input)
            {
                try
                {
                    await ToolDispatcher.HandleAsync(ccbWs, mcp, toolId, toolName, input,
                        msg => CoreLog.Info($"[{config.Name}] {msg}"));
                }
                catch (Exception ex)
                {
                    CoreLog.Error($"[{config.Name}] Tool 执行异常: {ex.Message}");
                }
            }

            ccbWs.OnResult += OnResult;
            ccbWs.OnToolUse += OnToolUse;
            try
            {
                await ccbWs.SendChat(prompt);
                var timeoutMs = config.Name == "combat" ? 300000 : 120000;
                var timeout = Task.Delay(timeoutMs);
                await Task.WhenAny(tcs.Task, timeout);
            }
            finally
            {
                ccbWs.OnResult -= OnResult;
                ccbWs.OnToolUse -= OnToolUse;

                // 确保游戏恢复 + 清除阶段状态
                await paceController.EnsureResumed(mcp);
                AgentOrchestrator.ClearPhase();
                AgentOrchestrator.PaceController = null;
                AgentOrchestrator.SessionMcp = null;
                paceController.Dispose();
            }

            try
            {
                MemoryManager.Append(config.Name, new MemoryEntry
                {
                    Day = AgentOrchestrator.GameDay,
                    Insight = $"Load={Scheduler.LoadScore}({Scheduler.Mode})",
                    Type = "session"
                });
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[{config.Name}] 记忆写入失败: {ex.Message}");
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
