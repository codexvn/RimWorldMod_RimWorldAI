using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent
{
    internal class Program
    {
        private static readonly CancellationTokenSource _cts = new();

        public static async Task Main(string[] args)
        {
            // 设置控制台输出编码为 UTF-8
            Console.OutputEncoding = Encoding.UTF8;
            CoreLog.OnInfo = msg => Console.WriteLine($"[Core] {msg}");
            CoreLog.OnError = msg => Console.Error.WriteLine($"[Core] {msg}");

            var mcpUrl = args.Length > 0 ? args[0] : "http://localhost:9878";
            var sessionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "claude-sessions", "dev-session");
            Directory.CreateDirectory(sessionDir);
            TaskBoard.SessionDir = sessionDir;

            Console.WriteLine($"RimWorldAgent 启动");
            Console.WriteLine($"  MCP: {mcpUrl}");
            Console.WriteLine($"  Session: {sessionDir}");

            // 启动 CCB 子进程（自动查找 cc-companion 目录）
            var ccbDir = FindCcbDir();
            CcbManager? ccb = null;
            if (!string.IsNullOrEmpty(ccbDir))
            {
                if (!CompanionInstaller.IsInstalled(ccbDir))
                {
                    Console.WriteLine("  CCB: npm install...");
                    await CompanionInstaller.InstallAsync(ccbDir);
                }
                ccb = new CcbManager(ccbDir!, sessionDir);
                if (ccb.Start()) { Console.WriteLine("  CCB: 启动中..."); await ccb.WaitReadyAsync(); Console.WriteLine("  CCB: 就绪"); }
            }
            if (ccb == null || !ccb.IsReady) Console.WriteLine("  CCB: 未启动 (Agent 将在无 CCB 模式运行)");

            var skillsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            InternalToolRegistry.Instance.LoadSkills(skillsDir);
            InternalToolRegistry.Instance.InitializeSkillTools();

            // Agent MCP Server (:9878) — 暴露内部 Tool 给 CCB
            var agentHost = new SimpleMspServer.McpServiceHost(9878, "localhost",
                    new SimpleMspServer.DelegateMspLog(Console.WriteLine));
            agentHost.RegisterProvider(InternalToolRegistry.Instance);
            agentHost.Start();
            Console.WriteLine("  AgentMCP: :9878 (内部 Tool + Skills)");

            using var mcp = new McpClient(mcpUrl);
            mcp.OnGameEvent += evt =>
            {
                if (evt.Severity == "Critical" && evt.Category == "Combat")
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Combat);
                else if (evt.Severity != "Critical")
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Overseer);
            };
            mcp.StartSse();

            var ctx = new ContextBuilder(mcp);
            var loopInterval = TimeSpan.FromSeconds(10);

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

                    // 3. Combat Agent — 有急迫事件时唤醒
                    if (AgentOrchestrator.IsSleeping("combat") && AgentOrchestrator.HasPendingEvents("combat"))
                    {
                        AgentOrchestrator.BeginAgent("combat");
                        Console.WriteLine("=== Combat 唤醒 ===");
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

            Console.WriteLine("RimWorldAgent 退出");
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

            // 工具由 CCB SDK 自动从 settings.json MCP server 发现
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
            // 优先 publish 打包版 (1.6/cc-companion), 回退源码目录
            var pub = Path.GetFullPath(Path.Combine(baseDir, "cc-companion"));
            var src = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "cc-companion"));
            if (Directory.Exists(pub) && File.Exists(Path.Combine(pub, "package.json"))) return pub;
            if (Directory.Exists(src) && File.Exists(Path.Combine(src, "package.json"))) return src;
            return null;
        }

        private static int ParseInt(string s) { foreach (var p in s.Split('|')) if (int.TryParse(p.Trim(), out var v)) return v; return 0; }
        private static float ParseFloat(string s) { foreach (var p in s.Split('|')) if (float.TryParse(p.Trim(), out var v)) return v; return 0f; }
    }
}
