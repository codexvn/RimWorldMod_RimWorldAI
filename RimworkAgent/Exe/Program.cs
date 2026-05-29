using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimworkAgent.Core.AgentRuntime;
using RimworkAgent.Core.CcbManager;
using RimworkAgent.Core.Mcp;

namespace RimworkAgent
{
    internal class Program
    {
        private static readonly CancellationTokenSource _cts = new();

        public static async Task Main(string[] args)
        {
            CoreLog.OnInfo = msg => Console.WriteLine($"[Core] {msg}");
            CoreLog.OnError = msg => Console.Error.WriteLine($"[Core] {msg}");

            var mcpUrl = args.Length > 0 ? args[0] : "http://localhost:9877";
            var sessionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "claude-sessions", "dev-session");
            Directory.CreateDirectory(sessionDir);
            TaskBoard.SessionDir = sessionDir;

            Console.WriteLine($"RimworkAgent 启动");
            Console.WriteLine($"  MCP: {mcpUrl}");
            Console.WriteLine($"  Session: {sessionDir}");

            // 启动 CCB 子进程（自动查找 cc-companion 目录）
            var ccbDir = FindCcbDir();
            CcbManager? ccb = null;
            if (!string.IsNullOrEmpty(ccbDir))
            {
                ccb = new CcbManager(ccbDir, sessionDir);
                if (ccb.Start()) { Console.WriteLine("  CCB: 启动中..."); await ccb.WaitReadyAsync(); Console.WriteLine("  CCB: 就绪"); }
            }
            if (ccb == null || !ccb.IsReady) Console.WriteLine("  CCB: 未启动 (Agent 将在无 CCB 模式运行)");

            var skillsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Skills");
            InternalToolRegistry.LoadSkills(skillsDir);
            InternalToolRegistry.InitializeSkillTools();

            using var agentMcp = new RimworkAgent.Core.Mcp.Server.AgentMcpServer(9878);
            _ = agentMcp.StartAsync();
            Console.WriteLine("  AgentMCP: :9878 (内部 Tool + Skills)");

            using var mcp = new McpClient(mcpUrl);
            var ctx = new ContextBuilder(mcp);
            var loopInterval = TimeSpan.FromSeconds(10);

            // SSE 事件路由
            var combatTriggered = new TaskCompletionSource<ColonyEvent>();
            mcp.OnGameEvent += evt =>
            {
                if (evt.Severity == "Critical" && evt.Category == "Combat")
                {
                    combatTriggered.TrySetResult(evt);
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Combat);
                }
                else if (evt.Severity != "Critical")
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Overseer);
            };
            mcp.StartSse();

            Console.WriteLine("Agent Main Loop 启动 (Ctrl+C 退出)");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // 1. 获取世界状态 → 更新 Scheduler
                    try
                    {
                        var summary = await mcp.CallTool("get_world_summary");
                        // 从 get_world_summary 输出中解析基本数据（简单实现，后续可改为结构化）
                        var input = ParseSummaryToInput(summary);
                        Scheduler.Tick(input);
                        AgentOrchestrator.GameDay = input.CurrentTick / 60000;
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Error($"MCP get_world_summary 失败: {ex.Message}");
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }

                    // 2. 检查每个 Agent
                    var currentTick = Environment.TickCount;
                    foreach (var config in AgentConfigs.All)
                    {
                        if (config.Name == "combat") continue;
                        if (!AgentOrchestrator.IsSleeping(config.Name)) continue;

                        bool shouldWake = config.IntervalGameHours > 0
                            && Scheduler.ShouldWake(config.Name, config.IntervalGameHours, currentTick);
                        if (config.TriggerDaily && AgentOrchestrator.IsNewDay(config.Name))
                            shouldWake = true;

                        if (shouldWake)
                        {
                            AgentOrchestrator.BeginAgent(config.Name);
                            Console.WriteLine($"=== {config.Name} 唤醒 (Load={Scheduler.LoadScore}, {Scheduler.Mode}) ===");

                            var prompt = await ctx.BuildAsync(config);
                            Console.WriteLine($"[Prompt] {prompt.Length} 字符");

                            // 通过 CCB WebSocket 发送给 Claude
                            await RunAgentSession(config, prompt, mcp);

                            AgentOrchestrator.EndAgent(config.Name);
                            Console.WriteLine($"=== {config.Name} 休眠 ===");
                        }
                    }

                    // 3. 检查 Combat Agent（SSE 事件驱动）
                    if (combatTriggered.Task.IsCompleted && AgentOrchestrator.IsSleeping("combat"))
                    {
                        var evt = await combatTriggered.Task;
                        combatTriggered = new TaskCompletionSource<ColonyEvent>();
                        AgentOrchestrator.BeginAgent("combat");
                        Console.WriteLine($"=== Combat 唤醒: {evt.Summary} ===");
                        var cc = AgentConfigs.Combat;
                        var cp = await ctx.BuildAsync(cc);
                        await RunAgentSession(cc, cp, mcp);
                        AgentOrchestrator.EndAgent("combat");
                        Console.WriteLine("=== Combat 休眠 ===");
                    }

                    await Task.Delay(loopInterval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }

            Console.WriteLine("RimworkAgent 退出");
        }

        private static SchedulerInput ParseSummaryToInput(string text)
        {
            var input = new SchedulerInput { CurrentTick = Environment.TickCount };
            if (string.IsNullOrEmpty(text)) return input;

            // 简单解析：从 Markdown 表格中提取
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

        private static async Task RunAgentSession(AgentConfig config, string prompt, McpClient mcp)
        {
            var tcs = new TaskCompletionSource<bool>();
            using var ccbWs = new CcbWebSocket();

            ccbWs.OnResult += (subtype, _) =>
            {
                Console.WriteLine($"  [{config.Name}] 回合结束: {subtype}");
                tcs.TrySetResult(true);
            };

            ccbWs.OnToolUse += async (toolId, toolName, input) =>
            {
                await ToolDispatcher.HandleAsync(ccbWs, mcp, toolId, toolName, input,
                    msg => Console.WriteLine($"  [{config.Name}] {msg}"));
            };

            ccbWs.OnAssistantText += text =>
            {
                if (!string.IsNullOrEmpty(text))
                    Console.WriteLine($"  [{config.Name}] {text.Substring(0, Math.Min(120, text.Length))}...");
            };

            if (!await ccbWs.ConnectAsync()) { Console.WriteLine($"  [{config.Name}] CCB 连接失败"); return; }

            // 获取该 Agent 可用的 Tool 列表
            try
            {
                var tools = await mcp.ListTools(config.Name);
                Console.WriteLine($"  [{config.Name}] 可用工具: {tools.Count}");
            }
            catch { /* 工具过滤失败不影响对话 */ }

            await ccbWs.SendChat(prompt);

            // 等待 Claude 完成（含 tool call 循环）
            var timeout = Task.Delay(config.Name == "combat" ? 300000 : 120000);
            await Task.WhenAny(tcs.Task, timeout);

            // 写 Memory
            try
            {
                var day = AgentOrchestrator.GameDay;
                MemoryManager.Append(config.Name, new MemoryEntry
                {
                    Day = day,
                    Insight = $"Load={Scheduler.LoadScore}({Scheduler.Mode}), Session={DateTime.Now:HH:mm}",
                    Type = "session"
                });
            }
            catch { /* Memory 写入失败不影响主流程 */ }
        }

        private static string? FindCcbDir()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                // 优先本地（Agent 自带）
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "cc-companion")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "cc-companion")),
                // 回退 RimWorldMCP
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "RimWorldMCP", "cc-companion")),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c) && File.Exists(Path.Combine(c, "package.json")))
                    return c;
            return null;
        }

        private static int ParseInt(string s) { foreach (var p in s.Split('|')) if (int.TryParse(p.Trim(), out var v)) return v; return 0; }
        private static float ParseFloat(string s) { foreach (var p in s.Split('|')) if (float.TryParse(p.Trim(), out var v)) return v; return 0f; }
    }
}
